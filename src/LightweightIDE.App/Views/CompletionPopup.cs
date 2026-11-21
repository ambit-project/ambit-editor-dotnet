using LightweightIDE.Language.Completion;

namespace LightweightIDE.App.Views;

/// <summary>
/// Completion list shown under the caret. Keeps keyboard focus in the editor: the editor forwards Up/Down/Enter/Tab/Escape.
/// </summary>
public sealed class CompletionPopup
{
    const int MaxVisibleItems = 200;
    readonly Gtk.Popover popover = Gtk.Popover.New();
    readonly Gtk.ListBox list = Gtk.ListBox.New();
    readonly Gtk.Label description = Ui.Label("", "completion-description");
    IReadOnlyList<CompletionEntry> shown = [];

    /// <summary>Picks remembered by the provider; they rise in the list.</summary>
    public CompletionUsage? Usage { get; set; }
    /// <summary>The highlighted item changed (to load its description).</summary>
    public event Action<CompletionEntry>? SelectionChanged;

    public CompletionSession? Session { get; private set; }
    public bool IsVisible => popover.Visible;
    public event Action<CompletionEntry>? Committed;

    public CompletionPopup(Gtk.Widget parent)
    {
        popover.SetParent(parent);
        popover.Autohide = false;
        popover.HasArrow = false;
        popover.CanFocus = false;
        popover.AddCssClass("completion");
        popover.Position = Gtk.PositionType.Bottom;
        list.SelectionMode = Gtk.SelectionMode.Browse;
        list.CanFocus = false;
        list.OnRowActivated += (_, e) => Commit(e.Row.GetIndex());
        list.OnRowSelected += (_, e) =>
        {
            if (e.Row is not null && shown.ElementAtOrDefault(e.Row.GetIndex()) is { } entry) SelectionChanged?.Invoke(entry);
        };
        var scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(list);
        scrolled.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scrolled.MaxContentHeight = 260;
        scrolled.PropagateNaturalHeight = true;
        scrolled.MinContentWidth = 360;
        scrolled.PropagateNaturalWidth = true;
        var box = Ui.VBox(4);
        box.Append(scrolled);
        description.Wrap = true;
        description.MaxWidthChars = 70;
        description.Selectable = false;
        description.Visible = false;
        box.Append(description);
        popover.SetChild(box);
    }

    public void Show(CompletionSession session, string typed, Gdk.Rectangle caret)
    {
        Session = session;
        popover.SetPointingTo(caret);
        if (!Filter(typed)) return;
        popover.Popup();
    }

    /// <summary>Re-filters with the text typed so far; hides the popup when nothing matches.</summary>
    public bool Filter(string typed)
    {
        if (Session is null) { Hide(); return false; }
        shown = CompletionProvider.Filter(Session.Items, typed, Session.IsPlainText, Usage).Take(MaxVisibleItems).ToList();
        if (shown.Count == 0) { Hide(); return false; }
        list.RemoveAll();
        foreach (var item in shown)
        {
            var row = Ui.HBox(8);
            row.Append(Ui.Label(KindGlyph(item.Kind), "completion-kind"));
            var name = Ui.Label(item.Smart is not null || item.TargetMatch ? "★ " + item.DisplayText : item.DisplayText, item.Smart is not null || item.TargetMatch ? "completion-smart" : null);
            name.Hexpand = true;
            row.Append(name);
            if (item.Suffix is not null) row.Append(Ui.Label(item.Suffix, item.Smart is not null ? "completion-smart-reason" : "dim-label"));
            list.Append(row);
        }
        list.SelectRow(list.GetRowAtIndex(0));
        return true;
    }

    public void Move(int delta)
    {
        var current = list.GetSelectedRow()?.GetIndex() ?? 0;
        var next = Math.Clamp(current + delta, 0, shown.Count - 1);
        var row = list.GetRowAtIndex(next);
        if (row is null) return;
        list.SelectRow(row);
        row.GrabFocus(); // scrolls into view; focus returns to the editor right after
    }

    public void CommitSelected() => Commit(list.GetSelectedRow()?.GetIndex() ?? 0);

    void Commit(int index)
    {
        if (index < 0 || index >= shown.Count) return;
        var entry = shown[index];
        Hide();
        Committed?.Invoke(entry);
    }

    /// <summary>Signature / documentation of the highlighted item (null hides the area).</summary>
    public void SetDescription(string? text)
    {
        description.SetText(text ?? "");
        description.Visible = !string.IsNullOrWhiteSpace(text);
    }

    public void Hide()
    {
        if (popover.Visible) popover.Popdown();
    }

    public void Clear() => Session = null;

    static string KindGlyph(string kind) => kind switch
    {
        "Method" or "ExtensionMethod" => "ƒ",
        "Property" => "◆",
        "Field" or "Constant" => "▪",
        "Event" => "⚡",
        "Class" or "Record" => "C",
        "Structure" or "RecordStruct" => "S",
        "Interface" => "I",
        "Enum" => "E",
        "EnumMember" => "e",
        "Namespace" => "{}",
        "Local" or "Parameter" or "RangeVariable" => "x",
        "Keyword" => "k",
        "Snippet" => "✂",
        "Delegate" => "D",
        "TypeParameter" => "T",
        _ => "·",
    };
}
