using LightweightIDE.Language.Classification;

namespace LightweightIDE.App.Views;

/// <summary>
/// The text tags of semantic highlighting for a color scheme. The IDE's own schemes (Lightweight Dark / Light) define
/// a style "lw:&lt;kind&gt;" per kind; for any other installed scheme the kind falls back to the closest standard
/// style (types → def:type, methods → def:function, constants → def:constant, control keywords → def:statement) or to
/// a built-in palette for the kinds standard schemes have no style for (locals, parameters, fields, properties).
/// </summary>
public static class SemanticColors
{
    public const string TagPrefix = "lw-";

    static readonly Dictionary<string, string> StandardFallback = new(StringComparer.Ordinal)
    {
        ["class"] = "def:type",
        ["struct"] = "def:type",
        ["interface"] = "def:type",
        ["enum"] = "def:type",
        ["delegate"] = "def:type",
        ["type-parameter"] = "def:type",
        ["method"] = "def:function",
        ["extension-method"] = "def:function",
        ["operator-overloaded"] = "def:function",
        ["constant"] = "def:constant",
        ["enum-member"] = "def:constant",
        ["control-keyword"] = "def:statement",
        ["string-escape"] = "def:special-char",
    };

    static readonly Dictionary<string, (string Dark, string Light)> Palette = new(StringComparer.Ordinal)
    {
        ["class"] = ("#4EC9B0", "#2B91AF"),
        ["struct"] = ("#86C691", "#2E8B57"),
        ["interface"] = ("#B8D7A3", "#1E7E7E"),
        ["enum"] = ("#B8D7A3", "#1E7E7E"),
        ["delegate"] = ("#4EC9B0", "#2B91AF"),
        ["type-parameter"] = ("#B8D7A3", "#1E7E7E"),
        ["method"] = ("#DCDCAA", "#74531F"),
        ["extension-method"] = ("#DCDCAA", "#74531F"),
        ["operator-overloaded"] = ("#DCDCAA", "#74531F"),
        ["constant"] = ("#4FC1FF", "#0070C1"),
        ["enum-member"] = ("#4FC1FF", "#0070C1"),
        ["control-keyword"] = ("#C586C0", "#AF00DB"),
        ["string-escape"] = ("#D7BA7D", "#EE0000"),
        ["local"] = ("#9CDCFE", "#1F377F"),
        ["parameter"] = ("#7FB7E8", "#2F6DB5"),
        ["property"] = ("#C8B4F5", "#7A3E9D"),
        ["field"] = ("#A9A1D8", "#8E5BB5"),
        ["event"] = ("#E8C07D", "#A35D00"),
    };

    /// <summary>Schemes with a dark background among the ones GtkSourceView ships (ids).</summary>
    public static bool IsDark(GtkSource.StyleScheme? scheme)
    {
        var id = scheme?.GetId() ?? "";
        return id.Contains("dark", StringComparison.OrdinalIgnoreCase) || id is "oblivion" or "cobalt";
    }

    /// <summary>Creates (or recreates) one tag per kind in the buffer's tag table, styled for the scheme.</summary>
    public static Dictionary<string, Gtk.TextTag> CreateTags(Gtk.TextTagTable table, GtkSource.StyleScheme? scheme)
    {
        var dark = IsDark(scheme);
        var tags = new Dictionary<string, Gtk.TextTag>(StringComparer.Ordinal);
        foreach (var kind in SemanticHighlighter.Kinds)
        {
            if (table.Lookup(TagPrefix + kind) is { } old) table.Remove(old);
            var tag = Gtk.TextTag.New(TagPrefix + kind);
            if (scheme?.GetStyle("lw:" + kind) is { } own) own.Apply(tag);
            else if (kind == "static") tag.Style = Pango.Style.Italic;
            else if (StandardFallback.TryGetValue(kind, out var standard) && scheme?.GetStyle(standard) is { } style) style.Apply(tag);
            else if (Palette.TryGetValue(kind, out var colors)) tag.Foreground = dark ? colors.Dark : colors.Light;
            table.Add(tag);
            tags[kind] = tag;
        }
        return tags;
    }

    /// <summary>Tags for the occurrences of the symbol under the caret and for unused (faded) code.</summary>
    public sealed record DecorTags(Gtk.TextTag Usage, Gtk.TextTag UsageDefinition, Gtk.TextTag Unnecessary);

    /// <summary>
    /// Creates (or recreates) the decoration tags. Call after <see cref="CreateTags"/>: a tag created later wins, and
    /// faded code must override the semantic colors.
    /// </summary>
    public static DecorTags CreateDecorTags(Gtk.TextTagTable table, GtkSource.StyleScheme? scheme)
    {
        var dark = IsDark(scheme);
        Gtk.TextTag Make(string kind, Action<Gtk.TextTag> fallback)
        {
            if (table.Lookup(TagPrefix + kind) is { } old) table.Remove(old);
            var tag = Gtk.TextTag.New(TagPrefix + kind);
            if (scheme?.GetStyle("lw:" + kind) is { } own) own.Apply(tag);
            else fallback(tag);
            table.Add(tag);
            return tag;
        }
        return new DecorTags(
            Make("usage", t => t.Background = dark ? "#45484D" : "#DCDCDC"),
            Make("usage-definition", t => t.Background = dark ? "#0F4166" : "#CCE0F0"),
            Make("unnecessary", t => t.Foreground = dark ? "#7A7A7A" : "#A0A0A0"));
    }
}
