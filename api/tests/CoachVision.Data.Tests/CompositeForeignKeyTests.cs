using CoachVision.TestSupport;
using Npgsql;
using NpgsqlTypes;

namespace CoachVision.Data.Tests;

/// <summary>
/// The composite-key guarantee the schema relies on everywhere a row points at a job: the foreign
/// key names (tenant_id, job_id), so a result or usage row can't be attached to a job belonging
/// to a different tenant, even by a role (the migrator) that RLS doesn't restrict at all.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class CompositeForeignKeyTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task A_result_cannot_reference_another_tenants_job()
    {
        await using var admin = new NpgsqlConnection(fixture.MigratorConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);

        var tenantA = await InsertTenantAsync(admin);
        var tenantB = await InsertTenantAsync(admin);
        var jobOfTenantB = await InsertJobAsync(admin, tenantB);

        await using var insertResult = admin.CreateCommand();
        insertResult.CommandText = """
            INSERT INTO results (tenant_id, job_id, feature, payload, created_at)
            VALUES ($1, $2, 'bar_path', '{}', now())
            """;
        // Tenant A's id paired with tenant B's job id: no row in jobs has that combination.
        insertResult.Parameters.Add(new NpgsqlParameter { Value = tenantA, NpgsqlDbType = NpgsqlDbType.Uuid });
        insertResult.Parameters.Add(new NpgsqlParameter { Value = jobOfTenantB, NpgsqlDbType = NpgsqlDbType.Text });

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => insertResult.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("23503", exception.SqlState); // foreign_key_violation
    }

    [Fact]
    public async Task A_usage_record_cannot_reference_another_tenants_job()
    {
        await using var admin = new NpgsqlConnection(fixture.MigratorConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);

        var tenantA = await InsertTenantAsync(admin);
        var tenantB = await InsertTenantAsync(admin);
        var jobOfTenantB = await InsertJobAsync(admin, tenantB);

        await using var insertUsage = admin.CreateCommand();
        insertUsage.CommandText = """
            INSERT INTO usage (tenant_id, job_id, feature, video_seconds, occurred_at)
            VALUES ($1, $2, 'bar_path', 9.4, now())
            """;
        insertUsage.Parameters.Add(new NpgsqlParameter { Value = tenantA, NpgsqlDbType = NpgsqlDbType.Uuid });
        insertUsage.Parameters.Add(new NpgsqlParameter { Value = jobOfTenantB, NpgsqlDbType = NpgsqlDbType.Text });

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => insertUsage.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("23503", exception.SqlState);
    }

    [Fact]
    public async Task A_usage_record_with_no_job_is_allowed_a_deleted_jobs_usage_outlives_it()
    {
        await using var admin = new NpgsqlConnection(fixture.MigratorConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);

        var tenantA = await InsertTenantAsync(admin);

        await using var insertUsage = admin.CreateCommand();
        insertUsage.CommandText = "INSERT INTO usage (tenant_id, job_id, feature, video_seconds, occurred_at) VALUES ($1, NULL, 'bar_path', 9.4, now())";
        insertUsage.Parameters.Add(new NpgsqlParameter { Value = tenantA, NpgsqlDbType = NpgsqlDbType.Uuid });

        await insertUsage.ExecuteNonQueryAsync(TestContext.Current.CancellationToken); // Must not throw.
    }

    private static async Task<Guid> InsertTenantAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tenants (name, created_at) VALUES ($1, now()) RETURNING id";
        command.Parameters.Add(new NpgsqlParameter { Value = "tenant-" + Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Text });
        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<string> InsertJobAsync(NpgsqlConnection connection, Guid tenantId)
    {
        var jobId = $"job_{Guid.NewGuid():N}0000000"[..30];
        await using var command = connection.CreateCommand();
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
}
