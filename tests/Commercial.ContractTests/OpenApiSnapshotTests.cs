using System.Text.Json;
using System.Text.Json.Nodes;

namespace Commercial.ContractTests;

/// <summary>
/// Phase 0 contract test #1 — the Commercial API OpenAPI document must match the reviewed snapshot.
/// Intentional contract changes: run with UPDATE_OPENAPI_SNAPSHOT=1, review and commit the diff.
/// </summary>
public sealed class OpenApiSnapshotTests : IClassFixture<ContractApiFactory>
{
    private const string SnapshotRelativePath = "tests/Commercial.ContractTests/Snapshots/openapi-v1.json";
    private readonly ContractApiFactory _factory;

    public OpenApiSnapshotTests(ContractApiFactory factory) => _factory = factory;

    [Fact]
    public async Task OpenApi_document_matches_reviewed_snapshot()
    {
        using var client = _factory.CreateClient();
        var json = await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var actual = Normalize(json);

        var snapshotPath = Path.Combine(RepositoryRoot(), SnapshotRelativePath);
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            await File.WriteAllTextAsync(snapshotPath, actual, TestContext.Current.CancellationToken);
            return;
        }

        if (!File.Exists(snapshotPath))
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(snapshotPath, ".received.json"), actual, TestContext.Current.CancellationToken);
            Assert.Fail($"No OpenAPI snapshot at {SnapshotRelativePath}. Review the .received.json file and re-run with UPDATE_OPENAPI_SNAPSHOT=1.");
        }

        var expected = Normalize(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken));
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(snapshotPath, ".received.json"), actual, TestContext.Current.CancellationToken);
            Assert.Fail(
                $"OpenAPI contract changed. Diff {SnapshotRelativePath} against openapi-v1.received.json; " +
                "if the change is intentional, re-run with UPDATE_OPENAPI_SNAPSHOT=1 and commit the reviewed snapshot.");
        }
    }

    [Fact]
    public async Task OpenApi_document_declares_the_error_envelope_and_v1_paths()
    {
        using var client = _factory.CreateClient();
        var doc = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!;

        Assert.Equal("v1", doc["info"]!["version"]!.GetValue<string>());
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.All(paths, p => Assert.StartsWith("/api/v1/", p, StringComparison.Ordinal));
        Assert.NotNull(doc["components"]?["schemas"]?["ErrorEnvelope"]);
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ReplaceLineEndings("\n") + "\n";

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commercial.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
