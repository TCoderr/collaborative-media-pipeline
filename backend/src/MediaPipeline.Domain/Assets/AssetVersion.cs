using MediaPipeline.Domain.Common;

namespace MediaPipeline.Domain.Assets;

/// <summary>
/// Availability of a version's <em>source</em> media. Deliberately separate from
/// processing and review: Ready requires successfully validated derivatives.
/// </summary>
public enum VersionLifecycleState
{
    /// <summary>Row reserved (version number allocated), bytes not yet durable.</summary>
    Reserved = 0,

    /// <summary>Source object is durable in storage and its metadata is recorded.</summary>
    Stored = 1,

    /// <summary>Source validated and all required derivatives successfully generated.</summary>
    Ready = 2,

    /// <summary>Upload failed; the row survives as a tombstone of the attempt.</summary>
    Failed = 3,

    /// <summary>Reserved but never uploaded within the grace window.</summary>
    Abandoned = 4,

    Quarantined = 5,
}

/// <summary>Derivative-generation state for a version. Owned by the worker.</summary>
public enum ProcessingState
{
    Pending = 0,
    Queued = 1,
    Processing = 2,
    Succeeded = 3,
    Failed = 4,
    DeadLettered = 5,
}

/// <summary>Human review state for a version. Owned by the API and its collaborators.</summary>
public enum ReviewState
{
    Draft = 0,
    InReview = 1,
    Approved = 2,
    ChangesRequested = 3,
}

/// <summary>
/// One revision of an asset's media. Once <see cref="VersionLifecycleState.Stored"/>,
/// the source object key, checksum and byte size never change - corrections create
/// a new version instead.
/// </summary>
public sealed class AssetVersion
{
    public const int MaxFileNameLength = 255;
    public const int MaxContentTypeLength = 128;

    private static readonly System.Buffers.SearchValues<char> PathSeparators =
        System.Buffers.SearchValues.Create("/\\");

    private readonly List<AssetDerivative> _derivatives = [];

    private AssetVersion() { }

    public Guid Id { get; private set; }
    public string PublicRef { get; private set; } = string.Empty;
    public Guid AssetId { get; private set; }
    public Guid ProjectId { get; private set; }

    /// <summary>1-based, strictly monotonic and unique within the asset.</summary>
    public int VersionNumber { get; private set; }

    public VersionLifecycleState LifecycleState { get; private set; }
    public ProcessingState ProcessingState { get; private set; }
    public ReviewState ReviewState { get; private set; }

    /// <summary>Server-generated storage key. Never derived from a client filename.</summary>
    public string SourceObjectKey { get; private set; } = string.Empty;

