using Commercial.Infrastructure.BackgroundJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Commercial.Infrastructure.Caching;

public static class RedisRegistration
{
    public const string ConnectionStringName = "Redis";

    public static IServiceCollection AddCommercialRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"Connection string 'ConnectionStrings:{ConnectionStringName}' is not configured.");
            }

            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false; // start even if Redis is temporarily down; readiness reports it
            options.ClientName ??= "commercial-platform";
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: [HealthTags.Ready]);
        return services;
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F1} ms");
        }
#pragma warning disable CA1031 // Health checks report, never throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy("Redis unreachable.", ex);
        }
    }
}
