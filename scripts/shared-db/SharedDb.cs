#:project ../../src/Commercial.Infrastructure/Commercial.Infrastructure.csproj
#:property ManagePackageVersionsCentrally=false
#:property TreatWarningsAsErrors=false
#:property PublishAot=false

// Shared development database helper (invoked via scripts/shared-db.ps1). Identities (ADR-0001):
//   runtime    commercial_platform_app    password: dev store  "Commercial:Postgres:Runtime:Password"
//   migration  commercial_platform_owner  password: ops store  "Commercial:Postgres:Migration:Password"
//   dba        superuser (DBA)            ops store "Commercial:Postgres:Dba:Username|Password"
// Actions:
//   inspect [--as runtime|migration|dba]   read-only state + ownership/privilege report
//   role-split                              run scripts/db/shared-dev-role-split.sql as DBA (preconditions inside)
//   set-role-passwords                      set owner/maintenance passwords from the ops store as SCRAM verifiers
//   cleanup <correlationId>                 delete ONLY diagnostic rows with that correlation ID (runtime)
// Passwords are never printed; plain-text role passwords are never sent to the server.

using Commercial.Infrastructure.Hosting;
using Commercial.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

var config = new ConfigurationBuilder()
    .AddUserSecrets(DevelopmentSecrets.UserSecretsId)
    .AddUserSecrets(DevelopmentSecrets.OpsUserSecretsId)
    .AddEnvironmentVariables()
    .Build();

var action = args.Length > 0 ? args[0] : "inspect";
var asIndex = Array.IndexOf(args, "--as");
var identity = asIndex >= 0 && asIndex + 1 < args.Length ? args[asIndex + 1] : action switch
{
    "role-split" or "set-role-passwords" => "dba",
    _ => "runtime",
};

string connectionString;
try
{
    connectionString = identity switch
    {
        "runtime" => PostgresConnection.Resolve(config, DatabaseIdentity.Runtime),
        "migration" => PostgresConnection.Resolve(config, DatabaseIdentity.Migration),
        "dba" => Dba(config),
        _ => throw new InvalidOperationException($"Unknown identity '{identity}'."),
    };
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var target = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 10, ApplicationName = "commercial-shared-db-helper", Pooling = false };
var description = PostgresConnection.Describe(target.ConnectionString);
if (identity == "dba")
{
    // DBA usernames are operator-supplied; never echo them (a mis-entered value could be a secret).
    description = System.Text.RegularExpressions.Regex.Replace(description, "Username=[^;]*", "Username=<redacted>");
}

Console.WriteLine($"Identity: {identity} → {description.Replace("<redacted>", "<configured, redacted>", StringComparison.Ordinal).Replace("<none>", "<NOT CONFIGURED>", StringComparison.Ordinal)}");

try
{
    await using var connection = new NpgsqlConnection(target.ConnectionString);
    await connection.OpenAsync();
    Console.WriteLine("Login: OK");

    switch (action)
    {
        case "cleanup":
            return await CleanupAsync(connection, args.Length > 1 ? args[1] : null);
        case "role-split":
            await InspectAsync(connection);
            Console.WriteLine();
            Console.WriteLine("== Executing scripts/db/shared-dev-role-split.sql (single transaction; preconditions inside) ==");
            await ExecAsync(connection, ScriptWithoutPsqlMetaCommands());
            Console.WriteLine("Role split: COMMITTED");
            await SetRolePasswordsAsync(connection, config, onlyIfConfigured: true);
            await InspectAsync(connection);
            return 0;
        case "verify-privileges":
            return await VerifyPrivilegesAsync(connection, identity);
        case "set-role-passwords":
            return await SetRolePasswordsAsync(connection, config, onlyIfConfigured: false);
        default:
            await InspectAsync(connection);
            return 0;
    }
}
catch (PostgresException ex)
{
    Console.Error.WriteLine($"PostgreSQL error {ex.SqlState}: {Redact(ex.MessageText)}");
    return 1;
}
catch (NpgsqlException ex)
{
    Console.Error.WriteLine($"Connection error: {Redact(ex.Message)}");
    return 1;
}

