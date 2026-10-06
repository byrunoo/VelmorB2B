using Commercial.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commercial.IntegrationTests.Hosts;

/// <summary>
/// Guard: even when a developer's shell carries the shared-development-database profile
/// (COMMERCIAL__POSTGRES__* environment variables), test hosts resolve ONLY the Testcontainers database.
/// </summary>
public sealed class SharedDatabaseIsolationTests(ContainersFixture containers)
{
    [Fact]
    public async Task Test_hosts_ignore_shared_database_environment_overrides()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        var expected = new NpgsqlConnectionStringBuilder(cs);

        Environment.SetEnvironmentVariable("COMMERCIAL__POSTGRES__HOST", "shared-db-guard.invalid");
        Environment.SetEnvironmentVariable("COMMERCIAL__POSTGRES__DATABASE", "shared_guard_db");
        try
        {
            await using var api = new ApiHostFactory(cs, containers.RedisConnectionString);
            var resolved = new NpgsqlConnectionStringBuilder(api.Services.GetRequiredService<NpgsqlDataSource>().ConnectionString);

            Assert.Equal(expected.Host, resolved.Host);
            Assert.Equal(expected.Database, resolved.Database);

            using var client = api.CreateClient();
            using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMMERCIAL__POSTGRES__HOST", null);
            Environment.SetEnvironmentVariable("COMMERCIAL__POSTGRES__DATABASE", null);
        }
    }
}
