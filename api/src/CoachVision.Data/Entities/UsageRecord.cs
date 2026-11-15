namespace CoachVision.Data.Entities;

/// <summary>
/// One billable event (a feature that came back <c>analysed</c>). Kept even after a job is
/// deleted (the OpenAPI contract: "a usage record without content ... is kept for billing"), so
/// <see cref="JobId"/> is nullable and its composite foreign key is enforced only while set.
/// </summary>
public sealed class UsageRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string? JobId { get; set; }

    public required string Feature { get; set; }

    /// <summary>The video's length, in seconds, that this feature was billed for (spec.md: "a job's metering records video seconds and each feature that came back analysed").</summary>
    public decimal VideoSeconds { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
