using MediaPipeline.Domain.Common;

namespace MediaPipeline.Domain.Assets;

public enum AssetStatus
{
    Active = 0,
    Archived = 1,
    Deleted = 2,
}

/// <summary>
/// The logical creative item. An asset is stable across its whole life; the media
/// it points at lives in <see cref="AssetVersion"/>s, which are never overwritten.
/// </summary>
public sealed class Asset
{
    public const int MaxTitleLength = 200;

    private Asset() { }

    public Guid Id { get; private set; }
    public string PublicRef { get; private set; } = string.Empty;
    public Guid ProjectId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public AssetStatus Status { get; private set; }

    /// <summary>Points at the highest finalised version; null until V1 is stored.</summary>
    public Guid? CurrentVersionId { get; private set; }

    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint RowVersion { get; private set; }

    public static Asset Create(Guid projectId, string title, Guid createdByUserId, DateTimeOffset now)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxTitleLength)
        {
            throw new DomainException("INVALID_ASSET_TITLE", $"Asset title must be 1-{MaxTitleLength} characters.");
        }

        return new Asset
        {
            Id = Guid.CreateVersion7(),
            PublicRef = Common.PublicRef.New(Common.PublicRef.AssetPrefix),
            ProjectId = projectId,
            Title = trimmed,
            Status = AssetStatus.Active,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Rename(string title, DateTimeOffset now)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxTitleLength)
        {
            throw new DomainException("INVALID_ASSET_TITLE", $"Asset title must be 1-{MaxTitleLength} characters.");
        }

        Title = trimmed;
        UpdatedAt = now;
    }

    public void ChangeStatus(AssetStatus target, DateTimeOffset now)
    {
        if (target == Status)
        {
            return;
        }

        var allowed = Status switch
        {
            AssetStatus.Active => target is AssetStatus.Archived or AssetStatus.Deleted,
            AssetStatus.Archived => target is AssetStatus.Active or AssetStatus.Deleted,
            AssetStatus.Deleted => false,
            _ => false,
        };

        if (!allowed)
        {
            throw new InvalidStateTransitionException(nameof(Asset), Status.ToString(), target.ToString());
        }

        Status = target;
        UpdatedAt = now;
    }

    public bool AcceptsNewVersions => Status == AssetStatus.Active;
}
