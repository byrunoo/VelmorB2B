using System.Text.RegularExpressions;
using Commercial.ArchitectureTests.Support;

namespace Commercial.ArchitectureTests;

/// <summary>
/// A015 (hard-coded secrets — narrow repository scan; CI additionally runs gitleaks),
/// A016 (frontend cross-app imports) and A017 (frontend provider bypass).
/// ESLint enforces A016/A017 in the editor and in CI; these checks keep the guarantee in the
/// fast .NET architecture suite as well.
/// </summary>
public sealed partial class SecretAndFrontendRules
{
    private static readonly string[] ScannedRoots = ["src", "tests", "frontend", "scripts", ".github"];

    private static readonly string[] RootFiles =
        ["docker-compose.yml", "Directory.Build.props", "Directory.Packages.props", "global.json", "CLAUDE.md", "README.md"];

    private static readonly string[] TextExtensions =
        [".cs", ".json", ".yml", ".yaml", ".ts", ".tsx", ".js", ".mjs", ".cjs", ".html", ".props", ".csproj", ".ps1", ".sh", ".md", ".env", ".example", ".config", ".xml"];

    // ---------------- A015 ----------------

    [Fact]
    public void A015_Repository_must_not_contain_hardcoded_secrets()
    {
        var files = ScannedRoots.SelectMany(r => Repository.TextFiles(r))
            .Concat(RootFiles.Select(f => Repository.PathOf(f)).Where(File.Exists))
            .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                || Path.GetFileName(f).StartsWith(".env", StringComparison.Ordinal));

        var findings = files
            .SelectMany(f => SecretFindings(File.ReadAllText(f)).Select(kind => $"{Repository.Relative(f)}: {kind}"))
            .ToArray();

