using System.Diagnostics;
using System.Reflection;
using LightweightIDE.App.Application;
using LightweightIDE.App.Views;
using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.DotNet.Testing;
using LightweightIDE.Language.Formatting;
using LightweightIDE.Language.RoslynWorkspace;
using LightweightIDE.Language.Testing;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Windows;

public sealed partial class MainWindow
{
    readonly Dictionary<string, Gio.SimpleAction> actions = new(StringComparer.Ordinal);

    // ---------- actions and shortcuts ----------

    void CreateActions()
    {
        // File
        Add("open-folder", () => _ = PickFolderAsync(), "<Control>o");
        Add("open-file", () => _ = PickFileAsync(), "<Control><Shift>o");
        Add("new-file", () => _ = OnExplorerActionAsync("new-file", CurrentDirectory()), "<Control>n");
        Add("new-solution", ShowNewSolution, "<Control><Shift>n");
        Add("add-project", ShowAddProject);
        Add("add-existing-project", () => _ = AddExistingProjectAsync(null));
        Add("change-sdk", ShowSdkDialog);
        Add("save", () => _ = SaveActiveAsync(), "<Control>s");
        Add("save-as", () => _ = SaveAsAsync());
        Add("save-all", editors.SaveAll, "<Control><Alt>s");
        Add("close-tab", () => { if (editors.Active is { } t) _ = editors.CloseAsync(t); }, "<Control>w", "<Control>F4");
        Add("close-folder", () => _ = CloseFolderAsync());
        Add("select-solution", () => _ = SelectSolutionAsync());
        Add("settings", OpenSettings, "<Control>comma");
        Add("quit", () => window.Close(), "<Control>q");

        // Edit
        Add("find", () => editors.FindBar.Open(false), "<Control>f");
        Add("replace", () => editors.FindBar.Open(true), "<Control>h");
        Add("find-next", () => editors.FindBar.Next(true), "F3");
        Add("find-previous", () => editors.FindBar.Next(false), "<Shift>F3");
        Add("toggle-comment", ToggleComment, "<Control>slash");
        Add("markdown-preview", () => editors.Active?.CycleMarkdownView(), "<Control><Shift>v");
        Add("goto-line", () => _ = GoToLineAsync(), "<Control>g");
        Add("format-document", () => { if (editors.Active is { } t) _ = FormatDocumentAsync(t); }, "<Control><Shift>i");
        Add("rename-symbol", () => _ = RenameSymbolAsync(), "<Shift>F6");
        Add("code-actions", () => _ = ShowCodeActionsAsync(), "<Alt>Return", "<Control>period");
        Add("implement-members", () => _ = ImplementMissingMembersAsync(), "<Control>i");
        Add("http-send", () => { if (editors.Active is { IsHttp: true } t) _ = SendHttpAsync(t, t.CursorLineColumn().Line); }, "<Control>Return");
        Add("http-reset", () => { if (editors.Active is { IsHttp: true } t) { http.Reset(t.Path); Status("HTTP session reset: cookies and stored responses of this file are gone."); } });
        Add("show-http", () => ShowPage(BottomPanel.HttpPage));

        // View
        Add("toggle-explorer", () => ShowExplorer(!explorer.Widget.Visible), "<Control>b");
        Add("toggle-panel", () => ShowPanel(!bottom.Widget.Visible), "<Control>j");
        Add("show-problems", () => ShowPage(BottomPanel.ProblemsPage), "<Control><Shift>m");
        Add("show-output", () => ShowPage(BottomPanel.OutputPage), "<Control><Shift>u");
        Add("show-terminal", () => { ShowPage(BottomPanel.TerminalPage); bottom.Terminal.Focus(); }, "<Control>grave");
        Add("show-tests", () => ShowPage(BottomPanel.TestsPage));
        Add("show-results", () => ShowPage(BottomPanel.ResultsPage));
        Add("theme-system", () => SetTheme("system"));
        Add("theme-light", () => SetTheme("light"));
        Add("theme-dark", () => SetTheme("dark"));
        Add("color-scheme-auto", () => SetColorScheme(null));
        Add("toggle-inlay-hints", () =>
        {
            var on = !(state.InlayHints ?? editorOptions.InlayHints);
            state.InlayHints = on;
            editorOptions.InlayHints = on;
            foreach (var tab in editors.Tabs) tab.SetInlayHints(on);
            Status(on ? "Inlay hints on." : "Inlay hints off.");
        }, "<Control><Alt>h");
        foreach (var (id, _) in ColorSchemes.All()) Add(ColorSchemeAction(id), () => SetColorScheme(id));
        Add("next-tab", () => editors.NextTab(1), "<Control>Page_Down", "<Control>Tab");
        Add("previous-tab", () => editors.NextTab(-1), "<Control>Page_Up", "<Control><Shift>Tab");
        Add("focus-editor", () => editors.Active?.Focus(), "Escape");

        // Navigate
        Add("quick-open", () => palette.Open(PaletteMode.Files), "<Control>p");
        Add("goto-symbol", () => palette.Open(PaletteMode.Symbols, editors.Active?.SelectedWord() ?? ""), "<Control><Shift>p", "<Control>t");
        Add("find-in-files", () => palette.Open(PaletteMode.Text, editors.Active?.SelectedText() ?? ""), "<Control><Shift>f");
        Add("goto-definition", () => _ = GoToDefinitionAsync(), "F12");
        Add("find-references", () => _ = FindReferencesAsync(), "<Shift>F12", "<Alt>F7");
        Add("goto-implementation", () => _ = GoToImplementationAsync(), "<Control><Alt>b");
        Add("goto-tests", () => _ = GoToTestsAsync(), "<Control><Shift>g");
        Add("recent-files", ShowRecentFiles, "<Control>e");
        Add("file-structure", ShowFileStructure, "<Control>F12");
        Add("next-problem", () => NextProblem(), "F8");
        Add("navigate-back", () => MoveInHistory(back: true), "<Alt>Left");
        Add("navigate-forward", () => MoveInHistory(back: false), "<Alt>Right");
        Add("reveal-in-explorer", () => { if (editors.Active is { } t) { ShowExplorer(true); explorer.Reveal(t.Path); } }, "<Control><Shift>e");

        // Build
        Add("build-solution", () => _ = RunBuildAsync(BuildAction.Build, SolutionTarget), "<Control><Shift>b");
        Add("rebuild-solution", () => _ = RunBuildAsync(BuildAction.Rebuild, SolutionTarget));
        Add("clean-solution", () => _ = RunBuildAsync(BuildAction.Clean, SolutionTarget));
        Add("restore-solution", () => _ = RunBuildAsync(BuildAction.Restore, SolutionTarget));
        Add("build-project", () => _ = RunBuildAsync(BuildAction.Build, CurrentProject), "F6");
        Add("rebuild-project", () => _ = RunBuildAsync(BuildAction.Rebuild, CurrentProject));
        Add("clean-project", () => _ = RunBuildAsync(BuildAction.Clean, CurrentProject));
        Add("config-debug", () => configuration.Selected = 0);
        Add("config-release", () => configuration.Selected = 1);
        Add("analyze-project", () => _ = AnalyzeProjectAsync());
        Add("nuget", () => OpenPackages(null), "<Control><Alt>n");
        Add("project-references", () => { if (CurrentProject is { } p) ShowProjectReferences(p); });
        Add("run-project", RunProject, "<Control>F5");
        Add("stop", () => operationCts?.Cancel(), "<Control>F2");

        CreateDebugActions();

        // Test
        Add("test-solution", () => _ = RunTestsAsync(SolutionTarget, null), "<Control><Shift>t");
        Add("test-project", () => _ = RunTestsAsync(CurrentProject, null));
        Add("test-at-cursor", () => _ = RunTestAtCursorAsync(), "<Control><Shift>r");
        Add("test-file", () => _ = RunTestsInFileAsync(), "<Control><Alt>r");
        Add("test-rerun", () =>
        {
            if (lastTestRun is { } rerun) _ = rerun();
            else Status("No tests have run yet in this session.");
        }, "<Shift><Alt>r");

        // Terminal
        Add("terminal-restart", () => { if (session.Folder is { } f) { bottom.Terminal.Start(f); ShowPage(BottomPanel.TerminalPage); } });
        foreach (var (name, command) in new[] { ("restore", "dotnet restore"), ("info", "dotnet --info"), ("sdks", "dotnet --list-sdks"), ("format", "dotnet format"), ("outdated", "dotnet list package --outdated") })
            Add($"terminal-{name}", () => RunInTerminal(command));

        // Index / workspace
        Add("reindex", () => { _ = session.ReindexAsync(force: true); Status("Rebuilding the index…"); });
        Add("reload-projects", () => { _ = session.ReloadProjectsAsync(); Status("Reloading projects…"); });
        Add("refresh-explorer", explorer.Refresh, "<Control><Shift>F5");

        // Help
        Add("shortcuts", ShowShortcuts, "<Control>F1");
        Add("about", () => _ = ShowAboutAsync());
        Add("open-logs", () => OpenExternally(AppPaths.LogDirectory));

        // Recent folders: parameter = folder path
        var recent = Gio.SimpleAction.New("open-recent", GLib.VariantType.New("s"));
        recent.OnActivate += (sender, args) =>
        {
            if (args.Parameter?.GetString(out nuint _) is { } folder) _ = OpenFolderAsync(folder);
        };
        window.AddAction(recent);
        RebuildRecentMenu();
    }

