namespace LightweightIDE.Language.RoslynWorkspace;

/// <summary>Language service settings, bound from the "Language" configuration section.</summary>
public sealed class LanguageOptions
{
    /// <summary>Load the MSBuild workspace automatically after opening a folder. When false it loads on first use.</summary>
    public bool LoadWorkspaceOnOpen { get; set; } = true;
    /// <summary>Delay after the last keystroke before live diagnostics are computed.</summary>
    public int DiagnosticsDelayMs { get; set; } = 600;
    /// <summary>MSBuild global properties used for design-time evaluation.</summary>
    public string Configuration { get; set; } = "Debug";
    public int MaxReferences { get; set; } = 5000;
}
