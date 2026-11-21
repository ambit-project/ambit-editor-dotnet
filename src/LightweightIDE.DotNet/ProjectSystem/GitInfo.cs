namespace LightweightIDE.DotNet.ProjectSystem;

/// <summary>Reads the current branch from .git/HEAD without running git.</summary>
public static class GitInfo
{
    public static string? FindGitDirectory(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(candidate)) return candidate;
            if (File.Exists(candidate))
            {
                // worktree / submodule: "gitdir: <path>"
                var line = File.ReadAllText(candidate).Trim();
                if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                    return Path.GetFullPath(line["gitdir:".Length..].Trim(), dir.FullName);
            }
        }
        return null;
    }

    public static string? CurrentBranch(string start)
    {
        var gitDir = FindGitDirectory(start);
        if (gitDir is null) return null;
        try
        {
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            const string prefix = "ref: refs/heads/";
            return head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : head[..Math.Min(8, head.Length)];
        }
        catch (IOException) { return null; }
    }
}
