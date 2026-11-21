using LightweightIDE.DotNet.Packages;
using LightweightIDE.DotNet.Scaffolding;

namespace LightweightIDE.App.Views;

/// <summary>Central Package Management for the whole solution, and adding a package to several projects.</summary>
public sealed partial class PackagesWindow
{
    readonly Func<string?> solution;
    readonly Action<string> openFile;
    readonly Gtk.ListBox centralList = Gtk.ListBox.New();
    readonly Gtk.Label centralSummary = Ui.Label("", "dim-label");
    readonly Gtk.Box centralActions = Ui.HBox(4);
    readonly Dictionary<string, string> latest = new(StringComparer.OrdinalIgnoreCase);

    string? SolutionDirectory => solution() is { } s ? (Directory.Exists(s) ? s : Path.GetDirectoryName(s)) : null;
    string? PropsFile => SolutionDirectory is { } dir ? CentralPackages.FindEnabledFile(dir) : null;

    Gtk.Widget BuildCentral()
    {
        var box = Ui.VBox(4);
        centralActions.AddCssClass("panel-toolbar");
        box.Append(centralActions);
        centralSummary.Wrap = true;
        centralSummary.MarginStart = 8;
        box.Append(centralSummary);
        centralList.AddCssClass("result-list");
        centralList.SelectionMode = Gtk.SelectionMode.None;
        box.Append(Ui.Scrolled(centralList));
        return box;
    }

    static Gtk.Button Flat(string label, string tooltip, Action onClick)
    {
        var b = Gtk.Button.NewWithLabel(label);
        b.AddCssClass("flat");
        b.TooltipText = tooltip;
        b.OnClicked += (_, _) => onClick();
        return b;
    }

