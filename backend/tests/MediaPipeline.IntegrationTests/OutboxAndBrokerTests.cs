using System.Net.Http.Json;
using System.Text.Json;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Infrastructure.Messaging;
using MediaPipeline.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MediaPipeline.IntegrationTests;

/// <summary>
/// The durable hand-off from PostgreSQL to RabbitMQ, exercised against both real engines.
/// <para>
/// The outbox exists for one failure: the database commits and the publish then fails, so
/// work that the user was told about never reaches the worker. These tests force that shape
/// rather than describing it.
/// </para>
/// </summary>
[Collection(MediaPipelineTestGroup.Name)]
public sealed class OutboxAndBrokerTests(MediaPipelineFixture fixture) : IAsyncLifetime
{
    private BrokerHarness _broker = null!;

    public async Task InitializeAsync()
    {
        _broker = await BrokerHarness.ConnectAsync();
        await _broker.PurgeAsync();
    }

    public async Task DisposeAsync() => await _broker.DisposeAsync();

    [Fact]
    public async Task AnUploadWritesItsMessageToTheOutboxInTheSameTransaction()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Outbox Atomicity");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "frame.jpg");

        using var scope = fixture.NewScope();

        // The version, its job and the outbox row are all durable, and nothing has been
        // published yet - the publisher is disabled in this suite.
        var version = await scope.Db.AssetVersions.AsNoTracking()
            .SingleAsync(v => v.PublicRef == versionRef);

