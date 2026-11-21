using LightweightIDE.App.Views;

namespace LightweightIDE.App.Windows;

/// <summary>New solution, add project and the folder's SDK (global.json).</summary>
public sealed partial class MainWindow
{
    void ShowNewSolution()
    {
        // Default location: the folder that contains the opened one, else ~/projects or the home folder.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var location = session.Folder is { } f ? Path.GetDirectoryName(f) ?? home
            : Directory.Exists(Path.Combine(home, "projects")) ? Path.Combine(home, "projects") : home;
        ShowPage(BottomPanel.OutputPage);
        bottom.ClearOutput();
        new NewSolutionDialog(window, scaffolder, location, bottom.AppendOutput, async solution =>
        {
            Status($"Created {Path.GetFileName(solution)}");
            await OpenFolderAsync(Path.GetDirectoryName(solution)!, solution);
            // Show the entry point of the first project.
            var src = Path.Combine(Path.GetDirectoryName(solution)!, "src");
            var program = Directory.Exists(src) ? Directory.EnumerateFiles(src, "Program.cs", SearchOption.AllDirectories).FirstOrDefault() : null;
            if (program is not null) editors.Open(program);
        }).Present();
    }

    void ShowAddProject()
    {
        if (session.Root?.Solution is not { } solution)
        {
            Status("Add Project needs a solution (.sln or .slnx). Create one with File → New Solution.");
            return;
        }
        var current = CurrentProject;
        var preferred = current is not null && !current.Contains("Test", StringComparison.OrdinalIgnoreCase) ? current : null;
        ShowPage(BottomPanel.OutputPage);
        bottom.ClearOutput();
        new AddProjectDialog(window, scaffolder, solution.Path, session.SdkVersion, AllProjects(), preferred, bottom.AppendOutput, async project =>
        {
            await session.RefreshSolutionAsync();
            explorer.Load(session.Root);
            ScheduleTestDiscovery(500);
            UpdateCommandState();
            var entry = Directory.EnumerateFiles(Path.GetDirectoryName(project)!, "*.cs").OrderBy(p => Path.GetFileName(p) == "Program.cs" ? 0 : 1).FirstOrDefault();
            if (entry is not null) editors.Open(entry);
            explorer.Reveal(project);
            Status($"Added {Path.GetFileNameWithoutExtension(project)} to {Path.GetFileName(solution.Path)}");
        }).Present();
    }

    void ShowSdkDialog()
    {
        if (session.Folder is not { } folder) return;
        new SdkDialog(window, folder, async () =>
        {
            await session.RefreshSdkAsync();
            sdkLabel.SetText(session.SdkVersion is { } v ? ".NET SDK " + v : "SDK not installed");
            explorer.RefreshPaths([Path.Combine(folder, "global.json")]);
            UpdateWorkspaceStatus();
            Status(session.SdkVersion is { } version ? $"global.json saved; the folder now uses SDK {version}. Projects are reloading."
                : "global.json saved, but the SDK it selects is not installed.", 10);
        }).Present();
    }

