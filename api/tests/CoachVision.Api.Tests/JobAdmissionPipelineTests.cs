using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoachVision.Data;
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
/// Job-admission limits (clip size, concurrent jobs, daily minutes), each at the limit
/// (admitted) and limit + 1 (the contract's exact error), through the real HTTP pipeline.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class JobAdmissionPipelineTests(VisionPostgresFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new CoachVisionWebApplicationFactory(fixture.RuntimeConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_clip_at_the_size_limit_is_admitted_and_one_byte_over_is_413()
    {
        var (_, key, limitBytes) = await ProvisionTenantAsync();
        using var client = AuthenticatedClient(key);

        using var atLimit = await PostAdmissionCheckAsync(client, limitBytes);
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);

        using var overLimit = await PostAdmissionCheckAsync(client, limitBytes + 1);
        Assert.Equal((HttpStatusCode)413, overLimit.StatusCode);
        var problem = await overLimit.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken);
        Assert.Equal("video_too_large", problem!.Code);
        Assert.Equal($"The limit is {limitBytes} bytes.", problem.Detail);
    }

    [Fact]
    public async Task Concurrent_jobs_at_the_limit_are_admitted_and_one_more_is_429_with_retry_after()
    {
        var (tenantId, key, _) = await ProvisionTenantAsync();
        var concurrentLimit = await SetConcurrentJobsLimitAsync(tenantId, 2);
        using var client = AuthenticatedClient(key);

        // One below the limit open already: the next job (this check) would be the Nth, exactly
        // at the limit, and must be admitted.
        for (var i = 0; i < concurrentLimit - 1; i++)
        {
            using var seed = await client.PostAsync("/v1/_test/seed-open-job", content: null, TestContext.Current.CancellationToken);
            seed.EnsureSuccessStatusCode();
        }

        using var atLimit = await PostAdmissionCheckAsync(client, 1_000);
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);

        using var seedOneMore = await client.PostAsync("/v1/_test/seed-open-job", content: null, TestContext.Current.CancellationToken);
        seedOneMore.EnsureSuccessStatusCode();

        using var overLimit = await PostAdmissionCheckAsync(client, 1_000);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
        Assert.True(overLimit.Headers.RetryAfter is not null || overLimit.Headers.Contains("Retry-After"));
        var problem = await overLimit.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken);
        Assert.Equal("rate_limited", problem!.Code);
    }

    [Fact]
    public async Task Daily_minutes_just_under_the_limit_are_admitted_and_at_the_limit_is_429()
    {
        var (tenantId, key, _) = await ProvisionTenantAsync();
        var dailyMinutesLimit = await GetDailyMinutesLimitAsync(tenantId);
        using var client = AuthenticatedClient(key);

        using var seedJustUnder = await client.PostAsJsonAsync(
            "/v1/_test/seed-usage",
            new { videoSeconds = (dailyMinutesLimit * 60m) - 1m },
            TestContext.Current.CancellationToken);
        seedJustUnder.EnsureSuccessStatusCode();

        using var admitted = await PostAdmissionCheckAsync(client, 1_000);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);

        using var seedTheLastSecond = await client.PostAsJsonAsync(
            "/v1/_test/seed-usage",
            new { videoSeconds = 1m },
            TestContext.Current.CancellationToken);
        seedTheLastSecond.EnsureSuccessStatusCode();

        using var overLimit = await PostAdmissionCheckAsync(client, 1_000);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
        var problem = await overLimit.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken);
        Assert.Equal("rate_limited", problem!.Code);
    }

    private HttpClient AuthenticatedClient(string plaintextKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintextKey);
        return client;
    }

    private static Task<HttpResponseMessage> PostAdmissionCheckAsync(HttpClient client, long sizeBytes) =>
        client.PostAsJsonAsync("/v1/_test/admission-check", new { sizeBytes }, TestContext.Current.CancellationToken);

    private async Task<(Guid TenantId, string Key, long MaxClipSizeBytes)> ProvisionTenantAsync()
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid());
        var (_, plaintext) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind);
        await using var transaction = await db.BeginTenantScopeAsync(tenant.Id);
        var limits = await db.TenantLimits.FindAsync(tenant.Id);
        await transaction.CommitAsync();
        return (tenant.Id, plaintext, limits!.MaxClipSizeBytes);
    }

    private async Task<int> SetConcurrentJobsLimitAsync(Guid tenantId, int limit)
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        await using var transaction = await db.BeginTenantScopeAsync(tenantId);
        var limits = await db.TenantLimits.FindAsync(tenantId);
        limits!.ConcurrentJobsLimit = limit;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return limit;
    }

    private async Task<decimal> GetDailyMinutesLimitAsync(Guid tenantId)
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        await using var transaction = await db.BeginTenantScopeAsync(tenantId);
        var limits = await db.TenantLimits.FindAsync(tenantId);
        await transaction.CommitAsync();
        return limits!.DailyMinutesLimit;
    }

    private sealed record ProblemBody(string Type, string Title, int Status, string Code, string? Detail);

    private sealed class CoachVisionWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // See ApiKeyAuthenticationTests's CoachVisionWebApplicationFactory for why UseSetting
            // (not ConfigureAppConfiguration) is required here.
            builder.UseSetting($"ConnectionStrings:{VisionDbOptions.ConnectionStringName}", connectionString);
            builder.UseSetting("Vision:AcceptedKeyKind", "live");
            builder.ConfigureTestServices(services =>
                services.AddSingleton<Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder>>(TestOnlyEndpoints.MapTestOnlyEndpoints));
        }
    }
}
