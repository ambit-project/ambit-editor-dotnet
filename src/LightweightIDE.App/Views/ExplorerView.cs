using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.DotNet.SolutionDiscovery;

namespace LightweightIDE.App.Views;

/// <summary>
/// Solution explorer with two tabs: Solution (solution folders, projects, dependencies) and Files (the workspace
/// folder on disk). Each tab is a lazy GtkTreeListModel: a node's children are listed only when the node is shown
/// with an expander or expanded, one directory level at a time. The Files tree is built only when its tab is first
/// opened. Items are GtkStringObjects holding a node key ("kind|value"), so no custom GObject types are needed.
/// Without a solution only the Files tab is shown.
/// </summary>
public sealed class ExplorerView
{
    const string Sln = "sln", SFolder = "sfolder", Proj = "proj", Dir = "dir", File = "file";
    // Dependencies of a project: Deps (value = project) → DepProjects / DepPackages (value = project)
    // → ProjRef (value = "project|referenced project") and Package (value = "project|id|version").
    const string Deps = "deps", DepProjects = "depp", DepPackages = "depk", ProjRef = "pref", Package = "pkg";

    const int SolutionPage = 0, FilesPage = 1;

    /// <summary>One tab: its own tree model, so a directory shown in both tabs has a child store per tab.</summary>
    sealed class Pane
    {
        public readonly Gio.ListStore RootStore = Gio.ListStore.New(Gtk.StringObject.GetGType());
        public readonly Dictionary<string, Gio.ListStore> ChildStores = new(StringComparer.Ordinal);
        public Gtk.TreeListModel Tree = null!;
        public Gtk.SingleSelection Selection = null!;
        public Gtk.ListView List = null!;
        /// <summary>The root items were listed (Files is filled on its first show).</summary>
        public bool Loaded;
    }

    readonly Gtk.Box root = Ui.VBox();
    readonly Gtk.Notebook tabs = Gtk.Notebook.New();
    readonly Pane solutionPane, filesPane;
    readonly Gio.SimpleActionGroup actions = Gio.SimpleActionGroup.New();
    WorkspaceRoot? workspace;
    string? contextKey;
    string? startupProject;
    /// <summary>Kind of each project shown (read from its .csproj once per Load), for the icon and the menu.</summary>
    readonly Dictionary<string, ProjectKind> kinds = new(StringComparer.Ordinal);

    /// <summary>The startup project, shown in bold with "startup" after its name.</summary>
    public string? StartupProject
    {
        get => startupProject;
        set
        {
            if (startupProject == value) return;
            var old = startupProject;
            startupProject = value;
            foreach (var project in new[] { old, value })
                if (project is not null) RefreshRow($"{Proj}|{project}");
        }
    }

    public Gtk.Widget Widget => root;
    public event Action<string>? FileActivated;
    /// <summary>(action, path) for actions handled by the window: new-file, new-folder, rename, delete, terminal, build, run, debug, set-startup, test, nuget, copy-path.</summary>
    public event Action<string, string>? ActionRequested;
    /// <summary>Run Tests on a solution folder: the test projects under it.</summary>
    public event Action<string, IReadOnlyList<string>>? TestProjectsRequested;
    /// <summary>Add Existing Project…: the solution folder ("tests", "src/Shared") or null on the solution itself.</summary>
    public event Action<string?>? AddExistingProjectRequested;

    public ExplorerView()
    {
        solutionPane = CreatePane();
        filesPane = CreatePane();

        foreach (var name in new[] { "open", "new-file", "new-folder", "rename", "delete", "terminal", "reveal", "build", "run", "debug", "set-startup", "test", "nuget", "references", "remove-reference", "format", "add-project", "add-existing-project", "remove-from-solution", "copy-path" })
        {
            var action = Gio.SimpleAction.New(name, null);
            action.OnActivate += (_, _) => OnContextAction(name);
            actions.AddAction(action);
        }
        root.InsertActionGroup("explorer", actions);

        var header = Ui.HBox(2);
        header.AddCssClass("panel-header");
        var title = Ui.Label("EXPLORER", "panel-title");
        title.Hexpand = true;
        header.Append(title);
        var collapse = Ui.IconButton("view-restore-symbolic", "Collapse all");
        collapse.OnClicked += (_, _) => CollapseAll();
        header.Append(collapse);
        var refresh = Ui.IconButton("view-refresh-symbolic", "Refresh");
        refresh.OnClicked += (_, _) => Refresh();
        header.Append(refresh);
        root.Append(header);

        tabs.AddCssClass("explorer-tabs");
        tabs.ShowBorder = false;
        tabs.AppendPage(Ui.Scrolled(solutionPane.List), Ui.Label("Solution"));
        tabs.AppendPage(Ui.Scrolled(filesPane.List), Ui.Label("Files"));
        tabs.Vexpand = true;
        tabs.ShowTabs = false;
        tabs.OnSwitchPage += (_, e) => { if (e.PageNum == FilesPage) EnsureFilesLoaded(); };
        root.Append(tabs);
    }

