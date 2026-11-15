using CoachVision.Api.Logging;
using CoachVision.Api.Security;
using CoachVision.Data;
using CoachVision.Data.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

// One JSON line per entry on standard output, which Cloud Run passes to Cloud Logging.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = CloudLoggingJsonFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<CloudLoggingJsonFormatter, ConsoleFormatterOptions>();

// No checks are registered: /health answers as soon as the app serves requests. Cloud Run's
// startup probe and the uptime check call it, so it must stay off the database once there is one.
builder.Services.AddHealthChecks();

// Staging has no database secret until its runbook step is done; /health (and the app's startup)
// must keep working with none. VisionDbContext is registered unconditionally, but its provider is
// read lazily, from the service provider's own IConfiguration, the first time a DbContext
// instance is actually requested — not read eagerly here, which would run before a test host's
// configuration is merged into the final host. With no connection string configured, UseNpgsql is
// simply never called; nothing resolves VisionDbContext unless a /v1 request needs it, which
// never happens without a database. Migrations are never run here (EF Core 9+ has Migrate() throw
// on pending model changes rather than silently apply them) — see the runbook for how they reach
// an environment.
builder.Services.AddDbContext<VisionDbContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString(VisionDbOptions.ConnectionStringName);
    if (!string.IsNullOrEmpty(connectionString))
    {
        options.UseNpgsql(connectionString);
    }
});

// Which key kind this deployment accepts (live in production, test in staging), defaulting to
// live when unset. Read lazily (see above) so a test host can set it through its own factory.
// Validated eagerly, the first time anything resolves it: a value other than live/test would
// otherwise match no key's kind and fail every request forever with no clue why.
builder.Services.AddSingleton(serviceProvider =>
{
    var acceptedKeyKind = serviceProvider.GetRequiredService<IConfiguration>()["Vision:AcceptedKeyKind"] ?? "live";
    if (acceptedKeyKind is not (ApiKeySecrets.LiveKind or ApiKeySecrets.TestKind))
    {
        throw new InvalidOperationException(
            $"""Vision:AcceptedKeyKind is "{acceptedKeyKind}"; it must be "{ApiKeySecrets.LiveKind}" or "{ApiKeySecrets.TestKind}".""");
    }

    return new VisionEnvironmentOptions { AcceptedKeyKind = acceptedKeyKind };
});

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = RateLimiting.CreatePartitionedLimiter();
    options.OnRejected = (context, cancellationToken) => RateLimiting.OnRejectedAsync(context, cancellationToken);
});

var app = builder.Build();

// Resolved eagerly, at startup, rather than waiting for the first request to hit the
// factory-based registration above: a bad Vision:AcceptedKeyKind should fail the deploy, not
// fail every request forever with no clue why.
_ = app.Services.GetRequiredService<VisionEnvironmentOptions>();

app.MapHealthChecks("/health").ShortCircuit();

// Authentication runs for every request (it's the default scheme), not only /v1 ones, and always
// before the limiter: the limiter bounds what reaches an endpoint's handler, not the key lookup
// itself, so a flood of bad keys still costs one hash and one indexed database lookup each,
// capped only by whatever actually calls this pipeline. Authorization runs last, only for
// requests the limiter let through.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

var v1 = app.MapGroup("/v1").RequireAuthorization();

// An optional, generic hook: a test host registers an Action<IEndpointRouteBuilder> singleton to
// map its own routes onto this group; nothing here depends on, or even names, what those routes
// are. A production build never registers one, so this is a no-op and the production surface
// gains no endpoint the contract doesn't have.
var additionalV1Endpoints = app.Services.GetService<Action<IEndpointRouteBuilder>>();
additionalV1Endpoints?.Invoke(v1);

app.Run();