    void Add(string name, Action handler, params string[] accels)
    {
        var action = Gio.SimpleAction.New(name, null);
        action.OnActivate += (_, _) =>
        {
            try { handler(); }
            catch (Exception e)
            {
                logger.LogError(e, "Command {Command} failed", name);
                Status($"{name} failed: {e.Message}");
            }
        };
        window.AddAction(action);
        actions[name] = action;
        if (accels.Length > 0) app.SetAccelsForAction("win." + name, accels);
    }

    void Enable(string name, bool enabled)
    {
        if (actions.TryGetValue(name, out var a)) a.SetEnabled(enabled);
    }

    void UpdateCommandState()
    {
        var open = session.IsOpen;
        var busy = operationCts is not null;
        var tab = editors.Active;
        foreach (var n in new[] { "build-solution", "rebuild-solution", "clean-solution", "restore-solution", "test-solution" })
            Enable(n, open && !busy);
        var project = CurrentProject is not null;
        foreach (var n in new[] { "build-project", "rebuild-project", "clean-project", "test-project", "test-at-cursor", "project-references" })
            Enable(n, open && !busy && project);
        Enable("run-project", open);
        UpdateStartupButton();
        Enable("analyze-project", open && !busy && project);
        Enable("stop", busy);
        foreach (var n in new[] { "save", "save-as", "close-tab", "find", "replace", "find-next", "find-previous", "goto-line", "toggle-comment", "reveal-in-explorer" })
            Enable(n, tab is not null);
        var cs = tab?.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true;
        Enable("goto-definition", cs);
        Enable("find-references", cs);
        Enable("goto-implementation", cs);
        Enable("goto-tests", cs);
        Enable("file-structure", cs);
        Enable("recent-files", open);
        foreach (var n in new[] { "close-folder", "reindex", "reload-projects", "refresh-explorer", "terminal-restart" })
            Enable(n, open);
        Enable("select-solution", (session.Root?.SolutionFiles.Count ?? 0) > 1);
        Enable("rename-symbol", cs);
        Enable("code-actions", cs);
        Enable("implement-members", cs);
        Enable("http-send", tab?.IsHttp == true);
        Enable("test-debug-at-cursor", open && !busy && cs && CurrentProject is not null);
        Enable("test-file", open && !busy && cs && CurrentProject is not null);
        Enable("test-debug-file", open && !busy && cs && CurrentProject is not null);
        Enable("test-run-failed", open && !busy);
        Enable("test-rerun", open && !busy);
        Enable("http-reset", tab?.IsHttp == true);
        Enable("markdown-preview", tab?.IsMarkdown == true);
        Enable("format-document", tab is not null && FormattingService.CanFormat(tab.Path));
        Enable("nuget", open);
        Enable("add-project", session.Root?.Solution is not null);
        Enable("add-existing-project", session.Root?.Solution is not null);
        Enable("change-sdk", open);
        UpdateDebugState();
        UpdateHistoryState();
        busySpinner.Spinning = busy;
        busySpinner.Visible = busy;
    }

    // ---------- menu ----------

