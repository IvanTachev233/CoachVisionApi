using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoachVision.Data.Migrations
{
    /// <summary>
    /// A key's kind is checked at the database, not only validated by the code that inserts it.
    /// At most two active (unrevoked) keys per tenant and kind, enforced where a race between two
    /// simultaneous issues can't beat it (an advisory lock inside the trigger, not only a
    /// count-then-insert in application code); revoking is one-way, so an update can't be used to
    /// bring a revoked key back and bypass that same limit. Also adds the per-tenant limits row
    /// job admission reads.
    ///
    /// Must run as the same migrator identity as the migration before it (owns the tables, member
    /// of <c>neon_superuser</c> on Neon): it adds constraints and triggers to <c>api_keys</c>, and
    /// row-level security to the new table, neither of which the run-time role is allowed to do.
    /// </summary>
    public partial class AddKeyRotationAndTenantLimits : Migration
    {
        // Kept as a literal here, the same way the earlier migration does, rather than
        // referencing the shared constant: an applied migration's SQL is never edited.
        private const string RuntimeRole = "vision_api_runtime";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_limits",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate_limit_per_minute = table.Column<int>(type: "integer", nullable: false),
                    concurrent_jobs_limit = table.Column<int>(type: "integer", nullable: false),
                    daily_minutes_limit = table.Column<decimal>(type: "numeric", nullable: false),
                    max_clip_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    max_clip_length_seconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_limits", x => x.tenant_id);
                    table.CheckConstraint("CK_tenant_limits_max_clip_size_bytes_bounded", "max_clip_size_bytes > 0 AND max_clip_size_bytes <= 209715200");
                    table.CheckConstraint("CK_tenant_limits_positive", "rate_limit_per_minute > 0 AND concurrent_jobs_limit > 0 AND daily_minutes_limit > 0 AND max_clip_length_seconds > 0");
                    table.ForeignKey(
                        name: "FK_tenant_limits_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Keyed by tenant_id directly (one row per tenant, like tenants.id) rather than its
            // own id, so its row-security policy compares tenant_id the same way the other
            // tenant-owned tables do.
            migrationBuilder.Sql($"GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_limits TO {RuntimeRole};");
            migrationBuilder.Sql("ALTER TABLE tenant_limits ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE tenant_limits FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON tenant_limits
                    USING (tenant_id = app_current_tenant_id())
                    WITH CHECK (tenant_id = app_current_tenant_id());
                """);

            migrationBuilder.Sql("""
                ALTER TABLE api_keys ADD CONSTRAINT "CK_api_keys_kind" CHECK (kind IN ('live', 'test'));
                """);

            // At most two active keys per tenant and kind. A plain CHECK can't count rows, and a
            // unique index can't express "at most 2"; a BEFORE INSERT trigger can, but by itself
            // is still open to two concurrent inserts both reading "1 active, go ahead" before
            // either commits (read committed sees no uncommitted row from the other). The
            // advisory lock below serialises concurrent attempts for the same (tenant_id, kind) so
            // the count it then takes is never racing another insert for that same pair;
            // unrelated tenants/kinds take different lock keys and don't block each other.
            migrationBuilder.Sql("""
                CREATE FUNCTION enforce_api_key_rotation_limit() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    active_count integer;
                BEGIN
                    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.tenant_id::text || ':' || NEW.kind, 0));

                    SELECT count(*) INTO active_count
                    FROM api_keys
                    WHERE tenant_id = NEW.tenant_id AND kind = NEW.kind AND revoked_at IS NULL;

                    IF active_count >= 2 THEN
                        RAISE EXCEPTION 'api_key_rotation_limit_exceeded: tenant % already has 2 active % keys', NEW.tenant_id, NEW.kind
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN NEW;
                END;
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER enforce_api_key_rotation_limit
                    BEFORE INSERT ON api_keys
                    FOR EACH ROW
                    EXECUTE FUNCTION enforce_api_key_rotation_limit();
                """);

            // The insert trigger above only stops a third key from being *issued*; the run-time
            // role still has table-wide UPDATE, and either an UPDATE clearing revoked_at back to
            // null, or one simply changing kind (a revoked test key relabelled live) would reach
            // three active keys the same way, without ever going through IssueAsync's trigger at
            // all. A key row is therefore immutable after insert except for revoked_at, and that
            // only going from null to a value once — never cleared, never changed, never backed
            // out to an earlier or later time.
            migrationBuilder.Sql("""
                CREATE FUNCTION enforce_api_key_immutable_except_revocation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                        OR NEW.key_hash IS DISTINCT FROM OLD.key_hash
                        OR NEW.kind IS DISTINCT FROM OLD.kind
                        OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                        RAISE EXCEPTION 'api_key_is_immutable: tenant % key % cannot change tenant_id, key_hash, kind or created_at', OLD.tenant_id, OLD.id
                            USING ERRCODE = '23514';
                    END IF;

                    IF OLD.revoked_at IS NOT NULL AND NEW.revoked_at IS DISTINCT FROM OLD.revoked_at THEN
                        RAISE EXCEPTION 'api_key_revocation_is_one_way: tenant % key % is already revoked', OLD.tenant_id, OLD.id
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN NEW;
                END;
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER enforce_api_key_immutable_except_revocation
                    BEFORE UPDATE ON api_keys
                    FOR EACH ROW
                    EXECUTE FUNCTION enforce_api_key_immutable_except_revocation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS enforce_api_key_immutable_except_revocation ON api_keys;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS enforce_api_key_immutable_except_revocation();");

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS enforce_api_key_rotation_limit ON api_keys;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS enforce_api_key_rotation_limit();");
            migrationBuilder.Sql("""ALTER TABLE api_keys DROP CONSTRAINT IF EXISTS "CK_api_keys_kind";""");

            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON tenant_limits;");
            migrationBuilder.Sql("ALTER TABLE tenant_limits DISABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"REVOKE ALL ON tenant_limits FROM {RuntimeRole};");

            migrationBuilder.DropTable(
                name: "tenant_limits");
        }
    }
}
