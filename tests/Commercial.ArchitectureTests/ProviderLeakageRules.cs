using System.Reflection;
using Commercial.ArchitectureTests.Support;
using NetArchTest.Rules;

namespace Commercial.ArchitectureTests;

/// <summary>A003 (provider leakage in core) and A008 (connector abstractions provider-independent).</summary>
public sealed class ProviderLeakageRules
{
    // ---- Scanner self-tests (required by contract §7) ----

    [Theory]
    [InlineData("CompanyLogo")]
    [InlineData("LogoUrl")]
    [InlineData("BrandLogo")]
    [InlineData("SaplingConfiguration")]
    [InlineData("Commercial.Domain.Branding.CompanyLogoUrl")]
    [InlineData("LogoGoal")]
    [InlineData("WinnerBadge")]
    [InlineData("SAPPHIRE_THEME")]
    public void Scanner_must_not_flag_legitimate_names(string name) =>
        Assert.Null(ProviderTokens.Find(name));

    [Theory]
    [InlineData("KumparaPriceMapper", "Kumpara")]
    [InlineData("Commercial.Application.LogoTigerOrderPolicy", "LogoTiger")]
    [InlineData("SapBusinessOneAdapter", "SapBusinessOne")]
    [InlineData("BusinessOSClient", "BusinessOS")]
    [InlineData("KumparaOrderDto", "Kumpara")]
    [InlineData("LogoErpCapabilitySwitch", "LogoErp")]
    [InlineData("MikroErpGateway", "MikroErp")]
    [InlineData("NebimV3Client", "Nebim")]
    [InlineData("WinnerErpConnector", "WinnerErp")]
    [InlineData("kumparaBaseUrl", "Kumpara")]
    [InlineData("PARASUT_TOKEN", "Parasut")]
    public void Scanner_must_match_exact_provider_word_sequences(string name, string expected) =>
        Assert.Equal(expected, ProviderTokens.Find(name));

    // ---- A003 ----

    [Fact]
    public void A003_Core_must_not_reference_provider_assemblies_or_namespaces()
    {
        foreach (var assembly in new[] { Repository.Domain, Repository.Application })
        {
            AssertNoProviderDependency(assembly, "PROVIDER_LEAKAGE_IN_CORE");
        }
    }

    [Fact]
    public void A003_Core_type_and_namespace_declarations_must_not_contain_provider_tokens()
    {
        var violations = DeclarationViolations(Repository.Domain).Concat(DeclarationViolations(Repository.Application)).ToArray();
        Assert.True(violations.Length == 0, $"PROVIDER_LEAKAGE_IN_CORE: declarations={string.Join(", ", violations)}");
    }

    [Fact]
    public void A003_Core_source_must_not_branch_on_provider_identity()
    {
        // Catches provider names in string constants / member names / switch labels that
        // dependency + declaration scans cannot see (e.g. if (provider == "Kumpara")).
        var violations = SourceViolations("src/Commercial.Domain").Concat(SourceViolations("src/Commercial.Application")).ToArray();
        Assert.True(violations.Length == 0, $"PROVIDER_LEAKAGE_IN_CORE: source={string.Join(", ", violations)}");
    }

    // ---- A008 ----

    [Fact]
    public void A008_Connector_abstractions_must_not_depend_on_providers()
    {
        AssertNoProviderDependency(Repository.ConnectorAbstractions, "CONNECTOR_ABSTRACTION_PROVIDER_LEAKAGE");

        var connectorRefs = Repository.ConnectorAbstractions.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Commercial.", StringComparison.Ordinal))
            .ToArray();
        Assert.True(connectorRefs.Length == 0, $"CONNECTOR_ABSTRACTION_PROVIDER_LEAKAGE: abstractions must stay dependency-free, found {string.Join(", ", connectorRefs)}");
    }

    [Fact]
    public void A008_Connector_abstraction_declarations_and_source_must_be_provider_independent()
    {
        var violations = DeclarationViolations(Repository.ConnectorAbstractions)
            .Concat(SourceViolations("src/Commercial.Connectors.Abstractions"))
            .ToArray();
        Assert.True(violations.Length == 0, $"CONNECTOR_ABSTRACTION_PROVIDER_LEAKAGE: {string.Join(", ", violations)}");
    }

    private static void AssertNoProviderDependency(Assembly assembly, string failureCode)
    {
        var roots = ProviderTokens.All
            .SelectMany(t => new[] { t, "Commercial.Connectors." + t })
            .ToArray();
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(roots).GetResult();
        Assert.True(result.IsSuccessful, $"{failureCode}: dependency {string.Join(", ", result.FailingTypeNames ?? [])}");

        var refs = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => ProviderTokens.Find(n) is not null)
            .ToArray();
        Assert.True(refs.Length == 0, $"{failureCode}: assembly references {string.Join(", ", refs)}");
    }

    private static IEnumerable<string> DeclarationViolations(Assembly assembly) =>
        assembly.GetTypes()
            .Select(t => (Type: t, Match: ProviderTokens.Find(t.FullName ?? t.Name)))
            .Where(x => x.Match is not null)
            .Select(x => $"{x.Type.FullName} -> {x.Match}")
            .OrderBy(x => x, StringComparer.Ordinal);

    private static IEnumerable<string> SourceViolations(string relativeDir) =>
        Repository.TextFiles(relativeDir, ".cs")
            .SelectMany(file => SourceText.Words(File.ReadAllText(file))
                .Select(w => (File: file, Word: w, Match: ProviderTokens.Find(w))))
            .Where(x => x.Match is not null)
            .Select(x => $"{Repository.Relative(x.File)}: '{x.Word}' -> {x.Match}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal);
}
