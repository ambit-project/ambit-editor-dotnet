namespace LightweightIDE.App.Views;

/// <summary>Editor color schemes: the IDE's own (next to the app, Styles/schemes) plus the installed GtkSourceView ones.</summary>
public static class ColorSchemes
{
    public const string Dark = "lightweight-dark";
    public const string Light = "lightweight-light";

    static bool searchPathAdded;

    public static GtkSource.StyleSchemeManager Manager
    {
        get
        {
            var manager = GtkSource.StyleSchemeManager.GetDefault();
            if (!searchPathAdded)
            {
                searchPathAdded = true;
                var own = Path.Combine(AppContext.BaseDirectory, "Styles", "schemes");
                if (Directory.Exists(own)) manager.AppendSearchPath(own);
            }
            return manager;
        }
    }

    /// <summary>(id, display name) of every scheme, the IDE's own first.</summary>
    public static IReadOnlyList<(string Id, string Name)> All()
    {
        var manager = Manager;
        return (manager.GetSchemeIds() ?? [])
            .Select(id => (Id: id, Name: manager.GetScheme(id)?.GetName() ?? id))
            .OrderBy(s => s.Id.StartsWith("lightweight", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
