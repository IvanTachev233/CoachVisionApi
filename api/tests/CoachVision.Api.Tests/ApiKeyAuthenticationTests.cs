using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoachVision.Data;
using CoachVision.Data.Entities;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using CoachVision.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoachVision.Api.Tests;

/// <summary>
/// Authentication through the real HTTP pipeline: tenant scoping and rate limiting, against
/// a real Postgres database (<see cref="VisionPostgresFixture"/>), not a mocked one.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class ApiKeyAuthenticationTests(VisionPostgresFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = CreateFactory("live");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task No_authorization_header_is_401_with_the_contract_body()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Wrong_scheme_is_401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");
        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Malformed_key_is_401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-vision-key");
        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Unknown_key_is_401()
    {
        using var client = _factory.CreateClient();
        var (unknownPlaintext, _) = ApiKeySecrets.Generate(ApiKeySecrets.LiveKind);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unknownPlaintext);
        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Revoked_key_is_401()
    {
        var (tenant, plaintext) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.LiveKind);
        await RevokeAsync(tenant.Id, plaintext);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);
        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task A_test_kind_key_is_401_against_a_deployment_that_accepts_only_live()
    {
        var (_, testPlaintext) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.TestKind);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", testPlaintext);
        using var response = await client.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task A_valid_key_authenticates_and_the_request_runs_in_its_own_tenants_scope()
    {
        var (tenantA, keyA) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.LiveKind);
        var (_, keyB) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.LiveKind);

        await SeedOpenJobAsync(keyA);

        using var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyA);
        using var responseA = await clientA.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        responseA.EnsureSuccessStatusCode();
        var bodyA = await responseA.Content.ReadFromJsonAsync<PingResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(tenantA.Id, bodyA!.TenantId);
        Assert.Equal(1, bodyA.JobCount);

        using var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyB);
        using var responseB = await clientB.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        responseB.EnsureSuccessStatusCode();
        var bodyB = await responseB.Content.ReadFromJsonAsync<PingResponse>(TestContext.Current.CancellationToken);
        // Tenant B's key never sees tenant A's job: row security, not an application filter.
        Assert.Equal(0, bodyB!.JobCount);
    }

    [Fact]
    public async Task Both_keys_of_a_tenant_work_during_rotation()
    {
        var (tenant, firstKey) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.LiveKind);
        var secondKey = await IssueSecondKeyAsync(tenant.Id);

        using var clientFirst = _factory.CreateClient();
        clientFirst.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstKey);
        using var responseFirst = await clientFirst.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        responseFirst.EnsureSuccessStatusCode();

        using var clientSecond = _factory.CreateClient();
        clientSecond.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondKey);
        using var responseSecond = await clientSecond.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        responseSecond.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_presented_key_never_appears_in_logs()
    {
        var logSink = new CapturingLoggerProvider();
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logSink)));

        var (_, plaintext) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.LiveKind);
        using var validClient = factory.CreateClient();
        validClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);
        await validClient.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        var (unknownPlaintext, _) = ApiKeySecrets.Generate(ApiKeySecrets.LiveKind);
        using var invalidClient = factory.CreateClient();
        invalidClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unknownPlaintext);
        await invalidClient.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logSink.Messages, message => message.Contains(plaintext, StringComparison.Ordinal));
        Assert.DoesNotContain(logSink.Messages, message => message.Contains(unknownPlaintext, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_number_limit_passes_and_limit_plus_one_is_429_with_retry_after_and_does_not_affect_another_tenant()
    {
        var (tenantA, keyA) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.LiveKind);
        var limit = await SetRateLimitAsync(tenantA.Id, 2);
        var (_, keyB) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.LiveKind);

        using var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyA);
        for (var i = 0; i < limit; i++)
        {
            using var atLimit = await clientA.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        }

        using var overLimit = await clientA.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
        Assert.True(overLimit.Headers.RetryAfter is not null || overLimit.Headers.Contains("Retry-After"));

        using var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", keyB);
        using var tenantBResponse = await clientB.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, tenantBResponse.StatusCode);
    }

    [Fact]
    public async Task Failed_authentication_is_itself_rate_limited_by_ip_without_affecting_a_valid_tenant()
    {
        using var badClient = _factory.CreateClient();
        var (unknownPlaintext, _) = ApiKeySecrets.Generate(ApiKeySecrets.LiveKind);
        badClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unknownPlaintext);

        for (var i = 1; i <= 20; i++)
        {
            using var response = await badClient.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var last = await badClient.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.True(last.Headers.RetryAfter is not null || last.Headers.Contains("Retry-After"));

        var (_, validKey) = await ProvisionTenantWithKeyAsync(ApiKeySecrets.LiveKind);
        using var goodClient = _factory.CreateClient();
        goodClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validKey);
        using var goodResponse = await goodClient.GetAsync("/v1/_test/ping", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, goodResponse.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(string acceptedKeyKind) =>
        new CoachVisionWebApplicationFactory(fixture.RuntimeConnectionString, acceptedKeyKind);

    private async Task<(Tenant Tenant, string Plaintext)> ProvisionTenantWithKeyAsync(string kind)
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid());
        var (_, plaintext) = await ApiKeyService.IssueAsync(db, tenant.Id, kind);
        return (tenant, plaintext);
    }

    private async Task<string> IssueSecondKeyAsync(Guid tenantId)
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var (_, plaintext) = await ApiKeyService.IssueAsync(db, tenantId, ApiKeySecrets.LiveKind);
        return plaintext;
    }

    private async Task RevokeAsync(Guid tenantId, string plaintext)
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var hash = ApiKeySecrets.Hash(plaintext);
        var lookup = await ApiKeyLookup.FindByHashAsync(db, hash);
        await using var transaction = await db.BeginTenantScopeAsync(tenantId);
        await ApiKeyService.RevokeAsync(db, lookup!.ApiKeyId);
        await transaction.CommitAsync();
    }

    private async Task<int> SetRateLimitAsync(Guid tenantId, int permitLimit)
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        await using var transaction = await db.BeginTenantScopeAsync(tenantId);
        var limits = await db.TenantLimits.FindAsync(tenantId);
        limits!.RateLimitPerMinute = permitLimit;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return permitLimit;
    }

    private async Task SeedOpenJobAsync(string plaintextKey)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintextKey);
        using var response = await client.PostAsync("/v1/_test/seed-open-job", content: null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssertUnauthorizedProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken);
        Assert.Equal("unauthorized", problem!.Code);
        Assert.Equal("https://vision-api.invalid/problems/unauthorized", problem.Type);
    }

    private sealed record ProblemBody(string Type, string Title, int Status, string Code);

    private sealed record PingResponse(Guid TenantId, int JobCount);

    private sealed class CoachVisionWebApplicationFactory(string connectionString, string acceptedKeyKind) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // UseSetting (not ConfigureAppConfiguration) is what actually lands in time for
            // Program.cs's own builder.Configuration.GetConnectionString(...) call: with a
            // top-level-statements Program, WebApplicationFactory's ConfigureAppConfiguration
            // hook merges in after that line has already run and read an empty connection
            // string, so AddDbContext never registers VisionDbContext at all. UseSetting writes
            // into the WebHostBuilderContext's own configuration, which backs
            // WebApplication.CreateBuilder(args) from the start.
            builder.UseSetting($"ConnectionStrings:{VisionDbOptions.ConnectionStringName}", connectionString);
            builder.UseSetting("Vision:AcceptedKeyKind", acceptedKeyKind);
            builder.ConfigureTestServices(services =>
                services.AddSingleton<Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>>(TestOnlyEndpoints.MapTestOnlyEndpoints));
        }
    }

    private sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _messages = [];

        public IReadOnlyCollection<string> Messages => _messages;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentBag<string> messages) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                messages.Add(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Add(exception.ToString());
                }
            }
        }
    }
}
