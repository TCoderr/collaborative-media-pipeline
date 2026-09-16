using MediaPipeline.Application.Abstractions;
using MediaPipeline.Application.Assets;
using MediaPipeline.Application.Identity;
using MediaPipeline.Application.Media;
using MediaPipeline.Application.Observability;
using MediaPipeline.Application.Persistence;
using MediaPipeline.Application.Processing;
using MediaPipeline.Application.Projects;
using MediaPipeline.Application.Reviews;
using MediaPipeline.Infrastructure.Media;
using MediaPipeline.Infrastructure.Messaging;
using MediaPipeline.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MediaPipeline.Infrastructure;

/// <summary>Which object-storage provider a host should use.</summary>
public enum StorageProvider
{
    Local,
    AzureBlob,
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.Local;
}

/// <summary>
/// Composition root shared by the API and the worker. Both processes talk to the same
/// database, storage and broker, so their registrations belong in one place - a divergence
/// between them would be an outage waiting to happen.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMediaPipelineInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<MediaPipelineMetrics>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();

        services.AddOptions<MediaPolicyOptions>()
            .Bind(configuration.GetSection(MediaPolicyOptions.SectionName))
            .Validate(o => o.MaxUploadBytes is > 0 and <= 26214400 && o.MaxPixels is > 0 and <= 16000000 && o.MaxDimension is > 0 and <= 12000, "Media limits exceed the safe processing envelope.")
            .Validate(o => o.ProcessingTimeout > TimeSpan.Zero && o.ProcessingTimeout <= TimeSpan.FromSeconds(60), "Processing timeout must be between zero and 60 seconds.")
            .Validate(o => o.ThumbnailLongEdge is > 0 and <= 512 && o.PreviewLongEdge is > 0 and <= 1600 && o.WebpQuality is >= 1 and <= 100, "Invalid derivative policy.")
            .ValidateOnStart();

        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .Validate(o => o.PrefetchCount > 0, "RabbitMq:PrefetchCount must be greater than zero.")
            .Validate(o => o.MaxConcurrentMessages is > 0 and <= 2, "RabbitMq:MaxConcurrentMessages must be greater than zero.")
            .ValidateOnStart();

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .Validate(o => o.BatchSize is > 0 and <= 500, "Outbox:BatchSize must be between 1 and 500.")
            .ValidateOnStart();

        AddPersistence(services, configuration);
        AddStorage(services, configuration);
        AddMessaging(services);

        services.AddScoped<IMediaProcessor, SkiaMediaProcessor>();

        // Application services.
        services.AddScoped<ProjectAccessService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<AssetService>();
        services.AddScoped<ReviewService>();
        services.AddScoped<IdentityService>();
        services.AddScoped<MediaProcessingHandler>();
        services.AddScoped<ProcessingOperationsService>();
        services.AddScoped<ReconciliationService>();

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        // An empty string is as broken as a missing one, and fails much later and less
        // clearly, so both are rejected here at startup.
        var connectionString = configuration.GetConnectionString("MediaPipeline");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:MediaPipeline is not configured. Supply it through the environment "
                + "or Key Vault - it is never committed to source.");
        }

        services.AddDbContext<MediaPipelineDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                // Retry only transient faults; a unique-violation or concurrency conflict
                // must surface to the caller, not be silently replayed.
                npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                npgsql.CommandTimeout(30);

                // The DbContext lives in the application layer, but migrations are
                // provider-specific SQL, so they belong with the Npgsql wiring.
                npgsql.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName);
            });

            options.UseSnakeCaseNamingConventionSafe();
        });
    }

    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<LocalStorageOptions>(configuration.GetSection(LocalStorageOptions.SectionName));
        services.Configure<AzureBlobStorageOptions>(configuration.GetSection(AzureBlobStorageOptions.SectionName));

        var provider = configuration.GetSection(StorageOptions.SectionName).GetValue("Provider", StorageProvider.Local);

        if (provider == StorageProvider.AzureBlob)
        {
            services.AddSingleton<IObjectStorage, AzureBlobObjectStorage>();
        }
        else
        {
            services.AddSingleton<IObjectStorage, LocalFileSystemObjectStorage>();
        }
    }

    private static void AddMessaging(IServiceCollection services)
    {
        services.AddSingleton<RabbitMqConnectionProvider>();
        services.AddSingleton<RabbitMqPublisher>();
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<RabbitMqPublisher>());

        // Both hosts announce state changes: the API for review and comments, the worker
        // for processing outcomes. Registering it here keeps them from diverging.
        services.AddHostedService<MediaPipeline.Infrastructure.Observability.PipelineMetricsObserver>();
    }

    /// <summary>
    /// The naming convention is applied inside <see cref="MediaPipelineDbContext.OnModelCreating"/>;
    /// this hook exists so the intent is visible at the composition root too.
    /// </summary>
    private static DbContextOptionsBuilder UseSnakeCaseNamingConventionSafe(this DbContextOptionsBuilder builder) => builder;

    /// <summary>
    /// Convenience for hosts that need the concrete options instance during startup, before
    /// the container is built (health checks, migration jobs).
    /// </summary>
    public static T GetRequiredOptions<T>(this IServiceProvider services) where T : class =>
        services.GetRequiredService<IOptions<T>>().Value;
}
