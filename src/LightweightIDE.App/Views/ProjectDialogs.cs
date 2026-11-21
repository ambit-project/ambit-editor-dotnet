using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.DotNet.Scaffolding;

namespace LightweightIDE.App.Views;

/// <summary>Shared pieces of the project dialogs: a modal window with a form grid, a status line and buttons.</summary>
public abstract class FormDialog
{
    protected readonly Gtk.Window Window;
    protected readonly Gtk.Grid Form = Gtk.Grid.New();
    protected readonly Gtk.Label Status = Ui.Label("", "dim-label");
    protected readonly Gtk.Label Error = Ui.Label("", "error");
    protected readonly Gtk.Button Ok;
    readonly Gtk.Spinner spinner = Gtk.Spinner.New();
    int row;
    bool busy;

    protected FormDialog(Gtk.Window parent, string title, string okLabel)
    {
        Window = Gtk.Window.New();
        Window.Title = title;
        Window.SetTransientFor(parent);
        Window.Modal = true;
        Window.SetDefaultSize(620, -1);
        Window.AddCssClass("form-dialog");

        var root = Ui.VBox(10);
        root.MarginTop = root.MarginBottom = root.MarginStart = root.MarginEnd = 14;
        Form.RowSpacing = 8;
        Form.ColumnSpacing = 12;
        root.Append(Form);
        Error.Wrap = true;
        Error.Visible = false;
        root.Append(Error);

        var footer = Ui.HBox(8);
        footer.Append(spinner);
        Status.Hexpand = true;
        Status.Ellipsize = Pango.EllipsizeMode.End;
        footer.Append(Status);
        var cancel = Gtk.Button.NewWithLabel("Cancel");
        cancel.OnClicked += (_, _) => Window.Close();
        Ok = Gtk.Button.NewWithLabel(okLabel);
        Ok.AddCssClass("suggested-action");
        Ok.OnClicked += (_, _) => _ = SubmitAsync();
        footer.Append(cancel);
        footer.Append(Ok);
        root.Append(footer);
        Window.SetChild(root);

        Window.OnCloseRequest += (_, _) => busy; // keep the window while the CLI runs
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != Gdk.Constants.KEY_Escape || busy) return false;
            Window.Close();
            return true;
        };
        Window.AddController(keys);
    }

    protected void AddRow(string label, Gtk.Widget widget, string? hint = null)
    {
        var l = Ui.Label(label);
        l.Valign = Gtk.Align.Center;
        Form.Attach(l, 0, row, 1, 1);
        widget.Hexpand = true;
        if (hint is not null) widget.TooltipText = hint;
        Form.Attach(widget, 1, row, 1, 1);
        row++;
    }

    protected void AddWide(Gtk.Widget widget)
    {
        Form.Attach(widget, 1, row, 1, 1);
        row++;
    }

    protected static Gtk.DropDown Strings(IEnumerable<string> items) => Gtk.DropDown.NewFromStrings(items.ToArray());

    protected static void SetItems(Gtk.DropDown dropDown, IReadOnlyList<string> items, int selected = 0)
    {
        dropDown.Model = Gtk.StringList.New(items.ToArray());
        dropDown.Selected = (uint)Math.Clamp(selected, 0, Math.Max(0, items.Count - 1));
    }

    protected static string? SelectedString(Gtk.DropDown dropDown) => (dropDown.SelectedItem as Gtk.StringObject)?.GetString();

    protected void ShowError(string? message)
    {
        Error.SetText(message ?? "");
        Error.Visible = message is not null;
    }

    /// <summary>Validates; null = OK.</summary>
    protected abstract string? Validate();

    protected abstract Task RunAsync(CancellationToken ct);

    async Task SubmitAsync()
    {
        if (busy) return;
        if (Validate() is { } invalid)
        {
            ShowError(invalid);
            return;
        }
        ShowError(null);
        busy = true;
        Form.Sensitive = false;
        Ok.Sensitive = false;
        spinner.Spinning = true;
        try
        {
            await RunAsync(CancellationToken.None);
            busy = false;
            Window.Close();
        }
        catch (Exception e)
        {
            ShowError(e.Message);
            Status.SetText("");
        }
        finally
        {
            busy = false;
            Form.Sensitive = true;
            Ok.Sensitive = true;
            spinner.Spinning = false;
        }
    }

    /// <summary>Output goes to the IDE's Output panel; the last line is shown here too.</summary>
    protected Action<string> Tee(Action<string> output) => line =>
    {
        output(line);
        if (line.Trim().Length > 0) Ui.Post(() => Status.SetText(line.Trim()));
    };

    public void Present() => Window.Present();
}

