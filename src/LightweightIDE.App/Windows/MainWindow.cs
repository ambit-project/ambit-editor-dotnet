using System.Diagnostics;
using System.Reflection;
using LightweightIDE.App.Application;
using LightweightIDE.App.Views;
using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.Http;
using LightweightIDE.DotNet.Packages;
using LightweightIDE.DotNet.Scaffolding;
using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.DotNet.Testing;
using LightweightIDE.Editor;
using LightweightIDE.Language.Completion;
using LightweightIDE.Language.Diagnostics;
using LightweightIDE.Language.Formatting;
using LightweightIDE.Language.Navigation;
using LightweightIDE.Language.Refactoring;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Windows;

/// <summary>Creates the main window with its services (registered in DI).</summary>
public sealed class MainWindowFactory(
    ILoggerFactory loggerFactory,
    UiOptions uiOptions,
    EditorOptions editorOptions,
    LanguageOptions languageOptions,
    AppState state,
    SolutionSession session,
    BuildService builds,
    TestRunner tests,
    WorkspaceService workspace,
    DiagnosticsService diagnostics,
    NavigationService navigation,
    CompletionProvider completion,
    SignatureHelpProvider signatureHelp,
    RenameService rename,
    FormattingService formatting,
    FormattingOptions formattingOptions,
    PackageService packages,
    SolutionScaffolder scaffolder,
    CodeActionService codeActions,
    HttpClientService http,
    LightweightIDE.Language.Analysis.CodeModel codeModel,
    LightweightIDE.Language.Classification.SemanticHighlighter semantic,
    LightweightIDE.Language.Classification.SymbolUsages usages,
    LightweightIDE.Language.Classification.UnnecessaryCode unnecessary,
    LightweightIDE.Language.Classification.InlayHintService inlayHints)
{
    public MainWindow Create(Gtk.Application app)
    {
        var editorServices = new EditorServices(editorOptions, languageOptions, workspace, diagnostics, navigation, completion,
            loggerFactory.CreateLogger<EditorTab>(), packages, signatureHelp, semantic, usages, unnecessary, inlayHints);
        return new MainWindow(app, loggerFactory.CreateLogger<MainWindow>(), uiOptions, editorOptions, languageOptions, state, session,
            builds, tests, diagnostics, navigation, editorServices, rename, formatting, formattingOptions, packages, scaffolder, codeActions, http, codeModel);
    }
}

/// <summary>
/// The IDE window: menu bar, toolbar, explorer | editor, bottom panel and status bar. It wires the views to the
/// services and owns long-running commands (build, test), which run off the UI thread and report back with
/// <see cref="Ui.Post"/>.
/// </summary>
public sealed partial class MainWindow
{
    readonly Gtk.Application app;
    readonly ILogger logger;
    readonly UiOptions uiOptions;
    readonly EditorOptions editorOptions;
    readonly LanguageOptions languageOptions;
    readonly AppState state;
    readonly SolutionSession session;
    readonly BuildService builds;
    readonly TestRunner tests;
    readonly DiagnosticsService diagnostics;
    readonly NavigationService navigation;
    readonly RenameService rename;
    readonly FormattingService formatting;
    readonly FormattingOptions formattingOptions;
    readonly PackageService packages;
    readonly SolutionScaffolder scaffolder;
    readonly CodeActionService codeActions;
    readonly HttpClientService http;

    readonly Gtk.ApplicationWindow window;
    readonly ExplorerView explorer = new();
    readonly EditorArea editors;
    readonly BottomPanel bottom = new();
    readonly SearchPalette palette;
    readonly Gtk.Paned horizontal = Gtk.Paned.New(Gtk.Orientation.Horizontal);
    readonly Gtk.Paned vertical = Gtk.Paned.New(Gtk.Orientation.Vertical);
    readonly Gtk.DropDown configuration = Gtk.DropDown.NewFromStrings(["Debug", "Release"]);
    readonly Gtk.Spinner busySpinner = Gtk.Spinner.New();
    readonly Gtk.MenuButton startupButton = Gtk.MenuButton.New();
    readonly Gtk.Image startupIcon = Gtk.Image.New();
    readonly Gtk.Label startupLabel = Ui.Label("");
    readonly Gio.Menu recentMenu = Gio.Menu.New();
    readonly Gtk.CssProvider editorCss = Gtk.CssProvider.New();

