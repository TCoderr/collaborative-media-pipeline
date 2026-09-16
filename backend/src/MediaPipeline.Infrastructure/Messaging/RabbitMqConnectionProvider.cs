using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MediaPipeline.Infrastructure.Messaging;

/// <summary>
/// Owns the single AMQP connection for a process and declares the topology once.
/// <para>
/// Connecting is lazy and failure here is never fatal at startup: a broker outage must
/// degrade processing, not stop the API from serving reads and accepting uploads.
/// </para>
/// </summary>
public sealed class RabbitMqConnectionProvider : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnectionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private bool _topologyDeclared;
    private bool _disposed;

    public RabbitMqConnectionProvider(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnectionProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
                _topologyDeclared = false;
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.UserName,
                Password = _options.Password,
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,
                NetworkRecoveryInterval = _options.RecoveryInterval,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
                ConsumerDispatchConcurrency = (ushort)_options.MaxConcurrentMessages,
                ClientProvidedName = $"collaborative-media-pipeline-{Environment.MachineName}",
            };

            if (_options.UseTls)
            {
                factory.Ssl = new SslOption { Enabled = true, ServerName = _options.HostName };
            }

            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "event=broker_connected host={BrokerHost} port={BrokerPort} vhost={VirtualHost}",
                _options.HostName, _options.Port, _options.VirtualHost);

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Declares exchanges, queues and bindings. Idempotent on the broker side, and guarded
    /// here so the declaration cost is paid once per process.
    /// </summary>
    public async Task EnsureTopologyAsync(CancellationToken cancellationToken)
    {
        if (_topologyDeclared)
        {
            return;
        }

        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var channelScope = channel.ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(Topology.MediaExchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(Topology.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(Topology.RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(Topology.NotificationExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Work queue: anything rejected without requeue lands on the dead-letter exchange.
        await channel.QueueDeclareAsync(
            Topology.MediaQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = Topology.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = Topology.DeadLetterRoutingKey,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(Topology.MediaQueue, Topology.MediaExchange, Topology.MediaRoutingKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(Topology.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(Topology.DeadLetterQueue, Topology.DeadLetterExchange, Topology.DeadLetterRoutingKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Retry tiers: a message parked here expires after the tier's TTL and is
        // dead-lettered straight back onto the work queue. No scheduler process needed.
        foreach (var tier in Topology.RetryTiers)
        {
            await channel.QueueDeclareAsync(
                tier.QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = (int)tier.Delay.TotalMilliseconds,
                    ["x-dead-letter-exchange"] = Topology.MediaExchange,
                    ["x-dead-letter-routing-key"] = Topology.MediaRoutingKey,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await channel.QueueBindAsync(tier.QueueName, Topology.RetryExchange, tier.RoutingKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        _topologyDeclared = true;
        _logger.LogInformation("event=broker_topology_declared exchange={Exchange} queue={Queue}",
            Topology.MediaExchange, Topology.MediaQueue);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_connection is not null)
        {
            try
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "event=broker_close_failed");
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
