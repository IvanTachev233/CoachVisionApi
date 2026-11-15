namespace CoachVision.Data.Security;

/// <summary>Thrown when a tenant already has two active keys of the kind being issued.</summary>
public sealed class ApiKeyRotationLimitExceededException(Guid tenantId, string kind)
    : Exception($"Tenant {tenantId} already has 2 active {kind} keys; revoke one before issuing another.")
{
    public Guid TenantId { get; } = tenantId;

    public string Kind { get; } = kind;
}
