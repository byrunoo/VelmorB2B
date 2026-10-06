using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Commercial.ArchitectureTests.Support;

/// <summary>Repository locations and production assembly handles. Deterministic, no network.</summary>
public static class Repository
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    public static string Root => RootLazy.Value;

    public static string PathOf(params string[] segments) => Path.Combine([Root, .. segments]);

    public static readonly Assembly Domain = typeof(Commercial.Domain.AssemblyMarker).Assembly;
    public static readonly Assembly Application = typeof(Commercial.Application.AssemblyMarker).Assembly;
    public static readonly Assembly Infrastructure = typeof(Commercial.Infrastructure.DependencyInjection).Assembly;
    public static readonly Assembly Api = typeof(Commercial.Api.ApiAssemblyMarker).Assembly;
    public static readonly Assembly Worker = typeof(Commercial.Worker.WorkerAssemblyMarker).Assembly;
    public static readonly Assembly ConnectorAbstractions = typeof(Commercial.Connectors.Abstractions.AssemblyMarker).Assembly;

    public static IReadOnlyList<Assembly> ProductionAssemblies { get; } =
        [Domain, Application, Infrastructure, Api, Worker, ConnectorAbstractions];

    /// <summary>All project files under src/ and tests/.</summary>
    public static IReadOnlyList<ProjectFile> Projects => ProjectsLazy.Value;

    public static IEnumerable<ProjectFile> ProductionProjects =>
        Projects.Where(p => p.IsProduction);

    private static readonly Lazy<IReadOnlyList<ProjectFile>> ProjectsLazy = new(() =>
        new[] { "src", "tests" }
            .Select(d => PathOf(d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
            .Where(p => !IsGenerated(p))
            .Select(ProjectFile.Load)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList());

    public static bool IsGenerated(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal)
            || normalized.Contains("/node_modules/", StringComparison.Ordinal)
            || normalized.Contains("/dist/", StringComparison.Ordinal);
    }

    /// <summary>Enumerates text files under <paramref name="relativeDir"/> skipping build output and dependencies.</summary>
    public static IEnumerable<string> TextFiles(string relativeDir, params string[] extensions)
    {
        var dir = PathOf(relativeDir);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !IsGenerated(f))
            .Where(f => extensions.Length == 0 || extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    public static string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Commercial.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root (Commercial.slnx) not found from " + AppContext.BaseDirectory);
    }
}

public sealed record ProjectFile(
    string Name,
    string FullPath,
    bool IsProduction,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> AssemblyReferences)
{
    public static ProjectFile Load(string path)
    {
        var doc = XDocument.Load(path);
        var dir = Path.GetDirectoryName(path)!;
        var relative = Repository.Relative(path);

        return new ProjectFile(
            Path.GetFileNameWithoutExtension(path),
            path,
            relative.StartsWith("src/", StringComparison.Ordinal),
            doc.Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .OfType<string>()
                .Select(i => Path.GetFileNameWithoutExtension(Path.GetFullPath(Path.Combine(dir, i.Replace('\\', Path.DirectorySeparatorChar)))))
                .ToList(),
            doc.Descendants("PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
                .OfType<string>()
                .ToList(),
            doc.Descendants("Reference")
                .Select(e => (string?)e.Attribute("Include"))
                .OfType<string>()
                .ToList());
    }
}

public static partial class SourceText
{
    /// <summary>Identifier-like words and string literal contents of a source file.</summary>
    public static IEnumerable<string> Words(string text) =>
        WordRegex().Matches(text).Select(m => m.Value);

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_]*")]
    private static partial Regex WordRegex();
}