        // Finding text never echoes the secret value itself.
        Assert.True(findings.Length == 0, $"HARDCODED_SECRET_DETECTED: {string.Join("; ", findings)}");
    }

    [Theory]
    [InlineData("Host=db;Database=x;Username=u;Pass" + "word=Sup3rS3cret!", true)]
    [InlineData("\"ClientSec" + "ret\": \"abcDEF1234567890\"", true)]
    [InlineData("-----BEGIN RSA PRIV" + "ATE KEY-----", true)]
    [InlineData("AKIA" + "ABCDEFGHIJKLMNOP", true)]
    [InlineData("Host=localhost;Port=5432;Database=commercial;Username=commercial", false)]
    [InlineData("\"ApiKey\": \"\"", false)]
    [InlineData("Pass" + "word=${COMMERCIAL_DB_PASSWORD}", false)]
    [InlineData("\"Host=db;Pass" + "word=abc123\"", true)]
    [InlineData("\"Pass" + "word=abc123;Host=db\"", true)]
    [InlineData("Server=db; Pass" + "word = abc123", true)]
    [InlineData("builder.Pass" + "word = password;", false)]
    public void A015_Secret_scanner_detects_and_ignores_expected_shapes(string text, bool expected) =>
        Assert.Equal(expected, SecretFindings(text).Any());

    private static IEnumerable<string> SecretFindings(string text)
    {
        foreach (Match m in ConnectionStringPassword().Matches(text))
        {
            var value = m.Groups["value"].Value;
            if (!IsPlaceholder(value))
            {
                yield return "connection-string password";
            }
        }

        foreach (Match m in JsonSecretKey().Matches(text))
        {
            if (!IsPlaceholder(m.Groups["value"].Value))
            {
                yield return $"secret-like setting '{m.Groups["key"].Value}'";
            }
        }

        if (PrivateKey().IsMatch(text))
        {
            yield return "private key block";
        }

        if (CloudToken().IsMatch(text))
        {
            yield return "cloud/API token pattern";
        }
    }

    private static bool IsPlaceholder(string value) =>
        value.Length == 0
        || value.StartsWith('$')
        || value.StartsWith('<')
        || value.StartsWith('{')
        || value.StartsWith('%')
        || value.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
        || value.Contains("changeme", StringComparison.OrdinalIgnoreCase);

    // Connection-string context only: the key starts the string or follows ';' / a quote. A C# member
    // assignment such as `builder.Password = secret` is not a connection string and is not matched.
    [GeneratedRegex(@"(?i)(?:^|[;""'])\s*(?:password|pwd)\s*=\s*(?<value>[^;""'\s]*)", RegexOptions.Multiline)]
    private static partial Regex ConnectionStringPassword();

    [GeneratedRegex(@"""(?<key>(?i:[A-Za-z]*(?:password|secret|apikey|api_key|clientsecret|accesskey|token)))""\s*:\s*""(?<value>[^""]*)""")]
    private static partial Regex JsonSecretKey();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{36}|xox[abprs]-[A-Za-z0-9-]{10,}|sk_live_[A-Za-z0-9]{16,}|kp_live_[A-Za-z0-9]{16,})\b")]
    private static partial Regex CloudToken();

    // ---------------- A016 ----------------

    [Fact]
    public void A016_Frontend_apps_must_not_import_each_other()
    {
        var violations = CrossAppImports("admin", "b2b").Concat(CrossAppImports("b2b", "admin")).ToArray();
        Assert.True(violations.Length == 0, $"FRONTEND_CROSS_APP_IMPORT: {string.Join("; ", violations)}");
    }

    [Fact]
    public void A016_Shared_package_must_not_import_apps()
    {
        var violations = Repository.TextFiles("frontend/shared/src", ".ts", ".tsx")
            .SelectMany(f => ImportSpecifiers(File.ReadAllText(f))
                .Where(s => Regex.IsMatch(s, @"(^|/)(admin|b2b)(/|$)") || s.StartsWith("@commercial/admin", StringComparison.Ordinal) || s.StartsWith("@commercial/b2b", StringComparison.Ordinal))
                .Select(s => $"{Repository.Relative(f)} imports '{s}'"))
            .ToArray();
        Assert.True(violations.Length == 0, $"FRONTEND_CROSS_APP_IMPORT: {string.Join("; ", violations)}");
    }

    private static IEnumerable<string> CrossAppImports(string app, string other) =>
        Repository.TextFiles($"frontend/{app}", ".ts", ".tsx", ".js", ".mjs")
            .SelectMany(f => ImportSpecifiers(File.ReadAllText(f))
                .Where(s => s.StartsWith($"@commercial/{other}", StringComparison.Ordinal)
                    || Regex.IsMatch(s, $@"^(\.\./)+{other}(/|$)")
                    || s.Contains($"frontend/{other}", StringComparison.Ordinal))
                .Select(s => $"{Repository.Relative(f)} imports '{s}'"));

    // ---------------- A017 ----------------

    [Fact]
    public void A017_Frontend_must_not_reference_external_provider_endpoints()
    {
        // Everything that ships to (or configures) the browser: application source, HTML entry points
        // and VITE_* env files. Node-side tooling (eslint/vite config) is not browser code.
        var sources = new[] { "frontend/admin", "frontend/b2b", "frontend/shared" }
            .SelectMany(d => Repository.TextFiles(d + "/src", ".ts", ".tsx", ".js", ".mjs", ".json", ".css")
                .Concat(Directory.Exists(Repository.PathOf(d))
                    ? Directory.EnumerateFiles(Repository.PathOf(d), "*", SearchOption.TopDirectoryOnly)
                        .Where(f => Path.GetFileName(f).StartsWith(".env", StringComparison.Ordinal)
                            || Path.GetExtension(f).Equals(".html", StringComparison.OrdinalIgnoreCase))
                    : []))
            .Distinct()
            .ToList();
        Assert.NotEmpty(sources);

        var providerHits = sources
            .SelectMany(f => SourceText.Words(File.ReadAllText(f)).Select(w => (File: f, Match: ProviderTokens.Find(w))))
            .Where(x => x.Match is not null)
            .Select(x => $"{Repository.Relative(x.File)} -> {x.Match}")
            .Distinct();

        // Browser code may only call the Commercial API via the shared client using a configured
        // (relative or env-provided) base URL — no absolute URLs in application source.
        var absoluteUrls = sources
            .Where(f => Repository.Relative(f).Contains("/src/", StringComparison.Ordinal))
            .SelectMany(f => AbsoluteUrl().Matches(File.ReadAllText(f)).Select(m => $"{Repository.Relative(f)} -> {m.Value}"));

        var violations = providerHits.Concat(absoluteUrls).ToArray();
        Assert.True(violations.Length == 0, $"FRONTEND_EXTERNAL_PROVIDER_BYPASS: {string.Join("; ", violations)}");
    }

    private static IEnumerable<string> ImportSpecifiers(string source) =>
        ImportRegex().Matches(source).Select(m => m.Groups["spec"].Value);

    [GeneratedRegex(@"(?:import|export)\s[^'""]*?from\s*['""](?<spec>[^'""]+)['""]|import\s*\(\s*['""](?<spec>[^'""]+)['""]\s*\)|require\(\s*['""](?<spec>[^'""]+)['""]\s*\)")]
    private static partial Regex ImportRegex();

    [GeneratedRegex(@"https?://(?!www\.w3\.org/)[A-Za-z0-9.-]+")]
    private static partial Regex AbsoluteUrl();
}
