using CoachVision.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Security;

/// <summary>
/// The one place that reads a tenant's limits row, shared by every caller that needs it
/// (authentication, which only needs the rate limit; job admission, which needs the rest): a
/// missing row (shouldn't happen — a tenant always gets one in the same transaction it's created
/// in — but a tenant made any other way might lack one) falls back to <see cref="TenantLimits"/>'s
/// own defaults, the same way for everyone, rather than one caller throwing where another
/// silently defaults.
/// </summary>
public static class TenantLimitsLookup
{
    public static async Task<TenantLimits> GetOrDefaultAsync(VisionDbContext db, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var limits = await db.TenantLimits.FindAsync([tenantId], cancellationToken);
        return limits ?? new TenantLimits { TenantId = tenantId };
    }
}