/// <summary>File → New Solution: folder, global.json with the chosen SDK, solution, optional first project and tests.</summary>
public sealed class NewSolutionDialog : FormDialog
{
    readonly SolutionScaffolder scaffolder;
    readonly Action<string> output;
    readonly Func<string, Task> created;
    readonly Gtk.Entry name = Gtk.Entry.New();
    readonly Gtk.Entry location = Gtk.Entry.New();
    readonly Gtk.Label target = Ui.Label("", "dim-label");
    readonly Gtk.DropDown sdk = Strings(["Loading…"]);
    readonly Gtk.DropDown rollForward = Strings(GlobalJson.RollForwardPolicies);
    readonly Gtk.DropDown framework = Strings(["…"]);
    readonly Gtk.DropDown template;
    readonly Gtk.Entry projectName = Gtk.Entry.New();
    readonly Gtk.CheckButton slnx = Gtk.CheckButton.NewWithLabel(".slnx solution format (XML)");
    readonly Gtk.CheckButton tests = Gtk.CheckButton.NewWithLabel("xUnit test project (tests/<Name>.Tests)");
    readonly Gtk.CheckButton buildProps = Gtk.CheckButton.NewWithLabel("Directory.Build.props (nullable, implicit usings)");
    readonly Gtk.CheckButton cpm = Gtk.CheckButton.NewWithLabel("Central Package Management (Directory.Packages.props)");
    readonly Gtk.CheckButton editorConfig = Gtk.CheckButton.NewWithLabel(".editorconfig");
    readonly Gtk.CheckButton csharpier = Gtk.CheckButton.NewWithLabel("CSharpier as a local dotnet tool");
    readonly Gtk.CheckButton git = Gtk.CheckButton.NewWithLabel("git init");
    readonly List<ProjectTemplate> templates = ProjectTemplates.All.Where(t => !t.IsTest).ToList();
    List<InstalledSdk> sdks = [];
    bool projectNameEdited;
    int frameworkRequest;

    public NewSolutionDialog(Gtk.Window parent, SolutionScaffolder scaffolder, string defaultLocation, Action<string> output, Func<string, Task> created)
        : base(parent, "New Solution", "Create")
    {
        this.scaffolder = scaffolder;
        this.output = output;
        this.created = created;
        template = Strings(new[] { "None" }.Concat(templates.Select(t => t.Name)));

        name.SetText("MyService");
        AddRow("Name", name);
        var locationRow = Ui.HBox(6);
        location.SetText(defaultLocation);
        location.Hexpand = true;
        locationRow.Append(location);
        var browse = Gtk.Button.NewWithLabel("Browse…");
        browse.OnClicked += (_, _) => _ = BrowseAsync();
        locationRow.Append(browse);
        AddRow("Location", locationRow);
        AddWide(target);
        AddRow(".NET SDK", sdk, "Written to global.json; every dotnet command in the solution uses it.");
        AddRow("Roll forward", rollForward, "latestFeature: newer feature bands / patches of the same major.minor are allowed (recommended).\n" +
            "latestPatch: only patches.  disable: exactly this SDK.");
        AddRow("Target framework", framework);
        AddRow("First project", template);
        AddRow("Project name", projectName);
        foreach (var c in new[] { slnx, tests, buildProps, cpm, editorConfig, csharpier, git }) AddWide(c);
        slnx.Active = tests.Active = buildProps.Active = cpm.Active = editorConfig.Active = csharpier.Active = git.Active = true;
        template.Selected = 1; // Web API

        name.OnChanged += (_, _) => UpdateDerived();
        location.OnChanged += (_, _) => UpdateDerived();
        projectName.OnChanged += (_, _) =>
        {
            if (projectName.HasFocus) projectNameEdited = true;
        };
        sdk.OnNotify += (sender, e) =>
        {
            if (e.Pspec.GetName() == "selected") _ = LoadFrameworksAsync();
        };
        template.OnNotify += (sender, e) =>
        {
            if (e.Pspec.GetName() != "selected") return;
            projectNameEdited = false;
            UpdateDerived();
            _ = LoadFrameworksAsync();
        };
        UpdateDerived();
        _ = LoadSdksAsync();
    }

