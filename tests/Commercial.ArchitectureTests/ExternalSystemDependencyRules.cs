using System.Text.Json;
using Commercial.ArchitectureTests.Support;
using NetArchTest.Rules;

namespace Commercial.ArchitectureTests;

/// <summary>A004 (Kumpara internal dependency) and A005 (legacy Sales Ops dependency).</summary>
public sealed class ExternalSystemDependencyRules
{
    /// <summary>Kumpara / BusinessOS internal roots. Only the Public API over HTTP is allowed (future connector).</summary>
    private static readonly string[] KumparaInternalRoots = ["BusinessOS", "Kumpara"];

    [Fact]
    public void A004_No_production_assembly_references_Kumpara_internals()
    {
        var violations = new List<string>();

        foreach (var assembly in Repository.ProductionAssemblies)
        {
            violations.AddRange(assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => MatchesRoot(n, KumparaInternalRoots))
                .Select(n => $"{assembly.GetName().Name} -> assembly {n}"));

            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(KumparaInternalRoots).GetResult();
            violations.AddRange((result.FailingTypeNames ?? []).Select(t => $"{t} -> namespace"));
        }

        foreach (var project in Repository.ProductionProjects)
        {
            violations.AddRange(project.ProjectReferences.Concat(project.PackageReferences).Concat(project.AssemblyReferences)
                .Where(r => MatchesRoot(r, KumparaInternalRoots))
                .Select(r => $"{project.Name}.csproj -> {r}"));
        }

        Assert.True(violations.Count == 0, $"KUMPARA_INTERNAL_DEPENDENCY_VIOLATION: {string.Join("; ", violations)}");
    }

    [Fact]
    public void A005_No_production_assembly_references_legacy_Sales_Ops()
    {
        var identifiers = LoadLegacyIdentifiers();
        Assert.NotEmpty(identifiers);
        var violations = new List<string>();

        foreach (var assembly in Repository.ProductionAssemblies)
        {
            violations.AddRange(assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => MatchesRoot(n, identifiers))
                .Select(n => $"{assembly.GetName().Name} -> assembly {n}"));

            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(identifiers).GetResult();
            violations.AddRange((result.FailingTypeNames ?? []).Select(t => $"{t} -> namespace"));

            violations.AddRange(assembly.GetTypes()
                .Where(t => t.Namespace is not null && MatchesRoot(t.Namespace, identifiers))
                .Select(t => $"{t.FullName} declared in legacy root namespace"));
        }

        foreach (var project in Repository.ProductionProjects)
        {
            violations.AddRange(project.ProjectReferences.Concat(project.PackageReferences).Concat(project.AssemblyReferences)
                .Where(r => MatchesRoot(r, identifiers))
                .Select(r => $"{project.Name}.csproj -> {r}"));
        }

        Assert.True(violations.Count == 0, $"LEGACY_SALES_OPS_DEPENDENCY_VIOLATION: {string.Join("; ", violations)}");
    }

    [Theory]
    [InlineData("BusinessOS.Domain", true)]
    [InlineData("BusinessOS.Infrastructure", true)]
    [InlineData("Kumpara.Web", true)]
    [InlineData("Commercial.Connectors.Kumpara", false)] // future connector: governed by A002/A009/A018, talks HTTP only
    [InlineData("BusinessOSX", false)]
    public void Root_matching_is_exact_on_assembly_segments(string name, bool expected) =>
        Assert.Equal(expected, MatchesRoot(name, KumparaInternalRoots));

    private static bool MatchesRoot(string name, IEnumerable<string> roots) =>
        roots.Any(r => name.Equals(r, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(r + ".", StringComparison.OrdinalIgnoreCase));

    private static string[] LoadLegacyIdentifiers()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Repository.PathOf("tests/Commercial.ArchitectureTests/Data/legacy-sales-ops-identifiers.json")));
        return doc.RootElement.GetProperty("rootIdentifiers").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }
}
