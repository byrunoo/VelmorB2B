using Commercial.Api;
using Commercial.Application.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.ContractTests;

/// <summary>
/// The real API host with no reachable database: the Phase 0 contract (OpenAPI + error envelope)
/// must be verifiable without infrastructure. Connection strings point to an invalid host and are
/// never opened by these tests.
/// </summary>
public class ContractApiFactory : WebApplicationFactory<ApiAssemblyMarker>
{
    protected virtual bool IncludeContractProbes => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Commercial", "Host=contract-tests.invalid;Database=none;Username=none");
        builder.UseSetting("ConnectionStrings:Redis", "contract-tests.invalid:6379,abortConnect=false");
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        foreach (var key in new[] { "Host", "Port", "Database", "Username", "Password", "Runtime:Username", "Runtime:Password", "Migration:Username", "Migration:Password" })
        {
            builder.UseSetting($"Commercial:Postgres:{key}", string.Empty); // never a shared/dev database
        }

        if (IncludeContractProbes)
        {
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, ContractProbeStartupFilter>());
        }
    }

    /// <summary>
    /// Test-only routes appended AFTER the application pipeline (so the real exception handler and
    /// correlation middleware wrap them). They exist only in this test host and are NOT part of the
    /// OpenAPI snapshot.
    /// </summary>
    private sealed class ContractProbeStartupFilter : IStartupFilter
    {
        public const string LeakyMessage = "Npgsql connection to db-secret-host failed: password authentication failed";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/__contract/conflict", b => b.Run(_ => throw new ConflictException("The resource was modified by another request.", "CONCURRENCY_CONFLICT")));
            app.Map("/__contract/unhandled", b => b.Run(_ => throw new InvalidOperationException(LeakyMessage)));
            app.Map("/__contract/not-found", b => b.Run(_ => throw new NotFoundException("Thing 'x' was not found.")));
        };
    }
}

public sealed class ContractApiFactoryWithProbes : ContractApiFactory
{
    protected override bool IncludeContractProbes => true;
}
