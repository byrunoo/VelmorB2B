using Commercial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Commercial.Infrastructure.Hosting;

/// <summary>
/// Applies EF migrations at startup ONLY when <c>Database:ApplyMigrationsOnStartup=true</c>
/// (local development). Production applies migrations explicitly (see docs/architecture/phase-0.md).
/// </summary>
internal sealed class DatabaseMigrationHostedService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CommercialDbContext>();
        logger.LogInformation("Applying EF Core migrations (Database:ApplyMigrationsOnStartup=true)");
        await db.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
