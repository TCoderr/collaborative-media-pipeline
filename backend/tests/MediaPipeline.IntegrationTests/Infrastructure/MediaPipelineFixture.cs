using System.Net.Http.Json;
using System.Text.Json;
using MediaPipeline.Application.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using Xunit;

namespace MediaPipeline.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API against real PostgreSQL and real RabbitMQ.
/// <para>
/// No in-memory database and no fake broker: the behaviours under test here - unique-index
/// contention, <c>xmin</c> concurrency, <c>SKIP LOCKED</c> claiming, at-least-once delivery,
/// dead-lettering - only exist in the real engines. A test double would assert the double's
/// behaviour, not the system's.
/// </para>
/// <para>
/// Each run gets its own database. Supply an isolated broker virtual host for concurrent
/// runs; the suite serialises transport tests within that host.
/// </para>
/// </summary>
public sealed class MediaPipelineFixture : WebApplicationFactory<MediaPipeline.Api.ApiEntryPoint>, IAsyncLifetime
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..8];

    private readonly string _databaseName = $"collaborative-media-pipeline_it_{RunId}";
    private string _adminConnectionString = string.Empty;
    private string _storageRoot = string.Empty;

    public string ConnectionString { get; private set; } = string.Empty;

    public static TestBrokerSettings Broker { get; } = TestBrokerSettings.FromEnvironment();

    public async Task InitializeAsync()
    {
        var template = Environment.GetEnvironmentVariable("MEDIA_PIPELINE_TEST_POSTGRES")
            ?? "Host=127.0.0.1;Port=55432;Database=collaborative-media-pipeline_test;Username=collaborative-media-pipeline;Password=collaborative-media-pipeline_local_dev_pw";

        var builder = new NpgsqlConnectionStringBuilder(template);
        _adminConnectionString = new NpgsqlConnectionStringBuilder(template) { Database = "postgres" }.ToString();
        builder.Database = _databaseName;
        ConnectionString = builder.ToString();

        await CreateDatabaseAsync().ConfigureAwait(false);

        _storageRoot = Path.Combine(Path.GetTempPath(), "collaborative-media-pipeline-it", RunId);
        Directory.CreateDirectory(_storageRoot);

        // Force the host to build now so migrations run before the first test.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaPipelineDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // UseSetting, not ConfigureAppConfiguration: with minimal hosting the entry point
        // reads builder.Configuration while registering services, which happens before a
        // deferred configuration callback would run. UseSetting lands in host configuration
        // early enough for the real Program.cs to see it.
        foreach (var (key, value) in TestSettings())
        {
            builder.UseSetting(key, value);
        }
    }

    private Dictionary<string, string?> TestSettings() => new()
    {
        ["ConnectionStrings:MediaPipeline"] = ConnectionString,
        ["Storage:Provider"] = "Local",
        ["Storage:Local:RootPath"] = _storageRoot,
        ["RabbitMq:HostName"] = Broker.HostName,
        ["RabbitMq:Port"] = Broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["RabbitMq:VirtualHost"] = Broker.VirtualHost,
        ["RabbitMq:UserName"] = Broker.UserName,
        ["RabbitMq:Password"] = Broker.Password,
        ["RabbitMq:PrefetchCount"] = "4",
        ["RabbitMq:MaxConcurrentMessages"] = "2",

        // Tests drive the outbox explicitly so assertions are deterministic rather than
        // racing a background timer.
        ["Outbox:Enabled"] = "false",
        ["Outbox:BatchSize"] = "500",
        ["Media:MaxUploadBytes"] = "5242880",

        // The suite runs from a single client address; production limits would throttle
        // the tests rather than the threat they exist for. Limiter behaviour itself is
        // covered by a dedicated test that lowers them again.
        ["RateLimits:AuthPerMinute"] = "10000",
        ["RateLimits:UploadPerMinute"] = "10000",
        ["RateLimits:GlobalPerMinute"] = "100000",
        ["OpenTelemetry:OtlpEndpoint"] = "",
        ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Information",
    };

    public async Task ReadyAsync(string versionRef)
    {
        using var scope = NewScope();
        var json = await scope.Db.OutboxMessages.Where(m => m.SubjectRef == versionRef && m.MessageType == MediaPipeline.Contracts.MessageTypes.MediaProcessingRequested)
            .OrderByDescending(m => m.OccurredAt).Select(m => m.Payload).FirstAsync();
        var message = JsonSerializer.Deserialize<MediaPipeline.Contracts.MediaProcessingRequestedV1>(json)!;
        var outcome = await scope.Get<MediaPipeline.Application.Processing.MediaProcessingHandler>().HandleAsync(message, "fixture-processor", CancellationToken.None);
        Assert.Equal(MediaPipeline.Application.Processing.HandlerOutcome.Complete, outcome.Outcome);
    }

    public static async Task<HttpResponseMessage> ReviewAsync(TestUser user, string versionRef, string target)
    {
        var snapshot = await user.Client.GetAsync(new Uri($"/api/v1/versions/{versionRef}", UriKind.Relative));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/versions/{versionRef}/review") { Content = JsonContent.Create(new { reviewState = target }) };
        request.Headers.TryAddWithoutValidation("If-Match", snapshot.Headers.ETag?.ToString());
        return await user.Client.SendAsync(request);
    }

    public async Task OpenReviewAsync(TestUser user, string versionRef)
    {
        await ReadyAsync(versionRef);
        (await ReviewAsync(user, versionRef, "InReview")).EnsureSuccessStatusCode();
    }

    public async Task DispatchAsync()
    {
        using var scope = NewScope();
        var publisher = ActivatorUtilities.CreateInstance<MediaPipeline.Infrastructure.Messaging.OutboxPublisher>(scope.Services);
        while (await publisher.DispatchBatchAsync(CancellationToken.None) > 0) { }
    }

    /// <summary>A scope plus the services a test usually needs from it.</summary>
    public TestScope NewScope() => new(Services.CreateScope());

    /// <summary>
    /// A client that manages its own cookie header rather than relying on the factory's
    /// hidden container. The SignalR tests need the same cookies on the WebSocket upgrade,
    /// so the session has to be something a test can hold and pass along.
    /// </summary>
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    /// <summary>Registers a user and returns a client whose headers carry their session.</summary>
    public async Task<TestUser> RegisterAsync(string? email = null, string? displayName = null)
    {
        var client = NewClient();
        var address = email ?? $"user-{Guid.NewGuid():N}@example.test";
        const string Password = "correct horse battery staple";

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        var (token, headerName) = await PrimeAntiforgeryAsync(client, cookies).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new
            {
                email = address,
                displayName = displayName ?? "Test Collaborator",
                password = Password,
            }),
        };
        request.Headers.Add(headerName, token);
        request.Headers.Add("Cookie", Format(cookies));

        var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        Capture(response, cookies);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);

        // The antiforgery token is bound to the authenticated identity, so a fresh one is
        // needed now that a session exists.
        var (freshToken, _) = await PrimeAntiforgeryAsync(client, cookies).ConfigureAwait(false);

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", Format(cookies));
        client.DefaultRequestHeaders.Remove(headerName);
        client.DefaultRequestHeaders.Add(headerName, freshToken);

        return new TestUser(client, body.GetProperty("userRef").GetString()!, address, Password, Format(cookies));
    }

    private static async Task<(string Token, string HeaderName)> PrimeAntiforgeryAsync(
        HttpClient client,
        Dictionary<string, string> cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf");
        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", Format(cookies));
        }

        var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Capture(response, cookies);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        return (body.GetProperty("token").GetString()!, body.GetProperty("headerName").GetString()!);
    }

    private static void Capture(HttpResponseMessage response, Dictionary<string, string> cookies)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            var pair = value.Split(';', 2)[0];
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                cookies[pair[..separator]] = pair[(separator + 1)..];
            }
        }
    }

    private static string Format(Dictionary<string, string> cookies) =>
        string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}"));

    private async Task CreateDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", conn);
        await drop.ExecuteNonQueryAsync().ConfigureAwait(false);

        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", conn);
        await create.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);

        NpgsqlConnection.ClearAllPools();

        try
        {
            await using var conn = new NpgsqlConnection(_adminConnectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Leaving a scratch database behind is not worth failing a suite over.
        }

        try
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Connection details for the local RabbitMQ the integration tests require.</summary>
public sealed record TestBrokerSettings(string HostName, int Port, string VirtualHost, string UserName, string Password)
{
    public static TestBrokerSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("MEDIA_PIPELINE_TEST_RABBITMQ_HOST") ?? "127.0.0.1",
        int.TryParse(Environment.GetEnvironmentVariable("MEDIA_PIPELINE_TEST_RABBITMQ_PORT"), out var port) ? port : 55672,
        Environment.GetEnvironmentVariable("MEDIA_PIPELINE_TEST_RABBITMQ_VHOST") ?? "collaborative-media-pipeline",
        Environment.GetEnvironmentVariable("MEDIA_PIPELINE_TEST_RABBITMQ_USER") ?? "collaborative-media-pipeline",
        Environment.GetEnvironmentVariable("MEDIA_PIPELINE_TEST_RABBITMQ_PASSWORD") ?? "collaborative-media-pipeline_local_dev_pw");

    public ConnectionFactory ToFactory() => new()
    {
        HostName = HostName,
        Port = Port,
        VirtualHost = VirtualHost,
        UserName = UserName,
        Password = Password,
    };
}

/// <summary>
/// A signed-in test user: an HTTP client with the session pinned to its default headers,
/// plus the raw cookie header so a SignalR connection can present the same session.
/// </summary>
public sealed record TestUser(HttpClient Client, string UserRef, string Email, string Password, string CookieHeader);

/// <summary>Disposable DI scope exposing the services tests reach for most often.</summary>
public sealed class TestScope(IServiceScope scope) : IDisposable
{
    public IServiceProvider Services => scope.ServiceProvider;

    public MediaPipelineDbContext Db => scope.ServiceProvider.GetRequiredService<MediaPipelineDbContext>();

    public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose() => scope.Dispose();
}

[CollectionDefinition(Name)]
public sealed class MediaPipelineTestGroup : ICollectionFixture<MediaPipelineFixture>
{
    public const string Name = "collaborative-media-pipeline";
}
