using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commercial.ArchitectureTests.Support;

/// <summary>Reads docs/architecture/active-phase.json (the single source of the active phase).</summary>
public sealed record ActivePhase(
    [property: JsonPropertyName("phase")] int Phase,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mandate")] string Mandate)
{
    public const string RelativePath = "docs/architecture/active-phase.json";

    public static ActivePhase Load() =>
        JsonSerializer.Deserialize<ActivePhase>(File.ReadAllText(Repository.PathOf(RelativePath)))
        ?? throw new InvalidOperationException($"{RelativePath} is empty or invalid.");
}

/// <summary>
/// Phase-specific A018 scope data (tests/Commercial.ArchitectureTests/PhaseScope/phase-scope.json).
/// A phase kickoff PR advances this data together with active-phase.json.
/// </summary>
public sealed record PhaseScope
{
    public const string RelativePath = "tests/Commercial.ArchitectureTests/PhaseScope/phase-scope.json";

    [JsonPropertyName("forbiddenProjectPatterns")]
    public IReadOnlyList<string> ForbiddenProjectPatterns { get; init; } = [];

    [JsonPropertyName("forbiddenTypeNames")]
    public IReadOnlyList<string> ForbiddenTypeNames { get; init; } = [];

    [JsonPropertyName("forbiddenNamespaceSegments")]
    public IReadOnlyList<string> ForbiddenNamespaceSegments { get; init; } = [];

    [JsonPropertyName("forbiddenFrontendPathSegments")]
    public IReadOnlyList<string> ForbiddenFrontendPathSegments { get; init; } = [];

    [JsonPropertyName("externalContractTestsAllowed")]
    public bool ExternalContractTestsAllowed { get; init; }

    public static IReadOnlyDictionary<int, PhaseScope> LoadAll()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Repository.PathOf(RelativePath)));
        return doc.RootElement.GetProperty("phases").EnumerateObject().ToDictionary(
            p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
            p => p.Value.Deserialize<PhaseScope>()!);
    }
}
