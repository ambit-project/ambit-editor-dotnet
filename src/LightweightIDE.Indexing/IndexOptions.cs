namespace LightweightIDE.Indexing;

/// <summary>Indexing settings, bound from the "Indexing" configuration section.</summary>
public sealed class IndexOptions
{
    /// <summary>Files whose text goes into the full-text index.</summary>
    public string[] ContentExtensions { get; set; } =
    [
        ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".json", ".xml", ".config", ".md", ".txt", ".yml", ".yaml",
        ".razor", ".cshtml", ".sql", ".http", ".editorconfig", ".resx", ".proto", ".sh", ".ps1", ".css", ".ts", ".js", ".html",
    ];

    /// <summary>Bigger files are listed (Quick Open) but their text is not indexed.</summary>
    public int MaxContentFileSizeKb { get; set; } = 1024;

    /// <summary>Delay after the last filesystem event before re-indexing changed files.</summary>
    public int WatcherDebounceMs { get; set; } = 400;
}