    ProjectTemplate? Template => template.Selected == 0 ? null : templates[(int)template.Selected - 1];
    InstalledSdk? Sdk => sdks.ElementAtOrDefault((int)sdk.Selected);

    void UpdateDerived()
    {
        var n = name.GetText().Trim();
        target.SetText($"Creates {Path.Combine(location.GetText().Trim(), n)}");
        projectName.Sensitive = Template is not null;
        if (!projectNameEdited) projectName.SetText(Template is { } t ? n + t.Suffix : "");
    }

    async Task BrowseAsync()
    {
        var dialog = Gtk.FileDialog.New();
        dialog.Title = "Location";
        if (Directory.Exists(location.GetText())) dialog.SetInitialFolder(Gio.FileHelper.NewForPath(location.GetText()));
        try
        {
            if ((await dialog.SelectFolderAsync(Window))?.GetPath() is { } path) location.SetText(path);
        }
        catch (GLib.GException) { }
    }

    async Task LoadSdksAsync()
    {
        Status.SetText("Reading installed SDKs…");
        sdks = (await SdkCatalog.ListAsync()).ToList();
        if (sdks.Count == 0)
        {
            ShowError("No .NET SDK found (dotnet --list-sdks).");
            Ok.Sensitive = false;
            return;
        }
        // Default: the newest stable SDK.
        var preferred = Math.Max(0, sdks.FindIndex(s => !s.IsPreview));
        SetItems(sdk, sdks.Select(s => s.IsPreview ? s.Version + " (preview)" : s.Version).ToList(), preferred);
        Status.SetText("");
        await LoadFrameworksAsync();
    }

    async Task LoadFrameworksAsync()
    {
        if (Sdk is not { } selected) return;
        slnx.Sensitive = selected.SupportsSlnx;
        if (!selected.SupportsSlnx) slnx.Active = false;
        var request = ++frameworkRequest;
        SetItems(framework, ["…"]);
        framework.Sensitive = false;
        var list = await SdkCatalog.FrameworksAsync(selected.Version, Template?.ShortName ?? "classlib");
        if (request != frameworkRequest) return; // a newer selection is loading
        SetItems(framework, list, Math.Max(0, list.ToList().IndexOf(selected.DefaultFramework)));
        framework.Sensitive = true;
    }

    protected override string? Validate()
    {
        var n = name.GetText().Trim();
        if (SolutionScaffolder.ValidateName(n) is { } invalid) return invalid;
        var parent = location.GetText().Trim();
        if (!Path.IsPathRooted(parent)) return "Choose an absolute location.";
        var folder = Path.Combine(parent, n);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) return $"{folder} already exists and is not empty.";
        if (Sdk is null) return "Choose an SDK.";
        if (Template is not null && SolutionScaffolder.ValidateName(projectName.GetText().Trim()) is { } p) return "Project name: " + p;
        if (!framework.Sensitive) return "Wait until the target frameworks are loaded.";
        return null;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var request = new NewSolutionRequest(location.GetText().Trim(), name.GetText().Trim(), Sdk!.Version,
            SelectedString(rollForward) ?? "latestFeature", SelectedString(framework) ?? Sdk.DefaultFramework, slnx.Active,
            Template, projectName.GetText().Trim(), tests.Active, buildProps.Active, cpm.Active, editorConfig.Active, csharpier.Active, git.Active);
        var solution = await Task.Run(() => scaffolder.CreateSolutionAsync(request, Tee(output), ct), ct);
        await created(solution);
    }
}

