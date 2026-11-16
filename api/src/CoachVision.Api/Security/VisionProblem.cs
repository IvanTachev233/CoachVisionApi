using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoachVision.Api.Security;

/// <summary>
/// RFC 9457 problem bodies in the exact shape <c>contracts/openapi/vision-v1.yaml</c>'s
/// <c>Problem</c> schema and its named responses (<c>Unauthorized</c>, <c>VideoTooLarge</c>,
/// <c>TooManyRequests</c>) describe, written directly rather than through
/// <c>IProblemDetailsService</c> so every field (including the contract's own <c>code</c>
/// extension) is under this type's control. <c>detail</c> is a string the contract says is
/// absent when there's nothing to say, not null, so it's omitted rather than serialised as null.
/// </summary>
public static class VisionProblem
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(HttpContext context, int status, string type, string title, string code, string? detail = null, CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var body = new
        {
            type = $"https://vision-api.invalid/problems/{type}",
            title,
            status,
            code,
            detail,
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, SerializerOptions), cancellationToken);
    }

    /// <summary>
    /// The one body every authentication failure gets: missing, malformed, unknown, revoked, or
    /// the wrong kind for this environment's key — the contract says never to distinguish which.
    /// </summary>
    public static Task WriteUnauthorizedAsync(HttpContext context, CancellationToken cancellationToken = default) =>
        WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", "unauthorized", cancellationToken: cancellationToken);

    public static Task WriteRateLimitedAsync(HttpContext context, int retryAfterSeconds, string? detail = null, CancellationToken cancellationToken = default)
    {
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        return WriteAsync(context, StatusCodes.Status429TooManyRequests, "rate-limited", "Too many requests", "rate_limited", detail, cancellationToken);
    }

    public static Task WriteVideoTooLargeAsync(HttpContext context, long limitBytes, CancellationToken cancellationToken = default) =>
        WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "video-too-large", "Video too large", "video_too_large", $"The limit is {limitBytes} bytes.", cancellationToken);

    /// <summary>
    /// The service can't authenticate anyone right now (for example, no database is configured):
    /// a different failure from a bad key, so it isn't reported as 401 — a tenant shouldn't read
    /// "your key is wrong" when the real problem is the service's own.
    /// </summary>
    public static Task WriteServiceUnavailableAsync(HttpContext context, CancellationToken cancellationToken = default) =>
        WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "service-unavailable", "Service unavailable", "service_unavailable", cancellationToken: cancellationToken);
}