string Redact(string message) =>
    identity == "dba" && !string.IsNullOrEmpty(target.Username) ? message.Replace(target.Username, "<redacted>", StringComparison.Ordinal) : message;

static string Dba(IConfiguration config)
{
    var common = new NpgsqlConnectionStringBuilder(PostgresConnection.Resolve(config, DatabaseIdentity.Runtime));
    var dba = config.GetSection("Commercial:Postgres:Dba");
    var builder = new NpgsqlConnectionStringBuilder { Host = common.Host, Port = common.Port, Database = common.Database };
    builder.Username = string.IsNullOrWhiteSpace(dba["Username"])
        ? throw new InvalidOperationException("DBA identity not configured: set 'Commercial:Postgres:Dba:Username' and ':Password' in the ops store (--id commercial-platform-ops).")
        : dba["Username"];
    builder.Password = dba["Password"];
    return builder.ConnectionString;
}

static async Task<int> SetRolePasswordsAsync(NpgsqlConnection connection, IConfiguration config, bool onlyIfConfigured)
{
    var roles = new (string Role, string Key)[]
    {
        (DatabaseRoles.Owner, "Commercial:Postgres:Migration:Password"),
        (DatabaseRoles.Maintenance, "Commercial:Postgres:Maintenance:Password"),
    };
    var missing = 0;
    foreach (var (role, key) in roles)
    {
        var secret = config[key];
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.WriteLine($"{role}: password not configured in ops store ({key}) — {(onlyIfConfigured ? "skipped (role cannot log in until set)" : "MISSING")}");
            missing++;
            continue;
        }

        await ExecAsync(connection, ScramSha256.AlterRolePasswordSql(role, ScramSha256.CreateVerifier(secret)));
        Console.WriteLine($"{role}: password set (client-side SCRAM verifier)");
    }

    return onlyIfConfigured || missing == 0 ? 0 : 1;
}

static async Task<int> VerifyPrivilegesAsync(NpgsqlConnection c, string identity)
{
    // Every probe runs inside a transaction that is ROLLED BACK: nothing persists on the shared database.
    var failures = 0;
    async Task Probe(string name, string sql, bool expectDenied)
    {
        await using var tx = await c.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"{(expectDenied ? "FAIL" : "PASS")}  {name}: allowed");
            failures += expectDenied ? 1 : 0;
        }
        catch (PostgresException ex)
        {
            var denied = ex.SqlState == PostgresErrorCodes.InsufficientPrivilege;
            Console.WriteLine($"{(expectDenied && denied ? "PASS" : "FAIL")}  {name}: {ex.SqlState} {(denied ? "permission denied" : ex.MessageText)}");
            failures += expectDenied && denied ? 0 : 1;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    Console.WriteLine();
    Console.WriteLine($"## Privilege probes as {identity} (all rolled back)");
    if (identity == "runtime")
    {
        await Probe("CREATE TABLE commercial", "CREATE TABLE commercial.privilege_probe (id int)", expectDenied: true);
        await Probe("CREATE TABLE hangfire", "CREATE TABLE hangfire.privilege_probe (id int)", expectDenied: true);
        await Probe("CREATE TABLE public", "CREATE TABLE public.privilege_probe (id int)", expectDenied: true);
        await Probe("CREATE SCHEMA", "CREATE SCHEMA privilege_probe", expectDenied: true);
        await Probe("ALTER owner table", "ALTER TABLE commercial.outbox_messages ADD COLUMN privilege_probe int", expectDenied: true);
        await Probe("ALTER hangfire table", "ALTER TABLE hangfire.job ADD COLUMN privilege_probe int", expectDenied: true);
        await Probe("ENABLE RLS", "ALTER TABLE commercial.outbox_messages ENABLE ROW LEVEL SECURITY", expectDenied: true);
        await Probe("DROP table", "DROP TABLE commercial.diagnostic_ping_receipts", expectDenied: true);
        await Probe("TRUNCATE", "TRUNCATE commercial.outbox_messages", expectDenied: true);
        await Probe("forge migration history", "INSERT INTO commercial.__ef_migrations_history (migration_id, product_version) VALUES ('99999999999999_Probe', 'x')", expectDenied: true);
        await Probe("DML commercial (insert+update+delete)", "INSERT INTO commercial.diagnostic_ping_receipts (message_id, correlation_id, recorded_at_utc) VALUES ('00000000-0000-0000-0000-00000000beef', 'privilege-probe', now()); UPDATE commercial.diagnostic_ping_receipts SET note = 'p' WHERE message_id = '00000000-0000-0000-0000-00000000beef'; DELETE FROM commercial.diagnostic_ping_receipts WHERE message_id = '00000000-0000-0000-0000-00000000beef'", expectDenied: false);
        await Probe("DML hangfire", "INSERT INTO hangfire.counter (key, value) VALUES ('privilege-probe', 1)", expectDenied: false);
        await Probe("read migration history", "SELECT count(*) FROM commercial.__ef_migrations_history", expectDenied: false);
    }
    else if (identity == "migration")
    {
        await Probe("owner CREATE+ALTER+DROP TABLE", "CREATE TABLE commercial.privilege_probe (id int); ALTER TABLE commercial.privilege_probe ADD COLUMN note text; ALTER TABLE commercial.privilege_probe ENABLE ROW LEVEL SECURITY; DROP TABLE commercial.privilege_probe", expectDenied: false);
        await Probe("owner DDL in hangfire", "CREATE TABLE hangfire.privilege_probe (id int)", expectDenied: false);
    }

    Console.WriteLine(failures == 0 ? "ALL PROBES PASSED" : $"{failures} PROBE(S) FAILED");
    return failures == 0 ? 0 : 1;
}

