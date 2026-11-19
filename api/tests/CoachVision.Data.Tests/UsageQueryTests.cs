using CoachVision.Data.Entities;
using CoachVision.Data.Metering;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using CoachVision.TestSupport;

namespace CoachVision.Data.Tests;

/// <summary>
/// <see cref="UsageQuery.PerDayAndFeatureAsync"/>: per UTC day and feature, record count and
/// total video seconds, and tenant A never sees tenant B's usage through it.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class UsageQueryTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task Usage_is_summed_per_day_and_feature()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);

        var today = DateTime.UtcNow.Date;
        await using var writeTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        db.Usage.AddRange(
            new UsageRecord { TenantId = tenant.Id, Feature = "bar_path", VideoSeconds = 10m, OccurredAtUtc = today.AddHours(1) },
            new UsageRecord { TenantId = tenant.Id, Feature = "bar_path", VideoSeconds = 5m, OccurredAtUtc = today.AddHours(2) },
            new UsageRecord { TenantId = tenant.Id, Feature = "velocity", VideoSeconds = 8m, OccurredAtUtc = today.AddHours(3) },
            new UsageRecord { TenantId = tenant.Id, Feature = "bar_path", VideoSeconds = 3m, OccurredAtUtc = today.AddDays(-1).AddHours(1) });
        await db.SaveChangesAsync(ct);
        await writeTransaction.CommitAsync(ct);

        await using var readTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        var report = await UsageQuery.PerDayAndFeatureAsync(db, tenant.Id, today.AddDays(-7), today.AddDays(1), ct);
        await readTransaction.CommitAsync(ct);

        var todayBarPath = Assert.Single(report, r => r.Day == DateOnly.FromDateTime(today) && r.Feature == "bar_path");
        Assert.Equal(2, todayBarPath.RecordCount);
        Assert.Equal(15m, todayBarPath.TotalVideoSeconds);

        var todayVelocity = Assert.Single(report, r => r.Day == DateOnly.FromDateTime(today) && r.Feature == "velocity");
        Assert.Equal(1, todayVelocity.RecordCount);
        Assert.Equal(8m, todayVelocity.TotalVideoSeconds);

        var yesterdayBarPath = Assert.Single(report, r => r.Day == DateOnly.FromDateTime(today.AddDays(-1)) && r.Feature == "bar_path");
        Assert.Equal(1, yesterdayBarPath.RecordCount);
        Assert.Equal(3m, yesterdayBarPath.TotalVideoSeconds);
    }

    [Fact]
    public async Task Tenant_A_never_sees_tenant_Bs_usage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenantA = await ApiKeyService.ProvisionTenantAsync(db, "tenant-a-" + Guid.NewGuid(), cancellationToken: ct);
        var tenantB = await ApiKeyService.ProvisionTenantAsync(db, "tenant-b-" + Guid.NewGuid(), cancellationToken: ct);

        var today = DateTime.UtcNow.Date;

        await using (var transaction = await db.BeginTenantScopeAsync(tenantA.Id, ct))
        {
            db.Usage.Add(new UsageRecord { TenantId = tenantA.Id, Feature = "bar_path", VideoSeconds = 10m, OccurredAtUtc = today.AddHours(1) });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await using (var transaction = await db.BeginTenantScopeAsync(tenantB.Id, ct))
        {
            db.Usage.Add(new UsageRecord { TenantId = tenantB.Id, Feature = "bar_path", VideoSeconds = 99m, OccurredAtUtc = today.AddHours(1) });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await using var readTransaction = await db.BeginTenantScopeAsync(tenantA.Id, ct);
        var report = await UsageQuery.PerDayAndFeatureAsync(db, tenantA.Id, today.AddDays(-1), today.AddDays(1), ct);
        await readTransaction.CommitAsync(ct);

        var row = Assert.Single(report);
        Assert.Equal(10m, row.TotalVideoSeconds);
    }
}
