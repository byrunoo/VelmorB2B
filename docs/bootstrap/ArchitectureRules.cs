using System;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Commercial.ArchitectureTests;

/// <summary>
/// Starting sample only. The repository implementation must extend this to satisfy
/// ARCHITECTURE_TEST_CONTRACT.md. Do not weaken rules just to make a build pass.
/// </summary>
public sealed class ArchitectureRules
{
    private static readonly Assembly DomainAssembly =
        typeof(Commercial.Domain.AssemblyMarker).Assembly;

    private static readonly Assembly ApplicationAssembly =
        typeof(Commercial.Application.AssemblyMarker).Assembly;

    private static readonly Assembly ConnectorAbstractionsAssembly =
        typeof(Commercial.Connectors.Abstractions.AssemblyMarker).Assembly;

    // Exact known provider token sequences. Intentionally no plain "Logo" or "SAP".
    private static readonly string[] ProviderTokens =
    [
        "Kumpara",
        "BusinessOS",
        "LogoTiger",
        "LogoGo",
        "LogoIsbasi",
        "LogoErp",
        "MikroErp",
        "Netsis",
        "Parasut",
        "Nebim",
        "SapBusinessOne"
    ];

    [Fact]
    public void Domain_must_not_depend_on_infrastructure_frameworks()
    {
        string[] forbiddenNamespaces =
        [
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Hangfire",
            "StackExchange.Redis",
            "Npgsql",
            "Serilog",
            "OpenTelemetry",
            "Commercial.Infrastructure",
            "Commercial.Api",
            "Commercial.Worker"
        ];

        var result = Types
            .InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        Assert.True(result.IsSuccessful, "DOMAIN_INFRASTRUCTURE_DEPENDENCY_VIOLATION");
    }

    [Fact]
    public void Application_must_not_depend_on_implementation_assemblies()
    {
        string[] forbiddenNamespaces =
        [
            "Commercial.Infrastructure",
            "Commercial.Api",
            "Commercial.Worker",
            "Commercial.Connectors.Kumpara",
            "Commercial.Connectors.LogoTiger",
            "Commercial.Connectors.LogoGo",
            "Commercial.Connectors.LogoIsbasi",
            "Commercial.Connectors.LogoErp",
            "Commercial.Connectors.MikroErp",
            "Commercial.Connectors.Netsis",
            "Commercial.Connectors.Parasut",
            "Commercial.Connectors.Nebim",
            "Commercial.Connectors.SapBusinessOne"
        ];

        var result = Types
            .InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        Assert.True(result.IsSuccessful, "APPLICATION_IMPLEMENTATION_DEPENDENCY_VIOLATION");
    }

    [Fact]
    public void Core_must_not_depend_on_exact_provider_tokens()
    {
        foreach (var providerToken in ProviderTokens)
        {
            var domainResult = Types
                .InAssembly(DomainAssembly)
                .ShouldNot()
                .HaveDependencyOn(providerToken)
                .GetResult();

            var applicationResult = Types
                .InAssembly(ApplicationAssembly)
                .ShouldNot()
                .HaveDependencyOn(providerToken)
                .GetResult();

            Assert.True(
                domainResult.IsSuccessful && applicationResult.IsSuccessful,
                $"PROVIDER_LEAKAGE_IN_CORE: dependency providerToken={providerToken}");
        }
    }

    [Fact]
    public void Core_defined_type_and_namespace_names_must_not_contain_provider_tokens()
    {
        var violations = DomainAssembly.GetTypes()
            .Concat(ApplicationAssembly.GetTypes())
            .Select(type => new
            {
                Type = type,
                Match = FindProviderToken(type.FullName ?? type.Name)
            })
            .Where(x => x.Match is not null)
            .Select(x => $"{x.Type.FullName} -> {x.Match}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"PROVIDER_LEAKAGE_IN_CORE: declarations={string.Join(", ", violations)}");
    }

    [Fact]
    public void Connector_abstractions_must_not_depend_on_exact_provider_tokens()
    {
        foreach (var providerToken in ProviderTokens)
        {
            var result = Types
                .InAssembly(ConnectorAbstractionsAssembly)
                .ShouldNot()
                .HaveDependencyOn(providerToken)
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"CONNECTOR_ABSTRACTION_PROVIDER_LEAKAGE: dependency providerToken={providerToken}");
        }
    }

    [Fact]
    public void Connector_abstractions_defined_types_must_be_provider_independent()
    {
        var violations = ConnectorAbstractionsAssembly.GetTypes()
            .Select(type => new
            {
                Type = type,
                Match = FindProviderToken(type.FullName ?? type.Name)
            })
            .Where(x => x.Match is not null)
            .Select(x => $"{x.Type.FullName} -> {x.Match}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"CONNECTOR_ABSTRACTION_PROVIDER_LEAKAGE: declarations={string.Join(", ", violations)}");
    }

    [Theory]
    [InlineData("CompanyLogo")]
    [InlineData("LogoUrl")]
    [InlineData("BrandLogo")]
    [InlineData("SaplingConfiguration")]
    public void Legitimate_branding_names_must_not_match_provider_scanner(string name)
    {
        Assert.Null(FindProviderToken(name));
    }

    [Theory]
    [InlineData("KumparaPriceMapper", "Kumpara")]
    [InlineData("Commercial.Application.LogoTigerOrderPolicy", "LogoTiger")]
    [InlineData("SapBusinessOneAdapter", "SapBusinessOne")]
    public void Provider_scanner_must_match_exact_known_provider_sequences(
        string name,
        string expectedToken)
    {
        Assert.Equal(expectedToken, FindProviderToken(name));
    }

    private static string? FindProviderToken(string value)
    {
        return ProviderTokens.FirstOrDefault(
            token => value.Contains(token, StringComparison.Ordinal));
    }
}
