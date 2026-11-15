using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CoachVision.Api.Security;

/// <summary>
/// Bounds what reaches an endpoint's own handler — it does not stop the authentication lookup
/// itself, which runs first on every request (one hash and one indexed database read per
/// request, whether the key turns out valid or not); a correct limiter placed *before* that
/// lookup would need a trustworthy client address to partition by, which this deployment doesn't
/// have (see below), so none is built. Partitioned by tenant once authenticated (the rate the
/// tenant's own <c>tenant_limits</c> row names, stashed in <c>HttpContext.Items</c> by
/// <see cref="ApiKeyAuthenticationHandler"/> since the partition factory below runs synchronously
/// and can't itself read the database); by remote IP otherwise, so a flood of requests with no
/// valid key at all is still bounded past this point, by a fixed ceiling unrelated to any
/// tenant's own limit (there is no tenant yet to charge it to). Behind Cloud Run,
/// <c>RemoteIpAddress</c> is the load balancer's own address unless forwarded headers are
/// configured, which this does not do — so that IP partition is, in practice, one shared bucket
/// for all unauthenticated traffic, not one per real client, until forwarded-header trust is
/// added deliberately (accepted as a gap, not fixed here).
///
/// <see cref="FixedWindowRateLimiter"/> is in-process memory, not shared storage, and each
/// partition's options (including the permit limit read at authentication time) are fixed for
/// the life of that partition: on more than one instance, or after a tenant's
/// <c>rate_limit_per_minute</c> changes, the real ceiling is wrong until the partition is evicted
/// or the instance restarts. A shared store (Redis, or Postgres advisory counters) behind the
/// same partition key would close both gaps.
/// </summary>
public static class RateLimiting
{
    public const string TenantIdItemsKey = "TenantId";
    public const string RateLimitPerMinuteItemsKey = "RateLimitPerMinute";

    private const int UnauthenticatedPermitLimitPerMinute = 20;

    public static PartitionedRateLimiter<HttpContext> CreatePartitionedLimiter() =>
        PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            if (httpContext.Items.TryGetValue(TenantIdItemsKey, out var tenantObj) && tenantObj is Guid tenantId)
            {
                var permitLimit = httpContext.Items.TryGetValue(RateLimitPerMinuteItemsKey, out var limitObj) && limitObj is int limit
                    ? limit
                    : UnauthenticatedPermitLimitPerMinute;

                return RateLimitPartition.GetFixedWindowLimiter(
                    $"tenant:{tenantId}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            }

            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                $"unauth:{remoteIp}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = UnauthenticatedPermitLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        });

    public static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfterSeconds = 60;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        }

        await VisionProblem.WriteRateLimitedAsync(context.HttpContext, retryAfterSeconds, cancellationToken: cancellationToken);
    }
}
