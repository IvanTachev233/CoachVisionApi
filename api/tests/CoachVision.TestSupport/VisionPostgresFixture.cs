using System.Security.Cryptography;
using CoachVision.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace CoachVision.TestSupport;

/// <summary>
/// One throwaway Postgres container, migrated once, shared by every test in the
/// <see cref="VisionDbCollection"/>.
///
/// The container's own bootstrap user is a real Postgres superuser, which Neon's projects don't
/// have at all, so nothing here connects as it beyond initial setup. Instead, setup creates
/// <c>vision_migrator</c>: a non-superuser role with <c>LOGIN CREATEROLE BYPASSRLS</c>, made the
/// test database's owner. That mirrors what Neon's docs (<c>manage/roles</c>,
/// <c>reference/compatibility</c>) say about roles made through the Console, CLI or API
/// (including a project's default <c>&lt;db&gt;_owner</c>): they join <c>neon_superuser</c>
/// (<c>CREATEDB</c>, <c>CREATEROLE</c>, <c>BYPASSRLS</c>), since Neon projects have no real
/// superuser. <see cref="MigratorConnectionString"/> is this role, not the container's bootstrap
/// user, so a migration that only the bootstrap superuser could run (and that
/// <c>vision_migrator</c>/Neon's owner role could not) fails here the same way it would on Neon.
///
/// <see cref="RuntimeConnectionString"/> is the least-privileged <c>vision_api_runtime</c> role
/// the migration creates, with a password generated for this run only and never written anywhere.
///
/// Shared by <c>CoachVision.Data.Tests</c> and <c>CoachVision.Api.Tests</c>: kept in its own
/// project rather than copied into each test assembly.
/// </summary>
public sealed class VisionPostgresFixture : IAsyncLifetime
{
    private const string MigratorRole = "vision_migrator";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithPassword(RandomSecret())
        .Build();

    public string MigratorConnectionString { get; private set; } = string.Empty;

    public string RuntimeConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var bootstrapConnectionString = _container.GetConnectionString();
        var migratorPassword = RandomSecret();
        var databaseName = new NpgsqlConnectionStringBuilder(bootstrapConnectionString).Database!;

        await using var bootstrap = new NpgsqlConnection(bootstrapConnectionString);
        await bootstrap.OpenAsync();
        await using (var createRole = bootstrap.CreateCommand())
        {
            // Not the container's own superuser: Neon's owner role is CREATEROLE + BYPASSRLS, not
            // SUPERUSER (Neon projects have no real superuser at all), and migrations must run as
            // something no more privileged than that, or a privilege gap here would hide a
            // migration step that fails on Neon.
            createRole.CommandText = $"CREATE ROLE {MigratorRole} LOGIN CREATEROLE BYPASSRLS PASSWORD '{migratorPassword}'";
            await createRole.ExecuteNonQueryAsync();
        }

        await using (var ownDatabase = bootstrap.CreateCommand())
        {
            ownDatabase.CommandText = $"ALTER DATABASE \"{databaseName}\" OWNER TO {MigratorRole}";
            await ownDatabase.ExecuteNonQueryAsync();
        }

        var migratorBuilder = new NpgsqlConnectionStringBuilder(bootstrapConnectionString)
        {
            Username = MigratorRole,
            Password = migratorPassword,
        };
        MigratorConnectionString = migratorBuilder.ConnectionString;

        using var migratorContext = CreateContext(MigratorConnectionString);
        await migratorContext.Database.MigrateAsync();

        var runtimePassword = RandomSecret();
        await using var migrator = new NpgsqlConnection(MigratorConnectionString);
        await migrator.OpenAsync();
        await using var alterRole = migrator.CreateCommand();
        alterRole.CommandText = $"ALTER ROLE {VisionDbOptions.RuntimeRoleName} WITH LOGIN PASSWORD '{runtimePassword}'";
        await alterRole.ExecuteNonQueryAsync();

        var runtimeBuilder = new NpgsqlConnectionStringBuilder(MigratorConnectionString)
        {
            Username = VisionDbOptions.RuntimeRoleName,
            Password = runtimePassword,
        };
        RuntimeConnectionString = runtimeBuilder.ConnectionString;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public static VisionDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<VisionDbContext>().UseNpgsql(connectionString).Options;
        return new VisionDbContext(options);
    }

    /// <summary>A fresh connection as the run-time role, for raw SQL assertions outside EF.</summary>
    public async Task<NpgsqlConnection> OpenRuntimeConnectionAsync()
    {
        var connection = new NpgsqlConnection(RuntimeConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>A fresh connection as the migrator role, for seeding data that bypasses RLS.</summary>
    public async Task<NpgsqlConnection> OpenMigratorConnectionAsync()
    {
        var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}

/// <summary>
/// The collection name shared by every test assembly's own <c>[CollectionDefinition]</c>. xUnit
/// v3 requires that attribute to live in the same assembly as the tests that use it (xUnit1041),
/// so it can't be declared once here alongside the fixture — each test project redeclares a
/// one-line collection definition under this name instead.
/// </summary>
public static class VisionDbCollection
{
    public const string Name = "vision-db";
}
