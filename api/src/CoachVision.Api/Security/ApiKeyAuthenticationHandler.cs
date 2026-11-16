using System.Security.Claims;
using System.Text.Encodings.Web;
using CoachVision.Data;
using CoachVision.Data.Entities;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CoachVision.Api.Security;

/// <summary>Claim type holding the authenticated tenant's id, set only on success.</summary>
public static class VisionClaimTypes
{
    public const string TenantId = "tenant_id";
}

/// <summary>
/// Validates <c>Authorization: Bearer &lt;key&gt;</c> against <c>lookup_api_key_by_hash</c>, for
/// <c>/v1</c> requests only — every other path gets <see cref="AuthenticateResult.NoResult"/>
/// without touching the database at all, since this is the default scheme and the middleware
/// authenticates it on every request regardless of which endpoint matches. Every <c>/v1</c>
/// failure that *is* about the key — missing header, wrong scheme, malformed key, unknown hash, a
/// revoked key, or a key of the wrong kind for this environment — ends the same way: a generic
/// <see cref="AuthenticateResult.Fail(string)"/> with a short, fixed reason string that never
/// includes the presented key, and the same 401 Problem body from
/// <see cref="HandleChallengeAsync"/> regardless of which reason it was — the contract requires
/// that a client can't tell them apart. A missing database is a different kind of failure (no
/// key, valid or not, can be checked at all) and gets 503, not 401, so a tenant doesn't read "your
/// key is bad" when the real problem is the service's own.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    VisionDbContext db,
    VisionEnvironmentOptions environmentOptions,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "VisionApiKey";

    private const string DatabaseUnavailableItemsKey = "DatabaseUnavailable";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments("/v1"))
        {
            return AuthenticateResult.NoResult();
        }

        // Checked directly against configuration, not by touching db: VisionDbContext is
        // registered with no provider configured at all when there is no connection string
        // (Program.cs), and any operation on it would throw rather than report "not configured".
        if (string.IsNullOrEmpty(configuration.GetConnectionString(VisionDbOptions.ConnectionStringName)))
        {
            Logger.LogError("Vision API key authentication failed: no database is configured.");
            Context.Items[DatabaseUnavailableItemsKey] = true;
            return AuthenticateResult.Fail("database-unavailable");
        }

        if (!Request.Headers.TryGetValue("Authorization", out var values) || values.Count == 0)
        {
            return AuthenticateResult.Fail("missing");
        }

        var header = values[0];
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("wrong-scheme");
        }

        var presented = header["Bearer ".Length..].Trim();
        if (presented.Length == 0)
        {
            return AuthenticateResult.Fail("empty");
        }

        var prefixKind = ApiKeySecrets.KindFromPrefix(presented);
        if (prefixKind is null)
        {
            return AuthenticateResult.Fail("malformed");
        }

        var hash = ApiKeySecrets.Hash(presented);
        var lookup = await ApiKeyLookup.FindByHashAsync(db, hash, Context.RequestAborted);
        if (lookup is null)
        {
            return AuthenticateResult.Fail("unknown");
        }

        if (lookup.RevokedAtUtc is not null)
        {
            return AuthenticateResult.Fail("revoked");
        }

        if (lookup.Kind != environmentOptions.AcceptedKeyKind)
        {
            return AuthenticateResult.Fail("wrong-kind");
        }

        Context.Items[RateLimiting.TenantIdItemsKey] = lookup.TenantId;

        // Read-only, but still needs the tenant scope open (row security): the rate limiter's
        // partition factory runs synchronously and can't query the database itself, so the
        // tenant's own rate limit is read once here and stashed for it.
        await using (var transaction = await db.BeginTenantScopeAsync(lookup.TenantId, Context.RequestAborted))
        {
            var limits = await TenantLimitsLookup.GetOrDefaultAsync(db, lookup.TenantId, Context.RequestAborted);
            Context.Items[RateLimiting.RateLimitPerMinuteItemsKey] = limits.RateLimitPerMinute;
            await transaction.CommitAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [new Claim(VisionClaimTypes.TenantId, lookup.TenantId.ToString())],
            SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Context.Items.ContainsKey(DatabaseUnavailableItemsKey)
            ? VisionProblem.WriteServiceUnavailableAsync(Context, Context.RequestAborted)
            : VisionProblem.WriteUnauthorizedAsync(Context, Context.RequestAborted);
}

/// <summary>
/// Which key kind this deployment accepts: <c>live</c> in production, <c>test</c> in staging
/// (<c>Vision:AcceptedKeyKind</c> in configuration, defaulting to <c>live</c> when unset).
/// "Keys are issued per tenant and environment" (the OpenAPI security scheme's own words) is read
/// here as one fixed kind per deployment, not a per-request choice — assumption, Ivan to confirm.
/// Validated at startup (<c>Program.cs</c>): a value other than <c>live</c>/<c>test</c> fails
/// immediately, with a clear message, rather than silently matching no key forever.
/// </summary>
public sealed class VisionEnvironmentOptions
{
    public required string AcceptedKeyKind { get; init; }
}
