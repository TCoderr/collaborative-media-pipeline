using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Observability;
using MediaPipeline.Application.Processing;
using MediaPipeline.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MediaPipeline.Infrastructure.Messaging;

/// <summary>
/// The media work-queue consumer.
/// <para>
/// Acknowledgement is manual and always happens <em>after</em> the handler has committed its
/// durable state, so a crash mid-flight causes redelivery rather than lost work. Prefetch and
/// an explicit semaphore bound how much a single worker can hold, so a burst of uploads
/// spreads across replicas instead of pinning one process.
/// </para>
/// </summary>
public sealed class MediaProcessingConsumer(
    RabbitMqConnectionProvider connections,
    IServiceScopeFactory scopeFactory,
    IMessagePublisher publisher,
    IOptions<RabbitMqOptions> options,
    MediaPipelineMetrics metrics,
    ILogger<MediaProcessingConsumer> logger) : BackgroundService
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private SemaphoreSlim? _concurrency;
    private IChannel? _channel;

    /// <summary>True once the consumer is attached to the queue. Feeds worker readiness.</summary>
    public bool IsConsuming { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentMessages, _options.MaxConcurrentMessages);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConsumerAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A broker outage is expected operational weather, not a crash: log, back
                // off, and reconnect. Messages stay durable on the broker meanwhile.
                IsConsuming = false;
                logger.LogError(ex, "event=consumer_connection_failed retry_in_seconds={RetrySeconds}",
                    _options.RecoveryInterval.TotalSeconds);

                try
                {
                    await Task.Delay(_options.RecoveryInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                IsConsuming = false;
                if (_channel is not null)
                {
                    await _channel.DisposeAsync().ConfigureAwait(false);
                    _channel = null;
                }
            }
        }

        IsConsuming = false;
        logger.LogInformation("event=consumer_stopped worker_id={WorkerId}", _workerId);
    }

    private async Task RunConsumerAsync(CancellationToken stoppingToken)
    {
        var connection = await connections.GetConnectionAsync(stoppingToken).ConfigureAwait(false);
        await connections.EnsureTopologyAsync(stoppingToken).ConfigureAwait(false);

        _channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
        await _channel.BasicQosAsync(0, _options.PrefetchCount, global: false, stoppingToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            await _concurrency!.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await OnMessageAsync(args, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                _concurrency.Release();
            }
        };

        await _channel.BasicConsumeAsync(
            Topology.MediaQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken).ConfigureAwait(false);

        IsConsuming = true;
        logger.LogInformation(
            "event=consumer_started queue={Queue} prefetch={Prefetch} concurrency={Concurrency} worker_id={WorkerId}",
            Topology.MediaQueue, _options.PrefetchCount, _options.MaxConcurrentMessages, _workerId);

        // Park until shutdown or until the channel drops, then let ExecuteAsync reconnect.
        while (!stoppingToken.IsCancellationRequested && _channel.IsOpen)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }

        IsConsuming = false;

        // Channel disposal cancels delivery and requeues anything not durably acknowledged.
    }

    private async Task OnMessageAsync(BasicDeliverEventArgs args, CancellationToken stoppingToken)
    {
        var deliveryTag = args.DeliveryTag;
        var attempt = ReadAttempt(args.BasicProperties);

        // Continue the producer's trace across the broker boundary.
        var parentId = ReadHeader(args.BasicProperties, MessageTypes.Headers.TraceParent);
        using var activity = MediaPipelineTelemetry.ActivitySource.StartActivity(
            $"{Topology.MediaQueue} process", ActivityKind.Consumer, parentId);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", Topology.MediaQueue);
        activity?.SetTag("messaging.rabbitmq.message.delivery_tag", deliveryTag);
        activity?.SetTag("messaging.attempt", attempt);

        MediaProcessingRequestedV1? message = null;
        try
        {
            message = Deserialise(args);
        }
        catch (Exception ex)
        {
            // A malformed or unknown-version message can never succeed. Dead-letter it
            // immediately rather than burning the retry budget on it.
            logger.LogError(ex, "event=message_contract_invalid delivery_tag={DeliveryTag}", deliveryTag);
            metrics.RabbitConsumerFailures.Add(1, new KeyValuePair<string, object?>("reason", "contract"));
            await SafeRejectAsync(deliveryTag).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<MediaProcessingHandler>();
            var work = handler.HandleAsync(message, _workerId, stoppingToken);
            var hardLimit = Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
            if (await Task.WhenAny(work, hardLimit).ConfigureAwait(false) != work)
            {
                stoppingToken.ThrowIfCancellationRequested();
                Environment.FailFast("MEDIA_PROCESSING_WATCHDOG: worker exceeded its hard processing deadline.");
            }
            var result = await work.ConfigureAwait(false);

            switch (result.Outcome)
            {
                case HandlerOutcome.Complete:
                    await _channel!.BasicAckAsync(deliveryTag, multiple: false, stoppingToken).ConfigureAwait(false);
                    break;

                case HandlerOutcome.Retry:
                    await RepublishForRetryAsync(args, message, attempt, result, stoppingToken).ConfigureAwait(false);
                    await _channel!.BasicAckAsync(deliveryTag, multiple: false, stoppingToken).ConfigureAwait(false);
                    break;

                case HandlerOutcome.DeadLetter:
                default:
                    logger.LogWarning(
                        "event=message_dead_lettered version_ref={VersionRef} failure_code={FailureCode}",
                        message.AssetVersionRef, result.FailureCode);
                    await SafeRejectAsync(deliveryTag).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: do not ack. The broker redelivers to whoever comes back.
            logger.LogInformation("event=message_abandoned_on_shutdown delivery_tag={DeliveryTag}", deliveryTag);
        }
        catch (Exception ex)
        {
            metrics.RabbitConsumerFailures.Add(1, new KeyValuePair<string, object?>("reason", "handler"));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "event=message_handler_failed delivery_tag={DeliveryTag}", deliveryTag);

            // The handler did not reach a decision (likely a database outage). Requeue so
            // another attempt happens once the dependency recovers.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            await SafeNackAsync(deliveryTag, requeue: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the message to a delay queue whose TTL matches the attempt number. The tier
    /// dead-letters back onto the work queue when it expires, which is how backoff happens
    /// without a scheduler process.
    /// </summary>
    private async Task RepublishForRetryAsync(
        BasicDeliverEventArgs args,
        MediaProcessingRequestedV1 message,
        int attempt,
        HandlerResult result,
        CancellationToken cancellationToken)
    {
        var tier = Topology.TierForAttempt(attempt);

        logger.LogInformation(
            "event=message_scheduled_for_retry version_ref={VersionRef} attempt={Attempt} delay_seconds={DelaySeconds} failure_code={FailureCode}",
            message.AssetVersionRef, attempt, tier.Delay.TotalSeconds, result.FailureCode);

        await publisher.PublishAsync(
            Topology.RetryExchange,
            tier.RoutingKey,
            message.MessageId,
            MessageTypes.MediaProcessingRequested,
            MediaProcessingRequestedV1.ContractVersion,
            args.Body.ToArray(),
            ReadHeader(args.BasicProperties, MessageTypes.Headers.TraceParent),
            new Dictionary<string, object?>
            {
                [MessageTypes.Headers.AttemptCount] = attempt + 1,
                [MessageTypes.Headers.FailureReason] = Encoding.UTF8.GetBytes(result.FailureCode ?? "unknown"),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static MediaProcessingRequestedV1 Deserialise(BasicDeliverEventArgs args)
    {
        var type = ReadHeader(args.BasicProperties, MessageTypes.Headers.MessageType) ?? args.BasicProperties.Type;
        if (!string.Equals(type, MessageTypes.MediaProcessingRequested, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected message type '{type}'.");
        }

        var version = args.BasicProperties.Headers?.TryGetValue(MessageTypes.Headers.ContractVersion, out var raw) == true
            ? Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture)
            : MediaProcessingRequestedV1.ContractVersion;

        if (version != MediaProcessingRequestedV1.ContractVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported contract version {version} for '{MessageTypes.MediaProcessingRequested}'.");
        }

        // Bound the payload: messages carry references, so anything large is malformed.
        if (args.Body.Length > 64 * 1024)
        {
            throw new InvalidOperationException("Message payload exceeds the permitted size.");
        }

        return JsonSerializer.Deserialize<MediaProcessingRequestedV1>(args.Body.Span)
            ?? throw new InvalidOperationException("Message payload deserialised to null.");
    }

    private static int ReadAttempt(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue(MessageTypes.Headers.AttemptCount, out var raw) == true && raw is not null)
        {
            try
            {
                return Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return 1;
            }
        }

        return 1;
    }

    private static string? ReadHeader(IReadOnlyBasicProperties properties, string name)
    {
        if (properties.Headers?.TryGetValue(name, out var raw) != true || raw is null)
        {
            return null;
        }

        return raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => raw.ToString(),
        };
    }

    private async Task SafeRejectAsync(ulong deliveryTag)
    {
        try
        {
            await _channel!.BasicRejectAsync(deliveryTag, requeue: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "event=reject_failed delivery_tag={DeliveryTag}", deliveryTag);
        }
    }

    private async Task SafeNackAsync(ulong deliveryTag, bool requeue)
    {
        try
        {
            await _channel!.BasicNackAsync(deliveryTag, multiple: false, requeue: requeue).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "event=nack_failed delivery_tag={DeliveryTag}", deliveryTag);
        }
    }

    public override void Dispose()
    {
        _concurrency?.Dispose();
        base.Dispose();
    }
}
