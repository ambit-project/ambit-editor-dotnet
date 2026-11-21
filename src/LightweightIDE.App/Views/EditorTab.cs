using LightweightIDE.DotNet.Build;
using LightweightIDE.Editor;
using LightweightIDE.Language.Completion;
using LightweightIDE.Language.Diagnostics;
using LightweightIDE.Language.Navigation;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Views;

public enum MarkdownViewMode { Split, Preview, Source }

/// <summary>Services an editor tab needs; one instance shared by all tabs.</summary>
public sealed record EditorServices(
    EditorOptions Options,
    LanguageOptions LanguageOptions,
    WorkspaceService Workspace,
    DiagnosticsService Diagnostics,
    NavigationService Navigation,
    CompletionProvider Completion,
    ILogger Logger,
    LightweightIDE.DotNet.Packages.PackageService Packages,
    SignatureHelpProvider SignatureHelp,
    LightweightIDE.Language.Classification.SemanticHighlighter Semantic,
    LightweightIDE.Language.Classification.SymbolUsages Usages,
    LightweightIDE.Language.Classification.UnnecessaryCode Unnecessary,
    LightweightIDE.Language.Classification.InlayHintService InlayHints);

/// <summary>One open file: GtkSourceView, its document state, live diagnostics, completion and hover.</summary>
public sealed partial class EditorTab : IDisposable
{
    readonly EditorServices services;
    readonly Gtk.Box root = Ui.VBox();
    readonly Gtk.Revealer diskBar = Gtk.Revealer.New();
    readonly Gtk.Label diskLabel = Ui.Label("");
    readonly Gtk.Label tabTitle = Ui.Label("");
    readonly Gtk.TextTag errorTag;
    readonly Gtk.TextTag warningTag;
    readonly Gtk.TextTag infoTag;
    readonly CompletionPopup completion;
    readonly SignaturePopup signature;
    uint signatureTimer;
    CancellationTokenSource? signatureCts;
    readonly bool isCSharp;
    readonly bool isLarge;
    readonly bool isMarkdown;
    readonly bool isHttp;
    readonly bool isMsBuild;
    uint packageTimer;
    readonly List<GtkSource.Mark> sendMarks = [];
    uint httpTimer;
    readonly MarkdownPreview? preview;
    Gtk.ScrolledWindow? sourceScrolled;
    uint previewTimer;
    uint diagnosticsTimer;
    uint completionTimer;
    CancellationTokenSource? diagnosticsCts;
    CancellationTokenSource? completionCts;
    CancellationTokenSource? hoverCts;
    string? lastInserted;
    bool suppressChange;
    (int Start, int End, int Version, string? Text, LightweightIDE.Language.Navigation.HoverTargets? Targets) hover = (-1, -1, -1, null, null);

    public EditorDocument Document { get; }
    public GtkSource.Buffer Buffer { get; }
    public GtkSource.View View { get; }
    public GtkSource.SearchSettings SearchSettings { get; } = GtkSource.SearchSettings.NewWithProperties([]);
    public GtkSource.SearchContext Search { get; }
    public Gtk.Widget Widget => root;
    public Gtk.Widget TabLabel { get; }
    public IReadOnlyList<LiveDiagnostic> LiveDiagnostics { get; private set; } = [];
    public bool IsDirty => Buffer.GetModified();
    public string Path => Document.Path;

    public event Action<EditorTab>? CloseRequested;
    public event Action<EditorTab>? DirtyChanged;
    public event Action<EditorTab>? CursorMoved;
    public event Action<EditorTab>? DiagnosticsUpdated;
    public event Action<EditorTab>? Saved;
    /// <summary>Navigation request from inside the editor (Ctrl+Click).</summary>
    public event Action<EditorTab>? GoToDefinitionRequested;
    /// <summary>F2 in the editor (the explorer handles its own F2).</summary>
    public event Action<EditorTab>? RenameRequested;
    /// <summary>The ▶ next to a request in a .http file was clicked (1-based line of the request).</summary>
    public event Action<EditorTab, int>? HttpSendRequested;
    /// <summary>A typed '}' closed a block (debounced); the window formats the file when its syntax is valid.</summary>
    public event Action<EditorTab>? FormatOnTypeRequested;
    public bool IsHttp => isHttp;
    /// <summary>Click in the line-marks gutter (1-based line).</summary>
    public event Action<EditorTab, int>? BreakpointToggleRequested;
    /// <summary>Right click in the gutter (1-based line): edit that breakpoint's condition / hit count / log message.</summary>
    public event Action<EditorTab, int>? BreakpointEditRequested;
    /// <summary>A link in the Markdown preview points to a local file.</summary>
    public event Action<string>? OpenFileRequested;

    public EditorTab(EditorDocument document, EditorServices services, GtkSource.StyleScheme? scheme)
    {
        this.services = services;
        Document = document;
        isCSharp = document.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || document.Path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);
        isMsBuild = LightweightIDE.DotNet.Packages.PackageCompletion.IsMsBuildFile(document.Path);
        isHttp = document.Path.EndsWith(".http", StringComparison.OrdinalIgnoreCase) || document.Path.EndsWith(".rest", StringComparison.OrdinalIgnoreCase);
        isMarkdown = document.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || document.Path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
        if (isMarkdown) preview = new MarkdownPreview();
        isLarge = document.SavedText.Length > services.Options.LargeFileThresholdKb * 1024;

        Buffer = GtkSource.Buffer.New(null);
        Buffer.HighlightMatchingBrackets = true;
        Buffer.HighlightSyntax = true;
        var language = GtkSource.LanguageManager.GetDefault().GuessLanguage(document.Path, null);
        if (language is not null) Buffer.Language = language;
        if (scheme is not null) Buffer.StyleScheme = scheme;
        SetTextIrreversibly(document.SavedText);
        Buffer.SetModified(false);
        PlaceCursor(0);

        View = GtkSource.View.NewWithBuffer(Buffer);
        View.ShowLineNumbers = true;
        View.HighlightCurrentLine = true;
        View.AutoIndent = true;
        View.IndentOnTab = true;
        View.SmartBackspace = true;
        View.SmartHomeEnd = GtkSource.SmartHomeEndType.Before;
        View.TabWidth = (uint)services.Options.TabSize;
        View.IndentWidth = services.Options.TabSize;
        View.InsertSpacesInsteadOfTabs = services.Options.InsertSpaces;
        View.Monospace = true;
        View.ShowRightMargin = false;
        View.WrapMode = services.Options.WrapLines ? Gtk.WrapMode.WordChar : Gtk.WrapMode.None;
        View.AddCssClass("editor");
        View.HasTooltip = true;
        if (services.Options.ShowWhitespace)
        {
            var drawer = View.GetSpaceDrawer();
            drawer.EnableMatrix = true;
        }

