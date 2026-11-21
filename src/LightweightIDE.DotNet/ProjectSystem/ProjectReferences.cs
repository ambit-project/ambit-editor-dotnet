using System.Xml.Linq;
using LightweightIDE.DotNet.Build;

namespace LightweightIDE.DotNet.ProjectSystem;

/// <summary>Project-to-project references: read from the .csproj XML, changed with <c>dotnet add/remove reference</c>.</summary>
public static class ProjectReferences
{
    /// <summary>Full paths of the projects <paramref name="projectPath"/> references directly. Includes with MSBuild properties are skipped.</summary>
    public static IReadOnlyList<string> Read(string projectPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        try
        {
            return XDocument.Load(projectPath).Root!.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => ((string?)e.Attribute("Include"))?.Trim())
                .Where(include => !string.IsNullOrEmpty(include) && !include.Contains("$(", StringComparison.Ordinal))
                .Select(include => Path.GetFullPath(Path.Combine(directory, include!.Replace('\\', '/'))))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Package references of a project with their versions: the project's own Version / VersionOverride, else the
    /// version from Directory.Packages.props (Central Package Management). Version is null when none is found.
    /// </summary>
    public static IReadOnlyList<(string Id, string? Version)> ReadPackages(string projectPath)
    {
        try
        {
            var central = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Scaffolding.CentralPackages.FindEnabledFile(projectPath) is { } props)
                foreach (var e in XDocument.Load(props).Root!.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
                    if ((string?)e.Attribute("Include") is { } id && VersionOf(e) is { } v) central[id] = v;

            return XDocument.Load(projectPath).Root!.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include") is not null)
                .Select(e =>
                {
                    var id = ((string)e.Attribute("Include")!).Trim();
                    return (id, VersionOf(e) ?? (central.TryGetValue(id, out var v) ? v : null));
                })
                .DistinctBy(p => p.id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return [];
        }

        static string? VersionOf(XElement e) =>
            ((string?)e.Attribute("VersionOverride") ?? (string?)e.Attribute("Version")
             ?? e.Elements().FirstOrDefault(c => c.Name.LocalName is "VersionOverride" or "Version")?.Value)?.Trim() is { Length: > 0 } v ? v : null;
    }

    /// <summary>
    /// True when referencing <paramref name="candidate"/> from <paramref name="project"/> closes a cycle:
    /// the candidate is the project itself or already depends on it, directly or transitively.
    /// </summary>
    public static bool WouldCreateCycle(string project, string candidate, Func<string, IReadOnlyList<string>>? read = null)
    {
        read ??= Read;
        project = Path.GetFullPath(project);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Path.GetFullPath(candidate)]);
        while (pending.TryPop(out var current))
        {
            if (current == project) return true;
            if (!seen.Add(current)) continue;
            foreach (var next in read(current)) pending.Push(next);
        }
        return false;
    }

    /// <summary>Adds and removes references of <paramref name="project"/> in one go (one dotnet call per direction).</summary>
    public static async Task ApplyAsync(string project, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, Action<string> output, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(project))!;
        if (remove.Count > 0) await RunAsync(["remove", project, "reference", .. remove]).ConfigureAwait(false);
        if (add.Count > 0) await RunAsync(["add", project, "reference", .. add]).ConfigureAwait(false);

        async Task RunAsync(string[] args)
        {
            output($"> dotnet {string.Join(' ', args)}");
            var exit = await ProcessRunner.RunAsync("dotnet", args, directory, (line, _) => output(line), cancellationToken: ct).ConfigureAwait(false);
            if (exit != 0) throw new InvalidOperationException($"dotnet {args[0]} reference failed (exit code {exit}). See Output.");
        }
    }
}
