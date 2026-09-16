namespace MediaPipeline.Domain.Processing;

public enum OutboxStatus
{
    Pending = 0,
    Published = 1,

    /// <summary>Publishing gave up (unroutable, or the attempt budget is exhausted).</summary>
    Abandoned = 2,
}

/// <summary>
/// Transactional outbox row. Written in the <em>same</em> database transaction as the
/// state change it announces, so a crash between "state committed" and "message on the
/// broker" cannot lose work - the publisher will find the row and retry.
/// <para>
/// This buys durable hand-off, not exactly-once delivery: a broker acknowledgement that
/// times out after the broker accepted the message will be republished. Consumers are
/// therefore required to be idempotent.
/// </para>
/// </summary>
public sealed class OutboxMessage
{
    public const int MaxErrorLength = 500;

    private OutboxMessage() { }

    /// <summary>Also the broker message id - the consumer's idempotency key.</summary>
    public Guid Id { get; private set; }

    public string MessageType { get; private set; } = string.Empty;
    public int ContractVersion { get; private set; }
    public string Payload { get; private set; } = string.Empty;

    /// <summary>W3C traceparent captured at enqueue time so the trace survives the queue.</summary>
    public string? TraceParent { get; private set; }

    public Guid? ProjectId { get; private set; }

    /// <summary>
    /// Public reference of the entity this message is about (an asset version, say).
    /// Indexed so "which message is stuck for this version?" is one query during an
    /// incident, rather than a scan through jsonb payloads.
    /// </summary>
    public string? SubjectRef { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public OutboxStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Cooperative lease so several publisher instances can run without publishing the
    /// same row twice in the common case. Correctness still rests on consumer idempotency.
    /// </summary>
    public string? LockedBy { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    public static OutboxMessage Create(
        Guid messageId,
        string messageType,
        int contractVersion,
        string payload,
        Guid? projectId,
        string? subjectRef,
        string? traceParent,
        DateTimeOffset now) => new()
        {
            Id = messageId,
            MessageType = messageType,
            ContractVersion = contractVersion,
            Payload = payload,
            ProjectId = projectId,
            SubjectRef = subjectRef,
            TraceParent = traceParent,
            OccurredAt = now,
            Status = OutboxStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
        };

    public void MarkPublished(DateTimeOffset now)
    {
        Status = OutboxStatus.Published;
        PublishedAt = now;
        LastError = null;
        LockedBy = null;
        LockedUntil = null;
    }

    public void MarkAttemptFailed(string error, DateTimeOffset now, int maxAttempts, TimeSpan baseDelay)
    {
        AttemptCount++;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        LockedBy = null;
        LockedUntil = null;

        if (AttemptCount >= maxAttempts)
        {
            Status = OutboxStatus.Abandoned;
            return;
        }

        // Exponential backoff, capped, so a broker outage does not become a hot loop.
        var seconds = Math.Min(baseDelay.TotalSeconds * Math.Pow(2, AttemptCount - 1), 300);
        NextAttemptAt = now.AddSeconds(seconds);
    }

    public void Lease(string owner, TimeSpan duration, DateTimeOffset now)
    {
        LockedBy = owner;
        LockedUntil = now.Add(duration);
    }
}
