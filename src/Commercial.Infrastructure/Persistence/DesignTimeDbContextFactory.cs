using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Commercial.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. Resolves the connection exactly like the hosts
/// (<see cref="PostgresConnection"/>): defaults to the local docker-compose database (trust auth,
/// no password), overridable by environment variables (<c>ConnectionStrings__Commercial</c>,
/// <c>COMMERCIAL__POSTGRES__HOST|PORT|DATABASE|USERNAME|PASSWORD</c>) and by the shared
/// development user-secrets store (<see cref="DevelopmentSecrets.UserSecretsId"/>).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CommercialDbContext>
{
    public CommercialDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PersistenceRegistration.ConnectionStringName}"] =
                    "Host=localhost;Port=5432;Database=commercial;Username=commercial",
            })
            .AddUserSecrets(DevelopmentSecrets.UserSecretsId)
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<CommercialDbContext>();
        PersistenceRegistration.ConfigureNpgsql(options, PostgresConnection.Resolve(configuration));
        return new CommercialDbContext(options.Options);
    }
}

public static class DevelopmentSecrets
{
    /// <summary>
    /// One user-secrets store shared by API, Worker and design-time tooling, so a developer sets the
    /// shared-DB password once: <c>dotnet user-secrets set "Commercial:Postgres:Password" ... --id commercial-platform-dev</c>.
    /// </summary>
    public const string UserSecretsId = "commercial-platform-dev";
}
