using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaPipeline.Application.Processing;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Infrastructure.Messaging;
using MediaPipeline.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MediaPipeline.IntegrationTests;

[Collection(MediaPipelineTestGroup.Name)]
public sealed class LifecycleSecurityTests(MediaPipelineFixture fixture)
{
    [Fact]
    public async Task QuarantinedUploadsCannotBeDownloadedReviewedOrRetried()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Quarantine");
        var (_, version) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "invalid.jpg", TestImages.CorruptJpeg());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.Client.GetAsync(new Uri($"/api/v1/versions/{version}/content", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await MediaPipelineFixture.ReviewAsync(owner, version, "InReview")).StatusCode);
        using var scope = fixture.NewScope();
        var payload = await scope.Db.OutboxMessages.Where(m => m.SubjectRef == version && m.MessageType == MessageTypes.MediaProcessingRequested).Select(m => m.Payload).SingleAsync();
        var result = await scope.Get<MediaProcessingHandler>().HandleAsync(JsonSerializer.Deserialize<MediaProcessingRequestedV1>(payload)!, "quarantine-test", CancellationToken.None);
        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{version}/reprocess", new { })).StatusCode);
        var archive = await owner.Client.GetFromJsonAsync<JsonElement>("/api/v1/archive");
        var row = Assert.Single(archive.Items());
        Assert.Equal("Quarantined", row.Str("lifecycle"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("thumbnail").ValueKind);
    }

    [Fact]
    public async Task ConcurrentUploadRetriesReserveOnlyOneVersion()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Idempotency race");
        var (asset, _) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "base.jpg");
        var key = Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Upload()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/assets/{asset}/versions") { Content = TestImages.AsUpload(TestImages.Jpeg(200, 100), "next.jpg") };
            request.Headers.Add("Idempotency-Key", key);
            return await owner.Client.SendAsync(request);
        }
        var responses = await Task.WhenAll(Upload(), Upload(), Upload());
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict));
        var final = await Upload();
        Assert.Equal(HttpStatusCode.Created, final.StatusCode);
        var versions = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/assets/{asset}/versions");
        Assert.Equal(2, versions.Items().Count());
    }

    [Fact]
    public async Task DatabaseRejectsSourceMutationEvenOutsideEfTracking()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Immutable source");
        var (_, version) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "source.jpg");
        using var scope = fixture.NewScope();
        await Assert.ThrowsAsync<PostgresException>(() => scope.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE asset_versions SET sha256 = 'tampered' WHERE public_ref = {version}"));
        await Assert.ThrowsAsync<PostgresException>(() => scope.Db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM state_history WHERE subject_ref = {version}"));
    }

    [Fact]
    public async Task ApprovalOwnershipConcurrencyDeliveryAndHistoryAreDurable()
    {
        var owner = await fixture.RegisterAsync();
        var first = await fixture.RegisterAsync();
        var second = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Approval race");
        await ProjectAuthorizationTests.AddMemberAsync(owner, project, first.Email, "Reviewer");
        await ProjectAuthorizationTests.AddMemberAsync(owner, project, second.Email, "Reviewer");
        var (_, version) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "proof.jpg");
        await fixture.OpenReviewAsync(owner, version);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await MediaPipelineFixture.ReviewAsync(owner, version, "Approved")).StatusCode);
        var snapshot = await owner.Client.GetAsync(new Uri($"/api/v1/versions/{version}", UriKind.Relative));
        async Task<HttpResponseMessage> Approve(TestUser user)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/versions/{version}/approvals") { Content = JsonContent.Create(new { }) };
            request.Headers.TryAddWithoutValidation("If-Match", snapshot.Headers.ETag!.ToString());
            return await user.Client.SendAsync(request);
        }
        var results = await Task.WhenAll(Approve(first), Approve(second));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var approvals = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{version}/approvals");
        Assert.Single(approvals.Items());
        Assert.True(approvals.Items().Single().Str("actorRef") is not null);
        var latest = await owner.Client.GetAsync(new Uri($"/api/v1/versions/{version}", UriKind.Relative));
        using var delivery = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/versions/{version}/deliveries") { Content = JsonContent.Create(new { }) };
        delivery.Headers.TryAddWithoutValidation("If-Match", latest.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.SendAsync(delivery)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await MediaPipelineFixture.ReviewAsync(owner, version, "InReview")).StatusCode);
        var deliveries = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{version}/deliveries");
        Assert.Single(deliveries.Items());
    }

    [Fact]
    public async Task InvalidMediaContractCannotProduceSuccessfulEmptyDerivatives()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Contract guard");
        var (_, version) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "proof.jpg");
        using var scope = fixture.NewScope();
        var payload = await scope.Db.OutboxMessages.Where(m => m.SubjectRef == version && m.MessageType == MessageTypes.MediaProcessingRequested).Select(m => m.Payload).SingleAsync();
        var message = JsonSerializer.Deserialize<MediaProcessingRequestedV1>(payload)! with { Operations = [] };
        var result = await scope.Get<MediaProcessingHandler>().HandleAsync(message, "contract-test", CancellationToken.None);
        Assert.Equal(HandlerOutcome.DeadLetter, result.Outcome);
        Assert.False(await scope.Db.AssetDerivatives.AnyAsync(d => scope.Db.AssetVersions.Any(v => v.PublicRef == version && v.Id == d.AssetVersionId)));
    }

    [Fact]
    public async Task ActualConsumerRejectsMalformedMessagesIntoTheDeadLetterQueue()
    {
        await fixture.DispatchAsync();
        await using var broker = await BrokerHarness.ConnectAsync();
        await broker.PurgeAsync();
        using var scope = fixture.NewScope();
        using var consumer = ActivatorUtilities.CreateInstance<MediaProcessingConsumer>(scope.Services);
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await broker.PublishRawAsync("not-json"u8.ToArray(), MessageTypes.MediaProcessingRequested, 1);
            await Eventually.TrueAsync(async () => await broker.DepthAsync(Topology.DeadLetterQueue) > 0, TimeSpan.FromSeconds(15), "malformed message to reach DLQ");
        }
        finally { await consumer.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public async Task TransientStorageFailureRetriesWithoutDuplicateDerivativesOrVersions()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Transient storage");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "source.jpg");
        using var scope = fixture.NewScope();
        var json = await scope.Db.OutboxMessages.Where(m => m.SubjectRef == versionRef && m.MessageType == MessageTypes.MediaProcessingRequested).Select(m => m.Payload).SingleAsync();
        var message = JsonSerializer.Deserialize<MediaProcessingRequestedV1>(json)!;
        var storage = new FailFirstReadStorage(scope.Get<IObjectStorage>());
        var handler = ActivatorUtilities.CreateInstance<MediaProcessingHandler>(scope.Services, storage);
        Assert.Equal(HandlerOutcome.Retry, (await handler.HandleAsync(message, "retry-worker", CancellationToken.None)).Outcome);
        scope.Db.ChangeTracker.Clear();
        Assert.Equal(ProcessingState.Failed, (await scope.Db.AssetVersions.SingleAsync(v => v.PublicRef == versionRef)).ProcessingState);
        Assert.Equal(HandlerOutcome.Complete, (await handler.HandleAsync(message, "retry-worker", CancellationToken.None)).Outcome);
        Assert.Equal(HandlerOutcome.Complete, (await handler.HandleAsync(message, "retry-worker", CancellationToken.None)).Outcome);
        scope.Db.ChangeTracker.Clear();
        var version = await scope.Db.AssetVersions.Include(v => v.Derivatives).SingleAsync(v => v.PublicRef == versionRef);
        Assert.Equal(ProcessingState.Succeeded, version.ProcessingState);
        Assert.Equal(2, version.Derivatives.Count);
        var job = await scope.Db.ProcessingJobs.SingleAsync(j => j.PublicRef == message.JobRef);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(1, await scope.Db.ProcessedMessages.CountAsync(m => m.MessageId == message.MessageId));
        Assert.Equal(1, await scope.Db.AssetVersions.CountAsync(v => v.AssetId == version.AssetId));
        Assert.True(await scope.Db.StateHistory.CountAsync(h => h.SubjectRef == job.PublicRef) >= 5);
    }

    [Fact]
    public async Task ExpiredOutboxPublisherCannotOverwriteANewerLease()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Outbox lease");
        var (_, version) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "source.jpg");
        using var first = fixture.NewScope();
        using var second = fixture.NewScope();
        var stale = await first.Db.OutboxMessages.SingleAsync(m => m.SubjectRef == version && m.MessageType == MessageTypes.MediaProcessingRequested);
        var current = await second.Db.OutboxMessages.SingleAsync(m => m.Id == stale.Id);
        current.Lease("new-publisher", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);
        await second.Db.SaveChangesAsync();
        stale.MarkPublished(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.Db.SaveChangesAsync());
        current.Lease("released", TimeSpan.Zero, DateTimeOffset.UtcNow.AddMinutes(-1));
        await second.Db.SaveChangesAsync();
    }

    private sealed class FailFirstReadStorage(IObjectStorage inner) : IObjectStorage
    {
        private bool _failed;
        public Task<StoredObject?> ReadAsync(string key, CancellationToken cancellationToken)
        {
            if (!_failed) { _failed = true; throw new IOException("Injected temporary storage outage."); }
            return inner.ReadAsync(key, cancellationToken);
        }
        public Task WriteAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) => inner.WriteAsync(key, content, contentType, cancellationToken);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => inner.ExistsAsync(key, cancellationToken);
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
        public IAsyncEnumerable<ObjectMetadata> ListAsync(string prefix, CancellationToken cancellationToken) => inner.ListAsync(prefix, cancellationToken);
    }

}
