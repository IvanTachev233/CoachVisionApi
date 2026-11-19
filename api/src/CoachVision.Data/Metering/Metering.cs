using CoachVision.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Metering;

/// <summary>
/// Records what a job used, for a later task's job-completion code to call (nothing calls this in
/// production yet). Vocabulary matches <c>CoachVision.Analysis</c>'s own outcome discriminator
/// (<see cref="AnalysedOutcome"/>, <see cref="UnanalysableOutcome"/>) rather than inventing a
/// second one.
/// </summary>
public static class MeteringService
{
    public const string AnalysedOutcome = "analysed";
    public const string UnanalysableOutcome = "unanalysable";

    /// <summary>
    /// Writes one usage row per feature that came back <see cref="AnalysedOutcome"/>, with the
    /// video's length in seconds. Must run inside the caller's own tenant scope
    /// (<see cref="TenantScope.BeginTenantScopeAsync"/>), as the run-time role, under row-level
    /// security — this method opens no transaction and no scope of its own.
    ///
    /// Ivan's rule (2026-11-19, ADR 0017 line 145): a usage record stays exactly as written, once
    /// per video and feature. <paramref name="isMarkedReanalysis"/> tells a first analysis from a
    /// marked re-analysis: for a marked re-analysis this writes nothing and deletes nothing,
    /// whatever <paramref name="featureOutcomes"/> says — a feature that was analysed and billed
    /// stays billed even if the marked run comes back unanalysable, and a feature that was
    /// unanalysable and becomes analysed after a mark gets no new row. A record that followed the
    /// final result could be erased by a junk mark after the result was read, and counts could go
    /// down after the fact — which is exactly what this guards against.
    ///
    /// For a first analysis, each insert is `INSERT ... ON CONFLICT (tenant_id, job_id, feature)
    /// DO NOTHING`: at most one row per (tenant, job, feature), enforced by the unique index in
    /// <c>VisionDbContext</c>, so calling this twice for the same job (or two callers racing) is a
    /// no-op the second time, never an error and never an update.
    /// </summary>
    public static async Task RecordUsageAsync(
        VisionDbContext db,
        Guid tenantId,
        string jobId,
        decimal videoSeconds,
        IReadOnlyDictionary<string, string> featureOutcomes,
        bool isMarkedReanalysis,
        CancellationToken cancellationToken = default)
    {
        if (isMarkedReanalysis)
        {
            return;
        }

        foreach (var (feature, outcome) in featureOutcomes)
        {
            if (outcome != AnalysedOutcome)
            {
                continue;
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO usage (tenant_id, job_id, feature, video_seconds, occurred_at)
                VALUES ({tenantId}, {jobId}, {feature}, {videoSeconds}, now())
                ON CONFLICT (tenant_id, job_id, feature) DO NOTHING
                """,
                cancellationToken);
        }
    }
}
