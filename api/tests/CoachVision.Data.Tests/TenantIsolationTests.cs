using CoachVision.Data;
using CoachVision.TestSupport;
using Npgsql;
using NpgsqlTypes;

namespace CoachVision.Data.Tests;

/// <summary>
/// Proves tenant isolation at the database level, as the run-time role sees it: not an
/// application-level filter, so a bug in a later query can't defeat it. Every table seeded here
/// is read, written, and probed through raw SQL rather than EF, so the test exercises exactly
/// what Postgres enforces, not what the ORM happens to generate.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class TenantIsolationTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task Tenant_can_read_its_own_job_but_not_another_tenants()
    {
        var (tenantA, tenantB) = await SeedTwoTenantsWithJobsAsync();

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(runtime, tenantA.TenantId);

        Assert.Equal(1, await CountJobsAsync(runtime, tenantA.JobId));
        Assert.Equal(0, await CountJobsAsync(runtime, tenantB.JobId));

        await tx.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Updating_or_deleting_another_tenants_job_affects_zero_rows()
    {
        var (tenantA, tenantB) = await SeedTwoTenantsWithJobsAsync();

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(runtime, tenantA.TenantId);

        await using var update = runtime.CreateCommand();
        update.CommandText = "UPDATE jobs SET status = 'failed' WHERE id = $1";
        update.Parameters.Add(new NpgsqlParameter { Value = tenantB.JobId, NpgsqlDbType = NpgsqlDbType.Text });
        Assert.Equal(0, await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        await using var delete = runtime.CreateCommand();
        delete.CommandText = "DELETE FROM jobs WHERE id = $1";
        delete.Parameters.Add(new NpgsqlParameter { Value = tenantB.JobId, NpgsqlDbType = NpgsqlDbType.Text });
        Assert.Equal(0, await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        await tx.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Inserting_a_row_stamped_with_another_tenants_id_is_refused()
    {
        var (tenantA, tenantB) = await SeedTwoTenantsWithJobsAsync();

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(runtime, tenantA.TenantId);

        await using var insert = runtime.CreateCommand();
        insert.CommandText = """
            INSERT INTO jobs (id, tenant_id, status, schema_version, lift, features, subject_id,
                               training_allowed, created_at, updated_at)
            VALUES ($1, $2, 'awaiting_upload', '1.0', 'squat', ARRAY['bar_path'], 'sub_1', false, now(), now())
            """;
        insert.Parameters.Add(new NpgsqlParameter { Value = $"job_{Guid.NewGuid():N}0000000", NpgsqlDbType = NpgsqlDbType.Text });
        // Session tenant is A; stamping the row with B's id must be refused by the WITH CHECK
        // clause, not silently re-owned or silently accepted.
        insert.Parameters.Add(new NpgsqlParameter { Value = tenantB.TenantId, NpgsqlDbType = NpgsqlDbType.Uuid });

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("42501", exception.SqlState); // insufficient_privilege: a row-security policy violation.

        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task With_no_tenant_set_nothing_is_visible_or_writable()
    {
        var (tenantA, _) = await SeedTwoTenantsWithJobsAsync();

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        // Deliberately not calling SetTenantAsync: app.tenant_id is unset for this transaction.

        Assert.Equal(0, await CountJobsAsync(runtime, tenantA.JobId));

        await using var insert = runtime.CreateCommand();
        insert.CommandText = """
            INSERT INTO jobs (id, tenant_id, status, schema_version, lift, features, subject_id,
                               training_allowed, created_at, updated_at)
            VALUES ($1, $2, 'awaiting_upload', '1.0', 'squat', ARRAY['bar_path'], 'sub_1', false, now(), now())
            """;
        insert.Parameters.Add(new NpgsqlParameter { Value = $"job_{Guid.NewGuid():N}0000000", NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new NpgsqlParameter { Value = tenantA.TenantId, NpgsqlDbType = NpgsqlDbType.Uuid });

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("42501", exception.SqlState);

        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Runtime_role_can_insert_a_usage_row_with_no_job_id()
    {
        var (tenantA, _) = await SeedTwoTenantsWithJobsAsync();

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(runtime, tenantA.TenantId);

        // UsageRecord.JobId is nullable: a usage record outlives the job it was billed for. The
        // run-time role must be able to write that row (its own composite foreign key to jobs is
        // skipped when job_id is null, Postgres MATCH SIMPLE).
        await using var insert = runtime.CreateCommand();
        insert.CommandText = """
            INSERT INTO usage (tenant_id, job_id, feature, video_seconds, occurred_at)
            VALUES ($1, NULL, 'bar_path', 12.5, now())
            """;
        insert.Parameters.Add(new NpgsqlParameter { Value = tenantA.TenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        await tx.CommitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("tenants")]
    [InlineData("api_keys")]
    [InlineData("jobs")]
    [InlineData("results")]
    [InlineData("usage")]
    [InlineData("tenant_limits")]
    public async Task Every_tenant_owned_table_forces_row_level_security(string table)
    {
        await using var admin = new NpgsqlConnection(fixture.MigratorConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = $1";
        command.Parameters.Add(new NpgsqlParameter { Value = table, NpgsqlDbType = NpgsqlDbType.Text });
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken), $"table {table} not found");
        Assert.True(reader.GetBoolean(0), $"{table}: RLS not enabled");
        Assert.True(reader.GetBoolean(1), $"{table}: RLS not forced (table owner would bypass it)");
    }

    private async Task<(TenantSeed TenantA, TenantSeed TenantB)> SeedTwoTenantsWithJobsAsync()
    {
        await using var admin = new NpgsqlConnection(fixture.MigratorConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);

        var tenantA = await SeedOneTenantWithJobAsync(admin, "tenant-a-" + Guid.NewGuid());
        var tenantB = await SeedOneTenantWithJobAsync(admin, "tenant-b-" + Guid.NewGuid());
        return (tenantA, tenantB);
    }

    private static async Task<TenantSeed> SeedOneTenantWithJobAsync(NpgsqlConnection admin, string name)
    {
        await using var insertTenant = admin.CreateCommand();
        insertTenant.CommandText = "INSERT INTO tenants (name, created_at) VALUES ($1, now()) RETURNING id";
        insertTenant.Parameters.Add(new NpgsqlParameter { Value = name, NpgsqlDbType = NpgsqlDbType.Text });
        var tenantId = (Guid)(await insertTenant.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        var jobId = $"job_{Guid.NewGuid():N}0000000"[..30];
        await using var insertJob = admin.CreateCommand();
        insertJob.CommandText = """
            INSERT INTO jobs (id, tenant_id, status, schema_version, lift, features, subject_id,
                               training_allowed, created_at, updated_at)
            VALUES ($1, $2, 'awaiting_upload', '1.0', 'squat', ARRAY['bar_path'], 'sub_1', false, now(), now())
            """;
        insertJob.Parameters.Add(new NpgsqlParameter { Value = jobId, NpgsqlDbType = NpgsqlDbType.Text });
        insertJob.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        await insertJob.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return new TenantSeed(tenantId, jobId);
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', $1, true)";
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId.ToString(), NpgsqlDbType = NpgsqlDbType.Text });
        await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountJobsAsync(NpgsqlConnection connection, string jobId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM jobs WHERE id = $1";
        command.Parameters.Add(new NpgsqlParameter { Value = jobId, NpgsqlDbType = NpgsqlDbType.Text });
        return (int)(long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private sealed record TenantSeed(Guid TenantId, string JobId);
}
