using System.Reflection;
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
/// Phase 1 WS3 — PostgreSQL session context (ADR-0001 §2.7/§2.8): writer = <see cref="RlsDbSessionInitializer"/>,
/// verifier = <see cref="DbSessionScopeGuard"/>. Failure code: DB_SESSION_CONTEXT_VIOLATION.
/// </summary>
public sealed partial class DbSessionContextRules
{
    private const string Failure = "DB_SESSION_CONTEXT_VIOLATION";
    private const string MigrationsDir = "src/Commercial.Infrastructure/Persistence/Migrations";

    /// <summary>Files allowed to create raw ADO commands on a CommercialDbContext / runtime connection.</summary>
    private static readonly Dictionary<string, string> RawCommandAllowList = new(StringComparer.Ordinal)
    {
        ["src/Commercial.Infrastructure/Persistence/Session/RlsDbSessionInitializer.cs"] = "the session-context writer itself",
        ["src/Commercial.Infrastructure/Persistence/Session/DbSessionContextReader.cs"] = "read-only current_setting of known keys",
        ["src/Commercial.Infrastructure/Messaging/Outbox/OutboxStore.cs"] = "outbox lease SQL; every command verified by DbSessionScopeGuard",
    };

    [Fact]
    public void Production_initializer_is_the_RLS_writer_not_the_no_op()
    {
        using var provider = BuildInfrastructure();
        Assert.IsType<RlsDbSessionInitializer>(provider.GetRequiredService<IDbSessionInitializer>());
    }

    [Fact]
    public void CommercialDbContext_always_carries_writer_and_verifier_interceptors()
    {
        using var provider = BuildInfrastructure();
        using var scope = provider.CreateScope();
        var interceptors = scope.ServiceProvider.GetRequiredService<CommercialDbContext>().GetService<IDbContextOptions>()
            .Extensions.OfType<CoreOptionsExtension>()
            .SelectMany(e => e.Interceptors ?? [])
            .ToArray();

        Assert.Contains(interceptors, i => i is DbSessionInitializerInterceptor);
        Assert.Contains(interceptors, i => i is DbSessionScopeGuardInterceptor);
    }

    [Fact]
    public void Runtime_CommercialDbContext_is_registered_once_and_only_migration_tooling_builds_its_own()
    {
        var offending = Repository.TextFiles("src", ".cs")
            .Where(f => !Repository.Relative(f).StartsWith(MigrationsDir, StringComparison.Ordinal))
            .SelectMany(f => OwnContextConstruction().Matches(File.ReadAllText(f)).Select(m => (File: Repository.Relative(f), m.Value)))
            .Where(x => x.File is not ("src/Commercial.Infrastructure/Hosting/DatabaseProvisioner.cs"
                or "src/Commercial.Infrastructure/Persistence/DesignTimeDbContextFactory.cs"
                or "src/Commercial.Infrastructure/Persistence/PersistenceRegistration.cs"))
            .Select(x => $"{x.File}: {x.Value}")
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: CommercialDbContext built outside the guarded registration: {string.Join("; ", offending)}");

        // The two tooling paths use the MIGRATION identity (owner) and never the runtime session path.
        foreach (var file in new[] { "src/Commercial.Infrastructure/Hosting/DatabaseProvisioner.cs", "src/Commercial.Infrastructure/Persistence/DesignTimeDbContextFactory.cs" })
        {
            Assert.Contains("ConfigureMigrationContext", File.ReadAllText(Repository.PathOf(file)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Writer_sets_every_key_session_level_with_parameters()
    {
        var sql = (string)typeof(RlsDbSessionInitializer).GetField("SetAllSql", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal(DbSessionContext.Keys.Count, SetConfigCall().Count(sql));
        Assert.Equal(DbSessionContext.Keys.Count, Regex.Count(sql, @", false\)")); // session level, never transaction-local
        Assert.DoesNotContain("commercial.", sql, StringComparison.Ordinal);             // key names are parameters, not SQL text
        Assert.DoesNotContain("true", sql, StringComparison.OrdinalIgnoreCase);

        foreach (var scope in new[]
        {
            ExecutionScope.Unscoped, ExecutionScope.SystemInfrastructure("x"),
            ExecutionScope.SystemBusiness(new(Guid.NewGuid()), new(Guid.NewGuid()), null, null, "x"),
        })
        {
            Assert.Equal(DbSessionContext.Keys, DbSessionContext.FromScope(scope).Settings.Select(s => s.Key));
            Assert.All(DbSessionContext.FromScope(scope).Settings, s => Assert.NotNull(s.Value));
        }
    }

    [Fact]
    public void Raw_ADO_commands_only_in_allow_listed_infrastructure_files_and_outbox_commands_are_verified()
    {
        var offending = Repository.TextFiles("src", ".cs")
            .Where(f => RawCommand().IsMatch(File.ReadAllText(f)))
            .Select(Repository.Relative)
            .Where(f => !RawCommandAllowList.ContainsKey(f)
                && f is not "src/Commercial.Infrastructure/Hosting/DatabaseProvisioner.cs") // owner-only provisioning connection (Pooling=false), no runtime session
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: raw ADO commands bypass the fingerprint guard: {string.Join(", ", offending)}");

        var outbox = File.ReadAllText(Repository.PathOf("src/Commercial.Infrastructure/Messaging/Outbox/OutboxStore.cs"));
        Assert.Equal(RawCommand().Count(outbox), Regex.Count(outbox, @"sessionGuard\.Verify\(command\)"));
    }

    [Fact]
    public void Session_initialization_never_uses_owner_or_maintenance_identity()
    {
        var registration = File.ReadAllText(Repository.PathOf("src/Commercial.Infrastructure/Persistence/PersistenceRegistration.cs"));
        Assert.Contains("PostgresConnection.Resolve(configuration))", registration, StringComparison.Ordinal); // runtime overload
        Assert.DoesNotContain("DatabaseIdentity.Migration", registration, StringComparison.Ordinal);

        foreach (var file in Repository.TextFiles("src/Commercial.Infrastructure/Persistence/Session", ".cs"))
        {
            var text = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(text, "commercial_platform_(owner|maintenance)|DatabaseIdentity\\.Migration|Maintenance:"),
                $"{Failure}: {Repository.Relative(file)} references a privileged identity");
        }
    }

    [Fact]
    public void No_production_RLS_schema_before_WS4()
    {
        // WS3 prepares the session context only. Production RLS tables / helper functions / policies arrive with WS4,
        // whose approved change replaces this rule with the A014 Level A invariants for real scoped tables.
        var offending = Repository.TextFiles(MigrationsDir, ".cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"ROW\s+LEVEL\s+SECURITY|CREATE\s+POLICY|current_setting\s*\(\s*'commercial\.", RegexOptions.IgnoreCase))
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: production RLS schema is WS4 scope: {string.Join(", ", offending)}");
    }

    [GeneratedRegex(@"set_config\(@k\d+, @v\d+, false\)")]
    private static partial Regex SetConfigCall();

    [GeneratedRegex(@"\.CreateCommand\(\)|new\s+NpgsqlCommand\s*\(")]
    private static partial Regex RawCommand();

    [GeneratedRegex(@"new\s+CommercialDbContext\s*\(|AddDbContext(Pool)?\s*<\s*CommercialDbContext")]
    private static partial Regex OwnContextConstruction();

    private static ServiceProvider BuildInfrastructure()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never connected to: DI inspection only.
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
}
