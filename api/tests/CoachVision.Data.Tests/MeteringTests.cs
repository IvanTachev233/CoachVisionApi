using CoachVision.Data.Entities;
using CoachVision.Data.Metering;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using CoachVision.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Tests;

/// <summary>
/// <see cref="MeteringService.RecordUsageAsync"/>: the DoD's primary case (one analysed, one
/// unanalysable feature meters only the first), Ivan's rule that a usage record stays exactly as
/// written once inserted, and that two simultaneous first calls for the same job and feature
/// leave exactly one row (the unique index's <c>ON CONFLICT DO NOTHING</c>).
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class MeteringTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task A_job_with_one_analysed_and_one_unanalysable_feature_meters_only_the_analysed_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var jobId = await SeedJobAsync(db, tenant.Id, ct);

        await using var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        await MeteringService.RecordUsageAsync(
            db,
            tenant.Id,
            jobId,
            videoSeconds: 12.5m,
            featureOutcomes: new Dictionary<string, string>
            {
                ["bar_path"] = MeteringService.AnalysedOutcome,
                ["velocity"] = MeteringService.UnanalysableOutcome,
            },
            isMarkedReanalysis: false,
            ct);
        await transaction.CommitAsync(ct);

        await using var readTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        var rows = await db.Usage.Where(u => u.JobId == jobId).ToListAsync(ct);
        await readTransaction.CommitAsync(ct);

        var row = Assert.Single(rows);
        Assert.Equal("bar_path", row.Feature);
        Assert.Equal(12.5m, row.VideoSeconds);
    }

    [Fact]
    public async Task Calling_it_twice_for_the_same_job_and_feature_leaves_exactly_one_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var jobId = await SeedJobAsync(db, tenant.Id, ct);
        var outcomes = new Dictionary<string, string> { ["bar_path"] = MeteringService.AnalysedOutcome };

        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await MeteringService.RecordUsageAsync(db, tenant.Id, jobId, 10m, outcomes, isMarkedReanalysis: false, ct);
            await transaction.CommitAsync(ct);
        }

        // The same call again: a no-op, not an error and not an update — even with a different
        // video length, the original row is untouched.
        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await MeteringService.RecordUsageAsync(db, tenant.Id, jobId, 999m, outcomes, isMarkedReanalysis: false, ct);
            await transaction.CommitAsync(ct);
        }

        await using var readTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        var rows = await db.Usage.Where(u => u.JobId == jobId).ToListAsync(ct);
        await readTransaction.CommitAsync(ct);

        var row = Assert.Single(rows);
        Assert.Equal(10m, row.VideoSeconds);
    }

    [Fact]
    public async Task A_marked_reanalysis_going_analysed_to_unanalysable_keeps_the_original_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var jobId = await SeedJobAsync(db, tenant.Id, ct);

        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await MeteringService.RecordUsageAsync(
                db, tenant.Id, jobId, 10m,
                new Dictionary<string, string> { ["bar_path"] = MeteringService.AnalysedOutcome },
                isMarkedReanalysis: false, ct);
            await transaction.CommitAsync(ct);
        }

        // The marked run comes back unanalysable for the same feature: Ivan's rule says the
        // record that was already billed stays billed.
        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await MeteringService.RecordUsageAsync(
                db, tenant.Id, jobId, 10m,
                new Dictionary<string, string> { ["bar_path"] = MeteringService.UnanalysableOutcome },
                isMarkedReanalysis: true, ct);
            await transaction.CommitAsync(ct);
        }

        await using var readTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        var rows = await db.Usage.Where(u => u.JobId == jobId).ToListAsync(ct);
        await readTransaction.CommitAsync(ct);

        var row = Assert.Single(rows);
        Assert.Equal("bar_path", row.Feature);
    }

    [Fact]
    public async Task A_marked_reanalysis_going_unanalysable_to_analysed_adds_no_new_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var jobId = await SeedJobAsync(db, tenant.Id, ct);

        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await MeteringService.RecordUsageAsync(
                db, tenant.Id, jobId, 10m,
                new Dictionary<string, string> { ["bar_path"] = MeteringService.UnanalysableOutcome },
                isMarkedReanalysis: false, ct);
            await transaction.CommitAsync(ct);
        }

        // The marked run comes back analysed for a feature that wasn't billed the first time:
        // Ivan's rule says a marked analysis never adds a record either.
        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await MeteringService.RecordUsageAsync(
                db, tenant.Id, jobId, 10m,
                new Dictionary<string, string> { ["bar_path"] = MeteringService.AnalysedOutcome },
                isMarkedReanalysis: true, ct);
            await transaction.CommitAsync(ct);
        }

        await using var readTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        var rowCount = await db.Usage.CountAsync(u => u.JobId == jobId, ct);
        await readTransaction.CommitAsync(ct);

        Assert.Equal(0, rowCount);
    }

    [Fact]
    public async Task A_usage_record_outlives_a_deleted_job_and_the_unique_index_still_holds_for_other_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var jobId = await SeedJobAsync(db, tenant.Id, ct);

        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await MeteringService.RecordUsageAsync(
                db, tenant.Id, jobId, 10m,
                new Dictionary<string, string> { ["bar_path"] = MeteringService.AnalysedOutcome },
                isMarkedReanalysis: false, ct);
            await transaction.CommitAsync(ct);
        }

        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == jobId, ct);
            db.Jobs.Remove(job);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // The job is gone, but its usage stays (job_id is now null through ON DELETE SET NULL).
        await using (var readTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            var surviving = await db.Usage.SingleAsync(u => u.TenantId == tenant.Id, ct);
            Assert.Null(surviving.JobId);
            await readTransaction.CommitAsync(ct);
        }

        // A second job can still record the same feature under the unique index: a null job_id
        // never collides with a different job's row.
        using var secondDb = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var secondJobId = await SeedJobAsync(secondDb, tenant.Id, ct);
        await using (var transaction = await secondDb.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await MeteringService.RecordUsageAsync(
                secondDb, tenant.Id, secondJobId, 5m,
                new Dictionary<string, string> { ["bar_path"] = MeteringService.AnalysedOutcome },
                isMarkedReanalysis: false, ct);
            await transaction.CommitAsync(ct);
        }

        await using var verifyTransaction = await secondDb.BeginTenantScopeAsync(tenant.Id, ct);
        var total = await secondDb.Usage.CountAsync(u => u.TenantId == tenant.Id, ct);
        await verifyTransaction.CommitAsync(ct);
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task Two_simultaneous_first_calls_for_the_same_job_and_feature_leave_one_row()
    {
        var ct = TestContext.Current.CancellationToken;
        using var seedDb = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(seedDb, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var jobId = await SeedJobAsync(seedDb, tenant.Id, ct);

        var outcomes = new Dictionary<string, string> { ["bar_path"] = MeteringService.AnalysedOutcome };

        async Task RecordAsync()
        {
            using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
            await using var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
            await MeteringService.RecordUsageAsync(db, tenant.Id, jobId, 10m, outcomes, isMarkedReanalysis: false, ct);
            await transaction.CommitAsync(ct);
        }

        await Task.WhenAll(RecordAsync(), RecordAsync());

        await using var readTransaction = await seedDb.BeginTenantScopeAsync(tenant.Id, ct);
        var rowCount = await seedDb.Usage.CountAsync(u => u.JobId == jobId, ct);
        await readTransaction.CommitAsync(ct);

        Assert.Equal(1, rowCount);
    }

    private static async Task<string> SeedJobAsync(VisionDbContext db, Guid tenantId, CancellationToken ct)
    {
        var jobId = $"job_{Guid.NewGuid():N}0000000"[..30];
        await using var transaction = await db.BeginTenantScopeAsync(tenantId, ct);
        db.Jobs.Add(new Job
        {
            Id = jobId,
            TenantId = tenantId,
            Status = "completed",
            SchemaVersion = "1.0",
            Lift = "squat",
            Features = ["bar_path", "velocity"],
            SubjectId = "sub_1",
            TrainingAllowed = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return jobId;
    }
}