static async Task<int> CleanupAsync(NpgsqlConnection connection, string? correlationId)
{
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        Console.Error.WriteLine("cleanup requires a correlation ID.");
        return 2;
    }

    await using var tx = await connection.BeginTransactionAsync();
    var receipts = await ExecAsync(connection, "DELETE FROM commercial.diagnostic_ping_receipts WHERE correlation_id = @c", tx, correlationId);
    var outbox = await ExecAsync(connection, "DELETE FROM commercial.outbox_messages WHERE correlation_id = @c AND type = 'diagnostics.ping.v1'", tx, correlationId);
    await tx.CommitAsync();
    Console.WriteLine($"Deleted diagnostic receipts: {receipts}, diagnostic outbox rows: {outbox} (correlationId={correlationId})");
    return 0;
}

static async Task InspectAsync(NpgsqlConnection c)
{
    await Print(c, "Session", "SELECT current_user, session_user, current_database() AS database, split_part(version(), ' on ', 1) AS server");
    await Print(c, "Commercial roles", """
        SELECT rolname, rolcanlogin AS login, rolsuper AS super, rolcreaterole AS createrole, rolcreatedb AS createdb,
               rolbypassrls AS bypassrls
        FROM pg_roles WHERE rolname LIKE 'commercial_platform_%' ORDER BY 1
        """);
    await Print(c, "Database owner", "SELECT datname, pg_get_userbyid(datdba) AS owner FROM pg_database WHERE datname = current_database()");
    await Print(c, "Non-system schemas", """
        SELECT nspname AS schema, pg_get_userbyid(nspowner) AS owner FROM pg_namespace
        WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' ORDER BY 1
        """);
    await Print(c, "Relation ownership by schema/kind", """
        SELECT n.nspname AS schema, c.relkind AS kind, pg_get_userbyid(c.relowner) AS owner, count(*) AS objects
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
        GROUP BY 1, 2, 3 ORDER BY 1, 2, 3
        """);
    await Print(c, "Tables (commercial)", """
        SELECT c.relname AS table, pg_get_userbyid(c.relowner) AS owner
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'commercial' AND c.relkind = 'r' ORDER BY 1
        """);
    await Print(c, "Routines / standalone types in commercial+hangfire", """
        SELECT 'routine' AS kind, n.nspname AS schema, p.proname AS name, pg_get_userbyid(p.proowner) AS owner
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname IN ('commercial', 'hangfire')
        UNION ALL
        SELECT 'type', n.nspname, t.typname, pg_get_userbyid(t.typowner)
        FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
        WHERE n.nspname IN ('commercial', 'hangfire') AND t.typrelid = 0 AND t.typelem = 0
        ORDER BY 1, 2, 3
        """);
    await Print(c, "Default privileges", """
        SELECT pg_get_userbyid(defaclrole) AS for_role, n.nspname AS schema, defaclobjtype AS objtype, defaclacl::text AS acl
        FROM pg_default_acl d LEFT JOIN pg_namespace n ON n.oid = d.defaclnamespace ORDER BY 1, 2, 3
        """);
    await Print(c, "Runtime privilege checks", """
        SELECT has_database_privilege('commercial_platform_app', current_database(), 'CONNECT') AS app_connect,
               has_database_privilege('commercial_platform_app', current_database(), 'CREATE') AS app_db_create,
               has_schema_privilege('commercial_platform_app', 'commercial', 'CREATE') AS app_create_commercial,
               has_schema_privilege('commercial_platform_app', 'hangfire', 'CREATE') AS app_create_hangfire,
               has_schema_privilege('commercial_platform_app', 'public', 'CREATE') AS app_create_public,
               has_table_privilege('commercial_platform_app', 'commercial.outbox_messages', 'INSERT,SELECT,UPDATE,DELETE') AS app_outbox_dml,
               has_table_privilege('commercial_platform_app', 'commercial.__ef_migrations_history', 'INSERT') AS app_history_insert
        """);
    await Print(c, "Policies", "SELECT schemaname, tablename, policyname FROM pg_policies ORDER BY 1, 2, 3");
    await Print(c, "Other databases owned by the runtime role", "SELECT datname FROM pg_database WHERE datname <> current_database() AND pg_get_userbyid(datdba) = 'commercial_platform_app'");
    await Print(c, "Sessions by role (this database)", "SELECT usename, count(*) AS sessions FROM pg_stat_activity WHERE datname = current_database() GROUP BY 1 ORDER BY 1");
    if (await ScalarAsync(c, "SELECT to_regclass('commercial.__ef_migrations_history') IS NOT NULL") is true)
    {
        await Print(c, "EF migration history", "SELECT migration_id, product_version FROM commercial.__ef_migrations_history ORDER BY 1");
        await Print(c, "commercial row counts", """
            SELECT 'outbox_messages' AS table, count(*) AS rows FROM commercial.outbox_messages
            UNION ALL SELECT 'diagnostic_ping_receipts', count(*) FROM commercial.diagnostic_ping_receipts
            """);
    }
}

static string ScriptWithoutPsqlMetaCommands()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commercial.slnx")))
    {
        dir = dir.Parent;
    }

    var lines = File.ReadAllLines(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Run from the repository."), "scripts", "db", "shared-dev-role-split.sql"));
    return string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith('\\')));
}

static async Task<int> ExecAsync(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null, string? correlationId = null)
{
    await using var cmd = new NpgsqlCommand(sql, c, tx);
    if (correlationId is not null)
    {
        cmd.Parameters.AddWithValue("c", correlationId);
    }

    return await cmd.ExecuteNonQueryAsync();
}

static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
{
    await using var cmd = new NpgsqlCommand(sql, c);
    return await cmd.ExecuteScalarAsync();
}

static async Task Print(NpgsqlConnection c, string title, string sql)
{
    Console.WriteLine();
    Console.WriteLine($"## {title}");
    try
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var reader = await cmd.ExecuteReaderAsync();
        Console.WriteLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            Console.WriteLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        if (rows == 0)
        {
            Console.WriteLine("(none)");
        }
    }
    catch (PostgresException ex)
    {
        Console.WriteLine($"(not readable as this identity: {ex.SqlState} {ex.MessageText})");
    }
}
