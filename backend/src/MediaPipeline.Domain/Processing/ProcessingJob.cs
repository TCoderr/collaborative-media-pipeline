using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Common;

namespace MediaPipeline.Domain.Processing;

/// <summary>
/// Why a processing attempt failed. The distinction drives retry policy: transient
/// failures go back on the queue, permanent ones must never be retried.
/// </summary>
public enum ProcessingFailureKind
{
    None = 0,

    /// <summary>Storage, database or network hiccup - worth retrying.</summary>
    Transient = 1,

    /// <summary>Corrupt, unsupported or oversized media - retrying cannot help.</summary>
    Permanent = 2,
}

/// <summary>
/// The durable record of derivative generation for one asset version. It is the
/// database-side half of the RabbitMQ work queue: the broker carries the request,
/// this row carries the truth about attempts, claims and outcomes.
/// </summary>
public sealed class ProcessingJob
{
    public const int MaxErrorMessageLength = 500;
    public const int DefaultMaxAttempts = 4;

    private ProcessingJob() { }

    public Guid Id { get; private set; }
    public string PublicRef { get; private set; } = string.Empty;
    public Guid AssetVersionId { get; private set; }
    public Guid ProjectId { get; private set; }
    public ProcessingState State { get; private set; }
    public int AttemptCount { get; private set; }
    public int MaxAttempts { get; private set; }

    public ProcessingFailureKind FailureKind { get; private set; }
    public string? FailureCode { get; private set; }

    /// <summary>Sanitised, length-bounded. Never a raw stack trace or a credential.</summary>
    public string? FailureMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }

    /// <summary>Worker instance currently holding the claim, for operator diagnostics.</summary>
    public string? ClaimedBy { get; private set; }
    public DateTimeOffset? ClaimExpiresAt { get; private set; }

    public uint RowVersion { get; private set; }

    public static ProcessingJob Create(Guid assetVersionId, Guid projectId, DateTimeOffset now, int maxAttempts = DefaultMaxAttempts) => new()
    {
        Id = Guid.CreateVersion7(),
        PublicRef = Common.PublicRef.New(Common.PublicRef.JobPrefix),
        AssetVersionId = assetVersionId,
        ProjectId = projectId,
        State = ProcessingState.Queued,
        AttemptCount = 0,
        MaxAttempts = maxAttempts,
        FailureKind = ProcessingFailureKind.None,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Marks the job as being worked on. Returns false when the job is already in a
    /// terminal success state, which is the normal outcome of a duplicate delivery.
    /// </summary>
    public bool TryClaim(string workerId, TimeSpan claimTtl, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return false;
        }

        var claimHeldByAnother = State == ProcessingState.Processing
            && ClaimExpiresAt is { } expiry
            && expiry > now
            ;

        if (claimHeldByAnother)
        {
            throw new DomainException("JOB_ALREADY_CLAIMED", "Another worker is already processing this job.");
        }

        if (AttemptCount >= MaxAttempts)
            throw new DomainException("RETRY_BUDGET_EXHAUSTED", "The processing attempt budget is exhausted.");
        AttemptCount++;
        State = ProcessingState.Processing;
        StartedAt ??= now;
        ClaimedBy = workerId;
        ClaimExpiresAt = now.Add(claimTtl);
        UpdatedAt = now;
        return true;
    }

    public void MarkSucceeded(DateTimeOffset now)
    {
        State = ProcessingState.Succeeded;
        FailureKind = ProcessingFailureKind.None;
        FailureCode = null;
        FailureMessage = null;
        CompletedAt = now;
        ClaimedBy = null;
        ClaimExpiresAt = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Records a failed attempt and decides whether it may be retried. Permanent
    /// failures are dead-lettered immediately regardless of remaining attempts.
    /// </summary>
    public bool MarkFailed(ProcessingFailureKind kind, string code, string? message, DateTimeOffset now)
    {
        FailureKind = kind == ProcessingFailureKind.None ? ProcessingFailureKind.Transient : kind;
        FailureCode = Truncate(code, 100);
        FailureMessage = Truncate(message, MaxErrorMessageLength);
        LastFailureAt = now;
        ClaimedBy = null;
        ClaimExpiresAt = null;
        UpdatedAt = now;

        var retryable = FailureKind == ProcessingFailureKind.Transient && AttemptCount < MaxAttempts;
        State = retryable ? ProcessingState.Failed : ProcessingState.DeadLettered;
        if (!retryable)
        {
            CompletedAt = now;
        }

        return retryable;
    }

    /// <summary>
    /// Operator-triggered replay. Resets the attempt budget but keeps the previous
    /// failure visible until the retry produces its own outcome.
    /// </summary>
    public void PrepareRetry(DateTimeOffset now)
    {
        if (State is ProcessingState.Processing)
        {
            throw new DomainException("JOB_IN_PROGRESS", "This job is currently being processed.");
        }

        if (State is ProcessingState.Succeeded)
        {
            throw new DomainException("JOB_ALREADY_SUCCEEDED", "This job has already completed successfully.");
        }

        if (State is not (ProcessingState.Failed or ProcessingState.DeadLettered) || FailureKind == ProcessingFailureKind.Permanent)
            throw new DomainException("JOB_NOT_RETRYABLE", "Only transiently failed jobs can be retried.");
        MaxAttempts = AttemptCount + DefaultMaxAttempts;
        State = ProcessingState.Queued;
        CompletedAt = null;
        ClaimedBy = null;
        ClaimExpiresAt = null;
        UpdatedAt = now;
    }

    public bool IsTerminal => State is ProcessingState.Succeeded or ProcessingState.DeadLettered;

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
