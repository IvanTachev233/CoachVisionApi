namespace CoachVision.Data.Entities;

/// <summary>
/// An analysis job, shaped after <c>contracts/openapi/vision-v1.yaml</c>'s <c>Job</c>. Equipment,
/// video info and failure are kept as JSON: they are read and written whole, never queried by
/// field, so a typed table would only add migrations for fields nothing here needs yet.
/// </summary>
public sealed class Job
{
    /// <summary>"job_" followed by a 26-character ULID, matching the OpenAPI <c>JobId</c> pattern.</summary>
    public required string Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Status { get; set; }

    public required string SchemaVersion { get; set; }

    public required string Lift { get; set; }

    public required string[] Features { get; set; }

    public required string SubjectId { get; set; }

    public bool TrainingAllowed { get; set; }

    public string? ClientReference { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>JSON for <c>Equipment</c> (for example <c>plateDiameterMm</c>), when given.</summary>
    public string? EquipmentJson { get; set; }

    /// <summary>JSON for <c>VideoInfo</c>, present from <c>processing</c> on.</summary>
    public string? VideoInfoJson { get; set; }

    /// <summary>JSON for <c>Failure</c>, present only on a <c>failed</c> job.</summary>
    public string? FailureJson { get; set; }
}
