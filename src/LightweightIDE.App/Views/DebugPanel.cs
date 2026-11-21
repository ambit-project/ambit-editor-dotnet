using LightweightIDE.DotNet.Debugging;

namespace LightweightIDE.App.Views;

/// <summary>
/// Debug page of the bottom panel: run controls, call stack, variables (expandable, loaded on demand) and watch
/// expressions that are evaluated again at every stop.
/// </summary>
public sealed class DebugPanel
{
    readonly Gtk.Box root = Ui.VBox();
    readonly Gtk.Label state = Ui.Label("Not debugging. F5 starts the startup project.", "dim-label");
    readonly Gtk.ListBox frames = Gtk.ListBox.New();
    readonly Gtk.ListBox variables = Gtk.ListBox.New();
    readonly Gtk.Entry watchEntry = Gtk.Entry.New();
    readonly List<VariableRow> rows = [];
    readonly List<string> watches = [];
    List<DebugFrame> shownFrames = [];
    bool suppressFrameSelection;

    sealed class VariableRow(Gtk.ListBoxRow row, DebugVariable variable, int depth, Gtk.Button? expander)
    {
        public Gtk.ListBoxRow Row { get; } = row;
        public DebugVariable Variable { get; } = variable;
        public int Depth { get; } = depth;
        public Gtk.Button? Expander { get; } = expander;
        public bool Expanded { get; set; }
    }

    public Gtk.Widget Widget => root;

    /// <summary>The user picked a frame in the call stack.</summary>
    public event Action<DebugFrame>? FrameSelected;
    /// <summary>Loads the children of a variable reference.</summary>
    public Func<int, Task<IReadOnlyList<DebugVariable>>>? LoadChildren { get; set; }
    /// <summary>Evaluates a watch expression in the selected frame.</summary>
    public Func<string, Task<DebugVariable>>? Evaluate { get; set; }

    public DebugPanel()
    {
        var bar = Ui.HBox(2);
        bar.AddCssClass("panel-toolbar");
        bar.Append(Ui.IconButton("media-playback-start-symbolic", "Start / Continue (F5)", "win.debug-start"));
        bar.Append(Ui.IconButton("media-playback-pause-symbolic", "Pause", "win.debug-pause"));
        bar.Append(Ui.IconButton("media-playback-stop-symbolic", "Stop (Shift+F5)", "win.debug-stop"));
        bar.Append(Ui.TextButton("Step Over", "Step Over (F10)", "win.debug-step-over"));
        bar.Append(Ui.TextButton("Step Into", "Step Into (F11)", "win.debug-step-into"));
        bar.Append(Ui.TextButton("Step Out", "Step Out (Shift+F11)", "win.debug-step-out"));
        bar.Append(Ui.TextButton("Remove All Breakpoints", "Remove all breakpoints in all files (Ctrl+Shift+F9)", "win.remove-breakpoints"));
        state.MarginStart = 10;
        state.Hexpand = true;
        state.Ellipsize = Pango.EllipsizeMode.End;
        bar.Append(state);
        root.Append(bar);

        var paned = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        paned.Vexpand = true;

        var left = Ui.VBox();
        left.Append(Ui.Label("CALL STACK", "panel-title"));
        frames.AddCssClass("result-list");
        frames.SelectionMode = Gtk.SelectionMode.Single;
        frames.OnRowSelected += (_, e) =>
        {
            if (suppressFrameSelection || e.Row is null) return;
            if (shownFrames.ElementAtOrDefault(e.Row.GetIndex()) is { } frame) FrameSelected?.Invoke(frame);
        };
        left.Append(Ui.Scrolled(frames));
        paned.SetStartChild(left);

        var right = Ui.VBox();
        right.Append(Ui.Label("VARIABLES", "panel-title"));
        variables.AddCssClass("result-list");
        variables.SelectionMode = Gtk.SelectionMode.None;
        right.Append(Ui.Scrolled(variables));
        watchEntry.PlaceholderText = "Add watch expression (Enter)";
        watchEntry.AddCssClass("monospace");
        watchEntry.OnActivate += (_, _) =>
        {
            var expression = watchEntry.GetText().Trim();
            if (expression.Length == 0) return;
            watches.Add(expression);
            watchEntry.SetText("");
            _ = RefreshWatchesAsync();
        };
        right.Append(watchEntry);
        paned.SetEndChild(right);
        paned.Position = 420;
        root.Append(paned);
    }

    public void SetState(string text) => state.SetText(text);

    public void Clear()
    {
        frames.RemoveAll();
        shownFrames = [];
        variables.RemoveAll();
        rows.Clear();
    }

