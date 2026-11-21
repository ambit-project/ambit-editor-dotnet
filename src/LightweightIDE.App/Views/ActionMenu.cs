namespace LightweightIDE.App.Views;

/// <summary>A keyboard-driven list at the caret (Alt+Enter actions, file structure): Up/Down, Enter, Escape.</summary>
public sealed class ActionMenu
{
    readonly Gtk.Popover popover = Gtk.Popover.New();
    readonly Gtk.ListBox list = Gtk.ListBox.New();
    IReadOnlyList<(string Title, string? Detail)> items = [];
    Action<int>? chosen;

    public ActionMenu()
    {
        popover.AddCssClass("action-menu");
        popover.HasArrow = false;
        popover.Autohide = true;
        list.SelectionMode = Gtk.SelectionMode.Browse;
        list.OnRowActivated += (_, e) => Choose(e.Row.GetIndex());
        var scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(list);
        scrolled.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scrolled.MaxContentHeight = 360;
        scrolled.PropagateNaturalHeight = true;
        scrolled.PropagateNaturalWidth = true;
        popover.SetChild(scrolled);

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval is Gdk.Constants.KEY_Return or Gdk.Constants.KEY_KP_Enter)
            {
                Choose(list.GetSelectedRow()?.GetIndex() ?? 0);
                return true;
            }
            return false;
        };
        popover.AddController(keys);
    }

    /// <summary>Shows the items under a rectangle of <paramref name="parent"/>; <paramref name="onChosen"/> gets the index.</summary>
    public void Show(Gtk.Widget parent, Gdk.Rectangle at, IReadOnlyList<(string Title, string? Detail)> entries, Action<int> onChosen)
    {
        if (popover.Parent is { } old && old.Handle.DangerousGetHandle() != parent.Handle.DangerousGetHandle()) popover.Unparent();
        if (popover.Parent is null) popover.SetParent(parent);
        items = entries;
        chosen = onChosen;
        list.RemoveAll();
        foreach (var (title, detail) in items)
        {
            var row = Ui.HBox(12);
            var label = Ui.Label(title);
            label.Hexpand = true;
            row.Append(label);
            if (detail is not null) row.Append(Ui.Label(detail, "dim-label"));
            list.Append(row);
        }
        popover.SetPointingTo(at);
        popover.Popup();
        if (list.GetRowAtIndex(0) is { } first)
        {
            list.SelectRow(first);
            first.GrabFocus();
        }
    }

    void Choose(int index)
    {
        popover.Popdown();
        if (index >= 0 && index < items.Count) chosen?.Invoke(index);
    }
}
