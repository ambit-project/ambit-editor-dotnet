using LightweightIDE.Indexing;

namespace LightweightIDE.App.Views;

public enum PaletteMode { Files, Symbols, Text, Recent, Structure }

/// <summary>Keyboard-driven search popup: Quick Open (Ctrl+P), Go to Symbol (Ctrl+Shift+P), Find in Files (Ctrl+Shift+F).</summary>
public sealed class SearchPalette
{
    const int MaxRows = 200;
    readonly Gtk.Window window;
    readonly Gtk.SearchEntry entry = Gtk.SearchEntry.New();
    readonly Gtk.ListBox list = Gtk.ListBox.New();
    readonly Gtk.Label status = Ui.Label("", "dim-label");
    readonly Gtk.Box options = Ui.HBox(8);
    readonly Gtk.CheckButton matchCase = Gtk.CheckButton.NewWithLabel("Match case");
    readonly Gtk.CheckButton wholeWord = Gtk.CheckButton.NewWithLabel("Whole word");
    readonly Func<SearchService?> search;
    PaletteMode mode;
    Func<string, CancellationToken, IReadOnlyList<(Location, string)>>? customSource;
    List<(Location Location, string Text)> results = [];
    CancellationTokenSource? cts;
    uint debounce;

    public event Action<Location>? Navigate;
    /// <summary>Text search results the user wants to keep in the Find Results panel.</summary>
    public event Action<string, IReadOnlyList<(Location, string)>>? ShowInPanel;

