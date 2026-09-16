using System.Diagnostics;
using System.Text.Json;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Common;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Domain.Reviews;
using MediaPipeline.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace MediaPipeline.Application.Persistence;

public sealed partial class MediaPipelineDbContext
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] ImmutableSourceProperties =
    ["AssetId", "ProjectId", "VersionNumber", "PublicRef", "SourceObjectKey", "OriginalFileName", "ContentType", "ByteSize", "Sha256", "CreatedByUserId", "CreatedAt", "StoredAt"];

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        var entries = ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var pending = new List<StateHistory>();
        foreach (var entry in entries)
        {
            if (entry.Entity is StateHistory && entry.State != EntityState.Added)
                throw new DomainException("IMMUTABLE_HISTORY", "History cannot be changed or deleted.");
            if (entry.Entity is AssetVersion immutable && entry.State == EntityState.Modified &&
                entry.OriginalValues.GetValue<DateTimeOffset?>("StoredAt") is not null &&
                ImmutableSourceProperties.Any(name => entry.Property(name).IsModified))
                throw new DomainException("IMMUTABLE_VERSION", "Stored source metadata cannot be changed.");
            if (entry.State == EntityState.Deleted && entry.Entity is not ProjectMember) continue;
            Guid projectId; string subject; string eventType; long sequence; object payload;
            switch (entry.Entity)
            {
                case AssetVersion v:
                    projectId = v.ProjectId; subject = v.PublicRef; sequence = v.StateSequence;
                    eventType = entry.State == EntityState.Added ? RealtimeEvents.AssetVersionCreated :
                        entry.Property(nameof(v.DeliveredAt)).IsModified ? "delivery.completed" :
                        entry.Property(nameof(v.ReviewState)).IsModified ? (v.ReviewState == ReviewState.Approved ? "approval.granted" : RealtimeEvents.ReviewStateChanged) : RealtimeEvents.ProcessingStateChanged;
                    payload = new { versionRef = v.PublicRef, lifecycleState = v.LifecycleState.ToString(), processingState = v.ProcessingState.ToString(),
                        reviewState = v.ReviewState.ToString(), v.ApprovedAt, v.DeliveredAt };
                    break;
                case ProcessingJob j:
                    projectId = j.ProjectId; subject = j.PublicRef; sequence = j.AttemptCount;
                    eventType = "processing.attempt.changed";
                    payload = new { jobRef = j.PublicRef, state = j.State.ToString(), j.AttemptCount, j.MaxAttempts, j.FailureCode, failureKind = j.FailureKind.ToString() };
                    break;
                case ReviewComment c:
                    projectId = c.ProjectId; subject = c.PublicRef; sequence = c.StateSequence;
                    eventType = entry.State == EntityState.Added ? RealtimeEvents.CommentCreated : RealtimeEvents.CommentResolved;
                    payload = new { commentRef = c.PublicRef, c.IsResolved };
                    break;
                case Project p:
                    projectId = p.Id; subject = p.PublicRef; sequence = p.UpdatedAt.ToUnixTimeMilliseconds();
                    eventType = "project.changed"; payload = new { projectRef = p.PublicRef };
                    break;
                case ProjectMember m:
                    projectId = m.ProjectId; subject = "member_" + m.Id.ToString("N"); sequence = 1;
                    eventType = "project.membership.changed"; payload = new { role = m.Role.ToString(), removed = entry.State == EntityState.Deleted };
                    break;
                default: continue;
            }
            var id = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(payload, EventJson);
            var history = Domain.Processing.StateHistory.Create(id, projectId, subject, eventType, sequence, ActorUserId, json, now);
            pending.Add(history);
            var projectRef = Projects.Local.FirstOrDefault(p => p.Id == projectId)?.PublicRef ??
                await Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.PublicRef).SingleAsync(cancellationToken).ConfigureAwait(false);
            var notification = new RealtimeNotificationV1
            {
                MessageId = id, OccurredAt = now, ProjectRef = projectRef, EventType = eventType,
                StateSequence = sequence, Payload = JsonSerializer.SerializeToElement(payload, EventJson),
            };
            OutboxMessages.Add(OutboxMessage.Create(id, MessageTypes.RealtimeNotification, 1,
                JsonSerializer.Serialize(notification, EventJson), projectId, subject, Activity.Current?.Id, now));
        }
        StateHistory.AddRange(pending);
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
