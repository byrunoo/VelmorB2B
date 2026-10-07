using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Security;
using Commercial.Infrastructure.BackgroundJobs;
using Commercial.Infrastructure.Caching;
using Commercial.Infrastructure.Hosting;
using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.Infrastructure.Persistence;
using Commercial.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Commercial.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Composition entry point used by API and Worker hosts.</summary>
    public static IServiceCollection AddCommercialInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthTags.Live])
            .AddDbContextCheck<CommercialDbContext>("postgresql", tags: [HealthTags.Ready]);

        services.AddCommercialPersistence(configuration);
        services.AddCommercialRedis(configuration);
        services.AddCommercialHangfireStorage(configuration);

        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddSingleton<IOutboxDispatcher, OutboxDispatcher>();

        // Fail-closed until the identity tables exist: resolves no principal, grants nothing.
        services.TryAddSingleton<IIdentityDirectory, UnprovisionedIdentityDirectory>();

        services.AddHostedService<DatabaseMigrationHostedService>();
        return services;
    }
}
