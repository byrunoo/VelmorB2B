using Commercial.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Commercial.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. Always resolves the MIGRATION identity (<see cref="DatabaseRoles.Owner"/>) — design-time
/// tooling never connects as the runtime role. Defaults target the local docker-compose database (trust auth on
/// 127.0.0.1, no password in source); overrides come from the ops user-secrets store
/// (<see cref="DevelopmentSecrets.OpsUserSecretsId"/>) and environment variables
/// (<c>ConnectionStrings__Commercial</c>, <c>COMMERCIAL__POSTGRES__HOST|PORT|DATABASE</c>,
/// <c>COMMERCIAL__POSTGRES__MIGRATION__USERNAME|PASSWORD</c>).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CommercialDbContext>
{
    public CommercialDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PersistenceRegistration.ConnectionStringName}"] = "Host=localhost;Port=5432;Database=commercial",
                [$"{PostgresConnection.MigrationSectionName}:Username"] = DatabaseRoles.Owner,
            })
            .AddUserSecrets(DevelopmentSecrets.OpsUserSecretsId)
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<CommercialDbContext>();
        PersistenceRegistration.ConfigureMigrationContext(options, PostgresConnection.Resolve(configuration, DatabaseIdentity.Migration));
        return new CommercialDbContext(options.Options);
    }
}
