using System.Diagnostics.Metrics;
using MediaPipeline.Application.Observability;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaPipeline.Infrastructure.Observability;

/// <summary>Only emits gauges after a successful live query. Failed observations never become fabricated zeroes.</summary>
public sealed class PipelineMetricsObserver(IServiceScopeFactory scopes, RabbitMqConnectionProvider broker,
    ILogger<PipelineMetricsObserver> logger) : BackgroundService
{
    private readonly Meter _meter = new(MediaPipelineTelemetry.MeterName);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reviews = _meter.CreateGauge<long>("quire.review.active");
        var quarantine = _meter.CreateGauge<long>("quire.processing.quarantined");
        var retries = _meter.CreateGauge<long>("quire.processing.retries.total");
        var outbox = _meter.CreateGauge<long>("quire.outbox.pending");
        var queue = _meter.CreateGauge<long>("quire.rabbitmq.queue.depth");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MediaPipelineDbContext>();
                reviews.Record(await db.AssetVersions.CountAsync(v => v.ReviewState == ReviewState.InReview, stoppingToken).ConfigureAwait(false));
                quarantine.Record(await db.AssetVersions.CountAsync(v => v.LifecycleState == VersionLifecycleState.Quarantined, stoppingToken).ConfigureAwait(false));
                retries.Record(await db.ProcessingJobs.SumAsync(j => (long)Math.Max(j.AttemptCount - 1, 0), stoppingToken).ConfigureAwait(false));
                outbox.Record(await db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Pending, stoppingToken).ConfigureAwait(false));
                var connection = await broker.GetConnectionAsync(stoppingToken).ConfigureAwait(false);
                await broker.EnsureTopologyAsync(stoppingToken).ConfigureAwait(false);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                queue.Record(await channel.MessageCountAsync(Topology.MediaQueue, stoppingToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "event=metrics_observation_failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public override void Dispose() { _meter.Dispose(); base.Dispose(); }
}
