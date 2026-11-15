namespace CoachVision.Data.Entities;

/// <summary>
/// One tenant's limits: what protects the service and its bill from a single tenant. A row
/// is created alongside its tenant, with the defaults below, and can be changed later (no admin
/// endpoint in the contract yet) only by someone with the migrator connection or a future admin
/// tool.
/// </summary>
public sealed class TenantLimits
{
    public Guid TenantId { get; set; }

    /// <summary>Requests per minute, any <c>/v1</c> route. Default and bound: assumption, Ivan to confirm.</summary>
    public int RateLimitPerMinute { get; set; } = 60;

    /// <summary>Jobs not yet <c>completed</c>/<c>failed</c>, at once. Default: assumption, Ivan to confirm.</summary>
    public int ConcurrentJobsLimit { get; set; } = 5;

    /// <summary>Minutes of video analysed per UTC day. Default: assumption, Ivan to confirm.</summary>
    public decimal DailyMinutesLimit { get; set; } = 120m;

    /// <summary>
    /// Bytes, at most the contract's <c>VideoDeclaration.sizeBytes</c> maximum (209715200 = 200
    /// MiB). Default: the contract's own maximum (spec.md Tier 3 Vision pilot: "200 MiB (the
    /// contract's limit)").
    /// </summary>
    public long MaxClipSizeBytes { get; set; } = 209_715_200;

    /// <summary>Seconds. Default 60: spec.md Tier 3 Vision pilot, marked there as an assumption.</summary>
    public int MaxClipLengthSeconds { get; set; } = 60;
}
