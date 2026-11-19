namespace CoachVision.Data.Entities;

/// <summary>
/// The end user's opinion of one job's result (contract: `JobLabel`). A job has at most one
/// label, enforced by a unique index on (<see cref="TenantId"/>, <see cref="JobId"/>) — the
/// endpoint overwrites the existing row in place rather than inserting a second one. Deleted with
/// its job through the composite foreign key, the same pattern <see cref="JobResult"/> and
/// <see cref="UsageRecord"/> use.
/// </summary>
public sealed class JobLabel
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string JobId { get; set; }

    /// <summary>"correct" or "incorrect" (contract: `LabelAssessment`).</summary>
    public required string Assessment { get; set; }

    /// <summary>Set only when <see cref="Assessment"/> is "incorrect" (contract: `LabelReason`).</summary>
    public string? Reason { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
