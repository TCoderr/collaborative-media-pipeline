using MediaPipeline.Domain.Assets;
using MediaPipeline.Domain.Identity;
using MediaPipeline.Domain.Processing;
using MediaPipeline.Domain.Projects;
using MediaPipeline.Domain.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MediaPipeline.Application.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.PublicRef).HasMaxLength(64).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(User.MaxEmailLength).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(User.MaxDisplayNameLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();

        builder.HasIndex(u => u.PublicRef).IsUnique();

        // Email is normalised to lower case in the domain, so a plain unique index is a
        // correct case-insensitive constraint without needing the citext extension.
        builder.HasIndex(u => u.Email).IsUnique();

        builder.Property(u => u.RowVersion).MapToXmin();
    }
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.PublicRef).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(Project.MaxNameLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Project.MaxDescriptionLength).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasIndex(p => p.PublicRef).IsUnique();
        builder.HasIndex(p => p.OwnerUserId);

        builder.HasOne<User>().WithMany().HasForeignKey(p => p.OwnerUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Members)
            .WithOne()
            .HasForeignKey(m => m.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Members).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Property(p => p.RowVersion).MapToXmin();
    }
}

internal sealed class ProjectMemberConfiguration : IEntityTypeConfiguration<ProjectMember>
{
    public void Configure(EntityTypeBuilder<ProjectMember> builder)
    {
        builder.ToTable("project_members");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(32).IsRequired();

        // A user holds at most one role per project - enforced by the database, not by
        // a read-then-write check that two concurrent requests could both pass.
        builder.HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique();
        builder.HasIndex(m => m.UserId);

        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(m => m.RowVersion).MapToXmin();
    }
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.PublicRef).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Title).HasMaxLength(Asset.MaxTitleLength).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasIndex(a => a.PublicRef).IsUnique();

        // The gallery lists a project's assets newest-first; this index serves that
        // ordering and the status filter without a sort.
        builder.HasIndex(a => new { a.ProjectId, a.Status, a.CreatedAt });

        builder.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(a => a.RowVersion).MapToXmin();
    }
}

internal sealed class AssetVersionConfiguration : IEntityTypeConfiguration<AssetVersion>
{
    public void Configure(EntityTypeBuilder<AssetVersion> builder)
    {
        builder.ToTable("asset_versions");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.PublicRef).HasMaxLength(64).IsRequired();
        builder.Property(v => v.SourceObjectKey).HasMaxLength(512).IsRequired();
        builder.Property(v => v.OriginalFileName).HasMaxLength(AssetVersion.MaxFileNameLength).IsRequired();
        builder.Property(v => v.ContentType).HasMaxLength(AssetVersion.MaxContentTypeLength).IsRequired();
        builder.Property(v => v.Sha256).HasMaxLength(64).IsRequired();

        builder.Property(v => v.LifecycleState).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(v => v.ProcessingState).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(v => v.ReviewState).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasIndex(v => v.PublicRef).IsUnique();

        // The invariant that makes concurrent version creation safe: two writers racing
        // for "the next version" collide here, and the loser retries with a fresh number.
        builder.HasIndex(v => new { v.AssetId, v.VersionNumber })
            .IsUnique()
            .HasDatabaseName("ix_asset_versions_asset_id_version_number");

        builder.HasIndex(v => new { v.ProjectId, v.ProcessingState });
        builder.HasIndex(v => new { v.LifecycleState, v.CreatedAt });

        builder.ToTable(t => t.HasCheckConstraint("ck_asset_versions_version_number", "version_number >= 1"));
        builder.ToTable(t => t.HasCheckConstraint("ck_asset_versions_byte_size", "byte_size >= 0"));

