using MediaPipeline.Domain.Common;

namespace MediaPipeline.Domain.Projects;

public enum ProjectStatus
{
    Draft = 0,
    Active = 1,
    Archived = 2,
}

/// <summary>
/// A creative project — the ownership and authorisation boundary for every asset,
/// version, comment and processing job beneath it.
/// </summary>
public sealed class Project
{
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 2000;

    private readonly List<ProjectMember> _members = [];

    private Project() { }

    public Guid Id { get; private set; }
    public string PublicRef { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public ProjectStatus Status { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint RowVersion { get; private set; }

    public IReadOnlyCollection<ProjectMember> Members => _members;

    public static Project Create(string name, string? description, Guid ownerUserId, DateTimeOffset now)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length is 0 or > MaxNameLength)
        {
            throw new DomainException("INVALID_PROJECT_NAME", $"Project name must be 1–{MaxNameLength} characters.");
        }

        var trimmedDescription = (description ?? string.Empty).Trim();
        if (trimmedDescription.Length > MaxDescriptionLength)
        {
            throw new DomainException("INVALID_PROJECT_DESCRIPTION", $"Description must be at most {MaxDescriptionLength} characters.");
        }

        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            PublicRef = Common.PublicRef.New(Common.PublicRef.ProjectPrefix),
            Name = trimmedName,
            Description = trimmedDescription,
            Status = ProjectStatus.Draft,
            OwnerUserId = ownerUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Invariant: a project always has exactly one Owner membership, created
        // atomically with the project itself.
        project._members.Add(ProjectMember.Create(project.Id, ownerUserId, ProjectRole.Owner, now));
        return project;
    }

    public void Rename(string name, string? description, DateTimeOffset now)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length is 0 or > MaxNameLength)
        {
            throw new DomainException("INVALID_PROJECT_NAME", $"Project name must be 1–{MaxNameLength} characters.");
        }

        var trimmedDescription = (description ?? string.Empty).Trim();
        if (trimmedDescription.Length > MaxDescriptionLength)
        {
            throw new DomainException("INVALID_PROJECT_DESCRIPTION", $"Description must be at most {MaxDescriptionLength} characters.");
        }

        Name = trimmedName;
        Description = trimmedDescription;
        UpdatedAt = now;
    }

    public void ChangeStatus(ProjectStatus target, DateTimeOffset now)
    {
        if (target == Status)
        {
            return;
        }

        var allowed = Status switch
        {
            ProjectStatus.Draft => target is ProjectStatus.Active or ProjectStatus.Archived,
            ProjectStatus.Active => target is ProjectStatus.Archived,
            ProjectStatus.Archived => target is ProjectStatus.Active,
            _ => false,
        };

        if (!allowed)
        {
            throw new InvalidStateTransitionException(nameof(Project), Status.ToString(), target.ToString());
        }

        Status = target;
        UpdatedAt = now;
    }

    public bool AcceptsNewWork => Status is ProjectStatus.Draft or ProjectStatus.Active;

    /// <summary>
    /// Transfers ownership. The old owner is demoted to Editor rather than removed,
    /// so a project can never end up ownerless or lose its history of collaborators.
    /// </summary>
    public void TransferOwnership(Guid newOwnerUserId, DateTimeOffset now)
    {
        if (newOwnerUserId == OwnerUserId)
        {
            return;
        }

        var incoming = _members.SingleOrDefault(m => m.UserId == newOwnerUserId)
            ?? throw new DomainException("MEMBER_NOT_FOUND", "The new owner must already be a project member.");

        var current = _members.SingleOrDefault(m => m.UserId == OwnerUserId);
        current?.ChangeRole(ProjectRole.Editor, now);
        incoming.ChangeRole(ProjectRole.Owner, now);

        OwnerUserId = newOwnerUserId;
        UpdatedAt = now;
    }

    public ProjectMember AddMember(Guid userId, ProjectRole role, DateTimeOffset now)
    {
        if (role == ProjectRole.Owner)
        {
            throw new DomainException("OWNER_ROLE_NOT_ASSIGNABLE", "Use ownership transfer to change a project's owner.");
        }

        if (_members.Any(m => m.UserId == userId))
        {
            throw new DomainException("MEMBER_ALREADY_EXISTS", "That user is already a member of this project.");
        }

        var member = ProjectMember.Create(Id, userId, role, now);
        _members.Add(member);
        UpdatedAt = now;
        return member;
    }

    public void ChangeMemberRole(Guid userId, ProjectRole role, DateTimeOffset now)
    {
        if (userId == OwnerUserId)
        {
            throw new DomainException("CANNOT_DEMOTE_OWNER", "Transfer ownership before changing the owner's role.");
        }

        if (role == ProjectRole.Owner)
        {
            throw new DomainException("OWNER_ROLE_NOT_ASSIGNABLE", "Use ownership transfer to change a project's owner.");
        }

        var member = _members.SingleOrDefault(m => m.UserId == userId)
            ?? throw new DomainException("MEMBER_NOT_FOUND", "That user is not a member of this project.");

        member.ChangeRole(role, now);
        UpdatedAt = now;
    }

    public ProjectMember RemoveMember(Guid userId, DateTimeOffset now)
    {
        if (userId == OwnerUserId)
        {
            throw new DomainException("CANNOT_REMOVE_OWNER", "Transfer ownership before removing the project owner.");
        }

        var member = _members.SingleOrDefault(m => m.UserId == userId)
            ?? throw new DomainException("MEMBER_NOT_FOUND", "That user is not a member of this project.");

        _members.Remove(member);
        UpdatedAt = now;
        return member;
    }

    public ProjectRole? RoleOf(Guid userId) =>
        _members.SingleOrDefault(m => m.UserId == userId)?.Role;
}
