using System.Text.Json;

namespace LightweightIDE.App.Application;

/// <summary>Small UI state remembered between sessions (not settings): recent folders, window and panel sizes.</summary>
public sealed class AppState
{
    public List<string> RecentFolders { get; set; } = [];
    /// <summary>Recently active files per opened folder, most recent first (Ctrl+E).</summary>
    public Dictionary<string, List<string>> RecentFiles { get; set; } = [];
    public Dictionary<string, string> PreferredSolution { get; set; } = [];
    public Dictionary<string, List<string>> OpenFiles { get; set; } = [];
    public int WindowWidth { get; set; } = 1400;
    public int WindowHeight { get; set; } = 900;
    public bool Maximized { get; set; }
    public int ExplorerWidth { get; set; } = 280;
    public int BottomPanelHeight { get; set; } = 220;
    public string BuildConfiguration { get; set; } = "Debug";
    public string? ThemeOverride { get; set; }
    /// <summary>Editor color scheme id (View → Color Theme); null = Lightweight Light / Dark following the theme.</summary>
    public string? ColorScheme { get; set; }
    /// <summary>View → Inlay Hints; null = the Editor:InlayHints setting.</summary>
    public bool? InlayHints { get; set; }
    /// <summary>Line breakpoints per file.</summary>
    public Dictionary<string, List<int>> Breakpoints { get; set; } = [];
    /// <summary>Condition / hit count / log message of breakpoints that have them, per file and line.</summary>
    public Dictionary<string, Dictionary<int, LightweightIDE.Editor.BreakpointOptions>> BreakpointOptions { get; set; } = [];
    /// <summary>The project F5 debugs, per opened folder.</summary>
    public Dictionary<string, string> StartupProject { get; set; } = [];
    /// <summary>The launch profile (launchSettings.json) per project path.</summary>
    public Dictionary<string, string> LaunchProfile { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppState Load()
    {
        try
        {
            if (File.Exists(AppPaths.StateFile))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(AppPaths.StateFile)) ?? new AppState();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new AppState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.StateDirectory);
            File.WriteAllText(AppPaths.StateFile, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
    }

    public void AddRecent(string folder)
    {
        RecentFolders.Remove(folder);
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > 12) RecentFolders.RemoveRange(12, RecentFolders.Count - 12);
    }
}
