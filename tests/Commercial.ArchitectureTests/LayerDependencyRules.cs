using System.Reflection;
using Commercial.ArchitectureTests.Support;
using NetArchTest.Rules;

namespace Commercial.ArchitectureTests;

/// <summary>A001, A002, A006, A007, A010 — assembly/namespace dependency rules.</summary>
public sealed class LayerDependencyRules
{
    private static readonly string[] ConnectorImplementationRoots =
        ProviderTokens.All.Select(t => "Commercial.Connectors." + t).ToArray();

    [Fact]
    public void A001_Domain_must_not_depend_on_infrastructure_frameworks_or_outer_layers()
    {
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions.DependencyInjection",
            "Hangfire",
            "StackExchange.Redis",
            "Npgsql",
            "Serilog",
            "OpenTelemetry",
            "System.Net.Http",
            "Commercial.Application",
            "Commercial.Infrastructure",
            "Commercial.Api",
            "Commercial.Worker",
            "Commercial.Connectors",
        ];

        AssertNoDependency(Repository.Domain, forbidden, "DOMAIN_INFRASTRUCTURE_DEPENDENCY_VIOLATION");
        AssertNoAssemblyReference(Repository.Domain, forbidden, "DOMAIN_INFRASTRUCTURE_DEPENDENCY_VIOLATION");
    }

    [Fact]
    public void A002_Application_must_not_depend_on_implementation_assemblies()
    {
        string[] forbidden =
        [
            "Commercial.Infrastructure",
            "Commercial.Api",
            "Commercial.Worker",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Hangfire",
            "StackExchange.Redis",
            "Npgsql",
            "Serilog",
            .. ConnectorImplementationRoots,
        ];

        AssertNoDependency(Repository.Application, forbidden, "APPLICATION_IMPLEMENTATION_DEPENDENCY_VIOLATION");
        AssertNoAssemblyReference(Repository.Application, forbidden, "APPLICATION_IMPLEMENTATION_DEPENDENCY_VIOLATION");

        // Convention (not a static list): Application may reference no Commercial.Connectors.* except Abstractions.
        var connectorRefs = Repository.Application.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Commercial.Connectors.", StringComparison.Ordinal)
                && n != "Commercial.Connectors.Abstractions")
            .ToArray();
        Assert.True(connectorRefs.Length == 0, $"APPLICATION_IMPLEMENTATION_DEPENDENCY_VIOLATION: {string.Join(", ", connectorRefs)}");
    }

    [Fact]
    public void A006_Api_must_not_bypass_application_boundary()
    {
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "Hangfire",
            "Commercial.Infrastructure.Persistence",
            "Commercial.Infrastructure.Messaging",
            "Commercial.Infrastructure.Caching",
            "Commercial.Infrastructure.BackgroundJobs",
        ];

        AssertNoDependency(Repository.Api, forbidden, "API_BYPASS_APPLICATION_BOUNDARY");
    }

    [Fact]
    public void A007_Worker_contains_no_business_logic_and_no_direct_persistence()
    {
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "StackExchange.Redis",
            "Commercial.Infrastructure.Persistence",
            "Commercial.Domain",
        ];

        AssertNoDependency(Repository.Worker, forbidden, "WORKER_BUSINESS_LOGIC_BOUNDARY_VIOLATION");

        var handlers = ImplementersOf(Repository.Worker, UseCaseContracts).ToArray();
        Assert.True(handlers.Length == 0, $"WORKER_BUSINESS_LOGIC_BOUNDARY_VIOLATION: use-case handlers in Worker: {string.Join(", ", handlers)}");
    }

    [Fact]
    public void A010_Infrastructure_must_not_implement_use_cases_or_business_rules()
    {
        var handlers = ImplementersOf(Repository.Infrastructure, UseCaseContracts).ToArray();
        Assert.True(handlers.Length == 0, $"INFRASTRUCTURE_BUSINESS_RULE_LEAKAGE: use-case contracts implemented in Infrastructure: {string.Join(", ", handlers)}");

        string[] businessSuffixes = ["Policy", "Policies", "Rule", "Rules", "Service", "UseCase", "Handler", "Engine", "Calculator", "Validator"];
        var leaks = Repository.Infrastructure.GetTypes()
            .Where(t => !t.Name.Contains('<', StringComparison.Ordinal))
            .Where(t => t.Namespace?.StartsWith("Commercial.Infrastructure.Persistence", StringComparison.Ordinal) == true)
            .Where(t => businessSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(leaks.Length == 0, $"INFRASTRUCTURE_BUSINESS_RULE_LEAKAGE: business-rule naming under persistence: {string.Join(", ", leaks)}");
    }

    private static readonly Type[] UseCaseContracts =
    [
        typeof(Commercial.Application.Abstractions.Requests.ICommandHandler<,>),
        typeof(Commercial.Application.Abstractions.Requests.IQueryHandler<,>),
        typeof(Commercial.Application.Abstractions.Requests.IRequestValidator<>),
        typeof(Commercial.Application.Abstractions.Messaging.IOutboxMessageHandler),
    ];

    private static IEnumerable<string> ImplementersOf(Assembly assembly, Type[] contracts) =>
        assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces().Any(i =>
                contracts.Contains(i.IsGenericType ? i.GetGenericTypeDefinition() : i)))
            .Select(t => t.FullName!);

    private static void AssertNoDependency(Assembly assembly, string[] forbidden, string failureCode)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(
            result.IsSuccessful,
            $"{failureCode}: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    private static void AssertNoAssemblyReference(Assembly assembly, string[] forbiddenRoots, string failureCode)
    {
        var offending = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => forbiddenRoots.Any(r => n == r || n.StartsWith(r + ".", StringComparison.Ordinal)))
            .ToArray();
        Assert.True(offending.Length == 0, $"{failureCode}: assembly references {string.Join(", ", offending)}");
    }
}
