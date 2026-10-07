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
/// A014 — tenant isolation (ADR-0001, ACCEPTED: RLS-primary hybrid).
///   Always: seams exist; no EF visibility query filters; No Reset On Close never enabled; phase ≥ 1 requires an
///           ACCEPTED ADR; RLS DDL requires an ACCEPTED ADR.
///   Level A (implementation invariants, any phase, triggered by the implementation itself): scoped entity ⇒
///           NOT NULL scope columns + ENABLE/FORCE RLS + USING/WITH CHECK policy; isolation implementation ⇒
///           production initializer (not no-op) + core integration proofs.
///   Level B (Phase 1 acceptance, 25+ proofs) is proven by the Phase 1 acceptance suite/report — not by a flag here.
/// </summary>
public sealed partial class TenantIsolationReadinessRules
{
    private const string Failure = "TENANT_ISOLATION_DECISION_GATE_VIOLATION";
    private const string AdrPath = "docs/adr/ADR-0001-tenant-isolation-decision-gate.md";
    private const string MigrationsDir = "src/Commercial.Infrastructure/Persistence/Migrations";

    public static readonly string[] CoreIsolationProofs =
        ["cross-tenant", "cross-company", "cross-customer", "fail-closed", "pool-scope-leak", "runtime-role-not-owner"];

    // ------------------------------------------------------------------ always

    [Fact]
    public void A014_Execution_scope_abstraction_carries_isolation_keys()
    {
        // ExecutionScope v2 (Phase 1 WS2). SalesChannelId was removed: sales channels are out of Phase 1 scope (A018)
        // and return as a scope key only with their phase.
        string[] required =
        [
            "TenantId", "ActiveCompanyId", "CustomerAccountId", "ActiveCustomerBindingId", "PrincipalType", "PrincipalId",
            "Permissions", "Grants", "CorrelationId", "Fingerprint",
        ];
        var missing = required.Where(p => typeof(ExecutionScope).GetProperty(p) is null).ToArray();
        Assert.True(missing.Length == 0, $"{Failure}: ExecutionScope missing {string.Join(", ", missing)}");
        Assert.NotNull(typeof(IExecutionScopeAccessor).GetProperty(nameof(IExecutionScopeAccessor.Current)));
    }

    [Fact]
    public void A014_Session_initializer_is_wired_and_not_no_op_once_isolation_exists()
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

        // Level A: once isolation implementation exists, the registered initializer cannot be the no-op.
        if (IsolationImplementationPresent())
        {
            Assert.False(initializer is NoOpDbSessionInitializer, $"{Failure}: isolation implementation exists but the registered IDbSessionInitializer is the no-op.");
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

    // ------------------------------------------------------------------ Level A — implementation invariants
    // Triggered by the presence of isolation implementation (any phase), never by a phase number or a
    // manual "phase complete" flag. Full Phase 1 completeness is proven by the Phase 1 acceptance suite/report.

    [Fact]
    public void A014_Tenant_scoped_tables_have_scope_columns_forced_RLS_and_policies()
    {
        using var provider = BuildInfrastructure();
        using var scope = provider.CreateScope();
        var scoped = ScopedEntities(scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Model);
        if (scoped.Count == 0)
        {
            return; // no tenant-scoped entity yet — nothing to enforce
        }

        var migrations = string.Join("\n", Repository.TextFiles(MigrationsDir, ".cs").Select(File.ReadAllText));
        var violations = new List<string>();
        foreach (var (entity, markers) in scoped)
        {
            var table = $"{entity.GetSchema() ?? CommercialDbContext.Schema}.{entity.GetTableName()}";
            violations.AddRange(MissingRls(table, migrations).Select(m => $"{table}: {m}"));
            foreach (var column in RequiredScopeColumns(markers))
            {
                var property = entity.FindProperty(column);
                if (property is null || property.IsNullable)
                {
                    violations.Add($"{entity.ClrType.Name}: {column} must exist and be NOT NULL");
                }
            }
        }

        Assert.True(violations.Count == 0, $"{Failure}: {string.Join("; ", violations)}");
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
    public void A014_Isolation_implementation_has_core_integration_proofs()
    {
        if (!IsolationImplementationPresent())
        {
            return;
        }

        var sources = Repository.TextFiles("tests/Commercial.IntegrationTests", ".cs")
            .Where(f => !Repository.Relative(f).Contains("/Spikes/", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        var missing = CoreIsolationProofs
            .Where(p => !sources.Any(s => s.Contains($"[Trait(\"ADR-0001\", \"{p}\")]", StringComparison.Ordinal)))
            .ToArray();
        Assert.True(missing.Length == 0, $"{Failure}: isolation implementation exists but core proofs are missing: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData(new[] { "ITenantScoped" }, new[] { "TenantId" })]
    [InlineData(new[] { "ITenantScoped", "ICompanyScoped" }, new[] { "TenantId", "CompanyId" })]
    [InlineData(new[] { "ITenantScoped", "ICompanyScoped", "ICustomerAccountScoped" }, new[] { "TenantId", "CompanyId", "CustomerAccountId" })]
    public void A014_Scope_column_requirements_follow_markers(string[] markers, string[] expected) =>
        Assert.Equal(expected, RequiredScopeColumns(markers));

    private static readonly string[] ScopeMarkers = ["ITenantScoped", "ICompanyScoped", "ICustomerAccountScoped"];

    /// <summary>Entities implementing any Commercial.Domain.Isolation scope marker (resolved by name; may not exist yet).</summary>
    private static List<(Microsoft.EntityFrameworkCore.Metadata.IEntityType Entity, string[] Markers)> ScopedEntities(Microsoft.EntityFrameworkCore.Metadata.IModel model)
    {
        var markerTypes = ScopeMarkers
            .Select(m => Repository.Domain.GetType($"Commercial.Domain.Isolation.{m}"))
            .OfType<Type>()
            .ToArray();

        return model.GetEntityTypes()
            .Select(e => (Entity: e, Markers: markerTypes.Where(m => m.IsAssignableFrom(e.ClrType)).Select(m => m.Name).ToArray()))
            .Where(x => x.Markers.Length > 0)
            .ToList();
    }

    internal static string[] RequiredScopeColumns(IEnumerable<string> markers)
    {
        var set = markers.ToHashSet(StringComparer.Ordinal);
        var columns = new List<string>();
        if (set.Count > 0)
        {
            columns.Add("TenantId"); // every scope marker implies tenant scope
        }

        if (set.Contains("ICompanyScoped") || set.Contains("ICustomerAccountScoped"))
        {
            columns.Add("CompanyId");
        }

        if (set.Contains("ICustomerAccountScoped"))
        {
            columns.Add("CustomerAccountId");
        }

        return [.. columns];
    }

    /// <summary>True once any isolation implementation exists: a scoped entity, RLS DDL in migrations, or a production initializer type.</summary>
    private static bool IsolationImplementationPresent()
    {
        using var provider = BuildInfrastructure();
        using var scope = provider.CreateScope();
        var scoped = ScopedEntities(scope.ServiceProvider.GetRequiredService<CommercialDbContext>().Model).Count > 0;
        var rlsDdl = Repository.TextFiles(MigrationsDir, ".cs").Any(f => RlsPattern().IsMatch(File.ReadAllText(f)));
        var productionInitializer = Repository.Infrastructure.GetTypes()
            .Any(t => t is { IsClass: true, IsAbstract: false } && typeof(IDbSessionInitializer).IsAssignableFrom(t) && t != typeof(NoOpDbSessionInitializer));
        return scoped || rlsDdl || productionInitializer;
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
