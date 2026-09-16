using System.Diagnostics;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Media;
using MediaPipeline.Application.Observability;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaPipeline.Application.Processing;

/// <summary>What the consumer should do with the message after the handler returns.</summary>
public enum HandlerOutcome
{
    /// <summary>Work is durably recorded. Acknowledge.</summary>
    Complete,

    /// <summary>Transient failure with attempts remaining. Send to the next retry tier.</summary>
    Retry,

    /// <summary>Permanent failure or attempts exhausted. Dead-letter.</summary>
    DeadLetter,
}

public sealed record HandlerResult(HandlerOutcome Outcome, string? FailureCode = null, string? Reason = null);

/// <summary>
/// The media-processing use case, independent of the transport that delivered the message.
/// <para>
/// Idempotency is layered, because at-least-once delivery makes duplicates normal rather
/// than exceptional:
/// </para>
/// <list type="number">
///   <item><description>The inbox ledger short-circuits a message this consumer already finished.</description></item>
///   <item><description>A job already <c>Succeeded</c> is acknowledged without reprocessing.</description></item>
///   <item><description>Derivative keys are deterministic, so a re-run overwrites rather than duplicates.</description></item>
///   <item><description>The unique index on (version, kind) makes a duplicate row impossible.</description></item>
/// </list>
/// <para>
/// Side effects and the ledger row commit in one transaction, so the ledger can never claim
/// work that was not actually recorded.
/// </para>
/// </summary>
public sealed class MediaProcessingHandler(
    MediaPipelineDbContext db,
    IObjectStorage storage,
    IMediaProcessor mediaProcessor,
    IClock clock,
    MediaPipelineMetrics metrics,
    IOptions<MediaPolicyOptions> mediaPolicy,
    ILogger<MediaProcessingHandler> logger)
{
    public const string ConsumerName = "media-worker";
    private static readonly TimeSpan ClaimTtl = TimeSpan.FromMinutes(10);

    private readonly MediaPolicyOptions _policy = mediaPolicy.Value;

    public async Task<HandlerResult> HandleAsync(
        MediaProcessingRequestedV1 message,
        string workerId,
        CancellationToken cancellationToken)
    {
        using var activity = MediaPipelineTelemetry.ActivitySource.StartActivity("media.process", ActivityKind.Consumer);
        activity?.SetTag("collaborative-media-pipeline.version_ref", message.AssetVersionRef);
        activity?.SetTag("collaborative-media-pipeline.job_ref", message.JobRef);

        var started = Stopwatch.GetTimestamp();

        // Layer 1: has this consumer already finished this exact message?
        var alreadyProcessed = await db.ProcessedMessages.AsNoTracking()
            .AnyAsync(p => p.MessageId == message.MessageId && p.Consumer == ConsumerName, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyProcessed)
        {
            metrics.DuplicateMessagesSkipped.Add(1);
            logger.LogInformation(
                "event=duplicate_message_skipped message_id={MessageId} version_ref={VersionRef}",
                message.MessageId, message.AssetVersionRef);
            return new HandlerResult(HandlerOutcome.Complete, Reason: "duplicate");
        }

        var job = await db.ProcessingJobs
            .SingleOrDefaultAsync(j => j.PublicRef == message.JobRef, cancellationToken).ConfigureAwait(false);

        if (job is null)
        {
            // The job row is gone (asset deleted, or the message predates a restore).
            // Nothing to do and nothing to retry.
            logger.LogWarning("event=processing_job_missing job_ref={JobRef}", message.JobRef);
            return new HandlerResult(HandlerOutcome.Complete, Reason: "job-missing");
        }

        var version = await db.AssetVersions
            .Include(v => v.Derivatives)
            .SingleOrDefaultAsync(v => v.Id == job.AssetVersionId, cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            logger.LogWarning("event=processing_version_missing job_ref={JobRef}", message.JobRef);
            return new HandlerResult(HandlerOutcome.Complete, Reason: "version-missing");
        }

        if (message.MessageId == Guid.Empty || message.AssetVersionRef != version.PublicRef ||
            message.SourceObjectKey != version.SourceObjectKey || message.Sha256 != version.Sha256 ||
            message.ByteSize != version.ByteSize || message.ContentType != version.ContentType ||
            message.Operations is null || message.Operations.Count != 2 ||
            !message.Operations.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["Thumbnail", "Preview"]))
            return new HandlerResult(HandlerOutcome.DeadLetter, "INVALID_JOB_CONTRACT");
        if (job.State == ProcessingState.Processing && job.ClaimExpiresAt > clock.UtcNow)
            return new HandlerResult(HandlerOutcome.Retry, "JOB_ALREADY_CLAIMED");
        if (!job.IsTerminal && job.AttemptCount >= job.MaxAttempts)
        {
            job.MarkFailed(ProcessingFailureKind.Transient, "RETRY_BUDGET_EXHAUSTED", "The attempt budget is exhausted.", clock.UtcNow);
            version.MarkProcessingFailed(true, clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new HandlerResult(HandlerOutcome.DeadLetter, "RETRY_BUDGET_EXHAUSTED");
        }

        // Layer 2: a completed job acknowledges rather than reprocessing.
        var now = clock.UtcNow;
        if (!job.TryClaim(workerId, ClaimTtl, now))
        {
            logger.LogInformation(
                "event=processing_already_succeeded job_ref={JobRef} version_ref={VersionRef}",
                job.PublicRef, version.PublicRef);
            await RecordProcessedAsync(message, "already-succeeded", cancellationToken).ConfigureAwait(false);
            return new HandlerResult(HandlerOutcome.Complete, Reason: "already-succeeded");
        }

        version.MarkProcessing(now);
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return new HandlerResult(HandlerOutcome.Retry, "JOB_ALREADY_CLAIMED"); }

        metrics.ProcessingJobsStarted.Add(1);
        logger.LogInformation(
            "event=processing_started job_ref={JobRef} version_ref={VersionRef} attempt={AttemptCount}",
            job.PublicRef, version.PublicRef, job.AttemptCount);



        try
        {
            var (width, height, derivatives) = await ProduceDerivativesAsync(message, version, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await CommitSuccessAsync(message, job.Id, version.Id, width, height, derivatives, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally { foreach (var derivative in derivatives) derivative.Dispose(); }

            metrics.ProcessingJobsSucceeded.Add(1);
            metrics.ProcessingDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", "succeeded"));

            logger.LogInformation(
                "event=processing_succeeded job_ref={JobRef} version_ref={VersionRef} derivatives={DerivativeCount}",
                job.PublicRef, version.PublicRef, derivatives.Count);



            return new HandlerResult(HandlerOutcome.Complete);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-flight: leave the job claimed and let the broker redeliver.
            // The claim expires, so a restarted worker can pick it up.
            logger.LogInformation("event=processing_interrupted job_ref={JobRef}", job.PublicRef);
            throw;
        }
        catch (Exception ex)
        {
            var (kind, code, reason) = Classify(ex);
            var retryable = await CommitFailureAsync(message, job.Id, version.Id, kind, code, reason, cancellationToken)
                .ConfigureAwait(false);

            metrics.ProcessingJobsFailed.Add(1, new KeyValuePair<string, object?>("failure_kind", kind.ToString()));
            metrics.ProcessingDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", "failed"));

            logger.LogError(ex,
                "event=processing_failed job_ref={JobRef} version_ref={VersionRef} failure_kind={FailureKind} " +
                "failure_code={FailureCode} retryable={Retryable}",
                job.PublicRef, version.PublicRef, kind, code, retryable);



            return new HandlerResult(
                retryable ? HandlerOutcome.Retry : HandlerOutcome.DeadLetter, code, reason);
        }
    }

    private async Task<(int Width, int Height, List<GeneratedDerivative> Derivatives)> ProduceDerivativesAsync(
        MediaProcessingRequestedV1 message,
        AssetVersion version,
        CancellationToken cancellationToken)
    {
        // Bound the whole operation: a pathological file must not pin a worker slot.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_policy.ProcessingTimeout);

        var source = await storage.ReadAsync(version.SourceObjectKey, timeout.Token).ConfigureAwait(false)
            ?? throw new PermanentSourceMissingException(version.SourceObjectKey);

        var content = source.Content;
        await using var contentScope = content.ConfigureAwait(false);

        // Read the source once into a seekable buffer: the codec needs to rewind between
        // the probe and each derivative, and the size is already capped by upload policy.
        if (source.ByteSize <= 0 || source.ByteSize > _policy.MaxUploadBytes)
            throw new PermanentMediaException("MEDIA_SIZE_EXCEEDED", "The stored media exceeds the permitted size.");
        await using var buffer = new MemoryStream(capacity: (int)source.ByteSize);
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > _policy.MaxUploadBytes)
                throw new PermanentMediaException("MEDIA_SIZE_EXCEEDED", "The stored media exceeds the permitted size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
        }
        var checksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        if (buffer.Length != version.ByteSize || checksum != version.Sha256)
            throw new PermanentMediaException("SOURCE_INTEGRITY_FAILED", "Stored media does not match its immutable upload checksum.");
        buffer.Position = 0;

        var probe = mediaProcessor.Probe(buffer);
        var derivatives = new List<GeneratedDerivative>();

        try
        {
            foreach (var operation in message.Operations)
            {
                if (!Enum.TryParse<DerivativeKind>(operation, ignoreCase: true, out var kind))
                {
                    throw new UnsupportedOperationException(operation);
                }

                buffer.Position = 0;
                var derivative = await mediaProcessor.CreateDerivativeAsync(buffer, kind, timeout.Token).ConfigureAwait(false);
                derivatives.Add(derivative);

                var key = StorageKeys.Derivative(
                    version.ProjectId, version.AssetId, version.Id, kind, derivative.Extension);
                StorageKeys.EnsureSafe(key);

                derivative.Content.Position = 0;
                await storage.WriteAsync(key, derivative.Content, derivative.ContentType, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (var derivative in derivatives)
            {
                derivative.Dispose();
            }

            throw;
        }
        finally
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
        }

        return (probe.Width, probe.Height, derivatives);
    }

    /// <summary>
    /// One transaction: derivative rows, version state, job state, and the inbox ledger.
    /// A crash before this commits means the message is redelivered and the whole attempt
    /// repeats safely; a crash after it means the ledger recognises the redelivery.
    /// </summary>
    private async Task CommitSuccessAsync(
        MediaProcessingRequestedV1 message,
        Guid jobId,
        Guid versionId,
        int width,
        int height,
        List<GeneratedDerivative> derivatives,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var transactionScope = transaction.ConfigureAwait(false);
            var now = clock.UtcNow;

            var version = await db.AssetVersions.Include(v => v.Derivatives)
                .SingleAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
            var job = await db.ProcessingJobs.SingleAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);

            foreach (var derivative in derivatives)
            {
                var key = StorageKeys.Derivative(version.ProjectId, version.AssetId, version.Id, derivative.Kind, derivative.Extension);
                version.UpsertDerivative(
                    derivative.Kind, key, derivative.ContentType,
                    derivative.Width, derivative.Height, derivative.ByteSize, derivative.Sha256, now);
            }

            version.MarkProcessingSucceeded(width, height, now);
            job.MarkSucceeded(now);

            db.ProcessedMessages.Add(ProcessedMessage.Create(
                message.MessageId, ConsumerName, MessageTypes.MediaProcessingRequested, "succeeded", now));

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

    }

    private async Task<bool> CommitFailureAsync(
        MediaProcessingRequestedV1 message,
        Guid jobId,
        Guid versionId,
        ProcessingFailureKind kind,
        string code,
        string reason,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var token = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;

        var job = await db.ProcessingJobs.SingleAsync(j => j.Id == jobId, token).ConfigureAwait(false);
        var version = await db.AssetVersions.SingleAsync(v => v.Id == versionId, token).ConfigureAwait(false);
        var now = clock.UtcNow;
        if (job.IsTerminal) return false;

        var retryable = job.MarkFailed(kind, code, reason, now);
        if (kind == ProcessingFailureKind.Permanent) version.Quarantine(now);
        else version.MarkProcessingFailed(exhausted: !retryable, now);

        if (!retryable)
        {
            // Terminal: record it in the ledger so a dead-letter replay of the *same*
            // message id does not re-run work that already reached a final decision.
            db.ProcessedMessages.Add(ProcessedMessage.Create(
                message.MessageId, ConsumerName, MessageTypes.MediaProcessingRequested, $"failed:{code}", now));
        }

        await db.SaveChangesAsync(token).ConfigureAwait(false);
        return retryable;
    }

    private async Task RecordProcessedAsync(MediaProcessingRequestedV1 message, string outcome, CancellationToken cancellationToken)
    {
        db.ProcessedMessages.Add(ProcessedMessage.Create(
            message.MessageId, ConsumerName, MessageTypes.MediaProcessingRequested, outcome, clock.UtcNow));

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Decides whether a failure is worth retrying. Anything that would fail identically on
    /// the next attempt is permanent, so poison media never loops through the retry tiers.
    /// </summary>
    private static (ProcessingFailureKind Kind, string Code, string Reason) Classify(Exception ex) => ex switch
    {
        PermanentSourceMissingException => (ProcessingFailureKind.Permanent, "SOURCE_OBJECT_MISSING",
            "The source media is not present in storage."),
        UnsupportedOperationException op => (ProcessingFailureKind.Permanent, "UNSUPPORTED_OPERATION",
            $"Unsupported processing operation '{op.Operation}'."),
        Media.PermanentMediaException permanent => (ProcessingFailureKind.Permanent, permanent.Code, permanent.Message),
        OperationCanceledException => (ProcessingFailureKind.Transient, "PROCESSING_TIMEOUT",
            "Processing exceeded its time budget."),
        TimeoutException => (ProcessingFailureKind.Transient, "PROCESSING_TIMEOUT",
            "Processing exceeded its time budget."),
        _ => (ProcessingFailureKind.Transient, "PROCESSING_ERROR", "A transient error interrupted processing."),
    };
}

internal sealed class PermanentSourceMissingException(string key)
    : Exception($"Source object '{key}' is missing.");

internal sealed class UnsupportedOperationException(string operation)
    : Exception($"Unsupported operation '{operation}'.")
{
    public string Operation { get; } = operation;
}
