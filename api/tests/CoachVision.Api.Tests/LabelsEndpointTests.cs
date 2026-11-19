using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CoachVision.Data;
using CoachVision.Data.Entities;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using CoachVision.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Api.Tests;

/// <summary>
/// <c>POST /v1/jobs/{jobId}/labels</c> through the real HTTP pipeline. Jobs aren't created
/// through the API yet, so each test seeds a job directly in the database (as
/// <c>ApiKeyServiceTests</c> seeds tenants) rather than through an endpoint.
/// </summary>
[Collection(VisionDbCollection.Name)]
public sealed class LabelsEndpointTests(VisionPostgresFixture fixture) : IAsyncLifetime
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
    public async Task A_correct_label_on_a_completed_job_is_stored_and_returned()
    {
        var (_, key, jobId) = await ProvisionTenantWithCompletedJobAsync();
        using var client = AuthenticatedClient(key);

        using var response = await client.PostAsJsonAsync($"/v1/jobs/{jobId}/labels", new { assessment = "correct" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JobLabelBody>(TestContext.Current.CancellationToken);
        Assert.Equal(jobId, body!.JobId);
        Assert.Equal("correct", body.Assessment);
        Assert.Null(body.Reason);
    }

    [Fact]
    public async Task An_incorrect_label_with_a_reason_is_stored()
    {
        var (_, key, jobId) = await ProvisionTenantWithCompletedJobAsync();
        using var client = AuthenticatedClient(key);

        using var response = await client.PostAsJsonAsync(
            $"/v1/jobs/{jobId}/labels",
            new { assessment = "incorrect", reason = "wrong_rep_count" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JobLabelBody>(TestContext.Current.CancellationToken);
        Assert.Equal("incorrect", body!.Assessment);
        Assert.Equal("wrong_rep_count", body.Reason);
    }

    [Fact]
    public async Task A_second_POST_replaces_the_first_label_rather_than_adding_a_second_one()
    {
        var (tenantId, key, jobId) = await ProvisionTenantWithCompletedJobAsync();
        using var client = AuthenticatedClient(key);

        using var first = await client.PostAsJsonAsync($"/v1/jobs/{jobId}/labels", new { assessment = "incorrect", reason = "wrong_lift" }, TestContext.Current.CancellationToken);
        first.EnsureSuccessStatusCode();

        using var second = await client.PostAsJsonAsync($"/v1/jobs/{jobId}/labels", new { assessment = "correct" }, TestContext.Current.CancellationToken);
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<JobLabelBody>(TestContext.Current.CancellationToken);
        Assert.Equal("correct", body!.Assessment);
        Assert.Null(body.Reason);

        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        await using var transaction = await db.BeginTenantScopeAsync(tenantId, TestContext.Current.CancellationToken);
        var count = await db.Labels.CountAsync(l => l.JobId == jobId, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Several_simultaneous_first_POSTs_for_the_same_job_all_succeed_and_leave_one_row()
    {
        var (tenantId, key, jobId) = await ProvisionTenantWithCompletedJobAsync();

        async Task<HttpResponseMessage> PostAsync()
        {
            using var client = AuthenticatedClient(key);
            return await client.PostAsJsonAsync($"/v1/jobs/{jobId}/labels", new { assessment = "correct" }, TestContext.Current.CancellationToken);
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostAsync()));

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        await using var transaction = await db.BeginTenantScopeAsync(tenantId, TestContext.Current.CancellationToken);
        var count = await db.Labels.CountAsync(l => l.JobId == jobId, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("""{"assessment":"maybe"}""")] // unknown assessment
    [InlineData("""{"assessment":"correct","reason":"wrong_rep_count"}""")] // reason with correct
    [InlineData("""{"assessment":"incorrect","reason":"not_a_real_reason"}""")] // unknown reason
    [InlineData("""{"assessment":"correct","extra":"nope"}""")] // unknown property
    [InlineData("""not json""")] // malformed JSON
    [InlineData("""{}""")] // missing assessment
    public async Task Malformed_bodies_are_400(string json)
    {
        var (_, key, jobId) = await ProvisionTenantWithCompletedJobAsync();
        using var client = AuthenticatedClient(key);

        using var response = await client.PostAsync(
            $"/v1/jobs/{jobId}/labels",
            new StringContent(json, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid_request", problem!.Code);
    }

    [Fact]
    public async Task No_body_is_400()
    {
        var (_, key, jobId) = await ProvisionTenantWithCompletedJobAsync();
        using var client = AuthenticatedClient(key);

        using var response = await client.PostAsync($"/v1/jobs/{jobId}/labels", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task No_key_is_401()
    {
        var (_, _, jobId) = await ProvisionTenantWithCompletedJobAsync();
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync($"/v1/jobs/{jobId}/labels", new { assessment = "correct" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_job_that_never_existed_is_404()
    {
        var (_, key, _) = await ProvisionTenantWithCompletedJobAsync();
        using var client = AuthenticatedClient(key);

        using var response = await client.PostAsJsonAsync("/v1/jobs/job_does_not_exist/labels", new { assessment = "correct" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_job_id_is_404_not_a_server_error()
    {
        var (_, key, _) = await ProvisionTenantWithCompletedJobAsync();
        using var client = AuthenticatedClient(key);

        using var response = await client.PostAsJsonAsync("/v1/jobs/not-a-valid-job-id/labels", new { assessment = "correct" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_job_is_404_not_distinguishable_from_not_found()
    {
        var (_, keyA, _) = await ProvisionTenantWithCompletedJobAsync();
        var (_, _, jobOfB) = await ProvisionTenantWithCompletedJobAsync();
        using var client = AuthenticatedClient(keyA);

        using var response = await client.PostAsJsonAsync($"/v1/jobs/{jobOfB}/labels", new { assessment = "correct" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_job_that_isnt_completed_is_409()
    {
        var (_, key, jobId) = await ProvisionTenantWithJobAsync(status: "processing");
        using var client = AuthenticatedClient(key);

        using var response = await client.PostAsJsonAsync($"/v1/jobs/{jobId}/labels", new { assessment = "correct" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken);
        Assert.Equal("job_not_completed", problem!.Code);
    }

    private HttpClient AuthenticatedClient(string plaintextKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintextKey);
        return client;
    }

    private Task<(Guid TenantId, string Key, string JobId)> ProvisionTenantWithCompletedJobAsync() =>
        ProvisionTenantWithJobAsync(status: "completed");

    private async Task<(Guid TenantId, string Key, string JobId)> ProvisionTenantWithJobAsync(string status)
    {
        using var db = VisionPostgresFixture.CreateContext(fixture.RuntimeConnectionString);
        var tenant = await ApiKeyService.ProvisionTenantAsync(db, "tenant-" + Guid.NewGuid());
        var (_, plaintext) = await ApiKeyService.IssueAsync(db, tenant.Id, ApiKeySecrets.LiveKind);

        var jobId = $"job_{Guid.NewGuid():N}0000000"[..30];
        await using var transaction = await db.BeginTenantScopeAsync(tenant.Id);
        db.Jobs.Add(new Job
        {
            Id = jobId,
            TenantId = tenant.Id,
            Status = status,
            SchemaVersion = "1.0",
            Lift = "squat",
            Features = ["bar_path"],
            SubjectId = "sub_1",
            TrainingAllowed = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = status == "completed" ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenant.Id, plaintext, jobId);
    }

    private sealed record JobLabelBody(string JobId, string Assessment, string? Reason, DateTime UpdatedAt);

    private sealed record ProblemBody(string Type, string Title, int Status, string Code, string? Detail);

    private sealed class CoachVisionWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting($"ConnectionStrings:{VisionDbOptions.ConnectionStringName}", connectionString);
            builder.UseSetting("Vision:AcceptedKeyKind", "live");
        }
    }
}
