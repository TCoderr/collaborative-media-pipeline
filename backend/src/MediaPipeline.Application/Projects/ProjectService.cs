using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Contracts;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaPipeline.Application.Projects;

/// <summary>Project lifecycle and membership. The write side of the project aggregate.</summary>
public sealed class ProjectService(
    MediaPipelineDbContext db,
    ProjectAccessService access,
    IClock clock,
    ILogger<ProjectService> logger)
{
    public async Task<ProjectDetail> CreateAsync(Guid userId, string name, string? description, CancellationToken cancellationToken)
    {
        var project = Project.Create(name, description, userId, clock.UtcNow);
        db.Projects.Add(project);
        await SaveAsActorAsync(userId, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("event=project_created project_ref={ProjectRef}", project.PublicRef);
        return await GetDetailAsync(userId, project.PublicRef, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists only projects the caller is a member of. The membership join is part of the
    /// query, not a filter applied afterwards, so there is no shape of this call that
    /// returns someone else's project.
    /// </summary>
    public async Task<Page<ProjectSummary>> ListAsync(Guid userId, PageRequest request, CancellationToken cancellationToken)
    {
        var page = request.Normalised();
        var cursor = Cursor.Decode(page.Cursor);

        var query = from p in db.Projects.AsNoTracking()
                    join m in db.ProjectMembers.AsNoTracking() on p.Id equals m.ProjectId
                    where m.UserId == userId
                    select new { Project = p, m.Role };

        if (cursor is not null)
        {
            query = query.Where(x => x.Project.CreatedAt < cursor.Value.CreatedAt
                || (x.Project.CreatedAt == cursor.Value.CreatedAt && x.Project.Id.CompareTo(cursor.Value.Id) < 0));
        }

        // Fetch one extra row to decide whether a next page exists, without a count query.
        var rows = await query
            .OrderByDescending(x => x.Project.CreatedAt)
            .ThenByDescending(x => x.Project.Id)
            .Take(page.Limit + 1)
            .Select(x => new
            {
                x.Project.Id,
                x.Project.PublicRef,
                x.Project.Name,
                x.Project.Description,
                x.Project.Status,
                x.Role,
                x.Project.CreatedAt,
                x.Project.UpdatedAt,
                AssetCount = db.Assets.Count(a => a.ProjectId == x.Project.Id && a.Status == AssetStatus.Active),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit)
            .Select(r => new ProjectSummary(
                r.PublicRef, r.Name, r.Description, r.Status.ToString(), r.Role.ToString(),
                r.AssetCount, r.CreatedAt, r.UpdatedAt))
            .ToList();

        var next = hasMore && rows.Count > page.Limit
            ? Cursor.Encode(rows[page.Limit - 1].CreatedAt, rows[page.Limit - 1].Id)
            : null;

        return new Page<ProjectSummary>(items, next, page.Limit);
    }

    public async Task<ProjectDetail> GetDetailAsync(Guid userId, string projectRef, CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.ViewProject, cancellationToken)
            .ConfigureAwait(false);

        var project = await db.Projects.AsNoTracking()
            .Where(p => p.Id == resolved.ProjectId)
            .Select(p => new { p.PublicRef, p.Name, p.Description, p.Status, p.OwnerUserId, p.CreatedAt, p.UpdatedAt })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var members = await (from m in db.ProjectMembers.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             where m.ProjectId == resolved.ProjectId
                             orderby m.Role descending, u.DisplayName
                             select new ProjectMemberView(u.PublicRef, u.DisplayName, u.Email, m.Role.ToString(), m.CreatedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var ownerRef = await db.Users.AsNoTracking()
            .Where(u => u.Id == project.OwnerUserId)
            .Select(u => u.PublicRef)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        return new ProjectDetail(
            project.PublicRef, project.Name, project.Description, project.Status.ToString(),
            resolved.Role.ToString(), ownerRef, members, project.CreatedAt, project.UpdatedAt);
    }

    /// <summary>
    /// Updates project metadata under optimistic concurrency. A stale <paramref name="expectedVersion"/>
    /// is rejected with a conflict rather than silently overwriting a collaborator's edit.
    /// </summary>
    public async Task<ProjectDetail> UpdateAsync(
        Guid userId,
        string projectRef,
        string name,
        string? description,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.ManageProject, cancellationToken)
            .ConfigureAwait(false);

        var project = await db.Projects.SingleAsync(p => p.Id == resolved.ProjectId, cancellationToken).ConfigureAwait(false);
        ConcurrencyToken.Enforce(expectedVersion, project.RowVersion);

        project.Rename(name, description, clock.UtcNow);
        await SaveWithConcurrencyMappingAsync(userId, cancellationToken).ConfigureAwait(false);

        return await GetDetailAsync(userId, projectRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectDetail> ChangeStatusAsync(
        Guid userId,
        string projectRef,
        ProjectStatus status,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.ManageProject, cancellationToken)
            .ConfigureAwait(false);

        var project = await db.Projects.SingleAsync(p => p.Id == resolved.ProjectId, cancellationToken).ConfigureAwait(false);
        ConcurrencyToken.Enforce(expectedVersion, project.RowVersion);

        project.ChangeStatus(status, clock.UtcNow);
        await SaveWithConcurrencyMappingAsync(userId, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("event=project_status_changed project_ref={ProjectRef} status={ProjectStatus}", projectRef, status);
        return await GetDetailAsync(userId, projectRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectDetail> AddMemberAsync(
        Guid userId,
        string projectRef,
        string email,
        ProjectRole role,
        CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.ManageMembers, cancellationToken)
            .ConfigureAwait(false);

        var project = await db.Projects
            .Include(p => p.Members)
            .SingleAsync(p => p.Id == resolved.ProjectId, cancellationToken).ConfigureAwait(false);

        var normalised = Domain.Identity.User.NormaliseEmail(email ?? string.Empty);
        var targetUserId = await db.Users.AsNoTracking()
            .Where(u => u.Email == normalised)
            .Select(u => (Guid?)u.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw MediaPipelineException.NotFound("USER_NOT_FOUND", "No account exists for that email address.");

        project.AddMember(targetUserId, role, clock.UtcNow);

        try
        {
            await SaveAsActorAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // Two owners adding the same collaborator at once: the index decides.
            db.ChangeTracker.Clear();
            throw MediaPipelineException.Conflict("MEMBER_ALREADY_EXISTS", "That user is already a member of this project.");
        }

        logger.LogInformation("event=project_member_added project_ref={ProjectRef} role={ProjectRole}", projectRef, role);
        return await GetDetailAsync(userId, projectRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectDetail> RemoveMemberAsync(Guid userId, string projectRef, string userRef, CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.ManageMembers, cancellationToken)
            .ConfigureAwait(false);

        var project = await db.Projects
            .Include(p => p.Members)
            .SingleAsync(p => p.Id == resolved.ProjectId, cancellationToken).ConfigureAwait(false);

        var targetUserId = await ResolveUserRefAsync(userRef, cancellationToken).ConfigureAwait(false);
        var removed = project.RemoveMember(targetUserId, clock.UtcNow);
        db.ProjectMembers.Remove(removed);

        await SaveAsActorAsync(userId, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("event=project_member_removed project_ref={ProjectRef}", projectRef);
        return await GetDetailAsync(userId, projectRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectDetail> ChangeMemberRoleAsync(
        Guid userId,
        string projectRef,
        string userRef,
        ProjectRole role,
        CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.ManageMembers, cancellationToken)
            .ConfigureAwait(false);

        var project = await db.Projects
            .Include(p => p.Members)
            .SingleAsync(p => p.Id == resolved.ProjectId, cancellationToken).ConfigureAwait(false);

        var targetUserId = await ResolveUserRefAsync(userRef, cancellationToken).ConfigureAwait(false);
        project.ChangeMemberRole(targetUserId, role, clock.UtcNow);
        await SaveWithConcurrencyMappingAsync(userId, cancellationToken).ConfigureAwait(false);

        return await GetDetailAsync(userId, projectRef, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the Owner role to another member. Modelled as a single transactional operation
    /// so the project cannot pass through a state with no owner or with two.
    /// </summary>
    public async Task<ProjectDetail> TransferOwnershipAsync(
        Guid userId,
        string projectRef,
        string newOwnerRef,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.ManageMembers, cancellationToken)
            .ConfigureAwait(false);

        var project = await db.Projects
            .Include(p => p.Members)
            .SingleAsync(p => p.Id == resolved.ProjectId, cancellationToken).ConfigureAwait(false);

        ConcurrencyToken.Enforce(expectedVersion, project.RowVersion);

        var newOwnerId = await ResolveUserRefAsync(newOwnerRef, cancellationToken).ConfigureAwait(false);
        project.TransferOwnership(newOwnerId, clock.UtcNow);
        await SaveWithConcurrencyMappingAsync(userId, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("event=project_ownership_transferred project_ref={ProjectRef}", projectRef);
        return await GetDetailAsync(userId, projectRef, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> ResolveUserRefAsync(string userRef, CancellationToken cancellationToken)
    {
        if (!PublicRefGuard.IsUser(userRef))
        {
            throw MediaPipelineException.NotFound("USER_NOT_FOUND", "No such user.");
        }

        return await db.Users.AsNoTracking()
            .Where(u => u.PublicRef == userRef)
            .Select(u => (Guid?)u.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw MediaPipelineException.NotFound("USER_NOT_FOUND", "No such user.");
    }

    private async Task SaveWithConcurrencyMappingAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await SaveAsActorAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw MediaPipelineException.Conflict(
                "CONCURRENT_MODIFICATION", "Someone else changed this while you were editing. Reload and try again.");
        }
    }
    private Task<int> SaveAsActorAsync(Guid userId, CancellationToken cancellationToken)
    {
        db.ActorUserId = userId;
        return db.SaveChangesAsync(cancellationToken);
    }

}