/// <summary>Add Project: a web / backend / test template next to the solution (src/ or tests/), added to the solution.</summary>
public sealed class AddProjectDialog : FormDialog
{
    readonly SolutionScaffolder scaffolder;
    readonly string solution;
    readonly string? sdkVersion;
    readonly IReadOnlyList<string> projects;
    readonly Action<string> output;
    readonly Func<string, Task> added;
    readonly Gtk.DropDown template = Strings(ProjectTemplates.All.Select(t => t.Name));
    readonly Gtk.Entry name = Gtk.Entry.New();
    readonly Gtk.Label target = Ui.Label("", "dim-label");
    readonly Gtk.DropDown framework = Strings(["…"]);
    readonly Gtk.DropDown reference;
    readonly uint preferredReferenceIndex;
    bool nameEdited;
    int frameworkRequest;

    /// <param name="sdkVersion">SDK the solution folder resolves (global.json); null when unknown.</param>
    /// <param name="preferredReference">Project to reference by default from a test project.</param>
    public AddProjectDialog(Gtk.Window parent, SolutionScaffolder scaffolder, string solution, string? sdkVersion, IReadOnlyList<string> projects,
        string? preferredReference, Action<string> output, Func<string, Task> added)
        : base(parent, "Add Project", "Add")
    {
        this.scaffolder = scaffolder;
        this.solution = solution;
        this.sdkVersion = sdkVersion;
        this.projects = projects;
        this.output = output;
        this.added = added;
        reference = Strings(new[] { "None" }.Concat(projects.Select(p => Path.GetFileNameWithoutExtension(p))));

        AddRow("Template", template);
        AddRow("Name", name);
        AddWide(target);
        AddRow("Target framework", framework, sdkVersion is null ? null : $"Frameworks offered by SDK {sdkVersion} (global.json)");
        AddRow("Reference", reference, "Adds a project reference from the new project (e.g. tests → tested project).");
        preferredReferenceIndex = preferredReference is not null && projects.ToList().IndexOf(preferredReference) is >= 0 and var i ? (uint)(i + 1) : 0;

        name.OnChanged += (_, _) =>
        {
            if (name.HasFocus) nameEdited = true;
            UpdateTarget();
        };
        template.OnNotify += (sender, e) =>
        {
            if (e.Pspec.GetName() != "selected") return;
            UpdateDefaults();
            _ = LoadFrameworksAsync();
        };
        UpdateDefaults();
        _ = LoadFrameworksAsync();
    }

    ProjectTemplate Template => ProjectTemplates.All[(int)template.Selected];
    string SolutionName => Path.GetFileNameWithoutExtension(solution);

    void UpdateDefaults()
    {
        if (!nameEdited)
        {
            var baseName = SolutionName + Template.Suffix;
            var candidate = baseName;
            for (var n = 2; projects.Any(p => Path.GetFileNameWithoutExtension(p) == candidate); n++) candidate = baseName + n;
            name.SetText(candidate);
        }
        // A test project references the project being worked on; other templates start without a reference.
        reference.Selected = Template.IsTest ? preferredReferenceIndex : 0;
        UpdateTarget();
    }

    void UpdateTarget() => target.SetText($"Creates {Template.Folder}/{name.GetText().Trim()}/{name.GetText().Trim()}.csproj");

    async Task LoadFrameworksAsync()
    {
        var request = ++frameworkRequest;
        SetItems(framework, ["…"]);
        framework.Sensitive = false;
        IReadOnlyList<string> list = sdkVersion is null ? [] : await SdkCatalog.FrameworksAsync(sdkVersion, Template.ShortName);
        if (request != frameworkRequest) return;
        // Default: what the other projects of the solution target, else the SDK's newest.
        var used = projects.Select(ReadTargetFramework).OfType<string>().GroupBy(f => f).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var items = list.Count > 0 ? list.ToList() : ["SDK default"];
        SetItems(framework, items, used is null ? 0 : Math.Max(0, items.IndexOf(used)));
        framework.Sensitive = true;
    }

    static string? ReadTargetFramework(string project)
    {
        try
        {
            var text = File.ReadAllText(project);
            var start = text.IndexOf("<TargetFramework>", StringComparison.Ordinal);
            if (start < 0) return null;
            start += "<TargetFramework>".Length;
            var end = text.IndexOf('<', start);
            return end > start ? text[start..end].Trim() : null;
        }
        catch (IOException) { return null; }
    }

