using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Identity;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Domain.Projects;
using MediaPipeline.Domain.Reviews;
using Microsoft.EntityFrameworkCore;

namespace MediaPipeline.Application.Persistence;

/// <summary>
/// The single write model. Quire is a modular monolith: one schema, one transaction
/// boundary, module separation enforced by aggregates rather than by separate databases.
/// </summary>
public sealed partial class MediaPipelineDbContext(DbContextOptions<MediaPipelineDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetVersion> AssetVersions => Set<AssetVersion>();
    public DbSet<AssetDerivative> AssetDerivatives => Set<AssetDerivative>();
    public DbSet<ReviewComment> ReviewComments => Set<ReviewComment>();
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<StateHistory> StateHistory => Set<StateHistory>();
    public Guid? ActorUserId { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Enums are stored as text (see the per-entity configurations): readable in psql,
        // and adding a member later cannot silently renumber existing rows.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MediaPipelineDbContext).Assembly);

        // Every aggregate mints its own UUIDv7 in the domain, so EF must not treat a
        // GUID key as store-generated.
        //
        // This is not cosmetic. With the default convention EF reads "key already set" on a
        // child added to a *tracked* parent as "this row exists", turns the INSERT into an
        // UPDATE, and the update matches nothing - surfacing as a concurrency exception on
        // a plain create. Declaring the keys app-assigned is what makes adding a member to
        // a loaded project an insert.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var key in entity.GetProperties().Where(p => p.IsPrimaryKey() && p.ClrType == typeof(Guid)))
            {
                key.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }

        modelBuilder.ApplySnakeCaseNames();
    }
}
