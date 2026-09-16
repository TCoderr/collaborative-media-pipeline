using MediaPipeline.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MediaPipeline.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time.
/// <para>
/// The connection string comes from the environment, never from a checked-in file, so
/// scaffolding a migration cannot leak a credential into the repository - and cannot
/// accidentally point at a real database because someone left a default in source.
/// </para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MediaPipelineDbContext>
{
    public MediaPipelineDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("MEDIA_PIPELINE_CONNECTIONSTRING")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__MediaPipeline")
            ?? throw new InvalidOperationException(
                "Set MEDIA_PIPELINE_CONNECTIONSTRING before running dotnet-ef. See docs/RUNBOOK.md.");

        var options = new DbContextOptionsBuilder<MediaPipelineDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.FullName))
            .Options;

        return new MediaPipelineDbContext(options);
    }
}