    /// <summary>Checklist of the solution's projects referenced by <paramref name="project"/>.</summary>
    void ShowProjectReferences(string project)
    {
        if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(project)) return;
        ShowPage(BottomPanel.OutputPage);
        new ProjectReferencesDialog(window, project, AllProjects(), bottom.AppendOutput, () =>
        {
            // Roslyn must read the project again to see the new references.
            _ = session.ReloadProjectsAsync();
            explorer.RefreshPaths([project]);
            Status($"References of {Path.GetFileNameWithoutExtension(project)} changed; reloading projects…");
            return Task.CompletedTask;
        }).Present();
    }

    /// <summary>Explorer → Dependencies → Projects → Remove Reference ("project|referenced project").</summary>
    async Task RemoveReferenceAsync(string pair)
    {
        if (pair.Split('|') is not [var project, var reference]) return;
        var name = Path.GetFileNameWithoutExtension(reference);
        var choice = await Ui.ChooseAsync(window, $"Remove the reference to {name}?",
            $"{Path.GetFileNameWithoutExtension(project)} will no longer reference {name}.", ["Cancel", "Remove"], 0, 1);
        if (choice != 1) return;
        ShowPage(BottomPanel.OutputPage);
        try
        {
            await Task.Run(() => LightweightIDE.DotNet.ProjectSystem.ProjectReferences.ApplyAsync(project, [], [reference], bottom.AppendOutput, CancellationToken.None));
            _ = session.ReloadProjectsAsync();
            explorer.RefreshPaths([project]);
            Status($"Removed the reference to {name}; reloading projects…");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            await Ui.ShowErrorAsync(window, "Cannot remove the reference", e.Message);
        }
    }

    /// <summary>File → Add Existing Project… or the explorer (solution / solution folder): pick .csproj files, dotnet sln add.</summary>
    async Task AddExistingProjectAsync(string? solutionFolder)
    {
        if (session.Root?.Solution is not { } solution)
        {
            Status("Add Existing Project needs a solution (.sln or .slnx).");
            return;
        }
        var dialog = Gtk.FileDialog.New();
        dialog.Title = solutionFolder is null ? "Add Existing Project" : $"Add Existing Project to {solutionFolder}";
        dialog.SetInitialFolder(Gio.FileHelper.NewForPath(solution.Directory));
        var filter = Gtk.FileFilter.New();
        filter.Name = "C# projects (*.csproj)";
        filter.AddPattern("*.csproj");
        var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
        filters.Append(filter);
        dialog.SetFilters(filters);
        dialog.SetDefaultFilter(filter);

        var picked = new List<string>();
        try
        {
            if (await dialog.OpenMultipleAsync(window) is { } files)
                for (uint i = 0; i < files.GetNItems(); i++)
                    if (files.GetObject(i) is Gio.File file && file.GetPath() is { } path) picked.Add(Path.GetFullPath(path));
        }
        catch (GLib.GException) { return; } // cancelled
        if (picked.Count == 0) return;

        var known = solution.Projects.Select(p => Path.GetFullPath(p.Path)).ToHashSet(StringComparer.Ordinal);
        var added = picked.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !known.Contains(p)).Distinct().ToList();
        if (added.Count == 0)
        {
            Status(picked.Count == 1 ? $"{Path.GetFileNameWithoutExtension(picked[0])} is already in the solution." : "These projects are already in the solution.");
            return;
        }
        ShowPage(BottomPanel.OutputPage);
        try
        {
            await Task.Run(() => LightweightIDE.DotNet.ProjectSystem.SolutionProjects.AddAsync(solution.Path, added, solutionFolder, bottom.AppendOutput, CancellationToken.None));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            await Ui.ShowErrorAsync(window, "Cannot add the project", e.Message);
            return;
        }
        await AfterSolutionChangedAsync();
        explorer.Reveal(added[0]);
        var names = string.Join(", ", added.Select(Path.GetFileNameWithoutExtension));
        Status($"Added {names} to {Path.GetFileName(solution.Path)}{(solutionFolder is null ? "" : " / " + solutionFolder)}.");
    }

    /// <summary>Explorer → Remove from Solution: dotnet sln remove; the project's files stay on disk.</summary>
    async Task RemoveFromSolutionAsync(string project)
    {
        if (session.Root?.Solution is not { } solution) return;
        var name = Path.GetFileNameWithoutExtension(project);
        var users = LightweightIDE.DotNet.ProjectSystem.SolutionProjects.ReferencedBy(project, AllProjects());
        var detail = $"The project stays on disk ({Path.GetRelativePath(solution.Directory, project)}); only the solution forgets it.";
        string[] buttons = ["Cancel", "Remove"];
        if (users.Count > 0)
        {
            detail += $"\n\nStill referenced by {string.Join(", ", users.Select(Path.GetFileNameWithoutExtension))}: " +
                "they still build (MSBuild follows project references outside the solution), but the dependency is no longer " +
                "visible in the solution. Remove the references too if the project is no longer needed.";
            buttons = ["Cancel", "Remove, Keep References", "Remove with References"];
        }
        var choice = await Ui.ChooseAsync(window, $"Remove {name} from {Path.GetFileName(solution.Path)}?", detail, buttons, 0, buttons.Length - 1);
        if (choice <= 0) return;
        ShowPage(BottomPanel.OutputPage);
        try
        {
            await Task.Run(async () =>
            {
                await LightweightIDE.DotNet.ProjectSystem.SolutionProjects.RemoveAsync(solution.Path, [project], bottom.AppendOutput, CancellationToken.None);
                if (choice == 2)
                    foreach (var user in users)
                        await LightweightIDE.DotNet.ProjectSystem.ProjectReferences.ApplyAsync(user, [], [project], bottom.AppendOutput, CancellationToken.None);
            });
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            await Ui.ShowErrorAsync(window, "Cannot remove the project", e.Message);
        }
        await AfterSolutionChangedAsync();
        Status(choice == 2 ? $"Removed {name} and {users.Count} reference(s) to it." : $"Removed {name} from the solution; the files stay on disk.");
    }

    /// <summary>The .sln / .slnx changed: read it again, rebuild the tree, reload the workspace.</summary>
    async Task AfterSolutionChangedAsync()
    {
        await session.RefreshSolutionAsync();
        explorer.Load(session.Root);
        UpdateCommandState();
        ScheduleTestDiscovery(500);
    }
}
