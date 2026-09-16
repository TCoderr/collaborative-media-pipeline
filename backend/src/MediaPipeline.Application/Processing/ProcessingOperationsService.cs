using System.Diagnostics;
using System.Text.Json;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Contracts;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Application.Projects;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaPipeline.Application.Processing;

/// <summary>Aggregate health of the asynchronous pipeline, for operators and health checks.</summary>
public sealed record PipelineHealth(
    int OutboxPending,
    double OutboxOldestAgeSeconds,
    int JobsQueued,
    int JobsProcessing,
    int JobsFailed,
    int JobsDeadLettered);

/// <summary>
/// Operator-facing processing actions and diagnostics.
/// <para>
/// Retry is exposed as an authorised product action rather than an admin console: an Editor
/// on the project can re-run a failed derivative for their own asset, and the replay reuses
/// the existing job so its failure history survives.
/// </para>
/// </summary>
public sealed class ProcessingOperationsService(
    MediaPipelineDbContext db,
    ProjectAccessService access,
    IClock clock,
    ILogger<ProcessingOperationsService> logger)
{
    public async Task<ProcessingJobView> RetryAsync(Guid userId, string versionRef, CancellationToken cancellationToken)
    {
        var (project, versionId, assetId) = await access
            .RequireVersionAsync(userId, versionRef, ProjectCapability.RetryProcessing, cancellationToken)
            .ConfigureAwait(false);

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var transactionScope = transaction.ConfigureAwait(false);

            var job = await db.ProcessingJobs.SingleOrDefaultAsync(j => j.AssetVersionId == versionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw MediaPipelineException.NotFound("PROCESSING_JOB_NOT_FOUND", "There is no processing job for this version.");

            var version = await db.AssetVersions.SingleAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
            var asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId, cancellationToken).ConfigureAwait(false);

            if (version.LifecycleState is VersionLifecycleState.Reserved
                or VersionLifecycleState.Failed
                or VersionLifecycleState.Abandoned)
            {
                throw MediaPipelineException.Rule(
                    "SOURCE_NOT_AVAILABLE", "This version has no stored source media to reprocess.");
            }

            var now = clock.UtcNow;
            job.PrepareRetry(now);
            version.MarkQueued(now);

            // A fresh message id: the inbox ledger keys on message id, so reusing the old
            // one would make the worker recognise the replay as an already-handled duplicate.
            var messageId = Guid.CreateVersion7();
            var payload = JsonSerializer.Serialize(new MediaProcessingRequestedV1
            {
                MessageId = messageId,
                OccurredAt = now,
                ProjectRef = project.ProjectRef,
                AssetRef = asset.PublicRef,
                AssetVersionRef = version.PublicRef,
                JobRef = job.PublicRef,
                SourceObjectKey = version.SourceObjectKey,
                ContentType = version.ContentType,
                ByteSize = version.ByteSize,
                Sha256 = version.Sha256,
                Operations = [DerivativeKind.Thumbnail.ToString(), DerivativeKind.Preview.ToString()],
            });

            db.OutboxMessages.Add(OutboxMessage.Create(
                messageId,
                MessageTypes.MediaProcessingRequested,
                MediaProcessingRequestedV1.ContractVersion,
                payload,
                project.ProjectId,
                version.PublicRef,
                Activity.Current?.Id,
                now));

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "event=processing_retry_requested project_ref={ProjectRef} version_ref={VersionRef} job_ref={JobRef} attempt={AttemptCount}",
                project.ProjectRef, versionRef, job.PublicRef, job.AttemptCount);

            return new ProcessingJobView(
                job.PublicRef, job.State.ToString(), job.AttemptCount, job.MaxAttempts,
                job.FailureKind.ToString(), job.FailureCode, job.FailureMessage, job.StartedAt, job.CompletedAt);
        }).ConfigureAwait(false);
    }

    public async Task<ProcessingJobView> GetJobAsync(Guid userId, string jobRef, CancellationToken cancellationToken)
    {
        if (!PublicRefGuard.IsJob(jobRef))
        {
            throw MediaPipelineException.NotFound("PROCESSING_JOB_NOT_FOUND", "Processing job does not exist.");
        }

        var row = await db.ProcessingJobs.AsNoTracking()
            .Where(j => j.PublicRef == jobRef)
            .Join(db.Projects.AsNoTracking(), j => j.ProjectId, p => p.Id, (j, p) => new { Job = j, p.PublicRef })
            .Join(
                db.ProjectMembers.AsNoTracking().Where(m => m.UserId == userId),
                x => x.Job.ProjectId,
                m => m.ProjectId,
                (x, m) => x.Job)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw MediaPipelineException.NotFound("PROCESSING_JOB_NOT_FOUND", "Processing job does not exist.");

        return new ProcessingJobView(
            row.PublicRef, row.State.ToString(), row.AttemptCount, row.MaxAttempts,
            row.FailureKind.ToString(), row.FailureCode, row.FailureMessage, row.StartedAt, row.CompletedAt);
    }

    /// <summary>
    /// Pipeline health, used by the operations endpoint and by observable gauges. Read with
    /// a short timeout by callers so a slow database cannot stall a health probe.
    /// </summary>
    public async Task<PipelineHealth> GetPipelineHealthAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var outboxPending = await db.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Status == OutboxStatus.Pending, cancellationToken).ConfigureAwait(false);

        var oldest = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending)
            .OrderBy(m => m.OccurredAt)
            .Select(m => (DateTimeOffset?)m.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var jobCounts = await db.ProcessingJobs.AsNoTracking()
            .GroupBy(j => j.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int CountOf(ProcessingState state) => jobCounts.SingleOrDefault(c => c.State == state)?.Count ?? 0;

        return new PipelineHealth(
            outboxPending,
            oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds),
            CountOf(ProcessingState.Queued),
            CountOf(ProcessingState.Processing),
            CountOf(ProcessingState.Failed),
            CountOf(ProcessingState.DeadLettered));
    }
}
