using System.Text.RegularExpressions;
using Commercial.Application.Abstractions.Scope;
using Commercial.ArchitectureTests.Support;
using Commercial.Infrastructure;
using Commercial.Infrastructure.Persistence;
using Commercial.Infrastructure.Persistence.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.ArchitectureTests;

/// <summary>
/// A014 — tenant isolation (ADR-0001, ACCEPTED: RLS-primary hybrid). Two-stage and phase-aware:
///   always            — seams exist, no EF visibility query filters, No Reset On Close never enabled,
///                       phase ≥ 1 requires an ACCEPTED ADR, RLS forbidden while the ADR is not accepted;
///   phase 0+ACCEPTED  — decision recorded, implementation not yet required;
///   phase ≥ 1         — tenant-scoped tables have ENABLE+FORCE RLS and policies, production initializer,
///                       required isolation integration proofs.
/// </summary>
public sealed partial class TenantIsolationReadinessRules
{
    private const string Failure = "TENANT_ISOLATION_DECISION_GATE_VIOLATION";
    private const string AdrPath = "docs/adr/ADR-0001-tenant-isolation-decision-gate.md";
    private const string MigrationsDir = "src/Commercial.Infrastructure/Persistence/Migrations";

    /// <summary>Phase 1 introduces this marker for tenant-scoped business entities (Domain).</summary>
    public const string TenantScopedMarker = "Commercial.Domain.Isolation.ITenantScoped";

    public static readonly string[] RequiredPhase1Proofs =
        ["cross-tenant", "cross-company", "cross-customer", "fail-closed", "pool-scope-leak", "runtime-role-not-owner"];

    // ------------------------------------------------------------------ always

    [Fact]
    public void A014_Execution_scope_abstraction_carries_isolation_keys()
    {
        string[] required = ["TenantId", "CompanyId", "CustomerAccountId", "SalesChannelId", "PrincipalType", "PrincipalId", "CorrelationId"];
        var missing = required.Where(p => typeof(ExecutionScope).GetProperty(p) is null).ToArray();
        Assert.True(missing.Length == 0, $"{Failure}: ExecutionScope missing {string.Join(", ", missing)}");
        Assert.NotNull(typeof(IExecutionScopeAccessor).GetProperty(nameof(IExecutionScopeAccessor.Current)));
    }

    [Fact]
    public void A014_Session_initializer_is_wired_and_production_ready_from_phase_1()
    {
        using var provider = BuildInfrastructure();
        var initializer = provider.GetRequiredService<IDbSessionInitializer>();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommercialDbContext>();
        var interceptors = db.GetService<IDbContextOptions>()
            .Extensions.OfType<CoreOptionsExtension>()
            .SelectMany(e => e.Interceptors ?? [])
            .ToArray();
        Assert.Contains(interceptors, i => i is DbSessionInitializerInterceptor);

        if (ActivePhase.Load().Phase >= 1)
        {
            Assert.False(initializer is NoOpDbSessionInitializer, $"{Failure}: Phase 1+ requires the production RLS session initializer, not the no-op.");
        }
    }

    [Fact]
    public void A014_Phase_1_requires_an_accepted_ADR()
    {
        var phase = ActivePhase.Load();
        var status = ReadAdrStatus();
        if (phase.Phase >= 1)
        {
            Assert.True(status.StartsWith("ACCEPTED", StringComparison.Ordinal), $"{Failure}: Phase {phase.Phase} requires ADR-0001 ACCEPTED (found '{status}').");
        }
    }

