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
        services.TryAddSingleton<IDbSessionInitializer, NoOpDbSessionInitializer>();
        services.TryAddSingleton<DbSessionInitializerInterceptor>();

        services.TryAddSingleton(_ => new NpgsqlDataSourceBuilder(PostgresConnection.Resolve(configuration))
        {
            Name = "commercial",
        }.Build());

        services.AddDbContext<CommercialDbContext>((sp, options) =>
        {
            ConfigureNpgsql(options, sp.GetRequiredService<NpgsqlDataSource>());
            options.AddInterceptors(sp.GetRequiredService<DbSessionInitializerInterceptor>());
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

    private static void ConfigureProvider(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql
            .MigrationsAssembly(typeof(CommercialDbContext).Assembly.GetName().Name)
            .MigrationsHistoryTable(CommercialDbContext.MigrationsHistoryTable, CommercialDbContext.Schema);
}
