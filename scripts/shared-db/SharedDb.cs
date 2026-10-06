#:package Npgsql@10.0.0
#:package Microsoft.Extensions.Configuration.UserSecrets@10.0.0
#:package Microsoft.Extensions.Configuration.EnvironmentVariables@10.0.0
#:property ManagePackageVersionsCentrally=false
#:property TreatWarningsAsErrors=false
#:property PublishAot=false

// Shared development database helper (invoked via scripts/shared-db.ps1).
//   inspect                 read-only: login, role privileges, schemas, tables, migration history
//   cleanup <correlationId> delete ONLY Phase 0 diagnostic rows created with that correlation ID
//
// Connection is resolved like the application: COMMERCIAL__POSTGRES__* environment variables + the
// shared user-secrets store "commercial-platform-dev". The password is never printed.

using Microsoft.Extensions.Configuration;
using Npgsql;

var config = new ConfigurationBuilder()
    .AddUserSecrets("commercial-platform-dev")
    .AddEnvironmentVariables()
    .Build();
var section = config.GetSection("Commercial:Postgres");
var builder = new NpgsqlConnectionStringBuilder
{
    Host = section["Host"],
    Port = int.TryParse(section["Port"], out var port) ? port : 5432,
    Database = section["Database"],
    Username = section["Username"],
    Timeout = 10,
    ApplicationName = "commercial-shared-db-helper",
};
if (!string.IsNullOrWhiteSpace(section["Password"]))
{
    builder.Password = section["Password"];
}

Console.WriteLine($"Target: Host={builder.Host};Port={builder.Port};Database={builder.Database};Username={builder.Username};Password={(builder.Password is null ? "<NOT CONFIGURED>" : "<configured, redacted>")}");
if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
{
    Console.Error.WriteLine("Host/Database not set. Run through scripts/shared-db.ps1.");
    return 2;
}

var action = args.Length > 0 ? args[0] : "inspect";
try
{
    await using var connection = new NpgsqlConnection(builder.ConnectionString);
    await connection.OpenAsync();
    Console.WriteLine("Login: OK");

    if (action == "cleanup")
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("cleanup requires a correlation ID.");
            return 2;
        }

        await using var tx = await connection.BeginTransactionAsync();
        var receipts = await Exec(connection, tx,
            "DELETE FROM commercial.diagnostic_ping_receipts WHERE correlation_id = @c", args[1]);
        var outbox = await Exec(connection, tx,
            "DELETE FROM commercial.outbox_messages WHERE correlation_id = @c AND type = 'diagnostics.ping.v1'", args[1]);
        await tx.CommitAsync();
        Console.WriteLine($"Deleted diagnostic receipts: {receipts}, diagnostic outbox rows: {outbox} (correlationId={args[1]})");
        return 0;
    }

    await Print(connection, "Session", """
        SELECT current_user AS current_user, session_user AS session_user, current_database() AS database,
               split_part(version(), ' on ', 1) AS server
        """);
    await Print(connection, "Role privileges (application needs none of SUPERUSER/CREATEROLE)", """
        SELECT rolname, rolsuper, rolcreaterole, rolcreatedb, rolbypassrls
        FROM pg_roles WHERE rolname = current_user
        """);
    await Print(connection, "Database owner", """
        SELECT d.datname, pg_get_userbyid(d.datdba) AS owner, (pg_get_userbyid(d.datdba) = current_user) AS current_user_is_owner
        FROM pg_database d WHERE d.datname = current_database()
        """);
    await Print(connection, "Non-system schemas", """
        SELECT n.nspname AS schema, pg_get_userbyid(n.nspowner) AS owner
        FROM pg_namespace n
        WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
        ORDER BY 1
        """);
    await Print(connection, "Tables / views (non-system)", """
        SELECT n.nspname AS schema, c.relname AS name, c.relkind AS kind,
               pg_get_userbyid(c.relowner) AS owner, c.reltuples::bigint AS est_rows
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r','p','v','m','f')
          AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
        ORDER BY 1, 2
        """);
    await Print(connection, "Extensions", "SELECT extname, extversion FROM pg_extension ORDER BY 1");
    await Print(connection, "EF migration history tables anywhere", """
        SELECT n.nspname AS schema, c.relname AS table
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'r' AND lower(c.relname) IN ('__ef_migrations_history', '__efmigrationshistory')
        """);

    if (await Scalar<bool>(connection, "SELECT to_regclass('commercial.__ef_migrations_history') IS NOT NULL"))
    {
        await Print(connection, "commercial.__ef_migrations_history", "SELECT migration_id, product_version FROM commercial.__ef_migrations_history ORDER BY 1");
    }

    if (await Scalar<bool>(connection, "SELECT to_regclass('commercial.outbox_messages') IS NOT NULL"))
    {
        await Print(connection, "commercial row counts", """
            SELECT 'outbox_messages' AS table, count(*) AS rows FROM commercial.outbox_messages
            UNION ALL SELECT 'diagnostic_ping_receipts', count(*) FROM commercial.diagnostic_ping_receipts
            """);
        await Print(connection, "outbox_messages by type/status", """
            SELECT type, status, count(*) AS rows FROM commercial.outbox_messages GROUP BY 1, 2 ORDER BY 1, 2
            """);
    }

    if (await Scalar<bool>(connection, "SELECT to_regnamespace('hangfire') IS NOT NULL"))
    {
        await Print(connection, "hangfire storage summary", """
            SELECT (SELECT count(*) FROM hangfire.job) AS jobs,
                   (SELECT count(*) FROM hangfire.server) AS servers,
                   (SELECT count(*) FROM hangfire.set WHERE key = 'recurring-jobs') AS recurring_jobs
            """);
    }

    return 0;
}
catch (PostgresException ex)
{
    Console.Error.WriteLine($"PostgreSQL error {ex.SqlState}: {ex.MessageText}");
    return 1;
}
catch (NpgsqlException ex)
{
    Console.Error.WriteLine($"Connection error: {ex.Message}");
    return 1;
}

static async Task<int> Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, string correlationId)
{
    await using var cmd = new NpgsqlCommand(sql, c, tx);
    cmd.Parameters.AddWithValue("c", correlationId);
    return await cmd.ExecuteNonQueryAsync();
}

static async Task<T> Scalar<T>(NpgsqlConnection c, string sql)
{
    await using var cmd = new NpgsqlCommand(sql, c);
    return (T)(await cmd.ExecuteScalarAsync())!;
}

static async Task Print(NpgsqlConnection c, string title, string sql)
{
    Console.WriteLine();
    Console.WriteLine($"## {title}");
    await using var cmd = new NpgsqlCommand(sql, c);
    await using var reader = await cmd.ExecuteReaderAsync();
    var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
    Console.WriteLine(string.Join(" | ", columns));
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
