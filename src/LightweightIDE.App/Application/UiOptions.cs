namespace LightweightIDE.App.Application;

public enum ThemeMode { System, Light, Dark }

/// <summary>"Ui" configuration section.</summary>
public sealed class UiOptions
{
    public string Theme { get; set; } = "system";
    public bool ShowMemoryUsage { get; set; } = true;

    public ThemeMode ThemeMode => Theme.ToLowerInvariant() switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.System,
    };
}
