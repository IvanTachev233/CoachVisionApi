namespace CoachVision.Data.Entities;

/// <summary>
/// A tenant's API key. Only its SHA-256 hash is stored; the key itself is shown once, at
/// issuance, and is never recoverable from this table.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>SHA-256 of the key's secret bytes.</summary>
    public required byte[] KeyHash { get; set; }

    /// <summary>"live" or "test".</summary>
    public required string Kind { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }
}
