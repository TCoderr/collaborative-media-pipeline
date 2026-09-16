namespace MediaPipeline.Domain.Projects;

/// <summary>
/// Project-scoped roles. Ordered least → most privileged so capability checks can
/// compare ranks instead of enumerating role names at every call site.
/// </summary>
public enum ProjectRole
{
    Viewer = 0,
    Reviewer = 1,
    Editor = 2,
    Owner = 3,
}

/// <summary>Discrete things a member may attempt inside a project.</summary>
public enum ProjectCapability
{
    ViewProject,
    UploadAsset,
    CreateVersion,
    Comment,
    ResolveComment,
    ChangeReviewState,
    ManageProject,
    ManageMembers,
    RetryProcessing,
}

/// <summary>
/// The single place project authorisation is decided. Role comparisons live here
/// and nowhere else, so a new capability cannot quietly bypass a role.
/// </summary>
public static class ProjectAuthorization
{
    public static bool Allows(ProjectRole role, ProjectCapability capability) => capability switch
    {
        ProjectCapability.ViewProject => true,
        ProjectCapability.Comment => role >= ProjectRole.Reviewer,
        ProjectCapability.ChangeReviewState => role >= ProjectRole.Reviewer,
        ProjectCapability.ResolveComment => role >= ProjectRole.Reviewer,
        ProjectCapability.UploadAsset => role >= ProjectRole.Editor,
        ProjectCapability.CreateVersion => role >= ProjectRole.Editor,
        ProjectCapability.RetryProcessing => role >= ProjectRole.Editor,
        ProjectCapability.ManageProject => role == ProjectRole.Owner,
        ProjectCapability.ManageMembers => role == ProjectRole.Owner,
        _ => false,
    };
}
