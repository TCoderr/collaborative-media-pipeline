namespace MediaPipeline.Application.Contracts;

/// <summary>
/// Read models returned by the API. Deliberately hand-written rather than projected from
/// entities, so an internal id or a storage key cannot leak by accident when a domain
/// property is added later.
/// </summary>
public sealed record ProjectSummary(
    string Ref,
    string Name,
    string Description,
    string Status,
    string Role,
    int AssetCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectMemberView(string UserRef, string DisplayName, string Email, string Role, DateTimeOffset JoinedAt);

public sealed record ProjectDetail(
    string Ref,
    string Name,
    string Description,
    string Status,
    string Role,
    string OwnerRef,
    IReadOnlyList<ProjectMemberView> Members,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DerivativeView(string Kind, int Width, int Height, long ByteSize, string Url);

public sealed record AssetVersionSummary(
    string Ref,
    int VersionNumber,
    string LifecycleState,
    string ProcessingState,
    string ReviewState,
    string OriginalFileName,
    string ContentType,
    long ByteSize,
    string Sha256,
    int? Width,
    int? Height,
    long StateSequence,
    string CreatedByRef,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DerivativeView> Derivatives,
    ProcessingJobView? Job,
    string ETag = "");

public sealed record ProcessingJobView(
    string Ref,
    string State,
    int AttemptCount,
    int MaxAttempts,
    string FailureKind,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record AssetSummary(
    string Ref,
    string Title,
    string Status,
    int VersionCount,
    AssetVersionSummary? CurrentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AssetDetail(
    string Ref,
    string Title,
    string Status,
    string ProjectRef,
    IReadOnlyList<AssetVersionSummary> Versions,
    string? CurrentVersionRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CommentView(
    string Ref,
    string Body,
    string AuthorRef,
    string AuthorName,
    bool Resolved,
    string? ResolvedByRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    long StateSequence,
    string ETag = "",
    string? ParentRef = null);

/// <summary>A page of results plus the cursor needed to fetch the next one.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, int Limit);

/// <summary>Standard pagination input. Bounded so a client cannot ask for an unbounded list.</summary>
public sealed record PageRequest
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public int Limit { get; init; } = DefaultLimit;
    public string? Cursor { get; init; }

    public PageRequest Normalised() => this with { Limit = Math.Clamp(Limit <= 0 ? DefaultLimit : Limit, 1, MaxLimit) };
}