    public void ShowFrames(IReadOnlyList<DebugFrame> list, int selected = 0)
    {
        suppressFrameSelection = true;
        frames.RemoveAll();
        shownFrames = list.ToList();
        foreach (var frame in shownFrames)
        {
            var row = Ui.HBox(8);
            var name = Ui.Label(frame.Name, frame.Path is null ? "dim-label" : null);
            name.Hexpand = true;
            name.Ellipsize = Pango.EllipsizeMode.End;
            row.Append(name);
            if (frame.Path is not null) row.Append(Ui.Label($"{Path.GetFileName(frame.Path)}:{frame.Line}", "result-location"));
            frames.Append(row);
        }
        if (frames.GetRowAtIndex(selected) is { } r) frames.SelectRow(r);
        suppressFrameSelection = false;
    }

    /// <summary>Shows scopes (Locals, …) with their variables; watch expressions come first.</summary>
    public async Task ShowVariablesAsync(IReadOnlyList<(string Scope, IReadOnlyList<DebugVariable> Variables)> scopes)
    {
        variables.RemoveAll();
        rows.Clear();
        await RefreshWatchesAsync();
        foreach (var (scope, list) in scopes)
        {
            if (scopes.Count > 1)
            {
                var header = Gtk.ListBoxRow.New();
                header.SetChild(Ui.Label(scope, "panel-title"));
                header.Activatable = false;
                variables.Append(header);
                // Kept in rows (depth -1) so row indexes always match the list box.
                rows.Add(new VariableRow(header, new DebugVariable(scope, "", null, 0), -1, null));
            }
            foreach (var variable in list) AddRow(variable, 0, -1);
        }
    }

    async Task RefreshWatchesAsync()
    {
        // Remove previous watch rows (they are at the top, depth 0, marked by the "watch" class).
        foreach (var row in rows.Where(r => r.Row.HasCssClass("watch")).ToList())
        {
            RemoveChildren(row);
            variables.Remove(row.Row);
            rows.Remove(row);
        }
        if (Evaluate is null) return;
        var position = 0;
        foreach (var expression in watches)
        {
            DebugVariable value;
            try { value = await Evaluate(expression); }
            catch (Exception e) { value = new DebugVariable(expression, e.Message, null, 0); }
            var added = AddRow(value with { Name = expression }, 0, position++);
            added.Row.AddCssClass("watch");
        }
    }

    VariableRow AddRow(DebugVariable variable, int depth, int position)
    {
        var box = Ui.HBox(6);
        box.MarginStart = depth * 16;
        Gtk.Button? expander = null;
        if (variable.Reference > 0)
        {
            expander = Gtk.Button.NewWithLabel("▸");
            expander.AddCssClass("flat");
            expander.AddCssClass("tree-expander");
            box.Append(expander);
        }
        else box.Append(Ui.Label("  "));
        box.Append(Ui.Label(variable.Name, "variable-name"));
        var value = Ui.Label(variable.Value, "monospace");
        value.Hexpand = true;
        value.Ellipsize = Pango.EllipsizeMode.End;
        value.Selectable = true;
        value.TooltipText = variable.Value;
        box.Append(value);
        if (variable.Type is { Length: > 0 } type) box.Append(Ui.Label(type, "dim-label"));

        var listRow = Gtk.ListBoxRow.New();
        listRow.SetChild(box);
        var row = new VariableRow(listRow, variable, depth, expander);
        if (position < 0)
        {
            variables.Append(listRow);
            rows.Add(row);
        }
        else
        {
            variables.Insert(listRow, position);
            rows.Insert(position, row);
        }
        if (expander is not null) expander.OnClicked += (_, _) => _ = ToggleAsync(row);
        return row;
    }

    async Task ToggleAsync(VariableRow row)
    {
        if (row.Expanded)
        {
            RemoveChildren(row);
            row.Expanded = false;
            row.Expander?.SetLabel("▸");
            return;
        }
        if (LoadChildren is null) return;
        row.Expanded = true;
        row.Expander?.SetLabel("▾");
        IReadOnlyList<DebugVariable> children;
        try { children = await LoadChildren(row.Variable.Reference); }
        catch (Exception e) { children = [new DebugVariable("error", e.Message, null, 0)]; }
        var position = row.Row.GetIndex() + 1;
        foreach (var child in children.Take(500)) AddRow(child, row.Depth + 1, position++);
    }

    void RemoveChildren(VariableRow row)
    {
        var index = rows.IndexOf(row);
        if (index < 0) return;
        while (index + 1 < rows.Count && rows[index + 1].Depth > row.Depth)
        {
            variables.Remove(rows[index + 1].Row);
            rows.RemoveAt(index + 1);
        }
    }
}
