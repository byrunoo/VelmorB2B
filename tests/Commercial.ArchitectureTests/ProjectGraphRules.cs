using Commercial.ArchitectureTests.Support;

namespace Commercial.ArchitectureTests;

/// <summary>A009 (connector isolation), A011 (no cycles), A012 (no production -> test dependency).</summary>
public sealed class ProjectGraphRules
{
    private static readonly string[] TestPackages =
        ["xunit", "xunit.v3", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk", "NetArchTest.Rules", "Microsoft.AspNetCore.Mvc.Testing", "Testcontainers", "JsonSchema.Net", "Microsoft.Extensions.TimeProvider.Testing", "OpenTelemetry.Exporter.InMemory"];

    [Fact]
    public void A009_Connector_implementations_must_not_reference_each_other()
    {
        var graph = Repository.Projects.ToDictionary(p => p.Name, p => p.ProjectReferences);
        var violations = ConnectorCrossReferences(graph).ToArray();
        Assert.True(violations.Length == 0, $"CONNECTOR_CROSS_DEPENDENCY_VIOLATION: {string.Join(", ", violations)}");
    }

    [Fact]
    public void A009_Rule_detects_synthetic_cross_reference()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Commercial.Connectors.Kumpara"] = ["Commercial.Connectors.Abstractions", "Commercial.Connectors.LogoTiger"],
            ["Commercial.Connectors.LogoTiger"] = ["Commercial.Connectors.Abstractions"],
            ["Commercial.Connectors.Abstractions"] = [],
        };
        Assert.Equal(["Commercial.Connectors.Kumpara -> Commercial.Connectors.LogoTiger"], ConnectorCrossReferences(graph));
    }

    [Fact]
    public void A011_Production_project_references_must_be_acyclic()
    {
        var graph = Repository.Projects.ToDictionary(p => p.Name, p => p.ProjectReferences);
        var cycle = FindCycle(graph);
        Assert.True(cycle is null, $"CIRCULAR_PROJECT_DEPENDENCY: {string.Join(" -> ", cycle ?? [])}");
    }

    [Fact]
    public void A011_Cycle_detector_detects_synthetic_cycle()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>> { ["A"] = ["B"], ["B"] = ["C"], ["C"] = ["A"] };
        Assert.NotNull(FindCycle(graph));
    }

    [Fact]
    public void A012_Production_projects_must_not_reference_tests()
    {
        var testProjects = Repository.Projects.Where(p => !p.IsProduction).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var project in Repository.ProductionProjects)
        {
            violations.AddRange(project.ProjectReferences
                .Where(r => testProjects.Contains(r) || r.EndsWith("Tests", StringComparison.Ordinal))
                .Select(r => $"{project.Name} -> project {r}"));
            violations.AddRange(project.PackageReferences
                .Where(p => TestPackages.Any(t => p.Equals(t, StringComparison.OrdinalIgnoreCase) || p.StartsWith(t + ".", StringComparison.OrdinalIgnoreCase)))
                .Select(p => $"{project.Name} -> package {p}"));
        }

        foreach (var assembly in Repository.ProductionAssemblies)
        {
            violations.AddRange(assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.EndsWith("Tests", StringComparison.Ordinal) || n.StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
                .Select(n => $"{assembly.GetName().Name} -> assembly {n}"));
        }

        Assert.True(violations.Count == 0, $"PRODUCTION_TEST_DEPENDENCY_VIOLATION: {string.Join("; ", violations)}");
    }

    private static IEnumerable<string> ConnectorCrossReferences(IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        static bool IsImplementation(string name) =>
            name.StartsWith("Commercial.Connectors.", StringComparison.Ordinal) && name != "Commercial.Connectors.Abstractions";

        return graph
            .Where(kv => IsImplementation(kv.Key))
            .SelectMany(kv => kv.Value.Where(IsImplementation).Select(r => $"{kv.Key} -> {r}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string>? FindCycle(IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 new, 1 visiting, 2 done
        var stack = new List<string>();

        List<string>? Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var next in graph.TryGetValue(node, out var refs) ? refs : [])
            {
                var s = state.GetValueOrDefault(next);
                if (s == 1)
                {
                    return [.. stack.Skip(stack.IndexOf(next)), next];
                }

                if (s == 0 && Visit(next) is { } found)
                {
                    return found;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
            return null;
        }

        foreach (var node in graph.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(node) == 0 && Visit(node) is { } cycle)
            {
                return cycle;
            }
        }

        return null;
    }
}
