using Commercial.Application.Abstractions.Scope;
using Commercial.Infrastructure.Persistence;
using Commercial.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Commercial.IntegrationTests.Persistence.RlsSession;

namespace Commercial.IntegrationTests.Persistence;

/// <summary>
/// Phase 1 WS3 — proves that the context written by the REAL <c>RlsDbSessionInitializer</c> drives PostgreSQL RLS.
/// The table, helper function and policy below are TEST-ONLY (created per throw-away Testcontainers database by the
/// owner identity); they are NOT a production migration — production RLS schema belongs to WS4. Queries run as the
/// runtime identity through the real CommercialDbContext (initializer + fingerprint guard).
/// </summary>
public sealed class RlsSessionPolicyProofTests(ContainersFixture containers)
{
    private const string TestSchemaSql = """
        CREATE FUNCTION commercial.ws3_test_ctx(key text) RETURNS text
            LANGUAGE sql STABLE SECURITY INVOKER
            AS $$ SELECT nullif(current_setting('commercial.' || key, true), '') $$;

        CREATE TABLE commercial.ws3_rls_probe (
            id uuid PRIMARY KEY,
            tenant_id uuid NOT NULL,
            company_id uuid NOT NULL,
            customer_account_id uuid NULL,
            payload text NOT NULL);
        ALTER TABLE commercial.ws3_rls_probe ENABLE ROW LEVEL SECURITY;
        ALTER TABLE commercial.ws3_rls_probe FORCE ROW LEVEL SECURITY;

        -- NULL / '' / missing => scope unavailable => no rows. Tenant-wide only with the explicit WORKFORCE grant.
        CREATE POLICY ws3_rls_probe_isolation ON commercial.ws3_rls_probe
            USING (
                commercial.ws3_test_ctx('tenant_id') IS NOT NULL
                AND tenant_id = commercial.ws3_test_ctx('tenant_id')::uuid
                AND (
                    (commercial.ws3_test_ctx('principal_type') = 'CUSTOMER'
                        AND company_id = commercial.ws3_test_ctx('company_id')::uuid
                        AND customer_account_id = commercial.ws3_test_ctx('customer_account_id')::uuid)
                    OR (commercial.ws3_test_ctx('principal_type') IN ('WORKFORCE', 'SYSTEM')
                        AND company_id = commercial.ws3_test_ctx('company_id')::uuid)
                    OR (commercial.ws3_test_ctx('principal_type') = 'WORKFORCE'
                        AND commercial.ws3_test_ctx('company_id') IS NULL
                        AND commercial.ws3_test_ctx('tenant_wide_company_access') = 'true')))
            WITH CHECK (
                commercial.ws3_test_ctx('tenant_id') IS NOT NULL
                AND tenant_id = commercial.ws3_test_ctx('tenant_id')::uuid
                AND company_id = commercial.ws3_test_ctx('company_id')::uuid
                AND customer_account_id IS NOT DISTINCT FROM commercial.ws3_test_ctx('customer_account_id')::uuid);
        """;

    private async Task<(ServiceProvider Provider, TestDatabase Database)> ArrangeAsync(string? options = null)
    {
        var database = await containers.CreateDatabaseAsync(options);
        await ExecAsync(database.OwnerConnectionString, TestSchemaSql);

        // Seed as the bootstrap superuser (bypasses RLS) — fixtures only, never a runtime path.
        await ExecAsync(database.AdminConnectionString, $"""
            INSERT INTO commercial.ws3_rls_probe VALUES
              (gen_random_uuid(), '{TenantA}', '{CompanyA1}', NULL, 'A1-W'),
              (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{AccountX}', 'A1-X'),
              (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{AccountY}', 'A1-Y'),
              (gen_random_uuid(), '{TenantA}', '{CompanyA2}', NULL, 'A2-W'),
              (gen_random_uuid(), '{TenantB}', '{CompanyB1}', NULL, 'B1-W');
            """);

        return (containers.BuildServices(database.RuntimeConnectionString), database);
    }

    private static Task<List<string>> PayloadsAsync(IServiceProvider provider, ExecutionScope scope) =>
        InScopeAsync(provider, scope, db => db.Database
            .SqlQueryRaw<string>("SELECT payload AS \"Value\" FROM commercial.ws3_rls_probe ORDER BY payload")
            .ToListAsync());

