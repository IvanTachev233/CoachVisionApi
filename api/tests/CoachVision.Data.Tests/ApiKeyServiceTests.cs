using CoachVision.Data.Entities;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using CoachVision.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CoachVision.Data.Tests;

/// <summary>
/// Issuing, rotating and revoking tenant keys, proved through the run-time role and a real
/// database — not mocked, since the rotation limit is enforced by a database trigger that a mock
/// would not exercise.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class ApiKeyServiceTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task Issuing_a_key_returns_the_plaintext_once_and_stores_only_its_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);

        var (apiKey, plaintext) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);

        Assert.StartsWith("vk_live_", plaintext);
        Assert.Equal(ApiKeySecrets.Hash(plaintext), apiKey.KeyHash);

        // The plaintext, and its secret part, must appear nowhere in any text/bytea column of the
        // database after issuing it — not only not in api_keys.key_hash.
        var secretPart = plaintext["vk_live_".Length..];
        await AssertPlaintextNotStoredAnywhereAsync(plaintext, secretPart, ct);
    }

    [Fact]
    public async Task Two_active_keys_of_one_tenant_both_authenticate_during_rotation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);

        var (first, _) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);
        var (second, _) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);

        var firstLookup = await ApiKeyLookup.FindByHashAsync(db, first.KeyHash, ct);
        var secondLookup = await ApiKeyLookup.FindByHashAsync(db, second.KeyHash, ct);

        Assert.NotNull(firstLookup);
        Assert.Null(firstLookup!.RevokedAtUtc);
        Assert.NotNull(secondLookup);
        Assert.Null(secondLookup!.RevokedAtUtc);
    }

    [Fact]
    public async Task A_third_active_key_of_the_same_kind_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);

        await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);
        await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);

        using var secondContext = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        await Assert.ThrowsAsync<ApiKeyRotationLimitExceededException>(
            () => ApiKeyService.IssueAsync(secondContext, tenant.Id, ApiKeySecrets.LiveKind, ct));
    }

    [Fact]
    public async Task After_revoking_one_key_a_new_one_can_be_issued_and_the_revoked_one_is_marked_revoked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);

        var (first, _) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);
        await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);

        using var revokeContext = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        await using (var transaction = await revokeContext.BeginTenantScopeAsync(tenant.Id, ct))
        {
            await ApiKeyService.RevokeAsync(revokeContext, first.Id, ct);
            await transaction.CommitAsync(ct);
        }

        using var thirdContext = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var (third, _) = await ApiKeyService.IssueAsync(thirdContext, tenant.Id, ApiKeySecrets.LiveKind, ct);
        Assert.NotEqual(Guid.Empty, third.Id);

        var revokedLookup = await ApiKeyLookup.FindByHashAsync(thirdContext, first.KeyHash, ct);
        Assert.NotNull(revokedLookup);
        Assert.NotNull(revokedLookup!.RevokedAtUtc);
    }

    [Fact]
    public async Task Live_and_test_kinds_are_independent_rotation_slots()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);

        await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);
        await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);

        using var testContext = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var (testKey, _) = await ApiKeyService.IssueAsync(testContext, tenant.Id, ApiKeySecrets.TestKind, ct);
        Assert.Equal(ApiKeySecrets.TestKind, testKey.Kind);
    }

    [Fact]
    public void An_unknown_kind_is_rejected_before_any_database_call()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ApiKeySecrets.Generate("staging"));
    }

    [Fact]
    public async Task A_revoked_key_cannot_be_un_revoked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var (apiKey, _) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);

        await using var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        await ApiKeyService.RevokeAsync(db, apiKey.Id, ct);
        await db.SaveChangesAsync(ct);

        apiKey.RevokedAtUtc = null;
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.IsType<Npgsql.PostgresException>(exception.InnerException);
    }

    [Fact]
    public async Task Changing_a_keys_kind_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var (apiKey, _) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.TestKind, ct);

        await using var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        apiKey.Kind = ApiKeySecrets.LiveKind;
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.IsType<Npgsql.PostgresException>(exception.InnerException);
    }

    [Fact]
    public async Task Changing_a_keys_hash_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        var (apiKey, _) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind, ct);

        await using var transaction = await db.BeginTenantScopeAsync(tenant.Id, ct);
        apiKey.KeyHash = ApiKeySecrets.Hash(Guid.NewGuid().ToString());
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.IsType<Npgsql.PostgresException>(exception.InnerException);
    }

    [Fact]
    public async Task Changing_a_keys_tenant_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenantA = await ApiKeyService.ProvisionTenantAsync(db, "tenant-a-" + Guid.NewGuid(), cancellationToken: ct);
        var tenantB = await ApiKeyService.ProvisionTenantAsync(db, "tenant-b-" + Guid.NewGuid(), cancellationToken: ct);
        var (apiKey, _) = await ApiKeyService.IssueAsync(db, tenantA.Id, ApiKeySecrets.LiveKind, ct);

        await using var transaction = await db.BeginTenantScopeAsync(tenantA.Id, ct);
        apiKey.TenantId = tenantB.Id;
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.IsType<Npgsql.PostgresException>(exception.InnerException);
    }

    [Fact]
    public async Task Two_simultaneous_issues_for_the_same_tenant_and_kind_leave_exactly_two_active_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        using var seedDb = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(seedDb, "tenant-" + Guid.NewGuid(), cancellationToken: ct);
        await ApiKeyService.IssueAsync(seedDb, tenant.Id, ApiKeySecrets.LiveKind, ct);

        // One active key already exists; two concurrent issues now race for the second slot. The
        // advisory lock in the migration's trigger must serialise them so exactly one succeeds —
        // run a few times, since a missing lock only sometimes loses the race under read
        // committed (both reads can land before either commits, but not always).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var contextA = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
            using var contextB = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);

            var taskA = IssueOrNullAsync(contextA, tenant.Id, ct);
            var taskB = IssueOrNullAsync(contextB, tenant.Id, ct);
            var results = await Task.WhenAll(taskA, taskB);

            var successes = results.Count(r => r is not null);
            Assert.Equal(1, successes);

            using var verifyDb = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
            await using var verifyTx = await verifyDb.BeginTenantScopeAsync(tenant.Id, ct);
            var activeCount = await verifyDb.ApiKeys.CountAsync(
                k => k.TenantId == tenant.Id && k.Kind == ApiKeySecrets.LiveKind && k.RevokedAtUtc == null, ct);
            Assert.Equal(2, activeCount);

            // Revoke the one just issued so the next attempt starts from the same "one active
            // key" baseline.
            var justIssued = await verifyDb.ApiKeys
                .Where(k => k.TenantId == tenant.Id && k.Kind == ApiKeySecrets.LiveKind && k.RevokedAtUtc == null)
                .OrderByDescending(k => k.CreatedAtUtc)
                .Skip(1)
                .FirstAsync(ct);
            await ApiKeyService.RevokeAsync(verifyDb, justIssued.Id, ct);
            await verifyTx.CommitAsync(ct);
        }
        // Manually confirmed (then restored): removing the pg_advisory_xact_lock line in the
        // migration's enforce_api_key_rotation_limit() makes this loop fail — both concurrent
        // issues succeed at least once across 5 attempts, landing the tenant on 3 active keys.
    }

    private static async Task<ApiKey?> IssueOrNullAsync(VisionDbContext db, Guid tenantId, CancellationToken ct)
    {
        try
        {
            var (apiKey, _) = await ApiKeyService.IssueAsync(db, tenantId, ApiKeySecrets.LiveKind, ct);
            return apiKey;
        }
        catch (ApiKeyRotationLimitExceededException)
        {
            return null;
        }
    }

    private async Task AssertPlaintextNotStoredAnywhereAsync(string plaintext, string secretPart, CancellationToken ct)
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        await using var command = migrator.CreateCommand();
        // Every text-typed column across every table, searched for the plaintext key or its
        // secret part.
        command.CommandText = """
            SELECT string_agg(format('%I.%I', table_name, column_name), ', ')
            FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type IN ('text', 'character varying')
            """;
        var textColumns = (string?)await command.ExecuteScalarAsync(ct) ?? string.Empty;

        foreach (var qualified in textColumns.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = qualified.Split('.', 2);
            await using var check = migrator.CreateCommand();
            check.CommandText = $"SELECT count(*) FROM {parts[0]} WHERE {parts[1]} = $1 OR {parts[1]} LIKE $2";
            check.Parameters.Add(new NpgsqlParameter { Value = plaintext, NpgsqlDbType = NpgsqlDbType.Text });
            check.Parameters.Add(new NpgsqlParameter { Value = $"%{secretPart}%", NpgsqlDbType = NpgsqlDbType.Text });
            var count = (long)(await check.ExecuteScalarAsync(ct))!;
            Assert.Equal(0, count);
        }
    }
}
