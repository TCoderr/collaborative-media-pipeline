using System.Text.Json;
using System.Text;
using System.Diagnostics;
using MediaPipeline.Application.Observability;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Application.Projects;
using MediaPipeline.Contracts;
using MediaPipeline.Infrastructure.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MediaPipeline.Api.Realtime;

/// <summary>
/// Receives the fan-out of real-time notifications and pushes them into this replica's
/// SignalR groups.
/// <para>
/// The queue is exclusive and auto-delete: it belongs to this process only, and vanishes when
/// the process does. Notifications are ephemeral by design - there is nothing to replay,
/// because PostgreSQL, not the socket, is the source of truth.
/// </para>
/// </summary>
public sealed class RealtimeNotificationConsumer(
    RabbitMqConnectionProvider connections,
    IHubContext<CollaborationHub, ICollaborationClient> hub,
    IServiceScopeFactory scopeFactory,
    ProjectSubscriptions subscriptions,
    MediaPipelineMetrics metrics,
    ILogger<RealtimeNotificationConsumer> logger) : BackgroundService
{
    private IChannel? _channel;

    /// <summary>Whether this replica is currently receiving fan-out. Diagnostics only.</summary>
    public bool IsConsuming { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                IsConsuming = false;
                logger.LogWarning(ex, "event=realtime_consumer_failed");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
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
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var connection = await connections.GetConnectionAsync(stoppingToken).ConfigureAwait(false);
        await connections.EnsureTopologyAsync(stoppingToken).ConfigureAwait(false);

        _channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);

        var queue = await _channel.QueueDeclareAsync(
            queue: string.Empty,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        await _channel.QueueBindAsync(queue.QueueName, Topology.NotificationExchange, routingKey: string.Empty,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, args) => await OnNotificationAsync(args, stoppingToken).ConfigureAwait(false);

        // autoAck is correct here and nowhere else: losing an ephemeral notification is
        // acceptable, and redelivering one would only duplicate a live update.
        await _channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer: consumer, cancellationToken: stoppingToken)
            .ConfigureAwait(false);

        IsConsuming = true;
        logger.LogInformation("event=realtime_consumer_started queue={Queue}", queue.QueueName);

        while (!stoppingToken.IsCancellationRequested && _channel.IsOpen)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }

        IsConsuming = false;
    }

    private async Task OnNotificationAsync(BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Body.Length > 64 * 1024)
            {
                logger.LogWarning("event=realtime_payload_oversized bytes={ByteSize}", args.Body.Length);
                return;
            }

            string? parent = null;
            if (args.BasicProperties.Headers?.TryGetValue(MessageTypes.Headers.TraceParent, out var raw) == true)
                parent = raw is byte[] bytes ? Encoding.UTF8.GetString(bytes) : raw?.ToString();
            using var activity = MediaPipelineTelemetry.ActivitySource.StartActivity("signalr.update", ActivityKind.Consumer, parent);
            var notification = JsonSerializer.Deserialize<RealtimeNotificationV1>(args.Body.Span, RealtimeJson.Options);
            if (notification is null)
            {
                return;
            }

            // Resolve the project's internal id so the group name is built from server data,
            // exactly as the hub does when a client subscribes.
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaPipelineDbContext>();

            if (!PublicRefGuard.IsProject(notification.ProjectRef))
            {
                return;
            }

            var projectId = await db.Projects.AsNoTracking()
                .Where(p => p.PublicRef == notification.ProjectRef)
                .Select(p => (Guid?)p.Id)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (projectId is null)
            {
                return;
            }

            var envelope = new RealtimeEnvelope(
                notification.EventType,
                notification.ProjectRef,
                notification.StateSequence,
                notification.OccurredAt,
                notification.Payload);

            var members = (await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == projectId.Value)
                .Select(m => m.UserId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            await hub.Clients.Clients(subscriptions.AuthorisedConnections(projectId.Value, members))
                .ProjectEvent(envelope).ConfigureAwait(false);
            metrics.RealtimeEventsBroadcast.Add(1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "event=realtime_dispatch_failed");
        }
    }
}
