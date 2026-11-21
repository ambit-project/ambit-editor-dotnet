namespace LightweightIDE.App.Views;

/// <summary>Find / replace bar above the editor (Ctrl+F, Ctrl+H, F3, Shift+F3). Works on the active tab.</summary>
public sealed class FindBar
{
    readonly Gtk.Revealer revealer = Gtk.Revealer.New();
    readonly Gtk.SearchEntry find = Gtk.SearchEntry.New();
    readonly Gtk.Entry replace = Gtk.Entry.New();
    readonly Gtk.Box replaceRow = Ui.HBox(4);
    readonly Gtk.Label count = Ui.Label("", "dim-label");
    readonly Gtk.CheckButton matchCase = Gtk.CheckButton.NewWithLabel("Aa");
    readonly Gtk.CheckButton wholeWord = Gtk.CheckButton.NewWithLabel("Word");
    readonly Gtk.CheckButton regex = Gtk.CheckButton.NewWithLabel(".*");
    readonly Func<EditorTab?> activeTab;

    public Gtk.Widget Widget => revealer;
    public bool IsOpen => revealer.RevealChild;

    public FindBar(Func<EditorTab?> activeTab)
    {
        this.activeTab = activeTab;
        var box = Ui.VBox(4);
        box.AddCssClass("findbar");

        var findRow = Ui.HBox(4);
        find.PlaceholderText = "Find";
        find.Hexpand = true;
        find.OnSearchChanged += (_, _) => { Apply(); Next(forward: true, fromSelectionStart: true); };
        find.OnActivate += (_, _) => Next(true);
        find.OnStopSearch += (_, _) => Close();
        matchCase.TooltipText = "Match case";
        wholeWord.TooltipText = "Whole word";
        regex.TooltipText = "Regular expression";
        foreach (var c in new[] { matchCase, wholeWord, regex }) c.OnToggled += (_, _) => Apply();
        findRow.Append(find);
        findRow.Append(count);
        findRow.Append(matchCase);
        findRow.Append(wholeWord);
        findRow.Append(regex);
        var prev = Ui.IconButton("go-up-symbolic", "Previous (Shift+F3)");
        prev.OnClicked += (_, _) => Next(false);
        var next = Ui.IconButton("go-down-symbolic", "Next (F3)");
        next.OnClicked += (_, _) => Next(true);
        var close = Ui.IconButton("window-close-symbolic", "Close (Esc)");
        close.OnClicked += (_, _) => Close();
        findRow.Append(prev);
        findRow.Append(next);
        findRow.Append(close);
        box.Append(findRow);

        replace.PlaceholderText = "Replace";
        replace.Hexpand = true;
        replace.OnActivate += (_, _) => ReplaceOne();
        var one = Gtk.Button.NewWithLabel("Replace");
        one.OnClicked += (_, _) => ReplaceOne();
        var all = Gtk.Button.NewWithLabel("Replace All");
        all.OnClicked += (_, _) => ReplaceAll();
        replaceRow.Append(replace);
        replaceRow.Append(one);
        replaceRow.Append(all);
        box.Append(replaceRow);

        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval == Gdk.Constants.KEY_Escape) { Close(); return true; }
            return false;
        };
        box.AddController(keys);
        revealer.SetChild(box);
        // With transition None a hidden GtkRevealer still reserves its child's height, so visibility is toggled too.
        revealer.TransitionType = Gtk.RevealerTransitionType.None;
        revealer.Visible = false;
    }

    public void Open(bool withReplace)
    {
        replaceRow.Visible = withReplace;
        revealer.Visible = true;
        revealer.RevealChild = true;
        if (activeTab() is { } tab)
        {
            var s = new Gtk.TextIter();
            var e = new Gtk.TextIter();
            if (tab.Buffer.GetSelectionBounds(out s, out e) && s.GetLine() == e.GetLine())
                find.SetText(tab.Buffer.GetText(s, e, false));
        }
        find.GrabFocus();
        find.SelectRegion(0, -1);
        Apply();
    }

    public void Close()
    {
        revealer.RevealChild = false;
        revealer.Visible = false;
        if (activeTab() is { } tab)
        {
            tab.SearchSettings.SearchText = null;
            tab.Focus();
        }
    }

    /// <summary>Copies the bar's settings into the active tab's search context.</summary>
    public void Apply()
    {
        if (activeTab() is not { } tab) return;
        var s = tab.SearchSettings;
        s.CaseSensitive = matchCase.Active;
        s.AtWordBoundaries = wholeWord.Active;
        s.RegexEnabled = regex.Active;
        s.SearchText = IsOpen && find.GetText().Length > 0 ? find.GetText() : null;
        Ui.After(30, UpdateCount);
    }

    void UpdateCount()
    {
        var n = activeTab()?.Search.OccurrencesCount ?? 0;
        count.SetText(find.GetText().Length == 0 ? "" : n < 0 ? "…" : n == 0 ? "No results" : $"{n} found");
    }

    public void Next(bool forward, bool fromSelectionStart = false)
    {
        if (activeTab() is not { } tab) return;
        if (tab.SearchSettings.SearchText is null)
        {
            if (find.GetText().Length == 0) { Open(false); return; }
            Apply();
        }
        var selStart = new Gtk.TextIter();
        var selEnd = new Gtk.TextIter();
        tab.Buffer.GetSelectionBounds(out selStart, out selEnd);
        var from = forward ? (fromSelectionStart ? selStart : selEnd) : selStart;
        var start = new Gtk.TextIter();
        var end = new Gtk.TextIter();
        var wrapped = false;
        var found = forward ? tab.Search.Forward(from, out start, out end, out wrapped) : tab.Search.Backward(from, out start, out end, out wrapped);
        if (!found) { UpdateCount(); return; }
        tab.Buffer.SelectRange(start, end);
        tab.View.ScrollToMark(tab.Buffer.GetInsert(), 0.1, false, 0, 0);
        UpdateCount();
    }

    void ReplaceOne()
    {
        if (activeTab() is not { } tab) return;
        var s = new Gtk.TextIter();
        var e = new Gtk.TextIter();
        if (tab.Buffer.GetSelectionBounds(out s, out e))
        {
            try { tab.Search.Replace(s, e, replace.GetText(), -1); }
            catch (GLib.GException) { /* selection is not a match */ }
        }
        Next(true);
    }

    void ReplaceAll()
    {
        if (activeTab() is not { } tab) return;
        try
        {
            var n = tab.Search.ReplaceAll(replace.GetText(), -1);
            count.SetText($"{n} replaced");
        }
        catch (GLib.GException ex) { count.SetText(ex.Message); }
    }
}
