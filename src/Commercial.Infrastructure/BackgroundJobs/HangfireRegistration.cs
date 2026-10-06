using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Commercial.Infrastructure.BackgroundJobs;

public sealed class HangfireOptions
{
    public const string SectionName = "Hangfire";

    public string SchemaName { get; set; } = "hangfire";

    public int WorkerCount { get; set; } = 4;

    public TimeSpan SchedulePollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public string? ServerName { get; set; }
}

/// <summary>Hangfire persistence on PostgreSQL (schema <c>hangfire</c>, managed by Hangfire itself).</summary>
public static class HangfireRegistration
{
    public static IServiceCollection AddCommercialHangfireStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>() ?? new HangfireOptions();

        // The storage is an explicit DI singleton (registered before AddHangfire, which only TryAdds).
        // Hangfire's default registration resolves the process-wide static JobStorage.Current, which is
        // unsafe when several hosts live in one process (tests) — DI consumers must get THEIR storage.
        services.AddSingleton<JobStorage>(_ =>
        {
            // Same database as the application, separate schema ("hangfire"), same resolver.
            var connectionString = Persistence.PostgresConnection.Resolve(configuration);
            var storageOptions = new PostgreSqlStorageOptions
            {
                SchemaName = options.SchemaName,
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = options.QueuePollInterval,
            };
            return new PostgreSqlStorage(new Hangfire.PostgreSql.Factories.NpgsqlConnectionFactory(connectionString, storageOptions), storageOptions);
        });

        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings());

        services.AddHealthChecks().AddCheck<HangfireStorageHealthCheck>("hangfire-storage", tags: [HealthTags.Ready]);
        return services;
    }

    /// <summary>Runs a Hangfire server in this process (Worker host only).</summary>
    public static IServiceCollection AddCommercialHangfireServer(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>() ?? new HangfireOptions();
        services.AddHangfireServer(o =>
        {
            o.ServerName = options.ServerName ?? $"commercial-worker:{Environment.MachineName}:{Environment.ProcessId}";
            o.WorkerCount = options.WorkerCount;
            o.SchedulePollingInterval = options.SchedulePollingInterval;
            o.Queues = ["default"];
        });
        return services;
    }
}

public static class HealthTags
{
    public const string Live = "live";
    public const string Ready = "ready";
}

internal sealed class HangfireStorageHealthCheck(JobStorage storage) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = storage.GetConnection();
            _ = connection.GetAllItemsFromSet("commercial:health-probe");
            return Task.FromResult(HealthCheckResult.Healthy());
        }
#pragma warning disable CA1031 // Health checks report, never throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Hangfire storage unreachable.", ex));
        }
    }
}
