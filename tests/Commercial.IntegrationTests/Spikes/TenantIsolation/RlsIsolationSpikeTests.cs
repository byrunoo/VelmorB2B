using Commercial.IntegrationTests.Infrastructure;
using Npgsql;
using static Commercial.IntegrationTests.Spikes.TenantIsolation.RlsSpikeDatabase;

namespace Commercial.IntegrationTests.Spikes.TenantIsolation;

/// <summary>
/// ADR-0001 SPIKE — empirical PostgreSQL row-level-security behaviour on Testcontainers.
/// Evidence for the decision record; not a production implementation.
/// </summary>
[Trait("ADR-0001", "spike")]
public sealed class RlsIsolationSpikeTests(ContainersFixture containers)
{
    private static readonly string[] OwnRowsOfX = ["A1-X-1", "A1-X-2"];

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    [Fact] // scenario 1
    public async Task Tenant_A_cannot_read_tenant_B()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppConnectionString);
        await SpikeSession.ApplyAsync(c, SpikeScope.WorkforceTenantWide(TenantA), transactionLocal: false);

        var rows = await SpikeSession.PayloadsAsync(c);

        Assert.Equal(["A1-X-1", "A1-X-2", "A1-Y-1", "A1-Y-2", "A2-Z-1"], rows);
        Assert.DoesNotContain("B1-W-1", rows);
    }

    [Fact] // scenario 2
    public async Task Tenant_A_cannot_write_tenant_B()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppConnectionString);
        await SpikeSession.ApplyAsync(c, SpikeScope.WorkforceTenantWide(TenantA), transactionLocal: false);

        // Updates/deletes of invisible rows silently affect 0 rows (USING).
        Assert.Equal(0, await SpikeSession.ExecuteAsync(c, $"UPDATE spike.scoped_rows SET payload = 'hacked' WHERE tenant_id = '{TenantB}'"));
        Assert.Equal(0, await SpikeSession.ExecuteAsync(c, $"DELETE FROM spike.scoped_rows WHERE tenant_id = '{TenantB}'"));

        // Inserting into another tenant violates WITH CHECK.
        var insert = await Assert.ThrowsAsync<PostgresException>(() => SpikeSession.ExecuteAsync(c,
            $"INSERT INTO spike.scoped_rows VALUES (gen_random_uuid(), '{TenantB}', '{CompanyB1}', '{CustomerW}', 'injected')"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insert.SqlState); // 42501 new row violates row-level security policy

        // Moving an own row into another tenant also violates WITH CHECK.
        var move = await Assert.ThrowsAsync<PostgresException>(() => SpikeSession.ExecuteAsync(c,
            $"UPDATE spike.scoped_rows SET tenant_id = '{TenantB}' WHERE payload = 'A1-X-1'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, move.SqlState);

        await using var admin = await OpenAsync(db.AdminConnectionString);
        Assert.Equal(["B1-W-1"], await ReadAsync(admin, $"SELECT payload FROM spike.scoped_rows WHERE tenant_id = '{TenantB}'"));
    }

    [Fact] // scenario 3
    public async Task Company_scope_isolates_companies_and_tenant_wide_access_must_be_explicit()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppConnectionString);

        await SpikeSession.ApplyAsync(c, SpikeScope.Workforce(TenantA, CompanyA1), transactionLocal: false);
        Assert.Equal(["A1-X-1", "A1-X-2", "A1-Y-1", "A1-Y-2"], await SpikeSession.PayloadsAsync(c));

        await SpikeSession.ApplyAsync(c, SpikeScope.Workforce(TenantA, CompanyA2), transactionLocal: false);
        Assert.Equal(["A2-Z-1"], await SpikeSession.PayloadsAsync(c));

        // Workforce without company and without an explicit tenant-wide grant sees nothing (no implicit widening).
        await SpikeSession.ApplyAsync(c, new SpikeScope("WORKFORCE", TenantA), transactionLocal: false);
        Assert.Empty(await SpikeSession.PayloadsAsync(c));

        // A CUSTOMER can never use the tenant-wide grant even if it were set.
        await SpikeSession.ApplyAsync(c, new SpikeScope("CUSTOMER", TenantA, CompanyA2, CustomerX, TenantWideCompanyAccess: true), transactionLocal: false);
        Assert.Empty(await SpikeSession.PayloadsAsync(c));
    }

    [Fact] // scenarios 4 + 5
    public async Task Customer_sees_and_writes_only_own_rows()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppConnectionString);
        await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: false);

        Assert.Equal(OwnRowsOfX, await SpikeSession.PayloadsAsync(c));
        Assert.Equal(0, await SpikeSession.ExecuteAsync(c, $"UPDATE spike.scoped_rows SET payload = 'x' WHERE customer_account_id = '{CustomerY}'"));

        Assert.Equal(1, await SpikeSession.ExecuteAsync(c, "UPDATE spike.scoped_rows SET payload = 'A1-X-1*' WHERE payload = 'A1-X-1'"));
        Assert.Equal(1, await SpikeSession.ExecuteAsync(c,
            $"INSERT INTO spike.scoped_rows VALUES (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{CustomerX}', 'A1-X-3')"));
        await Assert.ThrowsAsync<PostgresException>(() => SpikeSession.ExecuteAsync(c,
            $"INSERT INTO spike.scoped_rows VALUES (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{CustomerY}', 'forged-for-Y')"));

        Assert.Equal(["A1-X-1*", "A1-X-2", "A1-X-3"], await SpikeSession.PayloadsAsync(c));
    }

    [Fact] // scenario 6
    public async Task Pooled_connection_reuse_never_leaks_scope_when_every_key_is_rewritten()
    {
        var db = await CreateAsync(containers);
        var pooled = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1 }.ConnectionString;

        int pid1, pid2;
        await using (var c = await OpenAsync(pooled))
        {
            pid1 = c.ProcessID;
            await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: false);
            Assert.Equal(OwnRowsOfX, await SpikeSession.PayloadsAsync(c));
        }

        await using (var c = await OpenAsync(pooled))
        {
            pid2 = c.ProcessID;
            await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerY), transactionLocal: false);
            Assert.Equal(["A1-Y-1", "A1-Y-2"], await SpikeSession.PayloadsAsync(c));
        }

        Assert.Equal(pid1, pid2); // same physical backend reused
    }

    [Fact] // scenario 6 (mechanism detail)
    public async Task Npgsql_pool_reset_clears_session_settings_but_No_Reset_On_Close_leaks_them()
    {
        var db = await CreateAsync(containers);

        // Default: Npgsql resets session state (DISCARD ALL) when a pooled connection is returned.
        var defaultPool = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1, ApplicationName = "reset" }.ConnectionString;
        await using (var c = await OpenAsync(defaultPool))
        {
            await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: false);
        }

        await using (var c = await OpenAsync(defaultPool))
        {
            Assert.Empty(await SpikeSession.PayloadsAsync(c)); // nothing inherited -> fail-closed
        }

        // With No Reset On Close=true the previous session's scope survives on the reused connection.
        var noReset = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1, NoResetOnClose = true, ApplicationName = "noreset" }.ConnectionString;
        await using (var c = await OpenAsync(noReset))
        {
            await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: false);
        }

        await using (var c = await OpenAsync(noReset))
        {
            Assert.Equal(OwnRowsOfX, await SpikeSession.PayloadsAsync(c)); // LEAK: proves the hook must rewrite every key on every open
        }
    }

    [Fact] // scenario 7
    public async Task Missing_or_partial_scope_is_fail_closed()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppConnectionString);

        Assert.Empty(await SpikeSession.PayloadsAsync(c)); // never set in this session

        await SpikeSession.ApplyAsync(c, SpikeScope.None, transactionLocal: false);
        Assert.Empty(await SpikeSession.PayloadsAsync(c));

        await SpikeSession.ApplyAsync(c, new SpikeScope(null, TenantA, CompanyA1), transactionLocal: false); // no principal type
        Assert.Empty(await SpikeSession.PayloadsAsync(c));

        await SpikeSession.ApplyAsync(c, new SpikeScope("CUSTOMER", TenantA, CompanyA1), transactionLocal: false); // customer without account
        Assert.Empty(await SpikeSession.PayloadsAsync(c));

        await SpikeSession.ApplyAsync(c, SpikeScope.None, transactionLocal: false);
        await Assert.ThrowsAsync<PostgresException>(() => SpikeSession.ExecuteAsync(c,
            $"INSERT INTO spike.scoped_rows VALUES (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{CustomerX}', 'no-scope-write')"));
    }

    [Fact] // scenario 8
    public async Task System_scope_is_explicit_per_tenant_and_cross_tenant_needs_a_dedicated_role()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppConnectionString);

        await SpikeSession.ApplyAsync(c, new SpikeScope("SYSTEM", null), transactionLocal: false);
        Assert.Empty(await SpikeSession.PayloadsAsync(c)); // a job without tenant context sees nothing

        await SpikeSession.ApplyAsync(c, new SpikeScope("SYSTEM", TenantA, TenantWideCompanyAccess: true), transactionLocal: false);
        Assert.Equal(5, (await SpikeSession.PayloadsAsync(c)).Count); // one tenant, all companies (explicit grant)

        await SpikeSession.ApplyAsync(c, new SpikeScope("SYSTEM", TenantB, CompanyB1), transactionLocal: false);
        Assert.Equal(["B1-W-1"], await SpikeSession.PayloadsAsync(c));

        // Cross-tenant maintenance is only possible through a separate BYPASSRLS role/connection.
        await using var maint = await OpenAsync(db.MaintenanceConnectionString);
        Assert.Equal(6, (await SpikeSession.PayloadsAsync(maint)).Count);
    }

    [Fact] // scenario 9
    public async Task Transaction_local_scope_does_not_survive_commit_or_rollback()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppConnectionString);

        await using (var tx = await c.BeginTransactionAsync())
        {
            await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: true, tx);
            Assert.Equal(OwnRowsOfX, await SpikeSession.PayloadsAsync(c, transaction: tx));
            await tx.RollbackAsync();
        }

        Assert.Empty(await SpikeSession.PayloadsAsync(c)); // after rollback: reverted to '' -> fail-closed
        Assert.Equal("", await ScalarAsync(c, "SELECT current_setting('commercial.tenant_id', true)")); // '' not NULL

        await using (var tx = await c.BeginTransactionAsync())
        {
            await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: true, tx);
            await tx.CommitAsync();
        }

        Assert.Empty(await SpikeSession.PayloadsAsync(c)); // after commit too

        // Autocommit statements: a LOCAL setting issued alone is gone before the next statement.
        await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: true);
        Assert.Empty(await SpikeSession.PayloadsAsync(c));
    }

    [Fact] // scenario 10
    public async Task Raw_sql_cannot_bypass_rls()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppConnectionString);
        await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: false);

        Assert.Equal("2", await ScalarAsync(c, "SELECT count(*)::text FROM spike.scoped_rows"));
        Assert.Equal("2", await ScalarAsync(c, "SELECT count(*)::text FROM spike.scoped_rows WHERE true OR tenant_id IS NOT NULL"));
        Assert.Equal("0", await ScalarAsync(c, $"SELECT count(*)::text FROM spike.scoped_rows WHERE tenant_id = '{TenantB}'"));

        // The runtime role cannot switch RLS off or impersonate.
        var disable = await Assert.ThrowsAsync<PostgresException>(() => SpikeSession.ExecuteAsync(c, "ALTER TABLE spike.scoped_rows DISABLE ROW LEVEL SECURITY"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, disable.SqlState); // must be owner
        await Assert.ThrowsAsync<PostgresException>(() => SpikeSession.ExecuteAsync(c, "SET row_security = off; SELECT count(*) FROM spike.scoped_rows"));

        // ...but it CAN rewrite its own session context: RLS trusts whoever sets the GUCs.
        await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerY), transactionLocal: false);
        Assert.Equal("2", await ScalarAsync(c, $"SELECT count(*)::text FROM spike.scoped_rows WHERE customer_account_id = '{CustomerY}'"));
    }

    [Fact] // scenario 12
    public async Task Table_owner_bypasses_rls_unless_forced_and_can_turn_it_off()
    {
        var db = await CreateAsync(containers);
        await using var c = await OpenAsync(db.AppOwnerConnectionString);
        await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: false);

        // ENABLE ROW LEVEL SECURITY alone: the owner ignores the policy entirely.
        Assert.Equal(6, (await SpikeSession.PayloadsAsync(c, "spike.owned_rows")).Count);

        // FORCE makes the policy apply to the owner as well.
        await SpikeSession.ExecuteAsync(c, "ALTER TABLE spike.owned_rows FORCE ROW LEVEL SECURITY");
        Assert.Equal(OwnRowsOfX, await SpikeSession.PayloadsAsync(c, "spike.owned_rows"));

        // ...but an owning runtime role can undo it at any time (bug, injection, compromised credential).
        await SpikeSession.ExecuteAsync(c, "ALTER TABLE spike.owned_rows NO FORCE ROW LEVEL SECURITY");
        Assert.Equal(6, (await SpikeSession.PayloadsAsync(c, "spike.owned_rows")).Count);
        await SpikeSession.ExecuteAsync(c, "ALTER TABLE spike.owned_rows DISABLE ROW LEVEL SECURITY");
        Assert.Equal(6, (await SpikeSession.PayloadsAsync(c, "spike.owned_rows")).Count);
    }

    [Fact] // performance smoke
    public async Task Policy_predicates_are_index_usable()
    {
        var db = await CreateAsync(containers);
        await RlsSpikeDatabase.ExecAsync(db.AdminConnectionString, """
            INSERT INTO spike.scoped_rows (id, tenant_id, company_id, customer_account_id, payload)
            SELECT gen_random_uuid(), t.id, c.id, gen_random_uuid(), 'bulk'
            FROM (SELECT gen_random_uuid() AS id FROM generate_series(1, 400)) t
            CROSS JOIN LATERAL (SELECT gen_random_uuid() AS id FROM generate_series(1, 5)) c
            CROSS JOIN generate_series(1, 25);
            ANALYZE spike.scoped_rows;
            """);

        await using var c = await OpenAsync(db.AppConnectionString);
        await SpikeSession.ApplyAsync(c, SpikeScope.Customer(TenantA, CompanyA1, CustomerX), transactionLocal: false);
        var plan = string.Join("\n", await ReadAsync(c, "EXPLAIN SELECT payload FROM spike.scoped_rows"));

        Assert.Contains("ix_scoped_rows_scope", plan, StringComparison.Ordinal); // 50k rows, policy quals drive an index scan
        Assert.DoesNotContain("Seq Scan", plan, StringComparison.Ordinal);
    }

    private static async Task<string?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = new NpgsqlCommand(sql, c);
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static async Task<List<string>> ReadAsync(NpgsqlConnection c, string sql)
    {
        await using var command = new NpgsqlCommand(sql, c);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
