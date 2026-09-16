using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MediaPipeline.Application.Observability;

/// <summary>Activity and meter names, registered with OpenTelemetry by both hosts.</summary>
public static class MediaPipelineTelemetry
{
    public const string ServiceNamespace = "collaborative-media-pipeline";
    public const string ActivitySourceName = "MediaPipeline";
    public const string MeterName = "MediaPipeline";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
}

/// <summary>
/// The instrument set. Deliberately small and low-cardinality: no asset, user, message or
/// file name ever becomes a label, because each unique value is a new time series.
/// </summary>
public sealed class MediaPipelineMetrics : IDisposable
{
    private readonly Meter _meter;

    public MediaPipelineMetrics()
    {
        _meter = new Meter(MediaPipelineTelemetry.MeterName, "1.0.0");

        ApprovalLatency = _meter.CreateHistogram<double>("quire.approval.latency", unit: "s", description: "Time from successful processing to approval.");
        ProcessingJobsStarted = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.processing.jobs.started", unit: "{job}", description: "Media processing attempts started.");
        ProcessingJobsSucceeded = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.processing.jobs.succeeded", unit: "{job}", description: "Media processing attempts that produced derivatives.");
        ProcessingJobsFailed = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.processing.jobs.failed", unit: "{job}", description: "Media processing attempts that failed, labelled by failure kind.");
        ProcessingDuration = _meter.CreateHistogram<double>(
            "collaborative-media-pipeline.processing.duration", unit: "ms", description: "Wall-clock duration of a media processing attempt.");
        DuplicateMessagesSkipped = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.processing.duplicates.skipped", unit: "{message}", description: "Redelivered messages recognised by the inbox ledger.");

        RabbitPublished = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.rabbitmq.published", unit: "{message}", description: "Messages confirmed by the broker.");
        RabbitPublishFailures = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.rabbitmq.publish.failures", unit: "{message}", description: "Publishes that failed or were not confirmed.");
        RabbitConsumerFailures = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.rabbitmq.consumer.failures", unit: "{message}", description: "Message handlers that threw.");

        OutboxPublished = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.outbox.published", unit: "{message}", description: "Outbox rows successfully handed to the broker.");
        OutboxFailures = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.outbox.failures", unit: "{message}", description: "Outbox publish attempts that failed.");

        StorageOperationDuration = _meter.CreateHistogram<double>(
            "collaborative-media-pipeline.storage.operation.duration", unit: "ms", description: "Object storage operation latency by operation.");

        RealtimeEventsBroadcast = _meter.CreateCounter<long>(
            "collaborative-media-pipeline.realtime.events.broadcast", unit: "{event}", description: "Real-time events fanned out to SignalR groups.");
    }

    public Histogram<double> ApprovalLatency { get; }
    public Counter<long> ProcessingJobsStarted { get; }
    public Counter<long> ProcessingJobsSucceeded { get; }
    public Counter<long> ProcessingJobsFailed { get; }
    public Histogram<double> ProcessingDuration { get; }
    public Counter<long> DuplicateMessagesSkipped { get; }
    public Counter<long> RabbitPublished { get; }
    public Counter<long> RabbitPublishFailures { get; }
    public Counter<long> RabbitConsumerFailures { get; }
    public Counter<long> OutboxPublished { get; }
    public Counter<long> OutboxFailures { get; }
    public Histogram<double> StorageOperationDuration { get; }
    public Counter<long> RealtimeEventsBroadcast { get; }

    /// <summary>
    /// Gauges observed on scrape rather than pushed, so a slow database cannot stall the
    /// request path. Wired up by the host that owns a database connection.
    /// </summary>
    public void ObserveOutboxDepth(Func<IEnumerable<Measurement<long>>> pendingObserver, Func<IEnumerable<Measurement<double>>> ageObserver)
    {
        _meter.CreateObservableGauge("collaborative-media-pipeline.outbox.pending", pendingObserver, unit: "{message}",
            description: "Outbox rows awaiting publication.");
        _meter.CreateObservableGauge("collaborative-media-pipeline.outbox.oldest.age", ageObserver, unit: "s",
            description: "Age of the oldest unpublished outbox row.");
    }

    public void ObserveProcessingBacklog(Func<IEnumerable<Measurement<long>>> observer) =>
        _meter.CreateObservableGauge("collaborative-media-pipeline.processing.backlog", observer, unit: "{job}",
            description: "Processing jobs not yet in a terminal state, by state.");

    public void Dispose() => _meter.Dispose();
}
