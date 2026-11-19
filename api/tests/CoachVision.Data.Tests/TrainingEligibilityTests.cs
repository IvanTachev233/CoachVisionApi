using CoachVision.Data.Entities;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using CoachVision.Data.Training;
using CoachVision.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Tests;

/// <summary>
/// A tenant's training flag defaults to off; a job is eligible for training only when both the
/// tenant's flag and the job's own <c>TrainingAllowed</c> are true.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class TrainingEligibilityTests(VisionPostgresFixture fixture)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void IsEligible_requires_both_flags(bool tenantTrainingAllowed, bool jobTrainingAllowed, bool expected)
    {
        Assert.Equal(expected, TrainingEligibility.IsEligible(tenantTrainingAllowed, jobTrainingAllowed));
    }

    [Fact]
    public async Task A_new_tenant_defaults_to_training_off_even_when_its_jobs_allow_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        Assert.False(tenant.TrainingAllowed);

        var jobId = await SeedJobAsync(db, tenant.Id, jobTrainingAllowed: true, ct);

        await using var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        var eligible = await TrainingEligibility.IsJobEligibleAsync(db, tenant.Id, jobId, ct);
        await transaction.CommitAsync(ct);

        Assert.False(eligible);
    }

    [Fact]
    public async Task A_job_is_eligible_only_once_both_the_tenants_flag_and_its_own_flag_are_true()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var jobId = await SeedJobAsync(db, tenant.Id, jobTrainingAllowed: true, ct);

        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await ApiKeyService.SetTenantTrainingAsync(db, tenant.Id, trainingAllowed: true, ct);
            await transaction.CommitAsync(ct);
        }

        await using var readTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        var eligible = await TrainingEligibility.IsJobEligibleAsync(db, tenant.Id, jobId, ct);
        await readTransaction.CommitAsync(ct);

        Assert.True(eligible);
    }

    [Fact]
    public async Task A_tenant_allowing_training_does_not_make_an_opted_out_job_eligible()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var jobId = await SeedJobAsync(db, tenant.Id, jobTrainingAllowed: false, ct);

        await using (var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await ApiKeyService.SetTenantTrainingAsync(db, tenant.Id, trainingAllowed: true, ct);
            await transaction.CommitAsync(ct);
        }

        await using var readTransaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        var eligible = await TrainingEligibility.IsJobEligibleAsync(db, tenant.Id, jobId, ct);
        await readTransaction.CommitAsync(ct);

        Assert.False(eligible);
    }

    private static async Task<string> SeedJobAsync(VisionDbContext db, Guid tenantId, bool jobTrainingAllowed, CancellationToken ct)
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
            Features = ["bar_path"],
            SubjectId = "sub_1",
            TrainingAllowed = jobTrainingAllowed,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return jobId;
    }
}
