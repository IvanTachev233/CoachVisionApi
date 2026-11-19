namespace CoachVision.Data.Entities;

/// <summary>One Vision customer (LiftBig is the first). Every tenant-owned table carries this id.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Whether this tenant's jobs may ever be used for training, defaulting to <c>false</c> for
    /// every tenant, external ones included (spec.md, ADR 0017). A job is eligible for training
    /// only when this is true <em>and</em> the job's own <c>Job.TrainingAllowed</c> is true (see
    /// <c>Training.TrainingEligibility</c>) — this flag can only narrow a job's own setting, never
    /// widen it.
    /// </summary>
    public bool TrainingAllowed { get; set; }
}