    /// <summary>The original filename, kept as display data only - never as a path.</summary>
    public string OriginalFileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long ByteSize { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public int? Width { get; private set; }
    public int? Height { get; private set; }

    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StoredAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Bumped on every state change. Real-time clients use it to discard events that
    /// arrive out of order, instead of trusting network ordering.
    /// </summary>
    public long StateSequence { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public Guid? DeliveredByUserId { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }

    public uint RowVersion { get; private set; }

    public IReadOnlyCollection<AssetDerivative> Derivatives => _derivatives;

    /// <summary>
    /// Reserves the next version slot. The entity mints its own id and then asks the caller
    /// to derive the storage key from it, so the key is always tied to a real row and can
    /// never be influenced by anything the client supplied.
    /// </summary>
    public static AssetVersion Reserve(
        Guid assetId,
        Guid projectId,
        int versionNumber,
        Func<Guid, string> sourceKeyFactory,
        string originalFileName,
        Guid createdByUserId,
        DateTimeOffset now)
    {
        if (versionNumber < 1)
        {
            throw new DomainException("INVALID_VERSION_NUMBER", "Version numbers start at 1.");
        }

        var id = Guid.CreateVersion7();

        return new AssetVersion
        {
            Id = id,
            PublicRef = Common.PublicRef.New(Common.PublicRef.VersionPrefix),
            AssetId = assetId,
            ProjectId = projectId,
            VersionNumber = versionNumber,
            LifecycleState = VersionLifecycleState.Reserved,
            ProcessingState = ProcessingState.Pending,
            ReviewState = ReviewState.Draft,
            SourceObjectKey = sourceKeyFactory(id),
            OriginalFileName = SanitiseFileName(originalFileName),
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            UpdatedAt = now,
            StateSequence = 1,
        };
    }

    /// <summary>Finalises the source media. Only legal from <c>Reserved</c>.</summary>
    public void MarkStored(string contentType, long byteSize, string sha256, DateTimeOffset now)
    {
        if (LifecycleState != VersionLifecycleState.Reserved)
        {
            throw new InvalidStateTransitionException(
                nameof(AssetVersion), LifecycleState.ToString(), nameof(VersionLifecycleState.Stored));
        }

        if (byteSize <= 0)
        {
            throw new DomainException("EMPTY_UPLOAD", "Uploaded media must contain at least one byte.");
        }

        ContentType = contentType.Length > MaxContentTypeLength ? contentType[..MaxContentTypeLength] : contentType;
        ByteSize = byteSize;
        Sha256 = sha256;
        LifecycleState = VersionLifecycleState.Stored;
        StoredAt = now;
        Touch(now);
    }

    public void MarkUploadFailed(DateTimeOffset now)
    {
        if (LifecycleState != VersionLifecycleState.Reserved)
        {
            throw new InvalidStateTransitionException(
                nameof(AssetVersion), LifecycleState.ToString(), nameof(VersionLifecycleState.Failed));
        }

        LifecycleState = VersionLifecycleState.Failed;
        ProcessingState = ProcessingState.Failed;
        Touch(now);
    }

    public void MarkAbandoned(DateTimeOffset now)
    {
        if (LifecycleState != VersionLifecycleState.Reserved)
        {
            throw new InvalidStateTransitionException(
                nameof(AssetVersion), LifecycleState.ToString(), nameof(VersionLifecycleState.Abandoned));
        }

        LifecycleState = VersionLifecycleState.Abandoned;
        Touch(now);
    }

    public void MarkQueued(DateTimeOffset now)
    {
        if (ProcessingState is not (ProcessingState.Pending or ProcessingState.Failed or ProcessingState.DeadLettered))
        {
            throw new InvalidStateTransitionException(
                nameof(ProcessingState), ProcessingState.ToString(), nameof(ProcessingState.Queued));
        }

        ProcessingState = ProcessingState.Queued;
        Touch(now);
    }

    public void MarkProcessing(DateTimeOffset now)
    {
        if (ProcessingState is not (ProcessingState.Queued or ProcessingState.Failed or ProcessingState.Processing))
        {
            throw new InvalidStateTransitionException(
                nameof(ProcessingState), ProcessingState.ToString(), nameof(ProcessingState.Processing));
        }

        ProcessingState = ProcessingState.Processing;
        Touch(now);
    }

    /// <summary>
    /// Records a successful processing run. Replaying the same outcome on an
    /// already-succeeded version is a no-op rather than an error: at-least-once
    /// delivery means the worker will legitimately see the same job twice.
    /// </summary>
    public void MarkProcessingSucceeded(int? width, int? height, DateTimeOffset now)
    {
        if (LifecycleState is not (VersionLifecycleState.Stored or VersionLifecycleState.Ready))
        {
            throw new InvalidStateTransitionException(
                nameof(AssetVersion), LifecycleState.ToString(), nameof(VersionLifecycleState.Ready));
        }

        if (ProcessingState == ProcessingState.Succeeded) return;
        if (ProcessingState != ProcessingState.Processing)
            throw new DomainException("JOB_NOT_PROCESSING", "Only a claimed processing job can succeed.");

        Width = width;
        Height = height;
        ProcessingState = ProcessingState.Succeeded;
        LifecycleState = VersionLifecycleState.Ready;
        Touch(now);
    }

    public void MarkProcessingFailed(bool exhausted, DateTimeOffset now)
    {
        ProcessingState = exhausted ? ProcessingState.DeadLettered : ProcessingState.Failed;

        // Failed media never becomes available for review or download.
        Touch(now);
    }

    public void ChangeReviewState(ReviewState target, DateTimeOffset now)
    {
        if (target == ReviewState.Approved)
            throw new DomainException("APPROVAL_REQUIRES_ACTOR", "Use an attributed approval to approve a version.");
        ApplyReviewState(target, now);
    }

    private void ApplyReviewState(ReviewState target, DateTimeOffset now)
    {
        if (target == ReviewState)
        {
            return;
        }

        if (LifecycleState != VersionLifecycleState.Ready || ProcessingState != ProcessingState.Succeeded || DeliveredAt is not null)
        {
            throw new DomainException(
                "VERSION_NOT_REVIEWABLE", "Only successfully processed, undelivered versions can be reviewed.");
        }

        var allowed = ReviewState switch
        {
            ReviewState.Draft => target is ReviewState.InReview,
            ReviewState.InReview => target is ReviewState.Approved or ReviewState.ChangesRequested or ReviewState.Draft,
            ReviewState.ChangesRequested => target is ReviewState.InReview or ReviewState.Draft,
            ReviewState.Approved => target is ReviewState.InReview,
            _ => false,
        };

        if (!allowed)
        {
            throw new InvalidStateTransitionException(nameof(ReviewState), ReviewState.ToString(), target.ToString());
        }

        if (target != ReviewState.Approved) { ApprovedAt = null; ApprovedByUserId = null; }
        ReviewState = target;
        Touch(now);
    }

    public void RecordReviewActivity(DateTimeOffset now) => Touch(now);

    public void Approve(Guid actor, DateTimeOffset now)
    {
        if (actor == CreatedByUserId)
            throw new DomainException("SELF_APPROVAL_FORBIDDEN", "Approval requires another project reviewer.");
        if (ReviewState == ReviewState.Approved) return;
        ApplyReviewState(ReviewState.Approved, now);
        ApprovedByUserId = actor;
        ApprovedAt = now;
    }

    public void Deliver(Guid actor, DateTimeOffset now)
    {
        if (DeliveredAt is not null) return;
        if (ReviewState != ReviewState.Approved || ApprovedByUserId is null || ProcessingState != ProcessingState.Succeeded)
            throw new DomainException("VERSION_NOT_APPROVED", "Delivery requires a successfully processed, approved version.");
        DeliveredByUserId = actor;
        DeliveredAt = now;
        Touch(now);
    }

    public void Quarantine(DateTimeOffset now)
    {
        LifecycleState = VersionLifecycleState.Quarantined;
        ProcessingState = ProcessingState.DeadLettered;
        Touch(now);
    }

    public AssetDerivative UpsertDerivative(
        DerivativeKind kind,
        string objectKey,
        string contentType,
        int width,
        int height,
        long byteSize,
        string sha256,
        DateTimeOffset now)
    {
        var existing = _derivatives.SingleOrDefault(d => d.Kind == kind);
        if (existing is not null)
        {
            existing.Update(objectKey, contentType, width, height, byteSize, sha256, now);
            return existing;
        }

        var derivative = AssetDerivative.Create(Id, kind, objectKey, contentType, width, height, byteSize, sha256, now);
        _derivatives.Add(derivative);
        return derivative;
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        StateSequence++;
    }

    /// <summary>
    /// Keeps only the leaf name and neutralises anything that could be read as a path
    /// or a control sequence. The result is display data; it never builds a storage key.
    /// </summary>
    public static string SanitiseFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "untitled";
        }

        var name = fileName.Trim();
        var lastSeparator = name.AsSpan().LastIndexOfAny(PathSeparators);
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        var length = Math.Min(name.Length, MaxFileNameLength);
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            var c = name[i];
            var unsafeChar = char.IsControl(c)
                || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|';
            buffer[i] = unsafeChar ? '_' : c;
        }

        var cleaned = new string(buffer).Trim(' ', '.');
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }
}
