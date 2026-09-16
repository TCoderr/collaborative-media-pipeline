using MediaPipeline.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MediaPipeline.Infrastructure.Observability;

/// <summary>
/// Wires the three pillars once for both hosts.
/// <para>
/// Instrumentation is vendor-neutral OpenTelemetry with an OTLP exporter. Nothing here
/// references Azure Monitor: production points the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
/// at a collector, so swapping the backend is a configuration change rather than a code change.
/// </para>
/// </summary>
public static class TelemetrySetup
{
    public static IHostApplicationBuilder AddMediaPipelineTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        var version = typeof(TelemetrySetup).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName, MediaPipelineTelemetry.ServiceNamespace, version, autoGenerateServiceInstanceId: true)
            .AddAttributes(
            [
                new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName),
            ]);

        // JSON to stdout: the container platform collects it, and every line already carries
        // the trace id because the OTel logging provider stamps it.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";
            options.UseUtcTimestamp = true;
        });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                logging.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            }
        });

        var otel = builder.Services.AddOpenTelemetry().ConfigureResource(r => r
            .AddService(serviceName, MediaPipelineTelemetry.ServiceNamespace, version, autoGenerateServiceInstanceId: true));

        otel.WithTracing(tracing =>
        {
            tracing
                .AddSource(MediaPipelineTelemetry.ActivitySourceName)
                .AddSource("Npgsql")
                .AddHttpClientInstrumentation();

            if (builder.Environment.IsDevelopment())
            {
                // Sample everything locally; production relies on the collector's policy.
                tracing.SetSampler(new AlwaysOnSampler());
            }

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics
                .AddMeter(MediaPipelineTelemetry.MeterName)
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation();

            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            }
        });

        return builder;
    }
}
