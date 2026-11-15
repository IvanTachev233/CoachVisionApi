using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CoachVision.Data.Tenancy;

/// <summary>
/// Opens one tenant's view of the database for exactly one transaction.
///
/// Row-level security policies read <c>app_current_tenant_id()</c>, backed by
/// <c>current_setting('app.tenant_id', true)</c>. That setting must be transaction-local
/// (<c>set_config(..., true)</c>, the third argument), never session-level: Neon's pooled
/// endpoint runs PgBouncer in transaction mode, where session commands (plain <c>SET</c>,
/// <c>RESET</c>, session-level advisory locks, ...) aren't supported at all, not merely
/// unreliable. Scoping the setting to one transaction, alongside the queries that depend on it,
/// is what the pooled endpoint actually supports.
/// </summary>
public static class TenantScope
{
    /// <summary>
    /// Begins a transaction with the tenant set for its lifetime. The caller must commit (or let
    /// disposal roll back); nothing is cleaned up automatically, matching
    /// <see cref="DbContext.Database"/>'s normal transaction contract.
    /// </summary>
    public static async Task<IDbContextTransaction> BeginTenantScopeAsync(
        this DbContext context,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)",
            cancellationToken);
        return transaction;
    }
}
