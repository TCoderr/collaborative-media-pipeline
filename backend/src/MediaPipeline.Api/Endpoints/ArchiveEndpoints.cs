using System.Security.Claims;
using System.Text.Json;
using MediaPipeline.Api.Http;
using MediaPipeline.Application.Contracts;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Application.Projects;
using MediaPipeline.Application.Reviews;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace MediaPipeline.Api.Endpoints;

public static class ArchiveEndpoints
{
    public static void MapArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization();
        group.MapGet("/archive", async (ClaimsPrincipal principal, HttpRequest request, MediaPipelineDbContext db, CancellationToken ct) =>
        {
            var userId = principal.UserId();
            var page = RequestHelpers.PageFrom(request).Normalised();
            var cursor = Cursor.Decode(page.Cursor);
            var query = db.AssetVersions.AsNoTracking().Where(v =>
                db.ProjectMembers.Any(m => m.ProjectId == v.ProjectId && m.UserId == userId) &&
                db.Assets.Any(a => a.Id == v.AssetId && a.Status == AssetStatus.Active));
            if (cursor is not null)
                query = query.Where(v => v.CreatedAt < cursor.Value.CreatedAt ||
                    (v.CreatedAt == cursor.Value.CreatedAt && v.Id.CompareTo(cursor.Value.Id) < 0));
            var rows = await query.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).Take(page.Limit + 1)
                .Select(v => new
                {
                    v.Id, v.PublicRef, v.VersionNumber, v.CreatedAt, v.UpdatedAt, v.Width, v.Height, v.StateSequence,
                    processing = v.ProcessingState.ToString(), lifecycle = v.LifecycleState.ToString(), review = v.ReviewState.ToString(),
                    approved = v.ApprovedAt != null, delivered = v.DeliveredAt != null,
                    project = db.Projects.Where(p => p.Id == v.ProjectId).Select(p => p.Name).Single(),
                    projectRef = db.Projects.Where(p => p.Id == v.ProjectId).Select(p => p.PublicRef).Single(),
                    asset = db.Assets.Where(a => a.Id == v.AssetId).Select(a => a.Title).Single(),
                    thumbnail = v.ProcessingState == ProcessingState.Succeeded && db.AssetDerivatives.Any(d => d.AssetVersionId == v.Id && d.Kind == DerivativeKind.Thumbnail)
                        ? "/api/v1/versions/" + v.PublicRef + "/content?derivative=thumbnail" : null,
                }).ToListAsync(ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                items = rows.Take(page.Limit).Select(v => new { id = v.PublicRef, version = v.VersionNumber, v.UpdatedAt, v.Width, v.Height,
                    v.StateSequence, v.processing, v.lifecycle, v.review, approval = v.approved ? "approved" : "pending",
                    delivery = v.delivered ? "delivered" : "pending", v.project, v.projectRef, v.asset, v.thumbnail }),
                nextCursor = rows.Count > page.Limit ? Cursor.Encode(rows[page.Limit - 1].CreatedAt, rows[page.Limit - 1].Id) : null,
            });
        });

        group.MapPost("/versions/{versionRef}/approvals", async (string versionRef, ClaimsPrincipal user, HttpRequest request, ReviewService reviews, CancellationToken ct) =>
            Results.Ok(await reviews.ChangeReviewStateAsync(user.UserId(), versionRef, ReviewState.Approved, RequiredToken(request), ct).ConfigureAwait(false)));
        group.MapPost("/versions/{versionRef}/deliveries", async (string versionRef, ClaimsPrincipal user, HttpRequest request,
            MediaPipelineDbContext db, ProjectAccessService access, CancellationToken ct) =>
        {
            var (_, id, _) = await access.RequireVersionAsync(user.UserId(), versionRef, ProjectCapability.ManageProject, ct).ConfigureAwait(false);
            var v = await db.AssetVersions.SingleAsync(v => v.Id == id, ct).ConfigureAwait(false);
            // Delivery records a human-confirmed handoff; it does not pretend an external transfer occurred.
            ConcurrencyToken.Enforce(RequiredToken(request), v.RowVersion);
            db.ActorUserId = user.UserId();
            v.Deliver(user.UserId(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { versionRef, state = "Delivered", v.DeliveredAt });
        });
        group.MapGet("/versions/{versionRef}/history", async (string versionRef, ClaimsPrincipal user, HttpRequest request,
            MediaPipelineDbContext db, ProjectAccessService access, CancellationToken ct) =>
        {
            await access.RequireVersionAsync(user.UserId(), versionRef, ProjectCapability.ViewProject, ct).ConfigureAwait(false);
            return Results.Ok(await ReadHistoryAsync(db, versionRef, request, null, ct).ConfigureAwait(false));
        });
        group.MapGet("/versions/{versionRef}/approvals", async (string versionRef, ClaimsPrincipal user, HttpRequest request,
            MediaPipelineDbContext db, ProjectAccessService access, CancellationToken ct) =>
        {
            await access.RequireVersionAsync(user.UserId(), versionRef, ProjectCapability.ViewProject, ct).ConfigureAwait(false);
            return Results.Ok(await ReadHistoryAsync(db, versionRef, request, "approval.granted", ct).ConfigureAwait(false));
        });
        group.MapGet("/versions/{versionRef}/deliveries", async (string versionRef, ClaimsPrincipal user, HttpRequest request,
            MediaPipelineDbContext db, ProjectAccessService access, CancellationToken ct) =>
        {
            await access.RequireVersionAsync(user.UserId(), versionRef, ProjectCapability.ViewProject, ct).ConfigureAwait(false);
            return Results.Ok(await ReadHistoryAsync(db, versionRef, request, "delivery.completed", ct).ConfigureAwait(false));
        });
        group.MapGet("/processing-jobs/{jobRef}/history", async (string jobRef, ClaimsPrincipal user, HttpRequest request,
            MediaPipelineDbContext db, MediaPipeline.Application.Processing.ProcessingOperationsService jobs, CancellationToken ct) =>
        {
            await jobs.GetJobAsync(user.UserId(), jobRef, ct).ConfigureAwait(false);
            return Results.Ok(await ReadHistoryAsync(db, jobRef, request, null, ct).ConfigureAwait(false));
        });
    }

    public static string RequiredToken(HttpRequest request) => RequestHelpers.IfMatch(request) ??
        throw MediaPipeline.Application.Abstractions.MediaPipelineException.Conflict("PRECONDITION_REQUIRED", "Read the resource and send its ETag in If-Match.");

    private static async Task<object> ReadHistoryAsync(MediaPipelineDbContext db, string subject, HttpRequest request, string? eventType, CancellationToken ct)
    {
        var page = RequestHelpers.PageFrom(request).Normalised();
        var cursor = Cursor.Decode(page.Cursor);
        var query = db.StateHistory.AsNoTracking().Where(h => h.SubjectRef == subject && (eventType == null || h.EventType == eventType));
        if (cursor is not null) query = query.Where(h => h.OccurredAt < cursor.Value.CreatedAt ||
            (h.OccurredAt == cursor.Value.CreatedAt && h.Id.CompareTo(cursor.Value.Id) < 0));
        var rows = await query.OrderByDescending(h => h.OccurredAt).ThenByDescending(h => h.Id).Take(page.Limit + 1)
            .Select(h => new { h.Id, h.EventType, h.Sequence, h.Payload, h.OccurredAt,
                actorRef = db.Users.Where(u => u.Id == h.ActorUserId).Select(u => u.PublicRef).SingleOrDefault() })
            .ToListAsync(ct).ConfigureAwait(false);
        return new { items = rows.Take(page.Limit).Select(h => new { h.EventType, h.Sequence, h.OccurredAt, h.actorRef, payload = JsonSerializer.Deserialize<JsonElement>(h.Payload) }),
            nextCursor = rows.Count > page.Limit ? Cursor.Encode(rows[page.Limit - 1].OccurredAt, rows[page.Limit - 1].Id) : null };
    }
}