    async Task LoadCentralAsync(bool checkUpdates)
    {
        if (busy) return;
        while (centralActions.GetFirstChild() is { } child) centralActions.Remove(child);
        centralList.RemoveAll();
        var allProjects = projects();
        if (PropsFile is not { } props)
        {
            centralActions.Append(Flat("Convert to Central Package Management…",
                "Create Directory.Packages.props with one version per package and remove the versions from the projects", () => _ = ConvertAsync()));
            centralSummary.SetText("This solution does not use Central Package Management: every project file has its own package versions.");
            return;
        }
        centralActions.Append(Flat("Refresh", "Read the file again", () => _ = LoadCentralAsync(false)));
        centralActions.Append(Flat("Check for Updates", "dotnet package list --outdated for the solution", () => _ = LoadCentralAsync(true)));
        centralActions.Append(Flat("Update All", "Set every package to its latest version", () => _ = UpdateCentralAsync(null)));
        centralActions.Append(Flat("Open File", props, () => openFile(props)));

        if (checkUpdates && solution() is { } target)
        {
            await RunAsync("Checking for updates…", async ct =>
            {
                var outdated = await Task.Run(() => packages.ListAsync(target, outdated: true, output, ct), ct);
                foreach (var p in outdated.Packages)
                    if (p.LatestVersion is { } v) latest[p.Id] = v;
            });
        }

        var entries = CentralPackageService.Read(props, allProjects);
        var updates = entries.Count(e => latest.TryGetValue(e.Id, out var v) && NuGetVersionComparer.Instance.Compare(v, e.Version) > 0);
        var unused = entries.Count(e => e.Projects.Count == 0);
        centralSummary.SetText($"{props} — {entries.Count} package(s)" + (updates > 0 ? $", {updates} update(s)" : "")
            + (unused > 0 ? $", {unused} not used by any project" : "") + (latest.Count > 0 ? "." : ". Check for Updates to compare with the feeds."));
        if (checkUpdates) SetStatus(updates == 0 ? "All central versions are up to date." : $"{updates} update(s) available.");
        foreach (var entry in entries.OrderByDescending(e => latest.TryGetValue(e.Id, out var v) && NuGetVersionComparer.Instance.Compare(v, e.Version) > 0)
                     .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
        {
            var row = Ui.HBox(10);
            var name = Ui.Label(entry.Id, "package-id");
            name.Hexpand = true;
            name.Ellipsize = Pango.EllipsizeMode.End;
            row.Append(name);
            var usage = Ui.Label(entry.Projects.Count == 0 ? "unused" : $"{entry.Projects.Count} project(s)", "dim-label");
            usage.TooltipText = entry.Projects.Count == 0 ? "No PackageReference uses it."
                : string.Join("\n", entry.Projects.Select(Path.GetFileNameWithoutExtension));
            row.Append(usage);
            if (entry.Overrides.Count > 0)
            {
                var warning = Ui.Label("VersionOverride", "test-failed");
                warning.TooltipText = "These projects override the central version:\n" + string.Join("\n", entry.Overrides.Select(Path.GetFileNameWithoutExtension));
                row.Append(warning);
            }
            row.Append(Ui.Label(entry.Version, "package-version"));
            if (latest.TryGetValue(entry.Id, out var newest) && NuGetVersionComparer.Instance.Compare(newest, entry.Version) > 0)
            {
                row.Append(Ui.Label("→ " + newest, "package-update"));
                row.Append(Flat("Update", $"Set {entry.Id} to {newest} for every project", () => _ = UpdateCentralAsync(entry.Id)));
            }
            if (entry.Projects.Count == 0)
            {
                var id = entry.Id;
                row.Append(Flat("Remove", "Remove this unused PackageVersion", () => _ = RemoveCentralAsync(id)));
            }
            centralList.Append(row);
        }
    }

    async Task UpdateCentralAsync(string? id)
    {
        if (PropsFile is not { } props) return;
        var targets = latest.Where(l => id is null || l.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        var changedCount = 0;
        foreach (var (package, version) in targets)
            if (CentralPackageService.SetVersion(props, package, version)) changedCount++;
        if (changedCount == 0)
        {
            SetStatus("Nothing to update. Check for Updates first.");
            return;
        }
        await RestoreAsync($"Updated {changedCount} package version(s) in {Path.GetFileName(props)}");
        await LoadCentralAsync(checkUpdates: false);
    }

    async Task RemoveCentralAsync(string id)
    {
        if (PropsFile is not { } props) return;
        var choice = await Ui.ChooseAsync(window, $"Remove {id}?", $"No project references it. The PackageVersion is removed from {Path.GetFileName(props)}.",
            ["Cancel", "Remove"], 0, 0);
        if (choice != 1) return;
        CentralPackageService.Remove(props, id);
        latest.Remove(id);
        changed(props);
        await LoadCentralAsync(checkUpdates: false);
        SetStatus($"Removed {id}.");
    }

    async Task ConvertAsync()
    {
        if (SolutionDirectory is not { } dir) return;
        var plan = CentralPackageService.PlanConversion(dir, projects());
        if (plan.Versions.Count == 0)
        {
            SetStatus("No project references a package with a version: nothing to convert.");
            return;
        }
        var conflicts = plan.Conflicts.Take(10).Select(c => $"  {c.Id}: {string.Join(", ", c.Uses.Select(u => u.Version).Distinct())} → {c.Chosen}").ToList();
        if (plan.Conflicts.Count > 10) conflicts.Add($"  … and {plan.Conflicts.Count - 10} more");
        var detail = $"Creates {plan.PropsPath} with {plan.Versions.Count} package version(s) and removes the versions from {plan.Projects.Count} project file(s)."
            + (conflicts.Count > 0 ? $"\n\nProjects disagree on these versions; the highest is used:\n{string.Join("\n", conflicts)}" : "")
            + "\n\nCommit your work first: this changes every project file.";
        var choice = await Ui.ChooseAsync(window, "Convert to Central Package Management?", detail, ["Cancel", "Convert"], 0, 0);
        if (choice != 1) return;
        try
        {
            var changedProjects = await Task.Run(() => CentralPackageService.Convert(plan));
            output($"Central Package Management: {plan.PropsPath} created; versions removed from {changedProjects.Count} project(s).");
            await RestoreAsync("Converted to Central Package Management");
            openFile(plan.PropsPath);
            await LoadCentralAsync(checkUpdates: false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetStatus("Conversion failed: " + e.Message);
        }
    }

    /// <summary>dotnet restore for the solution after the versions changed; then the IDE reloads projects.</summary>
    async Task RestoreAsync(string what)
    {
        if (solution() is not { } target) return;
        var exit = 0;
        await RunAsync(what + " — restoring…", async ct => exit = await Task.Run(() => LightweightIDE.DotNet.Build.ProcessRunner.RunAsync("dotnet",
            ["restore", target], SolutionDirectory!, (line, _) => output(line), cancellationToken: ct), ct));
        changed(PropsFile ?? target);
        SetStatus(exit == 0 ? $"{what}." : $"{what}, but restore failed (exit code {exit}). See the Output panel.");
    }

    /// <summary>Asks which projects get the package, then adds it to each (with CPM the version goes to Directory.Packages.props once).</summary>
    async Task AddToProjectsAsync(string id, string version, string latestVersion)
    {
        var all = projects();
        if (all.Count == 0) return;
        var chosen = await ChooseProjectsAsync($"Add {id} {(version.Length > 0 ? version : latestVersion)}", all, Project is { } p ? [p] : []);
        if (chosen.Count == 0) return;
        var failed = new List<string>();
        await RunAsync($"Adding {id} to {chosen.Count} project(s)…", async ct =>
        {
            var first = true;
            foreach (var project in chosen)
            {
                // With CPM the first add writes the PackageVersion; later ones only add the reference.
                var exit = await Task.Run(() => packages.AddAsync(project, id, first || PropsFile is null ? (version.Length > 0 ? version : null) : null, output, ct), ct);
                if (exit != 0) failed.Add(Path.GetFileNameWithoutExtension(project));
                first = false;
            }
        });
        foreach (var project in chosen) changed(project);
        await LoadInstalledAsync();
        SetStatus(failed.Count == 0 ? $"Added {id} to {chosen.Count} project(s)." : $"Adding {id} failed for {string.Join(", ", failed)}. See the Output panel.");
    }

    Task<IReadOnlyList<string>> ChooseProjectsAsync(string title, IReadOnlyList<string> all, IReadOnlyList<string> preselected)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();
        var dialog = Gtk.Window.New();
        dialog.Title = title;
        dialog.SetTransientFor(window);
        dialog.Modal = true;
        dialog.SetDefaultSize(460, 420);
        var root = Ui.VBox(8);
        root.MarginTop = root.MarginBottom = root.MarginStart = root.MarginEnd = 12;
        root.Append(Ui.Label("Projects that get the package:"));
        var checks = new List<(Gtk.CheckButton Check, string Project)>();
        var list = Ui.VBox(2);
        foreach (var project in all)
        {
            var check = Gtk.CheckButton.NewWithLabel(Path.GetFileNameWithoutExtension(project));
            check.TooltipText = project;
            check.Active = preselected.Contains(project);
            checks.Add((check, project));
            list.Append(check);
        }
        root.Append(Ui.Scrolled(list));
        var buttons = Ui.HBox(6);
        buttons.Halign = Gtk.Align.End;
        var cancel = Gtk.Button.NewWithLabel("Cancel");
        var add = Gtk.Button.NewWithLabel("Add");
        add.AddCssClass("suggested-action");
        buttons.Append(cancel);
        buttons.Append(add);
        root.Append(buttons);
        dialog.SetChild(root);
        cancel.OnClicked += (_, _) => dialog.Close();
        add.OnClicked += (_, _) =>
        {
            tcs.TrySetResult(checks.Where(c => c.Check.Active).Select(c => c.Project).ToList());
            dialog.Close();
        };
        dialog.OnCloseRequest += (_, _) =>
        {
            tcs.TrySetResult([]);
            return false;
        };
        dialog.Present();
        return tcs.Task;
    }
}
