using System.Security.Claims;
using MediaPipeline.Api.Http;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Assets;
using MediaPipeline.Application.Media;
using MediaPipeline.Application.Processing;
using MediaPipeline.Application.Reviews;
using MediaPipeline.Domain.Assets;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace MediaPipeline.Api.Endpoints;

/// <summary>
/// Asset, version, review and media routes.
/// <para>
/// Uploads are read straight from the multipart section into the storage provider - the
/// request body is never buffered to disk or memory first, so a 25 MB image costs kilobytes
/// of working set and an oversized body is cut off mid-transfer.
/// </para>
/// </summary>
public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        MapUploadRoutes(app);
        MapAssetReadRoutes(app);
        MapVersionRoutes(app);
    }

    private static void MapUploadRoutes(IEndpointRouteBuilder app)
    {
        var uploads = app.MapGroup("/api/v1").WithTags("Uploads")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Upload);

        uploads.MapPost("/projects/{projectRef}/assets", async (
            string projectRef,
            HttpRequest request,
            ClaimsPrincipal principal,
            AssetService assets,
            IOptions<MediaPolicyOptions> policy,
            CancellationToken cancellationToken) =>
        {
            var (file, fields) = await MultipartUpload.OpenAsync(request, policy.Value, cancellationToken)
                .ConfigureAwait(false);

            var title = fields.GetValueOrDefault("title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(file.FileName);
            }

            var detail = await assets.CreateAssetAsync(
                principal.UserId(),
                projectRef,
                title ?? "Untitled",
                new UploadRequest(file.Content, file.FileName, file.ContentType, RequestHelpers.IdempotencyKey(request)),
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/assets/{detail.Ref}", detail);
        })
        .WithSummary("Creates an asset from an uploaded image and stores it as version 1.")
        .DisableAntiforgery(); // multipart uploads carry the CSRF token in a header instead

        uploads.MapPost("/assets/{assetRef}/versions", async (
            string assetRef,
            HttpRequest request,
            ClaimsPrincipal principal,
            AssetService assets,
            IOptions<MediaPolicyOptions> policy,
            CancellationToken cancellationToken) =>
        {
            var (file, _) = await MultipartUpload.OpenAsync(request, policy.Value, cancellationToken).ConfigureAwait(false);

            var version = await assets.CreateVersionAsync(
                principal.UserId(),
                assetRef,
                new UploadRequest(file.Content, file.FileName, file.ContentType, RequestHelpers.IdempotencyKey(request)),
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/versions/{version.Ref}", version);
        })
        .WithSummary("Adds the next immutable version of an asset.")
        .DisableAntiforgery();
    }

    private static void MapAssetReadRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/assets").WithTags("Assets").RequireAuthorization();

        group.MapGet("/{assetRef}", async (
            string assetRef,
            ClaimsPrincipal principal,
            AssetService assets,
            CancellationToken cancellationToken) =>
            Results.Ok(await assets.GetAssetDetailAsync(principal.UserId(), assetRef, cancellationToken).ConfigureAwait(false)))
        .WithSummary("Returns an asset with its recent version history.");

        group.MapGet("/{assetRef}/versions", async (
            string assetRef,
            ClaimsPrincipal principal,
            AssetService assets,
            HttpRequest request,
            CancellationToken cancellationToken) =>
            Results.Ok(await assets.ListVersionsAsync(
                principal.UserId(), assetRef, RequestHelpers.PageFrom(request), cancellationToken).ConfigureAwait(false)))
        .WithSummary("Pages through an asset's full version history.");
    }

    private static void MapVersionRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/versions").WithTags("Versions").RequireAuthorization();

        group.MapGet("/{versionRef}", async (string versionRef, ClaimsPrincipal principal, AssetService assets,
            HttpResponse response, CancellationToken cancellationToken) =>
        {
            var version = await assets.GetVersionAsync(principal.UserId(), versionRef, cancellationToken).ConfigureAwait(false);
            response.Headers.ETag = "\"" + version.ETag + "\"";
            return Results.Ok(version);
        });

        group.MapGet("/{versionRef}/content", async (
            string versionRef,
            string? derivative,
            ClaimsPrincipal principal,
            AssetService assets,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            DerivativeKind? kind = null;
            if (!string.IsNullOrWhiteSpace(derivative))
            {
                kind = RequestHelpers.ParseEnum<DerivativeKind>(derivative, "INVALID_DERIVATIVE_KIND");
            }

            var stored = await assets.OpenContentAsync(principal.UserId(), versionRef, kind, cancellationToken)
                .ConfigureAwait(false);

            // Private media: never cached by a shared proxy, and never framed.
            context.Response.Headers.CacheControl = "private, max-age=300";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentDisposition = kind is null ? "attachment" : "inline";

            return Results.Stream(stored.Content, stored.ContentType, enableRangeProcessing: true);
        })
        .WithSummary("Streams a version's source media or one of its derivatives.");

        group.MapPost("/{versionRef}/review", async (
            string versionRef,
            ChangeReviewStateRequest body,
            ClaimsPrincipal principal,
            ReviewService reviews,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            body.Validated();
            var target = RequestHelpers.ParseEnum<ReviewState>(body.ReviewState, "INVALID_REVIEW_STATE");
            var version = await reviews.ChangeReviewStateAsync(
                principal.UserId(), versionRef, target, RequestHelpers.IfMatch(request), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(version);
        })
        .WithSummary("Moves a version through the review state machine.");

        group.MapGet("/{versionRef}/comments", async (
            string versionRef,
            ClaimsPrincipal principal,
            ReviewService reviews,
            HttpRequest request,
            CancellationToken cancellationToken) =>
            Results.Ok(await reviews.ListCommentsAsync(
                principal.UserId(), versionRef, RequestHelpers.PageFrom(request), cancellationToken).ConfigureAwait(false)))
        .WithSummary("Lists review comments oldest-first.");

        group.MapPost("/{versionRef}/comments", async (
            string versionRef,
            CreateCommentRequest body,
            ClaimsPrincipal principal,
            ReviewService reviews,
            CancellationToken cancellationToken) =>
        {
            body.Validated();
            var comment = await reviews.AddCommentAsync(principal.UserId(), versionRef, body.Body, cancellationToken, body.ParentRef)
                .ConfigureAwait(false);
            return Results.Created($"/api/v1/comments/{comment.Ref}", comment);
        })
        .WithSummary("Adds a plain-text review comment.");

        group.MapPost("/{versionRef}/reprocess", async (
            string versionRef,
            ClaimsPrincipal principal,
            ProcessingOperationsService processing,
            CancellationToken cancellationToken) =>
            Results.Accepted(
                $"/api/v1/versions/{versionRef}",
                await processing.RetryAsync(principal.UserId(), versionRef, cancellationToken).ConfigureAwait(false)))
        .WithSummary("Re-queues derivative generation for a failed version.");

        var comments = app.MapGroup("/api/v1/comments").WithTags("Comments").RequireAuthorization();

        comments.MapPost("/{commentRef}/resolution", async (
            string commentRef,
            ResolveCommentRequest body,
            ClaimsPrincipal principal,
            ReviewService reviews,
            HttpRequest request,
            CancellationToken cancellationToken) =>
            Results.Ok(await reviews.SetCommentResolutionAsync(
                principal.UserId(), commentRef, body.Resolved, RequestHelpers.IfMatch(request), cancellationToken)
                .ConfigureAwait(false)))
        .WithSummary("Resolves or reopens a review comment.");

        var jobs = app.MapGroup("/api/v1/processing-jobs").WithTags("Processing").RequireAuthorization();

        jobs.MapGet("/{jobRef}", async (
            string jobRef,
            ClaimsPrincipal principal,
            ProcessingOperationsService processing,
            CancellationToken cancellationToken) =>
            Results.Ok(await processing.GetJobAsync(principal.UserId(), jobRef, cancellationToken).ConfigureAwait(false)))
        .WithSummary("Returns a processing job's state and failure history.");
    }
}