        if (SemanticEnabled)
        {
            semanticTags = SemanticColors.CreateTags(Buffer.TagTable!, scheme);
            View.OnMap += (_, _) =>
            {
                if (!scrollHooked && View.GetVadjustment() is { } adjustment)
                {
                    scrollHooked = true;
                    adjustment.OnValueChanged += (_, _) => ScheduleSemantic(60);
                }
                ScheduleSemantic(30);
            };
        }
        if (InsightsEnabled) decor = SemanticColors.CreateDecorTags(Buffer.TagTable!, scheme);
        inlayHintsOn = services.Options.InlayHints;
        if (isCSharp && !isLarge)
        {
            var hintsHooked = false;
            View.OnMap += (_, _) =>
            {
                if (!hintsHooked && View.GetVadjustment() is { } adjustment)
                {
                    hintsHooked = true;
                    adjustment.OnValueChanged += (_, _) => ScheduleHints(150);
                }
                ScheduleHints(300);
            };
        }

        errorTag = Tag("diag-error", Pango.Underline.Error, "#e5484d");
        warningTag = Tag("diag-warning", Pango.Underline.Error, "#d39e00");
        infoTag = Tag("diag-info", Pango.Underline.Single, "#5b8def");

        Search = GtkSource.SearchContext.New(Buffer, SearchSettings);
        Search.Highlight = true;
        SearchSettings.WrapAround = true;

        completion = new CompletionPopup(View) { Usage = services.Completion.Usage };
        signature = new SignaturePopup(View);
        completion.Committed += entry => _ = CommitCompletionAsync(entry);
        completion.SelectionChanged += entry => _ = LoadDescriptionAsync(entry);

        // Disk change bar
        var bar = Ui.HBox(8);
        bar.AddCssClass("infobar");
        diskLabel.Hexpand = true;
        bar.Append(diskLabel);
        var reload = Gtk.Button.NewWithLabel("Reload");
        reload.OnClicked += (_, _) => ReloadFromDisk();
        var keep = Gtk.Button.NewWithLabel("Keep my version");
        keep.OnClicked += (_, _) => { Document.AcknowledgeDisk(); diskBar.RevealChild = false; };
        bar.Append(reload);
        bar.Append(keep);
        diskBar.SetChild(bar);
        diskBar.RevealChild = false;
        root.Append(diskBar);

        var scrolled = Ui.Scrolled(View);
        sourceScrolled = scrolled;
        if (preview is null) root.Append(scrolled);
        else AttachPreview(scrolled, preview);

        // Tab label: name + close button
        var label = Ui.HBox(4);
        tabTitle.SetText(document.FileName);
        label.Append(tabTitle);
        var close = Gtk.Button.NewFromIconName("window-close-symbolic");
        close.AddCssClass("flat");
        close.AddCssClass("tab-close");
        close.TooltipText = "Close (Ctrl+W)";
        close.OnClicked += (_, _) => CloseRequested?.Invoke(this);
        label.Append(close);
        label.TooltipText = document.Path;
        var middleClick = Gtk.GestureClick.New();
        middleClick.Button = 2;
        middleClick.OnPressed += (_, _) => CloseRequested?.Invoke(this);
        label.AddController(middleClick);
        TabLabel = label;

