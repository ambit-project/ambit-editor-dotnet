namespace LightweightIDE.Editor;

/// <summary>Editor settings, bound from the "Editor" configuration section.</summary>
public sealed class EditorOptions
{
    public int TabSize { get; set; } = 4;
    public bool InsertSpaces { get; set; } = true;
    public string FontFamily { get; set; } = "Monospace";
    public int FontSize { get; set; } = 11;
    public bool ShowWhitespace { get; set; }
    public bool WrapLines { get; set; }
    /// <summary>Files bigger than this open without semantic analysis.</summary>
    public int LargeFileThresholdKb { get; set; } = 2048;
    /// <summary>Color identifiers by what they are (class, interface, method, local, parameter, field, static …).</summary>
    public bool SemanticHighlighting { get; set; } = true;
    /// <summary>Highlight the other occurrences of the symbol under the caret.</summary>
    public bool HighlightUsages { get; set; } = true;
    /// <summary>Show unused usings, private members, parameters and values faded (IDE0005, IDE0051/52, IDE0059/60).</summary>
    public bool FadeUnusedCode { get; set; } = true;
    /// <summary>Parameter names before literal arguments and inferred types after var / lambda parameters (View → Inlay Hints).</summary>
    public bool InlayHints { get; set; } = true;
}
