using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Observability;
using MediaPipeline.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MediaPipeline.Infrastructure.Messaging;

/// <summary>
/// Publishes with publisher confirms. A publish that is not confirmed within the timeout
/// is reported as failed, which may mean the broker never saw it <em>or</em> saw it and
/// lost the acknowledgement - so the outbox will republish and consumers must be idempotent.
/// </summary>
public sealed class RabbitMqPublisher : IMessagePublisher, IAsyncDisposable
{
    private readonly RabbitMqConnectionProvider _connections;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly SemaphoreSlim _channelGate = new(1, 1);
    private readonly Counter<long> _publishFailures;
    private readonly Counter<long> _published;

    private IChannel? _channel;
    private bool _disposed;

    public RabbitMqPublisher(
        RabbitMqConnectionProvider connections,
        IOptions<RabbitMqOptions> options,
        MediaPipelineMetrics metrics,
        ILogger<RabbitMqPublisher> logger)
    {
        _connections = connections;
        _options = options.Value;
        _logger = logger;
        _publishFailures = metrics.RabbitPublishFailures;
        _published = metrics.RabbitPublished;
    }

    public async Task PublishAsync(
        string exchange,
        string routingKey,
        Guid messageId,
        string messageType,
        int contractVersion,
        ReadOnlyMemory<byte> body,
        string? traceParent,
        IReadOnlyDictionary<string, object?>? extraHeaders,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var activity = MediaPipelineTelemetry.ActivitySource.StartActivity(
            $"{exchange} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
        activity?.SetTag("messaging.message.id", messageId);

        var headers = new Dictionary<string, object?>
        {
            [MessageTypes.Headers.MessageType] = Encoding.UTF8.GetBytes(messageType),
            [MessageTypes.Headers.ContractVersion] = contractVersion,
        };

        // Carry the trace across the broker so a worker span joins the upload's trace.
        var propagated = activity?.Id ?? traceParent;
        if (!string.IsNullOrEmpty(propagated))
        {
            headers[MessageTypes.Headers.TraceParent] = Encoding.UTF8.GetBytes(propagated);
        }

        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                headers[key] = value;
            }
        }

        var properties = new BasicProperties
        {
            MessageId = messageId.ToString("D"),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Type = messageType,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Headers = headers,
        };

        try
        {
            var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.PublishConfirmTimeout);

            await channel.BasicPublishAsync(
                exchange, routingKey, mandatory: true, properties, body, timeout.Token).ConfigureAwait(false);

            _published.Add(1, new KeyValuePair<string, object?>("exchange", exchange));
            _logger.LogInformation(
                "event=message_published exchange={Exchange} routing_key={RoutingKey} message_id={MessageId} type={MessageType}",
                exchange, routingKey, messageId, messageType);
        }
        catch (Exception ex)
        {
            _publishFailures.Add(1, new KeyValuePair<string, object?>("exchange", exchange));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            // Drop the channel so the next attempt reconnects rather than reusing a
            // channel the broker may already have closed.
            await ResetChannelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _channelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            var connection = await _connections.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            await _connections.EnsureTopologyAsync(cancellationToken).ConfigureAwait(false);

            _channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);

            return _channel;
        }
        finally
        {
            _channelGate.Release();
        }
    }

    private async Task ResetChannelAsync()
    {
        var channel = Interlocked.Exchange(ref _channel, null);
        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "event=publisher_channel_dispose_failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ResetChannelAsync().ConfigureAwait(false);
        _channelGate.Dispose();
    }
}
