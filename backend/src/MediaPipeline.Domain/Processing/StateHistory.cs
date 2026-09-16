namespace MediaPipeline.Domain.Processing;

/// <summary>Append-only state and attempt history, committed with the corresponding entity.</summary>
public sealed class StateHistory
{
    private StateHistory() { }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string SubjectRef { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public long Sequence { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public static StateHistory Create(Guid id, Guid projectId, string subjectRef, string eventType,
        long sequence, Guid? actor, string payload, DateTimeOffset now) => new()
    {
        Id = id, ProjectId = projectId, SubjectRef = subjectRef, EventType = eventType,
        Sequence = sequence, ActorUserId = actor, Payload = payload, OccurredAt = now,
    };
}