    Gio.Menu CreateMenu()
    {
        var menu = Gio.Menu.New();

        var file = Gio.Menu.New();
        file.AppendSection(null, Section(("New Solution…", "new-solution"), ("Open Folder…", "open-folder"), ("Open File…", "open-file"), ("New File…", "new-file")));
        file.AppendSubmenu("Open Recent", recentMenu);
        file.AppendSection(null, Section(("Add Project…", "add-project"), ("Add Existing Project…", "add-existing-project"), ("SDK and global.json…", "change-sdk")));
        file.AppendSection(null, Section(("Select Solution…", "select-solution"), ("Close Folder", "close-folder")));
        file.AppendSection(null, Section(("Save", "save"), ("Save As…", "save-as"), ("Save All", "save-all"), ("Close Tab", "close-tab")));
        file.AppendSection(null, Section(("Settings (JSON)", "settings"), ("Quit", "quit")));
        menu.AppendSubmenu("_File", file);

        var edit = Gio.Menu.New();
        edit.AppendSection(null, Section(("Find…", "find"), ("Replace…", "replace"), ("Find Next", "find-next"), ("Find Previous", "find-previous")));
        edit.AppendSection(null, Section(("Toggle Line Comment", "toggle-comment"), ("Go to Line…", "goto-line")));
        edit.AppendSection(null, Section(("Quick Fixes and Refactorings…", "code-actions"), ("Implement Missing Members", "implement-members"), ("Format Document", "format-document"), ("Rename Symbol…", "rename-symbol")));
        menu.AppendSubmenu("_Edit", edit);

        var view = Gio.Menu.New();
        view.AppendSection(null, Section(("Explorer", "toggle-explorer"), ("Bottom Panel", "toggle-panel"), ("Markdown Preview (split / preview / source)", "markdown-preview")));
        view.AppendSection(null, Section(("Problems", "show-problems"), ("Output", "show-output"), ("Terminal", "show-terminal"),
            ("Test Results", "show-tests"), ("Find Results", "show-results"), ("HTTP Responses", "show-http")));
        var theme = Section(("System", "theme-system"), ("Light", "theme-light"), ("Dark", "theme-dark"));
        view.AppendSubmenu("Theme", theme);
        var colors = Gio.Menu.New();
        colors.AppendSection(null, Section(("Automatic (Lightweight Light / Dark)", "color-scheme-auto")));
        colors.AppendSection(null, Section(ColorSchemes.All().Select(s => (s.Name, ColorSchemeAction(s.Id))).ToArray()));
        view.AppendSubmenu("Color Theme", colors);
        view.AppendSection(null, Section(("Inlay Hints (parameter names, types)", "toggle-inlay-hints")));
        view.AppendSection(null, Section(("Next Tab", "next-tab"), ("Previous Tab", "previous-tab"), ("Refresh Explorer", "refresh-explorer")));
        menu.AppendSubmenu("_View", view);

        var navigate = Gio.Menu.New();
        navigate.AppendSection(null, Section(("Back", "navigate-back"), ("Forward", "navigate-forward")));
        navigate.AppendSection(null, Section(("Quick Open…", "quick-open"), ("Go to Symbol…", "goto-symbol"), ("Find in Files…", "find-in-files")));
        navigate.AppendSection(null, Section(("Recent Files…", "recent-files"), ("File Structure…", "file-structure")));
        navigate.AppendSection(null, Section(("Go to Definition", "goto-definition"), ("Go to Implementation", "goto-implementation"),
            ("Go to Tests / Tested Code", "goto-tests"), ("Find All References", "find-references"),
            ("Next Problem", "next-problem"), ("Reveal in Explorer", "reveal-in-explorer")));
        menu.AppendSubmenu("_Navigate", navigate);

        var build = Gio.Menu.New();
        build.AppendSection(null, Section(("Build Solution", "build-solution"), ("Rebuild Solution", "rebuild-solution"),
            ("Clean Solution", "clean-solution"), ("Restore Packages", "restore-solution")));
        build.AppendSection(null, Section(("Build Current Project", "build-project"), ("Rebuild Current Project", "rebuild-project"),
            ("Clean Current Project", "clean-project"), ("Analyze Current Project (Roslyn)", "analyze-project")));
        build.AppendSection(null, Section(("Manage NuGet Packages…", "nuget"), ("Project References…", "project-references")));
        build.AppendSection(null, Section(("Configuration: Debug", "config-debug"), ("Configuration: Release", "config-release")));
        build.AppendSection(null, Section(("Run Startup Project in Terminal", "run-project"), ("Stop", "stop")));
        menu.AppendSubmenu("_Build", build);
        menu.AppendSubmenu("_Debug", CreateDebugMenu());

        var test = Gio.Menu.New();
        test.AppendSection(null, Section(("Run All Tests", "test-solution"), ("Run Tests in Current Project", "test-project"),
            ("Run Test at Cursor", "test-at-cursor"), ("Debug Test at Cursor", "test-debug-at-cursor"),
            ("Run Tests in Current File", "test-file"), ("Debug Tests in Current File", "test-debug-file"), ("Run Failed Tests", "test-run-failed"), ("Rerun Last Tests", "test-rerun"), ("Stop", "stop")));
        menu.AppendSubmenu("_Test", test);

        var terminal = Gio.Menu.New();
        terminal.AppendSection(null, Section(("Show Terminal", "show-terminal"), ("Restart Shell", "terminal-restart")));
        terminal.AppendSection(null, Section(("dotnet restore", "terminal-restore"), ("dotnet format", "terminal-format"),
            ("dotnet list package --outdated", "terminal-outdated"), ("dotnet --list-sdks", "terminal-sdks"), ("dotnet --info", "terminal-info")));
        menu.AppendSubmenu("Te_rminal", terminal);
        var httpMenu = Gio.Menu.New();
        httpMenu.AppendSection(null, Section(("Send Request at Caret", "http-send"), ("Reset Session (cookies, responses)", "http-reset"), ("Show Responses", "show-http")));
        menu.AppendSubmenu("HTT_P", httpMenu);

        var help = Gio.Menu.New();
        help.AppendSection(null, Section(("Keyboard Shortcuts", "shortcuts"), ("Rebuild Index", "reindex"), ("Reload Projects", "reload-projects"),
            ("Open Log Folder", "open-logs")));
        help.AppendSection(null, Section(("About", "about")));
        menu.AppendSubmenu("_Help", help);
        return menu;
    }

    static Gio.Menu Section(params (string Label, string Action)[] items)
    {
        var m = Gio.Menu.New();
        foreach (var (label, action) in items) m.Append(label, "win." + action);
        return m;
    }

    void RebuildRecentMenu()
    {
        recentMenu.RemoveAll();
        foreach (var folder in state.RecentFolders.Where(Directory.Exists))
        {
            var item = Gio.MenuItem.New(folder.Replace("_", "__"), null);
            item.SetActionAndTargetValue("win.open-recent", GLib.Variant.NewString(folder));
            recentMenu.AppendItem(item);
        }
    }

    // ---------- folders ----------

    async Task PickFolderAsync()
    {
        var dialog = Gtk.FileDialog.New();
        dialog.Title = "Open Folder";
        if ((session.Folder ?? state.RecentFolders.FirstOrDefault()) is { } initial && Directory.Exists(initial))
            dialog.SetInitialFolder(Gio.FileHelper.NewForPath(Path.GetDirectoryName(initial.TrimEnd('/')) ?? initial));
        try
        {
            var folder = await dialog.SelectFolderAsync(window);
            if (folder?.GetPath() is { } path) await OpenFolderAsync(path);
        }
        catch (GLib.GException) { } // dismissed
    }

