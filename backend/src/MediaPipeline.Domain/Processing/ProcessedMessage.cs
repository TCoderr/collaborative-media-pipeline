namespace MediaPipeline.Domain.Processing;

/// <summary>
/// The consumer-side idempotency ledger (an "inbox"). A row is inserted in the same
/// transaction as the side effects of handling a message, so a redelivery of that
/// message can be recognised and acknowledged without repeating the work.
/// </summary>
public sealed class ProcessedMessage
{
    private ProcessedMessage() { }

    public Guid MessageId { get; private set; }

    /// <summary>Which consumer processed it - two consumers may legitimately both handle a message.</summary>
    public string Consumer { get; private set; } = string.Empty;

    public string MessageType { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }

    /// <summary>Short summary of the outcome, for operator diagnostics on replay.</summary>
    public string? Outcome { get; private set; }

    public static ProcessedMessage Create(Guid messageId, string consumer, string messageType, string? outcome, DateTimeOffset now) => new()
    {
        MessageId = messageId,
        Consumer = consumer,
        MessageType = messageType,
        Outcome = outcome,
        ProcessedAt = now,
    };
}

/// <summary>
/// API-level idempotency for unsafe requests. A client that retries a create with the
/// same <c>Idempotency-Key</c> gets the original resource back instead of a duplicate.
/// </summary>
public sealed class IdempotencyRecord
{
    public const int MaxKeyLength = 128;

    private IdempotencyRecord() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;

    /// <summary>Hash of the request body, so the same key with a different payload is a conflict.</summary>
    public string RequestHash { get; private set; } = string.Empty;

    public string ResponseRef { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public void SetResponse(string responseRef)
    {
        if (ResponseRef.Length != 0 && ResponseRef != responseRef) throw new InvalidOperationException("An idempotent request already has a resource.");
        ResponseRef = responseRef;
    }

    public static IdempotencyRecord Create(
        Guid userId,
        string endpoint,
        string key,
        string requestHash,
        string responseRef,
        DateTimeOffset now,
        TimeSpan retention) => new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Endpoint = endpoint,
            Key = key,
            RequestHash = requestHash,
            ResponseRef = responseRef,
            CreatedAt = now,
            ExpiresAt = now.Add(retention),
        };
}
