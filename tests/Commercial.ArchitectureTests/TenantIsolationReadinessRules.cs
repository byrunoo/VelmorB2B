using System.Text.RegularExpressions;
using Commercial.Application.Abstractions.Scope;
using Commercial.ArchitectureTests.Support;
using Commercial.Infrastructure;
using Commercial.Infrastructure.Persistence;
using Commercial.Infrastructure.Persistence.Session;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.ArchitectureTests;

/// <summary>A014 — tenant isolation must stay pluggable until ADR-0001 is decided.</summary>
public sealed partial class TenantIsolationReadinessRules
{
    private const string Failure = "TENANT_ISOLATION_DECISION_GATE_VIOLATION";
    private const string AdrPath = "docs/adr/ADR-0001-tenant-isolation-decision-gate.md";
    private const string Undecided = "REQUIRED BEFORE PHASE 1 — NOT YET DECIDED";

    [Fact]
    public void A014_Execution_scope_abstraction_carries_future_isolation_keys()
    {
        string[] required = ["TenantId", "CompanyId", "CustomerAccountId", "SalesChannelId", "PrincipalType", "PrincipalId", "CorrelationId"];
        var missing = required.Where(p => typeof(ExecutionScope).GetProperty(p) is null).ToArray();
        Assert.True(missing.Length == 0, $"{Failure}: ExecutionScope missing {string.Join(", ", missing)}");
        Assert.NotNull(typeof(IExecutionScopeAccessor).GetProperty(nameof(IExecutionScopeAccessor.Current)));
    }

    [Fact]
    public void A014_Session_initializer_hook_is_registered_and_no_op_by_default()
    {
        using var provider = BuildInfrastructure();
        var initializer = provider.GetRequiredService<IDbSessionInitializer>();
        Assert.IsType<NoOpDbSessionInitializer>(initializer);

        // The hook must be wired into the centralized DbContext registration.
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommercialDbContext>();
        var interceptors = db.GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>()
            .Extensions.OfType<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()
            .SelectMany(e => e.Interceptors ?? [])
            .ToArray();
        Assert.Contains(interceptors, i => i is DbSessionInitializerInterceptor);
    }

    [Fact]
    public void A014_Phase_gate_matches_ADR_status()
    {
        var phase = ActivePhase.Load();
        var status = ReadAdrStatus();

        if (phase.Phase >= 1)
        {
            Assert.False(
                status.Contains(Undecided, StringComparison.Ordinal),
                $"{Failure}: active phase is {phase.Phase} but ADR-0001 is still '{status}'.");
            Assert.True(
                status.StartsWith("ACCEPTED", StringComparison.Ordinal),
                $"{Failure}: Phase 1+ requires ADR-0001 status ACCEPTED (found '{status}').");
        }
    }

    [Fact]
    public void A014_No_isolation_mechanism_is_chosen_while_ADR_is_undecided()
    {
        if (!ReadAdrStatus().Contains(Undecided, StringComparison.Ordinal))
        {
            return; // decided by human review — the decided mechanism is then governed by the ADR
        }

        var rlsInMigrations = Repository.TextFiles("src/Commercial.Infrastructure/Persistence/Migrations", ".cs")
            .Where(f => RlsPattern().IsMatch(File.ReadAllText(f)))
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(rlsInMigrations.Length == 0, $"{Failure}: RLS found while ADR-0001 undecided: {string.Join(", ", rlsInMigrations)}");

        using var provider = BuildInfrastructure();
        using var scope = provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Model;
        var filtered = model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters().Count > 0)
            .Select(e => e.Name)
            .ToArray();
        Assert.True(filtered.Length == 0, $"{Failure}: global query filters found while ADR-0001 undecided: {string.Join(", ", filtered)}");
    }

    private static string ReadAdrStatus()
    {
        var path = Repository.PathOf(AdrPath);
        Assert.True(File.Exists(path), $"{Failure}: {AdrPath} is missing.");
        var match = StatusLine().Match(File.ReadAllText(path));
        Assert.True(match.Success, $"{Failure}: {AdrPath} has no '**Status:**' line.");
        return match.Groups[1].Value.Trim();
    }

    private static ServiceProvider BuildInfrastructure()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never connected to: model/DI inspection only.
                ["ConnectionStrings:Commercial"] = "Host=architecture-test.invalid;Database=none;Username=none",
                ["ConnectionStrings:Redis"] = "architecture-test.invalid:6379",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        Commercial.Application.DependencyInjection.AddCommercialApplication(services);
        services.AddCommercialInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    [GeneratedRegex(@"\*\*Status:\*\*\s*(.+)")]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"ROW\s+LEVEL\s+SECURITY|CREATE\s+POLICY", RegexOptions.IgnoreCase)]
    private static partial Regex RlsPattern();
}
