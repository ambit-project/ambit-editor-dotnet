using LightweightIDE.Editor;
using LightweightIDE.Editor.FileOperations;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Views;

/// <summary>Tabs of open files plus the find bar. Owns dirty-document protection on close.</summary>
public sealed class EditorArea
{
    readonly Gtk.Box root = Ui.VBox();
    readonly Gtk.Notebook notebook = Gtk.Notebook.New();
    readonly Gtk.Stack stack = Gtk.Stack.New();
    readonly List<EditorTab> tabs = [];
    readonly EditorServices services;
    readonly Func<Gtk.Window> window;
    GtkSource.StyleScheme? scheme;

    public FindBar FindBar { get; }
    public Gtk.Widget Widget => root;
    public IReadOnlyList<EditorTab> Tabs => tabs;

    public event Action<EditorTab?>? ActiveTabChanged;
    public event Action<EditorTab>? TabCursorMoved;
    public event Action<EditorTab>? TabDiagnosticsUpdated;
    public event Action<EditorTab>? TabSaved;
    public event Action<EditorTab>? TabClosed;
    public event Action<EditorTab>? GoToDefinitionRequested;
    public event Action<EditorTab>? RenameRequested;
    public event Action<string>? OpenFileRequested;
    public event Action<EditorTab>? TabOpened;
    public event Action<EditorTab, int>? BreakpointToggleRequested;
    public event Action<EditorTab, int>? BreakpointEditRequested;
    public event Action<EditorTab, int>? HttpSendRequested;
    public event Action<EditorTab, string, bool>? TestRunRequested;
    public event Action<EditorTab>? FormatOnTypeRequested;
    public event Action<EditorTab, string, int, int>? NavigateRequested;
    public event Action<EditorTab, int>? ImplementationsRequested;

    public EditorArea(EditorServices services, Func<Gtk.Window> window)
    {
        this.services = services;
        this.window = window;
        FindBar = new FindBar(() => Active);
        notebook.Scrollable = true;
        notebook.ShowBorder = false;
        notebook.Vexpand = true;
        notebook.AddCssClass("editor-tabs");
        notebook.OnSwitchPage += (_, e) => Ui.Post(() =>
        {
            FindBar.Apply();
            ActiveTabChanged?.Invoke(Active);
        });

        var empty = Ui.VBox(6);
        empty.Valign = Gtk.Align.Center;
        empty.Append(Ui.Label("No file open", "empty-title", 0.5f));
        empty.Append(Ui.Label("Ctrl+P  Quick Open      Ctrl+Shift+P  Go to Symbol      Ctrl+Shift+F  Find in Files", "dim-label", 0.5f));
        stack.AddNamed(empty, "empty");
        stack.AddNamed(notebook, "tabs");
        stack.Vexpand = true;
        stack.VisibleChildName = "empty";

        root.Append(FindBar.Widget);
        root.Append(stack);

        // Detect external changes to open files.
        Ui.Every(2000, () =>
        {
            foreach (var tab in tabs.ToList()) tab.CheckDisk();
            return true;
        });
    }

    public EditorTab? Active
    {
        get
        {
            var page = notebook.GetCurrentPage();
            if (page < 0) return null;
            var widget = notebook.GetNthPage(page);
            return tabs.FirstOrDefault(t => t.Widget.Handle.DangerousGetHandle() == widget?.Handle.DangerousGetHandle());
        }
    }

