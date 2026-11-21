using LightweightIDE.DotNet.Packages;

namespace LightweightIDE.App.Views;

/// <summary>
/// NuGet packages of one project: installed packages with available updates, update / remove, and search to add.
/// Every change goes through the dotnet CLI (see <see cref="PackageService"/>); its output goes to the Output panel.
/// </summary>
public sealed partial class PackagesWindow
{
    readonly Gtk.Window window;
    readonly PackageService packages;
    readonly Func<IReadOnlyList<string>> projects;
    readonly Action<string> output;
    readonly Action<string> changed;

    readonly Gtk.DropDown projectPicker = Gtk.DropDown.NewFromStrings([]);
    readonly Gtk.Label cpmLabel = Ui.Label("", "dim-label");
    readonly Gtk.Box content = Ui.VBox(6);
    readonly Gtk.ListBox installedList = Gtk.ListBox.New();
    readonly Gtk.Label installedSummary = Ui.Label("", "dim-label");
    readonly Gtk.ListBox browseList = Gtk.ListBox.New();
    readonly Gtk.SearchEntry search = Gtk.SearchEntry.New();
    readonly Gtk.CheckButton prerelease = Gtk.CheckButton.NewWithLabel("Include prerelease");
    readonly Gtk.Entry version = Gtk.Entry.New();
    readonly Gtk.Label status = Ui.Label("", "dim-label");
    readonly Gtk.Spinner spinner = Gtk.Spinner.New();
    readonly Gtk.Notebook pages = Gtk.Notebook.New();

    List<string> projectPaths = [];
    List<InstalledPackage> installed = [];
    CancellationTokenSource? cts;
    bool busy;
    bool suppressPick;

