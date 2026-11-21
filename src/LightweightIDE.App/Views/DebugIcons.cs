namespace LightweightIDE.App.Views;

/// <summary>
/// Gutter icons for breakpoints and the current line. Symbolic theme icons take the text colour, so these are small
/// coloured SVGs written once to the cache folder and loaded as pixbufs.
/// </summary>
public static class DebugIcons
{
    const string Breakpoint = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><circle cx="8" cy="8" r="6" fill="#e5484d"/></svg>
        """;

    const string Disabled = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><circle cx="8" cy="8" r="5.5" fill="none" stroke="#e5484d" stroke-width="2"/></svg>
        """;

    const string Current = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><path d="M2 5h6V1.5L15 8l-7 6.5V11H2z" fill="#f5b400" stroke="#8a6500" stroke-width="0.8"/></svg>
        """;

    const string Send = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><path d="M4 2.5v11l9-5.5z" fill="#2f9e44"/></svg>
        """;

    const string TestRun = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><path d="M4.5 3v10l8-5z" fill="none" stroke="#2f9e44" stroke-width="1.6" stroke-linejoin="round"/></svg>
        """;

    const string TestPassed = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><circle cx="8" cy="8" r="7" fill="#2f9e44"/><path d="M6.2 4.6v6.8l5.4-3.4z" fill="#fff"/></svg>
        """;

    const string TestFailed = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><circle cx="8" cy="8" r="7" fill="#e5484d"/><path d="M6.2 4.6v6.8l5.4-3.4z" fill="#fff"/></svg>
        """;

    const string Conditional = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><circle cx="8" cy="8" r="6" fill="#e5484d"/><rect x="4.8" y="6" width="6.4" height="1.5" rx=".5" fill="#fff"/><rect x="4.8" y="8.6" width="6.4" height="1.5" rx=".5" fill="#fff"/></svg>
        """;

    const string Logpoint = """
        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16"><path d="M8 1.8 14.2 8 8 14.2 1.8 8z" fill="#e5484d"/></svg>
        """;

    static readonly Dictionary<string, GdkPixbuf.Pixbuf?> cache = [];

    public static GdkPixbuf.Pixbuf? BreakpointIcon => Load("breakpoint", Breakpoint);
    public static GdkPixbuf.Pixbuf? UnverifiedIcon => Load("breakpoint-unverified", Disabled);
    public static GdkPixbuf.Pixbuf? ConditionalIcon => Load("breakpoint-conditional", Conditional);
    public static GdkPixbuf.Pixbuf? LogpointIcon => Load("breakpoint-log", Logpoint);
    public static GdkPixbuf.Pixbuf? CurrentLineIcon => Load("current-line", Current);
    public static GdkPixbuf.Pixbuf? SendIcon => Load("http-send", Send);
    public static GdkPixbuf.Pixbuf? TestRunIcon => Load("test-run", TestRun);
    public static GdkPixbuf.Pixbuf? TestPassedIcon => Load("test-passed", TestPassed);
    public static GdkPixbuf.Pixbuf? TestFailedIcon => Load("test-failed", TestFailed);

    static GdkPixbuf.Pixbuf? Load(string name, string svg)
    {
        if (cache.TryGetValue(name, out var cached)) return cached;
        GdkPixbuf.Pixbuf? pixbuf = null;
        try
        {
            var dir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "lightweight-ide", "icons");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, name + ".svg");
            if (!File.Exists(file) || File.ReadAllText(file) != svg) File.WriteAllText(file, svg);
            pixbuf = GdkPixbuf.Pixbuf.NewFromFileAtScale(file, 16, 16, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GLib.GException) { }
        cache[name] = pixbuf;
        return pixbuf;
    }
}
