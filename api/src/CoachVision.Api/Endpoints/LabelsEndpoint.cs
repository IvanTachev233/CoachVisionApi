using System.Text.Json;
using CoachVision.Api.Security;
using CoachVision.Data;
using CoachVision.Data.Labels;
using CoachVision.Data.Tenancy;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Api.Endpoints;

/// <summary>
/// `POST /v1/jobs/{jobId}/labels` (contract: `labelJob`): the first real endpoint on the `/v1`
/// group — jobs aren't created through the API yet, so tests seed a job directly in the
/// database, the same way the admission-pipeline tests do through the test-only endpoints.
///
/// The body is parsed by hand, not bound to a record, because the contract's validation rules
/// (unknown property, unknown enum value, `reason` only with `incorrect`, unknown `reason`) need
/// to all come back as the same `400 invalid_request` with no further distinction, which a plain
/// model-binding failure doesn't give control over.
/// </summary>
public static class LabelsEndpoint
{
    private static readonly string[] AllowedReasons = ["wrong_rep_count", "bar_path_off", "wrong_lift"];

    public static void Map(IEndpointRouteBuilder v1)
    {
        v1.MapPost("/jobs/{jobId}/labels", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(HttpContext context, VisionDbContext db, string jobId, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(context.User.FindFirst(VisionClaimTypes.TenantId)!.Value);

        var parsed = await TryParseRequestAsync(context.Request, cancellationToken);
        if (parsed.Error is not null)
        {
            await VisionProblem.WriteInvalidRequestAsync(context, parsed.Error, cancellationToken);
            return Results.Empty;
        }

        await using var transaction = await db.BeginTenantScopeAsync(tenantId, cancellationToken);

        // Row security already limits this to the tenant's own jobs; a malformed or
        // another-tenant's job id matches no row either way, so both cases become the same 404
        // the contract requires a client can't tell apart.
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            await VisionProblem.WriteNotFoundAsync(context, cancellationToken);
            return Results.Empty;
        }

        if (job.Status != "completed")
        {
            await VisionProblem.WriteJobNotCompletedAsync(context, cancellationToken);
            return Results.Empty;
        }

        var label = await LabelsRepository.UpsertAsync(db, tenantId, jobId, parsed.Assessment!, parsed.Reason, DateTime.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Json(
            new JobLabelResponse(label.JobId, label.Assessment, label.Reason, label.UpdatedAtUtc),
            ResponseSerializerOptions,
            contentType: "application/json",
            statusCode: StatusCodes.Status200OK);
    }

    private static readonly JsonSerializerOptions ResponseSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<ParsedLabelRequest> TryParseRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return ParsedLabelRequest.Invalid("The request body is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ParsedLabelRequest.Invalid("The request body must be a JSON object.");
            }

            if (!root.TryGetProperty("assessment", out var assessmentElement) || assessmentElement.ValueKind != JsonValueKind.String)
            {
                return ParsedLabelRequest.Invalid("assessment: is required.");
            }

            var assessment = assessmentElement.GetString();
            if (assessment is not ("correct" or "incorrect"))
            {
                return ParsedLabelRequest.Invalid($"""assessment: "{assessment}" is not "correct" or "incorrect".""");
            }

            string? reason = null;
            if (root.TryGetProperty("reason", out var reasonElement))
            {
                if (assessment == "correct")
                {
                    return ParsedLabelRequest.Invalid("reason: is only allowed with assessment \"incorrect\".");
                }

                if (reasonElement.ValueKind != JsonValueKind.String || !AllowedReasons.Contains(reasonElement.GetString()))
                {
                    return ParsedLabelRequest.Invalid("""reason: is not a known reason.""");
                }

                reason = reasonElement.GetString();
            }

            var allowedProperties = assessment == "correct" ? AssessmentOnlyProperties : AssessmentAndReasonProperties;
            foreach (var property in root.EnumerateObject())
            {
                if (!allowedProperties.Contains(property.Name))
                {
                    return ParsedLabelRequest.Invalid($"""{property.Name}: is not a known property.""");
                }
            }

            return ParsedLabelRequest.Valid(assessment, reason);
        }
    }

    private static readonly string[] AssessmentOnlyProperties = ["assessment"];
    private static readonly string[] AssessmentAndReasonProperties = ["assessment", "reason"];

    private sealed record ParsedLabelRequest(string? Assessment, string? Reason, string? Error)
    {
        public static ParsedLabelRequest Valid(string assessment, string? reason) => new(assessment, reason, null);

        public static ParsedLabelRequest Invalid(string error) => new(null, null, error);
    }

    private sealed record JobLabelResponse(string JobId, string Assessment, string? Reason, DateTime UpdatedAt);
}
