namespace LightweightIDE.DotNet.ProjectSystem;

public enum FileEntryKind { Directory, File }

public sealed record FileEntry(string Path, string Name, FileEntryKind Kind);

/// <summary>
/// Lazy, one-level-at-a-time directory listing for the explorer and the indexer. Never walks the whole tree on its own.
/// </summary>
public static class FileTree
{
    /// <summary>Directories that never show up in browsing or indexing.</summary>
    public static readonly HashSet<string> ExcludedDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", ".git", ".vs", ".vscode", ".idea", "node_modules", "packages", "TestResults",
        "artifacts", ".svn", ".hg", "__pycache__", ".angular", ".next", "dist", "coverage",
    };

    public static bool IsExcludedDirectoryName(string name) => ExcludedDirectories.Contains(name);

    /// <summary>True when any segment of <paramref name="path"/> below <paramref name="root"/> is an excluded directory.</summary>
    public static bool IsInExcludedDirectory(string root, string path)
    {
        var relative = System.IO.Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return true;
        var segments = relative.Split(System.IO.Path.DirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 1; i++)
            if (IsExcludedDirectoryName(segments[i])) return true;
        return false;
    }

    public static IReadOnlyList<FileEntry> ListChildren(string directory, bool showHidden = false)
    {
        var result = new List<FileEntry>();
        DirectoryInfo dir = new(directory);
        if (!dir.Exists) return result;
        try
        {
            foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                var hidden = info.Name.StartsWith('.');
                if (info is DirectoryInfo d)
                {
                    if (IsExcludedDirectoryName(d.Name) || (hidden && !showHidden)) continue;
                    if (d.LinkTarget is not null) continue; // do not follow symlinked directories (cycles)
                    result.Add(new FileEntry(d.FullName, d.Name, FileEntryKind.Directory));
                }
                else if (!hidden || showHidden || info.Name is ".editorconfig" or ".gitignore")
                {
                    result.Add(new FileEntry(info.FullName, info.Name, FileEntryKind.File));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        result.Sort(static (a, b) => a.Kind != b.Kind
            ? a.Kind == FileEntryKind.Directory ? -1 : 1
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Walks all non-excluded files below root (used by the background indexer, not by the UI).</summary>
    public static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken = default)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(current).EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var info in entries)
            {
                if (info is DirectoryInfo d)
                {
                    if (IsExcludedDirectoryName(d.Name) || d.Name.StartsWith('.') || d.LinkTarget is not null) continue;
                    stack.Push(d.FullName);
                }
                else yield return info.FullName;
            }
        }
    }
}
