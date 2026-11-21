namespace LightweightIDE.DotNet.SolutionDiscovery;

public sealed record SolutionProjectEntry(string Name, string Path, string? FolderPath);

public sealed record SolutionFolderEntry(string Name, string FolderPath, string? ParentPath, IReadOnlyList<string> Files);

/// <summary>Solution contents independent of the file format (.sln or .slnx).</summary>
public sealed record SolutionModelInfo(string Path, IReadOnlyList<SolutionFolderEntry> Folders, IReadOnlyList<SolutionProjectEntry> Projects)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;
}

/// <summary>What was found in an opened folder.</summary>
public sealed record WorkspaceRoot(string Folder, IReadOnlyList<string> SolutionFiles, SolutionModelInfo? Solution, IReadOnlyList<string> LooseProjects)
{
    /// <summary>The path dotnet build/test should target: the solution, the single project, or the folder.</summary>
    public string BuildTarget => Solution?.Path ?? (LooseProjects.Count == 1 ? LooseProjects[0] : Folder);
}