    /// <param name="changed">Called with the project path after packages were added, updated or removed.</param>
    /// <param name="solution">The opened solution (or folder) — Central Package Management is solution-wide.</param>
    /// <param name="openFile">Opens a file in the editor (Directory.Packages.props).</param>
    public PackagesWindow(Gtk.Window parent, PackageService packages, Func<IReadOnlyList<string>> projects, Action<string> output, Action<string> changed,
        Func<string?> solution, Action<string> openFile)
    {
        this.solution = solution;
        this.openFile = openFile;
        this.packages = packages;
        this.projects = projects;
        this.output = output;
        this.changed = changed;

        window = Gtk.Window.New();
        window.Title = "NuGet Packages";
        window.SetTransientFor(parent);
        window.SetDefaultSize(920, 620);
        window.HideOnClose = true;
        window.AddCssClass("packages");

        var root = Ui.VBox(6);
        root.MarginTop = root.MarginBottom = root.MarginStart = root.MarginEnd = 10;

        var header = Ui.HBox(8);
        header.Append(Ui.Label("Project"));
        projectPicker.Hexpand = true;
        projectPicker.OnNotify += (sender, e) =>
        {
            if (e.Pspec.GetName() == "selected" && !suppressPick) _ = LoadInstalledAsync();
        };
        header.Append(projectPicker);
        root.Append(header);
        cpmLabel.Wrap = true;
        root.Append(cpmLabel);

        pages.AppendPage(BuildInstalled(), Ui.Label("Installed"));
        pages.AppendPage(BuildBrowse(), Ui.Label("Browse"));
        pages.AppendPage(BuildCentral(), Ui.Label("Central (Directory.Packages.props)"));
        pages.OnSwitchPage += (notebook, e) =>
        {
            if (e.PageNum == 2) _ = LoadCentralAsync(checkUpdates: false);
        };
        pages.Vexpand = true;
        content.Append(pages);
        content.Vexpand = true;
        root.Append(content);

        var footer = Ui.HBox(6);
        footer.Append(spinner);
        status.Hexpand = true;
        status.Ellipsize = Pango.EllipsizeMode.End;
        footer.Append(status);
        root.Append(footer);
        window.SetChild(root);

        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != Gdk.Constants.KEY_Escape) return false;
            window.Close();
            return true;
        };
        window.AddController(keys);
    }

    string? Project => projectPaths.ElementAtOrDefault((int)projectPicker.Selected);

    /// <summary>Shows the window for a project (or keeps the current one when null).</summary>
    public void Open(string? project)
    {
        var list = projects();
        suppressPick = true;
        projectPaths = list.ToList();
        projectPicker.Model = Gtk.StringList.New(projectPaths.Select(p => Path.GetFileNameWithoutExtension(p)).ToArray());
        var index = project is null ? -1 : projectPaths.IndexOf(project);
        projectPicker.Selected = (uint)Math.Max(0, index);
        suppressPick = false;
        window.Present();
        if (projectPaths.Count == 0) SetStatus("No projects in the opened folder.");
        else _ = LoadInstalledAsync();
    }

    // ---------- installed ----------

    Gtk.Widget BuildInstalled()
    {
        var box = Ui.VBox(4);
        var bar = Ui.HBox(4);
        bar.AddCssClass("panel-toolbar");
        var refresh = Gtk.Button.NewWithLabel("Refresh");
        refresh.OnClicked += (_, _) => _ = LoadInstalledAsync();
        var updateAll = Gtk.Button.NewWithLabel("Update All");
        updateAll.TooltipText = "dotnet package update (every package of the project to its latest version)";
        updateAll.OnClicked += (_, _) => _ = RunChangeAsync("Updating all packages", (p, ct) => packages.UpdateAsync(p, null, null, output, ct));
        foreach (var b in new[] { refresh, updateAll }) { b.AddCssClass("flat"); bar.Append(b); }
        installedSummary.Hexpand = true;
        installedSummary.MarginStart = 8;
        bar.Append(installedSummary);
        box.Append(bar);
        installedList.AddCssClass("result-list");
        installedList.SelectionMode = Gtk.SelectionMode.None;
        box.Append(Ui.Scrolled(installedList));
        return box;
    }

    async Task LoadInstalledAsync()
    {
        if (Project is not { } project || busy) return;
        var cpm = PackageService.FindCentralPackagesFile(project);
        cpmLabel.SetText(cpm is null ? ""
            : $"Central Package Management: versions live in {cpm}. Updating a package changes it for every project that uses it.");
        cpmLabel.Visible = cpm is not null;

        await RunAsync($"Reading packages of {Path.GetFileName(project)}…", async ct =>
        {
            var list = await Task.Run(() => packages.ListAsync(project, outdated: false, output, ct), ct);
            installed = list.Packages.ToList();
            RenderInstalled(checking: true);
            SetStatus("Checking for updates…");
            var outdated = await Task.Run(() => packages.ListAsync(project, outdated: true, output, ct), ct);
            var latest = outdated.Packages.ToDictionary(p => p.Id, p => p.LatestVersion, StringComparer.OrdinalIgnoreCase);
            installed = installed.Select(p => p with { LatestVersion = latest.GetValueOrDefault(p.Id) ?? p.ResolvedVersion }).ToList();
            RenderInstalled(checking: false);
            var problems = list.Problems.Concat(outdated.Problems).Distinct().ToList();
            SetStatus(problems.Count > 0 ? "Problems: " + string.Join(" · ", problems) : "");
        });
    }

    void RenderInstalled(bool checking)
    {
        installedList.RemoveAll();
        var updates = installed.Count(p => p.IsOutdated);
        installedSummary.SetText(checking ? $"{installed.Count} packages" : $"{installed.Count} packages · {updates} update(s) available");
        foreach (var p in installed.OrderByDescending(p => p.IsOutdated).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var row = Ui.HBox(10);
            var name = Ui.Label(p.Id, "package-id");
            name.Hexpand = true;
            name.Ellipsize = Pango.EllipsizeMode.End;
            name.TooltipText = $"{p.Id} — {p.Framework}";
            row.Append(name);
            row.Append(Ui.Label(p.ResolvedVersion ?? p.RequestedVersion, "package-version"));
            if (p.IsOutdated)
            {
                row.Append(Ui.Label("→ " + p.LatestVersion, "package-update"));
                var update = Gtk.Button.NewWithLabel("Update");
                update.AddCssClass("flat");
                update.TooltipText = $"dotnet package update {p.Id}@{p.LatestVersion}";
                update.OnClicked += (_, _) => _ = RunChangeAsync($"Updating {p.Id} to {p.LatestVersion}",
                    (project, ct) => packages.UpdateAsync(project, p.Id, p.LatestVersion, output, ct));
                row.Append(update);
            }
            var remove = Gtk.Button.NewFromIconName("user-trash-symbolic");
            remove.AddCssClass("flat");
            remove.TooltipText = $"Remove {p.Id}";
            remove.OnClicked += (_, _) => _ = RemoveAsync(p.Id);
            row.Append(remove);
            installedList.Append(row);
        }
    }

    async Task RemoveAsync(string id)
    {
        if (Project is not { } project) return;
        var choice = await Ui.ChooseAsync(window, $"Remove {id}?", $"The reference is removed from {Path.GetFileName(project)}.", ["Cancel", "Remove"], 0, 0);
        if (choice == 1) await RunChangeAsync($"Removing {id}", (p, ct) => packages.RemoveAsync(p, id, output, ct));
    }

    // ---------- browse ----------

    Gtk.Widget BuildBrowse()
    {
        var box = Ui.VBox(4);
        var bar = Ui.HBox(8);
        bar.AddCssClass("panel-toolbar");
        search.PlaceholderText = "Search packages (Enter)";
        search.Hexpand = true;
        search.OnActivate += (_, _) => _ = SearchAsync();
        bar.Append(search);
        bar.Append(prerelease);
        version.PlaceholderText = "Version (latest)";
        version.WidthChars = 14;
        version.TooltipText = "Version to add; empty = latest";
        bar.Append(version);
        box.Append(bar);
        browseList.AddCssClass("result-list");
        browseList.SelectionMode = Gtk.SelectionMode.None;
        box.Append(Ui.Scrolled(browseList));
        return box;
    }

    async Task SearchAsync()
    {
        var term = search.GetText().Trim();
        if (term.Length == 0 || Project is not { } project) return;
        await RunAsync($"Searching for {term}…", async ct =>
        {
            var result = await Task.Run(() => packages.SearchAsync(term, prerelease.Active, Path.GetDirectoryName(project)!, output, ct), ct);
            browseList.RemoveAll();
            var have = installed.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var hit in result.Packages)
            {
                var row = Ui.HBox(10);
                var info = Ui.VBox();
                info.Hexpand = true;
                info.Append(Ui.Label(hit.Id, "package-id"));
                info.Append(Ui.Label($"{Downloads(hit.TotalDownloads)} downloads · {hit.Owners ?? "?"} · {hit.Source}", "dim-label"));
                row.Append(info);
                row.Append(Ui.Label(hit.LatestVersion, "package-version"));
                var add = Gtk.Button.NewWithLabel(have.Contains(hit.Id) ? "Add to more…" : "Add…");
                add.AddCssClass("flat");
                add.TooltipText = "Choose the projects that get this package";
                add.OnClicked += (_, _) => _ = AddToProjectsAsync(hit.Id, version.GetText().Trim(), hit.LatestVersion);
                row.Append(add);
                browseList.Append(row);
            }
            SetStatus($"{result.Packages.Count} package(s)" + (result.Problems.Count > 0 ? " · " + string.Join(" · ", result.Problems) : ""));
        });
    }

    static string Downloads(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#}B",
        >= 1_000_000 => $"{n / 1e6:0.#}M",
        >= 1_000 => $"{n / 1e3:0.#}K",
        _ => n.ToString(),
    };

    // ---------- operations ----------

    async Task RunChangeAsync(string what, Func<string, CancellationToken, Task<int>> change)
    {
        if (Project is not { } project) return;
        var exit = 0;
        await RunAsync(what + "…", async ct => exit = await Task.Run(() => change(project, ct), ct));
        if (exit != 0)
        {
            SetStatus($"{what} failed (exit code {exit}). See the Output panel.");
            return;
        }
        changed(project);
        await LoadInstalledAsync();
        SetStatus($"{what}: done.");
    }

    async Task RunAsync(string message, Func<CancellationToken, Task> work)
    {
        if (busy) return;
        busy = true;
        cts = new CancellationTokenSource();
        content.Sensitive = false;
        projectPicker.Sensitive = false;
        spinner.Spinning = true;
        SetStatus(message);
        try { await work(cts.Token); }
        catch (OperationCanceledException) { SetStatus("Cancelled."); }
        catch (Exception e) { SetStatus(e.Message); }
        finally
        {
            busy = false;
            content.Sensitive = true;
            projectPicker.Sensitive = true;
            spinner.Spinning = false;
        }
    }

    void SetStatus(string text)
    {
        status.SetText(text);
        status.TooltipText = text;
    }
}
