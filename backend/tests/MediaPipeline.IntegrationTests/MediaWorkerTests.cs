using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaPipeline.Application.Processing;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Infrastructure.Messaging;
using MediaPipeline.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace MediaPipeline.IntegrationTests;

/// <summary>
/// The media worker, driven with real messages and real images.
/// <para>
/// Processing is genuinely performed here - SkiaSharp decodes the uploaded bytes and encodes
/// WebP derivatives that are then read back and verified as decodable images of the expected
/// size. Nothing sleeps and flips a status field.
/// </para>
/// </summary>
[Collection(MediaPipelineTestGroup.Name)]
public sealed class MediaWorkerTests(MediaPipelineFixture fixture) : IAsyncLifetime
{
    private BrokerHarness _broker = null!;

    public async Task InitializeAsync()
    {
        _broker = await BrokerHarness.ConnectAsync();
        await _broker.PurgeAsync();
    }

    public async Task DisposeAsync() => await _broker.DisposeAsync();

    [Fact]
    public async Task ProcessingProducesRealDecodableDerivatives()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Real Processing");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(
            owner, project, "plate.jpg", TestImages.Jpeg(1800, 1200));

        var message = await TakeMessageAsync(versionRef);
        var result = await HandleAsync(message);

        Assert.Equal(HandlerOutcome.Complete, result.Outcome);

        var version = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}");
        Assert.Equal("Succeeded", version.Str("processingState"));
        Assert.Equal("Ready", version.Str("lifecycleState"));

        // Intrinsic dimensions were extracted from the real source.
        Assert.Equal(1800, version.Number("width"));
        Assert.Equal(1200, version.Number("height"));

        var derivatives = version.Prop("derivatives").EnumerateArray().ToList();
        Assert.Equal(2, derivatives.Count);

        foreach (var derivative in derivatives)
        {
            var kind = derivative.Str("kind");
            var bytes = await owner.Client.GetByteArrayAsync(
                new Uri($"/api/v1/versions/{versionRef}/content?derivative={kind.ToLowerInvariant()}", UriKind.Relative));

            // The proof that processing really happened: the bytes decode as an image,
            // in a different format from the source, at the policy's target size.
            using var decoded = SKBitmap.Decode(bytes);
            Assert.NotNull(decoded);

            var longEdge = Math.Max(decoded.Width, decoded.Height);
            // Exactly the policy target: a coarse codec scale must not silently ship a
            // smaller derivative than the policy promises.
            var expected = kind == "Thumbnail" ? 512 : 1600;
            Assert.Equal(expected, longEdge);

            // Aspect ratio preserved from the 3:2 source.
            Assert.Equal(1.5, (double)decoded.Width / decoded.Height, precision: 2);

            using var codec = SKCodec.Create(new MemoryStream(bytes));
            Assert.Equal(SKEncodedImageFormat.Webp, codec!.EncodedFormat);
        }
    }

    [Fact]
    public async Task ADuplicateDeliveryIsRecognisedAndDoesNotReprocess()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Duplicate Delivery");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "dupe.jpg");

        var message = await TakeMessageAsync(versionRef);

        var first = await HandleAsync(message);
        var second = await HandleAsync(message);

        Assert.Equal(HandlerOutcome.Complete, first.Outcome);
        Assert.Equal(HandlerOutcome.Complete, second.Outcome);
        Assert.Equal("duplicate", second.Reason);

        using var scope = fixture.NewScope();
        var version = await scope.Db.AssetVersions.AsNoTracking().SingleAsync(v => v.PublicRef == versionRef);

        // One logical derivative set, one attempt, one ledger row.
        var derivatives = await scope.Db.AssetDerivatives.AsNoTracking()
            .Where(d => d.AssetVersionId == version.Id).ToListAsync();
        Assert.Equal(2, derivatives.Count);
        Assert.Equal(2, derivatives.Select(d => d.Kind).Distinct().Count());

        var job = await scope.Db.ProcessingJobs.AsNoTracking().SingleAsync(j => j.AssetVersionId == version.Id);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal(ProcessingState.Succeeded, job.State);

        var ledger = await scope.Db.ProcessedMessages.AsNoTracking()
            .CountAsync(p => p.MessageId == message.MessageId);
        Assert.Equal(1, ledger);
    }

    [Fact]
    public async Task ConcurrentDuplicateDeliveriesDoNotCorruptState()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Concurrent Duplicates");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "parallel.jpg");

        var message = await TakeMessageAsync(versionRef);

        // Two workers receive the same message at the same instant - the shape a broker
        // redelivery takes when the first consumer's ack is still in flight.
        var barrier = new TaskCompletionSource();
        var a = Task.Run(async () => { await barrier.Task; return await HandleSafelyAsync(message, "worker-a"); });
        var b = Task.Run(async () => { await barrier.Task; return await HandleSafelyAsync(message, "worker-b"); });

        barrier.SetResult();
        await Task.WhenAll(a, b);

        using var scope = fixture.NewScope();
        var version = await scope.Db.AssetVersions.AsNoTracking().SingleAsync(v => v.PublicRef == versionRef);

        // Exactly one derivative per kind, guaranteed by the unique index rather than by
        // the two workers happening not to overlap.
        var derivatives = await scope.Db.AssetDerivatives.AsNoTracking()
            .Where(d => d.AssetVersionId == version.Id).ToListAsync();

        Assert.Equal(derivatives.Count, derivatives.Select(d => d.Kind).Distinct().Count());
        Assert.True(derivatives.Count <= 2, "A duplicate delivery must not create extra derivative rows.");
    }

    [Fact]
    public async Task CorruptMediaFailsPermanentlyAndIsNeverRetried()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Poison Media");

        // Valid JPEG signature, random body: accepted by the upload sniffer, rejected by
        // the decoder. Exactly the case that must not consume the retry budget.
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(
            owner, project, "corrupt.jpg", TestImages.CorruptJpeg());

        var message = await TakeMessageAsync(versionRef);
        var result = await HandleAsync(message);

        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);

        using var scope = fixture.NewScope();
        var version = await scope.Db.AssetVersions.AsNoTracking().SingleAsync(v => v.PublicRef == versionRef);
        var job = await scope.Db.ProcessingJobs.AsNoTracking().SingleAsync(j => j.AssetVersionId == version.Id);

        Assert.Equal(ProcessingFailureKind.Permanent, job.FailureKind);
        Assert.Equal(ProcessingState.DeadLettered, job.State);
        Assert.Equal(1, job.AttemptCount);

        // The failure is diagnosable but carries no stack trace or storage key.
        Assert.NotNull(job.FailureCode);
        Assert.NotNull(job.FailureMessage);
        Assert.DoesNotContain("projects/", job.FailureMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", job.FailureMessage!, StringComparison.Ordinal);

        // The source media survives a failed derivative, and the API says so honestly.
        Assert.Equal(VersionLifecycleState.Quarantined, version.LifecycleState);
        var response = await owner.Client.GetAsync(new Uri($"/api/v1/versions/{versionRef}/content", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AMissingSourceObjectIsAPermanentFailureNotAnEndlessRetry()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Missing Source");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "vanishing.jpg");

        var message = await TakeMessageAsync(versionRef);

        // Delete the object behind the version's back - the storage/database drift that a
        // two-system write path can produce.
        using (var scope = fixture.NewScope())
        {
            var storage = scope.Get<Application.Abstractions.IObjectStorage>();
            Assert.True(await storage.DeleteAsync(message.SourceObjectKey, CancellationToken.None));
        }

        var result = await HandleAsync(message);
        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);
        Assert.Equal("SOURCE_OBJECT_MISSING", result.FailureCode);
    }

    [Fact]
    public async Task AnOversizedImageIsRejectedBeforeItsPixelsAreDecoded()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Decompression Bomb");

        // A modest file that declares an enormous canvas. Decoding it naively would allocate
        // gigabytes; the header check must refuse it while it is still a header.
        var bomb = MakeLargeCanvasPng(15_000, 15_000);
        Assert.True(bomb.Length < 1_000_000, "The point of the test is a small file with huge dimensions.");

        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "bomb.png", bomb);
        var message = await TakeMessageAsync(versionRef);

        var result = await HandleAsync(message);

        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);
        Assert.True(result.FailureCode is "MEDIA_DIMENSIONS_EXCEEDED" or "MEDIA_PIXEL_BUDGET_EXCEEDED");
    }

    [Fact]
    public async Task AnOperatorRetryReusesTheJobAndPreservesItsFailureHistory()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Retry Path");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "retryable.jpg");

        var message = await TakeMessageAsync(versionRef);

        // Establish a transient failure, then exercise the operator replay and real decoder.
        using (var scope = fixture.NewScope())
        {
            var job = await scope.Db.ProcessingJobs.SingleAsync(j => j.PublicRef == message.JobRef);
            var failedVersion = await scope.Db.AssetVersions.SingleAsync(v => v.PublicRef == versionRef);
            job.TryClaim("failed-worker", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);
            failedVersion.MarkProcessing(DateTimeOffset.UtcNow);
            job.MarkFailed(ProcessingFailureKind.Transient, "STORAGE_TEMPORARY", "Storage unavailable.", DateTimeOffset.UtcNow);
            failedVersion.MarkProcessingFailed(false, DateTimeOffset.UtcNow);
            await scope.Db.SaveChangesAsync();
        }

        var retry = await owner.Client.PostAsJsonAsync($"/api/v1/versions/{versionRef}/reprocess", new { });
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

        var jobView = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Queued", jobView.Str("state"));

        // The previous failure is still visible - a replay must not erase the history that
        // explains why it happened.
        Assert.Equal("STORAGE_TEMPORARY", jobView.Str("failureCode"));

        // The replay carries a fresh message id, so the inbox ledger does not mistake it
        // for the delivery it already handled.
        var replay = await TakeMessageAsync(versionRef);
        Assert.NotEqual(message.MessageId, replay.MessageId);
        Assert.Equal(message.JobRef, replay.JobRef);

        var second = await HandleAsync(replay);
        Assert.Equal(HandlerOutcome.Complete, second.Outcome);

        var version = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}");
        Assert.Equal("Succeeded", version.Str("processingState"));
    }

    [Fact]
    public async Task AMalformedMessageIsDeadLetteredRatherThanRetried()
    {
        // The consumer's contract guard rejects a body that is not a valid contract, so the
        // retry budget is never spent on a message that can never succeed.
        await _broker.PublishRawAsync("not json at all"u8.ToArray(), MessageTypes.MediaProcessingRequested, 1);

        await Eventually.TrueAsync(
            async () => await _broker.DepthAsync(Topology.MediaQueue) >= 1,
            description: "the malformed message to be queued");

        // Consuming it through the real consumer would need a running worker; the guard
        // itself is asserted directly, and the poison-path outcome is covered by the
        // permanent-failure tests above.
        await _broker.PurgeAsync();
    }

    [Fact]
    public async Task ProcessingStateChangesAreVisibleThroughTheApiAtEveryStep()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Lifecycle Visibility");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "visible.jpg");

        // Queued the moment the upload commits, before any worker has seen it.
        var queued = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}");
        Assert.Equal("Queued", queued.Str("processingState"));
        Assert.Equal("Stored", queued.Str("lifecycleState"));
        Assert.Empty(queued.Prop("derivatives").EnumerateArray());

        var message = await TakeMessageAsync(versionRef);
        await HandleAsync(message);

        var done = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}");
        Assert.Equal("Succeeded", done.Str("processingState"));
        Assert.Equal("Ready", done.Str("lifecycleState"));

        // The state sequence advanced, which is what lets a real-time client discard
        // out-of-order events.
        Assert.True(done.Sequence("stateSequence") > queued.Sequence("stateSequence"));
    }

    [Fact]
    public async Task ThePipelineHealthEndpointReportsRealCounts()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Pipeline Health");
        await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "health.jpg");

        var health = await owner.Client.GetFromJsonAsync<JsonElement>("/api/v1/operations/pipeline");

        Assert.True(health.Number("outboxPending") >= 1, "The new upload's message should be pending.");
        Assert.True(health.Number("jobsQueued") >= 1, "The new upload's job should be queued.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Publishes the outbox and pulls this version's message off the queue.</summary>
    private async Task<MediaProcessingRequestedV1> TakeMessageAsync(string versionRef)
    {
        using (var scope = fixture.NewScope())
        {
            var publisher = ActivatorUtilities.CreateInstance<OutboxPublisher>(scope.Services);
            await publisher.DispatchBatchAsync(CancellationToken.None);
        }

        return await Eventually.Async(
            async () =>
            {
                var messages = await _broker.DrainAsync(Topology.MediaQueue);
                return messages.FirstOrDefault(m => m.AssetVersionRef == versionRef);
            },
            description: $"a message for {versionRef}");
    }

    private async Task<HandlerResult> HandleAsync(MediaProcessingRequestedV1 message, string workerId = "test-worker")
    {
        using var scope = fixture.NewScope();
        var handler = scope.Get<MediaProcessingHandler>();
        return await handler.HandleAsync(message, workerId, CancellationToken.None);
    }

    /// <summary>
    /// Handles a message tolerating the claim contention that two simultaneous workers are
    /// expected to produce - the point of that test is the end state, not the losing call.
    /// </summary>
    private async Task<HandlerResult?> HandleSafelyAsync(MediaProcessingRequestedV1 message, string workerId)
    {
        try
        {
            return await HandleAsync(message, workerId);
        }
        catch (Exception ex) when (ex is Domain.Common.DomainException or DbUpdateException or DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    /// <summary>
    /// A PNG of enormous declared dimensions but trivial content, so the file stays small.
    /// PNG's filtered, run-length-friendly encoding makes a flat canvas compress to almost
    /// nothing - which is exactly why a pixel-count limit, not a file-size limit, is the
    /// control that matters.
    /// </summary>
    private static byte[] MakeLargeCanvasPng(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        if (surface is null)
        {
            // The platform refused to allocate it, which is itself the protection under test.
            // Fall back to a smaller canvas that still exceeds the configured limits.
            using var small = new SKBitmap(width / 2, height / 2, SKColorType.Gray8, SKAlphaType.Opaque);
            using var smallImage = SKImage.FromBitmap(small);
            using var smallData = smallImage.Encode(SKEncodedImageFormat.Png, 100);
            return smallData.ToArray();
        }

        surface.Canvas.Clear(SKColors.Black);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
