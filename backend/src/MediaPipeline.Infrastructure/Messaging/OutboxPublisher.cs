using System.Text;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Observability;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Contracts;
using MediaPipeline.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaPipeline.Infrastructure.Messaging;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 10;
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a claimed batch stays leased to one publisher instance.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Drains the transactional outbox onto RabbitMQ.
/// <para>
/// Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c> plus a lease column, so several
/// instances can run concurrently without fighting over the same row. That reduces
/// duplicate publishes; it does not eliminate them, and it is not meant to - the design
/// is at-least-once delivery with idempotent consumers.
/// </para>
/// </summary>
public sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IMessagePublisher publisher,
    IOptions<OutboxOptions> options,
    IClock clock,
    MediaPipelineMetrics metrics,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("event=outbox_publisher_disabled");
            return;
        }

        logger.LogInformation("event=outbox_publisher_started instance={InstanceId}", _instanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = 0;
            try
            {
                dispatched = await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A broker or database outage must not kill the loop; back off and retry.
                logger.LogError(ex, "event=outbox_batch_failed");
            }

            if (dispatched == 0)
            {
                try
                {
                    await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("event=outbox_publisher_stopped instance={InstanceId}", _instanceId);
    }

    /// <summary>Exposed for integration tests that need a deterministic single pass.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaPipelineDbContext>();
        var now = clock.UtcNow;

        var claimed = await ClaimAsync(db, now, cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return 0;
        }

        foreach (var message in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var activity = MediaPipelineTelemetry.ActivitySource.StartActivity("outbox.dispatch", System.Diagnostics.ActivityKind.Producer, message.TraceParent);
            var (exchange, routingKey) = RouteFor(message.MessageType);
            try
            {
                await publisher.PublishAsync(
                    exchange,
                    routingKey,
                    message.Id,
                    message.MessageType,
                    message.ContractVersion,
                    Encoding.UTF8.GetBytes(message.Payload),
                    message.TraceParent,
                    extraHeaders: null,
                    cancellationToken).ConfigureAwait(false);

                message.MarkPublished(clock.UtcNow);
                metrics.OutboxPublished.Add(1, new KeyValuePair<string, object?>("message_type", message.MessageType));
            }
            catch (Exception ex)
            {
                message.MarkAttemptFailed("Broker publish failed; inspect the correlated server log.", clock.UtcNow, _options.MaxAttempts, _options.BaseRetryDelay);
                metrics.OutboxFailures.Add(1, new KeyValuePair<string, object?>("message_type", message.MessageType));
                logger.LogWarning(ex,
                    "event=outbox_publish_failed message_id={MessageId} type={MessageType} attempt={AttemptCount} status={OutboxStatus}",
                    message.Id, message.MessageType, message.AttemptCount, message.Status);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return claimed.Count;
    }

    /// <summary>
    /// Claims a batch inside its own transaction. <c>SKIP LOCKED</c> means a second
    /// publisher sees the next unclaimed rows instead of blocking behind this one.
    /// <para>
    /// The transaction runs through the execution strategy because retries are enabled on
    /// the connection: EF refuses a user-initiated transaction outside one, since a retry
    /// would otherwise replay only part of the unit.
    /// </para>
    /// </summary>
    private async Task<List<OutboxMessage>> ClaimAsync(MediaPipelineDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var transactionScope = transaction.ConfigureAwait(false);

            var candidates = await db.OutboxMessages
                .FromSql($"""
                    SELECT * FROM outbox_messages
                    WHERE status = 'Pending'
                      AND next_attempt_at <= {now}
                      AND (locked_until IS NULL OR locked_until < {now})
                    ORDER BY occurred_at
                    LIMIT {_options.BatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                candidate.Lease(_instanceId, _options.LeaseDuration, now);
            }

            if (candidates.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return candidates;
        }).ConfigureAwait(false);
    }

    private static (string Exchange, string RoutingKey) RouteFor(string messageType) => messageType switch
    {
        MessageTypes.MediaProcessingRequested => (Topology.MediaExchange, Topology.MediaRoutingKey),
        MessageTypes.RealtimeNotification => (Topology.NotificationExchange, string.Empty),
        _ => throw new InvalidOperationException($"No route configured for message type '{messageType}'."),
    };
}
