using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Training;

/// <summary>
/// Whether a job may ever be used for training: both the tenant's own flag
/// (<c>Tenant.TrainingAllowed</c>, default <c>false</c> for every tenant) and the job's own
/// (<c>Job.TrainingAllowed</c>) must be true. Either one being false makes the job ineligible —
/// the tenant flag can only narrow what a job already allows, never widen it.
/// </summary>
public static class TrainingEligibility
{
    public static bool IsEligible(bool tenantTrainingAllowed, bool jobTrainingAllowed) =>
        tenantTrainingAllowed && jobTrainingAllowed;

    /// <summary>
    /// Reads both flags for one job and tenant. Must run inside the caller's own tenant scope, as
    /// the run-time role, under row-level security.
    /// </summary>
    public static async Task<bool> IsJobEligibleAsync(
        VisionDbContext db,
        Guid tenantId,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, cancellationToken);
        var job = await db.Jobs.SingleAsync(j => j.TenantId == tenantId && j.Id == jobId, cancellationToken);
        return IsEligible(tenant.TrainingAllowed, job.TrainingAllowed);
    }
}
