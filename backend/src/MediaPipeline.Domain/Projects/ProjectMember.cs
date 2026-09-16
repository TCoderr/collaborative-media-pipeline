namespace MediaPipeline.Domain.Projects;

/// <summary>A user's membership of a single project, with its role.</summary>
public sealed class ProjectMember
{
    private ProjectMember() { }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid UserId { get; private set; }
    public ProjectRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint RowVersion { get; private set; }

    internal static ProjectMember Create(Guid projectId, Guid userId, ProjectRole role, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ProjectId = projectId,
        UserId = userId,
        Role = role,
        CreatedAt = now,
        UpdatedAt = now,
    };

    internal void ChangeRole(ProjectRole role, DateTimeOffset now)
    {
        Role = role;
        UpdatedAt = now;
    }
}
