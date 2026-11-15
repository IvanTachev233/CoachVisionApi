using CoachVision.TestSupport;
using Npgsql;
using NpgsqlTypes;

namespace CoachVision.Data.Tests;

/// <summary>
/// <c>lookup_api_key_by_hash</c> is the one deliberate row-security bypass in the schema (the
/// migration that creates it explains why it needs one): these tests prove it works for the seam
/// authentication needs, and that nothing wider leaked out alongside it.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class LookupApiKeyByHashTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task A_seeded_hash_is_found_with_no_tenant_set()
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        var tenantId = await InsertTenantAsync(migrator);
        var hash = RandomHash();
        var apiKeyId = await InsertApiKeyAsync(migrator, tenantId, hash, "live");

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        // No SetTenantAsync: this is exactly the situation the function exists for — no tenant is
        // known yet, so a direct SELECT on api_keys would see nothing (asserted below).
        await using var command = runtime.CreateCommand();
        command.CommandText = "SELECT tenant_id, api_key_id, kind, revoked_at FROM lookup_api_key_by_hash($1)";
        command.Parameters.Add(new NpgsqlParameter { Value = hash, NpgsqlDbType = NpgsqlDbType.Bytea });
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(tenantId, reader.GetGuid(0));
        Assert.Equal(apiKeyId, reader.GetGuid(1));
        Assert.Equal("live", reader.GetString(2));
        Assert.True(await reader.IsDBNullAsync(3, TestContext.Current.CancellationToken));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_hash_returns_no_row()
    {
        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var command = runtime.CreateCommand();
        command.CommandText = "SELECT 1 FROM lookup_api_key_by_hash($1)";
        command.Parameters.Add(new NpgsqlParameter { Value = RandomHash(), NpgsqlDbType = NpgsqlDbType.Bytea });
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_direct_select_on_api_keys_still_sees_nothing_with_no_tenant_set()
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        var tenantId = await InsertTenantAsync(migrator);
        await InsertApiKeyAsync(migrator, tenantId, RandomHash(), "live");

        // The function bypasses row security for its own narrow read; a direct query through the
        // run-time role, with still no tenant set, must not.
        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var command = runtime.CreateCommand();
        command.CommandText = "SELECT count(*) FROM api_keys";
        var count = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Public_has_no_execute_on_the_function()
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        await using var command = migrator.CreateCommand();
        command.CommandText = "SELECT has_function_privilege('public', 'lookup_api_key_by_hash(bytea)', 'EXECUTE')";
        var publicCanExecute = (bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        Assert.False(publicCanExecute);
    }

    private static async Task<Guid> InsertTenantAsync(NpgsqlConnection migrator)
    {
        await using var command = migrator.CreateCommand();
        command.CommandText = "INSERT INTO tenants (name, created_at) VALUES ($1, now()) RETURNING id";
        command.Parameters.Add(new NpgsqlParameter { Value = "tenant-" + Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Text });
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<Guid> InsertApiKeyAsync(NpgsqlConnection migrator, Guid tenantId, byte[] hash, string kind)
    {
        await using var command = migrator.CreateCommand();
        command.CommandText = "INSERT INTO api_keys (tenant_id, key_hash, kind, created_at) VALUES ($1, $2, $3, now()) RETURNING id";
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = hash, NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter { Value = kind, NpgsqlDbType = NpgsqlDbType.Text });
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static byte[] RandomHash() => System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());
}
