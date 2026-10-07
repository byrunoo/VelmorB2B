using Commercial.Infrastructure.BackgroundJobs;
using Commercial.Infrastructure.Persistence;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commercial.Infrastructure.Hosting;

/// <summary>
/// Explicit, owner-identity database provisioning (ADR-0001): applies EF Core migrations, hardens migration
/// metadata and installs/upgrades the Hangfire schema. Always runs as the MIGRATION identity
/// (<see cref="DatabaseRoles.Owner"/>), never as the runtime role. Used by:
///   * <c>dotnet run --project src/Commercial.Worker -- --provision-database</c> (shared / production pipelines);
///   * Development startup when <c>Database:ApplyMigrationsOnStartup=true</c> (local docker only);
///   * Testcontainers fixtures.
/// </summary>
public static class DatabaseProvisioner
{
    /// <summary>
    /// The runtime role must not be able to forge or delete migration history (it would hide pending schema changes).
    /// Default privileges grant it DML on owner-created tables, so revoke it for the history table after migrating.
    /// </summary>
    internal const string HardenMigrationHistorySql = $"""
        DO $$
        BEGIN
            IF to_regclass('{CommercialDbContext.Schema}.{CommercialDbContext.MigrationsHistoryTable}') IS NOT NULL
               AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{DatabaseRoles.Runtime}') THEN
                REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON {CommercialDbContext.Schema}.{CommercialDbContext.MigrationsHistoryTable} FROM {DatabaseRoles.Runtime};
            END IF;
        END $$;
        """;

    public static Task ProvisionAsync(IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = PostgresConnection.Resolve(configuration, DatabaseIdentity.Migration);
        var hangfire = configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>() ?? new HangfireOptions();
        return ProvisionAsync(connectionString, hangfire.SchemaName, logger, cancellationToken);
    }

    public static async Task ProvisionAsync(string migrationConnectionString, string hangfireSchema, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // One-shot operation: never keep idle owner connections pooled in the process.
        migrationConnectionString = new NpgsqlConnectionStringBuilder(migrationConnectionString) { Pooling = false }.ConnectionString;
        logger.LogInformation("Provisioning database as migration identity: {Target}", PostgresConnection.Describe(migrationConnectionString));

        var options = new DbContextOptionsBuilder<CommercialDbContext>();
        PersistenceRegistration.ConfigureMigrationContext(options, migrationConnectionString);
        await using (var db = new CommercialDbContext(options.Options))
        {
            await db.Database.MigrateAsync(cancellationToken); // runs migrations + seeding hook (history hardening)
        }

        await using var connection = new NpgsqlConnection(migrationConnectionString);
        await connection.OpenAsync(cancellationToken);
        PostgreSqlObjectsInstaller.Install(connection, hangfireSchema);
        logger.LogInformation("Database provisioning complete (EF migrations + Hangfire schema '{Schema}')", hangfireSchema);
    }
}
