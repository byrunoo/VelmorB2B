using Commercial.Application.Abstractions.Persistence;
using Commercial.Application.Diagnostics;
using Commercial.Infrastructure.Persistence.Diagnostics;
using Commercial.Infrastructure.Persistence.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Commercial.Infrastructure.Persistence;

/// <summary>Centralized DbContext / connection registration (the only place EF is configured).</summary>
public static class PersistenceRegistration
{
    public const string ConnectionStringName = "Commercial";

    public static IServiceCollection AddCommercialPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // ADR-0001 session context: writer (initializer) + verifier (fingerprint guard), both on every CommercialDbContext.
        services.TryAddSingleton<IDbSessionInitializer, RlsDbSessionInitializer>();
        services.TryAddSingleton<DbSessionRegistry>();
        services.TryAddSingleton<DbSessionInitializerInterceptor>();
        services.TryAddSingleton<DbSessionScopeGuard>();
        services.TryAddSingleton<DbSessionScopeGuardInterceptor>();

        services.TryAddSingleton(_ => new NpgsqlDataSourceBuilder(PostgresConnection.Resolve(configuration))
        {
            Name = "commercial",
        }.Build());

        services.AddDbContext<CommercialDbContext>((sp, options) =>
        {
            ConfigureNpgsql(options, sp.GetRequiredService<NpgsqlDataSource>());
            options.AddInterceptors(
                sp.GetRequiredService<DbSessionInitializerInterceptor>(),
                sp.GetRequiredService<DbSessionScopeGuardInterceptor>());
        });

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IDiagnosticPingReceiptStore, EfDiagnosticPingReceiptStore>();
        services.AddScoped<IDiagnosticPingReadModel, EfDiagnosticPingReadModel>();
        return services;
    }

    internal static void ConfigureNpgsql(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options
            .UseNpgsql(dataSource, ConfigureProvider)
            .UseSnakeCaseNamingConvention();

    internal static void ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, ConfigureProvider)
            .UseSnakeCaseNamingConvention();

    /// <summary>
    /// Migration (owner) context: same model, plus post-migration hardening that runs inside
    /// <c>Migrate()</c> and <c>dotnet ef database update</c> (EF seeding hook).
    /// </summary>
    internal static void ConfigureMigrationContext(DbContextOptionsBuilder options, string migrationConnectionString)
    {
        ConfigureNpgsql(options, migrationConnectionString);
        options
            .UseSeeding((context, _) => context.Database.ExecuteSqlRaw(Hosting.DatabaseProvisioner.HardenMigrationHistorySql))
            .UseAsyncSeeding((context, _, ct) => context.Database.ExecuteSqlRawAsync(Hosting.DatabaseProvisioner.HardenMigrationHistorySql, ct));
    }

    private static void ConfigureProvider(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql
            .MigrationsAssembly(typeof(CommercialDbContext).Assembly.GetName().Name)
            .MigrationsHistoryTable(CommercialDbContext.MigrationsHistoryTable, CommercialDbContext.Schema);
}