    Pane CreatePane()
    {
        var pane = new Pane();
        pane.Tree = Gtk.TreeListModel.New(pane.RootStore, false, false, item => CreateChildren(pane, item));
        pane.Selection = Gtk.SingleSelection.New(pane.Tree);
        pane.Selection.Autoselect = false;
        pane.Selection.CanUnselect = true;

        var factory = Gtk.SignalListItemFactory.New();
        factory.OnSetup += (_, e) => Setup((Gtk.ListItem)e.Object);
        factory.OnBind += (_, e) => Bind((Gtk.ListItem)e.Object);
        pane.List = Gtk.ListView.New(pane.Selection, factory);
        pane.List.AddCssClass("explorer");
        pane.List.SingleClickActivate = false;
        pane.List.Vexpand = true;
        pane.List.OnActivate += (_, e) => Activate(pane, e.Position);

        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            var key = SelectedKey(pane);
            if (key is null) return false;
            switch (e.Keyval)
            {
                case Gdk.Constants.KEY_F2: contextKey = key; OnContextAction("rename"); return true;
                case Gdk.Constants.KEY_Delete: contextKey = key; OnContextAction("delete"); return true;
            }
            return false;
        };
        pane.List.AddController(keys);
        return pane;
    }

    Pane Current => tabs.GetCurrentPage() == FilesPage ? filesPane : solutionPane;

    public void Load(WorkspaceRoot? root)
    {
        workspace = root;
        kinds.Clear();
        foreach (var pane in new[] { solutionPane, filesPane })
        {
            pane.ChildStores.Clear();
            pane.RootStore.RemoveAll();
            pane.Loaded = false;
        }
        tabs.ShowTabs = root?.Solution is not null;
        if (root is null) return;
        if (root.Solution is not null)
        {
            solutionPane.RootStore.Append(Gtk.StringObject.New($"{Sln}|{root.Solution.Path}"));
            solutionPane.Loaded = true;
            Ui.Post(() => solutionPane.Tree.GetRow(0)?.SetExpanded(true));
            // The Files tree is listed when its tab is opened (EnsureFilesLoaded).
            if (tabs.GetCurrentPage() == FilesPage) EnsureFilesLoaded();
        }
        else
        {
            tabs.SetCurrentPage(FilesPage);
            EnsureFilesLoaded();
        }
    }

    /// <summary>Lists the workspace folder in the Files tab the first time it is needed.</summary>
    void EnsureFilesLoaded()
    {
        if (filesPane.Loaded || workspace is null) return;
        filesPane.Loaded = true;
        foreach (var key in ListDirectory(workspace.Folder)) filesPane.RootStore.Append(Gtk.StringObject.New(key));
    }

    // ---------- model ----------

    static (string Kind, string Value) Parse(string key)
    {
        var i = key.IndexOf('|');
        return (key[..i], key[(i + 1)..]);
    }

    Gio.ListModel? CreateChildren(Pane pane, GObject.Object item)
    {
        var key = ((Gtk.StringObject)item).GetString();
        var (kind, _) = Parse(key);
        if (kind is File or ProjRef or Package) return null;
        var store = Gio.ListStore.New(Gtk.StringObject.GetGType());
        foreach (var child in ChildKeys(key)) store.Append(Gtk.StringObject.New(child));
        pane.ChildStores[key] = store;
        return store;
    }

    IEnumerable<string> ChildKeys(string key)
    {
        var (kind, value) = Parse(key);
        var solution = workspace?.Solution;
        switch (kind)
        {
            case Sln when solution is not null:
                foreach (var f in solution.Folders.Where(f => f.ParentPath is null).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    yield return $"{SFolder}|{f.FolderPath}";
                foreach (var p in solution.Projects.Where(p => p.FolderPath is null)) yield return $"{Proj}|{p.Path}";
                break;
            case SFolder when solution is not null:
                foreach (var f in solution.Folders.Where(f => f.ParentPath == value).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    yield return $"{SFolder}|{f.FolderPath}";
                foreach (var p in solution.Projects.Where(p => p.FolderPath == value)) yield return $"{Proj}|{p.Path}";
                foreach (var file in solution.Folders.FirstOrDefault(f => f.FolderPath == value)?.Files ?? []) yield return $"{File}|{file}";
                break;
            case Proj:
                yield return $"{Deps}|{value}";
                foreach (var k in ListDirectory(Path.GetDirectoryName(value)!)) yield return k;
                break;
            case Deps:
                yield return $"{DepProjects}|{value}";
                yield return $"{DepPackages}|{value}";
                break;
            case DepProjects:
                foreach (var reference in ProjectReferences.Read(value).OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase))
                    yield return $"{ProjRef}|{value}|{reference}";
                break;
            case DepPackages:
                foreach (var (id, version) in ProjectReferences.ReadPackages(value)) yield return $"{Package}|{value}|{id}|{version}";
                break;
            case Dir:
                foreach (var k in ListDirectory(value)) yield return k;
                break;
        }
    }

    static IEnumerable<string> ListDirectory(string dir) =>
        FileTree.ListChildren(dir).Select(e => e.Kind == FileEntryKind.Directory ? $"{Dir}|{e.Path}" : $"{File}|{e.Path}");

    string LabelFor(string key)
    {
        var (kind, value) = Parse(key);
        return kind switch
        {
            Sln => $"{Path.GetFileName(value)}  ({workspace?.Solution?.Projects.Count ?? 0} projects)",
            SFolder => value.TrimEnd('/').Split('/').Last(),
            Proj => Path.GetFileNameWithoutExtension(value) + (value == startupProject ? "  (startup)" : ""),
            Deps => "Dependencies",
            DepProjects => "Projects",
            DepPackages => "Packages",
            ProjRef => Path.GetFileNameWithoutExtension(value.Split('|')[1]),
            Package => value.Split('|') is [_, var id, var version] && version.Length > 0 ? $"{id}  {version}" : value.Split('|')[1],
            _ => Path.GetFileName(value),
        };
    }

    ProjectKind KindOf(string project)
    {
        if (!kinds.TryGetValue(project, out var kind))
            kinds[project] = kind = System.IO.File.Exists(project) ? ProjectInfo.Read(project).Kind : ProjectKind.Library;
        return kind;
    }

    string IconFor(string key)
    {
        var (kind, value) = Parse(key);
        return kind switch
        {
            Sln => "view-list-symbolic",
            SFolder => "folder-symbolic",
            Deps => "insert-link-symbolic",
            DepProjects => "folder-symbolic",
            DepPackages => "folder-symbolic",
            ProjRef => "applications-engineering-symbolic",
            Package => "package-x-generic-symbolic",
            Proj => KindOf(value) switch
            {
                ProjectKind.Web => "network-server-symbolic",
                ProjectKind.Worker => "system-run-symbolic",
                ProjectKind.Console => "utilities-terminal-symbolic",
                ProjectKind.Test => "emblem-ok-symbolic",
                _ => "applications-engineering-symbolic",
            },
            Dir => "folder-symbolic",
            _ when value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) => "text-x-script-symbolic",
            _ when value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                   || value.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) => "emblem-system-symbolic",
            _ when value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => "text-x-generic-symbolic",
            _ => "text-x-generic-symbolic",
        };
    }

    // ---------- rows ----------

    void Setup(Gtk.ListItem item)
    {
        var expander = Gtk.TreeExpander.New();
        var box = Ui.HBox(6);
        box.Append(Gtk.Image.New());
        box.Append(Ui.Label(""));
        expander.SetChild(box);
        item.SetChild(expander);

        // One context menu for the whole tree (ContextMenu): building a PopoverMenu per row made expanding a
        // project with many entries take seconds (GTK creates every row's widgets at once).
        var click = Gtk.GestureClick.New();
        click.Button = 3;
        click.OnPressed += (_, e) =>
        {
            contextKey = expander.Name;
            if (contextKey is null) return;
            UpdateActionState(contextKey);
            var menu = ContextMenu();
            var point = new Graphene.Point { X = (float)e.X, Y = (float)e.Y };
            if (!expander.ComputePoint(root, point, out var at)) at = point;
            menu.SetPointingTo(new Gdk.Rectangle { X = (int)at.X, Y = (int)at.Y, Width = 1, Height = 1 });
            menu.Popup();
        };
        expander.AddController(click);
    }

    Gtk.PopoverMenu? contextMenu;

    /// <summary>The tree's context menu, created on the first right click and shared by every row.</summary>
    Gtk.PopoverMenu ContextMenu()
    {
        if (contextMenu is not null) return contextMenu;
        var menu = Gio.Menu.New();
        var top = Gio.Menu.New();
        top.Append("Open", "explorer.open");
        top.Append("New File…", "explorer.new-file");
        top.Append("New Folder…", "explorer.new-folder");
        menu.AppendSection(null, top);
        var edit = Gio.Menu.New();
        edit.Append("Rename…", "explorer.rename");
        edit.Append("Move to Trash", "explorer.delete");
        edit.Append("Copy Path", "explorer.copy-path");
        edit.Append("Format File", "explorer.format");
        menu.AppendSection(null, edit);
        var dotnet = Gio.Menu.New();
        dotnet.Append("Build", "explorer.build");
        dotnet.Append("Run", "explorer.run");
        dotnet.Append("Debug", "explorer.debug");
        dotnet.Append("Set as Startup Project", "explorer.set-startup");
        dotnet.Append("Run Tests", "explorer.test");
        dotnet.Append("Manage NuGet Packages…", "explorer.nuget");
        dotnet.Append("Project References…", "explorer.references");
        dotnet.Append("Remove Reference", "explorer.remove-reference");
        dotnet.Append("Add Project…", "explorer.add-project");
        dotnet.Append("Add Existing Project…", "explorer.add-existing-project");
        dotnet.Append("Remove from Solution", "explorer.remove-from-solution");
        dotnet.Append("Open Terminal Here", "explorer.terminal");
        dotnet.Append("Show in File Manager", "explorer.reveal");
        menu.AppendSection(null, dotnet);
        contextMenu = Gtk.PopoverMenu.NewFromModel(menu);
        contextMenu.SetParent(root); // the "explorer.*" actions are inserted on the root box
        contextMenu.HasArrow = false;
        contextMenu.Halign = Gtk.Align.Start;
        return contextMenu;
    }

    /// <summary>The key each row label currently shows (rows are recycled; Bind overwrites the entry).</summary>
    readonly Dictionary<Gtk.Label, string> boundLabels = [];

    /// <summary>Re-renders the label of a shown row without touching the model (keeps the row's expansion).</summary>
    void RefreshRow(string key)
    {
        foreach (var (label, shown) in boundLabels)
            if (shown == key) label.SetText(LabelFor(key));
    }

    void Bind(Gtk.ListItem item)
    {
        var row = (Gtk.TreeListRow)item.GetItem()!;
        var key = ((Gtk.StringObject)row.GetItem()!).GetString();
        var expander = (Gtk.TreeExpander)item.GetChild()!;
        expander.SetListRow(row);
        expander.Name = key;
        var box = (Gtk.Box)expander.GetChild()!;
        var image = (Gtk.Image)box.GetFirstChild()!;
        var label = (Gtk.Label)image.GetNextSibling()!;
        image.SetFromIconName(IconFor(key));
        label.SetText(LabelFor(key));
        boundLabels[label] = key;
        var kind = Parse(key).Kind;
        label.RemoveCssClass("project-label");
        if (kind is Proj or Sln) label.AddCssClass("project-label");
        var (_, tipValue) = Parse(key);
        expander.TooltipText = kind switch
        {
            Deps or DepProjects or DepPackages => null,
            ProjRef => tipValue.Split('|')[1] + (System.IO.File.Exists(tipValue.Split('|')[1]) ? "" : " (not found)"),
            Package => tipValue.Split('|') is [_, var id, var v] ? (v.Length > 0 ? $"{id} {v}" : $"{id} (no version found)") : null,
            Proj => KindOf(tipValue) switch
            {
                ProjectKind.Web => "Web app",
                ProjectKind.Worker => "Worker service",
                ProjectKind.Console => "Console app",
                ProjectKind.Test => "Test project",
                _ => "Class library",
            } + "\n" + tipValue,
            _ => tipValue,
        };
    }

    void Activate(Pane pane, uint position)
    {
        var row = pane.Tree.GetRow(position);
        if (row is null) return;
        var key = ((Gtk.StringObject)row.GetItem()!).GetString();
        var (kind, value) = Parse(key);
        if (kind == File) FileActivated?.Invoke(value);
        else if (kind == ProjRef) Reveal(value.Split('|')[1]);
        else if (kind == Package) ActionRequested?.Invoke("nuget", value.Split('|')[0]);
        else row.Expanded = !row.Expanded;
    }

    static string? SelectedKey(Pane pane) =>
        pane.Selection.GetSelectedItem() is Gtk.TreeListRow row ? ((Gtk.StringObject)row.GetItem()!).GetString() : null;

    void UpdateActionState(string key)
    {
        var (kind, value) = Parse(key);
        var isFs = kind is File or Dir or Proj;
        Enable("open", kind == File);
        Enable("new-file", kind is Dir or Proj);
        Enable("new-folder", kind is Dir or Proj);
        Enable("rename", kind is File or Dir);
        Enable("delete", kind is File or Dir);
        Enable("copy-path", isFs || kind == Sln);
        Enable("nuget", kind is Proj or Deps or DepPackages or Package);
        Enable("add-project", kind == Sln);
        Enable("add-existing-project", kind is Sln or SFolder);
        Enable("remove-from-solution", kind == Proj && workspace?.Solution?.Projects.Any(p => p.Path == value) == true);
        Enable("terminal", isFs || kind == Sln);
        Enable("reveal", isFs || kind == Sln);
        Enable("build", kind is Proj or Sln);
        var runnable = kind == Proj && KindOf(value) is ProjectKind.Web or ProjectKind.Worker or ProjectKind.Console;
        Enable("run", runnable);
        Enable("debug", runnable);
        Enable("set-startup", runnable && value != startupProject);
        // A .cs file or a folder runs the test classes found in it.
        Enable("test", kind is Proj or Sln or Dir or SFolder || (kind == File && value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)));
        Enable("references", kind is Proj or Deps or DepProjects or ProjRef);
        Enable("remove-reference", kind == ProjRef);
        Enable("format", kind == File && LightweightIDE.Language.Formatting.FormattingService.CanFormat(value));

        void Enable(string name, bool enabled) => ((Gio.SimpleAction)actions.LookupAction(name)!).Enabled = enabled;
    }

    void OnContextAction(string name)
    {
        if (contextKey is null) return;
        var (kind, value) = Parse(contextKey);
        if (name == "open") { FileActivated?.Invoke(value); return; }
        if (name == "add-existing-project")
        {
            AddExistingProjectRequested?.Invoke(kind == SFolder ? value.Trim('/') : null);
            return;
        }
        if (name == "test" && kind == SFolder)
        {
            var projects = workspace?.Solution?.Projects
                .Where(p => p.FolderPath is { } f && f.StartsWith(value, StringComparison.Ordinal) && System.IO.File.Exists(p.Path)
                    && ProjectInfo.Read(p.Path).IsTestProject)
                .Select(p => p.Path).ToList() ?? [];
            TestProjectsRequested?.Invoke(value.Trim('/'), projects);
            return;
        }
        // Directory for actions that need one
        var path = kind switch
        {
            Deps or DepProjects or DepPackages or Package or ProjRef when name is "nuget" or "references" => value.Split('|')[0],
            Proj when name is "new-file" or "new-folder" or "terminal" or "reveal" => Path.GetDirectoryName(value)!,
            Sln when name is "terminal" or "reveal" => Path.GetDirectoryName(value)!,
            _ => value,
        };
        ActionRequested?.Invoke(name, path);
    }

    // ---------- refresh / reveal ----------

    /// <summary>Re-lists every loaded directory node, keeping expansion state of unchanged nodes.</summary>
    public void Refresh()
    {
        foreach (var pane in new[] { solutionPane, filesPane })
            foreach (var (key, store) in pane.ChildStores.ToList()) Sync(pane, key, store);
        if (workspace is not null && filesPane.Loaded) Sync(filesPane, $"{Dir}|{workspace.Folder}", filesPane.RootStore);
    }

    /// <summary>Refreshes only the directories that contain the given paths (from file watcher events).</summary>
    public void RefreshPaths(IReadOnlyList<string> paths)
    {
        var dirs = paths.Select(p => Path.GetDirectoryName(p)).Where(d => d is not null).ToHashSet(StringComparer.Ordinal);
        // Dependencies come from the .csproj and Directory.Packages.props.
        var files = paths.ToHashSet(StringComparer.Ordinal);
        var props = paths.Any(p => Path.GetFileName(p) is "Directory.Packages.props" or "Directory.Build.props");
        foreach (var pane in new[] { solutionPane, filesPane })
        {
            foreach (var (key, store) in pane.ChildStores.ToList())
            {
                var (kind, value) = Parse(key);
                var dir = kind == Proj ? Path.GetDirectoryName(value) : kind == Dir ? value : null;
                if (dir is not null && dirs.Contains(dir)) Sync(pane, key, store);
            }
            foreach (var (key, store) in pane.ChildStores.ToList())
            {
                var (kind, value) = Parse(key);
                if (kind is DepProjects or DepPackages && (props || files.Contains(value))) Sync(pane, key, store);
            }
        }
        if (workspace is not null && filesPane.Loaded && dirs.Contains(workspace.Folder))
            Sync(filesPane, $"{Dir}|{workspace.Folder}", filesPane.RootStore);
    }

    void Sync(Pane pane, string key, Gio.ListStore store)
    {
        var wanted = ChildKeys(key).ToList();
        var current = new List<string>();
        for (uint i = 0; i < store.GetNItems(); i++) current.Add(((Gtk.StringObject)store.GetObject(i)!).GetString());
        if (current.SequenceEqual(wanted)) return;

        // remove vanished entries (from the end so indexes stay valid)
        var wantedSet = wanted.ToHashSet(StringComparer.Ordinal);
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (wantedSet.Contains(current[i])) continue;
            store.Remove((uint)i);
            pane.ChildStores.Remove(current[i]);
            current.RemoveAt(i);
        }
        // insert new entries at their sorted position
        var currentSet = current.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (currentSet.Contains(wanted[i])) continue;
            store.Insert((uint)i, Gtk.StringObject.New(wanted[i]));
        }
    }

    void CollapseAll()
    {
        var tree = Current.Tree;
        for (var i = (int)tree.GetNItems() - 1; i >= 0; i--) tree.GetRow((uint)i)?.SetExpanded(false);
    }

    /// <summary>
    /// Expands the tree down to a file and selects it: in the open tab, or in Files when the Solution tab is open and
    /// the file is not in any project.
    /// </summary>
    public void Reveal(string path)
    {
        if (workspace is null) return;
        var chain = new List<string>();
        var solution = workspace.Solution;
        var project = solution?.Projects
            .Where(p => path.StartsWith(Path.GetDirectoryName(p.Path)! + "/", StringComparison.Ordinal))
            .OrderByDescending(p => p.Path.Length).FirstOrDefault();
        if (Current == solutionPane && (solution is null || project is null))
        {
            if (!path.StartsWith(workspace.Folder.TrimEnd('/') + "/", StringComparison.Ordinal)) return;
            tabs.SetCurrentPage(FilesPage); // lists the Files tree (OnSwitchPage)
        }
        var pane = Current;
        string baseDir;
        if (pane == solutionPane && solution is not null && project is not null)
        {
            chain.Add($"{Sln}|{solution.Path}");
            var folders = new Stack<string>();
            for (var f = project.FolderPath; f is not null; f = solution.Folders.FirstOrDefault(x => x.FolderPath == f)?.ParentPath)
                folders.Push($"{SFolder}|{f}");
            chain.AddRange(folders);
            chain.Add($"{Proj}|{project.Path}");
            baseDir = Path.GetDirectoryName(project.Path)!;
        }
        else
        {
            EnsureFilesLoaded();
            baseDir = workspace.Folder;
        }
        var relative = Path.GetRelativePath(baseDir, Path.GetDirectoryName(path)!);
        if (relative != ".")
        {
            var current = baseDir;
            foreach (var part in relative.Split('/'))
            {
                current = Path.Combine(current, part);
                chain.Add($"{Dir}|{current}");
            }
        }
        chain.Add($"{File}|{path}");

        var tree = pane.Tree;
        uint start = 0;
        foreach (var key in chain)
        {
            var found = false;
            for (var i = start; i < tree.GetNItems(); i++)
            {
                var row = tree.GetRow(i);
                if (row is null || ((Gtk.StringObject)row.GetItem()!).GetString() != key) continue;
                if (key.StartsWith(File + "|", StringComparison.Ordinal))
                {
                    pane.Selection.SetSelected(i);
                    pane.List.ScrollTo(i, Gtk.ListScrollFlags.Focus | Gtk.ListScrollFlags.Select, null);
                    return;
                }
                row.Expanded = true;
                start = i + 1;
                found = true;
                break;
            }
            if (!found) return;
        }
    }
}
