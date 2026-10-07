using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Commercial.Infrastructure.Hosting;

/// <summary>User-secrets store ids (Development only; never contain production credentials).</summary>
public static class DevelopmentSecrets
{
    /// <summary>
    /// Runtime store, loaded by API and Worker in Development. Holds ONLY the runtime password:
    /// <c>dotnet user-secrets set "Commercial:Postgres:Runtime:Password" ... --id commercial-platform-dev</c>.
    /// </summary>
    public const string UserSecretsId = "commercial-platform-dev";

    /// <summary>
    /// Operations/tooling store — never loaded by the API/Worker runtime. Holds migration (owner), maintenance and DBA
    /// credentials used by <c>dotnet ef</c>, <c>--provision-database</c> and <c>scripts/shared-db.ps1</c>.
    /// </summary>
    public const string OpsUserSecretsId = "commercial-platform-ops";
}

/// <summary>
/// <c>Commercial.Worker --provision-database</c>: one-shot, owner-identity provisioning (EF migrations + Hangfire
/// schema), then exit. This is the ONLY Worker mode that loads the ops secrets store; the normal runtime never does.
/// </summary>
public static class DatabaseProvisioningCommand
{
    public const string Switch = "--provision-database";

    public static bool IsRequested(string[] args) => args.Contains(Switch, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args.Where(a => !string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase)).ToArray());
        builder.Configuration.AddUserSecrets(DevelopmentSecrets.OpsUserSecretsId);
        builder.Configuration.AddEnvironmentVariables(); // environment wins over user-secrets

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var logger = loggerFactory.CreateLogger("Commercial.DatabaseProvisioning");
        try
        {
            await DatabaseProvisioner.ProvisionAsync(builder.Configuration, logger, CancellationToken.None);
            return 0;
        }
#pragma warning disable CA1031 // command-line entry point: report and exit non-zero
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError("Database provisioning failed: {Error}", ex.Message);
            return 1;
        }
    }
}
