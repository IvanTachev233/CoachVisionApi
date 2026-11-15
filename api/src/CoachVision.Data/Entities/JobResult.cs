namespace CoachVision.Data.Entities;

/// <summary>
/// One feature's result for one job (<c>BarPathResult</c>, <c>VelocityResult</c>, ... from the
/// OpenAPI contract), kept as JSON. The composite foreign key to <see cref="Job"/> carries
/// <see cref="TenantId"/>, so a result can never be attached to another tenant's job.
/// </summary>
public sealed class JobResult
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string JobId { get; set; }

    /// <summary>"bar_path", "velocity", "rep_count" or "verification_checks".</summary>
    public required string Feature { get; set; }

    public required string PayloadJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
