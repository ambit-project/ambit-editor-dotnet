using System.Collections.Concurrent;

namespace LightweightIDE.DotNet.ProjectSystem;

/// <summary>Maps a file to the nearest .csproj above it. Results are cached per directory.</summary>
public sealed class ProjectLocator(string root)
{
    readonly ConcurrentDictionary<string, string?> cache = new(StringComparer.Ordinal);

    public string? FindProjectFile(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        return dir is null ? null : FindForDirectory(dir);
    }

    public string? FindProjectName(string filePath) =>
        FindProjectFile(filePath) is { } p ? Path.GetFileNameWithoutExtension(p) : null;

    string? FindForDirectory(string dir)
    {
        if (cache.TryGetValue(dir, out var cached)) return cached;
        string? found = null;
        try
        {
            found = Directory.EnumerateFiles(dir, "*.csproj").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (found is null && !string.Equals(dir.TrimEnd('/'), root.TrimEnd('/'), StringComparison.Ordinal))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && parent.StartsWith(root, StringComparison.Ordinal)) found = FindForDirectory(parent);
        }
        cache[dir] = found;
        return found;
    }

    public void Invalidate() => cache.Clear();
}
