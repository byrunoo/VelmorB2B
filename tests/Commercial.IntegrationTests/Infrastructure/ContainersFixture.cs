using Commercial.Application;
using Commercial.Infrastructure;
using Commercial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Commercial.IntegrationTests.Infrastructure.ContainersFixture))]

namespace Commercial.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL + one Redis container per test run (Testcontainers — no manually installed services).
/// Each test class gets its own freshly migrated database, so outbox tests never see each other's rows.
/// </summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>Creates an empty database, applies EF migrations through the real DbContext and returns its connection string.</summary>
    public async Task<string> CreateMigratedDatabaseAsync(string? extraConnectionOptions = null)
    {
        var name = "cp_" + Guid.NewGuid().ToString("N")[..16];
        await using (var admin = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name };
        var connectionString = builder.ConnectionString + (extraConnectionOptions is null ? string.Empty : ";" + extraConnectionOptions);

        await using var provider = BuildServices(connectionString);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Database.MigrateAsync();
        return connectionString;
    }

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
}