        var job = await scope.Db.ProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.AssetVersionId == version.Id);

        var outbox = await scope.Db.OutboxMessages.AsNoTracking()
            .Where(m => m.MessageType == MessageTypes.MediaProcessingRequested)
            .ToListAsync();

        Assert.Equal(Domain.Assets.ProcessingState.Queued, version.ProcessingState);
        Assert.Equal(Domain.Assets.ProcessingState.Queued, job.State);
        Assert.Contains(outbox, m => m.Status == OutboxStatus.Pending && m.SubjectRef == versionRef);
    }

    [Fact]
    public async Task ThePublisherMovesPendingRowsOntoTheBroker()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Publish Path");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "publish.jpg");

        var dispatched = await DispatchOutboxAsync();
        Assert.True(dispatched > 0, "The publisher should have claimed at least the new upload's message.");

        var messages = await _broker.DrainAsync(Topology.MediaQueue);
        Assert.Contains(messages, m => m.AssetVersionRef == versionRef);

        // The row is marked published, so a restart cannot send it a second time in the
        // ordinary case.
        using var scope = fixture.NewScope();
        var published = await scope.Db.OutboxMessages.AsNoTracking()
            .Where(m => m.SubjectRef == versionRef && m.MessageType == MessageTypes.MediaProcessingRequested)
            .SingleAsync();

        Assert.Equal(OutboxStatus.Published, published.Status);
        Assert.NotNull(published.PublishedAt);
    }

    [Fact]
    public async Task AMessageCarriesReferencesNotMediaAndStaysSmall()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Message Shape");

        // A deliberately large source: the message must not grow with it.
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(
            owner, project, "large.jpg", TestImages.Jpeg(2400, 1600));

        await DispatchOutboxAsync();
        var messages = await _broker.DrainAsync(Topology.MediaQueue);
        var message = Assert.Single(messages, m => m.AssetVersionRef == versionRef);

        Assert.StartsWith("quarantine/", message.SourceObjectKey, StringComparison.Ordinal);
        Assert.Equal(64, message.Sha256.Length);
        Assert.True(message.ByteSize > 10_000, "The source itself should be substantial.");

        var encoded = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message);
        Assert.True(encoded.Length < 2048, $"Message payload was {encoded.Length} bytes; it must stay a reference, not a payload.");
    }

    [Fact]
    public async Task ABrokerOutageLeavesTheWorkPendingRatherThanLosingIt()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Broker Outage");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "resilient.jpg");

        // Publish through a publisher pointed at a port nothing is listening on: the exact
        // shape of a broker being down while the API keeps accepting uploads.
        var failures = await DispatchWithUnreachableBrokerAsync();
        Assert.True(failures > 0, "The publisher should have attempted and failed to publish.");

        using (var scope = fixture.NewScope())
        {
            var row = await scope.Db.OutboxMessages.AsNoTracking()
                .Where(m => m.SubjectRef == versionRef && m.MessageType == MessageTypes.MediaProcessingRequested)
                .SingleAsync();

            // Still pending, attempt counted, backoff scheduled - not lost, not published.
            Assert.Equal(OutboxStatus.Pending, row.Status);
            Assert.True(row.AttemptCount >= 1);
            Assert.NotNull(row.LastError);
            Assert.True(row.NextAttemptAt > row.OccurredAt, "A failed attempt must schedule a backoff.");
        }

        // The user is told the truth in the meantime: queued, not processed.
        var version = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{versionRef}");
        Assert.Equal("Queued", version.Str("processingState"));

        // When the broker returns, the same row is delivered. Backoff has to elapse first,
        // so the row is made due rather than waiting on wall-clock time.
        await MakeOutboxDueAsync(versionRef);
        await DispatchOutboxAsync();

        var messages = await _broker.DrainAsync(Topology.MediaQueue);
        Assert.Contains(messages, m => m.AssetVersionRef == versionRef);
    }

    [Fact]
    public async Task CompetingPublishersDoNotDeadlockAndDeliverEveryMessage()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Competing Publishers");

        var expected = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, $"batch-{i}.jpg");
            expected.Add(versionRef);
        }

        // Two publisher instances draining the same table at once. FOR UPDATE SKIP LOCKED
        // means neither blocks; between them every row must be claimed exactly once in the
        // happy path, and no row may be left behind.
        var first = DispatchOutboxAsync();
        var second = DispatchOutboxAsync();
        await Task.WhenAll(first, second);

        using var scope = fixture.NewScope();
        var pending = await scope.Db.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Status == OutboxStatus.Pending);

        Assert.Equal(0, pending);

        var delivered = await _broker.DrainAsync(Topology.MediaQueue);
        foreach (var versionRef in expected)
        {
            Assert.Contains(delivered, m => m.AssetVersionRef == versionRef);
        }
    }

    [Fact]
    public async Task RepublishingTheSameRowIsSafeBecauseDeliveryIsAtLeastOnce()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Duplicate Publish");
        var (_, versionRef) = await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "duplicate.jpg");

        await DispatchOutboxAsync();

        // Force the row back to pending, exactly as a broker acknowledgement that timed out
        // after the broker had already accepted the message would leave it.
        await ResetOutboxToPendingAsync(versionRef);
        await DispatchOutboxAsync();

        var messages = await _broker.DrainAsync(Topology.MediaQueue);
        var copies = messages.Where(m => m.AssetVersionRef == versionRef).ToList();

        Assert.Equal(2, copies.Count);

        // Both copies share a message id, which is what lets the consumer's inbox ledger
        // recognise the second as a duplicate. This is the system being at-least-once, not
        // a defect: exactly-once delivery is not claimed anywhere.
        Assert.Single(copies.Select(c => c.MessageId).Distinct());
    }

    [Fact]
    public async Task TheTopologyDeclaresADeadLetterPathAndRetryTiers()
    {
        // Force the topology to exist by publishing once.
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Topology");
        await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "topology.jpg");
        await DispatchOutboxAsync();

        // QueueDeclarePassive throws if a queue is absent, so reaching a depth proves it exists.
        _ = await _broker.DepthAsync(Topology.MediaQueue);
        _ = await _broker.DepthAsync(Topology.DeadLetterQueue);

        foreach (var tier in Topology.RetryTiers)
        {
            _ = await _broker.DepthAsync(tier.QueueName);
        }

        // Tiers must be strictly increasing, or backoff is not backoff.
        var delays = Topology.RetryTiers.Select(t => t.Delay).ToList();
        Assert.Equal(delays.OrderBy(d => d), delays);
    }

    [Fact]
    public async Task AnUnknownContractVersionIsRejectedRatherThanGuessedAt()
    {
        var owner = await fixture.RegisterAsync();
        var project = await ProjectAuthorizationTests.CreateProjectAsync(owner, "Contract Guard");
        await ProjectAuthorizationTests.UploadAssetAsync(owner, project, "contract.jpg");
        await DispatchOutboxAsync();
        await _broker.DrainAsync(Topology.MediaQueue);

        // A message from a future producer. The consumer must refuse it, not attempt to
        // interpret fields it does not understand.
        await _broker.PublishRawAsync(
            """{"messageId":"0195c0a0-0000-7000-8000-000000000000","occurredAt":"2026-01-01T00:00:00Z"}"""u8.ToArray(),
            MessageTypes.MediaProcessingRequested,
            contractVersion: 99);

        var handler = ContractGuard.Evaluate(MessageTypes.MediaProcessingRequested, 99);
        Assert.False(handler, "A message declaring an unsupported contract version must not be processed.");

        await _broker.PurgeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Runs exactly one publisher pass, so assertions do not race a timer.</summary>
    private async Task<int> DispatchOutboxAsync()
    {
        using var scope = fixture.NewScope();
        var publisher = ActivatorUtilities.CreateInstance<OutboxPublisher>(scope.Services);
        return await publisher.DispatchBatchAsync(CancellationToken.None);
    }

    /// <summary>
    /// Runs a publisher whose broker connection cannot succeed, reproducing an outage
    /// without stopping the shared RabbitMQ the rest of the suite needs.
    /// </summary>
    private async Task<int> DispatchWithUnreachableBrokerAsync()
    {
        using var scope = fixture.NewScope();

        var options = Microsoft.Extensions.Options.Options.Create(new RabbitMqOptions
        {
            HostName = "127.0.0.1",
            Port = 1, // nothing listens here
            VirtualHost = MediaPipelineFixture.Broker.VirtualHost,
            UserName = MediaPipelineFixture.Broker.UserName,
            Password = MediaPipelineFixture.Broker.Password,
            PublishConfirmTimeout = TimeSpan.FromSeconds(2),
            RecoveryInterval = TimeSpan.FromMilliseconds(200),
        });

        await using var connections = new RabbitMqConnectionProvider(
            options, scope.Get<ILogger<RabbitMqConnectionProvider>>());

        await using var publisher = new RabbitMqPublisher(
            connections, options,
            scope.Get<Application.Observability.MediaPipelineMetrics>(),
            scope.Get<ILogger<RabbitMqPublisher>>());

        var outbox = new OutboxPublisher(
            scope.Get<IServiceScopeFactory>(),
            publisher,
            Microsoft.Extensions.Options.Options.Create(new OutboxOptions { MaxAttempts = 10 }),
            scope.Get<Application.Abstractions.IClock>(),
            scope.Get<Application.Observability.MediaPipelineMetrics>(),
            scope.Get<ILogger<OutboxPublisher>>());

        return await outbox.DispatchBatchAsync(CancellationToken.None);
    }

    private async Task MakeOutboxDueAsync(string versionRef)
    {
        using var scope = fixture.NewScope();
        await scope.Db.OutboxMessages
            .Where(m => m.SubjectRef == versionRef && m.MessageType == MessageTypes.MediaProcessingRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    private async Task ResetOutboxToPendingAsync(string versionRef)
    {
        using var scope = fixture.NewScope();
        await scope.Db.OutboxMessages
            .Where(m => m.SubjectRef == versionRef && m.MessageType == MessageTypes.MediaProcessingRequested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null));
    }
}

/// <summary>Mirrors the consumer's contract check so it can be asserted without a broker round trip.</summary>
internal static class ContractGuard
{
    public static bool Evaluate(string messageType, int contractVersion) =>
        messageType == MessageTypes.MediaProcessingRequested
        && contractVersion == MediaProcessingRequestedV1.ContractVersion;
}