    [Fact]
    [Trait("ADR-0001", "cross-tenant")]
    public async Task Tenant_A_sees_only_tenant_A()
    {
        var (provider, _) = await ArrangeAsync();
        await using var _p = provider;

        Assert.Equal(["A1-W", "A1-X", "A1-Y", "A2-W"], await PayloadsAsync(provider, WorkforceTenantWideA()));
        Assert.Equal(["B1-W"], await PayloadsAsync(provider, WorkforceB1()));
        Assert.Equal(["B1-W"], await PayloadsAsync(provider, SystemB1())); // SYSTEM is tenant + company bound, not unrestricted
    }

    [Fact]
    [Trait("ADR-0001", "cross-company")]
    public async Task Company_A1_cannot_see_company_A2()
    {
        var (provider, _) = await ArrangeAsync();
        await using var _p = provider;

        Assert.Equal(["A1-W", "A1-X", "A1-Y"], await PayloadsAsync(provider, WorkforceA1()));
        Assert.Equal(["A2-W"], await PayloadsAsync(provider, WorkforceA2()));
    }

    [Fact]
    [Trait("ADR-0001", "cross-customer")]
    public async Task Customer_binding_X_cannot_see_Y_and_cannot_write_into_Y()
    {
        var (provider, _) = await ArrangeAsync();
        await using var _p = provider;

        Assert.Equal(["A1-X"], await PayloadsAsync(provider, CustomerX()));
        Assert.Equal(["A1-Y"], await PayloadsAsync(provider, CustomerY()));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InScopeAsync(provider, CustomerX(), db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO commercial.ws3_rls_probe VALUES (gen_random_uuid(), {0}, {1}, {2}, 'forged')",
            TenantA.Value, CompanyA1.Value, AccountY.Value)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState); // WITH CHECK violation (42501)
    }

    [Fact]
    [Trait("ADR-0001", "fail-closed")]
    public async Task Missing_or_empty_context_sees_zero_rows()
    {
        var (provider, database) = await ArrangeAsync();
        await using var _p = provider;

        Assert.Empty(await PayloadsAsync(provider, ExecutionScope.Anonymous("anon")));
        Assert.Empty(await PayloadsAsync(provider, ExecutionScope.Unscoped));
        Assert.Empty(await PayloadsAsync(provider, ExecutionScope.SystemInfrastructure("infra")));
        Assert.Empty(await PayloadsAsync(provider, Identity()));

        // A raw runtime session that never received any context (keys missing entirely) also sees nothing.
        await using var raw = new NpgsqlConnection(database.RuntimeConnectionString);
        await raw.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM commercial.ws3_rls_probe", raw);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    [Trait("ADR-0001", "pool-scope-leak")]
    public async Task Pooled_connection_X_unscoped_Y_X_has_no_residue_under_RLS()
    {
        var (provider, _) = await ArrangeAsync("Maximum Pool Size=1");
        await using var _p = provider;

        Assert.Equal(["A1-X"], await PayloadsAsync(provider, CustomerX()));
        Assert.Empty(await PayloadsAsync(provider, ExecutionScope.Unscoped));
        Assert.Equal(["A1-Y"], await PayloadsAsync(provider, CustomerY()));
        Assert.Equal(["B1-W"], await PayloadsAsync(provider, WorkforceB1()));
        Assert.Equal(["A1-X"], await PayloadsAsync(provider, CustomerX()));
    }

    [Fact]
    [Trait("ADR-0001", "runtime-role-not-owner")]
    public async Task Session_runs_as_runtime_role_which_does_not_own_and_cannot_disable_RLS()
    {
        var (provider, _) = await ArrangeAsync();
        await using var _p = provider;

        var facts = await InScopeAsync(provider, WorkforceA1(), db => db.Database.SqlQueryRaw<string>("""
            SELECT current_user || '|' || pg_get_userbyid(c.relowner) || '|' || r.rolbypassrls::text AS "Value"
            FROM pg_class c JOIN pg_roles r ON r.rolname = current_user
            WHERE c.oid = 'commercial.ws3_rls_probe'::regclass
            """).SingleAsync());
        Assert.Equal("commercial_platform_app|commercial_platform_owner|false", facts);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InScopeAsync(provider, WorkforceA1(),
            db => db.Database.ExecuteSqlRawAsync("ALTER TABLE commercial.ws3_rls_probe DISABLE ROW LEVEL SECURITY")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
