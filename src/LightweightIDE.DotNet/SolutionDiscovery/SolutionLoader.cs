using LightweightIDE.DotNet.ProjectSystem;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace LightweightIDE.DotNet.SolutionDiscovery;

/// <summary>Finds and reads .sln / .slnx files with Microsoft.VisualStudio.SolutionPersistence (the parser the dotnet CLI uses).</summary>
public static class SolutionLoader
{
    public static async Task<WorkspaceRoot> DiscoverAsync(string folder, string? preferredSolution = null, CancellationToken ct = default)
    {
        folder = Path.GetFullPath(folder);
        var solutions = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();

        var chosen = preferredSolution is not null && solutions.Contains(preferredSolution) ? preferredSolution
            : solutions.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s).Equals(Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase))
              ?? solutions.FirstOrDefault();

        if (chosen is not null)
            return new WorkspaceRoot(folder, solutions, await LoadAsync(chosen, ct), []);

        // No solution file: look for projects a few levels deep (bounded, skipping excluded directories).
        var projects = FindProjects(folder, maxDepth: 4).ToList();
        return new WorkspaceRoot(folder, solutions, null, projects);
    }

    public static async Task<SolutionModelInfo> LoadAsync(string solutionPath, CancellationToken ct = default)
    {
        solutionPath = Path.GetFullPath(solutionPath);
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath)
                         ?? throw new NotSupportedException($"Unsupported solution format: {solutionPath}");
        var model = await serializer.OpenAsync(solutionPath, ct).ConfigureAwait(false);
        var dir = Path.GetDirectoryName(solutionPath)!;

        var folders = model.SolutionFolders
            .Select(f => new SolutionFolderEntry(f.Name, f.Path, f.Parent?.Path,
                (f.Files ?? []).Select(file => Path.GetFullPath(Normalize(file), dir)).ToList()))
            .ToList();
        var projects = model.SolutionProjects
            .Select(p => new SolutionProjectEntry(p.ActualDisplayName, Path.GetFullPath(Normalize(p.FilePath), dir), p.Parent?.Path))
            .Where(p => p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) // C# only
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SolutionModelInfo(solutionPath, folders, projects);
    }

    static string Normalize(string path) => path.Replace('\\', Path.DirectorySeparatorChar);

    static IEnumerable<string> FindProjects(string dir, int maxDepth)
    {
        IEnumerable<string> files = [];
        try { files = Directory.EnumerateFiles(dir, "*.csproj").ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var f in files) yield return f;
        if (maxDepth == 0) yield break;
        foreach (var child in FileTree.ListChildren(dir).Where(e => e.Kind == FileEntryKind.Directory))
            foreach (var f in FindProjects(child.Path, maxDepth - 1))
                yield return f;
    }
}
