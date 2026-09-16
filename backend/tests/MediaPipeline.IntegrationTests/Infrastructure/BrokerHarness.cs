using System.Text;
using System.Text.Json;
using MediaPipeline.Contracts;
using MediaPipeline.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace MediaPipeline.IntegrationTests.Infrastructure;

/// <summary>
/// Direct access to the live RabbitMQ broker so tests can observe what the system actually
/// published, inject redeliveries, and read the dead-letter queue.
/// <para>
/// Asserting on the broker rather than on a mock is the point: at-least-once delivery,
/// dead-lettering and TTL-based retry are broker behaviours, and a test double would only
/// prove that the double behaves as written.
/// </para>
/// </summary>
public sealed class BrokerHarness : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private BrokerHarness(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<BrokerHarness> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var factory = MediaPipelineFixture.Broker.ToFactory();
        var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return new BrokerHarness(connection, channel);
    }

    /// <summary>Removes anything left on the work and dead-letter queues by earlier tests.</summary>
    public async Task PurgeAsync()
    {
        foreach (var queue in new[] { Topology.MediaQueue, Topology.DeadLetterQueue })
        {
            try
            {
                await _channel.QueuePurgeAsync(queue).ConfigureAwait(false);
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                // The queue does not exist yet; the API declares it on first publish.
            }
        }
    }

    public async Task<uint> DepthAsync(string queue)
    {
        try
        {
            var result = await _channel.QueueDeclarePassiveAsync(queue).ConfigureAwait(false);
            return result.MessageCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// How many consumers are attached. A transport test that needs to read back
    /// what it published cannot win a race against a live worker.
    /// </summary>
    public async Task<uint> ConsumerCountAsync(string queue)
    {
        try
        {
            var result = await _channel.QueueDeclarePassiveAsync(queue).ConfigureAwait(false);
            return result.ConsumerCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Pulls one message off a queue without acknowledging the work it represents. Used to
    /// inspect what the outbox published and to replay it.
    /// </summary>
    public async Task<BasicGetResult?> TakeAsync(string queue)
    {
        // The channel is recreated per call because a failed Get can close it.
        return await _channel.BasicGetAsync(queue, autoAck: true).ConfigureAwait(false);
    }

    public async Task<List<MediaProcessingRequestedV1>> DrainAsync(string queue, int max = 50)
    {
        var messages = new List<MediaProcessingRequestedV1>();
        for (var i = 0; i < max; i++)
        {
            var result = await TakeAsync(queue).ConfigureAwait(false);
            if (result is null)
            {
                break;
            }

            var message = JsonSerializer.Deserialize<MediaProcessingRequestedV1>(result.Body.Span);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    /// <summary>Publishes a raw body onto the work queue - used to inject poison messages.</summary>
    public async Task PublishRawAsync(byte[] body, string? messageType, int contractVersion, Guid? messageId = null)
    {
        var headers = new Dictionary<string, object?>
        {
            [MessageTypes.Headers.ContractVersion] = contractVersion,
        };

        if (messageType is not null)
        {
            headers[MessageTypes.Headers.MessageType] = Encoding.UTF8.GetBytes(messageType);
        }

        await _channel.BasicPublishAsync(
            Topology.MediaExchange,
            Topology.MediaRoutingKey,
            mandatory: true,
            new BasicProperties
            {
                MessageId = (messageId ?? Guid.NewGuid()).ToString("D"),
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Type = messageType,
                Headers = headers,
            },
            body).ConfigureAwait(false);
    }

    /// <summary>Republishes an existing contract message, simulating a broker redelivery.</summary>
    public Task RedeliverAsync(MediaProcessingRequestedV1 message) =>
        PublishRawAsync(
            JsonSerializer.SerializeToUtf8Bytes(message),
            MessageTypes.MediaProcessingRequested,
            MediaProcessingRequestedV1.ContractVersion,
            message.MessageId);

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Polls a condition instead of sleeping, so async assertions stay deterministic.</summary>
public static class Eventually
{
    public static async Task<T> Async<T>(
        Func<Task<T?>> probe,
        TimeSpan? timeout = null,
        string? description = null) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await probe().ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Condition never became true: {description ?? "unnamed"}.");
    }

    public static async Task TrueAsync(Func<Task<bool>> probe, TimeSpan? timeout = null, string? description = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Condition never became true: {description ?? "unnamed"}.");
    }
}
