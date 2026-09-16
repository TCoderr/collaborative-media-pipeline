using MediaPipeline.Infrastructure;
using MediaPipeline.Infrastructure.Messaging;
using MediaPipeline.Infrastructure.Observability;
using MediaPipeline.Worker;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// ─────────────────────────────────────────────────────────────────────────────
// Quire media worker.
//
// A second deployable process, not a second service: it shares the domain, the
// database and the storage abstraction with the API, and differs only in what
// drives it - a RabbitMQ queue instead of HTTP. Splitting it out is justified by
// its resource profile (CPU- and memory-heavy image decoding) and its scaling
// signal (queue depth), both of which differ sharply from the API's.
// ─────────────────────────────────────────────────────────────────────────────

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try { return (await probe.GetAsync("http://127.0.0.1:8081/health/live").ConfigureAwait(false)).IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { return 1; }
}
var builder = Host.CreateApplicationBuilder(args);

builder.AddMediaPipelineTelemetry("collaborative-media-pipeline-worker");
builder.Services.AddMediaPipelineInfrastructure(builder.Configuration);

// The same image also runs the operator commands, so reconciliation and pipeline
// inspection execute against the exact configuration the pipeline itself uses.
if (OperationsCommands.IsCommand(args))
{
    using var commandHost = builder.Build();
    return await OperationsCommands.RunAsync(args, commandHost.Services, CancellationToken.None)
        .ConfigureAwait(false);
}

// The worker consumes media jobs and also drains the outbox, so a retry enqueued
// by the worker itself still reaches the broker if the API is unavailable.
builder.Services.AddSingleton<MediaProcessingConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaProcessingConsumer>());
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<ProcessingHousekeepingService>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<StorageHealthCheck>("storage", tags: ["ready"])
    .AddCheck<ConsumerHealthCheck>("broker-consumer", failureStatus: HealthStatus.Degraded, tags: ["ready"]);

// A tiny HTTP surface for container liveness/readiness probes. The worker is not an
// API; this listener exists only so the platform can tell a wedged process from a busy one.
builder.Services.AddSingleton<WorkerProbeServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerProbeServer>());

var host = builder.Build();

// Give the process time to finish an in-flight image before the platform kills it.
host.Services.GetRequiredService<IHostApplicationLifetime>();

await host.RunAsync().ConfigureAwait(false);
return 0;

namespace MediaPipeline.Worker
{
    /// <summary>Entry-point marker so the integration tests can reference the worker assembly.</summary>
    public sealed partial class WorkerProgram;
}
