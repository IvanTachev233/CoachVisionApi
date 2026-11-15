using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoachVision.Data;
using CoachVision.Data.Security;
using CoachVision.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace CoachVision.Api.Tests;

/// <summary>
/// Authentication running on every request, not only <c>/v1</c> ones, and what happens when it
/// can't even reach a database — plus the exact shape of its own error bodies.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class AuthenticationEdgeCaseTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task Outside_v1_a_key_shaped_header_touches_no_database_and_the_path_is_still_404()
    {
        using var factory = new CoachVisionWebApplicationFactory(fixture.RuntimeConnectionString, "live");
        using var client = factory.CreateClient();
        var (keyShaped, _) = ApiKeySecrets.Generate(ApiKeySecrets.LiveKind);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyShaped);

        using var response = await client.GetAsync("/nothing-here", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Outside_v1_a_key_shaped_header_is_404_even_with_no_database_configured()
    {
        using var factory = new NoDatabaseWebApplicationFactory();
        using var client = factory.CreateClient();
        var (keyShaped, _) = ApiKeySecrets.Generate(ApiKeySecrets.LiveKind);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyShaped);

        using var response = await client.GetAsync("/nothing-here", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_answers_with_no_database_configured_even_with_a_key_shaped_header()
    {
        using var factory = new NoDatabaseWebApplicationFactory();
        using var client = factory.CreateClient();
        var (keyShaped, _) = ApiKeySecrets.Generate(ApiKeySecrets.LiveKind);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyShaped);

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_v1_request_with_no_database_configured_is_503_not_an_unhandled_exception()
    {
        using var factory = new NoDatabaseWebApplicationFactory();
        using var client = factory.CreateClient();
        var (anyKey, _) = ApiKeySecrets.Generate(ApiKeySecrets.LiveKind);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", anyKey);

        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken);
        Assert.Equal("service_unavailable", problem!.Code);
    }

    [Fact]
    public async Task An_invalid_accepted_key_kind_fails_at_startup()
    {
        using var factory = new CoachVisionWebApplicationFactory(fixture.RuntimeConnectionString, "staging");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/health", TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task A_tenant_with_no_limits_row_still_authenticates_under_the_defaults()
    {
        await using var migrator = await fixture.OpenMigratorConnectionAsync();
        var tenantId = Guid.NewGuid();
        await using (var insertTenant = migrator.CreateCommand())
        {
            insertTenant.CommandText = "INSERT INTO tenants (id, name, created_at) VALUES ($1, $2, now())";
            insertTenant.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
            insertTenant.Parameters.Add(new NpgsqlParameter { Value = "tenant-" + tenantId, NpgsqlDbType = NpgsqlDbType.Text });
            await insertTenant.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (plaintext, hash) = ApiKeySecrets.Generate(ApiKeySecrets.LiveKind);
        await using (var insertKey = migrator.CreateCommand())
        {
            insertKey.CommandText = "INSERT INTO api_keys (tenant_id, key_hash, kind, created_at) VALUES ($1, $2, 'live', now())";
            insertKey.Parameters.Add(new NpgsqlParameter { Value = tenantId, NpgsqlDbType = NpgsqlDbType.Uuid });
            insertKey.Parameters.Add(new NpgsqlParameter { Value = hash, NpgsqlDbType = NpgsqlDbType.Bytea });
            await insertKey.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        // Deliberately no tenant_limits row for this tenant.

        using var factory = new CoachVisionWebApplicationFactory(fixture.RuntimeConnectionString, "live");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);

        using var pingResponse = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, pingResponse.StatusCode);

        using var admissionResponse = await client.PostAsJsonAsync("/v1/_test/admission-check", new { sizeBytes = 1_000 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, admissionResponse.StatusCode);
    }

    [Fact]
    public async Task The_401_body_has_exactly_the_contracts_members_and_no_null_detail()
    {
        using var factory = new CoachVisionWebApplicationFactory(fixture.RuntimeConnectionString, "live");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var memberNames = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "code", "status", "title", "type" }, memberNames);
        Assert.Equal("unauthorized", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://vision-api.invalid/problems/unauthorized", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(401, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task The_429_body_has_the_contracts_members_including_detail_and_an_integer_retry_after()
    {
        var (tenant, key) = await ProvisionTenantWithKeyAsync();
        await SetRateLimitAsync(tenant, 1);
        using var factory = new CoachVisionWebApplicationFactory(fixture.RuntimeConnectionString, "live");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var first = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(int.TryParse(response.Headers.GetValues("Retry-After").Single(), out var retryAfter));
        Assert.True(retryAfter >= 1);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var memberNames = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "code", "status", "title", "type" }, memberNames);
        Assert.Equal("rate_limited", document.RootElement.GetProperty("code").GetString());
    }

    private async Task<(Guid TenantId, string Plaintext)> ProvisionTenantWithKeyAsync()
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid());
        var (_, plaintext) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind);
        return (tenant.Id, plaintext);
    }

    private async Task SetRateLimitAsync(Guid tenantId, int permitLimit)
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
        var limits = await db.TenantLimits.FindAsync(tenantId);
        limits!.RateLimitPerMinute = permitLimit;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private sealed record ProblemBody(string Type, string Title, int Status, string Code, string? Detail);

    private sealed class CoachVisionWebApplicationFactory(string connectionString, string acceptedKeyKind) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting($"ConnectionStrings:{VisionDbOptions.ConnectionStringName}", connectionString);
            builder.UseSetting("Vision:AcceptedKeyKind", acceptedKeyKind);
            builder.ConfigureTestServices(services =>
                services.AddSingleton<Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>>(TestOnlyEndpoints.MapTestOnlyEndpoints));
        }
    }

    private sealed class NoDatabaseWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Vision:AcceptedKeyKind", "live");
            builder.ConfigureTestServices(services =>
                services.AddSingleton<Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>>(TestOnlyEndpoints.MapTestOnlyEndpoints));
        }
    }
}
