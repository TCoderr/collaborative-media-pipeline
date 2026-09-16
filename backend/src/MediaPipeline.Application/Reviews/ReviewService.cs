using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Contracts;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Application.Projects;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Projects;
using MediaPipeline.Domain.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediaPipeline.Application.Reviews;

/// <summary>
/// Review state transitions and comment threads.
/// <para>
/// Every transition is decided by the domain state machine and guarded by the version's
/// concurrency token, so two reviewers acting at the same moment produce one winner and one
/// explicit conflict - never a silently overwritten decision.
/// </para>
/// </summary>
public sealed class ReviewService(
    MediaPipelineDbContext db,
    ProjectAccessService access,
    IClock clock,
    MediaPipeline.Application.Observability.MediaPipelineMetrics metrics,
    ILogger<ReviewService> logger)
{
    public async Task<AssetVersionSummary> ChangeReviewStateAsync(
        Guid userId,
        string versionRef,
        ReviewState target,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var (project, versionId, assetId) = await access
            .RequireVersionAsync(userId, versionRef, ProjectCapability.ChangeReviewState, cancellationToken)
            .ConfigureAwait(false);

        var version = await db.AssetVersions.SingleAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedVersion))
            throw MediaPipelineException.Conflict("PRECONDITION_REQUIRED", "Read the version and send its ETag in If-Match.");
        ConcurrencyToken.Enforce(expectedVersion, version.RowVersion);

        db.ActorUserId = userId;
        var previous = version.ReviewState;
        if (target == previous) return await LoadVersionSummaryAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (target == ReviewState.Approved)
        {
            if (await db.ReviewComments.AnyAsync(c => c.AssetVersionId == versionId && c.ParentCommentId == null && c.ResolvedAt == null, cancellationToken).ConfigureAwait(false))
                throw MediaPipelineException.Rule("OPEN_REVIEW_THREADS", "Resolve open review threads before approval.");
            version.Approve(userId, clock.UtcNow);
        }
        else version.ChangeReviewState(target, clock.UtcNow);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw MediaPipelineException.Conflict(
                "CONCURRENT_MODIFICATION", "Another reviewer changed this version first. Reload and try again.");
        }

        if (target == ReviewState.Approved)
        {
            var completed = await db.ProcessingJobs.Where(j => j.AssetVersionId == versionId).Select(j => j.CompletedAt).SingleAsync(cancellationToken).ConfigureAwait(false);
            if (completed is not null) metrics.ApprovalLatency.Record((clock.UtcNow - completed.Value).TotalSeconds);
        }
        var assetRef = await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId).Select(a => a.PublicRef)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "event=review_state_changed project_ref={ProjectRef} version_ref={VersionRef} from={FromState} to={ToState}",
            project.ProjectRef, versionRef, previous, target);

        return await LoadVersionSummaryAsync(versionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommentView> AddCommentAsync(
        Guid userId,
        string versionRef,
        string body,
        CancellationToken cancellationToken, string? parentRef = null)
    {
        var (project, versionId, assetId) = await access
            .RequireVersionAsync(userId, versionRef, ProjectCapability.Comment, cancellationToken).ConfigureAwait(false);

        db.ActorUserId = userId;
        var version = await db.AssetVersions.SingleAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
        if (version.ReviewState != ReviewState.InReview || version.ProcessingState != ProcessingState.Succeeded || version.DeliveredAt != null)
            throw MediaPipelineException.Rule("REVIEW_NOT_OPEN", "Comments require an open review on ready media.");
        version.RecordReviewActivity(clock.UtcNow);
        Guid? parentId = null;
        if (parentRef is not null)
        {
            var parent = await db.ReviewComments.SingleOrDefaultAsync(c => c.PublicRef == parentRef && c.AssetVersionId == versionId, cancellationToken).ConfigureAwait(false)
                ?? throw MediaPipelineException.NotFound("THREAD_NOT_FOUND", "That review thread does not exist on this version.");
            if (parent.IsResolved || parent.ParentCommentId is not null)
                throw MediaPipelineException.Rule("THREAD_NOT_OPEN", "Replies require an open root thread.");
            parentId = parent.Id;
        }
        var comment = ReviewComment.Create(versionId, project.ProjectId, userId, body, clock.UtcNow, parentId);
        db.ReviewComments.Add(comment);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var author = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PublicRef, u.DisplayName })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var assetRef = await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId).Select(a => a.PublicRef)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var view = new CommentView(
            comment.PublicRef, comment.Body, author.PublicRef, author.DisplayName,
            false, null, comment.CreatedAt, null, comment.StateSequence, ConcurrencyToken.Format(comment.RowVersion), parentRef);

        logger.LogInformation(
            "event=comment_created project_ref={ProjectRef} version_ref={VersionRef} comment_ref={CommentRef}",
            project.ProjectRef, versionRef, comment.PublicRef);

        return view;
    }

    public async Task<Page<CommentView>> ListCommentsAsync(
        Guid userId,
        string versionRef,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var (_, versionId, _) = await access
            .RequireVersionAsync(userId, versionRef, ProjectCapability.ViewProject, cancellationToken).ConfigureAwait(false);

        var page = request.Normalised();
        var cursor = Cursor.Decode(page.Cursor);

        var query = db.ReviewComments.AsNoTracking().Where(c => c.AssetVersionId == versionId);
        if (cursor is not null)
        {
            query = query.Where(c => c.CreatedAt > cursor.Value.CreatedAt
                || (c.CreatedAt == cursor.Value.CreatedAt && c.Id.CompareTo(cursor.Value.Id) > 0));
        }

        // Comments read chronologically, so this page ascends while lists elsewhere descend.
        var rows = await query
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(page.Limit + 1)
            .Select(c => new
            {
                c.Id,
                c.PublicRef,
                c.Body,
                c.RowVersion,
                ParentRef = db.ReviewComments.Where(p => p.Id == c.ParentCommentId).Select(p => p.PublicRef).SingleOrDefault(),
                c.CreatedAt,
                c.ResolvedAt,
                c.StateSequence,
                AuthorRef = db.Users.Where(u => u.Id == c.AuthorUserId).Select(u => u.PublicRef).Single(),
                AuthorName = db.Users.Where(u => u.Id == c.AuthorUserId).Select(u => u.DisplayName).Single(),
                ResolvedByRef = c.ResolvedByUserId == null
                    ? null
                    : db.Users.Where(u => u.Id == c.ResolvedByUserId).Select(u => u.PublicRef).Single(),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit)
            .Select(r => new CommentView(
                r.PublicRef, r.Body, r.AuthorRef, r.AuthorName,
                r.ResolvedAt is not null, r.ResolvedByRef, r.CreatedAt, r.ResolvedAt, r.StateSequence, ConcurrencyToken.Format(r.RowVersion), r.ParentRef))
            .ToList();

        var next = hasMore ? Cursor.Encode(rows[page.Limit - 1].CreatedAt, rows[page.Limit - 1].Id) : null;
        return new Page<CommentView>(items, next, page.Limit);
    }

    public async Task<CommentView> SetCommentResolutionAsync(
        Guid userId,
        string commentRef,
        bool resolved,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!PublicRefGuard.IsComment(commentRef))
        {
            throw MediaPipelineException.NotFound("COMMENT_NOT_FOUND", "Comment does not exist.");
        }

        var comment = await db.ReviewComments.SingleOrDefaultAsync(c => c.PublicRef == commentRef, cancellationToken)
            .ConfigureAwait(false)
            ?? throw MediaPipelineException.NotFound("COMMENT_NOT_FOUND", "Comment does not exist.");

        // Authorise through the project, from the comment's own project id - never from
        // anything the caller supplied.
        var projectRef = await db.Projects.AsNoTracking()
            .Where(p => p.Id == comment.ProjectId).Select(p => p.PublicRef)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var project = await access.RequireAsync(userId, projectRef, ProjectCapability.ResolveComment, cancellationToken)
            .ConfigureAwait(false);

        var version = await db.AssetVersions.SingleAsync(v => v.Id == comment.AssetVersionId, cancellationToken).ConfigureAwait(false);
        if (version.ReviewState != ReviewState.InReview || version.DeliveredAt is not null || comment.ParentCommentId is not null)
            throw MediaPipelineException.Rule("REVIEW_NOT_OPEN", "Resolution changes require an open review root thread.");
        version.RecordReviewActivity(clock.UtcNow);
        db.ActorUserId = userId;
        if (comment.AuthorUserId != userId && project.Role != ProjectRole.Owner)
            throw MediaPipelineException.Forbidden("COMMENT_OWNERSHIP", "Only the thread author or project owner may resolve it.");
        ConcurrencyToken.Enforce(expectedVersion, comment.RowVersion);

        if (resolved)
        {
            if (comment.IsResolved)
            {
                throw MediaPipelineException.Conflict("COMMENT_ALREADY_RESOLVED", "That comment is already resolved.");
            }

            comment.Resolve(userId, clock.UtcNow);
        }
        else
        {
            if (!comment.IsResolved)
            {
                throw MediaPipelineException.Conflict("COMMENT_NOT_RESOLVED", "That comment is already open.");
            }

            comment.Reopen(clock.UtcNow);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw MediaPipelineException.Conflict(
                "CONCURRENT_MODIFICATION", "Someone else changed this comment first. Reload and try again.");
        }

        var author = await db.Users.AsNoTracking()
            .Where(u => u.Id == comment.AuthorUserId)
            .Select(u => new { u.PublicRef, u.DisplayName })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var resolverRef = comment.ResolvedByUserId is null
            ? null
            : await db.Users.AsNoTracking().Where(u => u.Id == comment.ResolvedByUserId)
                .Select(u => u.PublicRef).SingleAsync(cancellationToken).ConfigureAwait(false);

        var versionRef = await db.AssetVersions.AsNoTracking()
            .Where(v => v.Id == comment.AssetVersionId).Select(v => v.PublicRef)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var view = new CommentView(
            comment.PublicRef, comment.Body, author.PublicRef, author.DisplayName,
            comment.IsResolved, resolverRef, comment.CreatedAt, comment.ResolvedAt, comment.StateSequence, ConcurrencyToken.Format(comment.RowVersion));

        return view;
    }

    private async Task<AssetVersionSummary> LoadVersionSummaryAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var v = await db.AssetVersions.AsNoTracking()
            .Where(x => x.Id == versionId)
            .Select(x => new
            {
                x.PublicRef,
                x.RowVersion,
                x.VersionNumber,
                x.LifecycleState,
                x.ProcessingState,
                x.ReviewState,
                x.OriginalFileName,
                x.ContentType,
                x.ByteSize,
                x.Sha256,
                x.Width,
                x.Height,
                x.StateSequence,
                x.CreatedAt,
                CreatedByRef = db.Users.Where(u => u.Id == x.CreatedByUserId).Select(u => u.PublicRef).Single(),
                CreatedByName = db.Users.Where(u => u.Id == x.CreatedByUserId).Select(u => u.DisplayName).Single(),
                Derivatives = db.AssetDerivatives.Where(d => d.AssetVersionId == x.Id)
                    .Select(d => new { d.Kind, d.Width, d.Height, d.ByteSize }).ToList(),
                Job = db.ProcessingJobs.Where(j => j.AssetVersionId == x.Id)
                    .Select(j => new ProcessingJobView(
                        j.PublicRef, j.State.ToString(), j.AttemptCount, j.MaxAttempts,
                        j.FailureKind.ToString(), j.FailureCode, j.FailureMessage, j.StartedAt, j.CompletedAt))
                    .SingleOrDefault(),
            })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        return new AssetVersionSummary(
            v.PublicRef, v.VersionNumber, v.LifecycleState.ToString(), v.ProcessingState.ToString(),
            v.ReviewState.ToString(), v.OriginalFileName, v.ContentType, v.ByteSize, v.Sha256,
            v.Width, v.Height, v.StateSequence, v.CreatedByRef, v.CreatedByName, v.CreatedAt,
            v.Derivatives.Select(d => new DerivativeView(
                d.Kind.ToString(), d.Width, d.Height, d.ByteSize,
                $"/api/v1/versions/{v.PublicRef}/content?derivative={d.Kind.ToString().ToLowerInvariant()}")).ToList(),
            v.Job, ConcurrencyToken.Format(v.RowVersion));
    }
}
