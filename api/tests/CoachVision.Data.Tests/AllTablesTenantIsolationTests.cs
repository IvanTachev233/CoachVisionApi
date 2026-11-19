using CoachVision.TestSupport;
using Npgsql;
using NpgsqlTypes;

namespace CoachVision.Data.Tests;

/// <summary>
/// The same guarantees <see cref="TenantIsolationTests"/> proves for <c>jobs</c>, repeated for
/// every other tenant-owned table (<c>api_keys</c>, <c>results</c>, <c>usage</c>), plus the one
/// case not yet covered for <c>jobs</c> either: moving a tenant's own row to another tenant.
/// <c>tenants</c> is covered separately, since it has no <c>tenant_id</c> column of its own.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class AllTablesTenantIsolationTests(VisionPostgresFixture fixture)
{
    [Theory]
    [InlineData("api_keys")]
    [InlineData("jobs")]
    [InlineData("results")]
    [InlineData("usage")]
    [InlineData("job_labels")]
    public async Task Tenant_A_cannot_read_update_delete_or_insert_into_tenant_Bs_rows(string table)
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        var tenantA = await InsertTenantAsync(migrator);
        var tenantB = await InsertTenantAsync(migrator);
        var jobA = await InsertJobAsync(migrator, tenantA);
        var jobB = await InsertJobAsync(migrator, tenantB);
        var (idType, rowA, rowB) = await SeedOwnRowsAsync(migrator, table, tenantA, tenantB, jobA, jobB);

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(runtime, tenantA);

        Assert.Equal(1, await CountByIdAsync(runtime, table, rowA, idType));
        Assert.Equal(0, await CountByIdAsync(runtime, table, rowB, idType));

        Assert.Equal(0, await ExecuteAsync(runtime, BenignUpdateSql(table), rowB, idType));
        Assert.Equal(0, await ExecuteAsync(runtime, $"DELETE FROM {table} WHERE id = $1", rowB, idType));

        // Each risky statement runs inside its own SAVEPOINT (a plain SQL savepoint, not a nested
        // ADO.NET transaction — Npgsql's connection only tracks one transaction at a time): once
        // a statement errors, Postgres aborts the rest of the enclosing transaction until a
        // ROLLBACK, so without a savepoint to roll back to, the second risky statement would fail
        // with "transaction is aborted" (25P02) instead of the real error being tested for.
        await RunInSavepointAsync(runtime, async () =>
        {
            var insertException = await Assert.ThrowsAsync<PostgresException>(
                () => InsertStampedWithOtherTenantAsync(runtime, table, tenantB, jobB));
            Assert.Equal("42501", insertException.SqlState);
        });

        // Moving A's own row to B: the WITH CHECK clause must refuse it even though A can select
        // the row to begin with. For results/usage this can surface as either the row-security
        // violation (session tenant stays A while the new row claims tenant B) or the composite
        // foreign key (job_id still names A's job, which no longer matches (B, job_id)); for
        // api_keys, a BEFORE UPDATE trigger (every column but revoked_at is immutable after
        // insert) raises its own check violation before row security even runs. Whichever
        // Postgres evaluates first, all three prove the move is impossible.
        await RunInSavepointAsync(runtime, async () =>
        {
            var moveException = await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsync(runtime, $"UPDATE {table} SET tenant_id = $2 WHERE id = $1", rowA, idType, tenantB));
            Assert.Contains(moveException.SqlState, new[] { "42501", "23503", "23514" });
        });

        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task With_no_tenant_set_every_table_is_empty_to_the_runtime_role()
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        var tenantA = await InsertTenantAsync(migrator);
        var jobA = await InsertJobAsync(migrator, tenantA);
        await InsertApiKeyAsync(migrator, tenantA);
        await InsertResultAsync(migrator, tenantA, jobA);
        await InsertUsageAsync(migrator, tenantA, jobA);
        await InsertJobLabelAsync(migrator, tenantA, jobA);

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        // Deliberately not calling SetTenantAsync.

        foreach (var table in new[] { "tenants", "api_keys", "jobs", "results", "usage", "tenant_limits", "job_labels" })
        {
            await using var command = runtime.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table}";
            var count = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(0, count);
        }

        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tenants_table_isolation_keyed_by_its_own_id()
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        var tenantA = await InsertTenantAsync(migrator);
        var tenantB = await InsertTenantAsync(migrator);

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(runtime, tenantA);

        Assert.Equal(1, await CountByIdAsync(runtime, "tenants", tenantA, NpgsqlDbType.Uuid));
        Assert.Equal(0, await CountByIdAsync(runtime, "tenants", tenantB, NpgsqlDbType.Uuid));

        Assert.Equal(0, await ExecuteAsync(runtime, "UPDATE tenants SET name = 'renamed' WHERE id = $1", tenantB, NpgsqlDbType.Uuid));
        Assert.Equal(0, await ExecuteAsync(runtime, "DELETE FROM tenants WHERE id = $1", tenantB, NpgsqlDbType.Uuid));

        // A new tenant row can only be inserted with id = the session's own tenant (there is no
        // legitimate way to "create" a different tenant from inside a tenant-scoped session).
        await using var insert = runtime.CreateCommand();
        insert.CommandText = "INSERT INTO tenants (id, name, created_at) VALUES ($1, 'sneaky', now())";
        insert.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Uuid });
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("42501", exception.SqlState);

        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TenantLimits_table_isolation_keyed_by_tenant_id()
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        var tenantA = await InsertTenantAsync(migrator);
        var tenantB = await InsertTenantAsync(migrator);
        await InsertTenantLimitsAsync(migrator, tenantA);
        await InsertTenantLimitsAsync(migrator, tenantB);

        await using var runtime = await fixture.OpenRuntimeConnectionAsync();
        await using var tx = await runtime.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(runtime, tenantA);

        Assert.Equal(1, await CountByIdAsync(runtime, "tenant_limits", tenantA, NpgsqlDbType.Uuid, idColumn: "tenant_id"));
        Assert.Equal(0, await CountByIdAsync(runtime, "tenant_limits", tenantB, NpgsqlDbType.Uuid, idColumn: "tenant_id"));

        await using (var update = runtime.CreateCommand())
        {
            update.CommandText = "UPDATE tenant_limits SET rate_limit_per_minute = 1 WHERE tenant_id = $1";
            update.Parameters.Add(new NpgsqlParameter { Value = tenantB, NpgsqlDbType = NpgsqlDbType.Uuid });
            Assert.Equal(0, await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await using (var delete = runtime.CreateCommand())
        {
            delete.CommandText = "DELETE FROM tenant_limits WHERE tenant_id = $1";
            delete.Parameters.Add(new NpgsqlParameter { Value = tenantB, NpgsqlDbType = NpgsqlDbType.Uuid });
            Assert.Equal(0, await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await RunInSavepointAsync(runtime, async () =>
        {
            await using var insert = runtime.CreateCommand();
            insert.CommandText = """
                INSERT INTO tenant_limits (tenant_id, rate_limit_per_minute, concurrent_jobs_limit, daily_minutes_limit, max_clip_size_bytes, max_clip_length_seconds)
                VALUES ($1, 60, 5, 120, 209715200, 60)
                """;
            insert.Parameters.Add(new NpgsqlParameter { Value = tenantB, NpgsqlDbType = NpgsqlDbType.Uuid });
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            Assert.Equal("42501", exception.SqlState);
        });

        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertTenantLimitsAsync(NpgsqlConnection migrator, Guid tenantId)
    {
        await using var command = migrator.CreateCommand();
        command.CommandText = """
            INSERT INTO tenant_limits (tenant_id, rate_limit_per_minute, concurrent_jobs_limit, daily_minutes_limit, max_clip_size_bytes, max_clip_length_seconds)
            VALUES ($1, 60, 5, 120, 209715200, 60)
            """;
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(NpgsqlDbType IdType, object RowA, object RowB)> SeedOwnRowsAsync(
        NpgsqlConnection migrator, string table, Guid tenantA, Guid tenantB, string jobA, string jobB)
    {
        return table switch
        {
            "jobs" => (NpgsqlDbType.Text, jobA, jobB),
            "api_keys" => (NpgsqlDbType.Uuid, await InsertApiKeyAsync(migrator, tenantA), await InsertApiKeyAsync(migrator, tenantB)),
            "results" => (NpgsqlDbType.Uuid, await InsertResultAsync(migrator, tenantA, jobA), await InsertResultAsync(migrator, tenantB, jobB)),
            "usage" => (NpgsqlDbType.Uuid, await InsertUsageAsync(migrator, tenantA, jobA), await InsertUsageAsync(migrator, tenantB, jobB)),
            "job_labels" => (NpgsqlDbType.Uuid, await InsertJobLabelAsync(migrator, tenantA, jobA), await InsertJobLabelAsync(migrator, tenantB, jobB)),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "unknown table"),
        };
    }

    private static string BenignUpdateSql(string table) => table switch
    {
        "jobs" => "UPDATE jobs SET status = 'failed' WHERE id = $1",
        "api_keys" => "UPDATE api_keys SET kind = 'live' WHERE id = $1",
        "results" => "UPDATE results SET feature = 'velocity' WHERE id = $1",
        "usage" => "UPDATE usage SET feature = 'velocity' WHERE id = $1",
        "job_labels" => "UPDATE job_labels SET assessment = 'incorrect', reason = 'wrong_lift' WHERE id = $1",
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "unknown table"),
    };

    private static async Task InsertStampedWithOtherTenantAsync(NpgsqlConnection runtime, string table, Guid otherTenant, string otherTenantsJob)
    {
        await using var command = runtime.CreateCommand();
        switch (table)
        {
            case "jobs":
                command.CommandText = """
                    INSERT INTO jobs (id, tenant_id, status, schema_version, lift, features, subject_id,
                                       training_allowed, created_at, updated_at)
                    VALUES ($1, $2, 'awaiting_upload', '1.0', 'squat', ARRAY['bar_path'], 'sub_1', false, now(), now())
                    """;
                command.Parameters.Add(new NpgsqlParameter { Value = NewJobId(), NpgsqlDbType = NpgsqlDbType.Text });
                command.Parameters.Add(new NpgsqlParameter { Value = otherTenant, NpgsqlDbType = NpgsqlDbType.Uuid });
                break;
            case "api_keys":
                command.CommandText = "INSERT INTO api_keys (tenant_id, key_hash, kind, created_at) VALUES ($1, $2, 'test', now())";
                command.Parameters.Add(new NpgsqlParameter { Value = otherTenant, NpgsqlDbType = NpgsqlDbType.Uuid });
                command.Parameters.Add(new NpgsqlParameter { Value = RandomHash(), NpgsqlDbType = NpgsqlDbType.Bytea });
                break;
            case "results":
                // job_id names otherTenant's own job, so only row security (not the composite
                // foreign key) is what refuses this from a tenant-A session.
                command.CommandText = "INSERT INTO results (tenant_id, job_id, feature, payload, created_at) VALUES ($1, $2, 'bar_path', '{}', now())";
                command.Parameters.Add(new NpgsqlParameter { Value = otherTenant, NpgsqlDbType = NpgsqlDbType.Uuid });
                command.Parameters.Add(new NpgsqlParameter { Value = otherTenantsJob, NpgsqlDbType = NpgsqlDbType.Text });
                break;
            case "usage":
                command.CommandText = "INSERT INTO usage (tenant_id, job_id, feature, video_seconds, occurred_at) VALUES ($1, $2, 'bar_path', 9.4, now())";
                command.Parameters.Add(new NpgsqlParameter { Value = otherTenant, NpgsqlDbType = NpgsqlDbType.Uuid });
                command.Parameters.Add(new NpgsqlParameter { Value = otherTenantsJob, NpgsqlDbType = NpgsqlDbType.Text });
                break;
            case "job_labels":
                // job_id names otherTenant's own job, so only row security (not the composite
                // foreign key) is what refuses this from a tenant-A session.
                command.CommandText = "INSERT INTO job_labels (tenant_id, job_id, assessment, updated_at) VALUES ($1, $2, 'correct', now())";
                command.Parameters.Add(new NpgsqlParameter { Value = otherTenant, NpgsqlDbType = NpgsqlDbType.Uuid });
                command.Parameters.Add(new NpgsqlParameter { Value = otherTenantsJob, NpgsqlDbType = NpgsqlDbType.Text });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(table), table, "unknown table");
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task RunInSavepointAsync(NpgsqlConnection connection, Func<Task> action)
    {
        await using (var savepoint = connection.CreateCommand())
        {
            savepoint.CommandText = "SAVEPOINT risky_statement";
            await savepoint.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await action();

        await using var rollback = connection.CreateCommand();
        rollback.CommandText = "ROLLBACK TO SAVEPOINT risky_statement";
        await rollback.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', $1, true)";
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId.ToString(), NpgsqlDbType = NpgsqlDbType.Text });
        await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountByIdAsync(NpgsqlConnection connection, string table, object id, NpgsqlDbType idType, string idColumn = "id")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table} WHERE {idColumn} = $1";
        command.Parameters.Add(new NpgsqlParameter { Value = id, NpgsqlDbType = idType });
        return (int)(long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, object id, NpgsqlDbType idType, Guid? second = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter { Value = id, NpgsqlDbType = idType });
        if (second.HasValue)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = second.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        }

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> InsertTenantAsync(NpgsqlConnection migrator)
    {
        await using var command = migrator.CreateCommand();
        command.CommandText = "INSERT INTO tenants (name, created_at) VALUES ($1, now()) RETURNING id";
        command.Parameters.Add(new NpgsqlParameter { Value = "tenant-" + Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Text });
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<string> InsertJobAsync(NpgsqlConnection migrator, Guid tenantId)
    {
        var jobId = NewJobId();
        await using var command = migrator.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (id, tenant_id, status, schema_version, lift, features, subject_id,
                               training_allowed, created_at, updated_at)
            VALUES ($1, $2, 'awaiting_upload', '1.0', 'squat', ARRAY['bar_path'], 'sub_1', false, now(), now())
            """;
        command.Parameters.Add(new NpgsqlParameter { Value = jobId, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return jobId;
    }

    private static async Task<Guid> InsertApiKeyAsync(NpgsqlConnection migrator, Guid tenantId)
    {
        await using var command = migrator.CreateCommand();
        command.CommandText = "INSERT INTO api_keys (tenant_id, key_hash, kind, created_at) VALUES ($1, $2, 'test', now()) RETURNING id";
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = RandomHash(), NpgsqlDbType = NpgsqlDbType.Bytea });
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<Guid> InsertResultAsync(NpgsqlConnection migrator, Guid tenantId, string jobId)
    {
        await using var command = migrator.CreateCommand();
        command.CommandText = "INSERT INTO results (tenant_id, job_id, feature, payload, created_at) VALUES ($1, $2, 'bar_path', '{}', now()) RETURNING id";
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = jobId, NpgsqlDbType = NpgsqlDbType.Text });
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<Guid> InsertUsageAsync(NpgsqlConnection migrator, Guid tenantId, string jobId)
    {
        await using var command = migrator.CreateCommand();
        command.CommandText = "INSERT INTO usage (tenant_id, job_id, feature, video_seconds, occurred_at) VALUES ($1, $2, 'bar_path', 9.4, now()) RETURNING id";
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = jobId, NpgsqlDbType = NpgsqlDbType.Text });
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<Guid> InsertJobLabelAsync(NpgsqlConnection migrator, Guid tenantId, string jobId)
    {
        await using var command = migrator.CreateCommand();
        command.CommandText = "INSERT INTO job_labels (tenant_id, job_id, assessment, updated_at) VALUES ($1, $2, 'correct', now()) RETURNING id";
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = jobId, NpgsqlDbType = NpgsqlDbType.Text });
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static string NewJobId() => $"job_{Guid.NewGuid():N}0000000"[..30];

    private static byte[] RandomHash() => System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray());
}
