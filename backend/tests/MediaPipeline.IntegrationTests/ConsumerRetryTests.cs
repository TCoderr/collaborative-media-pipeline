using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Observability;
using MediaPipeline.Contracts;
using MediaPipeline.Infrastructure.Messaging;
using MediaPipeline.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Xunit;
using Xunit.Abstractions;

namespace MediaPipeline.IntegrationTests;

/// <summary>
/// The transport behaviour behind the handler's decisions, exercised on the real
/// broker.
/// <para>
/// The handler tests prove the <em>decision</em> - retry or dead-letter. These
/// prove the topology acts on it: a retryable message really waits on a delay
/// queue and comes back when its TTL expires, a rejected message really reaches
/// the dead-letter queue, and a trace really survives the hop. Those are broker
/// behaviours; asserting them against a mock would prove only that the mock
/// behaves as written.
/// </para>
/// <para>
/// Each test binds <em>its own</em> queue to the real exchanges rather than
/// reading the shared work queue. A direct exchange delivers to every queue bound
/// on the routing key, so the test always gets a copy - and a worker running
/// against the same broker neither steals the message nor has to be stopped.
/// </para>
/// </summary>
[Collection(MediaPipelineTestGroup.Name)]
public sealed class ConsumerRetryTests(MediaPipelineFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private BrokerHarness _broker = null!;
    private IConnection _connection = null!;
    private IChannel _channel = null!;

    /// <summary>A queue only this class reads, bound alongside the real work queue.</summary>
    private string _observerQueue = string.Empty;

    public async Task InitializeAsync()
    {
        _broker = await BrokerHarness.ConnectAsync();

        var factory = MediaPipelineFixture.Broker.ToFactory();
        _connection = await factory.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();

        // Declare through the application's own provider, so what is bound to is
        // the production topology rather than one the test invented.
        using (var scope = fixture.NewScope())
        {
            await scope.Get<RabbitMqConnectionProvider>().EnsureTopologyAsync(CancellationToken.None);
        }

        var declared = await _channel.QueueDeclareAsync(
            queue: string.Empty, durable: false, exclusive: true, autoDelete: true);
        _observerQueue = declared.QueueName;

        await _channel.QueueBindAsync(_observerQueue, Topology.MediaExchange, Topology.MediaRoutingKey);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
        await _broker.DisposeAsync();
    }

    [Fact]
    public async Task ARetryableFailureWaitsOnADelayTierAndComesBack()
    {
        // The 5s tier first, so the whole round trip - park, expire, redeliver -
        // is observable without a long wait.
        var tier = Topology.RetryTiers[0];
        Assert.Equal(TimeSpan.FromSeconds(5), tier.Delay);

        await PurgeTierAsync(tier.QueueName);
        var message = SampleMessage();

        using (var scope = fixture.NewScope())
        {
            // Exactly what the consumer does when the handler asks for a retry.
            await scope.Get<IMessagePublisher>().PublishAsync(
                Topology.RetryExchange,
                tier.RoutingKey,
                message.MessageId,
                MessageTypes.MediaProcessingRequested,
                MediaProcessingRequestedV1.ContractVersion,
                JsonSerializer.SerializeToUtf8Bytes(message),
                traceParent: null,
                extraHeaders: new Dictionary<string, object?>
                {
                    [MessageTypes.Headers.AttemptCount] = 2,
                },
                CancellationToken.None);
        }

        // It waits on the delay queue. Nothing is working on it yet.
        await Eventually.TrueAsync(
            async () => await _broker.DepthAsync(tier.QueueName) >= 1,
            TimeSpan.FromSeconds(10),
            $"the message to be parked on {tier.QueueName}");

        output.WriteLine($"parked on {tier.QueueName}");

        // The TTL expires and the tier's dead-letter route returns it to the media
        // exchange, which reaches both the work queue and this observer. No
        // scheduler process is involved anywhere in that.
        var returned = await TakeFromObserverAsync(
            m => m.MessageId == message.MessageId,
            TimeSpan.FromSeconds(30),
            "the message to return after its TTL");

        Assert.Equal(message.AssetVersionRef, returned.AssetVersionRef);
        output.WriteLine($"returned to {Topology.MediaExchange} after {tier.Delay.TotalSeconds}s");
    }

    [Fact]
    public void RetryTiersBackOffAsTheAttemptCountRises()
    {
        var first = Topology.TierForAttempt(1);
        var second = Topology.TierForAttempt(2);
        var third = Topology.TierForAttempt(3);
        var beyond = Topology.TierForAttempt(99);

        // Without this, "backoff" is just another word for "retry".
        Assert.True(second.Delay > first.Delay);
        Assert.True(third.Delay > second.Delay);

        // Past the last tier it saturates rather than throwing or wrapping.
        Assert.Equal(third.Delay, beyond.Delay);

        output.WriteLine(
            $"tiers: {first.Delay.TotalSeconds}s -> {second.Delay.TotalSeconds}s -> {third.Delay.TotalSeconds}s (capped)");
    }

    [Fact]
    public async Task ARejectedMessageIsRoutedToTheDeadLetterQueue()
    {
        // A queue carrying the same dead-letter arguments as the work queue, so
        // the routing under test is the production routing - without competing
        // with a live worker for the production queue.
        var poison = await _channel.QueueDeclareAsync(
            queue: string.Empty,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = Topology.DeadLetterRoutingKey,
            });

        var before = await _broker.DepthAsync(Topology.DeadLetterQueue);
        var message = SampleMessage();

        await _channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: poison.QueueName,
            mandatory: true,
            new BasicProperties
            {
                MessageId = message.MessageId.ToString("D"),
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Type = MessageTypes.MediaProcessingRequested,
            },
            JsonSerializer.SerializeToUtf8Bytes(message));

        var delivered = await Eventually.Async(
            async () => await _channel.BasicGetAsync(poison.QueueName, autoAck: false),
            TimeSpan.FromSeconds(15),
            "the poison message to arrive");

        // requeue: false is what sends it to the dead-letter exchange - the same
        // call the consumer makes for a permanent failure.
        await _channel.BasicRejectAsync(delivered.DeliveryTag, requeue: false);

        await Eventually.TrueAsync(
            async () => await _broker.DepthAsync(Topology.DeadLetterQueue) > before,
            TimeSpan.FromSeconds(15),
            "the rejected message to reach the dead-letter queue");

        // Still readable there, so an operator can see what failed and replay it.
        var dead = await _broker.DrainAsync(Topology.DeadLetterQueue);
        Assert.Contains(dead, m => m.MessageId == message.MessageId);

        output.WriteLine($"dead-lettered onto {Topology.DeadLetterQueue} and readable");
    }

    [Fact]
    public async Task ATraceSurvivesTheHopThroughTheBroker()
    {
        // Trace continuity across the queue is what makes "upload to derivative"
        // one story in a trace viewer rather than two unrelated ones.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MediaPipelineTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var message = SampleMessage();

        using var root = MediaPipelineTelemetry.ActivitySource.StartActivity("test.upload", ActivityKind.Server);
        Assert.NotNull(root);
        var expectedTraceId = root!.TraceId;

        using (var scope = fixture.NewScope())
        {
            await scope.Get<IMessagePublisher>().PublishAsync(
                Topology.MediaExchange,
                Topology.MediaRoutingKey,
                message.MessageId,
                MessageTypes.MediaProcessingRequested,
                MediaProcessingRequestedV1.ContractVersion,
                JsonSerializer.SerializeToUtf8Bytes(message),
                traceParent: null,
                extraHeaders: null,
                CancellationToken.None);
        }

        var headers = await Eventually.Async(
            async () =>
            {
                var result = await _channel.BasicGetAsync(_observerQueue, autoAck: true);
                if (result is null)
                {
                    return null;
                }

                var body = JsonSerializer.Deserialize<MediaProcessingRequestedV1>(result.Body.Span);
                return body?.MessageId == message.MessageId ? result.BasicProperties.Headers : null;
            },
            TimeSpan.FromSeconds(15),
            "the published message to reach the observer queue");

        Assert.True(headers.TryGetValue(MessageTypes.Headers.TraceParent, out var raw));
        var traceParent = Encoding.UTF8.GetString((byte[])raw!);
        output.WriteLine($"traceparent on the wire: {traceParent}");

        Assert.Contains(expectedTraceId.ToString(), traceParent, StringComparison.Ordinal);

        // And a consumer span started from it genuinely joins that trace, which is
        // the property that makes the two halves one story.
        using var consumed = MediaPipelineTelemetry.ActivitySource.StartActivity(
            "test.consume", ActivityKind.Consumer, traceParent);

        Assert.NotNull(consumed);
        Assert.Equal(expectedTraceId, consumed!.TraceId);
    }

    [Fact]
    public async Task ThePublisherReportsFailureWhenTheBrokerIsUnreachable()
    {
        // A publish that cannot be confirmed must surface as a failure, so the
        // outbox row stays pending rather than being marked published on hope.
        using var scope = fixture.NewScope();

        var options = Options.Create(new RabbitMqOptions
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
            connections, options, scope.Get<MediaPipelineMetrics>(), scope.Get<ILogger<RabbitMqPublisher>>());

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(
            Topology.MediaExchange,
            Topology.MediaRoutingKey,
            Guid.CreateVersion7(),
            MessageTypes.MediaProcessingRequested,
            MediaProcessingRequestedV1.ContractVersion,
            "{}"u8.ToArray(),
            traceParent: null,
            extraHeaders: null,
            CancellationToken.None));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<MediaProcessingRequestedV1> TakeFromObserverAsync(
        Func<MediaProcessingRequestedV1, bool> predicate,
        TimeSpan timeout,
        string description) =>
        await Eventually.Async(
            async () =>
            {
                var result = await _channel.BasicGetAsync(_observerQueue, autoAck: true);
                if (result is null)
                {
                    return null;
                }

                var body = JsonSerializer.Deserialize<MediaProcessingRequestedV1>(result.Body.Span);
                return body is not null && predicate(body) ? body : null;
            },
            timeout,
            description);

    private async Task PurgeTierAsync(string queueName)
    {
        try
        {
            await _channel.QueuePurgeAsync(queueName);
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            // Not declared yet; EnsureTopologyAsync creates it.
        }
    }

    private static MediaProcessingRequestedV1 SampleMessage() => new()
    {
        MessageId = Guid.CreateVersion7(),
        OccurredAt = DateTimeOffset.UtcNow,
        ProjectRef = "prj_0000000000000000000000",
        AssetRef = "ast_0000000000000000000000",
        AssetVersionRef = "ver_0000000000000000000000",
        JobRef = "job_0000000000000000000000",
        SourceObjectKey = "projects/0/assets/0/versions/0/source",
        ContentType = "image/jpeg",
        ByteSize = 1024,
        Sha256 = new string('a', 64),
        Operations = ["Thumbnail"],
    };
}
