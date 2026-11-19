using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Labels;

/// <summary>
/// A job's label, persisted so a new `POST /v1/jobs/{jobId}/labels` replaces whatever label the
/// job already had. Must run inside the caller's own tenant scope, as the run-time role, under
/// row-level security.
/// </summary>
public static class LabelsRepository
{
    public static Task<Entities.JobLabel?> FindAsync(VisionDbContext db, Guid tenantId, string jobId, CancellationToken cancellationToken = default) =>
        db.Labels.AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == tenantId && l.JobId == jobId, cancellationToken);

    /// <summary>
    /// Inserts the job's first label, or overwrites its existing one in place — never two rows
    /// for the same job, since the unique index on (tenant_id, job_id) is the thing that actually
    /// prevents it.
    ///
    /// A read-then-insert-or-update (the earlier version of this method) is a race: two
    /// `POST /v1/jobs/{jobId}/labels` for the same job with no existing label both see no row and
    /// both try to insert, and the second hits the unique index and fails — the contract says
    /// "repeating a request is safe", which an unhandled exception isn't. One statement closes the
    /// race the database itself serialises: `INSERT ... ON CONFLICT (tenant_id, job_id) DO UPDATE`,
    /// parameterised through <c>ExecuteSqlInterpolatedAsync</c> (the same pattern
    /// <c>MeteringService.RecordUsageAsync</c> uses), then the row is read back for the response.
    /// </summary>
    public static async Task<Entities.JobLabel> UpsertAsync(
        VisionDbContext db, Guid tenantId, string jobId, string assessment, string? reason, DateTime updatedAtUtc, CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO job_labels (tenant_id, job_id, assessment, reason, updated_at)
            VALUES ({tenantId}, {jobId}, {assessment}, {reason}, {updatedAtUtc})
            ON CONFLICT (tenant_id, job_id) DO UPDATE SET
                assessment = EXCLUDED.assessment,
                reason = EXCLUDED.reason,
                updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);

        return await FindAsync(db, tenantId, jobId, cancellationToken) ??
            throw new InvalidOperationException($"job_labels row for tenant {tenantId} job {jobId} is missing immediately after upsert.");
    }
}