/// <summary>One uploaded part: an open stream plus the client's (untrusted) metadata.</summary>
public sealed record UploadedFile(Stream Content, string FileName, string? ContentType);

/// <summary>
/// Streams the first file part out of a multipart body without buffering it.
/// <para>
/// ASP.NET Core's <c>IFormFile</c> binding would spool the whole request to a temp file
/// first; reading the multipart sections directly keeps the upload a single pass from socket
/// to object storage.
/// </para>
/// </summary>
public static class MultipartUpload
{
    private const int MaxFieldLength = 1024;
    private const int MaxFields = 10;

    public static async Task<(UploadedFile File, Dictionary<string, string> Fields)> OpenAsync(
        HttpRequest request,
        MediaPolicyOptions policy,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw MediaPipelineException.Validation("EXPECTED_MULTIPART", "This endpoint expects a multipart/form-data body.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(
            mediaType.Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw MediaPipelineException.Validation("EXPECTED_MULTIPART", "The multipart boundary is missing.");
        }

        var reader = new MultipartReader(boundary, request.Body)
        {
            BodyLengthLimit = policy.MaxUploadBytes + (64 * 1024),
        };

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false);
             section is not null;
             section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!ContentDispositionHeaderValue.TryParse(
                    section.ContentDisposition, out var disposition))
            {
                continue;
            }

            if (disposition.IsFileDisposition())
            {
                // Return with the section still open: the caller streams it onward.
                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileName).Value ?? "upload";

                return (new UploadedFile(section.Body, fileName, section.ContentType), fields);
            }

            if (disposition.IsFormDisposition() && fields.Count < MaxFields)
            {
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty;

                using var text = new StreamReader(section.Body, System.Text.Encoding.UTF8);
                var buffer = new char[MaxFieldLength];
                var read = await text.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
                fields[name] = new string(buffer, 0, read);
            }
        }

        throw MediaPipelineException.Validation("NO_FILE_PART", "The request did not contain a file part.");
    }
}