    [Fact]
    public void A014_No_EF_global_visibility_filters()
    {
        // ADR-0001 §2.1: visibility lives only in PostgreSQL RLS. No tenant/company/customer EF filters.
        using var provider = BuildInfrastructure();
        using var scope = provider.CreateScope();
        var filtered = scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters().Count > 0)
            .Select(e => e.Name)
            .ToArray();
        Assert.True(filtered.Length == 0, $"{Failure}: EF global query filters are forbidden (RLS is authoritative): {string.Join(", ", filtered)}");
    }

    [Fact]
    public void A014_No_Reset_On_Close_is_never_enabled()
    {
        var offending = new[] { "src", "scripts", "frontend" }
            .SelectMany(d => Repository.TextFiles(d, ".cs", ".json", ".ps1", ".sh", ".yml", ".yaml"))
            .Concat(new[] { "docker-compose.yml" }.Select(f => Repository.PathOf(f)).Where(File.Exists))
            .Where(f => NoResetOnCloseEnabled(File.ReadAllText(f)))
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: 'No Reset On Close' leaks session scope across pooled connections: {string.Join(", ", offending)}");
    }

    [Theory]
    [InlineData("Host=h;No Reset On Close=true", true)]
    [InlineData("Host=h;NoResetOnClose=True", true)]
    [InlineData("builder.NoResetOnClose = true;", true)]
    [InlineData("Host=h;No Reset On Close=false", false)]
    [InlineData("Host=h;Maximum Pool Size=10", false)]
    public void A014_No_Reset_On_Close_detector(string text, bool expected) =>
        Assert.Equal(expected, NoResetOnCloseEnabled(text));

    [Fact]
    public void A014_RLS_requires_an_accepted_ADR()
    {
        if (ReadAdrStatus().StartsWith("ACCEPTED", StringComparison.Ordinal))
        {
            return;
        }

        var rls = Repository.TextFiles(MigrationsDir, ".cs")
            .Where(f => RlsPattern().IsMatch(File.ReadAllText(f)))
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(rls.Length == 0, $"{Failure}: RLS found while ADR-0001 is not accepted: {string.Join(", ", rls)}");
    }

    // ------------------------------------------------------------------ phase ≥ 1

    [Fact]
    public void A014_Phase_1_tenant_scoped_tables_have_forced_RLS_and_policies()
    {
        if (ActivePhase.Load().Phase < 1)
        {
            return; // Phase 0 + ACCEPTED: decision recorded; no tenant-scoped tables may exist (A018).
        }

        var marker = Repository.Domain.GetType(TenantScopedMarker);
        Assert.True(marker is not null, $"{Failure}: Phase 1 requires the tenant-scoped marker {TenantScopedMarker}.");

        using var provider = BuildInfrastructure();
        using var scope = provider.CreateScope();
        var tables = scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Model.GetEntityTypes()
            .Where(e => marker!.IsAssignableFrom(e.ClrType))
            .Select(e => $"{e.GetSchema() ?? CommercialDbContext.Schema}.{e.GetTableName()}")
            .Distinct()
            .ToArray();

        var migrations = string.Join("\n", Repository.TextFiles(MigrationsDir, ".cs").Select(File.ReadAllText));
        var violations = tables.SelectMany(t => MissingRls(t, migrations).Select(m => $"{t}: {m}")).ToArray();
        Assert.True(violations.Length == 0, $"{Failure}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void A014_RLS_migration_checker_detects_missing_pieces()
    {
        const string complete = """
            ALTER TABLE commercial.orders ENABLE ROW LEVEL SECURITY;
            ALTER TABLE commercial.orders FORCE ROW LEVEL SECURITY;
            CREATE POLICY orders_isolation ON commercial.orders USING (x) WITH CHECK (x);
            """;
        Assert.Empty(MissingRls("commercial.orders", complete));
        Assert.Equal(["FORCE ROW LEVEL SECURITY"], MissingRls("commercial.orders", complete.Replace("ALTER TABLE commercial.orders FORCE ROW LEVEL SECURITY;", "", StringComparison.Ordinal)));
        Assert.Equal(["ENABLE ROW LEVEL SECURITY", "FORCE ROW LEVEL SECURITY", "policy with USING and WITH CHECK"], MissingRls("commercial.quotes", complete));
    }

    [Fact]
    public void A014_Phase_1_isolation_integration_proofs_exist()
    {
        if (ActivePhase.Load().Phase < 1)
        {
            return;
        }

        var sources = Repository.TextFiles("tests/Commercial.IntegrationTests", ".cs")
            .Where(f => !Repository.Relative(f).Contains("/Spikes/", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        var missing = RequiredPhase1Proofs
            .Where(p => !sources.Any(s => s.Contains($"[Trait(\"ADR-0001\", \"{p}\")]", StringComparison.Ordinal)))
            .ToArray();
        Assert.True(missing.Length == 0, $"{Failure}: missing Phase 1 isolation proofs: {string.Join(", ", missing)}");
    }

    // ------------------------------------------------------------------ helpers

    internal static IReadOnlyList<string> MissingRls(string qualifiedTable, string migrationSql)
    {
        var t = Regex.Escape(qualifiedTable);
        var missing = new List<string>();
        if (!Regex.IsMatch(migrationSql, $@"ALTER\s+TABLE\s+{t}\s+ENABLE\s+ROW\s+LEVEL\s+SECURITY", RegexOptions.IgnoreCase))
        {
            missing.Add("ENABLE ROW LEVEL SECURITY");
        }

        if (!Regex.IsMatch(migrationSql, $@"ALTER\s+TABLE\s+{t}\s+FORCE\s+ROW\s+LEVEL\s+SECURITY", RegexOptions.IgnoreCase))
        {
            missing.Add("FORCE ROW LEVEL SECURITY");
        }

        if (!Regex.IsMatch(migrationSql, $@"CREATE\s+POLICY\s+\w+\s+ON\s+{t}\b[\s\S]*?USING[\s\S]*?WITH\s+CHECK", RegexOptions.IgnoreCase))
        {
            missing.Add("policy with USING and WITH CHECK");
        }

        return missing;
    }

    internal static bool NoResetOnCloseEnabled(string text) => NoResetOnClose().IsMatch(text);

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

    [GeneratedRegex(@"No\s*Reset\s*On\s*Close\s*=\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex NoResetOnClose();
}