    async Task OpenFolderAsync(string folder, string? preferredSolution = null)
    {
        folder = Path.GetFullPath(folder).TrimEnd('/');
        if (folder.Length == 0) folder = "/";
        if (!Directory.Exists(folder))
        {
            await Ui.ShowErrorAsync(window, "Folder not found", folder);
            return;
        }
        if (session.Folder is { } previous)
        {
            state.OpenFiles[previous] = editors.Tabs.Select(t => t.Path).ToList();
            if (!await editors.CloseAllAsync()) return;
        }
        operationCts?.Cancel();
        preferredSolution ??= state.PreferredSolution.GetValueOrDefault(folder);

        Status($"Opening {folder}…");
        try
        {
            await session.OpenAsync(folder, preferredSolution, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Cannot open {Folder}", folder);
            await Ui.ShowErrorAsync(window, "Cannot open folder", e.Message);
            return;
        }

        var root = session.Root!;
        if (root.Solution is not null) state.PreferredSolution[folder] = root.Solution.Path;
        state.AddRecent(folder);
        state.Save();
        RebuildRecentMenu();

        explorer.StartupProject = state.StartupProject.GetValueOrDefault(folder);
        explorer.Load(root);
        ScheduleTestDiscovery(500);
        bottom.Root = folder;
        bottom.SetBuildDiagnostics([]);
        bottom.Terminal.Start(folder);
        buildLabel.SetText("");
        branchLabel.SetText(GitInfo.CurrentBranch(folder) is { } branch ? "⎇ " + branch : "");
        solutionLabel.SetText(root.Solution is not null ? Path.GetFileName(root.Solution.Path)
            : root.LooseProjects.Count > 0 ? $"{Path.GetFileName(folder)} ({root.LooseProjects.Count} projects)"
            : Path.GetFileName(folder));
        solutionLabel.TooltipText = root.Solution?.Path ?? folder;
        sdkLabel.SetText("");
        UpdateWorkspaceStatus();
        UpdateTitle();
        UpdateCommandState();
        Status(root.SolutionFiles.Count > 1 ? $"{root.SolutionFiles.Count} solutions found, using {Path.GetFileName(root.Solution?.Path)} (File → Select Solution)"
            : $"Opened {folder}");

        // SDK version is resolved in the background by `dotnet --version` in the folder (global.json aware).
        Ui.After(1500, () =>
        {
            sdkLabel.SetText(session.SdkVersion is { } v ? ".NET SDK " + v : "");
            sdkLabel.TooltipText = "SDK selected by global.json for this folder";
        });

        foreach (var path in state.OpenFiles.GetValueOrDefault(folder) ?? [])
            if (File.Exists(path)) editors.Open(path, focus: false);
        editors.Active?.Focus();
        await OfferRecoveryAsync(folder);
    }

    async Task CloseFolderAsync()
    {
        if (session.Folder is not { } folder) return;
        state.OpenFiles[folder] = editors.Tabs.Select(t => t.Path).ToList();
        if (!await editors.CloseAllAsync()) return;
        operationCts?.Cancel();
        session.Close();
        explorer.Load(null);
        bottom.SetBuildDiagnostics([]);
        foreach (var l in new[] { branchLabel, sdkLabel, workspaceLabel, indexLabel, buildLabel }) l.SetText("");
        solutionLabel.SetText("No folder");
        UpdateTitle();
        UpdateCommandState();
    }

    async Task SelectSolutionAsync()
    {
        if (session.Root is not { SolutionFiles.Count: > 1 } root) return;
        var files = root.SolutionFiles.Take(8).ToList();
        var buttons = new List<string> { "Cancel" };
        buttons.AddRange(files.Select(f => Path.GetRelativePath(root.Folder, f)));
        var choice = await Ui.ChooseAsync(window, "Select solution", "The explorer, builds and code intelligence use the chosen solution.",
            buttons.ToArray(), 0, 1);
        if (choice <= 0) return;
        state.PreferredSolution[root.Folder] = files[choice - 1];
        await OpenFolderAsync(root.Folder, files[choice - 1]);
    }

    string CurrentDirectory()
    {
        if (editors.Active is { } tab) return Path.GetDirectoryName(tab.Path)!;
        return session.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // ---------- files ----------

    async Task PickFileAsync()
    {
        var dialog = Gtk.FileDialog.New();
        dialog.Title = "Open File";
        if (Directory.Exists(CurrentDirectory())) dialog.SetInitialFolder(Gio.FileHelper.NewForPath(CurrentDirectory()));
        try
        {
            var file = await dialog.OpenAsync(window);
            if (file?.GetPath() is { } path) editors.Open(path);
        }
        catch (GLib.GException) { }
    }

    async Task SaveAsAsync()
    {
        if (editors.Active is not { } tab) return;
        var dialog = Gtk.FileDialog.New();
        dialog.Title = "Save As";
        dialog.SetInitialFolder(Gio.FileHelper.NewForPath(Path.GetDirectoryName(tab.Path)!));
        dialog.InitialName = tab.Document.FileName;
        try
        {
            var file = await dialog.SaveAsync(window);
            if (file?.GetPath() is not { } path) return;
            tab.SaveAs(path);
            explorer.RefreshPaths([path]);
            UpdateTitle();
        }
        catch (GLib.GException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Ui.ShowErrorAsync(window, "Cannot save file", e.Message);
        }
    }

    async Task OnExplorerActionAsync(string action, string path)
    {
        try
        {
            switch (action)
            {
                case "new-file": await NewFileAsync(path); break;
                case "new-folder": await NewFolderAsync(path); break;
                case "rename": await RenameAsync(path); break;
                case "delete": await DeleteAsync(path); break;
                case "terminal":
                    ShowPage(BottomPanel.TerminalPage);
                    bottom.Terminal.Run("cd " + ShellQuote(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!));
                    break;
                case "reveal": OpenExternally(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!); break;
                case "build": await RunBuildAsync(BuildAction.Build, path); break;
                case "run": await RunProjectAsync(path); break;
                case "debug": await StartOrContinueAsync(path); break;
                case "set-startup": SetStartupProject(path); break;
                case "test" when path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase):
                    await RunTestsAsync(path, null);
                    break;
                case "test": await RunTestsInPathAsync(path); break;
                case "references": ShowProjectReferences(path); break;
                case "remove-reference": await RemoveReferenceAsync(path); break;
                case "format": await FormatFileAsync(path); break;
                case "nuget": OpenPackages(path); break;
                case "add-project": ShowAddProject(); break;
                case "remove-from-solution": await RemoveFromSolutionAsync(path); break;
                case "copy-path":
                    window.GetDisplay().GetClipboard().SetText(path);
                    Status("Path copied: " + path);
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Ui.ShowErrorAsync(window, $"Cannot {action.Replace('-', ' ')}", e.Message);
        }
    }

    static string? ValidateName(string directory, string name, string? current = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.Contains('/') || name is "." or "..") return "The name cannot contain '/'.";
        if (name != current && (File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name))))
            return "A file or folder with this name already exists.";
        return null;
    }

    async Task NewFileAsync(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var name = await Ui.PromptAsync(window, "New File", $"File name in {Path.GetFileName(directory)}/", "NewClass.cs",
            n => ValidateName(directory, n));
        if (name is null) return;
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? CSharpTemplate(path) : "");
        explorer.RefreshPaths([path]);
        explorer.Reveal(path);
        if (editors.Open(path) is { } tab && name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // caret inside the class body
            var lines = File.ReadAllLines(path);
            var body = Array.FindLastIndex(lines, l => l.Trim() == "{");
            if (body >= 0) tab.GoTo(body + 2, 5);
        }
    }

    /// <summary>File-scoped namespace from the project's root namespace (project name) and the folder path.</summary>
    string CSharpTemplate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var project = session.ProjectFileFor(path) ?? new ProjectLocator(Path.GetDirectoryName(path)!).FindProjectFile(path);
        var ns = "MyNamespace";
        if (project is not null)
        {
            var relative = Path.GetRelativePath(Path.GetDirectoryName(project)!, Path.GetDirectoryName(path)!);
            var parts = new List<string> { Path.GetFileNameWithoutExtension(project) };
            if (relative != ".") parts.AddRange(relative.Split('/'));
            ns = string.Join('.', parts.Select(p => new string(p.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray())));
        }
        var kind = name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]) ? "interface" : "class";
        return $"namespace {ns};\n\npublic {kind} {name}\n{{\n    \n}}\n";
    }

    async Task NewFolderAsync(string directory)
    {
        var name = await Ui.PromptAsync(window, "New Folder", $"Folder name in {Path.GetFileName(directory)}/", "NewFolder", n => ValidateName(directory, n));
        if (name is null) return;
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(path);
        explorer.RefreshPaths([path]);
        explorer.Reveal(path);
    }

    async Task RenameAsync(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var current = Path.GetFileName(path);
        var name = await Ui.PromptAsync(window, "Rename", $"New name for {current}", current, n => ValidateName(directory, n, current));
        if (name is null || name == current) return;
        var target = Path.Combine(directory, name);
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        editors.PathRenamed(path, target);
        history.Rename(path, target);
        breakpoints.Rename(path, target);
        explorer.RefreshPaths([path, target]);
        session.Projects?.Invalidate();
        Status($"Renamed to {name}");
    }

    async Task DeleteAsync(string path)
    {
        var isDirectory = Directory.Exists(path);
        var choice = await Ui.ChooseAsync(window, $"Move {Path.GetFileName(path)} to the trash?",
            isDirectory ? "The folder and everything in it will be moved to the trash." : path, ["Cancel", "Move to Trash"], 0, 0);
        if (choice != 1) return;

        var prefix = path.TrimEnd('/') + "/";
        foreach (var tab in editors.Tabs.Where(t => t.Path == path || t.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            tab.Buffer.SetModified(false); // the user agreed to remove the file
            await editors.CloseAsync(tab);
        }
        var exit = await ProcessRunner.RunAsync("gio", ["trash", path], Path.GetDirectoryName(path)!, (_, _) => { });
        if (exit != 0)
        {
            await Ui.ShowErrorAsync(window, "Cannot move to trash", $"`gio trash` failed with exit code {exit}.");
            return;
        }
        explorer.RefreshPaths([path]);
        history.Remove(path);
        UpdateHistoryState();
        session.Projects?.Invalidate();
        Status($"Moved {Path.GetFileName(path)} to the trash");
    }

    void OpenSettings()
    {
        if (!File.Exists(AppPaths.UserSettingsFile))
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            var defaults = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            File.WriteAllText(AppPaths.UserSettingsFile, File.Exists(defaults) ? File.ReadAllText(defaults) : "{\n}\n");
        }
        editors.Open(AppPaths.UserSettingsFile);
        Status("Settings are read at startup: restart the IDE after saving.", 10);
    }

    /// <summary>Action names allow letters, digits, '-' and '.'.</summary>
    static string ColorSchemeAction(string id) => "color-scheme-" + new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray());

    void SetColorScheme(string? id)
    {
        state.ColorScheme = id;
        ApplyTheme(state.ThemeOverride ?? uiOptions.Theme);
        Status(id is null ? "Color theme: Lightweight Light / Dark (follows the theme)" : "Color theme: " + (ColorSchemes.Manager.GetScheme(id)?.GetName() ?? id));
    }

    void SetTheme(string theme)
    {
        state.ThemeOverride = theme;
        ApplyTheme(theme);
    }

    static void OpenExternally(string path)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", [path]) { UseShellExecute = false })?.Dispose(); }
        catch (Exception) { }
    }

    static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ---------- editing ----------

    async Task GoToLineAsync()
    {
        if (editors.Active is not { } tab) return;
        var (line, _) = tab.CursorLineColumn();
        var lines = tab.Buffer.GetLineCount();
        var input = await Ui.PromptAsync(window, "Go to Line", $"Line[:column] (1–{lines})", line.ToString(), text =>
        {
            var parts = text.Split(':', ',');
            return int.TryParse(parts[0].Trim(), out var l) && l >= 1 && (parts.Length == 1 || int.TryParse(parts[1].Trim(), out _))
                ? null : "Enter a line number, optionally followed by :column.";
        });
        if (input is null) return;
        var p = input.Split(':', ',');
        var from = tab.Position();
        tab.GoTo(Math.Min(int.Parse(p[0].Trim()), lines), p.Length > 1 ? int.Parse(p[1].Trim()) : 1);
        history.Record(from, tab.Position());
        UpdateHistoryState();
        tab.Focus();
    }

    void ToggleComment()
    {
        if (editors.Active is not { } tab) return;
        var buffer = tab.Buffer;
        var start = new Gtk.TextIter();
        var end = new Gtk.TextIter();
        buffer.GetSelectionBounds(out start, out end);
        var first = start.GetLine();
        var last = end.GetLine();
        if (last > first && end.GetLineOffset() == 0) last--; // selection ends at the start of the next line

        var lines = new List<string>();
        for (var i = first; i <= last; i++)
        {
            var a = new Gtk.TextIter();
            buffer.GetIterAtLine(out a, i);
            var b = a.Copy();
            if (!b.EndsLine()) b.ForwardToLineEnd();
            lines.Add(buffer.GetText(a, b, true));
        }
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        if (nonEmpty.Count == 0) return;
        var uncomment = nonEmpty.All(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        var indent = nonEmpty.Min(l => l.Length - l.TrimStart().Length);

        buffer.BeginUserAction();
        for (var i = first; i <= last; i++)
        {
            var text = lines[i - first];
            if (text.Trim().Length == 0) continue;
            var a = new Gtk.TextIter();
            buffer.GetIterAtLine(out a, i);
            if (uncomment)
            {
                var at = text.IndexOf("//", StringComparison.Ordinal);
                var length = at + 2 < text.Length && text[at + 2] == ' ' ? 3 : 2;
                a.SetLineOffset(at);
                var b = a.Copy();
                b.ForwardChars(length);
                buffer.Delete(a, b);
            }
            else
            {
                a.SetLineOffset(indent);
                buffer.Insert(a, "// ", -1);
            }
        }
        buffer.EndUserAction();
    }

    void NextProblem()
    {
        var tab = editors.Active;
        var line = tab?.CursorLineColumn().Line ?? 0;
        if (bottom.NextProblem(tab?.Path, line) is { } location) Navigate(location);
        else Status("No problems.");
    }

    // ---------- code navigation ----------

    (EditorTab Tab, string Text, int Position, CancellationToken Token)? NavigationContext()
    {
        if (editors.Active is not { } tab || !tab.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;
        session.EnsureWorkspaceLoaded();
        navigationCts?.Cancel();
        navigationCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var text = tab.GetText();
        return (tab, text, TextOffsets.CharToUtf16(text, tab.CursorOffset), navigationCts.Token);
    }

    async Task GoToDefinitionAsync()
    {
        if (NavigationContext() is not { } c) return;
        Status("Finding definition…");
        try
        {
            var result = await Task.Run(() => navigation.GoToDefinitionAsync(c.Tab.Path, c.Text, c.Position, c.Token), c.Token);
            if (result is null) Status("No symbol under the caret.");
            else if (result.IsMetadata) Status($"{result.SymbolDisplay} is defined in a referenced assembly (no source).", 8);
            else if (result.Locations.Count == 1)
            {
                var l = result.Locations[0];
                Navigate(new Location(l.Path, l.Line, l.Column, l.Length));
                Status("");
            }
            else
            {
                ShowPanel(true);
                bottom.SetResults($"Definitions of {result.SymbolDisplay} ({result.Locations.Count})", ToResults(result.Locations));
                Status("");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            logger.LogWarning(e, "Go to definition failed");
            Status("Go to definition failed: " + e.Message);
        }
    }

    async Task FindReferencesAsync()
    {
        if (NavigationContext() is not { } c) return;
        Status(session.Workspace.State == WorkspaceState.Loading ? "Finding references (projects are still loading)…" : "Finding references…");
        try
        {
            var result = await Task.Run(() => navigation.FindReferencesAsync(c.Tab.Path, c.Text, c.Position, c.Token), c.Token);
            if (result is null)
            {
                Status("No symbol under the caret.");
                return;
            }
            ShowPanel(true);
            bottom.SetResults($"References to {result.SymbolDisplay}: {result.Locations.Count}{(result.Truncated ? " (truncated)" : "")}",
                ToResults(result.Locations));
            Status("");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            logger.LogWarning(e, "Find references failed");
            Status("Find references failed: " + e.Message);
        }
    }

    static List<(Location, string)> ToResults(IEnumerable<Language.Navigation.SourceLocation> locations) =>
        locations.Select(l => (new Location(l.Path, l.Line, l.Column, l.Length), l.LineText.Trim())).ToList();

    // ---------- build / test ----------

    string? SolutionTarget => session.Root?.BuildTarget;

    /// <summary>The project of the active file, or the only project of the folder.</summary>
    string? CurrentProject =>
        session.ProjectFileFor(editors.Active?.Path)
        ?? (session.Root is { Solution: null, LooseProjects.Count: 1 } r ? r.LooseProjects[0]
            : session.Root?.Solution is { Projects.Count: 1 } s ? s.Projects[0].Path : null);

    bool BeginOperation(string name)
    {
        if (operationCts is not null)
        {
            Status($"{operationName} is still running. Stop it first (Ctrl+F2).");
            return false;
        }
        editors.SaveAll();
        operationCts = new CancellationTokenSource();
        operationName = name;
        bottom.ClearOutput();
        UpdateCommandState();
        return true;
    }

    void EndOperation()
    {
        operationCts?.Dispose();
        operationCts = null;
        operationName = null;
        UpdateCommandState();
    }

    /// <summary>Runs a build action; true when it succeeded.</summary>
    async Task<bool> RunBuildAsync(BuildAction action, string? target)
    {
        if (target is null || session.Folder is not { } folder) return false;
        var name = $"{action} {Path.GetFileName(target)}";
        if (!BeginOperation(name)) return false;
        var ct = operationCts!.Token;
        ShowPage(BottomPanel.OutputPage);
        buildLabel.SetText($"⟳ {action}…");
        buildLabel.RemoveCssClass("status-error");
        buildLabel.RemoveCssClass("status-ok");
        try
        {
            var request = new BuildRequest(action, target, Configuration);
            var result = await Task.Run(() => builds.RunAsync(request, folder, bottom.AppendOutput, ct), CancellationToken.None);
            bottom.SetBuildDiagnostics(result.Diagnostics);
            buildLabel.SetText(result.Cancelled ? $"{action} cancelled"
                : result.Succeeded ? $"✓ {action} succeeded · {result.Warnings} warnings"
                : $"✗ {action} failed · {result.Errors} errors");
            buildLabel.AddCssClass(result.Succeeded ? "status-ok" : "status-error");
            buildLabel.TooltipText = $"{name} ({Configuration}) — {result.Duration.TotalSeconds:0.0}s";
            if (result.Errors > 0) ShowPage(BottomPanel.ProblemsPage);
            if (action is BuildAction.Restore or BuildAction.Build or BuildAction.Rebuild && result.Succeeded
                && session.Workspace.State == WorkspaceState.Failed)
                _ = session.ReloadProjectsAsync();
            return result.Succeeded;
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Operation} failed", name);
            buildLabel.SetText($"✗ {action} failed");
            bottom.AppendOutput("Error: " + e.Message);
            return false;
        }
        finally { EndOperation(); }
    }

    /// <param name="filter">Test names ("Ns.Class.Method" / "Ns.Class.*"); null runs all tests of the target.</param>
    Task RunTestsAsync(string? target, IReadOnlyList<string>? filter, string? label = null) =>
        target is null ? Task.CompletedTask : RunTestGroupsAsync([(target, filter)], label);

    /// <summary>Runs one <c>dotnet test</c> per target (in turn) and shows the results of all of them together.</summary>
    async Task RunTestGroupsAsync(IReadOnlyList<(string Target, IReadOnlyList<string>? Filter)> runs, string? label = null)
    {
        if (runs.Count == 0 || session.Folder is not { } folder) return;
        if (operationCts is null) lastTestRun = () => RunTestGroupsAsync(runs, label); // Rerun Last (Shift+Alt+R)
        var what = label ?? (runs.Count > 1 ? $"{runs.Count} projects"
            : runs[0].Filter is not { } f ? Path.GetFileName(runs[0].Target) : f.Count == 1 ? f[0] : $"{f.Count} tests");
        // "Run Failed" repeats on the same target; failed tests of several projects are found from the solution.
        debugProjectForTests = runs.Count == 1 ? runs[0].Target : SolutionTarget;
        if (!BeginOperation($"Tests {what}")) return;
        var ct = operationCts!.Token;
        bottom.SetTestsRunning(what, runs);
        ShowPage(BottomPanel.TestsPage);
        buildLabel.SetText("⟳ Running tests…");
        buildLabel.RemoveCssClass("status-error");
        buildLabel.RemoveCssClass("status-ok");
        try
        {
            var results = new List<TestRunResult>();
            foreach (var (target, filter) in runs)
            {
                if (runs.Count > 1) bottom.AppendOutput($"── {Path.GetFileNameWithoutExtension(target)}");
                var one = await Task.Run(() => tests.RunAsync(target, folder, Configuration, filter, bottom.AppendOutput, ct), CancellationToken.None);
                results.Add(one);
                if (one.Cancelled) break;
            }
            var result = results.Count == 1 ? results[0] : new TestRunResult(
                results.Select(r => r.ExitCode).FirstOrDefault(e => e != 0),
                results.Any(r => r.Cancelled),
                results.SelectMany(r => r.Tests).ToList(),
                TimeSpan.FromTicks(results.Sum(r => r.Duration.Ticks)),
                results[0].Mode);
            bottom.SetTestResults(result);
            RememberTestOutcomes(result);
            buildLabel.SetText(result.Cancelled ? "Tests cancelled"
                : result.Tests.Count == 0 ? $"Tests: exit {result.ExitCode}"
                : $"{(result.Failed > 0 ? "✗" : "✓")} {result.Passed} passed · {result.Failed} failed");
            buildLabel.AddCssClass(result.Failed > 0 || (result.Tests.Count == 0 && result.ExitCode != 0) ? "status-error" : "status-ok");
            if (result.Tests.Count == 0 && !result.Cancelled) ShowPage(BottomPanel.OutputPage);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Tests failed to run");
            buildLabel.SetText("✗ Tests failed to run");
            bottom.Tests.StopRunning("Tests failed to run: " + e.Message);
            bottom.AppendOutput("Error: " + e.Message);
            ShowPage(BottomPanel.OutputPage);
        }
        finally { EndOperation(); }
    }

    CancellationTokenSource? discoveryCts;
    uint discoveryTimer;

    /// <summary>Finds the tests of every test project in the source (no build) for the test tree; debounced.</summary>
    void ScheduleTestDiscovery(int delay = 1500)
    {
        Ui.Cancel(ref discoveryTimer);
        discoveryTimer = Ui.After(delay, () =>
        {
            discoveryTimer = 0;
            _ = DiscoverTestsAsync();
        });
    }

    async Task DiscoverTestsAsync()
    {
        discoveryCts?.Cancel();
        var cts = discoveryCts = new CancellationTokenSource();
        var projects = AllProjects();
        try
        {
            var tests = await Task.Run(() => projects
                .Where(p => ProjectInfo.Read(p).IsTestProject)
                .SelectMany(p => TestDiscovery.DiscoverProject(p, cts.Token))
                .ToList(), cts.Token);
            if (!cts.IsCancellationRequested) bottom.SetDiscoveredTests(tests);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { logger.LogWarning(e, "Test discovery failed"); }
    }

    /// <summary>The last test run or debug run, for Rerun Last (Shift+Alt+R).</summary>
    Func<Task>? lastTestRun;

    /// <summary>Last outcome per test method ("Ns.Class.Method", data-driven cases folded, failed wins) for the gutter ▶.</summary>
    readonly Dictionary<string, TestOutcome> testOutcomes = new(StringComparer.Ordinal);

    TestOutcome? TestOutcomeOf(string name) => testOutcomes.TryGetValue(name, out var o) ? o : null;

    void RememberTestOutcomes(TestRunResult result)
    {
        if (result.Cancelled && result.Tests.Count == 0) return;
        foreach (var group in result.Tests.GroupBy(t => t.Key))
            testOutcomes[group.Key] = group.Any(t => t.Outcome == TestOutcome.Failed) ? TestOutcome.Failed
                : group.Any(t => t.Outcome == TestOutcome.Passed) ? TestOutcome.Passed : group.First().Outcome;
        foreach (var tab in editors.Tabs) tab.RefreshTestMarks();
    }

    /// <summary>Runs the test method (or class) enclosing the caret, found from the syntax tree.</summary>
    async Task RunTestAtCursorAsync()
    {
        if (editors.Active is not { } tab || CurrentProject is not { } project) return;
        var text = tab.GetText();
        var position = TextOffsets.CharToUtf16(text, tab.CursorOffset);
        var name = await Task.Run(() => TestDiscovery.TestNameAt(text, position));
        if (name is null)
        {
            Status("Place the caret inside a test method or test class.");
            return;
        }
        await RunTestsAsync(project, [name]);
    }

    /// <summary>The test classes of the active file (unsaved text included), or null with a status message.</summary>
    async Task<(string Project, IReadOnlyList<string> Classes)?> TestsInActiveFileAsync()
    {
        if (editors.Active is not { } tab || CurrentProject is not { } project) return null;
        var text = tab.GetText();
        var classes = await Task.Run(() => TestDiscovery.TestClassesIn(text));
        if (classes.Count > 0) return (project, classes);
        Status($"{Path.GetFileName(tab.Path)} has no test classes ([Fact], [Theory], [Test], [TestMethod], …).");
        return null;
    }

    async Task RunTestsInFileAsync()
    {
        if (await TestsInActiveFileAsync() is not var (project, classes)) return;
        await RunTestsAsync(project, classes, Path.GetFileName(editors.Active!.Path));
    }

    /// <summary>Explorer → Run Tests on a .cs file or a folder: the test classes found there, grouped by their project.</summary>
    async Task RunTestsInPathAsync(string path)
    {
        if (session.Folder is null) return;
        editors.SaveAll();
        Status("Looking for tests…");
        var found = await Task.Run(() => File.Exists(path)
            ? (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && TestDiscovery.TestClassesIn(File.ReadAllText(path)) is { Count: > 0 } c
                ? [(path, c)] : [])
            : TestDiscovery.TestClassesUnder(path));
        var runs = found
            .Select(f => (Project: session.ProjectFileFor(f.File), f.Classes))
            .Where(f => f.Project is not null)
            .GroupBy(f => f.Project!, StringComparer.Ordinal)
            .OrderBy(g => Path.GetFileNameWithoutExtension(g.Key), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, (IReadOnlyList<string>?)g.SelectMany(f => f.Classes).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();
        if (runs.Count == 0)
        {
            Status($"No test classes in {Path.GetFileName(path)}.");
            return;
        }
        await RunTestGroupsAsync(runs, Path.GetFileName(path));
    }

    async Task AnalyzeProjectAsync()
    {
        if (CurrentProject is not { } project) return;
        if (!BeginOperation("Analyze " + Path.GetFileName(project))) return;
        var ct = operationCts!.Token;
        session.EnsureWorkspaceLoaded();
        Status("Analyzing project with Roslyn…");
        try
        {
            var result = await Task.Run(() => diagnostics.GetProjectDiagnosticsAsync(project, ct), CancellationToken.None);
            bottom.SetBuildDiagnostics(result);
            ShowPage(BottomPanel.ProblemsPage);
            Status($"{Path.GetFileName(project)}: {result.Count(d => d.Severity == DiagnosticSeverity.Error)} errors, " +
                $"{result.Count(d => d.Severity == DiagnosticSeverity.Warning)} warnings (Roslyn)");
        }
        catch (OperationCanceledException) { Status("Analysis cancelled."); }
        catch (Exception e)
        {
            logger.LogWarning(e, "Project analysis failed");
            Status("Analysis failed: " + e.Message);
        }
        finally { EndOperation(); }
    }

    void RunProject() => _ = RunProjectAsync();

    /// <summary>Ctrl+F5: dotnet run of the given project (explorer) or the startup project in the terminal.</summary>
    async Task RunProjectAsync(string? target = null)
    {
        if ((target ?? await ChooseStartupProjectAsync()) is not { } project) return;
        editors.SaveAll();
        var profile = LaunchProfileFor(project);
        RunInTerminal($"dotnet run --project {ShellQuote(project)} -c {Configuration}" + (profile is null ? "" : $" --launch-profile {ShellQuote(profile.Name)}"));
    }

    void RunInTerminal(string command)
    {
        if (!bottom.Terminal.IsAvailable)
        {
            Status("The terminal needs libvte-2.91-gtk4-0.");
            return;
        }
        ShowPage(BottomPanel.TerminalPage);
        bottom.Terminal.Run(command);
    }

    // ---------- help ----------

    void ShowShortcuts()
    {
        var rows = new (string Keys, string What)[]
        {
            ("Ctrl+Shift+N", "New solution"), ("Ctrl+O", "Open folder"), ("Ctrl+N", "New file"), ("Ctrl+S / Ctrl+Alt+S", "Save / Save all"), ("Ctrl+W", "Close tab"),
            ("Ctrl+Tab, Ctrl+PgDn/PgUp", "Next / previous tab"),
            ("Ctrl+P", "Quick open file"), ("Ctrl+Shift+P, Ctrl+T", "Go to symbol"), ("Ctrl+Shift+F", "Find in files"),
            ("Ctrl+F / Ctrl+H", "Find / replace"), ("F3 / Shift+F3", "Find next / previous"), ("Ctrl+G", "Go to line"),
            ("Ctrl+/", "Toggle line comment"), ("Ctrl+Shift+V", "Markdown: split / preview / source"), ("Ctrl+Space", "Completion"),
            ("Alt+Left / Alt+Right, mouse 4/5", "Navigate back / forward"),
            ("Alt+Enter, Ctrl+.", "Quick fixes and refactorings"), ("Ctrl+I", "Implement missing interface / abstract members"), ("F2, Shift+F6", "Rename symbol"), ("Ctrl+Shift+I", "Format document (CSharpier or Roslyn)"), ("Ctrl+Alt+N", "NuGet packages"),
            ("F12, Ctrl+Click", "Go to definition"), ("Ctrl+Alt+B", "Go to implementation"),
            ("Ctrl+Enter", ".http: send the request at the caret"), ("Ctrl+E", "Recent files"), ("Ctrl+F12", "File structure"), ("Shift+F12, Alt+F7", "Find all references"), ("F8", "Next problem"),
            ("Ctrl+Shift+E", "Reveal file in explorer"), ("F2 / Delete", "Rename / delete in explorer"),
            ("Ctrl+Shift+B", "Build solution"), ("F6", "Build current project"), ("Ctrl+F5", "Run startup project in terminal"),
            ("F5 / Shift+F5", "Debug: start or continue / stop"), ("F9", "Toggle breakpoint (or click the gutter)"),
            ("F10 / F11 / Shift+F11", "Step over / into / out"),
            ("Ctrl+Shift+T", "Run all tests"), ("Ctrl+Shift+R", "Run test at cursor"), ("Ctrl+Shift+G", "Go to tests / tested code"), ("Ctrl+Alt+R", "Run tests in current file"), ("Ctrl+Shift+D", "Debug test at cursor"), ("Ctrl+Shift+F9", "Remove all breakpoints"), ("Shift+Alt+R", "Rerun last tests"), ("Ctrl+Alt+H", "Inlay hints on / off"), ("Ctrl+Shift+F8", "Edit breakpoint (condition, hit count, log)"), ("Ctrl+F2", "Stop build / tests"),
            ("Ctrl+B / Ctrl+J", "Toggle explorer / bottom panel"), ("Ctrl+`", "Terminal"), ("Ctrl+Shift+M", "Problems"),
            ("Ctrl+Shift+U", "Output"), ("Ctrl+,", "Settings"), ("Ctrl+Q", "Quit"),
        };
        var grid = Gtk.Grid.New();
        grid.RowSpacing = 4;
        grid.ColumnSpacing = 24;
        grid.MarginTop = grid.MarginBottom = grid.MarginStart = grid.MarginEnd = 16;
        for (var i = 0; i < rows.Length; i++)
        {
            grid.Attach(Ui.Label(rows[i].Keys, "shortcut-keys"), 0, i, 1, 1);
            grid.Attach(Ui.Label(rows[i].What), 1, i, 1, 1);
        }
        var dialog = Gtk.Window.New();
        dialog.Title = "Keyboard Shortcuts";
        dialog.SetTransientFor(window);
        dialog.Modal = true;
        dialog.SetDefaultSize(560, 640);
        dialog.SetChild(Ui.Scrolled(grid));
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != Gdk.Constants.KEY_Escape) return false;
            dialog.Close();
            return true;
        };
        dialog.AddController(keys);
        dialog.Present();
    }

    async Task ShowAboutAsync()
    {
        var sdks = await SdkInfo.ListSdksAsync();
        await Ui.ChooseAsync(window, "Lightweight IDE", $"Version {AppVersion.Version}{(AppVersion.Commit is { } commit ? $" (commit {commit})" : "")}\nRuntime .NET {Environment.Version}\n" +
            $"GTK {Gtk.Functions.GetMajorVersion()}.{Gtk.Functions.GetMinorVersion()}.{Gtk.Functions.GetMicroVersion()}\n\n" +
            $"Installed SDKs:\n{string.Join("\n", sdks)}\n\nSettings: {AppPaths.UserSettingsFile}\nLogs: {AppPaths.LogDirectory}", ["Close"], 0);
    }
}
