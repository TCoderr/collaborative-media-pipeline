using System.Text.Json;
using MediaPipeline.Api.Realtime;
using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Application.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MediaPipeline.Api.Health;

/// <summary>
/// Liveness and readiness, kept genuinely distinct.
/// <para>
/// <b>Liveness</b> asks only whether the process is running - it must never depend on a
/// downstream service, or a database blip would trigger a restart storm that makes the
/// outage worse.
/// </para>
/// <para>
/// <b>Readiness</b> asks whether this instance can serve traffic correctly. PostgreSQL and
/// object storage are required, because without them almost every request fails. RabbitMQ is
/// deliberately <em>not</em>: the transactional outbox absorbs a broker outage, so the API can
/// still authenticate, read, and accept uploads while processing is delayed. Marking the API
/// unready during a broker outage would turn a degraded feature into a total one.
/// </para>
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false, // process is up; that is the entire question
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = WriteReportAsync,
        }).AllowAnonymous();

        app.MapHealthChecks("/api/v1/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/api/v1/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        { Predicate = r => r.Tags.Contains("ready"), ResponseWriter = WriteReportAsync }).AllowAnonymous();
        // Full picture including the broker, for humans and dashboards rather than probes.
        app.MapHealthChecks("/health/diagnostic", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => true,
            ResponseWriter = WriteReportAsync,
        }).AllowAnonymous();
    }

    private static async Task WriteReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description }),
        }, Http.JsonDefaults.Options)).ConfigureAwait(false);
    }
}

public sealed class DatabaseReadinessCheck(MediaPipelineDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            // A real round-trip, not just a pool check: a connection that cannot execute is
            // not a connection worth being ready on.
            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token).ConfigureAwait(false);
            if ((await db.Database.GetPendingMigrationsAsync(timeout.Token).ConfigureAwait(false)).Any())
                return HealthCheckResult.Unhealthy("Database migrations are pending.");
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", ex);
        }
    }
}

public sealed class StorageReadinessCheck(IObjectStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

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
/// Broker reachability, reported as Degraded. Real-time fan-out and job dispatch are affected
/// by an outage; correctness is not, because the outbox holds the work until the broker returns.
/// </summary>
public sealed class BrokerLivenessCheck(RealtimeNotificationConsumer consumer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(consumer.IsConsuming
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(
                "Not connected to RabbitMQ. Uploads still succeed; processing and live updates are delayed."));
}

/// <summary>
/// Operational visibility into the asynchronous pipeline: outbox depth, oldest pending
/// message, and job counts by state. Authenticated, because it describes system load.
/// </summary>
public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/operations/pipeline", async (
            ProcessingOperationsService processing,
            CancellationToken cancellationToken) =>
            Results.Ok(await processing.GetPipelineHealthAsync(cancellationToken).ConfigureAwait(false)))
        .WithTags("Operations")
        .WithSummary("Outbox depth, oldest pending message age, and processing job counts by state.")
        .RequireAuthorization();
    }
}
