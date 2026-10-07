using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Commercial.Infrastructure.Hosting;

/// <summary>
/// Provisions the database at startup ONLY when <c>Database:ApplyMigrationsOnStartup=true</c> (local docker
/// development). Uses the MIGRATION identity via <see cref="DatabaseProvisioner"/> — never the runtime DbContext —
/// and fails clearly when no migration identity is configured. Shared/production environments provision
/// explicitly (<c>--provision-database</c>) and keep this switch off.
/// </summary>
internal sealed class DatabaseMigrationHostedService(
    IConfiguration configuration,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
        {
            return Task.CompletedTask;
        }

        logger.LogInformation("Database:ApplyMigrationsOnStartup=true — provisioning with the migration identity");
        return DatabaseProvisioner.ProvisionAsync(configuration, logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