    // status bar
    readonly Gtk.Label branchLabel = Ui.Label("", "status-item");
    readonly Gtk.Label solutionLabel = Ui.Label("No folder", "status-item");
    readonly Gtk.Label sdkLabel = Ui.Label("", "status-item");
    readonly Gtk.Label workspaceLabel = Ui.Label("", "status-item");
    readonly Gtk.Label indexLabel = Ui.Label("", "status-item");
    readonly Gtk.Label buildLabel = Ui.Label("", "status-item");
    readonly Gtk.Label debugLabel = Ui.Label("", "status-debug");
    readonly Gtk.Label messageLabel = Ui.Label("", "status-message");
    readonly Gtk.Label cursorLabel = Ui.Label("", "status-item");
    readonly Gtk.Label fileInfoLabel = Ui.Label("", "status-item");
    readonly Gtk.Label memoryLabel = Ui.Label("", "status-item");
    readonly Gtk.Label versionLabel = Ui.Label("v" + AppVersion.Version, "status-item");

    CancellationTokenSource? operationCts;
    CancellationTokenSource? navigationCts;
    string? operationName;
    bool closeConfirmed;
    int lastExplorerWidth;
    readonly LightweightIDE.Language.Analysis.CodeModel codeModel;
    int lastPanelPosition;
    uint messageTimer;

    internal MainWindow(Gtk.Application app, ILogger logger, UiOptions uiOptions, EditorOptions editorOptions, LanguageOptions languageOptions,
        AppState state, SolutionSession session, BuildService builds, TestRunner tests, DiagnosticsService diagnostics,
        NavigationService navigation, EditorServices editorServices, RenameService rename, FormattingService formatting,
        FormattingOptions formattingOptions, PackageService packages, SolutionScaffolder scaffolder, CodeActionService codeActions,
        HttpClientService http, LightweightIDE.Language.Analysis.CodeModel codeModel)
    {
        this.codeModel = codeModel;
        this.http = http;
        this.codeActions = codeActions;
        this.scaffolder = scaffolder;
        this.rename = rename;
        this.formatting = formatting;
        this.formattingOptions = formattingOptions;
        this.packages = packages;
        this.app = app;
        this.logger = logger;
        this.uiOptions = uiOptions;
        this.editorOptions = editorOptions;
        this.languageOptions = languageOptions;
        this.state = state;
        this.session = session;
        this.builds = builds;
        this.tests = tests;
        this.diagnostics = diagnostics;
        this.navigation = navigation;

        window = Gtk.ApplicationWindow.New(app);
        window.Title = $"Lightweight IDE {AppVersion.Version}";
        window.SetIconName("text-x-csharp");
        window.SetDefaultSize(state.WindowWidth, state.WindowHeight);
        if (state.Maximized) window.Maximize();

        editors = new EditorArea(editorServices, () => window);
        palette = new SearchPalette(window, () => session.Search);

        LoadStyles();
        ApplyTheme(state.ThemeOverride ?? uiOptions.Theme);
        CreateActions();

        var root = Ui.VBox();
        root.Append(Gtk.PopoverMenuBar.NewFromModel(CreateMenu()));
        root.Append(CreateToolbar());

        horizontal.SetStartChild(explorer.Widget);
        horizontal.SetEndChild(editors.Widget);
        horizontal.ResizeStartChild = false;
        horizontal.ShrinkStartChild = false;
        horizontal.Position = state.ExplorerWidth;
        explorer.Widget.SetSizeRequest(160, -1);

        vertical.SetStartChild(horizontal);
        vertical.SetEndChild(bottom.Widget);
        vertical.ResizeEndChild = false;
        vertical.ShrinkEndChild = false;
        vertical.Vexpand = true;
        bottom.Widget.SetSizeRequest(-1, 80);
        root.Append(vertical);
        root.Append(CreateStatusBar());
        window.SetChild(root);

        WireEvents();
        if (state.InlayHints is { } inlay) editorOptions.InlayHints = inlay; // View → Inlay Hints wins over the setting
        InitDebugging();
        InitRecovery();
        InitNavigation();
        InitHttp();
        UpdateCommandState();
        UpdateFileStatus();
    }

