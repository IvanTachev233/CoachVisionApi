using CoachVision.Api.Security;
using CoachVision.Data;
using CoachVision.Data.Entities;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Api.Tests;

/// <summary>
/// Test-only seams under <c>/v1</c> — not part of the production assembly at all. Program.cs
/// exposes one generic, optional hook (an <c>Action&lt;IEndpointRouteBuilder&gt;</c> singleton,
/// resolved and invoked on the <c>/v1</c> group if, and only if, something registered one); a
/// production build never registers it, so the hook is a no-op there and these routes don't
/// exist. Each test's <c>WebApplicationFactory</c> registers <see cref="MapTestOnlyEndpoints"/>
/// as that singleton (<c>ConfigureTestServices</c>), which is how these endpoints come to exist
/// only inside the test host. They prove authentication, rate limiting and job admission
/// through the real HTTP pipeline without needing a real job-creation endpoint, and the contract
/// gets no admin or diagnostic endpoint it doesn't already have.
/// </summary>
public static class TestOnlyEndpoints
{
    public static void MapTestOnlyEndpoints(IEndpointRouteBuilder group)
    {
        // Proves: a valid key authenticates, and the request runs in that tenant's own scope (it
        // can only ever see its own job count, never another tenant's).
        group.MapGet("/_test/ping", async (HttpContext context, [FromServices] VisionDbContext db, CancellationToken cancellationToken) =>
        {
            var tenantId = GetTenantId(context);
            await using var transaction = await db.BeginTenantScopeAsync(tenantId, cancellationToken);
            var jobCount = await db.Jobs.CountAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new { tenantId, jobCount });
        });

        // Proves: each of the three database-checked admission limits (clip size, concurrent
        // jobs, daily minutes) returns the contract's exact error at limit + 1 and admits at the
        // limit, through the pipeline's own authentication and tenant scoping.
        group.MapPost("/_test/admission-check", async (HttpContext context, [FromServices] VisionDbContext db, AdmissionCheckRequest request, CancellationToken cancellationToken) =>
        {
            var tenantId = GetTenantId(context);
            await using var transaction = await db.BeginTenantScopeAsync(tenantId, cancellationToken);
            var result = await JobAdmission.CheckAsync(db, tenantId, request.SizeBytes, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            switch (result.Outcome)
            {
                case JobAdmissionOutcome.Admitted:
                    return Results.Ok(new { outcome = "admitted" });
                case JobAdmissionOutcome.ClipTooLarge:
                    await VisionProblem.WriteVideoTooLargeAsync(context, result.LimitBytes!.Value, cancellationToken);
                    return Results.Empty;
                case JobAdmissionOutcome.ConcurrentJobsLimitReached:
                    await VisionProblem.WriteRateLimitedAsync(context, result.RetryAfterSeconds!.Value, "The limit is the tenant's concurrent jobs.", cancellationToken);
                    return Results.Empty;
                case JobAdmissionOutcome.DailyMinutesLimitReached:
                    await VisionProblem.WriteRateLimitedAsync(context, result.RetryAfterSeconds!.Value, "The limit is the tenant's daily minutes.", cancellationToken);
                    return Results.Empty;
                default:
                    return Results.Problem(statusCode: 500);
            }
        });

        // Lets a test seed an open job directly, under the authenticated tenant's own scope, to
        // exercise the concurrent-jobs limit without needing a real job-creation endpoint.
        group.MapPost("/_test/seed-open-job", async (HttpContext context, [FromServices] VisionDbContext db, CancellationToken cancellationToken) =>
        {
            var tenantId = GetTenantId(context);
            await using var transaction = await db.BeginTenantScopeAsync(tenantId, cancellationToken);
            db.Jobs.Add(new Job
            {
                Id = $"job_{Guid.NewGuid():N}0000000"[..30],
                TenantId = tenantId,
                Status = "awaiting_upload",
                SchemaVersion = "1.0",
                Lift = "squat",
                Features = ["bar_path"],
                SubjectId = "sub_1",
                TrainingAllowed = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        });

        // Lets a test seed today's usage directly, to exercise the daily-minutes limit.
        group.MapPost("/_test/seed-usage", async (HttpContext context, [FromServices] VisionDbContext db, SeedUsageRequest request, CancellationToken cancellationToken) =>
        {
            var tenantId = GetTenantId(context);
            await using var transaction = await db.BeginTenantScopeAsync(tenantId, cancellationToken);
            db.Usage.Add(new UsageRecord
            {
                TenantId = tenantId,
                Feature = "bar_path",
                VideoSeconds = request.VideoSeconds,
                OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        });
    }

    private static Guid GetTenantId(HttpContext context) =>
        Guid.Parse(context.User.FindFirst(VisionClaimTypes.TenantId)!.Value);

    public sealed record AdmissionCheckRequest(long SizeBytes);

    public sealed record SeedUsageRequest(decimal VideoSeconds);
}