        Buffer.OnModifiedChanged += (_, _) => { UpdateTitle(); DirtyChanged?.Invoke(this); };
        Buffer.OnInsertText += (_, e) => lastInserted = e.Text;
        Buffer.OnChanged += (_, _) => OnTextChanged();
        // "cursor-position" changes on clicks, keyboard moves and typing (mark-set is not emitted when text is inserted).
        Buffer.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "cursor-position") return;
            CursorMoved?.Invoke(this);
            OnCaretMovedForSignature();
            ScheduleUsages(250);
        };

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) => OnKeyPressed(e.Keyval, e.State);
        View.AddController(keys);

        var focus = Gtk.EventControllerFocus.New();
        focus.OnLeave += (_, _) => { completion.Hide(); signature.Hide(); };
        View.AddController(focus);

        var click = Gtk.GestureClick.New();
        click.Button = 1;
        click.OnPressed += (g, _) =>
        {
            completion.Hide();
            if ((g.GetCurrentEventState() & Gdk.ModifierType.ControlMask) != 0)
                Ui.After(1, () => GoToDefinitionRequested?.Invoke(this));
        };
        View.AddController(click);

        View.OnQueryTooltip += (_, e) => OnQueryTooltip(e.X, e.Y, e.Tooltip);
        if (isCSharp) SetUpHover();

        if (isCSharp) SetUpDebugMarks();
        if (isCSharp && !isLarge) SetUpTestMarks();
        if (isHttp) SetUpHttpMarks();

        if (isCSharp) ScheduleDiagnostics(50);
    }

    Gtk.TextTag Tag(string name, Pango.Underline underline, string color)
    {
        var tag = Gtk.TextTag.New(name);
        tag.Underline = underline;
        var rgba = new Gdk.RGBA();
        rgba.Parse(color);
        tag.UnderlineRgba = rgba;
        Buffer.TagTable!.Add(tag);
        return tag;
    }

    void UpdateTitle()
    {
        tabTitle.SetText((IsDirty ? "● " : "") + Document.FileName);
        TabLabel.TooltipText = Document.Path;
    }

    // ---------- text access ----------

    public Gtk.TextIter IterAt(int charOffset)
    {
        var it = new Gtk.TextIter();
        Buffer.GetIterAtOffset(out it, charOffset);
        return it;
    }

    public string GetText()
    {
        var start = new Gtk.TextIter();
        var end = new Gtk.TextIter();
        Buffer.GetBounds(out start, out end);
        return Buffer.GetText(start, end, true);
    }

    public int CursorOffset => Buffer.CursorPosition;

    public (int Line, int Column) CursorLineColumn()
    {
        var it = IterAt(Buffer.CursorPosition);
        return (it.GetLine() + 1, it.GetLineOffset() + 1);
    }

    void SetTextIrreversibly(string text)
    {
        suppressChange = true;
        Buffer.BeginIrreversibleAction();
        Buffer.SetText(text, -1);
        Buffer.EndIrreversibleAction();
        suppressChange = false;
    }

    /// <summary>The selected text when it is on a single line, otherwise null.</summary>
    public string? SelectedText()
    {
        var start = new Gtk.TextIter();
        var end = new Gtk.TextIter();
        if (!Buffer.GetSelectionBounds(out start, out end)) return null;
        var text = Buffer.GetText(start, end, true);
        return text.Contains('\n') ? null : text;
    }

    /// <summary>The selection, or the identifier under the caret.</summary>
    public string SelectedWord()
    {
        if (SelectedText() is { Length: > 0 } selected) return selected;
        var (start, end) = WordAt(IterAt(Buffer.CursorPosition));
        return end > start ? Buffer.GetText(IterAt(start), IterAt(end), true) : "";
    }

    /// <summary>The caret as a navigation history point.</summary>
    public LightweightIDE.Editor.NavigationPoint Position()
    {
        var (line, column) = CursorLineColumn();
        return new LightweightIDE.Editor.NavigationPoint(Path, line, column);
    }

    /// <summary>
    /// Applies replacements computed against <paramref name="basisText"/> (UTF-16 offsets) as one undoable step.
    /// Returns false, changing nothing, when the buffer no longer contains that text.
    /// </summary>
    public bool ApplyEdits(string basisText, IReadOnlyList<LightweightIDE.Language.Formatting.TextReplacement> edits)
    {
        if (!string.Equals(GetText(), basisText, StringComparison.Ordinal)) return false;
        if (edits.Count == 0) return true;
        Buffer.BeginUserAction();
        try
        {
            // From the end, so earlier offsets stay valid.
            foreach (var edit in edits.OrderByDescending(e => e.Start))
            {
                var start = IterAt(TextOffsets.Utf16ToChar(basisText, edit.Start));
                var end = IterAt(TextOffsets.Utf16ToChar(basisText, edit.Start + edit.Length));
                if (edit.Length > 0) Buffer.Delete(start, end);
                if (edit.NewText.Length > 0)
                {
                    start = IterAt(TextOffsets.Utf16ToChar(basisText, edit.Start));
                    Buffer.Insert(start, edit.NewText, -1);
                }
            }
        }
        finally { Buffer.EndUserAction(); }
        return true;
    }

    public void PlaceCursor(int charOffset) => Buffer.PlaceCursor(IterAt(charOffset));

    /// <summary>Moves the caret to a 1-based line/column and centres it.</summary>
    public void GoTo(int line, int column, int selectLength = 0)
    {
        var it = new Gtk.TextIter();
        if (!Buffer.GetIterAtLine(out it, Math.Max(0, line - 1))) Buffer.GetIterAtOffset(out it, -1);
        var chars = it.GetCharsInLine();
        it.SetLineOffset(Math.Clamp(column - 1, 0, Math.Max(0, chars - (it.EndsLine() ? 0 : 1))));
        if (selectLength > 0)
        {
            var end = it.Copy();
            end.ForwardChars(selectLength);
            Buffer.SelectRange(it, end);
        }
        else Buffer.PlaceCursor(it);
        ScrollToCursor();
    }

    public void ScrollToCursor()
    {
        // The view may not be allocated yet right after opening: scroll once now and once after layout.
        View.ScrollToMark(Buffer.GetInsert(), 0.1, true, 0, 0.35);
        Ui.After(60, () => View.ScrollToMark(Buffer.GetInsert(), 0.1, true, 0, 0.35));
    }

    public void Focus() => View.GrabFocus();

    // ---------- save / reload ----------

    public void Save()
    {
        var text = GetText();
        Document.Save(text);
        Buffer.SetModified(false);
        diskBar.RevealChild = false;
        UpdateTitle();
        Saved?.Invoke(this);
    }

    public void SaveAs(string path)
    {
        Document.SaveAs(path, GetText());
        Buffer.SetModified(false);
        UpdateTitle();
        Saved?.Invoke(this);
    }

    /// <summary>Called periodically by the editor area. Reloads silently when clean, asks when dirty.</summary>
    public void CheckDisk()
    {
        switch (Document.CheckDisk())
        {
            case DiskState.ChangedOnDisk when !IsDirty:
                ReloadFromDisk();
                break;
            case DiskState.ChangedOnDisk:
                diskLabel.SetText("This file was changed on disk. Reload it and lose your unsaved changes?");
                diskBar.RevealChild = true;
                break;
            case DiskState.DeletedOnDisk:
                diskLabel.SetText("This file was deleted on disk. Saving will create it again.");
                diskBar.RevealChild = true;
                if (!IsDirty) Buffer.SetModified(true);
                break;
        }
    }

    public void ReloadFromDisk()
    {
        var cursor = Buffer.CursorPosition;
        string text;
        try { text = Document.Reload(); }
        catch (IOException e)
        {
            services.Logger.LogWarning(e, "Reload failed for {Path}", Document.Path);
            return;
        }
        SetTextIrreversibly(text);
        Buffer.SetModified(false);
        PlaceCursor(Math.Min(cursor, Buffer.GetCharCount()));
        diskBar.RevealChild = false;
        UpdateTitle();
        OnTextChanged();
    }

    public void Renamed(string newPath)
    {
        Document.Rename(newPath);
        UpdateTitle();
    }

    // ---------- live analysis ----------

    void OnTextChanged()
    {
        if (suppressChange) return;
        Document.Version++;
        if (isMarkdown) SchedulePreview();
        if (isHttp) ScheduleHttpMarks();
        if (isCSharp && !isLarge) ScheduleTestMarks();
        if (InlayHintsEnabled)
        {
            ScheduleHintLayout();
            ScheduleHints(400);
        }
        if (isMsBuild)
        {
            OnMsBuildTyping();
            return;
        }
        if (!isCSharp) return;
        ScheduleDiagnostics(services.LanguageOptions.DiagnosticsDelayMs);
        ScheduleSemantic(150);
        ClearUsages();
        unnecessaryCts?.Cancel();

        var inserted = lastInserted;
        lastInserted = null;
        if (inserted == "}" && !isLarge) ScheduleFormatOnType();
        if (inserted is "(" or ",") ScheduleSignature(30);
        else if (inserted == ")" && signature.IsVisible) ScheduleSignature(30);
        else if (signature.IsVisible) ScheduleSignature(150);
        if (completion.IsVisible)
        {
            if (inserted is not null && inserted.Length == 1 && !IsIdentifierChar(inserted[0]))
            {
                completion.Hide();
                // A new list: members after '.', the next generic argument after ',' / ' ' / '<' (★ types).
                if (inserted is "." or "," or " " or "<") ScheduleCompletion(false);
            }
            else RefilterCompletion();
        }
        else if (inserted is { Length: 1 } && inserted[0] is ' ' or '<' or ',' && !isLarge)
        {
            // Roslyn decides (space after `new`, generic arguments, …).
            ScheduleCompletion(false);
        }
        else if (inserted is { Length: 1 } && (inserted[0] == '.' || char.IsLetter(inserted[0]) || inserted[0] == '_') && !isLarge)
        {
            // Trigger on '.' and on the first letter of a word.
            var it = IterAt(Buffer.CursorPosition);
            it.BackwardChars(2);
            var previous = it.GetChar();
            if (inserted[0] == '.' || !IsIdentifierChar((char)previous)) ScheduleCompletion(false);
        }
    }

    uint formatTimer;

    void ScheduleFormatOnType()
    {
        Ui.Cancel(ref formatTimer);
        formatTimer = Ui.After(250, () =>
        {
            formatTimer = 0;
            FormatOnTypeRequested?.Invoke(this);
        });
    }

    static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    void ScheduleDiagnostics(int delay)
    {
        if (isLarge) return;
        Ui.Cancel(ref diagnosticsTimer);
        diagnosticsTimer = Ui.After(delay, () =>
        {
            diagnosticsTimer = 0;
            _ = RefreshDiagnosticsAsync();
        });
    }

    async Task RefreshDiagnosticsAsync()
    {
        diagnosticsCts?.Cancel();
        var cts = diagnosticsCts = new CancellationTokenSource();
        var text = GetText();
        var version = Document.Version;
        var path = Document.Path;
        try
        {
            var result = await Task.Run(() =>
            {
                services.Workspace.UpdateText(path, text);
                return services.Diagnostics.GetDocumentDiagnosticsAsync(path, text, cts.Token);
            }, cts.Token);
            if (version != Document.Version || cts.IsCancellationRequested) return;
            ApplyDiagnostics(text, result);
            _ = FadeUnnecessaryAsync(text, version);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { services.Logger.LogWarning(e, "Diagnostics failed for {Path}", path); }
    }

    void ApplyDiagnostics(string text, IReadOnlyList<LiveDiagnostic> diagnostics)
    {
        var start = new Gtk.TextIter();
        var end = new Gtk.TextIter();
        Buffer.GetBounds(out start, out end);
        Buffer.RemoveTag(errorTag, start, end);
        Buffer.RemoveTag(warningTag, start, end);
        Buffer.RemoveTag(infoTag, start, end);
        foreach (var d in diagnostics)
        {
            var from = IterAt(TextOffsets.Utf16ToChar(text, d.Start));
            var to = IterAt(TextOffsets.Utf16ToChar(text, Math.Max(d.End, d.Start + 1)));
            if (d.End <= d.Start)
            {
                // zero-length (e.g. "; expected"): underline the previous character
                from = to.Copy();
                from.BackwardChar();
            }
            var tag = d.Diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => errorTag,
                DiagnosticSeverity.Warning => warningTag,
                _ => infoTag,
            };
            Buffer.ApplyTag(tag, from, to);
        }
        LiveDiagnostics = diagnostics;
        DiagnosticsUpdated?.Invoke(this);
    }

    /// <summary>Re-runs diagnostics now (e.g. after the workspace finished loading).</summary>
    public void RefreshAnalysis()
    {
        if (isCSharp) ScheduleDiagnostics(10);
        ScheduleSemantic(10);
    }

    // ---------- semantic highlighting ----------

    Dictionary<string, Gtk.TextTag>? semanticTags;
    uint semanticTimer;
    CancellationTokenSource? semanticCts;
    bool scrollHooked;

    bool SemanticEnabled => isCSharp && !isLarge && services.Options.SemanticHighlighting;

    /// <summary>The color scheme changed: restyle the semantic tags and color the visible lines again.</summary>
    public void SetStyleScheme(GtkSource.StyleScheme? scheme)
    {
        Buffer.StyleScheme = scheme;
        if (InlayHintsEnabled)
        {
            ClearHints(); // anchors are redrawn in the scheme's text color
            ScheduleHints(10);
        }
        if (SemanticEnabled)
        {
            semanticTags = SemanticColors.CreateTags(Buffer.TagTable!, scheme);
            ScheduleSemantic(10);
        }
        if (InsightsEnabled)
        {
            // After the semantic tags, so faded code still wins over them.
            decor = SemanticColors.CreateDecorTags(Buffer.TagTable!, scheme);
            usagesShown = false;
            ScheduleUsages(10);
            ScheduleDiagnostics(10);
        }
    }

    void ScheduleSemantic(int delay)
    {
        if (!SemanticEnabled) return;
        Ui.Cancel(ref semanticTimer);
        semanticTimer = Ui.After(delay, () =>
        {
            semanticTimer = 0;
            _ = HighlightVisibleAsync();
        });
    }

    /// <summary>
    /// Classifies the visible lines (±40) with Roslyn off the UI thread and re-tags only that range. A result for an
    /// older version of the text is dropped (the next change schedules a new run).
    /// </summary>
    async Task HighlightVisibleAsync()
    {
        if (semanticTags is null) return;
        semanticCts?.Cancel();
        var cts = semanticCts = new CancellationTokenSource();
        View.GetVisibleRect(out var rect);
        int firstLine, lastLine;
        if (rect.Height <= 0)
        {
            firstLine = 0;
            lastLine = 200;
        }
        else
        {
            View.GetLineAtY(out var top, rect.Y, out _);
            View.GetLineAtY(out var bottom, rect.Y + rect.Height, out _);
            firstLine = top.GetLine();
            lastLine = bottom.GetLine();
        }
        firstLine = Math.Max(0, firstLine - 40);
        lastLine = Math.Min(Buffer.GetLineCount() - 1, lastLine + 40);
        Buffer.GetIterAtLine(out var from, firstLine);
        Buffer.GetIterAtLine(out var to, lastLine);
        if (!to.EndsLine()) to.ForwardToLineEnd();
        var fromChar = from.GetOffset();
        var toChar = to.GetOffset();
        var text = GetText();
        var version = Document.Version;
        var path = Document.Path;
        IReadOnlyList<LightweightIDE.Language.Classification.SemanticSpan> spans;
        try
        {
            spans = await Task.Run(() =>
            {
                services.Workspace.UpdateText(path, text);
                return services.Semantic.GetSpansAsync(path, text, TextOffsets.CharToUtf16(text, fromChar), TextOffsets.CharToUtf16(text, toChar), cts.Token);
            }, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            services.Logger.LogDebug(e, "Semantic highlighting failed");
            return;
        }
        if (cts.IsCancellationRequested || version != Document.Version || semanticTags is null) return;

        Buffer.GetIterAtOffset(out var clearFrom, fromChar);
        Buffer.GetIterAtOffset(out var clearTo, toChar);
        foreach (var tag in semanticTags.Values) Buffer.RemoveTag(tag, clearFrom, clearTo);
        var map = CharMap(text);
        foreach (var span in spans)
        {
            if (!semanticTags.TryGetValue(span.Kind, out var tag)) continue;
            ApplyTag(tag, text, map, span.Start, span.End);
        }
    }

    /// <summary>UTF-16 → character offsets: null (identity) unless the text has surrogate pairs (emoji).</summary>
    static int[]? CharMap(string text)
    {
        if (!TextOffsets.HasSurrogates(text)) return null;
        var map = new int[text.Length + 1];
        for (int i = 0, c = 0; i <= text.Length; i++)
        {
            map[i] = c;
            if (i < text.Length && !(char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1]))) c++;
        }
        return map;
    }

    /// <summary>Applies a tag to a UTF-16 range of <paramref name="text"/> (the buffer's current text).</summary>
    void ApplyTag(Gtk.TextTag tag, string text, int[]? map, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        Buffer.GetIterAtOffset(out var a, map is null ? start : map[start]);
        Buffer.GetIterAtOffset(out var b, map is null ? end : map[end]);
        Buffer.ApplyTag(tag, a, b);
    }

    // ---------- occurrences of the symbol under the caret, faded unused code ----------

    SemanticColors.DecorTags? decor;
    uint usagesTimer;
    bool usagesShown;
    CancellationTokenSource? usagesCts;
    CancellationTokenSource? unnecessaryCts;
    (IReadOnlyList<LightweightIDE.Language.Classification.UnnecessarySpan> Spans, int Version) unnecessary = ([], -1);

    bool InsightsEnabled => isCSharp && !isLarge;

    void ScheduleUsages(int delay)
    {
        if (!InsightsEnabled || !services.Options.HighlightUsages) return;
        Ui.Cancel(ref usagesTimer);
        usagesTimer = Ui.After(delay, () =>
        {
            usagesTimer = 0;
            _ = HighlightUsagesAsync();
        });
    }

    void ClearUsages()
    {
        usagesCts?.Cancel();
        if (!usagesShown || decor is null) return;
        Buffer.GetBounds(out var start, out var end);
        Buffer.RemoveTag(decor.Usage, start, end);
        Buffer.RemoveTag(decor.UsageDefinition, start, end);
        usagesShown = false;
    }

    /// <summary>Highlights every occurrence of the symbol under the caret in this file (nothing for a single one).</summary>
    async Task HighlightUsagesAsync()
    {
        if (decor is null) return;
        ClearUsages();
        if (Buffer.GetSelectionBounds(out _, out _)) return;
        var cts = usagesCts = new CancellationTokenSource();
        var text = GetText();
        var version = Document.Version;
        var path = Document.Path;
        var caret = TextOffsets.CharToUtf16(text, Buffer.CursorPosition);
        IReadOnlyList<LightweightIDE.Language.Classification.UsageSpan> usages;
        try
        {
            usages = await Task.Run(() =>
            {
                services.Workspace.UpdateText(path, text);
                return services.Usages.GetAsync(path, text, caret, cts.Token);
            }, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            services.Logger.LogDebug(e, "Usage highlighting failed");
            return;
        }
        if (cts.IsCancellationRequested || version != Document.Version || decor is null || usages.Count < 2) return;
        var map = CharMap(text);
        foreach (var usage in usages)
            ApplyTag(usage.IsDefinition ? decor.UsageDefinition : decor.Usage, text, map, usage.Start, usage.End);
        usagesShown = true;
    }

    /// <summary>Fades unused code of this version of the text (runs after its diagnostics).</summary>
    async Task FadeUnnecessaryAsync(string text, int version)
    {
        if (decor is null || !services.Options.FadeUnusedCode) return;
        unnecessaryCts?.Cancel();
        var cts = unnecessaryCts = new CancellationTokenSource();
        var path = Document.Path;
        IReadOnlyList<LightweightIDE.Language.Classification.UnnecessarySpan> spans;
        try
        {
            spans = await Task.Run(() => services.Unnecessary.GetAsync(path, text, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            services.Logger.LogDebug(e, "Unused code analysis failed");
            return;
        }
        if (cts.IsCancellationRequested || version != Document.Version || decor is null) return;
        Buffer.GetBounds(out var start, out var end);
        Buffer.RemoveTag(decor.Unnecessary, start, end);
        var map = CharMap(text);
        foreach (var span in spans) ApplyTag(decor.Unnecessary, text, map, span.Start, span.End);
        unnecessary = (spans, version);
    }

    // ---------- completion ----------

    bool OnKeyPressed(uint key, Gdk.ModifierType state)
    {
        var ctrl = (state & Gdk.ModifierType.ControlMask) != 0;
        if (completion.IsVisible)
        {
            switch (key)
            {
                case Gdk.Constants.KEY_Escape: completion.Hide(); return true;
                case Gdk.Constants.KEY_Down: completion.Move(1); View.GrabFocus(); return true;
                case Gdk.Constants.KEY_Up: completion.Move(-1); View.GrabFocus(); return true;
                case Gdk.Constants.KEY_Page_Down: completion.Move(10); View.GrabFocus(); return true;
                case Gdk.Constants.KEY_Page_Up: completion.Move(-10); View.GrabFocus(); return true;
                case Gdk.Constants.KEY_Return or Gdk.Constants.KEY_KP_Enter or Gdk.Constants.KEY_Tab:
                    completion.CommitSelected();
                    return true;
            }
        }
        if (key == Gdk.Constants.KEY_F2 && (state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask | Gdk.ModifierType.AltMask)) == 0)
        {
            if (isCSharp) Ui.After(1, () => RenameRequested?.Invoke(this));
            return isCSharp;
        }
        if (signature.IsVisible && !completion.IsVisible)
        {
            switch (key)
            {
                case Gdk.Constants.KEY_Escape: signature.Hide(); return true;
                case Gdk.Constants.KEY_Up when signature.HasOverloads: signature.Cycle(-1); return true;
                case Gdk.Constants.KEY_Down when signature.HasOverloads: signature.Cycle(1); return true;
            }
        }
        if (ctrl && (state & Gdk.ModifierType.ShiftMask) != 0 && key == Gdk.Constants.KEY_space && isCSharp)
        {
            ScheduleSignature(1);
            return true;
        }
        if (ctrl && key == Gdk.Constants.KEY_space && isMsBuild)
        {
            SchedulePackageCompletion(explicitInvoke: true);
            return true;
        }
        if (ctrl && key == Gdk.Constants.KEY_space)
        {
            if (isCSharp) ScheduleCompletion(true);
            return true;
        }
        return false;
    }

    void ScheduleCompletion(bool explicitInvoke)
    {
        Ui.Cancel(ref completionTimer);
        completionTimer = Ui.After(explicitInvoke ? 1 : 40, () =>
        {
            completionTimer = 0;
            _ = ShowCompletionAsync(explicitInvoke);
        });
    }

    async Task ShowCompletionAsync(bool explicitInvoke)
    {
        completionCts?.Cancel();
        var cts = completionCts = new CancellationTokenSource();
        var text = GetText();
        var position = TextOffsets.CharToUtf16(text, Buffer.CursorPosition);
        var version = Document.Version;
        var path = Document.Path;
        try
        {
            var session = await Task.Run(() =>
            {
                services.Workspace.UpdateText(path, text);
                return services.Completion.GetCompletionsAsync(path, text, position, explicitInvoke, cts.Token);
            }, cts.Token);
            if (session is null || cts.IsCancellationRequested || version != Document.Version) return;
            completion.Show(session, TypedPrefix(session), CaretRectangle());
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { services.Logger.LogWarning(e, "Completion failed"); }
    }

    string TypedPrefix(CompletionSession session)
    {
        var text = GetText();
        var start = TextOffsets.Utf16ToChar(text, session.SpanStart);
        var cursor = Buffer.CursorPosition;
        if (cursor < start) return "";
        return Buffer.GetText(IterAt(start), IterAt(cursor), true);
    }

    void RefilterCompletion()
    {
        if (completion.Session is null) return;
        var text = GetText();
        var start = TextOffsets.Utf16ToChar(text, completion.Session.SpanStart);
        if (Buffer.CursorPosition < start) { completion.Hide(); return; }
        completion.Filter(TypedPrefix(completion.Session));
    }

    CancellationTokenSource? descriptionCts;

    // ---------- signature help ----------

    void ScheduleSignature(int delay)
    {
        if (!isCSharp) return;
        Ui.Cancel(ref signatureTimer);
        signatureTimer = Ui.After(delay, () =>
        {
            signatureTimer = 0;
            _ = ShowSignatureAsync();
        });
    }

    async Task ShowSignatureAsync()
    {
        signatureCts?.Cancel();
        var cts = signatureCts = new CancellationTokenSource();
        var text = GetText();
        var position = TextOffsets.CharToUtf16(text, Buffer.CursorPosition);
        var path = Document.Path;
        try
        {
            var help = await Task.Run(() =>
            {
                services.Workspace.UpdateText(path, text);
                return services.SignatureHelp.GetAsync(path, text, position, cts.Token);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (help is null) signature.Hide();
            else signature.Show(help, CaretRectangle());
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { services.Logger.LogDebug(e, "Signature help failed"); }
    }

    /// <summary>The caret moved: keep the parameter info in step, or hide it outside the argument list.</summary>
    void OnCaretMovedForSignature()
    {
        if (signature.IsVisible) ScheduleSignature(120);
    }

    async Task LoadDescriptionAsync(CompletionEntry entry)
    {
        descriptionCts?.Cancel();
        var cts = descriptionCts = new CancellationTokenSource();
        if (completion.Session is not { IsPlainText: false } session)
        {
            completion.SetDescription(null);
            return;
        }
        try
        {
            await Task.Delay(80, cts.Token); // moving through the list quickly loads only the last item
            var text = await Task.Run(() => services.Completion.GetDescriptionAsync(session, entry, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) completion.SetDescription(entry.Smart is { } why ? $"★ {why}\n{text}" : text);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { services.Logger.LogDebug(e, "Completion description failed"); }
    }

    async Task CommitCompletionAsync(CompletionEntry entry)
    {
        var session = completion.Session;
        if (session is null) return;
        try
        {
            var edit = await Task.Run(() => services.Completion.GetCommitEditAsync(session, entry, CancellationToken.None));
            // The edit is relative to the text when completion started; extend its end to the current caret
            // so letters typed while the list was open are replaced too.
            var text = GetText();
            var start = TextOffsets.Utf16ToChar(text, edit.Start);
            var end = Math.Max(TextOffsets.Utf16ToChar(text, edit.End), Buffer.CursorPosition);
            Buffer.BeginUserAction();
            var from = IterAt(start);
            var to = IterAt(end);
            Buffer.Delete(from, to);
            from = IterAt(start);
            Buffer.Insert(from, edit.NewText, -1);
            Buffer.EndUserAction();
            if (edit.CaretPosition is { } caret && caret != edit.Start + edit.NewText.Length)
                PlaceCursor(start + (caret - edit.Start));
            lastInserted = null;
        }
        catch (Exception e) { services.Logger.LogWarning(e, "Completion commit failed"); }
        finally { completion.Clear(); }
    }

    public Gdk.Rectangle CaretRectangle()
    {
        var rect = new Gdk.Rectangle();
        View.GetIterLocation(IterAt(Buffer.CursorPosition), out rect);
        View.BufferToWindowCoords(Gtk.TextWindowType.Widget, rect.X, rect.Y, out var x, out var y);
        var result = new Gdk.Rectangle { X = x, Y = y, Width = 1, Height = rect.Height };
        return result;
    }

    // ---------- hover ----------

    bool OnQueryTooltip(int x, int y, Gtk.Tooltip tooltip)
    {
        if (!isCSharp || completion.IsVisible) return false;
        View.WindowToBufferCoords(Gtk.TextWindowType.Widget, x, y, out var bx, out var by);
        var it = new Gtk.TextIter();
        if (!View.GetIterAtLocation(out it, bx, by)) return false;
        var offset = it.GetOffset();

        // Diagnostics under the pointer take priority (no Roslyn call needed).
        var text = GetText();
        var utf16 = TextOffsets.CharToUtf16(text, offset);
        var diag = LiveDiagnostics.FirstOrDefault(d => utf16 >= d.Start && utf16 <= Math.Max(d.End, d.Start + 1));
        string? diagText = diag is null ? null : $"{diag.Diagnostic.Code}: {diag.Diagnostic.Message}";
        if (diagText is null && unnecessary.Version == Document.Version
            && unnecessary.Spans.FirstOrDefault(s => utf16 >= s.Start && utf16 < s.End) is { Id: not null } unused)
            diagText = $"{unused.Id}: {unused.Message}";

        var evaluator = DebugEvaluator;
        var (wordStart, wordEnd) = evaluator is not null ? ExpressionAt(it) : WordAt(it);
        // Shown in a popover the pointer can move onto (links to the definition and related types), not a tooltip.
        if (wordStart == wordEnd)
        {
            if (diagText is not null) ShowHover(offset, offset + 1, diagText, null);
            return false;
        }
        if (hover.Start == wordStart && hover.End == wordEnd && hover.Version == Document.Version)
        {
            var combined = string.Join("\n\n", new[] { diagText, hover.Text }.Where(s => !string.IsNullOrEmpty(s)));
            if (combined.Length > 0 || hover.Targets is { Definition: not null }) ShowHover(wordStart, wordEnd, combined, hover.Targets);
            return false;
        }

        // Fetch quick info in the background, then ask GTK to query the tooltip again.
        hoverCts?.Cancel();
        var cts = hoverCts = new CancellationTokenSource();
        var version = Document.Version;
        var path = Document.Path;
        _ = Task.Run(async () =>
        {
            try
            {
                string? info;
                LightweightIDE.Language.Navigation.HoverTargets? targets = null;
                if (evaluator is not null)
                {
                    // Paused in the debugger: the value wins over the declaration.
                    var expression = text.Substring(TextOffsets.CharToUtf16(text, wordStart),
                        TextOffsets.CharToUtf16(text, wordEnd) - TextOffsets.CharToUtf16(text, wordStart));
                    info = await evaluator(expression) is { } value ? $"{expression} = {value}" : null;
                }
                else
                {
                    services.Workspace.UpdateText(path, text);
                    info = await services.Navigation.GetQuickInfoAsync(path, text, utf16, cts.Token);
                    targets = await services.Navigation.GetHoverTargetsAsync(path, text, utf16, cts.Token);
                }
                Ui.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    hover = (wordStart, wordEnd, version, info, targets);
                    View.TriggerTooltipQuery();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { services.Logger.LogDebug(e, "Quick info failed"); }
        }, cts.Token);
        if (diagText is not null) ShowHover(wordStart, wordEnd, diagText, null);
        return false;
    }

    static (int Start, int End) WordAt(Gtk.TextIter at)
    {
        var start = at.Copy();
        var end = at.Copy();
        while (start.BackwardChar())
        {
            if (!IsIdentifierChar((char)start.GetChar())) { start.ForwardChar(); break; }
        }
        while (!end.EndsLine() && IsIdentifierChar((char)end.GetChar())) end.ForwardChar();
        return (start.GetOffset(), end.GetOffset());
    }

    // ---------- NuGet completion in MSBuild files ----------

    void OnMsBuildTyping()
    {
        var inserted = lastInserted;
        lastInserted = null;
        if (completion.IsVisible)
        {
            if (inserted == "\"") completion.Hide(); // the value was closed
            else RefilterCompletion();
            return;
        }
        if (inserted is { Length: 1 } && (char.IsLetterOrDigit(inserted[0]) || inserted[0] is '.' or '"')) SchedulePackageCompletion(explicitInvoke: false);
    }

    void SchedulePackageCompletion(bool explicitInvoke)
    {
        Ui.Cancel(ref packageTimer);
        Ui.Cancel(ref signatureTimer);
        signatureCts?.Cancel();
        signature.Hide();
        packageTimer = Ui.After(explicitInvoke ? 1 : 350, () =>
        {
            packageTimer = 0;
            _ = ShowPackageCompletionAsync(explicitInvoke);
        });
    }

    /// <summary>Package ids inside Include="…" (NuGet search) and versions inside Version="…" of PackageVersion / PackageReference.</summary>
    async Task ShowPackageCompletionAsync(bool explicitInvoke)
    {
        completionCts?.Cancel();
        var cts = completionCts = new CancellationTokenSource();
        var text = GetText();
        var position = TextOffsets.CharToUtf16(text, Buffer.CursorPosition);
        if (LightweightIDE.DotNet.Packages.PackageCompletion.At(text, position) is not { } context) return;
        if (context.Field == LightweightIDE.DotNet.Packages.PackageField.Id && context.Typed.Length < 2 && !explicitInvoke) return;
        var version = Document.Version;
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        try
        {
            IReadOnlyList<CompletionEntry> items;
            if (context.Field == LightweightIDE.DotNet.Packages.PackageField.Id)
            {
                var found = await Task.Run(() => services.Packages.SearchAsync(context.Typed, false, directory, _ => { }, cts.Token), cts.Token);
                // Most downloaded first: Serilog.Sinks.Console before Serilog.Sinks.Map.
                items = found.Packages.OrderByDescending(p => p.TotalDownloads)
                    .Select((p, i) => new CompletionEntry(p.Id, p.Id, "package", p.LatestVersion, i)).ToList();
            }
            else
            {
                var versions = await Task.Run(() => services.Packages.VersionsAsync(context.PackageId!, context.Typed.Contains('-'), directory, cts.Token), cts.Token);
                items = versions.Take(200).Select((v, i) => new CompletionEntry(v, v, "version", i == 0 ? "latest" : null, i)).ToList();
            }
            if (items.Count == 0 || cts.IsCancellationRequested || version != Document.Version) return;
            var session = new CompletionSession { Items = items, SpanStart = context.ValueStart, SpanEnd = context.ValueEnd };
            completion.Show(session, TypedPrefix(session), CaretRectangle());
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { services.Logger.LogDebug(e, "Package completion failed"); }
    }

    // ---------- .http files ----------

    void SetUpHttpMarks()
    {
        var attributes = GtkSource.MarkAttributes.New();
        if (DebugIcons.SendIcon is { } icon) attributes.SetPixbuf(icon);
        View.SetMarkAttributes("http-send", attributes, 10);
        View.ShowLineMarks = true;
        // Right click in the gutter: edit the breakpoint (condition, hit count, log message); creates one on an empty
        // line. Captured before the view's own context menu, which would otherwise take every right click.
        var gutterClick = Gtk.GestureClick.New();
        gutterClick.Button = 3;
        gutterClick.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        gutterClick.OnPressed += (gesture, e) =>
        {
            var gutterWidth = View.GetGutter(Gtk.TextWindowType.Left).GetWidth();
            if (e.X >= gutterWidth) return;
            gesture.SetState(Gtk.EventSequenceState.Claimed);
            View.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int)e.X, (int)e.Y, out _, out var y);
            View.GetLineAtY(out var it, y, out _);
            var line = it.GetLine() + 1;
            Ui.Post(() => BreakpointEditRequested?.Invoke(this, line));
        };
        View.AddController(gutterClick);
        View.OnLineMarkActivated += (_, e) =>
        {
            if (e.Button != 1 || e.NPresses != 1) return;
            var line = e.Iter.GetLine() + 1;
            if (LightweightIDE.DotNet.Http.HttpFile.Parse(GetText()).Requests.Any(r => r.Line == line)) HttpSendRequested?.Invoke(this, line);
        };
        UpdateHttpMarks();
    }

    void ScheduleHttpMarks()
    {
        Ui.Cancel(ref httpTimer);
        Ui.Cancel(ref testTimer);
        HideHover();
        Ui.Cancel(ref hintTimer);
        Ui.Cancel(ref hintLayoutTimer);
        hintCts?.Cancel();
        Ui.Cancel(ref formatTimer);
        Ui.Cancel(ref packageTimer);
        httpTimer = Ui.After(300, () =>
        {
            httpTimer = 0;
            UpdateHttpMarks();
        });
    }

    void UpdateHttpMarks()
    {
        foreach (var mark in sendMarks) if (!mark.GetDeleted()) Buffer.DeleteMark(mark);
        sendMarks.Clear();
        foreach (var request in LightweightIDE.DotNet.Http.HttpFile.Parse(GetText()).Requests)
        {
            Buffer.GetIterAtLine(out var it, request.Line - 1);
            sendMarks.Add(Buffer.CreateSourceMark(null, "http-send", it));
        }
    }

    // ---------- debugging ----------

    const string BreakpointCategory = "breakpoint", UnverifiedCategory = "breakpoint-unverified", CurrentCategory = "current-line",
        ConditionalCategory = "breakpoint-conditional", LogpointCategory = "breakpoint-log";
    readonly List<GtkSource.Mark> breakpointMarks = [];
    GtkSource.Mark? currentLineMark;

    /// <summary>While the debugger is paused: evaluates an expression in the current frame (hover).</summary>
    public Func<string, Task<string?>>? DebugEvaluator
    {
        get => debugEvaluator;
        set
        {
            debugEvaluator = value;
            hover = (-1, -1, -1, null, null); // values change at every stop
        }
    }

    Func<string, Task<string?>>? debugEvaluator;

    void SetUpDebugMarks()
    {
        static GtkSource.MarkAttributes Attributes(GdkPixbuf.Pixbuf? icon, string? background)
        {
            var attributes = GtkSource.MarkAttributes.New();
            if (icon is not null) attributes.SetPixbuf(icon);
            if (background is not null)
            {
                var rgba = new Gdk.RGBA();
                rgba.Parse(background);
                attributes.SetBackground(rgba);
            }
            return attributes;
        }
        View.SetMarkAttributes(BreakpointCategory, Attributes(DebugIcons.BreakpointIcon, "rgba(229,72,77,0.16)"), 10);
        View.SetMarkAttributes(UnverifiedCategory, Attributes(DebugIcons.UnverifiedIcon, null), 10);
        View.SetMarkAttributes(ConditionalCategory, Attributes(DebugIcons.ConditionalIcon, "rgba(229,72,77,0.16)"), 10);
        View.SetMarkAttributes(LogpointCategory, Attributes(DebugIcons.LogpointIcon, "rgba(229,72,77,0.10)"), 10);
        View.SetMarkAttributes(CurrentCategory, Attributes(DebugIcons.CurrentLineIcon, "rgba(245,180,0,0.30)"), 20);
        View.ShowLineMarks = true;
        // Right click in the gutter: edit the breakpoint (condition, hit count, log message); creates one on an empty
        // line. Captured before the view's own context menu, which would otherwise take every right click.
        var gutterClick = Gtk.GestureClick.New();
        gutterClick.Button = 3;
        gutterClick.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        gutterClick.OnPressed += (gesture, e) =>
        {
            var gutterWidth = View.GetGutter(Gtk.TextWindowType.Left).GetWidth();
            if (e.X >= gutterWidth) return;
            gesture.SetState(Gtk.EventSequenceState.Claimed);
            View.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int)e.X, (int)e.Y, out _, out var y);
            View.GetLineAtY(out var it, y, out _);
            var line = it.GetLine() + 1;
            Ui.Post(() => BreakpointEditRequested?.Invoke(this, line));
        };
        View.AddController(gutterClick);
        View.OnLineMarkActivated += (_, e) =>
        {
            if (e.Button != 1 || e.NPresses != 1) return;
            // A line with a test ▶ opens the Run / Debug menu (it also offers the breakpoint toggle).
            if (TestMarkAt(e.Iter.GetLine() + 1) is { } test) ShowTestMenu(test, e.Iter);
            else BreakpointToggleRequested?.Invoke(this, e.Iter.GetLine() + 1);
        };
    }

    /// <summary>Shows breakpoints on these 1-based lines; <paramref name="unverified"/> ones get a hollow icon.</summary>
    /// <param name="kindOf">Conditional / logpoint per line: picks the icon.</param>
    public void SetBreakpoints(IReadOnlyCollection<int> lines, IReadOnlyCollection<int>? unverified = null, Func<int, LightweightIDE.Editor.BreakpointOptions>? kindOf = null)
    {
        if (!isCSharp) return;
        foreach (var mark in breakpointMarks) Buffer.DeleteMark(mark);
        breakpointMarks.Clear();
        var lineCount = Buffer.GetLineCount();
        foreach (var line in lines.Where(l => l >= 1 && l <= lineCount))
        {
            Buffer.GetIterAtLine(out var it, line - 1);
            var options = kindOf?.Invoke(line);
            var category = unverified?.Contains(line) == true ? UnverifiedCategory
                : options?.IsLogpoint == true ? LogpointCategory
                : options is { IsDefault: false } ? ConditionalCategory
                : BreakpointCategory;
            breakpointMarks.Add(Buffer.CreateSourceMark(null, category, it));
        }
    }

    /// <summary>Where the breakpoint marks are now (they move with edits above them).</summary>
    public IReadOnlyList<int> BreakpointLines()
    {
        var lines = new SortedSet<int>();
        foreach (var mark in breakpointMarks)
        {
            if (mark.GetDeleted()) continue;
            Buffer.GetIterAtMark(out var it, mark);
            lines.Add(it.GetLine() + 1);
        }
        return lines.ToList();
    }

    /// <summary>Marks the line where execution is paused (null clears it).</summary>
    public void SetCurrentLine(int? line)
    {
        if (currentLineMark is not null && !currentLineMark.GetDeleted()) Buffer.DeleteMark(currentLineMark);
        currentLineMark = null;
        if (line is not { } l || !isCSharp) return;
        Buffer.GetIterAtLine(out var it, Math.Max(0, l - 1));
        currentLineMark = Buffer.CreateSourceMark(null, CurrentCategory, it);
    }

    /// <summary>The expression under a position: an identifier with the member-access chain before it (order.Customer.Name).</summary>
    (int Start, int End) ExpressionAt(Gtk.TextIter at)
    {
        var (start, end) = WordAt(at);
        if (start == end) return (start, end);
        var it = IterAt(start);
        while (true)
        {
            var probe = it.Copy();
            if (!probe.BackwardChar() || probe.GetChar() != '.') break;
            var before = probe.Copy();
            if (!before.BackwardChar() || !IsIdentifierChar((char)before.GetChar())) break;
            var (s, _) = WordAt(before);
            it = IterAt(s);
            start = s;
        }
        return (start, end);
    }

    // ---------- Markdown preview ----------

    public bool IsMarkdown => isMarkdown;
    public MarkdownViewMode MarkdownMode { get; private set; } = MarkdownViewMode.Split;

    void AttachPreview(Gtk.ScrolledWindow source, MarkdownPreview markdown)
    {
        var split = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        split.SetStartChild(source);
        split.SetEndChild(markdown.Widget);
        split.Vexpand = true;
        split.ShrinkStartChild = split.ShrinkEndChild = false;
        root.Append(split);
        markdown.OpenFileRequested += path => OpenFileRequested?.Invoke(path);

        // Half and half once the paned has its width.
        var attempts = 0;
        Ui.Every(30, () =>
        {
            var width = split.GetWidth();
            if (width <= 0 && ++attempts < 100) return true;
            if (width > 0) split.Position = width / 2;
            return false;
        });

        // The preview follows the source scroll position (proportionally: line heights differ).
        var syncing = false;
        source.GetVadjustment()!.OnValueChanged += (adjustment, _) =>
        {
            if (syncing || MarkdownMode != MarkdownViewMode.Split) return;
            syncing = true;
            var range = adjustment.Upper - adjustment.PageSize;
            var target = markdown.VerticalAdjustment;
            target.Value = range <= 0 ? 0 : adjustment.Value / range * Math.Max(0, target.Upper - target.PageSize);
            syncing = false;
        };
        RenderPreview();
    }

    void SchedulePreview()
    {
        if (preview is null || MarkdownMode == MarkdownViewMode.Source) return;
        Ui.Cancel(ref previewTimer);
        previewTimer = Ui.After(250, () =>
        {
            previewTimer = 0;
            RenderPreview();
        });
    }

    void RenderPreview() => preview?.Render(GetText(), System.IO.Path.GetDirectoryName(Path)!);

    /// <summary>Ctrl+Shift+V: source and preview side by side → preview only → source only.</summary>
    public void CycleMarkdownView()
    {
        if (preview is null || sourceScrolled is null) return;
        MarkdownMode = MarkdownMode switch
        {
            MarkdownViewMode.Split => MarkdownViewMode.Preview,
            MarkdownViewMode.Preview => MarkdownViewMode.Source,
            _ => MarkdownViewMode.Split,
        };
        sourceScrolled.Visible = MarkdownMode != MarkdownViewMode.Preview;
        preview.Widget.Visible = MarkdownMode != MarkdownViewMode.Source;
        if (MarkdownMode != MarkdownViewMode.Source) RenderPreview();
        if (MarkdownMode == MarkdownViewMode.Preview) preview.Widget.GrabFocus();
        else Focus();
    }

    public void Dispose()
    {
        Ui.Cancel(ref semanticTimer);
        semanticCts?.Cancel();
        Ui.Cancel(ref usagesTimer);
        usagesCts?.Cancel();
        unnecessaryCts?.Cancel();
        Ui.Cancel(ref previewTimer);
        Ui.Cancel(ref httpTimer);
        preview?.Dispose();
        Ui.Cancel(ref diagnosticsTimer);
        Ui.Cancel(ref completionTimer);
        diagnosticsCts?.Cancel();
        completionCts?.Cancel();
        hoverCts?.Cancel();
        completion.Hide();
    }
}
