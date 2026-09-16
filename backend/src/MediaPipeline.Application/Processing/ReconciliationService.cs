using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaPipeline.Application.Processing;

/// <summary>One inconsistency found between the database and object storage.</summary>
public sealed record ReconciliationFinding(string Kind, string Subject, string Detail, bool Repaired);

public sealed record ReconciliationReport(
    bool DryRun,
    int VersionsScanned,
    int ObjectsScanned,
    IReadOnlyList<ReconciliationFinding> Findings)
{
    public int RepairedCount => Findings.Count(f => f.Repaired);
}

/// <summary>
/// Cross-checks PostgreSQL against object storage and reports the drift a two-system write
/// path can produce.
/// <para>
/// Defaults to dry-run and never deletes media. The only repair it will perform is marking a
/// long-stale <c>Reserved</c> version as abandoned, which loses nothing: those rows describe
/// uploads that never delivered any bytes. Orphaned objects are reported for a human to
/// judge, because an object with no row may equally be a row that has not committed yet.
/// </para>
/// </summary>
public sealed class ReconciliationService(
    MediaPipelineDbContext db,
    IObjectStorage storage,
    IClock clock,
    ILogger<ReconciliationService> logger)
{
    /// <summary>How long a Reserved version may linger before it is considered abandoned.</summary>
    public static readonly TimeSpan ReservationGrace = TimeSpan.FromHours(1);

    /// <summary>An outbox row older than this without publishing is an operational alarm.</summary>
    public static readonly TimeSpan OutboxStaleAfter = TimeSpan.FromMinutes(15);

    public async Task<ReconciliationReport> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var findings = new List<ReconciliationFinding>();
        var now = clock.UtcNow;

        // 1. Stored versions whose source object is missing.
        var stored = await db.AssetVersions.AsNoTracking()
            .Where(v => v.LifecycleState == VersionLifecycleState.Stored || v.LifecycleState == VersionLifecycleState.Ready || v.LifecycleState == VersionLifecycleState.Quarantined)
            .Select(v => new { v.Id, v.PublicRef, v.SourceObjectKey })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var knownKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in stored)
        {
            knownKeys.Add(version.SourceObjectKey);
            if (!await storage.ExistsAsync(version.SourceObjectKey, cancellationToken).ConfigureAwait(false))
            {
                findings.Add(new ReconciliationFinding(
                    "missing_source_object", version.PublicRef,
                    "Version claims stored media that is absent from object storage.", false));
            }
        }

        // 2. Derivatives whose object is missing.
        var derivatives = await db.AssetDerivatives.AsNoTracking()
            .Select(d => new { d.Id, d.ObjectKey, d.Kind, d.AssetVersionId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var derivative in derivatives)
        {
            knownKeys.Add(derivative.ObjectKey);
            if (!await storage.ExistsAsync(derivative.ObjectKey, cancellationToken).ConfigureAwait(false))
            {
                findings.Add(new ReconciliationFinding(
                    "missing_derivative_object", derivative.Id.ToString(),
                    $"Recorded {derivative.Kind} derivative is absent from object storage.", false));
            }
        }

        // 3. Objects in storage with no row pointing at them.
        var objectsScanned = 0;
        await foreach (var item in storage.ListAsync("", cancellationToken).ConfigureAwait(false))
        {
            objectsScanned++;
            if (!knownKeys.Contains(item.Key))
            {
                findings.Add(new ReconciliationFinding(
                    "orphaned_object", item.Key,
                    $"Object has no database reference (last modified {item.LastModified:O}). Reported only - never auto-deleted.",
                    false));
            }
        }

        // 4. Reservations that never received bytes.
        var cutoff = now - ReservationGrace;
        var stale = await db.AssetVersions
            .Where(v => v.LifecycleState == VersionLifecycleState.Reserved && v.CreatedAt < cutoff)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var version in stale)
        {
            var repaired = false;
            if (!dryRun)
            {
                version.MarkAbandoned(now);
                repaired = true;
            }

            findings.Add(new ReconciliationFinding(
                "stale_reservation", version.PublicRef,
                $"Reserved for longer than {ReservationGrace.TotalMinutes:N0} minutes with no upload.", repaired));
        }

        if (!dryRun && stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // 5. Outbox rows the publisher has not managed to deliver.
        var outboxCutoff = now - OutboxStaleAfter;
        var stuckOutbox = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending && m.OccurredAt < outboxCutoff)
            .OrderBy(m => m.OccurredAt)
            .Select(m => new { m.Id, m.MessageType, m.AttemptCount, m.OccurredAt })
            .Take(100)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in stuckOutbox)
        {
            findings.Add(new ReconciliationFinding(
                "stale_outbox_message", row.Id.ToString(),
                $"{row.MessageType} pending since {row.OccurredAt:O} after {row.AttemptCount} attempts.", false));
        }

        // 6. Jobs whose claim expired - a worker died mid-flight.
        var expiredClaims = await db.ProcessingJobs.AsNoTracking()
            .Where(j => j.State == Domain.Assets.ProcessingState.Processing
                && j.ClaimExpiresAt != null && j.ClaimExpiresAt < now)
            .Select(j => new { j.PublicRef, j.ClaimedBy, j.ClaimExpiresAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var job in expiredClaims)
        {
            findings.Add(new ReconciliationFinding(
                "expired_processing_claim", job.PublicRef,
                $"Claim by '{job.ClaimedBy}' expired at {job.ClaimExpiresAt:O}; the broker will redeliver.", false));
        }

        logger.LogInformation(
            "event=reconciliation_completed dry_run={DryRun} versions={VersionsScanned} objects={ObjectsScanned} findings={FindingCount}",
            dryRun, stored.Count, objectsScanned, findings.Count);

        return new ReconciliationReport(dryRun, stored.Count, objectsScanned, findings);
    }
}