    public SearchPalette(Gtk.Window parent, Func<SearchService?> search)
    {
        this.search = search;
        window = Gtk.Window.New();
        window.SetTransientFor(parent);
        window.Modal = true;
        window.Decorated = false;
        window.SetDefaultSize(760, 460);
        window.AddCssClass("palette");
        window.HideOnClose = true;

        var box = Ui.VBox(6);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 8;
        entry.Hexpand = true;
        box.Append(entry);
        options.Append(matchCase);
        options.Append(wholeWord);
        var toPanel = Gtk.Button.NewWithLabel("Show in Find Results (Ctrl+Enter)");
        toPanel.AddCssClass("flat");
        toPanel.OnClicked += (_, _) => SendToPanel();
        options.Append(toPanel);
        box.Append(options);
        list.AddCssClass("palette-list");
        list.SelectionMode = Gtk.SelectionMode.Browse;
        list.OnRowActivated += (_, e) => Accept(e.Row.GetIndex());
        box.Append(Ui.Scrolled(list));
        box.Append(status);
        window.SetChild(box);

        entry.OnSearchChanged += (_, _) => Schedule();
        entry.OnActivate += (_, _) => Accept(list.GetSelectedRow()?.GetIndex() ?? 0);
        entry.OnStopSearch += (_, _) => window.Close();
        matchCase.OnToggled += (_, _) => Schedule();
        wholeWord.OnToggled += (_, _) => Schedule();

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            switch (e.Keyval)
            {
                case Gdk.Constants.KEY_Escape: window.Close(); return true;
                case Gdk.Constants.KEY_Down: Move(1); return true;
                case Gdk.Constants.KEY_Up: Move(-1); return true;
                case Gdk.Constants.KEY_Page_Down: Move(10); return true;
                case Gdk.Constants.KEY_Page_Up: Move(-10); return true;
                case Gdk.Constants.KEY_Return when (e.State & Gdk.ModifierType.ControlMask) != 0: SendToPanel(); return true;
            }
            return false;
        };
        window.AddController(keys);
    }

    /// <summary>Opens with items from <paramref name="source"/> (query → rows) instead of the index: recent files, file structure.</summary>
    public void Open(PaletteMode newMode, Func<string, CancellationToken, IReadOnlyList<(Location, string)>> source, string placeholder)
    {
        Open(newMode);
        customSource = source;
        entry.PlaceholderText = placeholder;
        status.SetText("");
        Schedule();
    }

    public void Open(PaletteMode newMode, string initial = "")
    {
        customSource = null;
        mode = newMode;
        options.Visible = mode == PaletteMode.Text;
        entry.PlaceholderText = mode switch
        {
            PaletteMode.Files => "Go to file (fuzzy: usrctl → UserController.cs)",
            PaletteMode.Symbols => "Go to symbol (Type, Type.Member)",
            _ => $"Find in files (at least {SearchService.MinTrigramLength} characters)",
        };
        entry.SetText(initial);
        list.RemoveAll();
        results.Clear();
        status.SetText(search() is null ? "Open a folder first." : "");
        window.Present();
        entry.GrabFocus();
        entry.SelectRegion(0, -1);
        Schedule();
    }

    void Move(int delta)
    {
        var index = Math.Clamp((list.GetSelectedRow()?.GetIndex() ?? -1) + delta, 0, Math.Max(0, results.Count - 1));
        if (list.GetRowAtIndex(index) is { } row)
        {
            list.SelectRow(row);
            row.GrabFocus();
            entry.GrabFocus();
            entry.SetPosition(-1);
        }
    }

    void Schedule()
    {
        Ui.Cancel(ref debounce);
        debounce = Ui.After(mode == PaletteMode.Text ? 200 : 60, () =>
        {
            debounce = 0;
            _ = RunAsync();
        });
    }

    async Task RunAsync()
    {
        var service = search();
        var source = customSource;
        if (service is null && source is null) return;
        cts?.Cancel();
        var token = (cts = new CancellationTokenSource()).Token;
        var query = entry.GetText();
        var currentMode = mode;
        var textOptions = new TextSearchOptions(matchCase.Active, wholeWord.Active);
        try
        {
            var found = await Task.Run(() => source is not null ? source(query, token).ToList() : currentMode switch
            {
                PaletteMode.Files => service!.QuickOpen(query, MaxRows, token)
                    .Select(h => (new Location(h.Path, 0, 1), $"{h.Name}\t{h.RelativePath}{(h.Project is null ? "" : $"  [{h.Project}]")}")).ToList(),
                PaletteMode.Symbols => service!.Symbols(query, MaxRows, token)
                    .Select(h => (new Location(h.Path, h.Line, h.Column, h.Name.Length), $"{h.Name}\t{h.Kind}  {h.Container}  —  {h.RelativePath}:{h.Line}")).ToList(),
                _ => service!.FullText(query, textOptions, token)
                    .Select(h => (new Location(h.Path, h.Line, h.Column, query.Length), $"{h.LineText}\t{h.RelativePath}:{h.Line}")).ToList(),
            }, token);
            if (token.IsCancellationRequested) return;
            results = found;
            Render();
            status.SetText(currentMode == PaletteMode.Text && query.Length < SearchService.MinTrigramLength
                ? $"Type at least {SearchService.MinTrigramLength} characters."
                : $"{found.Count}{(found.Count >= MaxRows && currentMode != PaletteMode.Text ? "+" : "")} result(s)");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { status.SetText(e.Message); }
    }

    void Render()
    {
        list.RemoveAll();
        foreach (var (_, text) in results.Take(MaxRows))
        {
            var parts = text.Split('\t', 2);
            var row = Ui.HBox(10);
            var main = Ui.Label(parts[0], "palette-main");
            main.Ellipsize = Pango.EllipsizeMode.End;
            main.MaxWidthChars = 60;
            row.Append(main);
            if (parts.Length > 1)
            {
                var detail = Ui.Label(parts[1], "dim-label");
                detail.Ellipsize = Pango.EllipsizeMode.Start;
                detail.Hexpand = true;
                row.Append(detail);
            }
            list.Append(row);
        }
        if (list.GetRowAtIndex(0) is { } first) list.SelectRow(first);
    }

    void Accept(int index)
    {
        if (index < 0 || index >= results.Count) return;
        window.Close();
        Navigate?.Invoke(results[index].Location);
    }

    void SendToPanel()
    {
        if (mode != PaletteMode.Text || results.Count == 0) return;
        window.Close();
        ShowInPanel?.Invoke($"“{entry.GetText()}” — {results.Count} match(es)", results);
    }
}
