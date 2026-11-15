using CoachVision.TestSupport;
using Npgsql;

namespace CoachVision.Data.Tests;

/// <summary>
/// The run-time role has exactly the privileges the API needs to read and write rows, and none of
/// the privileges that would let it defeat row-level security from inside the API process.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class RuntimeRoleGuardTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task Runtime_role_cannot_disable_row_level_security()
    {
        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var command = runtime.CreateCommand();
        command.CommandText = "ALTER TABLE jobs DISABLE ROW LEVEL SECURITY";

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("42501", exception.SqlState); // insufficient_privilege: not the table owner.
    }

    [Fact]
    public async Task Runtime_role_cannot_alter_tables()
    {
        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var command = runtime.CreateCommand();
        command.CommandText = "ALTER TABLE jobs ADD COLUMN sneaky text";

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Runtime_role_does_not_have_bypassrls_or_superuser()
    {
        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var command = runtime.CreateCommand();
        command.CommandText = "SELECT rolbypassrls, rolsuper FROM pg_roles WHERE rolname = current_user";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Runtime_role_cannot_create_databases_roles_or_replicate()
    {
        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var command = runtime.CreateCommand();
        command.CommandText = "SELECT rolcreatedb, rolcreaterole, rolreplication FROM pg_roles WHERE rolname = current_user";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
    }

    [Fact]
    public async Task Runtime_role_owns_no_table()
    {
        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var command = runtime.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM pg_tables
            WHERE schemaname = 'public' AND tableowner = current_user
            """;
        var ownedTables = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(0, ownedTables);
    }
}