    protected override string? Validate()
    {
        var n = name.GetText().Trim();
        if (SolutionScaffolder.ValidateName(n) is { } invalid) return invalid;
        if (projects.Any(p => Path.GetFileNameWithoutExtension(p).Equals(n, StringComparison.OrdinalIgnoreCase))) return $"The solution already has a project named {n}.";
        var dir = Path.Combine(Path.GetDirectoryName(solution)!, Template.Folder, n);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) return $"{dir} already exists and is not empty.";
        if (!framework.Sensitive) return "Wait until the target frameworks are loaded.";
        return null;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var tfm = SelectedString(framework) is { } f && f.StartsWith("net", StringComparison.Ordinal) ? f : null;
        var referenced = reference.Selected == 0 ? null : projects[(int)reference.Selected - 1];
        var request = new AddProjectRequest(solution, Template, name.GetText().Trim(), tfm, referenced);
        var project = await Task.Run(() => scaffolder.AddProjectAsync(request, Tee(output), ct), ct);
        await added(project);
    }
}

/// <summary>SDK of the opened folder: edits (or creates) global.json.</summary>
public sealed class SdkDialog : FormDialog
{
    readonly string folder;
    readonly Func<Task> saved;
    readonly Gtk.Label file = Ui.Label("", "dim-label");
    readonly Gtk.DropDown sdk = Strings(["Loading…"]);
    readonly Gtk.DropDown rollForward = Strings(GlobalJson.RollForwardPolicies);
    readonly Gtk.CheckButton mtp = Gtk.CheckButton.NewWithLabel("dotnet test uses Microsoft.Testing.Platform (test.runner, SDK 10+)");
    readonly string? existing;
    List<InstalledSdk> sdks = [];

    public SdkDialog(Gtk.Window parent, string folder, Func<Task> saved) : base(parent, "SDK and global.json", "Save")
    {
        this.folder = folder;
        this.saved = saved;
        existing = GlobalJson.Find(folder);
        file.Wrap = true;
        file.SetText(existing is null
            ? $"No global.json: the newest installed SDK is used. Saving creates {Path.Combine(folder, GlobalJson.FileName)}."
            : $"Editing {existing}");
        AddWide(file);
        AddRow(".NET SDK", sdk);
        AddRow("Roll forward", rollForward);
        AddWide(mtp);
        var current = existing is null ? null : Safe(() => GlobalJson.Read(existing));
        rollForward.Selected = (uint)Math.Max(0, GlobalJson.RollForwardPolicies.ToList().IndexOf(current?.RollForward ?? "latestFeature"));
        mtp.Active = current?.TestRunner == "Microsoft.Testing.Platform";
        _ = LoadAsync(current?.Version);
    }

    static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); }
        catch (Exception) { return null; }
    }

    async Task LoadAsync(string? currentVersion)
    {
        sdks = (await SdkCatalog.ListAsync()).ToList();
        var items = sdks.Select(s => s.Version).ToList();
        var index = currentVersion is null ? sdks.FindIndex(s => !s.IsPreview) : items.IndexOf(currentVersion);
        if (currentVersion is not null && index < 0)
        {
            // global.json pins an SDK that is not installed: keep it visible.
            items.Insert(0, currentVersion + " (not installed)");
            sdks.Insert(0, new InstalledSdk(currentVersion));
            index = 0;
        }
        SetItems(sdk, items, Math.Max(0, index));
    }

    protected override string? Validate() => sdks.Count == 0 ? "No SDK found." : null;

    protected override async Task RunAsync(CancellationToken ct)
    {
        var version = sdks[(int)sdk.Selected].Version;
        var path = existing ?? Path.Combine(folder, GlobalJson.FileName);
        var runner = mtp.Active ? "Microsoft.Testing.Platform" : "";
        await Task.Run(() => GlobalJson.Write(path, version, SelectedString(rollForward) ?? "latestFeature", runner), ct);
        await saved();
    }
}

/// <summary>
/// Project → Project References…: a checklist of the solution's other projects. Projects that already depend on this
/// one cannot be checked (a reference to them would close a cycle). Applies with dotnet add / remove reference.
/// </summary>
public sealed class ProjectReferencesDialog : FormDialog
{
    readonly string project;
    readonly Action<string> output;
    readonly Func<Task> changed;
    readonly HashSet<string> current;
    readonly List<(string Path, Gtk.CheckButton Check, Gtk.Box Row)> rows = [];
    readonly Gtk.SearchEntry search = Gtk.SearchEntry.New();

