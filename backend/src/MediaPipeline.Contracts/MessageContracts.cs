using System.Text.Json.Serialization;

namespace MediaPipeline.Contracts;

/// <summary>
/// Names and versions of every message that crosses the broker. Producers and consumers
/// agree here and nowhere else; an unknown pair is rejected rather than guessed at.
/// </summary>
public static class MessageTypes
{
    public const string MediaProcessingRequested = "media.processing.requested";
    public const string RealtimeNotification = "realtime.notification";

    /// <summary>Header names carried on every published message.</summary>
    public static class Headers
    {
        public const string MessageType = "x-message-type";
        public const string ContractVersion = "x-contract-version";
        public const string TraceParent = "traceparent";
        public const string AttemptCount = "x-attempt-count";
        public const string FailureReason = "x-failure-reason";
        public const string FailureKind = "x-failure-kind";
    }
}

/// <summary>
/// Request to generate derivatives for one asset version.
/// <para>
/// Carries references, never bytes: the broker is a work queue, not a data bus, so the
/// payload stays small and the source of truth stays in PostgreSQL and object storage.
/// </para>
/// </summary>
public sealed record MediaProcessingRequestedV1
{
    public const int ContractVersion = 1;

    [JsonPropertyName("messageId")]
    public required Guid MessageId { get; init; }

    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("projectRef")]
    public required string ProjectRef { get; init; }

    [JsonPropertyName("assetRef")]
    public required string AssetRef { get; init; }

    [JsonPropertyName("assetVersionRef")]
    public required string AssetVersionRef { get; init; }

    [JsonPropertyName("jobRef")]
    public required string JobRef { get; init; }

    [JsonPropertyName("sourceObjectKey")]
    public required string SourceObjectKey { get; init; }

    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    [JsonPropertyName("byteSize")]
    public required long ByteSize { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("operations")]
    public required IReadOnlyList<string> Operations { get; init; }
}

/// <summary>
/// A durable state change that already happened, broadcast so every API replica can push
/// it to its own SignalR clients. Best-effort by design: losing one costs a live update,
/// never data - the client can always re-read authoritative state from the API.
/// </summary>
public sealed record RealtimeNotificationV1
{
    public const int ContractVersion = 1;

    [JsonPropertyName("messageId")]
    public required Guid MessageId { get; init; }

    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("projectRef")]
    public required string ProjectRef { get; init; }

    [JsonPropertyName("eventType")]
    public required string EventType { get; init; }

    /// <summary>
    /// Monotonic per-entity sequence. Clients drop an event whose sequence is not newer
    /// than what they already hold, so out-of-order arrival cannot corrupt the view.
    /// </summary>
    [JsonPropertyName("stateSequence")]
    public required long StateSequence { get; init; }

    [JsonPropertyName("payload")]
    public required System.Text.Json.JsonElement Payload { get; init; }
}

/// <summary>Real-time event names. Shared with the browser client verbatim.</summary>
public static class RealtimeEvents
{
    public const string AssetVersionCreated = "asset.version.created";
    public const string ProcessingStateChanged = "asset.processing.state-changed";
    public const string ReviewStateChanged = "review.state.changed";
    public const string CommentCreated = "comment.created";
    public const string CommentResolved = "comment.resolved";
    public const string ProjectMemberChanged = "project.member.changed";
}
