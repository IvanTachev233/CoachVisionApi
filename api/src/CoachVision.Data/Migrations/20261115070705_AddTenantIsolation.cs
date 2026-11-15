using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoachVision.Data.Migrations
{
    /// <summary>
    /// The tenant boundary. Postgres enforces it, not application code:
    ///
    /// - <c>vision_api_runtime</c> is the role the API connects as. It owns nothing, has no
    ///   <c>BYPASSRLS</c>, and gets only SELECT/INSERT/UPDATE/DELETE on the five tables — no
    ///   ALTER, no DDL. The role is created <c>NOLOGIN</c> and with no password: it can't connect
    ///   at all until the runbook has Ivan run <c>ALTER ROLE ... LOGIN PASSWORD '&lt;generated&gt;'</c>
    ///   by hand, so no credential for it is ever checked in. Tests do the same with a password
    ///   generated for that run only (<c>VisionPostgresFixture</c>).
    /// - This migration must run as an identity that owns the five tables (or otherwise has
    ///   `GRANT`/`ALTER TABLE` rights on them) and can create roles and a `SECURITY DEFINER`
    ///   function — on Neon, a role made in the Console, CLI or API (including the project's
    ///   default `&lt;db&gt;_owner`), which Neon makes a member of `neon_superuser`
    ///   (`CREATEDB`, `CREATEROLE`, `BYPASSRLS`; Neon projects have no real Postgres superuser).
    ///   A role created by plain SQL gets only default public-schema privileges and is not a
    ///   `neon_superuser` member, which is exactly why `vision_api_runtime` (created below, by
    ///   SQL) ends up with none of those. `VisionPostgresFixture.MigratorConnectionString` plays
    ///   this part for tests: a non-superuser role, `LOGIN CREATEROLE BYPASSRLS`, owning the test
    ///   database, created by the throwaway container's own superuser — not that superuser
    ///   itself, which would hide the privilege boundary this migration depends on. Run migrations
    ///   over Neon's *direct* connection, not its pooled one (below).
    /// - <c>app_current_tenant_id()</c> reads a transaction-local setting
    ///   (<c>Tenancy.TenantScope</c> sets it with <c>set_config(..., true)</c>). Neon's pooled
    ///   endpoint runs PgBouncer in transaction mode, where session commands (<c>SET</c>,
    ///   <c>RESET</c>, session-level advisory locks, ...) are not supported at all: a
    ///   session-level <c>SET</c> would not merely be unreliable there, it would fail outright.
    ///   Scoping the setting to one transaction, alongside the queries that read it, is what the
    ///   pooled endpoint actually supports. A missing or empty setting returns SQL NULL, which a
    ///   row-security policy comparison never matches, so "no tenant set" shows no rows rather
    ///   than erroring or defaulting to "see everything".
    /// - Every tenant-owned table gets <c>ENABLE</c> *and* <c>FORCE</c> row level security (FORCE
    ///   also binds the table owner to the policy; <c>vision_api_runtime</c> is never the owner,
    ///   so plain ENABLE would already cover it for that role alone — FORCE is what makes the
    ///   <c>lookup_api_key_by_hash</c> bypass below a deliberate, single, documented exception
    ///   rather than an accident of ownership), with one policy using both <c>USING</c> (read,
    ///   update, delete) and <c>WITH CHECK</c> (insert, and the new values of an update).
    /// - <c>lookup_api_key_by_hash</c> is the one deliberate hole: a <c>SECURITY DEFINER</c>
    ///   function the key check calls before any tenant is known (there is no session to scope
    ///   to yet). It runs with its *owner's* privileges, not the caller's — the migrator that
    ///   creates it here, which is why this migration must run as a role Neon has made a
    ///   `neon_superuser` member (`BYPASSRLS`): a `SECURITY DEFINER` function only bypasses row
    ///   level security at all if its owner has `BYPASSRLS` (or is a real superuser); an owner
    ///   without it would see the same empty result `vision_api_runtime` would. It reads only
    ///   <c>api_keys</c> and returns nothing else; <c>vision_api_runtime</c> gets EXECUTE on it
    ///   and no broader bypass, and PUBLIC gets none at all.
    /// </summary>
    public partial class AddTenantIsolation : Migration
    {
        // Kept as a literal here (an applied migration's SQL is never edited) rather than
        // referencing VisionDbOptions.RuntimeRoleName; that constant's doc comment points back
        // here instead.
        private const string RuntimeRole = "vision_api_runtime";

        private static readonly string[] TenantOwnedTables = ["api_keys", "jobs", "results", "usage"];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOLOGIN, no password: the role can't connect until the runbook's rotation step
            // gives it one by hand. Nothing here is a credential.
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{RuntimeRole}') THEN
                        CREATE ROLE {RuntimeRole} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
                            NOREPLICATION NOBYPASSRLS;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql($"GRANT USAGE ON SCHEMA public TO {RuntimeRole};");
            migrationBuilder.Sql($"GRANT SELECT, INSERT, UPDATE, DELETE ON tenants, api_keys, jobs, results, usage TO {RuntimeRole};");

            migrationBuilder.Sql("""
                CREATE FUNCTION app_current_tenant_id() RETURNS uuid
                LANGUAGE sql STABLE AS $$
                    SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
                $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION app_current_tenant_id() FROM PUBLIC;");
            migrationBuilder.Sql($"GRANT EXECUTE ON FUNCTION app_current_tenant_id() TO {RuntimeRole};");

            // tenants: a tenant's own row is keyed by id, not a separate tenant_id column.
            migrationBuilder.Sql("ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE tenants FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON tenants
                    USING (id = app_current_tenant_id())
                    WITH CHECK (id = app_current_tenant_id());
                """);

            foreach (var table in TenantOwnedTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = app_current_tenant_id())
                        WITH CHECK (tenant_id = app_current_tenant_id());
                    """);
            }

            // The narrow, deliberate seam authentication needs: looking up a key's tenant before any tenant
            // is known. SECURITY DEFINER runs as this function's owner (the migrator), not as the
            // caller: it is the only place vision_api_runtime's row security is bypassed, and only
            // for this one read of this one table, and only because the migrator owns it with
            // BYPASSRLS (see the class comment above).
            migrationBuilder.Sql("""
                CREATE FUNCTION lookup_api_key_by_hash(p_key_hash bytea)
                RETURNS TABLE(tenant_id uuid, api_key_id uuid, kind text, revoked_at timestamptz)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
                    SELECT tenant_id, id, kind, revoked_at FROM api_keys WHERE key_hash = p_key_hash
                $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION lookup_api_key_by_hash(bytea) FROM PUBLIC;");
            migrationBuilder.Sql($"GRANT EXECUTE ON FUNCTION lookup_api_key_by_hash(bytea) TO {RuntimeRole};");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS lookup_api_key_by_hash(bytea);");

            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON tenants;");
            migrationBuilder.Sql("ALTER TABLE tenants DISABLE ROW LEVEL SECURITY;");

            foreach (var table in TenantOwnedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_current_tenant_id();");
            migrationBuilder.Sql($"REVOKE ALL ON tenants, api_keys, jobs, results, usage FROM {RuntimeRole};");
            migrationBuilder.Sql($"REVOKE USAGE ON SCHEMA public FROM {RuntimeRole};");
            migrationBuilder.Sql($"DROP ROLE IF EXISTS {RuntimeRole};");
        }
    }
}
