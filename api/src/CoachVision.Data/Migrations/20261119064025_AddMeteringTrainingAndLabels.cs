using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoachVision.Data.Migrations
{
    /// <summary>
    /// V4b: a tenant's own training flag (off by default, narrowing a job's own flag, never
    /// widening it); the usage table's "once per video and feature" unique index (Ivan's rule,
    /// 2026-11-19: a usage record stays exactly as written once inserted —
    /// <c>CoachVision.Data.Metering.MeteringService.RecordUsageAsync</c> is the only writer, and relies
    /// on this exact index for its <c>ON CONFLICT DO NOTHING</c>); and <c>job_labels</c>, a new
    /// tenant-owned table under row-level security, the same shape as <c>results</c> and
    /// <c>usage</c> (its own <c>id</c>, plus a composite foreign key to <c>jobs</c>).
    ///
    /// Must run as the migrator identity (owns the tables, member of <c>neon_superuser</c> on
    /// Neon), the same as <c>AddKeyRotationAndTenantLimits</c>: it grants the run-time role
    /// access to the new table, and adds row-level security and a policy to it, neither of which
    /// that role may do itself. <c>tenants</c> already has a table-wide grant for the run-time
    /// role (<c>AddTenantIsolation</c>); a new column on an already-granted table needs no fresh
    /// GRANT.
    ///
    /// Also fixes a bug the first metering test caught: <c>InitialSchema</c>'s composite foreign
    /// key from <c>usage</c> to <c>jobs</c> was <c>ON DELETE SET NULL</c> with no column list,
    /// which Postgres reads as nulling *every* referencing column — including
    /// <c>usage.tenant_id</c>, which is <c>NOT NULL</c>, so deleting a job whose usage already
    /// existed would itself fail with a not-null violation instead of leaving the usage row
    /// behind. Recreated here with Postgres 15+'s column-specific
    /// <c>ON DELETE SET NULL (job_id)</c>, which nulls only <c>job_id</c> — exactly what "a usage
    /// record outlives its job" needs. EF Core's fluent API has no column-specific SetNull, so
    /// this step is raw SQL rather than something the C# model configures.
    /// </summary>
    public partial class AddMeteringTrainingAndLabels : Migration
    {
        // Kept as a literal, the same as the other migrations: an applied migration's SQL is
        // never edited.
        private const string RuntimeRole = "vision_api_runtime";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_usage_tenant_id_job_id",
                table: "usage");

            migrationBuilder.AddColumn<bool>(
                name: "training_allowed",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "job_labels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<string>(type: "text", nullable: false),
                    assessment = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_labels", x => x.id);
                    table.CheckConstraint("CK_job_labels_assessment", "assessment IN ('correct', 'incorrect')");
                    table.CheckConstraint("CK_job_labels_reason", "reason IS NULL OR (assessment = 'incorrect' AND reason IN ('wrong_rep_count', 'bar_path_off', 'wrong_lift'))");
                    table.ForeignKey(
                        name: "FK_job_labels_jobs_tenant_id_job_id",
                        columns: x => new { x.tenant_id, x.job_id },
                        principalTable: "jobs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usage_tenant_id_job_id_feature",
                table: "usage",
                columns: new[] { "tenant_id", "job_id", "feature" },
                unique: true);

            // A job has at most one label: the endpoint overwrites the existing row in place
            // rather than inserting a second one, enforced here rather than only in application
            // code.
            migrationBuilder.CreateIndex(
                name: "IX_job_labels_tenant_id_job_id",
                table: "job_labels",
                columns: new[] { "tenant_id", "job_id" },
                unique: true);

            // job_labels is a brand-new tenant-owned table: same grant, ENABLE + FORCE row-level
            // security, and policy shape as every other one (AddTenantIsolation,
            // AddKeyRotationAndTenantLimits).
            migrationBuilder.Sql($"GRANT SELECT, INSERT, UPDATE, DELETE ON job_labels TO {RuntimeRole};");
            migrationBuilder.Sql("ALTER TABLE job_labels ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE job_labels FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON job_labels
                    USING (tenant_id = app_current_tenant_id())
                    WITH CHECK (tenant_id = app_current_tenant_id());
                """);

            // See the class remarks: only job_id should be nulled when a job is deleted, never
            // tenant_id.
            migrationBuilder.Sql("""ALTER TABLE usage DROP CONSTRAINT "FK_usage_jobs_tenant_id_job_id";""");
            migrationBuilder.Sql("""
                ALTER TABLE usage
                    ADD CONSTRAINT "FK_usage_jobs_tenant_id_job_id"
                    FOREIGN KEY (tenant_id, job_id) REFERENCES jobs (tenant_id, id)
                    ON DELETE SET NULL (job_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE usage DROP CONSTRAINT "FK_usage_jobs_tenant_id_job_id";""");
            migrationBuilder.Sql("""
                ALTER TABLE usage
                    ADD CONSTRAINT "FK_usage_jobs_tenant_id_job_id"
                    FOREIGN KEY (tenant_id, job_id) REFERENCES jobs (tenant_id, id)
                    ON DELETE SET NULL;
                """);

            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON job_labels;");
            migrationBuilder.Sql("ALTER TABLE job_labels DISABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"REVOKE ALL ON job_labels FROM {RuntimeRole};");

            migrationBuilder.DropTable(
                name: "job_labels");

            migrationBuilder.DropIndex(
                name: "IX_usage_tenant_id_job_id_feature",
                table: "usage");

            migrationBuilder.DropColumn(
                name: "training_allowed",
                table: "tenants");

            migrationBuilder.CreateIndex(
                name: "IX_usage_tenant_id_job_id",
                table: "usage",
                columns: new[] { "tenant_id", "job_id" });
        }
    }
}
