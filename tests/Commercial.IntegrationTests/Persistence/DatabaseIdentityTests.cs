using Commercial.Infrastructure.Hosting;
using Commercial.Infrastructure.Persistence;
using Commercial.IntegrationTests.Hosts;
using Commercial.IntegrationTests.Infrastructure;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Commercial.IntegrationTests.Persistence;

/// <summary>
/// ADR-0001 database identity separation, proven against real PostgreSQL (Testcontainers) with catalog queries and
/// real privilege attempts — not configuration inspection.
/// </summary>
[Trait("ADR-0001", "db-identity")]
public sealed class DatabaseIdentityTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Owner_can_create_and_alter_schema_objects()
    {
        var db = await containers.CreateDatabaseAsync();
        await using var owner = await OpenAsync(db.OwnerConnectionString);

        await ExecAsync(owner, "CREATE TABLE commercial.owner_probe (id int PRIMARY KEY)");
        await ExecAsync(owner, "ALTER TABLE commercial.owner_probe ADD COLUMN note text");
        await ExecAsync(owner, "ALTER TABLE commercial.owner_probe ENABLE ROW LEVEL SECURITY");
        await ExecAsync(owner, "DROP TABLE commercial.owner_probe");
    }

    [Theory]
    [InlineData("CREATE TABLE commercial.runtime_probe (id int)")]
    [InlineData("CREATE TABLE hangfire.runtime_probe (id int)")]
    [InlineData("CREATE TABLE public.runtime_probe (id int)")]
    [InlineData("CREATE SCHEMA runtime_probe")]
    [InlineData("CREATE FUNCTION commercial.runtime_probe() RETURNS int LANGUAGE sql AS 'SELECT 1'")]
    public async Task Runtime_cannot_create_objects(string ddl)
    {
        var db = await containers.CreateDatabaseAsync();
        await using var app = await OpenAsync(db.RuntimeConnectionString);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(app, ddl));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Theory]
    [InlineData("ALTER TABLE commercial.outbox_messages ADD COLUMN injected text")]
    [InlineData("ALTER TABLE commercial.outbox_messages ENABLE ROW LEVEL SECURITY")]
    [InlineData("ALTER TABLE commercial.outbox_messages DISABLE ROW LEVEL SECURITY")]
    [InlineData("CREATE POLICY injected ON commercial.outbox_messages USING (true)")]
    [InlineData("DROP TABLE commercial.diagnostic_ping_receipts")]
    [InlineData("TRUNCATE commercial.outbox_messages")]
    [InlineData("ALTER TABLE hangfire.job ADD COLUMN injected text")]
    public async Task Runtime_cannot_alter_owner_objects_or_rls(string statement)
    {
        var db = await containers.CreateDatabaseAsync();
        await using var app = await OpenAsync(db.RuntimeConnectionString);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(app, statement));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Runtime_DML_on_granted_tables_succeeds()
    {
        var db = await containers.CreateDatabaseAsync();
        await using var app = await OpenAsync(db.RuntimeConnectionString);
        var id = Guid.NewGuid();

        Assert.Equal(1, await ExecAsync(app, $"INSERT INTO commercial.diagnostic_ping_receipts (message_id, correlation_id, recorded_at_utc) VALUES ('{id}', 'dml', now())"));
        Assert.Equal(1, await ExecAsync(app, $"UPDATE commercial.diagnostic_ping_receipts SET note = 'x' WHERE message_id = '{id}'"));
        Assert.Equal("x", await ScalarAsync(app, $"SELECT note FROM commercial.diagnostic_ping_receipts WHERE message_id = '{id}'"));
        Assert.Equal(1, await ExecAsync(app, $"DELETE FROM commercial.diagnostic_ping_receipts WHERE message_id = '{id}'"));
        Assert.Equal(1, await ExecAsync(app, "INSERT INTO hangfire.counter (key, value) VALUES ('identity-probe', 1)"));
    }

    [Fact]
    public async Task Runtime_owns_nothing_and_has_no_elevated_attributes()
    {
        var db = await containers.CreateDatabaseAsync();
        await using var admin = await OpenAsync(db.AdminConnectionString);

        Assert.Equal("0", await ScalarAsync(admin, $"""
            SELECT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE pg_get_userbyid(c.relowner) = '{DatabaseRoles.Runtime}' AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema')
                 + (SELECT count(*) FROM pg_namespace WHERE pg_get_userbyid(nspowner) = '{DatabaseRoles.Runtime}')
                 + (SELECT count(*) FROM pg_proc WHERE pg_get_userbyid(proowner) = '{DatabaseRoles.Runtime}')
                 + (SELECT count(*) FROM pg_database WHERE datname = current_database() AND pg_get_userbyid(datdba) = '{DatabaseRoles.Runtime}')
            """));
        Assert.Equal("f|f|f|f|t", await ScalarAsync(admin, $"""
            SELECT concat_ws('|', rolsuper, rolcreaterole, rolcreatedb, rolbypassrls, rolcanlogin)
            FROM pg_roles WHERE rolname = '{DatabaseRoles.Runtime}'
            """));

        // Everything in commercial / hangfire belongs to the owner.
        Assert.Equal("0", await ScalarAsync(admin, $"""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('commercial', 'hangfire') AND pg_get_userbyid(c.relowner) <> '{DatabaseRoles.Owner}'
            """));
        Assert.Equal(DatabaseRoles.Owner, await ScalarAsync(admin, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()"));
        Assert.Equal("t", await ScalarAsync(admin, $"SELECT rolbypassrls FROM pg_roles WHERE rolname = '{DatabaseRoles.Maintenance}'"));
    }

    [Fact]
    public async Task Migrations_run_as_owner_and_history_is_read_only_for_runtime()
    {
        var db = await containers.CreateDatabaseAsync();
        await using var admin = await OpenAsync(db.AdminConnectionString);
        Assert.Equal(DatabaseRoles.Owner, await ScalarAsync(admin, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'commercial.__ef_migrations_history'::regclass"));

        await using var app = await OpenAsync(db.RuntimeConnectionString);
        Assert.NotEqual("0", await ScalarAsync(app, "SELECT count(*) FROM commercial.__ef_migrations_history"));
        var forged = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecAsync(app, "INSERT INTO commercial.__ef_migrations_history (migration_id, product_version) VALUES ('99999999999999_Forged', 'x')"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forged.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(app, "DELETE FROM commercial.__ef_migrations_history"));
    }

    [Fact]
    public async Task Provisioning_with_the_runtime_identity_fails_clearly()
    {
        var db = await containers.CreateDatabaseAsync(provision: false);

        // Database level: the runtime role cannot create the schema objects of a migration.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            DatabaseProvisioner.ProvisionAsync(db.RuntimeConnectionString, "hangfire", NullLogger.Instance, Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        // Configuration level: the resolver refuses to treat the runtime role as the migration identity.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Commercial"] = db.RuntimeConnectionString,
                ["Commercial:Postgres:Migration:Username"] = DatabaseRoles.Runtime,
            })
            .Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseProvisioner.ProvisionAsync(configuration, NullLogger.Instance, Ct));

        // The owner identity provisions the same database successfully.
        await DatabaseProvisioner.ProvisionAsync(db.OwnerConnectionString, "hangfire", NullLogger.Instance, Ct);
    }

    [Fact]
    public async Task Hangfire_runtime_needs_no_DDL_and_cannot_install_its_schema()
    {
        // Provisioned database: runtime storage with schema preparation disabled works (enqueue persists a job).
        var db = await containers.CreateDatabaseAsync();
        var options = new PostgreSqlStorageOptions { SchemaName = "hangfire", PrepareSchemaIfNecessary = false };
        var storage = new PostgreSqlStorage(new NpgsqlConnectionFactory(db.RuntimeConnectionString, options), options);
        var jobId = new BackgroundJobClient(storage).Enqueue(() => HangfireProbe.Touch("identity"));
        using (var connection = storage.GetConnection())
        {
            Assert.NotNull(connection.GetJobData(jobId));
        }

        // Unprovisioned database: the runtime identity is NOT able to install the Hangfire schema.
        var bare = await containers.CreateDatabaseAsync(provision: false);
        await using var app = await OpenAsync(bare.RuntimeConnectionString);
        var install = Assert.ThrowsAny<Exception>(() => PostgreSqlObjectsInstaller.Install(app, "hangfire"));
        Assert.Contains("permission denied", install.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_and_Worker_hosts_connect_as_the_runtime_identity_only()
    {
        var db = await containers.CreateDatabaseAsync();
        await using var api = new ApiHostFactory(db.RuntimeConnectionString, containers.RedisConnectionString);
        await using var worker = new WorkerHostFactory(db.RuntimeConnectionString, containers.RedisConnectionString);

        foreach (var services in new[] { api.Services, worker.Services })
        {
            Assert.Equal(DatabaseRoles.Runtime, new NpgsqlConnectionStringBuilder(services.GetRequiredService<NpgsqlDataSource>().ConnectionString).Username);
            await using var scope = services.CreateAsyncScope();
            var current = await scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Database
                .SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync(Ct);
            Assert.Equal(DatabaseRoles.Runtime, current);
            Assert.NotEqual(DatabaseRoles.Maintenance, current);
            Assert.NotEqual(DatabaseRoles.Owner, current);
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(Ct);
        return value switch
        {
            bool b => b ? "t" : "f",
            null => null,
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
