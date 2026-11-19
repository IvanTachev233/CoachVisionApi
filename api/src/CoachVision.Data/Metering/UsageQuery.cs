using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Metering;

/// <summary>One tenant's usage for one UTC day and one feature.</summary>
public sealed record DailyFeatureUsage(DateOnly Day, string Feature, int RecordCount, decimal TotalVideoSeconds);

/// <summary>
/// Usage per tenant per day, backed by the existing <c>(tenant_id, occurred_at)</c> index. Must
/// run inside the caller's own tenant scope, as the run-time role: under row-level security a
/// tenant sees only its own usage, so this never needs to weaken row security to answer "per
/// tenant" — a cross-tenant report is a later, narrower seam (ADR 0027 line 51), not this query
/// called once per tenant.
/// </summary>
public static class UsageQuery
{
    public static async Task<IReadOnlyList<DailyFeatureUsage>> PerDayAndFeatureAsync(
        VisionDbContext db,
        Guid tenantId,
        DateTime fromUtcInclusive,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.Usage
            .Where(u => u.TenantId == tenantId && u.OccurredAtUtc >= fromUtcInclusive && u.OccurredAtUtc < toUtcExclusive)
            .GroupBy(u => new { Day = u.OccurredAtUtc.Date, u.Feature })
            .Select(g => new
            {
                g.Key.Day,
                g.Key.Feature,
                RecordCount = g.Count(),
                TotalVideoSeconds = g.Sum(u => u.VideoSeconds),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new DailyFeatureUsage(DateOnly.FromDateTime(r.Day), r.Feature, r.RecordCount, r.TotalVideoSeconds))
            .OrderBy(r => r.Day)
            .ThenBy(r => r.Feature, StringComparer.Ordinal)
            .ToList();
    }
}
