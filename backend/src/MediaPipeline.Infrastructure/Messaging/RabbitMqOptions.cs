namespace MediaPipeline.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "guest";

    /// <summary>Injected from the environment or Key Vault. Never committed.</summary>
    public string Password { get; set; } = "guest";

    public bool UseTls { get; set; }

    /// <summary>
    /// Unacked messages a single consumer may hold. Media processing is CPU- and
    /// memory-hungry, so this is deliberately small: a large prefetch would let one
    /// worker hoard the queue while its siblings idle.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 4;

    /// <summary>Concurrent handlers inside one worker process. Bounded by a semaphore.</summary>
    public int MaxConcurrentMessages { get; set; } = 2;

    public TimeSpan PublishConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The broker topology, named in one place so the API, the worker and the operational
/// tooling cannot drift apart.
/// </summary>
public static class Topology
{
    /// <summary>Direct exchange carrying media work.</summary>
    public const string MediaExchange = "collaborative-media-pipeline.media";

    public const string MediaRoutingKey = "media.process";
    public const string MediaQueue = "collaborative-media-pipeline.media.process";

    /// <summary>Where exhausted or poison messages come to rest.</summary>
    public const string DeadLetterExchange = "collaborative-media-pipeline.media.dlx";
    public const string DeadLetterRoutingKey = "media.process.dead";
    public const string DeadLetterQueue = "collaborative-media-pipeline.media.dead";

    /// <summary>
    /// Retry tiers. Fixed per-queue TTLs rather than per-message expiry, because a
    /// per-message TTL on a shared queue lets one long delay block shorter ones behind it.
    /// </summary>
    public const string RetryExchange = "collaborative-media-pipeline.media.retry";

    public static readonly IReadOnlyList<RetryTier> RetryTiers =
    [
        new("collaborative-media-pipeline.media.retry.5s", "media.retry.5s", TimeSpan.FromSeconds(5)),
        new("collaborative-media-pipeline.media.retry.30s", "media.retry.30s", TimeSpan.FromSeconds(30)),
        new("collaborative-media-pipeline.media.retry.2m", "media.retry.2m", TimeSpan.FromMinutes(2)),
    ];

    /// <summary>Fan-out of real-time notifications to every API replica.</summary>
    public const string NotificationExchange = "collaborative-media-pipeline.notifications";

    public static RetryTier TierForAttempt(int attemptCount)
    {
        var index = Math.Clamp(attemptCount - 1, 0, RetryTiers.Count - 1);
        return RetryTiers[index];
    }
}

public sealed record RetryTier(string QueueName, string RoutingKey, TimeSpan Delay);
