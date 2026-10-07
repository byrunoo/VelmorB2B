using Commercial.Infrastructure.Hosting;
using Commercial.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Commercial.IntegrationTests.Persistence;

/// <summary>
/// Dry run of the DBA script <c>scripts/db/shared-dev-role-split.sql</c> on a replica of the shared development
/// database's pre-split state (only <c>commercial_platform_app</c> exists and owns the database, both schemas and all
/// objects, created by the real migrations + Hangfire installer). Uses its own container because the script must
/// create cluster-level roles that the shared fixture already has.
/// </summary>
[Trait("ADR-0001", "role-split-script")]
public sealed class SharedDevRoleSplitScriptTests : IAsyncLifetime
{
    private const string Database = "commercial_platform_dev";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string _appSecret = Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Script_moves_ownership_to_owner_keeps_runtime_working_and_refuses_a_second_run()
    {
        // Replica of the current shared state.
        await ExecAsync(Superuser("postgres"), $"CREATE ROLE {DatabaseRoles.Runtime} LOGIN PASSWORD '{_appSecret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS");
        await ExecAsync(Superuser("postgres"), $"CREATE DATABASE {Database} OWNER {DatabaseRoles.Runtime}");
        await DatabaseProvisioner.ProvisionAsync(As(DatabaseRoles.Runtime, _appSecret), "hangfire", NullLogger.Instance, CancellationToken.None);
        await ExecAsync(As(DatabaseRoles.Runtime, _appSecret), "INSERT INTO commercial.diagnostic_ping_receipts (message_id, correlation_id, recorded_at_utc) VALUES (gen_random_uuid(), 'pre-split', now())");

        // Execute the reviewed script exactly as the tooling does (psql meta-commands stripped).
        await ExecAsync(Superuser(Database), ScriptWithoutPsqlMetaCommands());

        await using var admin = new NpgsqlConnection(Superuser(Database));
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DatabaseRoles.Owner, await ScalarAsync(admin, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()"));
        Assert.Equal("0", await ScalarAsync(admin, $"""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('commercial', 'hangfire') AND pg_get_userbyid(c.relowner) <> '{DatabaseRoles.Owner}'
            """));
        Assert.Equal("0", await ScalarAsync(admin, $"SELECT count(*) FROM pg_class WHERE pg_get_userbyid(relowner) = '{DatabaseRoles.Runtime}'"));
        Assert.Equal("1", await ScalarAsync(admin, "SELECT count(*) FROM commercial.diagnostic_ping_receipts")); // data untouched

        // Runtime keeps working (DML) but can no longer create or alter anything.
        await ExecAsync(As(DatabaseRoles.Runtime, _appSecret), "INSERT INTO commercial.outbox_messages (id, occurred_at_utc, type, payload, correlation_id, attempts, next_attempt_at_utc, status) VALUES (gen_random_uuid(), now(), 't', '{}', 'c', 0, now(), 'Pending')");
        var create = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(As(DatabaseRoles.Runtime, _appSecret), "CREATE TABLE commercial.x (id int)"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, create.SqlState);
        var alter = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(As(DatabaseRoles.Runtime, _appSecret), "ALTER TABLE hangfire.job ADD COLUMN x int"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, alter.SqlState);

        // Owner has no password yet (cannot log in); set it via a client-side SCRAM verifier, then provisioning is "up to date".
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(As(DatabaseRoles.Owner, "anything"), "SELECT 1"));
        var ownerSecret = Guid.NewGuid().ToString("N");
        await ExecAsync(Superuser(Database), ScramSha256.AlterRolePasswordSql(DatabaseRoles.Owner, ScramSha256.CreateVerifier(ownerSecret)));
        await DatabaseProvisioner.ProvisionAsync(As(DatabaseRoles.Owner, ownerSecret), "hangfire", NullLogger.Instance, CancellationToken.None);

        // A second run is refused by the preconditions and changes nothing.
        var rerun = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(Superuser(Database), ScriptWithoutPsqlMetaCommands()));
        Assert.Contains("already exist", rerun.MessageText, StringComparison.Ordinal);
    }

    private string Superuser(string database) => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database, Pooling = false }.ConnectionString;

    private string As(string role, string secret)
    {
        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = Database, Username = role, Pooling = false };
        builder.Password = secret;
        return builder.ConnectionString;
    }

    private static string ScriptWithoutPsqlMetaCommands()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commercial.slnx")))
        {
            dir = dir.Parent;
        }

        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, "scripts", "db", "shared-dev-role-split.sql"));
        return string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith('\\')));
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
