using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace MediaPipeline.Application.Projects;

/// <summary>
/// A resolved, authorised handle on a project. Holding one is proof that membership was
/// checked server-side for this specific user and capability.
/// </summary>
public sealed record ProjectAccess(Guid ProjectId, string ProjectRef, ProjectRole Role, ProjectStatus Status)
{
    public bool Can(ProjectCapability capability) => ProjectAuthorization.Allows(Role, capability);
}

/// <summary>
/// The chokepoint for project authorisation. Every read and write path resolves a public
/// reference through here, so an unauthorised reference is indistinguishable from a
/// non-existent one - no enumeration oracle, and no path that forgets the membership join.
/// </summary>
public sealed class ProjectAccessService(MediaPipelineDbContext db)
{
    /// <summary>
    /// Resolves a project reference for a user. Returns 404-shaped errors for projects the
    /// user cannot see, and 403 only once membership is established but the role is too low.
    /// </summary>
    public async Task<ProjectAccess> RequireAsync(
        Guid userId,
        string projectRef,
        ProjectCapability capability,
        CancellationToken cancellationToken)
    {
        if (!PublicRefGuard.IsProject(projectRef))
        {
            throw MediaPipelineException.NotFound("PROJECT_NOT_FOUND", "Project does not exist.");
        }

        var access = await db.Projects
            .AsNoTracking()
            .Where(p => p.PublicRef == projectRef)
            .Join(
                db.ProjectMembers.Where(m => m.UserId == userId),
                p => p.Id,
                m => m.ProjectId,
                (p, m) => new ProjectAccess(p.Id, p.PublicRef, m.Role, p.Status))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (access is null)
        {
            // Deliberately identical to a genuinely missing project.
            throw MediaPipelineException.NotFound("PROJECT_NOT_FOUND", "Project does not exist.");
        }

        Authorise(access, capability);
        return access;
    }

    /// <summary>Resolves an asset reference together with the caller's access to its project.</summary>
    public async Task<(ProjectAccess Access, Guid AssetId, string AssetRef, AssetStatus Status)> RequireAssetAsync(
        Guid userId,
        string assetRef,
        ProjectCapability capability,
        CancellationToken cancellationToken)
    {
        if (!PublicRefGuard.IsAsset(assetRef))
        {
            throw MediaPipelineException.NotFound("ASSET_NOT_FOUND", "Asset does not exist.");
        }

        var row = await db.Assets
            .AsNoTracking()
            .Where(a => a.PublicRef == assetRef)
            .Join(db.Projects, a => a.ProjectId, p => p.Id, (a, p) => new { Asset = a, Project = p })
            .Join(
                db.ProjectMembers.Where(m => m.UserId == userId),
                x => x.Project.Id,
                m => m.ProjectId,
                (x, m) => new
                {
                    Access = new ProjectAccess(x.Project.Id, x.Project.PublicRef, m.Role, x.Project.Status),
                    x.Asset.Id,
                    x.Asset.PublicRef,
                    x.Asset.Status,
                })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            throw MediaPipelineException.NotFound("ASSET_NOT_FOUND", "Asset does not exist.");
        }

        Authorise(row.Access, capability);
        return (row.Access, row.Id, row.PublicRef, row.Status);
    }

    /// <summary>Resolves a version reference together with the caller's access to its project.</summary>
    public async Task<(ProjectAccess Access, Guid VersionId, Guid AssetId)> RequireVersionAsync(
        Guid userId,
        string versionRef,
        ProjectCapability capability,
        CancellationToken cancellationToken)
    {
        if (!PublicRefGuard.IsVersion(versionRef))
        {
            throw MediaPipelineException.NotFound("VERSION_NOT_FOUND", "Asset version does not exist.");
        }

        var row = await db.AssetVersions
            .AsNoTracking()
            .Where(v => v.PublicRef == versionRef)
            .Join(db.Projects, v => v.ProjectId, p => p.Id, (v, p) => new { Version = v, Project = p })
            .Join(
                db.ProjectMembers.Where(m => m.UserId == userId),
                x => x.Project.Id,
                m => m.ProjectId,
                (x, m) => new
                {
                    Access = new ProjectAccess(x.Project.Id, x.Project.PublicRef, m.Role, x.Project.Status),
                    VersionId = x.Version.Id,
                    x.Version.AssetId,
                })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            throw MediaPipelineException.NotFound("VERSION_NOT_FOUND", "Asset version does not exist.");
        }

        Authorise(row.Access, capability);
        return (row.Access, row.VersionId, row.AssetId);
    }

    /// <summary>
    /// Membership check used by the SignalR hub. Returns the internal project id, which is
    /// what the group name is derived from - the client's string is never trusted.
    /// </summary>
    public async Task<ProjectAccess?> TryResolveAsync(Guid userId, string projectRef, CancellationToken cancellationToken)
    {
        if (!PublicRefGuard.IsProject(projectRef))
        {
            return null;
        }

        return await db.Projects
            .AsNoTracking()
            .Where(p => p.PublicRef == projectRef)
            .Join(
                db.ProjectMembers.Where(m => m.UserId == userId),
                p => p.Id,
                m => m.ProjectId,
                (p, m) => new ProjectAccess(p.Id, p.PublicRef, m.Role, p.Status))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Authorise(ProjectAccess access, ProjectCapability capability)
    {
        if (!access.Can(capability))
        {
            throw MediaPipelineException.Forbidden(
                "INSUFFICIENT_PROJECT_ROLE",
                $"Your role on this project does not permit {capability}.");
        }

        // Archived projects are readable but frozen: no new work may be created in them.
        var mutating = capability is ProjectCapability.UploadAsset
            or ProjectCapability.CreateVersion
            or ProjectCapability.Comment
            or ProjectCapability.ChangeReviewState
            or ProjectCapability.RetryProcessing;

        if (mutating && access.Status == ProjectStatus.Archived)
        {
            throw MediaPipelineException.Rule("PROJECT_ARCHIVED", "This project is archived and no longer accepts changes.");
        }
    }
}

/// <summary>Cheap shape checks so a malformed reference never reaches a query.</summary>
public static class PublicRefGuard
{
    public static bool IsProject(string? value) => Domain.Common.PublicRef.TryParse(value, Domain.Common.PublicRef.ProjectPrefix, out _);
    public static bool IsAsset(string? value) => Domain.Common.PublicRef.TryParse(value, Domain.Common.PublicRef.AssetPrefix, out _);
    public static bool IsVersion(string? value) => Domain.Common.PublicRef.TryParse(value, Domain.Common.PublicRef.VersionPrefix, out _);
    public static bool IsComment(string? value) => Domain.Common.PublicRef.TryParse(value, Domain.Common.PublicRef.CommentPrefix, out _);
    public static bool IsJob(string? value) => Domain.Common.PublicRef.TryParse(value, Domain.Common.PublicRef.JobPrefix, out _);
    public static bool IsUser(string? value) => Domain.Common.PublicRef.TryParse(value, Domain.Common.PublicRef.UserPrefix, out _);
}