        builder.HasOne<Asset>().WithMany().HasForeignKey(v => v.AssetId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Project>().WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(v => v.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(v => v.Derivatives)
            .WithOne()
            .HasForeignKey(d => d.AssetVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(v => v.Derivatives).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Property(v => v.RowVersion).MapToXmin();
    }
}

internal sealed class AssetDerivativeConfiguration : IEntityTypeConfiguration<AssetDerivative>
{
    public void Configure(EntityTypeBuilder<AssetDerivative> builder)
    {
        builder.ToTable("asset_derivatives");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(d => d.ObjectKey).HasMaxLength(512).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(128).IsRequired();
        builder.Property(d => d.Sha256).HasMaxLength(64).IsRequired();

        // One derivative per kind per version: a duplicate message cannot produce a
        // second logical thumbnail, only overwrite the first.
        builder.HasIndex(d => new { d.AssetVersionId, d.Kind }).IsUnique();
    }
}

internal sealed class ReviewCommentConfiguration : IEntityTypeConfiguration<ReviewComment>
{
    public void Configure(EntityTypeBuilder<ReviewComment> builder)
    {
        builder.ToTable("review_comments");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.PublicRef).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Body).HasMaxLength(ReviewComment.MaxBodyLength).IsRequired();

        builder.HasIndex(c => c.PublicRef).IsUnique();
        builder.HasIndex(c => new { c.AssetVersionId, c.CreatedAt });
        builder.HasIndex(c => c.ProjectId);

        builder.HasOne<AssetVersion>().WithMany().HasForeignKey(c => c.AssetVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.AuthorUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ReviewComment>().WithMany().HasForeignKey(c => c.ParentCommentId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(c => c.RowVersion).MapToXmin();
    }
}

internal sealed class ProcessingJobConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    public void Configure(EntityTypeBuilder<ProcessingJob> builder)
    {
        builder.ToTable("processing_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.PublicRef).HasMaxLength(64).IsRequired();
        builder.Property(j => j.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.FailureKind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.FailureCode).HasMaxLength(100);
        builder.Property(j => j.FailureMessage).HasMaxLength(ProcessingJob.MaxErrorMessageLength);
        builder.Property(j => j.ClaimedBy).HasMaxLength(128);

        builder.HasIndex(j => j.PublicRef).IsUnique();

        // One live job per version. Replays reuse the row rather than creating a second
        // job, which is what makes operator-triggered retry safe.
        builder.HasIndex(j => j.AssetVersionId).IsUnique();
        builder.HasIndex(j => new { j.State, j.UpdatedAt });

        builder.HasOne<AssetVersion>().WithMany().HasForeignKey(j => j.AssetVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Project>().WithMany().HasForeignKey(j => j.ProjectId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(j => j.RowVersion).MapToXmin();
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.MessageType).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.TraceParent).HasMaxLength(64);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);
        builder.Property(m => m.LockedBy).HasMaxLength(128).IsConcurrencyToken();
        builder.Property(m => m.LockedUntil).IsConcurrencyToken();
        builder.Property(m => m.SubjectRef).HasMaxLength(64);

        builder.HasIndex(m => m.SubjectRef);

        // The publisher's only hot query: pending rows whose backoff has elapsed.
        builder.HasIndex(m => new { m.Status, m.NextAttemptAt })
            .HasDatabaseName("ix_outbox_messages_status_next_attempt");
    }
}

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        // Composite key: the same message may legitimately be handled by more than one
        // consumer, but never twice by the same one.
        builder.HasKey(p => new { p.MessageId, p.Consumer });

        builder.Property(p => p.Consumer).HasMaxLength(100).IsRequired();
        builder.Property(p => p.MessageType).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Outcome).HasMaxLength(200);

        builder.HasIndex(p => p.ProcessedAt);
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Endpoint).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Key).HasMaxLength(IdempotencyRecord.MaxKeyLength).IsRequired();
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.ResponseRef).HasMaxLength(64).IsRequired();

        // Scoped to the user so one caller's key can never collide with another's.
        builder.HasIndex(r => new { r.UserId, r.Endpoint, r.Key }).IsUnique();
        builder.HasIndex(r => r.ExpiresAt);
    }
}

internal sealed class StateHistoryConfiguration : IEntityTypeConfiguration<StateHistory>
{
    public void Configure(EntityTypeBuilder<StateHistory> builder)
    {
        builder.ToTable("state_history");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.SubjectRef).HasMaxLength(64).IsRequired();
        builder.Property(h => h.EventType).HasMaxLength(100).IsRequired();
        builder.Property(h => h.Payload).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(h => new { h.ProjectId, h.SubjectRef, h.OccurredAt });
        builder.HasOne<Project>().WithMany().HasForeignKey(h => h.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(h => h.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