    public EditorTab? Find(string path) => tabs.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.Ordinal));

    /// <summary>Opens a file (or activates its tab) and optionally moves the caret to a 1-based line/column.</summary>
    public EditorTab? Open(string path, int line = 0, int column = 1, int selectLength = 0, bool focus = true)
    {
        path = Path.GetFullPath(path);
        var tab = Find(path);
        if (tab is null)
        {
            if (!File.Exists(path)) return null;
            try
            {
                if (TextFile.LooksBinary(path))
                {
                    _ = Ui.ShowErrorAsync(window(), "Cannot open binary file", path);
                    return null;
                }
                var document = EditorDocument.Open(path);
                tab = new EditorTab(document, services, scheme);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                services.Logger.LogWarning(e, "Cannot open {Path}", path);
                _ = Ui.ShowErrorAsync(window(), "Cannot open file", e.Message);
                return null;
            }
            tab.CloseRequested += t => _ = CloseAsync(t);
            tab.CursorMoved += t => TabCursorMoved?.Invoke(t);
            tab.DiagnosticsUpdated += t => TabDiagnosticsUpdated?.Invoke(t);
            tab.Saved += t => TabSaved?.Invoke(t);
            tab.GoToDefinitionRequested += t => GoToDefinitionRequested?.Invoke(t);
            tab.RenameRequested += t => RenameRequested?.Invoke(t);
            tab.OpenFileRequested += p => OpenFileRequested?.Invoke(p);
            tab.BreakpointToggleRequested += (t, line) => BreakpointToggleRequested?.Invoke(t, line);
            tab.BreakpointEditRequested += (t, line) => BreakpointEditRequested?.Invoke(t, line);
            tab.HttpSendRequested += (t, line) => HttpSendRequested?.Invoke(t, line);
            tab.TestRunRequested += (t, name, debug) => TestRunRequested?.Invoke(t, name, debug);
            tab.FormatOnTypeRequested += t => FormatOnTypeRequested?.Invoke(t);
            tab.NavigateRequested += (t, path, line, column) => NavigateRequested?.Invoke(t, path, line, column);
            tab.ImplementationsRequested += (t, offset) => ImplementationsRequested?.Invoke(t, offset);
            tabs.Add(tab);
            var index = notebook.AppendPage(tab.Widget, tab.TabLabel);
            notebook.SetTabReorderable(tab.Widget, true);
            stack.VisibleChildName = "tabs";
            notebook.SetCurrentPage(index);
            TabOpened?.Invoke(tab);
        }
        else notebook.SetCurrentPage(notebook.PageNum(tab.Widget));

        if (line > 0) tab.GoTo(line, column, selectLength);
        if (focus) tab.Focus();
        return tab;
    }

    public void Activate(EditorTab tab) => notebook.SetCurrentPage(notebook.PageNum(tab.Widget));

    public void NextTab(int delta)
    {
        var n = notebook.GetNPages();
        if (n == 0) return;
        notebook.SetCurrentPage(((notebook.GetCurrentPage() + delta) % n + n) % n);
        Active?.Focus();
    }

    /// <summary>Closes a tab; asks to save when dirty. Returns false when the user cancelled.</summary>
    public async Task<bool> CloseAsync(EditorTab tab)
    {
        if (tab.IsDirty)
        {
            Activate(tab);
            var choice = await Ui.ChooseAsync(window(), $"Save changes to {tab.Document.FileName}?",
                "Your changes will be lost if you don't save them.", ["Cancel", "Don't Save", "Save"], 0, 2);
            if (choice == 0) return false;
            if (choice == 2 && !TrySave(tab)) return false;
        }
        var page = notebook.PageNum(tab.Widget);
        if (page >= 0) notebook.RemovePage(page);
        tabs.Remove(tab);
        tab.Dispose();
        TabClosed?.Invoke(tab);
        if (tabs.Count == 0)
        {
            stack.VisibleChildName = "empty";
            ActiveTabChanged?.Invoke(null);
        }
        return true;
    }

    /// <summary>Closes all tabs, asking once per dirty file. False when cancelled.</summary>
    public async Task<bool> CloseAllAsync()
    {
        foreach (var tab in tabs.ToList())
            if (!await CloseAsync(tab)) return false;
        return true;
    }

    public bool TrySave(EditorTab tab)
    {
        try
        {
            tab.Save();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = Ui.ShowErrorAsync(window(), $"Cannot save {tab.Document.FileName}", e.Message);
            return false;
        }
    }

    public void SaveAll()
    {
        foreach (var tab in tabs.Where(t => t.IsDirty)) TrySave(tab);
    }

    public bool HasDirty => tabs.Any(t => t.IsDirty);

    public void SetStyleScheme(GtkSource.StyleScheme? newScheme)
    {
        scheme = newScheme;
        foreach (var tab in tabs) tab.SetStyleScheme(newScheme);
    }

    /// <summary>A file was renamed or moved in the explorer.</summary>
    public void PathRenamed(string oldPath, string newPath)
    {
        foreach (var tab in tabs)
        {
            if (tab.Path == oldPath) tab.Renamed(newPath);
            else if (tab.Path.StartsWith(oldPath.TrimEnd('/') + "/", StringComparison.Ordinal))
                tab.Renamed(newPath.TrimEnd('/') + tab.Path[oldPath.TrimEnd('/').Length..]);
        }
    }

    public void RefreshAnalysis()
    {
        foreach (var tab in tabs) tab.RefreshAnalysis();
    }
}
