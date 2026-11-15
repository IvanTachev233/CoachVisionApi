namespace CoachVision.Data;

/// <summary>Names shared between the API's configuration, the migrations and the runbook.</summary>
public static class VisionDbOptions
{
    /// <summary>
    /// The configuration key the API reads its connection string from
    /// (<c>ConnectionStrings__VisionDb</c> as an environment variable, or Secret Manager under
    /// this name once the runbook's rotation step is done). Absent in an environment with no database yet.
    /// </summary>
    public const string ConnectionStringName = "VisionDb";

    /// <summary>
    /// The role the API connects as at run time. It owns no table, has no <c>BYPASSRLS</c>, and
    /// cannot alter the schema; see the <c>AddTenantIsolation</c> migration (which creates it
    /// under the same literal name — kept in sync by this cross-reference rather than a shared
    /// constant, since an applied migration's SQL is never edited) and the runbook.
    /// </summary>
    public const string RuntimeRoleName = "vision_api_runtime";
}
