using CoachVision.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data;

/// <summary>
/// Vision's schema. Table and column names are written out in snake_case rather than taken from a
/// naming-convention package, so the mapping is explicit and doesn't depend on a convention that
/// could change how an existing column is read.
///
/// Row-level security is the tenant boundary (see the migration that adds it and
/// <c>Tenancy.TenantScope</c>), not anything in this class: a context built with the run-time
/// connection string enforces nothing by itself, Postgres does.
/// </summary>
public sealed class VisionDbContext(DbContextOptions<VisionDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobResult> Results => Set<JobResult>();

    public DbSet<UsageRecord> Usage => Set<UsageRecord>();

    public DbSet<TenantLimits> TenantLimits => Set<TenantLimits>();

    public DbSet<JobLabel> Labels => Set<JobLabel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants");
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            tenant.Property(t => t.Name).HasColumnName("name").IsRequired();
            tenant.Property(t => t.CreatedAtUtc).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
            tenant.Property(t => t.TrainingAllowed).HasColumnName("training_allowed").HasDefaultValue(false).IsRequired();
        });

        modelBuilder.Entity<ApiKey>(apiKey =>
        {
            apiKey.ToTable("api_keys");
            apiKey.HasKey(k => k.Id);
            apiKey.Property(k => k.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            apiKey.Property(k => k.TenantId).HasColumnName("tenant_id").IsRequired();
            apiKey.Property(k => k.KeyHash).HasColumnName("key_hash").HasColumnType("bytea").IsRequired();
            apiKey.Property(k => k.Kind).HasColumnName("kind").IsRequired();
            apiKey.Property(k => k.CreatedAtUtc).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
            apiKey.Property(k => k.RevokedAtUtc).HasColumnName("revoked_at").HasColumnType("timestamptz");
            apiKey.HasIndex(k => k.KeyHash).IsUnique();
            // Lets a tenant's limits reference (tenant_id, api_key id) with a composite foreign key,
            // the same pattern used below for jobs, results and usage.
            apiKey.HasIndex(k => new { k.TenantId, k.Id }).IsUnique();
            apiKey.HasOne<Tenant>().WithMany().HasForeignKey(k => k.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantLimits>(limits =>
        {
            limits.ToTable("tenant_limits");
            limits.HasKey(l => l.TenantId);
            limits.Property(l => l.TenantId).HasColumnName("tenant_id");
            limits.Property(l => l.RateLimitPerMinute).HasColumnName("rate_limit_per_minute").IsRequired();
            limits.Property(l => l.ConcurrentJobsLimit).HasColumnName("concurrent_jobs_limit").IsRequired();
            limits.Property(l => l.DailyMinutesLimit).HasColumnName("daily_minutes_limit").HasColumnType("numeric").IsRequired();
            limits.Property(l => l.MaxClipSizeBytes).HasColumnName("max_clip_size_bytes").IsRequired();
            limits.Property(l => l.MaxClipLengthSeconds).HasColumnName("max_clip_length_seconds").IsRequired();
            limits.ToTable(t => t.HasCheckConstraint(
                "CK_tenant_limits_max_clip_size_bytes_bounded",
                "max_clip_size_bytes > 0 AND max_clip_size_bytes <= 209715200"));
            limits.ToTable(t => t.HasCheckConstraint(
                "CK_tenant_limits_positive",
                "rate_limit_per_minute > 0 AND concurrent_jobs_limit > 0 AND daily_minutes_limit > 0 AND max_clip_length_seconds > 0"));
            // tenants: this table's RLS doesn't need its own tenant_id - app.tenant_id comparison
            // the way api_keys/jobs/results/usage do; tenant_id here *is* the tenant (one row per
            // tenant, like tenants.id), so the migration's policy for it reads tenant_id directly.
            limits.HasOne<Tenant>().WithOne().HasForeignKey<TenantLimits>(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Job>(job =>
        {
            job.ToTable("jobs");
            job.HasKey(j => j.Id);
            job.Property(j => j.Id).HasColumnName("id");
            job.Property(j => j.TenantId).HasColumnName("tenant_id").IsRequired();
            job.Property(j => j.Status).HasColumnName("status").IsRequired();
            job.Property(j => j.SchemaVersion).HasColumnName("schema_version").IsRequired();
            job.Property(j => j.Lift).HasColumnName("lift").IsRequired();
            job.Property(j => j.Features).HasColumnName("features").HasColumnType("text[]").IsRequired();
            job.Property(j => j.SubjectId).HasColumnName("subject_id").IsRequired();
            job.Property(j => j.TrainingAllowed).HasColumnName("training_allowed").IsRequired();
            job.Property(j => j.ClientReference).HasColumnName("client_reference");
            job.Property(j => j.CreatedAtUtc).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
            job.Property(j => j.UpdatedAtUtc).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired();
            job.Property(j => j.CompletedAtUtc).HasColumnName("completed_at").HasColumnType("timestamptz");
            job.Property(j => j.EquipmentJson).HasColumnName("equipment").HasColumnType("jsonb");
            job.Property(j => j.VideoInfoJson).HasColumnName("video_info").HasColumnType("jsonb");
            job.Property(j => j.FailureJson).HasColumnName("failure").HasColumnType("jsonb");
            job.HasIndex(j => new { j.TenantId, j.SubjectId });
            // The target of every tenant-owned table's composite foreign key below: a row can
            // name a job of its own tenant only, never another tenant's, enforced by Postgres
            // (not application code) at insert and update time.
            job.HasIndex(j => new { j.TenantId, j.Id }).IsUnique();
            job.HasOne<Tenant>().WithMany().HasForeignKey(j => j.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JobResult>(result =>
        {
            result.ToTable("results");
            result.HasKey(r => r.Id);
            result.Property(r => r.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            result.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
            result.Property(r => r.JobId).HasColumnName("job_id").IsRequired();
            result.Property(r => r.Feature).HasColumnName("feature").IsRequired();
            result.Property(r => r.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            result.Property(r => r.CreatedAtUtc).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
            result.HasIndex(r => new { r.TenantId, r.JobId, r.Feature }).IsUnique();
            result
                .HasOne<Job>()
                .WithMany()
                .HasForeignKey(r => new { r.TenantId, r.JobId })
                .HasPrincipalKey(j => new { j.TenantId, j.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UsageRecord>(usage =>
        {
            usage.ToTable("usage");
            usage.HasKey(u => u.Id);
            usage.Property(u => u.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            usage.Property(u => u.TenantId).HasColumnName("tenant_id").IsRequired();
            usage.Property(u => u.JobId).HasColumnName("job_id");
            usage.Property(u => u.Feature).HasColumnName("feature").IsRequired();
            usage.Property(u => u.VideoSeconds).HasColumnName("video_seconds").HasColumnType("numeric").IsRequired();
            usage.ToTable(t => t.HasCheckConstraint("CK_usage_video_seconds_not_negative", "video_seconds >= 0"));
            usage.Property(u => u.OccurredAtUtc).HasColumnName("occurred_at").HasColumnType("timestamptz").IsRequired();
            // The seam future metering needs: usage per tenant per day.
            usage.HasIndex(u => new { u.TenantId, u.OccurredAtUtc });
            // Ivan's rule: at most one usage row per (tenant, job, feature), and it stays as
            // written once inserted (MeteringService.RecordUsageAsync writes it as INSERT ... ON
            // CONFLICT DO NOTHING against this exact index). A plain unique btree index already
            // treats every NULL job_id as distinct from every other, so a deleted job's usage
            // (job_id set to null) is never deduplicated against another job's rows — no partial
            // index is needed for that.
            usage.HasIndex(u => new { u.TenantId, u.JobId, u.Feature }).IsUnique();
            usage.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
            // Nullable: a usage record outlives its job (the contract keeps usage after a job is
            // deleted), so the foreign key is enforced only while JobId is set (Postgres MATCH
            // SIMPLE: any null column skips the check).
            usage
                .HasOne<Job>()
                .WithMany()
                .HasForeignKey(u => new { u.TenantId, u.JobId })
                .HasPrincipalKey(j => new { j.TenantId, j.Id })
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        modelBuilder.Entity<JobLabel>(label =>
        {
            label.ToTable("job_labels");
            label.HasKey(l => l.Id);
            label.Property(l => l.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            label.Property(l => l.TenantId).HasColumnName("tenant_id").IsRequired();
            label.Property(l => l.JobId).HasColumnName("job_id").IsRequired();
            label.Property(l => l.Assessment).HasColumnName("assessment").IsRequired();
            label.Property(l => l.Reason).HasColumnName("reason");
            label.Property(l => l.UpdatedAtUtc).HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired();
            label.ToTable(t => t.HasCheckConstraint("CK_job_labels_assessment", "assessment IN ('correct', 'incorrect')"));
            label.ToTable(t => t.HasCheckConstraint(
                "CK_job_labels_reason",
                "reason IS NULL OR (assessment = 'incorrect' AND reason IN ('wrong_rep_count', 'bar_path_off', 'wrong_lift'))"));
            // A job has at most one label: this is what the endpoint's "replace, never a second
            // row" relies on (LabelsRepository.UpsertAsync finds by this pair before inserting).
            label.HasIndex(l => new { l.TenantId, l.JobId }).IsUnique();
            label
                .HasOne<Job>()
                .WithMany()
                .HasForeignKey(l => new { l.TenantId, l.JobId })
                .HasPrincipalKey(j => new { j.TenantId, j.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
