using System.Net;
using System.Text.Json;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Observability;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Application.Processing;
using MediaPipeline.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MediaPipeline.Worker;

/// <summary>Readiness: can this instance reach the database it must write outcomes to?</summary>
public sealed class DatabaseHealthCheck(MediaPipelineDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            return await db.Database.CanConnectAsync(timeout.Token).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", ex);
        }
    }
}

/// <summary>Readiness: can this instance read and write the object store?</summary>
public sealed class StorageHealthCheck(IObjectStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            // A probe for a key that will never exist: proves reachability and credentials
            // without writing anything.
            await storage.ExistsAsync("projects/health/probe", timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Object storage is not reachable.", ex);
        }
    }
}

/// <summary>
/// Readiness: is the queue consumer actually attached?
/// <para>
/// Reported as Degraded rather than Unhealthy on purpose. A broker blip should not make the
/// platform restart a process that is otherwise fine and will reconnect on its own - restart
/// loops during a broker outage make the outage worse.
/// </para>
/// </summary>
public sealed class ConsumerHealthCheck(MediaProcessingConsumer consumer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(consumer.IsConsuming
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Not currently consuming from RabbitMQ; reconnect is in progress."));
}

/// <summary>
/// Periodic maintenance the pipeline needs to stay honest: expired processing claims are
/// released so a worker that died mid-flight does not block a redelivery forever, and the
/// pipeline gauges are refreshed.
/// </summary>
public sealed class ProcessingHousekeepingService(
    IServiceScopeFactory scopeFactory,
    MediaPipelineMetrics metrics,
    IClock clock,
    ILogger<ProcessingHousekeepingService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MediaPipelineDbContext>();
                var now = clock.UtcNow;

                var released = await db.ProcessingJobs
                    .Where(j => j.State == Domain.Assets.ProcessingState.Processing
                        && j.ClaimExpiresAt != null && j.ClaimExpiresAt < now)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(j => j.ClaimedBy, (string?)null)
                              .SetProperty(j => j.ClaimExpiresAt, (DateTimeOffset?)null),
                        stoppingToken).ConfigureAwait(false);

                if (released > 0)
                {
                    logger.LogWarning("event=expired_claims_released count={ReleasedCount}", released);
                }

                var health = await scope.ServiceProvider.GetRequiredService<ProcessingOperationsService>()
                    .GetPipelineHealthAsync(stoppingToken).ConfigureAwait(false);

                logger.LogInformation(
                    "event=pipeline_health outbox_pending={OutboxPending} outbox_oldest_age_s={OutboxOldestAge} " +
                    "jobs_queued={JobsQueued} jobs_processing={JobsProcessing} jobs_failed={JobsFailed} jobs_dead={JobsDead}",
                    health.OutboxPending, (int)health.OutboxOldestAgeSeconds, health.JobsQueued,
                    health.JobsProcessing, health.JobsFailed, health.JobsDeadLettered);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "event=housekeeping_failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _ = metrics;
    }
}

/// <summary>
/// Minimal HTTP listener exposing <c>/health/live</c> and <c>/health/ready</c> so Container
/// Apps can probe a process that otherwise speaks only AMQP.
/// </summary>
public sealed class WorkerProbeServer(
    HealthCheckService healthChecks,
    IConfiguration configuration,
    ILogger<WorkerProbeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = configuration.GetValue("Worker:ProbePort", 8081);

        // Container Apps probes the pod's own address, not "localhost", so the wildcard
        // prefix is the one that matters in production. Windows requires an elevated URL
        // reservation for it, so a developer machine falls back to a loopback binding
        // rather than running without probes at all.
        //
        // A fresh listener per attempt: a failed Start leaves the instance unusable.
        var listener = TryBind(port, [$"http://+:{port}/", $"http://127.0.0.1:{port}/"], logger);
        if (listener is null)
        {
            // Losing probes is bad, but it must not stop the worker from doing its job.
            logger.LogError("event=probe_listener_unavailable port={ProbePort}", port);
            return;
        }

        using var owned = listener;
        using var registration = stoppingToken.Register(listener.Stop);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            await HandleAsync(context, stoppingToken).ConfigureAwait(false);
        }
    }

    private static HttpListener? TryBind(int port, string[] prefixes, ILogger logger)
    {
        foreach (var prefix in prefixes)
        {
            var candidate = new HttpListener();
            candidate.Prefixes.Add(prefix);

            try
            {
                candidate.Start();
                logger.LogInformation("event=probe_listener_started prefix={ProbePrefix}", prefix);
                return candidate;
            }
            catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException)
            {
                logger.LogWarning("event=probe_listener_bind_failed prefix={ProbePrefix} reason={Reason}",
                    prefix, ex.Message);
                candidate.Close();
            }
        }

        _ = port;
        return null;
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (path == "/health/live")
            {
                await WriteAsync(context, 200, new { status = "Healthy" }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/health/ready")
            {
                var report = await healthChecks.CheckHealthAsync(r => r.Tags.Contains("ready"), cancellationToken)
                    .ConfigureAwait(false);

                var status = report.Status == HealthStatus.Healthy ? 200 : 503;
                await WriteAsync(context, status, new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, 404, new { code = "NOT_FOUND" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "event=probe_request_failed");
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, object payload, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        context.Response.Close();
    }
}
