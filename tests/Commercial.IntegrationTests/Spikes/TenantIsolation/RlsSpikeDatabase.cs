using Commercial.IntegrationTests.Infrastructure;
using Npgsql;

namespace Commercial.IntegrationTests.Spikes.TenantIsolation;

/// <summary>
/// ADR-0001 SPIKE — NOT production schema. Builds, on a throw-away Testcontainers database, a single
/// test table protected by PostgreSQL row-level security plus the roles needed to observe owner /
/// runtime / maintenance behaviour. Nothing here is a business table or a production migration.
///
/// Session context keys (custom GUC namespace "commercial."):
///   commercial.tenant_id, commercial.company_id, commercial.customer_account_id,
///   commercial.principal_type (WORKFORCE|CUSTOMER|SYSTEM), commercial.company_access (TENANT = explicit tenant-wide grant)
/// Missing/empty values are fail-closed (no rows, writes rejected by WITH CHECK).
/// </summary>
public sealed class RlsSpikeDatabase
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    public static readonly Guid CompanyA1 = Guid.Parse("aaaaaaaa-0000-0000-0001-000000000001");
    public static readonly Guid CompanyA2 = Guid.Parse("aaaaaaaa-0000-0000-0001-000000000002");
    public static readonly Guid CompanyB1 = Guid.Parse("bbbbbbbb-0000-0000-0001-000000000001");
    public static readonly Guid CustomerX = Guid.Parse("aaaaaaaa-0000-0000-0002-00000000000a");
    public static readonly Guid CustomerY = Guid.Parse("aaaaaaaa-0000-0000-0002-00000000000b");
    public static readonly Guid CustomerZ = Guid.Parse("aaaaaaaa-0000-0000-0002-00000000000c");
    public static readonly Guid CustomerW = Guid.Parse("bbbbbbbb-0000-0000-0002-00000000000d");

    /// <summary>The RLS predicate used for USING and WITH CHECK (single definition).</summary>
    public const string IsolationPredicate = """
        tenant_id = spike.scope_uuid('commercial.tenant_id')
        AND spike.scope_text('commercial.principal_type') IN ('WORKFORCE', 'CUSTOMER', 'SYSTEM')
        AND (
              company_id = spike.scope_uuid('commercial.company_id')
              OR (spike.scope_text('commercial.company_access') = 'TENANT'
                  AND spike.scope_text('commercial.principal_type') IN ('WORKFORCE', 'SYSTEM'))
            )
        AND (
              spike.scope_text('commercial.principal_type') <> 'CUSTOMER'
              OR customer_account_id = spike.scope_uuid('commercial.customer_account_id')
            )
        """;

    private RlsSpikeDatabase(string adminConnectionString)
    {
        AdminConnectionString = adminConnectionString;
    }

    /// <summary>Testcontainers superuser on the spike database (bypasses RLS; used only for setup/seeding).</summary>
    public string AdminConnectionString { get; }

    /// <summary>Migration/schema owner (NOT used at runtime).</summary>
    public string OwnerConnectionString { get; private set; } = "";

    /// <summary>Runtime application role: not owner, NOBYPASSRLS, DML grants only.</summary>
    public string AppConnectionString { get; private set; } = "";

    /// <summary>Runtime role that ALSO owns its table (today's shared-dev situation).</summary>
    public string AppOwnerConnectionString { get; private set; } = "";

    /// <summary>Explicit maintenance role with BYPASSRLS (cross-tenant support/ops only).</summary>
    public string MaintenanceConnectionString { get; private set; } = "";

    public string AppRole { get; private set; } = "";

    public static async Task<RlsSpikeDatabase> CreateAsync(ContainersFixture containers, string? extraOptions = null)
    {
        var admin = await containers.CreateMigratedDatabaseAsync(extraOptions);
        var db = new RlsSpikeDatabase(admin);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        string owner = $"spike_owner_{suffix}", app = $"spike_app_{suffix}", appOwner = $"spike_appowner_{suffix}", maint = $"spike_maint_{suffix}";
        var secret = new Dictionary<string, string>
        {
            [owner] = Guid.NewGuid().ToString("N"),
            [app] = Guid.NewGuid().ToString("N"),
            [appOwner] = Guid.NewGuid().ToString("N"),
            [maint] = Guid.NewGuid().ToString("N"),
        };

        await ExecAsync(admin, $"""
            CREATE ROLE {owner} LOGIN PASSWORD '{secret[owner]}' NOSUPERUSER NOCREATEROLE NOCREATEDB NOBYPASSRLS;
            CREATE ROLE {app} LOGIN PASSWORD '{secret[app]}' NOSUPERUSER NOCREATEROLE NOCREATEDB NOBYPASSRLS;
            CREATE ROLE {appOwner} LOGIN PASSWORD '{secret[appOwner]}' NOSUPERUSER NOCREATEROLE NOCREATEDB NOBYPASSRLS;
            CREATE ROLE {maint} LOGIN PASSWORD '{secret[maint]}' NOSUPERUSER NOCREATEROLE NOCREATEDB BYPASSRLS;

            CREATE SCHEMA spike AUTHORIZATION {owner};
            GRANT USAGE ON SCHEMA spike TO {app}, {appOwner}, {maint};

            SET ROLE {owner};

            -- Empty string = unset (a transaction-local custom GUC reverts to '' — not NULL — after the
            -- transaction ends), so both NULL and '' are treated as "no scope" (fail-closed).
            CREATE FUNCTION spike.scope_text(key text) RETURNS text
                LANGUAGE sql STABLE PARALLEL SAFE AS $$ SELECT nullif(current_setting(key, true), '') $$;
            CREATE FUNCTION spike.scope_uuid(key text) RETURNS uuid
                LANGUAGE sql STABLE PARALLEL SAFE AS $$ SELECT nullif(current_setting(key, true), '')::uuid $$;

            CREATE TABLE spike.scoped_rows (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NOT NULL,
                customer_account_id uuid NULL,
                payload text NOT NULL
            );
            CREATE INDEX ix_scoped_rows_scope ON spike.scoped_rows (tenant_id, company_id, customer_account_id);
            ALTER TABLE spike.scoped_rows ENABLE ROW LEVEL SECURITY;
            CREATE POLICY scoped_rows_isolation ON spike.scoped_rows
                USING ({IsolationPredicate})
                WITH CHECK ({IsolationPredicate});
            GRANT SELECT, INSERT, UPDATE, DELETE ON spike.scoped_rows TO {app}, {maint};

            RESET ROLE;

            -- Variant: the runtime role owns the table (mirrors a runtime role that is also the owner).
            CREATE TABLE spike.owned_rows (LIKE spike.scoped_rows INCLUDING ALL);
            ALTER TABLE spike.owned_rows OWNER TO {appOwner};
            ALTER TABLE spike.owned_rows ENABLE ROW LEVEL SECURITY;
            CREATE POLICY owned_rows_isolation ON spike.owned_rows
                USING ({IsolationPredicate})
                WITH CHECK ({IsolationPredicate});
            """);

        await SeedAsync(admin, "spike.scoped_rows");
        await SeedAsync(admin, "spike.owned_rows");

        db.AppRole = app;
        db.OwnerConnectionString = WithRole(admin, owner, secret[owner]);
        db.AppConnectionString = WithRole(admin, app, secret[app]);
        db.AppOwnerConnectionString = WithRole(admin, appOwner, secret[appOwner]);
        db.MaintenanceConnectionString = WithRole(admin, maint, secret[maint]);
        return db;
    }

    /// <summary>6 rows: A/A1/X ×2, A/A1/Y ×2, A/A2/Z ×1, B/B1/W ×1.</summary>
    private static Task SeedAsync(string admin, string table) => ExecAsync(admin, $"""
        INSERT INTO {table} (id, tenant_id, company_id, customer_account_id, payload) VALUES
          (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{CustomerX}', 'A1-X-1'),
          (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{CustomerX}', 'A1-X-2'),
          (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{CustomerY}', 'A1-Y-1'),
          (gen_random_uuid(), '{TenantA}', '{CompanyA1}', '{CustomerY}', 'A1-Y-2'),
          (gen_random_uuid(), '{TenantA}', '{CompanyA2}', '{CustomerZ}', 'A2-Z-1'),
          (gen_random_uuid(), '{TenantB}', '{CompanyB1}', '{CustomerW}', 'B1-W-1');
        """);

    private static string WithRole(string admin, string role, string secretValue)
    {
        var builder = new NpgsqlConnectionStringBuilder(admin) { Username = role };
        builder.Password = secretValue;
        return builder.ConnectionString;
    }

    public static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Scope as written into the PostgreSQL session (mirrors ExecutionScope + an explicit tenant-wide grant).</summary>
