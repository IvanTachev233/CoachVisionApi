using CoachVision.Data.Entities;
using CoachVision.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoachVision.Data.Security;

/// <summary>
/// Issuing and revoking a tenant's keys. Every method here must run inside a transaction
/// already scoped to the tenant (<see cref="Tenancy.TenantScope"/>) except <see cref="IssueAsync"/>
/// and <see cref="ProvisionTenantAsync"/>, which open their own: issuing is the one write that
/// must also survive a race with a concurrent issue for the same tenant and kind, which the
/// database migration's trigger (not this C# count-then-insert) is what actually prevents — the
/// check below is only a fast, friendly error for the common case, not the enforcement.
/// </summary>
public static class ApiKeyService
{
    /// <summary>
    /// Creates a tenant and its default <see cref="TenantLimits"/> row in one transaction. The
    /// tenant's id is generated here (not left to the database default) because row security's
    /// <c>WITH CHECK (id = app_current_tenant_id())</c> needs the id before the insert, to set the
    /// transaction-local tenant to it.
    /// </summary>
    public static async Task<Tenant> ProvisionTenantAsync(
        VisionDbContext db,
        string name,
        bool trainingAllowed = false,
        CancellationToken cancellationToken = default)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = name, CreatedAtUtc = DateTime.UtcNow, TrainingAllowed = trainingAllowed };

        await using var transaction = await db.BeginTenantScopeAsync(tenant.Id, cancellationToken);
        db.Tenants.Add(tenant);
        db.TenantLimits.Add(new TenantLimits { TenantId = tenant.Id });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return tenant;
    }

    /// <summary>
    /// Sets a tenant's own training flag (<c>Tenant.TrainingAllowed</c>), off by default for
    /// every tenant. The run-time role has UPDATE on <c>tenants</c> (the
    /// <c>AddTenantIsolation</c> migration's grant), so this runs under the tenant's own scope
    /// the same way <see cref="RevokeAsync"/> does, rather than needing the migrator role.
    /// </summary>
    public static async Task SetTenantTrainingAsync(VisionDbContext db, Guid tenantId, bool trainingAllowed, CancellationToken cancellationToken = default)
    {
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId, cancellationToken);
        tenant.TrainingAllowed = trainingAllowed;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Issues one key for an existing tenant. Throws <see cref="ApiKeyRotationLimitExceededException"/>
    /// if the tenant already has two active keys of that kind — whether this check or the
    /// database trigger caught it (both map to the trigger's exception, since the check here can
    /// race and lose, see the class remarks).
    /// </summary>
    public static async Task<(ApiKey Key, string Plaintext)> IssueAsync(
        VisionDbContext db,
        Guid tenantId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var (plaintext, hash) = ApiKeySecrets.Generate(kind);
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeyHash = hash,
            Kind = kind,
            CreatedAtUtc = DateTime.UtcNow,
        };

        await using var transaction = await db.BeginTenantScopeAsync(tenantId, cancellationToken);
        db.ApiKeys.Add(apiKey);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23514" } pg
            && pg.MessageText.Contains("api_key_rotation_limit_exceeded", StringComparison.Ordinal))
        {
            throw new ApiKeyRotationLimitExceededException(tenantId, kind);
        }

        await transaction.CommitAsync(cancellationToken);
        return (apiKey, plaintext);
    }

    /// <summary>Revokes one of the tenant's keys. The caller's transaction must already be scoped to that tenant.</summary>
    public static async Task RevokeAsync(VisionDbContext db, Guid apiKeyId, CancellationToken cancellationToken = default)
    {
        var apiKey = await db.ApiKeys.SingleAsync(k => k.Id == apiKeyId, cancellationToken);
        apiKey.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
