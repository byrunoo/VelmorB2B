using System.Text.Json;
using Commercial.ArchitectureTests.Support;
using Commercial.Infrastructure.BackgroundJobs;

namespace Commercial.ArchitectureTests;

/// <summary>
/// ADR-0001 database identity separation (configuration side; real privilege behaviour is proven by
/// tests/Commercial.IntegrationTests/Persistence/DatabaseIdentityTests). Failure code: DATABASE_IDENTITY_SEPARATION_VIOLATION.
/// </summary>
public sealed class DatabaseIdentityRules
{
    private const string Failure = "DATABASE_IDENTITY_SEPARATION_VIOLATION";
    private const string Maintenance = "commercial_platform_maintenance";
    private const string Owner = "commercial_platform_owner";

    private static IEnumerable<string> RuntimeConfigFiles() =>
        Repository.TextFiles("src", ".json")
            .Where(f => Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Runtime_configuration_never_contains_the_maintenance_identity()
    {
        var offending = RuntimeConfigFiles()
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains(Maintenance, StringComparison.OrdinalIgnoreCase)
                    || text.Contains("\"Maintenance\"", StringComparison.Ordinal)
                    || text.Contains("__MAINTENANCE__", StringComparison.OrdinalIgnoreCase);
            })
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: maintenance identity configured for API/Worker: {string.Join(", ", offending)}");
    }

    [Fact]
    public void Runtime_identity_is_never_the_owner()
    {
        var offending = RuntimeConfigFiles()
            .SelectMany(f => RuntimeUsernames(File.ReadAllText(f)).Select(u => (File: f, User: u)))
            .Where(x => x.User.Equals(Owner, StringComparison.OrdinalIgnoreCase) || x.User.Equals(Maintenance, StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{Repository.Relative(x.File)} -> {x.User}")
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: runtime connects as a privileged role: {string.Join(", ", offending)}");
    }

    [Fact]
    public void Runtime_launch_profiles_never_carry_migration_credentials()
    {
        var offending = Repository.TextFiles("src", ".json")
            .Where(f => Path.GetFileName(f).Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("__MIGRATION__", StringComparison.OrdinalIgnoreCase))
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: launch profiles must not configure the migration identity: {string.Join(", ", offending)}");
    }

    [Fact]
    public void Hangfire_runtime_never_prepares_its_schema()
    {
        Assert.False(new HangfireOptions().PrepareSchemaIfNecessary, $"{Failure}: HangfireOptions.PrepareSchemaIfNecessary must default to false.");
        var offending = RuntimeConfigFiles()
            .Where(f => File.ReadAllText(f).Contains("\"PrepareSchemaIfNecessary\": true", StringComparison.OrdinalIgnoreCase))
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: runtime config enables Hangfire schema preparation: {string.Join(", ", offending)}");
    }

    [Theory]
    [InlineData("{\"ConnectionStrings\":{\"Commercial\":\"Host=h;Username=commercial_platform_owner\"}}", "commercial_platform_owner")]
    [InlineData("{\"Commercial\":{\"Postgres\":{\"Runtime\":{\"Username\":\"commercial_platform_app\"}}}}", "commercial_platform_app")]
    [InlineData("{\"profiles\":{\"x\":{\"environmentVariables\":{\"COMMERCIAL__POSTGRES__RUNTIME__USERNAME\":\"commercial_platform_maintenance\"}}}}", "commercial_platform_maintenance")]
    public void Runtime_username_extraction_finds_all_sources(string json, string expected) =>
        Assert.Contains(expected, RuntimeUsernames(json));

    /// <summary>Runtime usernames from base connection strings, Runtime:Username and RUNTIME__USERNAME env entries.</summary>
    private static IEnumerable<string> RuntimeUsernames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var found = new List<string>();

        void Walk(JsonElement e, string path)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        Walk(p.Value, path + ":" + p.Name);
                    }

                    break;
                case JsonValueKind.String:
                    var value = e.GetString() ?? string.Empty;
                    if (path.EndsWith(":ConnectionStrings:Commercial", StringComparison.OrdinalIgnoreCase))
                    {
                        var user = value.Split(';').Select(s => s.Split('=', 2)).FirstOrDefault(kv => kv.Length == 2 && kv[0].Trim().Equals("Username", StringComparison.OrdinalIgnoreCase));
                        if (user is not null)
                        {
                            found.Add(user[1].Trim());
                        }
                    }
                    else if (path.EndsWith(":Runtime:Username", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(":COMMERCIAL__POSTGRES__RUNTIME__USERNAME", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(value);
                    }

                    break;
            }
        }

        Walk(doc.RootElement, string.Empty);
        return found.Where(u => u.Length > 0);
    }
}
