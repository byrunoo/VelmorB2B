using Commercial.Api;
using Commercial.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.IntegrationTests.Hosts;

internal static class TestHostIsolation
{
    /// <summary>
    /// Tests use ONLY Testcontainers. Blank out every <c>Commercial:Postgres:*</c> override so a developer's
    /// shared-database profile (environment variables / user-secrets) can never redirect a test host.
    /// </summary>
    public static void IsolateFromSharedDatabase(this IWebHostBuilder builder)
    {
        foreach (var key in new[] { "Host", "Port", "Database", "Username", "Password", "Runtime:Username", "Runtime:Password", "Migration:Username", "Migration:Password" })
        {
            builder.UseSetting($"Commercial:Postgres:{key}", string.Empty);
        }
    }
}

/// <summary>The real API host (Program.cs) wired to the Testcontainers PostgreSQL/Redis.</summary>
public sealed class ApiHostFactory(string connectionString, string redis, Action<IServiceCollection>? configure = null)
    : WebApplicationFactory<ApiAssemblyMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Commercial", connectionString);
        builder.UseSetting("ConnectionStrings:Redis", redis);
        builder.IsolateFromSharedDatabase();
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.ConfigureTestServices(services => configure?.Invoke(services));
    }
}

/// <summary>The real Worker host (Hangfire server + recurring outbox dispatch).</summary>
public sealed class WorkerHostFactory(string connectionString, string redis)
    : WebApplicationFactory<WorkerAssemblyMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Commercial", connectionString);
        builder.UseSetting("ConnectionStrings:Redis", redis);
        builder.IsolateFromSharedDatabase();
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.UseSetting("Worker:OutboxDispatchCron", "*/1 * * * * *");
        builder.UseSetting("Hangfire:SchedulePollingInterval", "00:00:00.500");
        builder.UseSetting("Hangfire:QueuePollInterval", "00:00:00.500");
    }
}
