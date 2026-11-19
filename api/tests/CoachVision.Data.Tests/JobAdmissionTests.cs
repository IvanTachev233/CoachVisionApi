using CoachVision.Data.Entities;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using CoachVision.TestSupport;

namespace CoachVision.Data.Tests;

/// <summary>
/// Job-admission checks: clip size, concurrent jobs and daily minutes, each passing at the
/// limit and refused at limit + 1, and each scoped per tenant. <see cref="JobAdmission.CheckAsync"/>
/// itself opens no transaction (job creation must run it and the job insert in one, per its own
/// remarks), so every call here opens the tenant scope it needs.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class JobAdmissionTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task A_clip_at_the_size_limit_is_admitted_and_one_byte_over_is_too_large()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var limit = await GetLimitsAsync(db, tenant.Id, ct);

        var atLimit = await CheckScopedAsync(db, tenant.Id, limit.MaxClipSizeBytes, ct);
        Assert.Equal(JobAdmissionOutcome.Admitted, atLimit.Outcome);

        var overLimit = await CheckScopedAsync(db, tenant.Id, limit.MaxClipSizeBytes + 1, ct);
        Assert.Equal(JobAdmissionOutcome.ClipTooLarge, overLimit.Outcome);
        Assert.Equal(limit.MaxClipSizeBytes, overLimit.LimitBytes);
    }

    [Fact]
    public async Task Concurrent_jobs_at_the_limit_are_admitted_and_one_more_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var limit = await GetLimitsAsync(db, tenant.Id, ct);

        await using (var tx = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            for (var i = 0; i < limit.ConcurrentJobsLimit - 1; i++)
            {
                InsertOpenJob(db, tenant.Id);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var admitted = await CheckScopedAsync(db, tenant.Id, 1_000, ct);
        Assert.Equal(JobAdmissionOutcome.Admitted, admitted.Outcome);

        await using (var tx = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            InsertOpenJob(db, tenant.Id);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var refused = await CheckScopedAsync(db, tenant.Id, 1_000, ct);
        Assert.Equal(JobAdmissionOutcome.ConcurrentJobsLimitReached, refused.Outcome);
        Assert.NotNull(refused.RetryAfterSeconds);
    }

    [Fact]
    public async Task Daily_minutes_at_the_limit_are_admitted_and_one_second_more_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var limit = await GetLimitsAsync(db, tenant.Id, ct);
        var limitSeconds = limit.DailyMinutesLimit * 60m;

        await using (var tx = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            db.Usage.Add(new UsageRecord
            {
                TenantId = tenant.Id,
                Feature = "bar_path",
                VideoSeconds = limitSeconds,
                OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Exactly at the limit already used: the next job's usage would exceed it, so the current
        // request is refused (the check guards the *next* clip, not the one already recorded).
        var refused = await CheckScopedAsync(db, tenant.Id, 1_000, ct);
        Assert.Equal(JobAdmissionOutcome.DailyMinutesLimitReached, refused.Outcome);
        Assert.NotNull(refused.RetryAfterSeconds);
    }

    [Fact]
    public async Task Daily_minutes_just_under_the_limit_are_admitted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var limit = await GetLimitsAsync(db, tenant.Id, ct);
        var limitSeconds = limit.DailyMinutesLimit * 60m;

        await using (var tx = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            db.Usage.Add(new UsageRecord
            {
                TenantId = tenant.Id,
                Feature = "bar_path",
                VideoSeconds = limitSeconds - 1m,
                OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var admitted = await CheckScopedAsync(db, tenant.Id, 1_000, ct);
        Assert.Equal(JobAdmissionOutcome.Admitted, admitted.Outcome);
    }

    [Fact]
    public async Task One_tenants_usage_does_not_affect_another_tenants_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenantA = await ApiKeyService.ProvisionTenantAsync(db, "tenant-a-" + Guid.NewGuid(), cancellationToken: ct);
        var tenantB = await ApiKeyService.ProvisionTenantAsync(db, "tenant-b-" + Guid.NewGuid(), cancellationToken: ct);
        var limitA = await GetLimitsAsync(db, tenantA.Id, ct);

        await using (var tx = await db.BeginTenantScopeAsync(tenantA.Id, ct))
        {
            db.Usage.Add(new UsageRecord
            {
                TenantId = tenantA.Id,
                Feature = "bar_path",
                VideoSeconds = limitA.DailyMinutesLimit * 60m,
                OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var tenantAResult = await CheckScopedAsync(db, tenantA.Id, 1_000, ct);
        var tenantBResult = await CheckScopedAsync(db, tenantB.Id, 1_000, ct);

        Assert.Equal(JobAdmissionOutcome.DailyMinutesLimitReached, tenantAResult.Outcome);
        Assert.Equal(JobAdmissionOutcome.Admitted, tenantBResult.Outcome);
    }

    [Fact]
    public async Task A_queued_job_holds_a_concurrency_slot_too()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var limit = await GetLimitsAsync(db, tenant.Id, ct);

        await using (var tx = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            for (var i = 0; i < limit.ConcurrentJobsLimit - 1; i++)
            {
                InsertOpenJob(db, tenant.Id, "queued");
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var admitted = await CheckScopedAsync(db, tenant.Id, 1_000, ct);
        Assert.Equal(JobAdmissionOutcome.Admitted, admitted.Outcome);

        await using (var tx = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            InsertOpenJob(db, tenant.Id, "queued");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var refused = await CheckScopedAsync(db, tenant.Id, 1_000, ct);
        Assert.Equal(JobAdmissionOutcome.ConcurrentJobsLimitReached, refused.Outcome);
    }

    private static void InsertOpenJob(VisionDbContext db, Guid tenantId, string status = "awaiting_upload")
    {
        db.Jobs.Add(new Job
        {
            Id = $"job_{Guid.NewGuid():N}0000000"[..30],
            TenantId = tenantId,
            Status = status,
            SchemaVersion = "1.0",
            Lift = "squat",
            Features = ["bar_path"],
            SubjectId = "sub_1",
            TrainingAllowed = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
    }

    private static async Task<TenantLimits> GetLimitsAsync(VisionDbContext db, Guid tenantId, CancellationToken ct)
    {
        await using var tx = await db.BeginTenantScopeAsync(tenantId, ct);
        var limits = await db.TenantLimits.FindAsync([tenantId], ct);
        await tx.CommitAsync(ct);
        return limits!;
    }

    private static async Task<JobAdmissionResult> CheckScopedAsync(VisionDbContext db, Guid tenantId, long sizeBytes, CancellationToken ct)
    {
        await using var tx = await db.BeginTenantScopeAsync(tenantId, ct);
        var result = await JobAdmission.CheckAsync(db, tenantId, sizeBytes, ct);
        await tx.CommitAsync(ct);
        return result;
    }
}
