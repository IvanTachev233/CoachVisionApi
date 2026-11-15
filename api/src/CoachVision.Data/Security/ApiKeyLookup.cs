using System.Data;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Security;

/// <summary>One row of <c>lookup_api_key_by_hash</c> (see the AddTenantIsolation migration).</summary>
public sealed record ApiKeyLookupResult(Guid TenantId, Guid ApiKeyId, string Kind, DateTime? RevokedAtUtc);

/// <summary>
/// Calls the one deliberate row-security bypass (<c>lookup_api_key_by_hash</c>) to find which
/// tenant a presented key belongs to, before any tenant is known. Uses the connection's own ADO.NET
/// API directly rather than a tenant-scoped EF query, since there is no tenant to scope to yet.
/// </summary>
public static class ApiKeyLookup
{
    public static async Task<ApiKeyLookupResult?> FindByHashAsync(
        VisionDbContext db,
        byte[] hash,
        CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tenant_id, api_key_id, kind, revoked_at FROM lookup_api_key_by_hash(@hash)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "hash";
        parameter.Value = hash;
        parameter.DbType = DbType.Binary;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ApiKeyLookupResult(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetDateTime(3));
    }
}