public sealed record SpikeScope(string? PrincipalType, Guid? TenantId, Guid? CompanyId = null, Guid? CustomerAccountId = null, bool TenantWideCompanyAccess = false)
{
    public static SpikeScope Customer(Guid tenant, Guid company, Guid customer) => new("CUSTOMER", tenant, company, customer);

    public static SpikeScope Workforce(Guid tenant, Guid company) => new("WORKFORCE", tenant, company);

    public static SpikeScope WorkforceTenantWide(Guid tenant) => new("WORKFORCE", tenant, TenantWideCompanyAccess: true);

    public static SpikeScope None { get; } = new(null, null);
}

public static class SpikeSession
{
    /// <summary>
    /// Writes EVERY key on every call (absent values become ''), so a reused connection can never keep
    /// a previous scope. <paramref name="transactionLocal"/> = SET LOCAL semantics (cleared at COMMIT/ROLLBACK).
    /// </summary>
    public const string ApplySql = """
        SELECT set_config('commercial.tenant_id', @tenant, @local),
               set_config('commercial.company_id', @company, @local),
               set_config('commercial.customer_account_id', @customer, @local),
               set_config('commercial.principal_type', @principal, @local),
               set_config('commercial.company_access', @access, @local)
        """;

    public static async Task ApplyAsync(System.Data.Common.DbConnection connection, SpikeScope scope, bool transactionLocal, System.Data.Common.DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ApplySql;
        Add(command, "tenant", scope.TenantId?.ToString() ?? "");
        Add(command, "company", scope.CompanyId?.ToString() ?? "");
        Add(command, "customer", scope.CustomerAccountId?.ToString() ?? "");
        Add(command, "principal", scope.PrincipalType ?? "");
        Add(command, "access", scope.TenantWideCompanyAccess ? "TENANT" : "");
        Add(command, "local", transactionLocal);
        await command.ExecuteNonQueryAsync();
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    public static async Task<List<string>> PayloadsAsync(System.Data.Common.DbConnection connection, string table = "spike.scoped_rows", System.Data.Common.DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT payload FROM {table} ORDER BY payload";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public static async Task<int> ExecuteAsync(System.Data.Common.DbConnection connection, string sql, System.Data.Common.DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }
}