    // ---------- startup / shutdown ----------

    public void Present(string? startPath)
    {
        window.Present();
        // The split position can be set only once the paned has its final height (after the first allocation).
        var attempts = 0;
        Ui.Every(30, () =>
        {
            var height = vertical.GetHeight();
            if (height <= 0 && ++attempts < 100) return true;
            if (height > 0) vertical.Position = Math.Max(120, height - state.BottomPanelHeight);
            return false;
        });

        if (startPath is null) return;
        var full = Path.GetFullPath(startPath);
        if (Directory.Exists(full)) _ = OpenFolderAsync(full);
        else if (File.Exists(full) && (full.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            _ = OpenFolderAsync(Path.GetDirectoryName(full)!, full);
        else if (File.Exists(full))
        {
            _ = OpenFolderAsync(FolderForFile(full)).ContinueWith(_ => Ui.Post(() => editors.Open(full)), TaskScheduler.Default);
        }
    }

    /// <summary>Folder to open for a single file: nearest folder with a solution, else the git root, else the project folder.</summary>
    static string FolderForFile(string file)
    {
        for (var dir = Path.GetDirectoryName(file); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            try
            {
                if (Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx")).Any()) return dir;
                if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var project = new ProjectLocator(Path.GetDirectoryName(file)!).FindProjectFile(file);
        return Path.GetDirectoryName(project ?? file)!;
    }

    bool OnCloseRequest()
    {
        if (closeConfirmed) return false;
        _ = ConfirmCloseAsync();
        return true;
    }

    async Task ConfirmCloseAsync()
    {
        if (!await editors.CloseAllAsync()) return;
        if (debug is { } running) await running.StopAsync();
        DeleteSessionBackups();
        SaveState();
        operationCts?.Cancel();
        session.Close();
        closeConfirmed = true;
        window.Close();
    }

    void SaveState()
    {
        state.Maximized = window.IsMaximized();
        if (!state.Maximized && window.GetWidth() > 0)
        {
            state.WindowWidth = window.GetWidth();
            state.WindowHeight = window.GetHeight();
        }
        if (explorer.Widget.Visible) state.ExplorerWidth = horizontal.Position;
        if (bottom.Widget.Visible && vertical.GetHeight() > 0) state.BottomPanelHeight = Math.Max(80, vertical.GetHeight() - vertical.Position);
        state.BuildConfiguration = Configuration;
        if (session.Folder is { } folder) state.OpenFiles[folder] = editors.Tabs.Select(t => t.Path).ToList();
        state.Save();
    }

    string Configuration => configuration.Selected == 1 ? "Release" : "Debug";

    // ---------- layout ----------

    Gtk.Widget CreateToolbar()
    {
        var bar = Ui.HBox(2);
        bar.AddCssClass("toolbar");
        bar.Append(Ui.IconButton("folder-open-symbolic", "Open Folder (Ctrl+O)", "win.open-folder"));
        bar.Append(Ui.IconButton("document-save-symbolic", "Save (Ctrl+S)", "win.save"));
        bar.Append(Ui.IconButton("document-save-as-symbolic", "Save All (Ctrl+Alt+S)", "win.save-all"));
        bar.Append(Ui.IconButton("format-justify-left-symbolic", "Format File (Ctrl+Shift+I)", "win.format-document"));
        bar.Append(Separator());
        bar.Append(Ui.IconButton("go-previous-symbolic", "Back (Alt+Left)", "win.navigate-back"));
        bar.Append(Ui.IconButton("go-next-symbolic", "Forward (Alt+Right)", "win.navigate-forward"));
        bar.Append(Separator());
        bar.Append(Ui.TextButton("Build", "Build Solution (Ctrl+Shift+B)", "win.build-solution"));
        bar.Append(Ui.TextButton("Rebuild", "Rebuild Solution", "win.rebuild-solution"));
        configuration.TooltipText = "Build configuration";
        configuration.AddCssClass("flat");
        configuration.Selected = state.BuildConfiguration == "Release" ? 1u : 0u;
        bar.Append(configuration);
        bar.Append(Ui.TextButton("Run Tests", "Run All Tests (Ctrl+Shift+T)", "win.test-solution"));
        var startupContent = Ui.HBox(4);
        startupContent.Append(startupIcon);
        startupLabel.Ellipsize = Pango.EllipsizeMode.End;
        startupLabel.MaxWidthChars = 24;
        startupContent.Append(startupLabel);
        startupContent.Append(Gtk.Image.NewFromIconName("pan-down-symbolic"));
        startupButton.SetChild(startupContent);
        startupButton.AddCssClass("flat");
        startupButton.Visible = false;
        bar.Append(startupButton);
        bar.Append(Ui.TextButton("Run", "Run Startup Project in Terminal (Ctrl+F5)", "win.run-project"));
        var stop = Ui.IconButton("media-playback-stop-symbolic", "Stop (Ctrl+F2)", "win.stop");
        stop.AddCssClass("stop-button");
        bar.Append(stop);
        bar.Append(busySpinner);
        bar.Append(Separator());

        var search = Gtk.Button.New();
        var content = Ui.HBox(6);
        content.Append(Gtk.Image.NewFromIconName("system-search-symbolic"));
        content.Append(Ui.Label("Search files, symbols, text…  Ctrl+P", "dim-label"));
        search.SetChild(content);
        search.AddCssClass("search-button");
        search.ActionName = "win.quick-open";
        search.TooltipText = "Quick Open (Ctrl+P) · Go to Symbol (Ctrl+Shift+P) · Find in Files (Ctrl+Shift+F)";
        search.Hexpand = false;
        bar.Append(search);
        return bar;
    }

    static Gtk.Widget Separator()
    {
        var s = Gtk.Separator.New(Gtk.Orientation.Vertical);
        s.MarginStart = s.MarginEnd = 4;
        return s;
    }

    Gtk.Widget CreateStatusBar()
    {
        var bar = Ui.HBox(0);
        bar.AddCssClass("statusbar");
        foreach (var l in new[] { branchLabel, solutionLabel, sdkLabel, workspaceLabel, indexLabel, buildLabel, debugLabel }) bar.Append(l);
        messageLabel.Hexpand = true;
        messageLabel.Ellipsize = Pango.EllipsizeMode.End;
        bar.Append(messageLabel);
        foreach (var l in new[] { cursorLabel, fileInfoLabel, memoryLabel, versionLabel }) bar.Append(l);
        versionLabel.AddCssClass("dim-label");
        versionLabel.TooltipText = $"Lightweight IDE {AppVersion.Display} — Help → About";
        solutionLabel.Ellipsize = Pango.EllipsizeMode.Middle;
        solutionLabel.MaxWidthChars = 40;
        workspaceLabel.TooltipText = "Roslyn workspace (code intelligence)";
        memoryLabel.TooltipText = "IDE process memory (working set / managed heap)";
        memoryLabel.Visible = uiOptions.ShowMemoryUsage;

        UpdateMemory();
        Ui.Every(2000, () =>
        {
            UpdateMemory();
            return true;
        });
        StartUiWatchdog();
        return bar;
    }

    /// <summary>
    /// Logs when the GTK main loop was blocked: a 100 ms timer that fires much later means the UI thread was busy
    /// (long work that belongs on a background thread). Cheap enough to stay on in release builds.
    /// </summary>
    long uiHeartbeat = Environment.TickCount64;

    /// <summary>
    /// A frozen UI says nothing about why. When the main loop has not ticked for 2 s, a background thread runs
    /// dotnet-stack (when installed, ~/.dotnet/tools) on this process and saves the stacks next to the logs
    /// (ui-freeze-HHmmss.txt), at most once a minute — the main thread's stack shows what blocked it.
    /// </summary>
    void StartFreezeCapture()
    {
        var tool = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-stack") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(d => Path.Combine(d, "dotnet-stack")))
            .FirstOrDefault(File.Exists);
        if (tool is null) return;
        var thread = new Thread(() =>
        {
            long lastCapture = 0;
            while (true)
            {
                Thread.Sleep(500);
                var now = Environment.TickCount64;
                if (now - Volatile.Read(ref uiHeartbeat) < 2000 || now - lastCapture < 60_000) continue;
                lastCapture = now;
                try
                {
                    var file = Path.Combine(AppPaths.LogDirectory, $"ui-freeze-{DateTime.Now:HHmmss}.txt");
                    var info = new ProcessStartInfo(tool, ["report", "-p", Environment.ProcessId.ToString()])
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    using var process = Process.Start(info)!;
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(30_000);
                    File.WriteAllText(file, output);
                    logger.LogWarning("UI thread frozen: stacks saved to {File}", file);
                }
                catch (Exception e) { logger.LogDebug(e, "Freeze capture failed"); }
            }
        }) { IsBackground = true, Name = "ui-freeze-capture" };
        thread.Start();
    }

    void StartUiWatchdog()
    {
        StartFreezeCapture();
        var clock = Stopwatch.StartNew();
        var last = clock.ElapsedMilliseconds;
        Ui.Every(100, () =>
        {
            var now = clock.ElapsedMilliseconds;
            var late = now - last - 100;
            last = now;
            Volatile.Write(ref uiHeartbeat, Environment.TickCount64);
            if (late > 250) logger.LogWarning("UI thread blocked for {Milliseconds} ms", late);
            return true;
        });
    }

    void UpdateMemory()
    {
        if (!uiOptions.ShowMemoryUsage) return;
        using var process = Process.GetCurrentProcess();
        memoryLabel.SetText($"{process.WorkingSet64 / (1024 * 1024)} MB · heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
    }

    // ---------- styles / theme ----------

    void LoadStyles()
    {
        var display = Gdk.Display.GetDefault()!;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.css"))
        {
            if (stream is not null)
            {
                var css = Gtk.CssProvider.New();
                css.LoadFromString(new StreamReader(stream).ReadToEnd());
                Gtk.StyleContext.AddProviderForDisplay(display, css, 600);
            }
        }
        var family = editorOptions.FontFamily.Replace("\"", "");
        editorCss.LoadFromString($"textview.editor, .terminal, .monospace {{ font-family: \"{family}\"; font-size: {editorOptions.FontSize}pt; }}");
        Gtk.StyleContext.AddProviderForDisplay(display, editorCss, 610);
    }

    bool? systemPrefersDark;

    void ApplyTheme(string theme)
    {
        var settings = Gtk.Settings.GetDefault();
        if (settings is null) return;
        systemPrefersDark ??= settings.GtkApplicationPreferDarkTheme || SystemColorSchemeIsDark();
        var mode = new UiOptions { Theme = theme }.ThemeMode;
        settings.GtkApplicationPreferDarkTheme = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => systemPrefersDark.Value,
        };
        var dark = Ui.IsDarkTheme() || settings.GtkApplicationPreferDarkTheme;
        if (dark) window.AddCssClass("dark");
        else window.RemoveCssClass("dark");
        var manager = ColorSchemes.Manager;
        var scheme = (state.ColorScheme is { } chosen ? manager.GetScheme(chosen) : null)
                     ?? manager.GetScheme(dark ? ColorSchemes.Dark : ColorSchemes.Light)
                     ?? manager.GetScheme(dark ? "Adwaita-dark" : "Adwaita");
        editors.SetStyleScheme(scheme);
        bottom.Http.SetStyleScheme(scheme);
        bottom.Terminal.ApplyColors();
    }

    /// <summary>GNOME/Cinnamon store the "prefer dark" choice in GSettings; GTK without libadwaita does not read it.</summary>
    static bool SystemColorSchemeIsDark()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gsettings", ["get", "org.gnome.desktop.interface", "color-scheme"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1000);
            return output.Contains("dark", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    // ---------- events ----------

    void WireEvents()
    {
        window.OnCloseRequest += (_, _) => OnCloseRequest();

        explorer.FileActivated += path => editors.Open(path);
        explorer.ActionRequested += (action, path) => _ = OnExplorerActionAsync(action, path);
        editors.FormatOnTypeRequested += tab => _ = FormatOnTypeAsync(tab);
        editors.NavigateRequested += (_, path, line, column) => Navigate(new Location(path, line, column));
        editors.ImplementationsRequested += (tab, offset) =>
        {
            tab.PlaceCursor(offset); // Go to Implementation works on the symbol at the caret
            _ = GoToImplementationAsync();
        };
        explorer.AddExistingProjectRequested += folder => _ = AddExistingProjectAsync(folder);
        explorer.TestProjectsRequested += (folder, projects) =>
        {
            if (projects.Count == 0) Status($"No test projects in {folder}.");
            else _ = RunTestGroupsAsync(projects.Select(p => (p, (IReadOnlyList<string>?)null)).ToList(), folder);
        };
        bottom.Navigate += Navigate;
        palette.Navigate += Navigate;
        palette.ShowInPanel += (title, results) =>
        {
            ShowPanel(true);
            bottom.SetResults(title, results);
        };

        editors.ActiveTabChanged += tab =>
        {
            bottom.SetCurrentFile(tab?.Path);
            UpdateFileStatus();
            UpdateCommandState();
            UpdateTitle();
        };
        editors.TabCursorMoved += tab =>
        {
            if (tab == editors.Active) UpdateCursor(tab);
        };
        editors.TabDiagnosticsUpdated += tab => bottom.SetLiveDiagnostics(tab.Path, tab.LiveDiagnostics.Select(d => d.Diagnostic).ToList());
        editors.TabSaved += tab =>
        {
            if (tab == editors.Active) UpdateFileStatus();
            if (Path.GetExtension(tab.Path) is ".csproj" or ".props" or ".targets" or ".slnx" or ".sln")
                Status("Project file saved — reloading projects…");
            if (Path.GetExtension(tab.Path) is ".csproj" or ".props" or ".targets") _ = session.ReloadProjectsAsync();
        };
        editors.TabClosed += tab =>
        {
            bottom.ClearLive(tab.Path);
            session.Workspace.CloseDocument(tab.Path);
        };
        editors.GoToDefinitionRequested += tab => _ = GoToDefinitionAsync();
        editors.RenameRequested += tab => _ = RenameSymbolAsync();
        editors.OpenFileRequested += path => Navigate(new Location(path, 0, 1));

        // Mouse back / forward buttons, anywhere in the window.
        var mouse = Gtk.GestureClick.New();
        mouse.Button = 0;
        mouse.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        mouse.OnPressed += (gesture, _) =>
        {
            switch (gesture.GetCurrentButton())
            {
                case 8: MoveInHistory(back: true); break;
                case 9: MoveInHistory(back: false); break;
            }
        };
        window.AddController(mouse);

        session.IndexProgress += p => Ui.Post(() => indexLabel.SetText(p.Total > 0 && p.Done < p.Total
            ? $"Indexing {p.Phase} {p.Done}/{p.Total}"
            : p.Phase == "done" ? "" : $"Indexing {p.Phase}…"));
        session.IndexUpdated += () => Ui.Post(() => indexLabel.SetText(""));
        session.PathsChanged += paths => Ui.Post(() =>
        {
            explorer.RefreshPaths(paths);
            // New, changed or deleted test files: find the tests again (debounced).
            if (paths.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                ScheduleTestDiscovery();
        });
        session.Workspace.StateChanged += () => Ui.Post(() => UpdateWorkspaceStatus());
        codeModel.Changed += () => Ui.Post(() => UpdateWorkspaceStatus(refreshEditors: false));

        configuration.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() == "selected") state.BuildConfiguration = Configuration;
        };
    }

    void UpdateWorkspaceStatus(bool refreshEditors = true)
    {
        var ws = session.Workspace;
        workspaceLabel.SetText(ws.State switch
        {
            WorkspaceState.Loading => "⟳ Loading projects…",
            WorkspaceState.Ready when !codeModel.IsReady && codeModel.Progress.Total > 0 => $"⟳ Analysing {codeModel.Progress.Done}/{codeModel.Progress.Total} projects",
            WorkspaceState.Ready => (ws.LoadErrors.Count > 0 ? $"Roslyn ✓ ({ws.LoadErrors.Count} warnings)" : "Roslyn ✓")
                                    + (codeModel.IsReady ? $" · {codeModel.TypeCount} types" : ""),
            WorkspaceState.Failed => "Roslyn ✗",
            _ => "",
        });
        workspaceLabel.TooltipText = ws.StatusMessage is { } m
            ? m + (ws.LoadErrors.Count > 0 ? "\n\n" + string.Join("\n", ws.LoadErrors.Take(15)) : "")
            : "Roslyn workspace (code intelligence)";
        if (codeModel.IsReady)
            workspaceLabel.TooltipText += $"\nSolution analysis: {codeModel.TypeCount} types indexed in {codeModel.Duration.TotalSeconds:0.0} s (implementations, smart completion)";
        if (refreshEditors && ws.State == WorkspaceState.Ready) editors.RefreshAnalysis();
        UpdateCommandState();
    }

    void UpdateTitle()
    {
        var folder = session.Folder is { } f ? Path.GetFileName(f.TrimEnd('/')) : null;
        var file = editors.Active?.Document.FileName;
        window.Title = string.Join(" — ", new[] { file, folder, $"Lightweight IDE {AppVersion.Version}" }.Where(s => s is not null));
    }

    void UpdateCursor(EditorTab tab)
    {
        var (line, column) = tab.CursorLineColumn();
        cursorLabel.SetText($"Ln {line}, Col {column}");
    }

    void UpdateFileStatus()
    {
        if (editors.Active is not { } tab)
        {
            cursorLabel.SetText("");
            fileInfoLabel.SetText("");
            return;
        }
        UpdateCursor(tab);
        var ending = tab.Document.LineEnding == LightweightIDE.Editor.FileOperations.LineEnding.CrLf ? "CRLF" : "LF";
        fileInfoLabel.SetText($"{tab.Document.EncodingName} · {ending} · {(editorOptions.InsertSpaces ? "Spaces" : "Tabs")}: {editorOptions.TabSize}");
    }

    /// <summary>Shows a transient message in the status bar.</summary>
    void Status(string message, int seconds = 6)
    {
        messageLabel.SetText(message);
        messageLabel.TooltipText = message;
        Ui.Cancel(ref messageTimer);
        messageTimer = Ui.After(seconds * 1000, () =>
        {
            messageTimer = 0;
            messageLabel.SetText("");
        });
    }

    /// <summary>Opens a location and records the jump in the navigation history.</summary>
    void Navigate(Location l)
    {
        var from = editors.Active?.Position();
        if (editors.Open(l.Path, l.Line, l.Column, l.Length) is not { } tab)
        {
            Status($"Cannot open {l.Path}");
            return;
        }
        if (from is not null) history.Record(from, tab.Position());
        UpdateHistoryState();
    }

    void ShowPanel(bool visible)
    {
        if (bottom.Widget.Visible == visible) return;
        if (!visible) lastPanelPosition = vertical.Position;
        bottom.Widget.Visible = visible;
        if (visible && lastPanelPosition > 0) vertical.Position = lastPanelPosition;
    }

    void ShowExplorer(bool visible)
    {
        if (explorer.Widget.Visible == visible) return;
        if (!visible) lastExplorerWidth = horizontal.Position;
        explorer.Widget.Visible = visible;
        if (visible && lastExplorerWidth > 0) horizontal.Position = lastExplorerWidth;
    }

    void ShowPage(int page)
    {
        ShowPanel(true);
        bottom.Show(page);
    }
}
