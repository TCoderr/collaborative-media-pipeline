using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Contracts;
using MediaPipeline.Application.Media;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Application.Projects;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaPipeline.Application.Assets;

/// <summary>What the caller supplies when uploading media.</summary>
public sealed record UploadRequest(Stream Content, string FileName, string? DeclaredContentType, string? IdempotencyKey);

/// <summary>
/// Asset and version lifecycle.
/// <para>
/// The upload path deliberately spans three commits, because object storage and PostgreSQL
/// are not one transaction:
/// </para>
/// <list type="number">
///   <item><description><b>Reserve</b> - allocate the version number and the storage key, commit.</description></item>
///   <item><description><b>Store</b> - stream the bytes to storage (no database involvement).</description></item>
///   <item><description><b>Finalise</b> - record metadata, create the processing job and the
///   outbox row in one transaction, commit.</description></item>
/// </list>
/// <para>
/// Each interruption leaves a state the system can reason about: a crash after (1) leaves a
/// Reserved version that reconciliation abandons; a crash after (2) leaves an orphan object
/// that reconciliation reports; a crash after (3) leaves durable work the outbox will
/// deliver. Nothing is lost, and nothing is ever half-visible to a collaborator.
/// </para>
/// </summary>
public sealed class AssetService(
    MediaPipelineDbContext db,
    ProjectAccessService access,
    IObjectStorage storage,
    IClock clock,
    IOptions<MediaPolicyOptions> mediaPolicy,
    ILogger<AssetService> logger)
{
    // Leave enough bounded attempts for the supported six-upload contention burst.
    private const int MaxVersionAllocationAttempts = 8;
    private readonly MediaPolicyOptions _policy = mediaPolicy.Value;

    public async Task<AssetDetail> CreateAssetAsync(
        Guid userId,
        string projectRef,
        string title,
        UploadRequest upload,
        CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.UploadAsset, cancellationToken)
            .ConfigureAwait(false);

        var replay = await TryReplayAsync(userId, "assets.create", upload.IdempotencyKey, projectRef, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            return await GetAssetDetailAsync(userId, replay, cancellationToken).ConfigureAwait(false);
        }

        var asset = Asset.Create(resolved.ProjectId, title, userId, clock.UtcNow);
        db.ActorUserId = userId;
        db.Assets.Add(asset);
        SetReservationResponse(userId, "assets.create", upload.IdempotencyKey, asset.PublicRef);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("event=asset_created project_ref={ProjectRef} asset_ref={AssetRef}", projectRef, asset.PublicRef);

        await AddVersionAsync(userId, resolved, asset.Id, asset.PublicRef, upload, cancellationToken).ConfigureAwait(false);
        await RecordIdempotencyAsync(userId, "assets.create", upload.IdempotencyKey, projectRef, asset.PublicRef, cancellationToken)
            .ConfigureAwait(false);

        return await GetAssetDetailAsync(userId, asset.PublicRef, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetVersionSummary> CreateVersionAsync(
        Guid userId,
        string assetRef,
        UploadRequest upload,
        CancellationToken cancellationToken)
    {
        var (resolved, assetId, publicRef, status) = await access
            .RequireAssetAsync(userId, assetRef, ProjectCapability.CreateVersion, cancellationToken).ConfigureAwait(false);

        if (status != AssetStatus.Active)
        {
            throw MediaPipelineException.Rule("ASSET_NOT_ACTIVE", "New versions can only be added to an active asset.");
        }

        var replay = await TryReplayAsync(userId, "versions.create", upload.IdempotencyKey, assetRef, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            return await GetVersionAsync(userId, replay, cancellationToken).ConfigureAwait(false);
        }

        var versionRef = await AddVersionAsync(userId, resolved, assetId, publicRef, upload, cancellationToken).ConfigureAwait(false);
        await RecordIdempotencyAsync(userId, "versions.create", upload.IdempotencyKey, assetRef, versionRef, cancellationToken)
            .ConfigureAwait(false);

        return await GetVersionAsync(userId, versionRef, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> AddVersionAsync(
        Guid userId,
        ProjectAccess project,
        Guid assetId,
        string assetRef,
        UploadRequest upload,
        CancellationToken cancellationToken)
    {
        // ── 1. Reserve ────────────────────────────────────────────────────────────
        var version = await ReserveVersionAsync(project.ProjectId, assetId, userId, upload.FileName, upload.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // ── 2. Store ──────────────────────────────────────────────────────────
            var (media, prefix) = await UploadSniffer.SniffAsync(upload.Content, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(upload.DeclaredContentType) && upload.DeclaredContentType != "application/octet-stream" && !string.Equals(upload.DeclaredContentType, media.ContentType, StringComparison.OrdinalIgnoreCase))
                throw MediaPipelineException.Validation("CONTENT_TYPE_MISMATCH", "Declared media type does not match the uploaded content.");

            var hashing = new HashingUploadStream(upload.Content, prefix, _policy.MaxUploadBytes);
            await using var hashingScope = hashing.ConfigureAwait(false);
            await storage.WriteAsync(version.SourceObjectKey, hashing, media.ContentType, cancellationToken).ConfigureAwait(false);

            var byteSize = hashing.BytesRead;
            var digest = hashing.Digest;

            // ── 3. Finalise ───────────────────────────────────────────────────────
            var versionRef = await FinaliseVersionAsync(
                version.Id, assetId, assetRef, project, media.ContentType, byteSize, digest, cancellationToken)
                .ConfigureAwait(false);

            return versionRef;
        }
        catch (Exception ex)
        {
            // Compensate: the version row exists, so record the failure rather than
            // leaving a Reserved row that looks like an upload still in progress.
            await MarkUploadFailedAsync(version.Id, cancellationToken).ConfigureAwait(false);

            if (ex is MediaPipelineException or OperationCanceledException)
            {
                throw;
            }

            logger.LogError(ex, "event=upload_failed asset_ref={AssetRef} version_ref={VersionRef}", assetRef, version.PublicRef);
            throw MediaPipelineException.Dependency("UPLOAD_FAILED", "The upload could not be completed. Please try again.", ex);
        }
    }

    /// <summary>
    /// Allocates the next version number.
    /// <para>
    /// The number is computed from a <c>MAX(...)</c> read and then defended by the unique
    /// index on <c>(asset_id, version_number)</c>. Two editors racing for "V3" both compute
    /// 3; the database rejects the loser, who simply retries and becomes V4. Correctness
    /// lives in the constraint, not in application-side coordination.
    /// </para>
    /// </summary>
    private async Task<AssetVersion> ReserveVersionAsync(
        Guid projectId,
        Guid assetId,
        Guid userId,
        string fileName,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxVersionAllocationAttempts; attempt++)
        {
            var highest = await db.AssetVersions
                .AsNoTracking()
                .Where(v => v.AssetId == assetId)
                .Select(v => (int?)v.VersionNumber)
                .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;

            var next = highest + 1;
            var version = AssetVersion.Reserve(
                assetId,
                projectId,
                next,
                versionId =>
                {
                    var key = StorageKeys.Source(projectId, assetId, versionId);
                    StorageKeys.EnsureSafe(key);
                    return key;
                },
                fileName,
                userId,
                clock.UtcNow);

            db.ActorUserId = userId;
            db.AssetVersions.Add(version);
            if (idempotencyKey is not null)
            {
                var reservation = await db.IdempotencyRecords.SingleOrDefaultAsync(r => r.UserId == userId && r.Endpoint == "versions.create" && r.Key == idempotencyKey, cancellationToken).ConfigureAwait(false);
                reservation?.SetResponse(version.PublicRef);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return version;
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                logger.LogInformation(
                    "event=version_number_contended asset_id={AssetId} attempted={VersionNumber} attempt={Attempt}",
                    assetId, next, attempt);
            }
        }

        throw MediaPipelineException.Conflict(
            "VERSION_ALLOCATION_CONTENTION",
            "Too many collaborators are creating versions of this asset at once. Please retry.");
    }

    /// <summary>
    /// One transaction: version metadata, the processing job, and the outbox row announcing
    /// it. Either all three are durable or none is, so the broker can never be told about
    /// work the database does not know about.
    /// </summary>
    private async Task<string> FinaliseVersionAsync(
        Guid versionId,
        Guid assetId,
        string assetRef,
        ProjectAccess project,
        string contentType,
        long byteSize,
        string digest,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var transactionScope = transaction.ConfigureAwait(false);

            var version = await db.AssetVersions.SingleAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
            var now = clock.UtcNow;

            version.MarkStored(contentType, byteSize, digest, now);
            version.MarkQueued(now);

            // The asset's "current version" pointer is advanced with a single conditional
            // statement rather than by loading the asset and saving it.
            //
            // Loading it would put the row's xmin into the transaction, so two editors
            // uploading different versions at the same moment would collide on the *asset*
            // and one would get a spurious conflict - even though both versions are valid
            // and correctly numbered. Two people adding versions concurrently is ordinary
            // collaboration, not a contended edit.
            //
            // The WHERE clause makes the write monotonic: the pointer only moves to a
            // higher version number, so a slower transaction finishing second cannot demote
            // the asset back to an older version.
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE assets AS a
                SET current_version_id = {version.Id}, updated_at = {now}
                WHERE a.id = {assetId}
                  AND (
                        a.current_version_id IS NULL
                     OR {version.VersionNumber} > COALESCE(
                            (SELECT cv.version_number FROM asset_versions AS cv WHERE cv.id = a.current_version_id), 0)
                  )
                """,
                cancellationToken).ConfigureAwait(false);

            var job = ProcessingJob.Create(version.Id, project.ProjectId, now);
            db.ProcessingJobs.Add(job);

            var messageId = Guid.CreateVersion7();
            var payload = JsonSerializer.Serialize(new MediaProcessingRequestedV1
            {
                MessageId = messageId,
                OccurredAt = now,
                ProjectRef = project.ProjectRef,
                AssetRef = assetRef,
                AssetVersionRef = version.PublicRef,
                JobRef = job.PublicRef,
                SourceObjectKey = version.SourceObjectKey,
                ContentType = contentType,
                ByteSize = byteSize,
                Sha256 = digest,
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
                "event=asset_version_stored project_ref={ProjectRef} asset_ref={AssetRef} version_ref={VersionRef} " +
                "version_number={VersionNumber} bytes={ByteSize} job_ref={JobRef}",
                project.ProjectRef, assetRef, version.PublicRef, version.VersionNumber, byteSize, job.PublicRef);

            // Real-time is announced only after the commit above succeeded.

            return version.PublicRef;
        }).ConfigureAwait(false);
    }

    private async Task MarkUploadFailedAsync(Guid versionId, CancellationToken cancellationToken)
    {
        try
        {
            db.ChangeTracker.Clear();
            var version = await db.AssetVersions.SingleOrDefaultAsync(v => v.Id == versionId, CancellationToken.None)
                .ConfigureAwait(false);

            if (version is null || version.LifecycleState != VersionLifecycleState.Reserved)
            {
                return;
            }

            version.MarkUploadFailed(clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Compensation is best effort; reconciliation will pick the row up otherwise.
            logger.LogWarning(ex, "event=upload_compensation_failed version_id={VersionId}", versionId);
        }

        _ = cancellationToken;
    }

    // ── Reads ────────────────────────────────────────────────────────────────────

    public async Task<Page<AssetSummary>> ListAssetsAsync(
        Guid userId,
        string projectRef,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var resolved = await access.RequireAsync(userId, projectRef, ProjectCapability.ViewProject, cancellationToken)
            .ConfigureAwait(false);

        var page = request.Normalised();
        var cursor = Cursor.Decode(page.Cursor);

        var query = db.Assets.AsNoTracking()
            .Where(a => a.ProjectId == resolved.ProjectId && a.Status != AssetStatus.Deleted);

        if (cursor is not null)
        {
            query = query.Where(a => a.CreatedAt < cursor.Value.CreatedAt
                || (a.CreatedAt == cursor.Value.CreatedAt && a.Id.CompareTo(cursor.Value.Id) < 0));
        }

        // One query, no N+1: the current version and its derivatives are joined in as
        // correlated projections rather than lazily walked per asset.
        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(page.Limit + 1)
            .Select(a => new
            {
                a.Id,
                a.PublicRef,
                a.Title,
                a.Status,
                a.CreatedAt,
                a.UpdatedAt,
                a.CurrentVersionId,
                VersionCount = db.AssetVersions.Count(v => v.AssetId == a.Id
                    && v.LifecycleState != VersionLifecycleState.Abandoned
                    && v.LifecycleState != VersionLifecycleState.Reserved),
                Current = db.AssetVersions
                    .Where(v => v.Id == a.CurrentVersionId)
                    .Select(v => new VersionRow
                    {
                        Id = v.Id,
                        RowVersion = v.RowVersion,
                        PublicRef = v.PublicRef,
                        VersionNumber = v.VersionNumber,
                        LifecycleState = v.LifecycleState,
                        ProcessingState = v.ProcessingState,
                        ReviewState = v.ReviewState,
                        OriginalFileName = v.OriginalFileName,
                        ContentType = v.ContentType,
                        ByteSize = v.ByteSize,
                        Sha256 = v.Sha256,
                        Width = v.Width,
                        Height = v.Height,
                        StateSequence = v.StateSequence,
                        CreatedAt = v.CreatedAt,
                        CreatedByRef = db.Users.Where(u => u.Id == v.CreatedByUserId).Select(u => u.PublicRef).Single(),
                        CreatedByName = db.Users.Where(u => u.Id == v.CreatedByUserId).Select(u => u.DisplayName).Single(),
                        Derivatives = db.AssetDerivatives
                            .Where(d => d.AssetVersionId == v.Id)
                            .Select(d => new DerivativeRow(d.Kind, d.Width, d.Height, d.ByteSize))
                            .ToList(),
                        Job = db.ProcessingJobs
                            .Where(j => j.AssetVersionId == v.Id)
                            .Select(j => new ProcessingJobView(
                                j.PublicRef, j.State.ToString(), j.AttemptCount, j.MaxAttempts,
                                j.FailureKind.ToString(), j.FailureCode, j.FailureMessage, j.StartedAt, j.CompletedAt))
                            .SingleOrDefault(),
                    })
                    .SingleOrDefault(),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit)
            .Select(r => new AssetSummary(
                r.PublicRef, r.Title, r.Status.ToString(), r.VersionCount,
                r.Current is null ? null : MapVersion(r.Current), r.CreatedAt, r.UpdatedAt))
            .ToList();

        var next = hasMore ? Cursor.Encode(rows[page.Limit - 1].CreatedAt, rows[page.Limit - 1].Id) : null;
        return new Page<AssetSummary>(items, next, page.Limit);
    }

    public async Task<AssetDetail> GetAssetDetailAsync(Guid userId, string assetRef, CancellationToken cancellationToken)
    {
        var (resolved, assetId, publicRef, _) = await access
            .RequireAssetAsync(userId, assetRef, ProjectCapability.ViewProject, cancellationToken).ConfigureAwait(false);

        var asset = await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new { a.Title, a.Status, a.CurrentVersionId, a.CreatedAt, a.UpdatedAt })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        // Bounded: the detail view shows recent history, the paged versions endpoint
        // serves the full archive. No API response is ever an unbounded list.
        const int DetailVersionWindow = 50;

        var versions = await VersionQuery()
            .Where(v => v.AssetId == assetId && v.LifecycleState != VersionLifecycleState.Reserved)
            .OrderByDescending(v => v.VersionNumber)
            .Take(DetailVersionWindow)
            .Select(ProjectVersion())
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var currentRef = versions.Count > 0 ? versions[0].PublicRef : null;

        return new AssetDetail(
            publicRef,
            asset.Title,
            asset.Status.ToString(),
            resolved.ProjectRef,
            versions.Select(MapVersion).ToList(),
            currentRef,
            asset.CreatedAt,
            asset.UpdatedAt);
    }

    public async Task<AssetVersionSummary> GetVersionAsync(Guid userId, string versionRef, CancellationToken cancellationToken)
    {
        var (_, versionId, _) = await access
            .RequireVersionAsync(userId, versionRef, ProjectCapability.ViewProject, cancellationToken).ConfigureAwait(false);

        var row = await VersionQuery().Where(v => v.Id == versionId).Select(ProjectVersion())
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        return MapVersion(row);
    }

    public async Task<Page<AssetVersionSummary>> ListVersionsAsync(
        Guid userId,
        string assetRef,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var (_, assetId, _, _) = await access
            .RequireAssetAsync(userId, assetRef, ProjectCapability.ViewProject, cancellationToken).ConfigureAwait(false);

        var page = request.Normalised();
        var cursor = Cursor.Decode(page.Cursor);

        var query = VersionQuery().Where(v => v.AssetId == assetId && v.LifecycleState != VersionLifecycleState.Reserved);
        if (cursor is not null)
        {
            query = query.Where(v => v.CreatedAt < cursor.Value.CreatedAt
                || (v.CreatedAt == cursor.Value.CreatedAt && v.Id.CompareTo(cursor.Value.Id) < 0));
        }

        // Version numbers are allocated in creation order, so ordering by (CreatedAt, Id)
        // is the same history as ordering by version number - and it is the ordering the
        // keyset cursor can express without a second sort key.
        var rows = await query
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .Take(page.Limit + 1)
            .Select(ProjectVersion())
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).Select(MapVersion).ToList();
        var next = hasMore ? Cursor.Encode(rows[page.Limit - 1].CreatedAt, rows[page.Limit - 1].Id) : null;

        return new Page<AssetVersionSummary>(items, next, page.Limit);
    }

    /// <summary>
    /// Streams a version's media back through the API. Access is decided here, on the
    /// caller's membership - storage itself is private and never serves a request directly.
    /// </summary>
    public async Task<StoredObject> OpenContentAsync(
        Guid userId,
        string versionRef,
        DerivativeKind? derivative,
        CancellationToken cancellationToken)
    {
        var (_, versionId, _) = await access
            .RequireVersionAsync(userId, versionRef, ProjectCapability.ViewProject, cancellationToken).ConfigureAwait(false);

        if (!await db.AssetVersions.AsNoTracking().AnyAsync(v => v.Id == versionId && v.ProcessingState == ProcessingState.Succeeded && v.LifecycleState == VersionLifecycleState.Ready, cancellationToken).ConfigureAwait(false))
            throw MediaPipelineException.Rule("MEDIA_QUARANTINED", "Media is unavailable until validation and processing succeed.");

        string? key;
        if (derivative is null)
        {
            key = await db.AssetVersions.AsNoTracking()
                .Where(v => v.Id == versionId && v.LifecycleState != VersionLifecycleState.Reserved)
                .Select(v => v.SourceObjectKey)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            key = await db.AssetDerivatives.AsNoTracking()
                .Where(d => d.AssetVersionId == versionId && d.Kind == derivative.Value)
                .Select(d => d.ObjectKey)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (key is null)
        {
            throw MediaPipelineException.NotFound("MEDIA_NOT_FOUND", "That media is not available.");
        }

        var stored = await storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            // The row promises an object that storage does not have: a real inconsistency
            // rather than a client error, so it is logged for reconciliation.
            logger.LogError("event=storage_object_missing object_key_hash={KeyHash}", HashKey(key));
            throw MediaPipelineException.NotFound("MEDIA_NOT_FOUND", "That media is not available.");
        }

        return stored;
    }

    // ── Idempotency ──────────────────────────────────────────────────────────────

    private async Task<string?> TryReplayAsync(
        Guid userId,
        string endpoint,
        string? key,
        string scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        EnsureValidIdempotencyKey(key);
        var hash = HashKey(scope);

        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == userId && r.Endpoint == endpoint && r.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.IdempotencyRecords.Add(IdempotencyRecord.Create(userId, endpoint, key, hash, "", clock.UtcNow, TimeSpan.FromDays(30)));
            try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
            catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                throw MediaPipelineException.Conflict("REQUEST_IN_PROGRESS", "A request with this key is already reserved. Retry with the same key.");
            }
            return null;
        }
        if (string.IsNullOrEmpty(existing.ResponseRef))
            throw MediaPipelineException.Conflict("REQUEST_IN_PROGRESS", "A request with this key is already reserved. Retry with the same key.");

        if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
        {
            throw MediaPipelineException.Conflict(
                "IDEMPOTENCY_KEY_REUSED", "That idempotency key was already used for a different request.");
        }

        logger.LogInformation("event=idempotent_replay endpoint={Endpoint}", endpoint);
        return existing.ResponseRef;
    }

    private async Task RecordIdempotencyAsync(
        Guid userId,
        string endpoint,
        string? key,
        string scope,
        string responseRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var reservation = await db.IdempotencyRecords.SingleAsync(r => r.UserId == userId && r.Endpoint == endpoint && r.Key == key, cancellationToken).ConfigureAwait(false);
        reservation.SetResponse(responseRef);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _ = scope;
    }

    private void SetReservationResponse(Guid userId, string endpoint, string? key, string responseRef)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        db.IdempotencyRecords.Local.Single(r => r.UserId == userId && r.Endpoint == endpoint && r.Key == key).SetResponse(responseRef);
    }

    private static void EnsureValidIdempotencyKey(string key)
    {
        if (key.Length > IdempotencyRecord.MaxKeyLength)
        {
            throw MediaPipelineException.Validation(
                "INVALID_IDEMPOTENCY_KEY", $"Idempotency-Key must be at most {IdempotencyRecord.MaxKeyLength} characters.");
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or ':'))
            {
                throw MediaPipelineException.Validation(
                    "INVALID_IDEMPOTENCY_KEY", "Idempotency-Key may contain only letters, digits, '-', '_' and ':'.");
            }
        }
    }

    private static string HashKey(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // ── Projection plumbing ──────────────────────────────────────────────────────

    private IQueryable<AssetVersion> VersionQuery() => db.AssetVersions.AsNoTracking();

    private System.Linq.Expressions.Expression<Func<AssetVersion, VersionRow>> ProjectVersion() => v => new VersionRow
    {
        Id = v.Id,
        RowVersion = v.RowVersion,
        PublicRef = v.PublicRef,
        VersionNumber = v.VersionNumber,
        LifecycleState = v.LifecycleState,
        ProcessingState = v.ProcessingState,
        ReviewState = v.ReviewState,
        OriginalFileName = v.OriginalFileName,
        ContentType = v.ContentType,
        ByteSize = v.ByteSize,
        Sha256 = v.Sha256,
        Width = v.Width,
        Height = v.Height,
        StateSequence = v.StateSequence,
        CreatedAt = v.CreatedAt,
        CreatedByRef = db.Users.Where(u => u.Id == v.CreatedByUserId).Select(u => u.PublicRef).Single(),
        CreatedByName = db.Users.Where(u => u.Id == v.CreatedByUserId).Select(u => u.DisplayName).Single(),
        Derivatives = db.AssetDerivatives
            .Where(d => d.AssetVersionId == v.Id)
            .Select(d => new DerivativeRow(d.Kind, d.Width, d.Height, d.ByteSize))
            .ToList(),
        Job = db.ProcessingJobs
            .Where(j => j.AssetVersionId == v.Id)
            .Select(j => new ProcessingJobView(
                j.PublicRef, j.State.ToString(), j.AttemptCount, j.MaxAttempts,
                j.FailureKind.ToString(), j.FailureCode, j.FailureMessage, j.StartedAt, j.CompletedAt))
            .SingleOrDefault(),
    };

    private static AssetVersionSummary MapVersion(VersionRow row) => new(
        row.PublicRef,
        row.VersionNumber,
        row.LifecycleState.ToString(),
        row.ProcessingState.ToString(),
        row.ReviewState.ToString(),
        row.OriginalFileName,
        row.ContentType,
        row.ByteSize,
        row.Sha256,
        row.Width,
        row.Height,
        row.StateSequence,
        row.CreatedByRef,
        row.CreatedByName,
        row.CreatedAt,
        row.Derivatives
            .Select(d => new DerivativeView(
                d.Kind.ToString(), d.Width, d.Height, d.ByteSize,
                $"/api/v1/versions/{row.PublicRef}/content?derivative={d.Kind.ToString().ToLowerInvariant()}"))
            .ToList(),
        row.Job, ConcurrencyToken.Format(row.RowVersion));

    internal sealed class VersionRow
    {
        public Guid Id { get; init; }
        public uint RowVersion { get; init; }
        public string PublicRef { get; init; } = string.Empty;
        public int VersionNumber { get; init; }
        public VersionLifecycleState LifecycleState { get; init; }
        public ProcessingState ProcessingState { get; init; }
        public ReviewState ReviewState { get; init; }
        public string OriginalFileName { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public long ByteSize { get; init; }
        public string Sha256 { get; init; } = string.Empty;
        public int? Width { get; init; }
        public int? Height { get; init; }
        public long StateSequence { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string CreatedByRef { get; init; } = string.Empty;
        public string CreatedByName { get; init; } = string.Empty;
        public List<DerivativeRow> Derivatives { get; init; } = [];
        public ProcessingJobView? Job { get; init; }
    }

    internal sealed record DerivativeRow(DerivativeKind Kind, int Width, int Height, long ByteSize);
}
