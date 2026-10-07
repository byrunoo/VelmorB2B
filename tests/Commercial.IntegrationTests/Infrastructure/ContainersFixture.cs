using Commercial.Application;
using Commercial.Infrastructure;
using Commercial.Infrastructure.Hosting;
using Commercial.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Commercial.IntegrationTests.Infrastructure.ContainersFixture))]

namespace Commercial.IntegrationTests.Infrastructure;

/// <summary>Connection strings for one throw-away test database, one per ADR-0001 identity.</summary>
public sealed record TestDatabase(
    string Name,
    string AdminConnectionString,
    string OwnerConnectionString,
    string RuntimeConnectionString,
    string MaintenanceConnectionString);

/// <summary>
/// One PostgreSQL + one Redis container per test run (Testcontainers — no manually installed services).
///
/// The container uses the SAME three-role model as local docker / shared dev / production (ADR-0001):
/// roles come from <c>scripts/db/local/01-roles.sql</c> (once per container, generated throw-away passwords),
/// every test database is prepared by <c>scripts/db/local/02-database.sql</c> and provisioned (EF migrations +
/// Hangfire schema) by the OWNER identity. Tests and test hosts run as the RUNTIME identity by default.
/// </summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=400") // many parallel test databases × identities
        .Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();
    private readonly Dictionary<string, string> _rolePasswords = new()
    {
        [DatabaseRoles.Owner] = Guid.NewGuid().ToString("N"),
        [DatabaseRoles.Runtime] = Guid.NewGuid().ToString("N"),
        [DatabaseRoles.Maintenance] = Guid.NewGuid().ToString("N"),
    };

    public string RedisConnectionString => _redis.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        await using var admin = new NpgsqlConnection(_postgres.GetConnectionString());
        await admin.OpenAsync();
        await ExecuteAsync(admin, await ReadRepositoryScriptAsync("01-roles.sql"));
        foreach (var (role, secret) in _rolePasswords)
        {
            // Test-only, generated per run. Parameters are not allowed in utility statements, hence the literal.
            await ExecuteAsync(admin, $"ALTER ROLE {role} PASSWORD '{secret}'");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>Creates a database with the three-role model, provisioned by the owner identity.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(string? extraConnectionOptions = null, bool provision = true)
    {
        var name = "cp_" + Guid.NewGuid().ToString("N")[..16];
        await using (var admin = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\"");
        }

        string For(string? role)
        {
            var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name };
            if (role is not null)
            {
                builder.Username = role;
                builder.Password = _rolePasswords[role];
            }

            return builder.ConnectionString + (extraConnectionOptions is null ? string.Empty : ";" + extraConnectionOptions);
        }

        await using (var adminDb = new NpgsqlConnection(For(null)))
        {
            await adminDb.OpenAsync();
            await ExecuteAsync(adminDb, await ReadRepositoryScriptAsync("02-database.sql"));
        }

        NpgsqlConnection.ClearPool(new NpgsqlConnection(For(null)));

        if (provision)
        {
            await DatabaseProvisioner.ProvisionAsync(For(DatabaseRoles.Owner), "hangfire", NullLogger.Instance, CancellationToken.None);
        }

        return new TestDatabase(name, For(null), For(DatabaseRoles.Owner), For(DatabaseRoles.Runtime), For(DatabaseRoles.Maintenance));
    }

    /// <summary>Creates a provisioned database and returns the RUNTIME (app) connection string.</summary>
    public async Task<string> CreateMigratedDatabaseAsync(string? extraConnectionOptions = null) =>
        (await CreateDatabaseAsync(extraConnectionOptions)).RuntimeConnectionString;

    /// <summary>Application + Infrastructure composition as used by the hosts, pointed at the test containers.</summary>
    public ServiceProvider BuildServices(
        string connectionString,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null,
        IDictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Commercial"] = connectionString,
            ["ConnectionStrings:Redis"] = RedisConnectionString,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        before?.Invoke(services);
        services.AddCommercialApplication();
        services.AddCommercialInfrastructure(configuration);
        after?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The fixture runs the very same bootstrap SQL as local docker-compose.</summary>
    private static Task<string> ReadRepositoryScriptAsync(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commercial.slnx")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), "scripts", "db", "local", fileName);
        return File.ReadAllTextAsync(path);
    }
}