    public ProjectReferencesDialog(Gtk.Window parent, string project, IReadOnlyList<string> projects, Action<string> output, Func<Task> changed)
        : base(parent, "Project References — " + Path.GetFileNameWithoutExtension(project), "Apply")
    {
        this.project = Path.GetFullPath(project);
        this.output = output;
        this.changed = changed;
        current = new HashSet<string>(ProjectReferences.Read(this.project), StringComparer.Ordinal);

        // Each .csproj is read once for the cycle checks.
        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        IReadOnlyList<string> Read(string p) => cache.TryGetValue(p, out var r) ? r : cache[p] = ProjectReferences.Read(p);

        var root = Path.GetDirectoryName(this.project)!;
        var solutionRoot = projects.Count > 0 ? CommonDirectory(projects.Append(this.project)) : root;
        var list = Ui.VBox(4);
        var others = projects.Select(Path.GetFullPath).Where(p => p != this.project).ToList();
        // References to projects outside the solution stay listed, so they can be removed.
        foreach (var missing in current.Where(c => !others.Contains(c))) others.Add(missing);
        foreach (var other in others.OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase))
        {
            var check = Gtk.CheckButton.NewWithLabel(Path.GetFileNameWithoutExtension(other));
            check.Active = current.Contains(other);
            var relative = Path.GetRelativePath(solutionRoot, other);
            var hint = Ui.Label(File.Exists(other) ? relative : relative + " (not found)", "dim-label");
            hint.Hexpand = true;
            hint.Halign = Gtk.Align.End;
            hint.Ellipsize = Pango.EllipsizeMode.Start;
            if (!check.Active && ProjectReferences.WouldCreateCycle(this.project, other, Read))
            {
                check.Sensitive = false;
                hint.SetText($"depends on {Path.GetFileNameWithoutExtension(this.project)} · {hint.GetText()}");
                check.TooltipText = $"{Path.GetFileNameWithoutExtension(other)} already depends on {Path.GetFileNameWithoutExtension(this.project)}; " +
                    "a reference would create a cycle.";
            }
            var row = Ui.HBox(8);
            row.Append(check);
            row.Append(hint);
            list.Append(row);
            rows.Add((other, check, row));
        }
        if (rows.Count == 0) list.Append(Ui.Label("The solution has no other projects.", "dim-label"));

        var scrolled = Ui.Scrolled(list);
        scrolled.SetSizeRequest(-1, 320);
        scrolled.Vexpand = true;
        search.PlaceholderText = "Filter projects";
        search.OnSearchChanged += (_, _) =>
        {
            var text = search.GetText().Trim();
            foreach (var (path, _, row) in rows)
                row.Visible = text.Length == 0 || Path.GetFileNameWithoutExtension(path).Contains(text, StringComparison.OrdinalIgnoreCase);
        };
        // Escape in the filter field: clear it first, then close the dialog.
        search.OnStopSearch += (_, _) =>
        {
            if (search.GetText().Length > 0) search.SetText("");
            else Window.Close();
        };
        AddRow("Filter", search);
        AddWide(scrolled);
        Status.SetText($"{current.Count} project reference(s)");
    }

    static string CommonDirectory(IEnumerable<string> paths)
    {
        var dirs = paths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!.TrimEnd('/') + "/").ToList();
        var common = dirs[0];
        foreach (var d in dirs.Skip(1))
            while (!d.StartsWith(common, StringComparison.Ordinal)) common = Path.GetDirectoryName(common.TrimEnd('/'))!.TrimEnd('/') + "/";
        return common;
    }

    protected override string? Validate() => null;

    protected override async Task RunAsync(CancellationToken ct)
    {
        var add = rows.Where(r => r.Check.Active && !current.Contains(r.Path)).Select(r => r.Path).ToList();
        var remove = rows.Where(r => !r.Check.Active && current.Contains(r.Path)).Select(r => r.Path).ToList();
        if (add.Count == 0 && remove.Count == 0) return;
        await Task.Run(() => ProjectReferences.ApplyAsync(project, add, remove, Tee(output), ct), ct);
        await changed();
    }
}
