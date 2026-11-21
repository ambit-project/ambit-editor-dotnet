namespace LightweightIDE.App.Application;

/// <summary>XDG locations: settings in ~/.config, state in ~/.local/state, logs next to state.</summary>
public static class AppPaths
{
    const string Name = "lightweight-ide";

    static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);

    public static string ConfigDirectory => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), Name);
    public static string StateDirectory => Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), Name);
    public static string UserSettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public static string StateFile => Path.Combine(StateDirectory, "state.json");
    public static string LogDirectory => Path.Combine(StateDirectory, "logs");
}
