using System.Text;
using LightweightIDE.App.Views;
using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.Debugging;
using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.DotNet.Testing;
using LightweightIDE.Editor;
using LightweightIDE.Language.Testing;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Windows;

/// <summary>Debugging with netcoredbg: breakpoints, start / stop, stepping, call stack, variables, hover values.</summary>
public sealed partial class MainWindow
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[\w.`+<>\[\]]+: (Trace|Debug|Information|Warning|Error|Critical): ")]
    private static partial System.Text.RegularExpressions.Regex DebugLoggerLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"Now listening on: (https?://\S+)")]
    private static partial System.Text.RegularExpressions.Regex ListeningLine();

    bool browserOpened = true;
    LaunchProfile? launchProfile;

    readonly BreakpointStore breakpoints = new();
    string? debugProjectForTests;
    readonly Dictionary<string, HashSet<int>> unverifiedBreakpoints = new(StringComparer.Ordinal);
    readonly StringBuilder programOutput = new();
    DebugSession? debug;
    string? debugProject;
    bool paused;
    int stoppedThread;
    DebugFrame? currentFrame;
    EditorTab? currentLineTab;

    void CreateDebugActions()
    {
        Add("debug-start", () => _ = StartOrContinueAsync(), "F5");
        Add("debug-stop", () => _ = StopDebuggingAsync(), "<Shift>F5");
        Add("debug-pause", () => _ = PauseAsync());
        Add("debug-step-over", () => _ = StepAsync(s => s.NextAsync(stoppedThread)), "F10");
        Add("debug-step-into", () => _ = StepAsync(s => s.StepInAsync(stoppedThread)), "F11");
        Add("debug-step-out", () => _ = StepAsync(s => s.StepOutAsync(stoppedThread)), "<Shift>F11");
        Add("toggle-breakpoint", () => { if (editors.Active is { } t) ToggleBreakpoint(t, t.CursorLineColumn().Line); }, "F9");
        Add("remove-breakpoints", RemoveAllBreakpoints, "<Control><Shift>F9");
        Add("edit-breakpoint", () => { if (editors.Active is { } t) EditBreakpoint(t, t.CursorLineColumn().Line); }, "<Control><Shift>F8");
        Add("remove-file-breakpoints", () =>
        {
            if (editors.Active is not { } tab) return;
            var count = breakpoints.ClearFile(tab.Path);
            Status(count == 0 ? $"{Path.GetFileName(tab.Path)} has no breakpoints." : $"Removed {count} breakpoint(s) in {Path.GetFileName(tab.Path)}.");
        });
        Add("startup-project", () => _ = ChooseStartupProjectAsync(force: true));
        Add("launch-profile", () => _ = ChooseLaunchProfileAsync());
        AddStartupMenuActions();
        Add("test-debug-at-cursor", () => _ = DebugTestAtCursorAsync(), "<Control><Shift>d");
        Add("test-debug-file", () => _ = DebugTestsInFileAsync());
        Add("test-run-failed", () => _ = RunFailedTestsAsync());
    }

    void RemoveAllBreakpoints()
    {
        var count = breakpoints.Count;
        var files = breakpoints.All().Count;
        breakpoints.Clear();
        Status(count == 0 ? "There are no breakpoints." : $"Removed {count} breakpoint(s) in {files} file(s).");
    }

    Gio.Menu CreateDebugMenu()
    {
        var menu = Gio.Menu.New();
        menu.AppendSection(null, Section(("Start / Continue", "debug-start"), ("Pause", "debug-pause"), ("Stop", "debug-stop")));
        menu.AppendSection(null, Section(("Step Over", "debug-step-over"), ("Step Into", "debug-step-into"), ("Step Out", "debug-step-out")));
        menu.AppendSection(null, Section(("Toggle Breakpoint", "toggle-breakpoint"), ("Edit Breakpoint (Condition, Hit Count, Log)…", "edit-breakpoint"),
            ("Remove Breakpoints in Current File", "remove-file-breakpoints"), ("Remove All Breakpoints", "remove-breakpoints")));
        menu.AppendSection(null, Section(("Set Startup Project…", "startup-project"), ("Launch Profile…", "launch-profile")));
        return menu;
    }

    void InitDebugging()
    {
        breakpoints.Import(state.Breakpoints, state.BreakpointOptions);
        breakpoints.Changed += path =>
        {
            state.Breakpoints = breakpoints.Export();
            state.BreakpointOptions = breakpoints.ExportOptions();
            ShowBreakpoints(path);
            _ = SendBreakpointsAsync(path);
        };
        editors.TabOpened += tab =>
        {
            tab.TestOutcomeLookup = TestOutcomeOf;
            tab.SetBreakpoints(breakpoints.For(tab.Path), unverifiedBreakpoints.GetValueOrDefault(tab.Path), l => breakpoints.OptionsFor(tab.Path, l));
            if (paused) tab.DebugEvaluator = EvaluateForHoverAsync;
        };
        editors.BreakpointToggleRequested += ToggleBreakpoint;
        editors.BreakpointEditRequested += EditBreakpoint;
        editors.TestRunRequested += (tab, name, debugIt) =>
        {
            if (session.ProjectFileFor(tab.Path) is not { } project) return;
            if (!debugIt) _ = RunTestsAsync(project, [name]);
            else if (debug is not null) Status("A debugging session is running. Stop it first (Shift+F5).");
            else _ = DebugTestsAsync(project, [name], name);
        };
        // Marks move with edits; the store follows them when the file is saved.
        editors.TabSaved += tab => breakpoints.Set(tab.Path, tab.BreakpointLines());

        bottom.Debug.FrameSelected += frame => _ = SelectFrameAsync(frame);
        bottom.Tests.FindTest += FindTest;
        bottom.Tests.RunRequested += (project, filter, debugIt) =>
        {
            if (!debugIt) _ = RunTestsAsync(project, filter);
            else if (debug is not null) Status("A debugging session is running. Stop it first (Shift+F5).");
            else if (filter is null) Status("Debug runs a class or a test; select one in the tree.");
            else _ = DebugTestsAsync(project, filter, filter[0]);
        };
        bottom.Debug.LoadChildren = reference => debug?.VariablesAsync(reference) ?? Task.FromResult<IReadOnlyList<DebugVariable>>([]);
        bottom.Debug.Evaluate = expression => debug is { } s && currentFrame is { } f
            ? s.EvaluateAsync(expression, f.Id)
            : Task.FromResult(new DebugVariable(expression, "(not paused)", null, 0));
        // GTK binds F10 to the menu bar; while paused it is Step Over (capture phase runs before that binding).
        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        keys.OnKeyPressed += (controller, e) =>
        {
            const Gdk.ModifierType modifiers = Gdk.ModifierType.ControlMask | Gdk.ModifierType.ShiftMask | Gdk.ModifierType.AltMask;
            if (e.Keyval != Gdk.Constants.KEY_F10 || (e.State & modifiers) != 0 || debug is null || !paused) return false;
            _ = StepAsync(s => s.NextAsync(stoppedThread));
            return true;
        };
        window.AddController(keys);
        UpdateDebugState();
    }

    // ---------- breakpoints ----------

    void ToggleBreakpoint(EditorTab tab, int line)
    {
        if (!tab.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        breakpoints.Set(tab.Path, tab.BreakpointLines()); // take edits into account first
        var added = breakpoints.Toggle(tab.Path, line);
        Status(added ? $"Breakpoint at {Path.GetFileName(tab.Path)}:{line}" : "Breakpoint removed");
    }

    void ShowBreakpoints(string path) =>
        editors.Find(path)?.SetBreakpoints(breakpoints.For(path), unverifiedBreakpoints.GetValueOrDefault(path), l => breakpoints.OptionsFor(path, l));

    /// <summary>Condition sent to the debugger with the breakpoint (netcoredbg evaluates it).</summary>
    string? BreakpointCondition(string path, int line) => breakpoints.OptionsFor(path, line).Condition;

    /// <summary>Right click on a breakpoint or Ctrl+Shift+F8 at the caret line: condition, hit count, log message.</summary>
    void EditBreakpoint(EditorTab tab, int line)
    {
        if (!tab.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        breakpoints.Set(tab.Path, tab.BreakpointLines()); // take edits into account first
        var path = tab.Path;
        new BreakpointDialog(window, Path.GetFileName(path), line, breakpoints.OptionsFor(path, line), debug?.SupportsConditions ?? true, options =>
        {
            breakpoints.SetOptions(path, line, options);
            hitCounts.Remove((path, line));
            Status($"Breakpoint at {Path.GetFileName(path)}:{line}: {options.Describe()}");
        }).Present();
    }

    async Task SendBreakpointsAsync(string path)
    {
        if (debug is not { IsRunning: true } session) return;
        try
        {
            var states = await session.SetBreakpointsAsync(path, breakpoints.For(path));
            RememberBoundLines(states);
            unverifiedBreakpoints[path] = states.Where(s => !s.Verified).Select(s => s.RequestedLine).ToHashSet();
            ShowBreakpoints(path);
        }
        catch (Exception e) { logger.LogDebug(e, "setBreakpoints failed"); }
    }

    // ---------- start / stop ----------

    static bool IsExecutable(string project) => KindOf(project) is { } kind && kind is ProjectKind.Web or ProjectKind.Worker or ProjectKind.Console;

    static readonly Dictionary<string, (DateTime Written, ProjectKind Kind)> projectKinds = new(StringComparer.Ordinal);

    /// <summary>Project type from the .csproj, cached until the file changes (the toolbar asks on every tab switch).</summary>
    static ProjectKind? KindOf(string project)
    {
        var written = File.GetLastWriteTimeUtc(project);
        if (written.Year < 1700) return null; // missing file
        if (projectKinds.TryGetValue(project, out var cached) && cached.Written == written) return cached.Kind;
        var kind = ProjectInfo.Read(project).Kind;
        projectKinds[project] = (written, kind);
        return kind;
    }

    /// <summary>The startup project without asking, and why it is the one (for the toolbar).</summary>
    (string Project, string Reason)? ResolveStartupProject()
    {
        if (session.Folder is not { } folder) return null;
        if (state.StartupProject.GetValueOrDefault(folder) is { } saved && IsExecutable(saved)) return (saved, "set as startup project");
        if (CurrentProject is { } current && IsExecutable(current)) return (current, "the current file's project");
        var executables = AllProjects().Where(IsExecutable).Take(2).ToList();
        return executables.Count == 1 ? (executables[0], "the only runnable project") : null;
    }

    Gio.SimpleAction? selectStartupAction, selectProfileAction;
    string? startupMenuSignature;

    /// <summary>Radio actions of the toolbar's startup menu: win.select-startup (project path), win.select-profile (name).</summary>
    void AddStartupMenuActions()
    {
        selectStartupAction = Gio.SimpleAction.NewStateful("select-startup", GLib.VariantType.New("s"), GLib.Variant.NewString(""));
        selectStartupAction.OnActivate += (_, e) =>
        {
            if (e.Parameter?.GetString(out nuint _) is { } project) SetStartupProject(project);
        };
        window.AddAction(selectStartupAction);
        selectProfileAction = Gio.SimpleAction.NewStateful("select-profile", GLib.VariantType.New("s"), GLib.Variant.NewString(""));
        selectProfileAction.OnActivate += (_, e) =>
        {
            if (e.Parameter?.GetString(out nuint _) is not { } name || ResolveStartupProject() is not { } startup) return;
            state.LaunchProfile[startup.Project] = name;
            Status($"Run and F5 use the profile '{name}' for {Path.GetFileNameWithoutExtension(startup.Project)}.");
            UpdateStartupButton();
        };
        window.AddAction(selectProfileAction);
        Add("open-launch-settings", () =>
        {
            if (ResolveStartupProject() is { } startup && LaunchSettingsPath(startup.Project) is { } file) editors.Open(file);
        });
    }

    static string? LaunchSettingsPath(string project)
    {
        var file = Path.Combine(Path.GetDirectoryName(project)!, "Properties", "launchSettings.json");
        return File.Exists(file) ? file : null;
    }

    /// <summary>
    /// Toolbar menu button: type icon, name and launch profile of the project F5 / Ctrl+F5 start. Its menu lists the
    /// runnable projects and the startup project's launch profiles (Properties/launchSettings.json).
    /// </summary>
    void UpdateStartupButton()
    {
        startupButton.Visible = session.IsOpen;
        if (!session.IsOpen) return;
        var startup = ResolveStartupProject();
        var profiles = startup is { } s ? DebugTargets.ReadLaunchProfiles(s.Project) : [];
        var profile = startup is { } p ? LaunchProfileFor(p.Project) : null;
        if (startup is { } current)
        {
            startupIcon.SetFromIconName(KindOf(current.Project) switch
            {
                ProjectKind.Web => "network-server-symbolic",
                ProjectKind.Worker => "system-run-symbolic",
                _ => "utilities-terminal-symbolic",
            });
            var name = Path.GetFileNameWithoutExtension(current.Project);
            startupLabel.SetText(profile is null ? name : $"{name} · {profile.Name}");
            startupLabel.RemoveCssClass("dim-label");
            startupButton.TooltipText = $"Startup project ({current.Reason})"
                + (profile is null ? "" : $", launch profile {profile.Name}{(profile.ApplicationUrl is { } url ? " · " + url : "")}")
                + ": F5 debugs, Ctrl+F5 runs it.";
        }
        else
        {
            startupIcon.SetFromIconName("applications-engineering-symbolic");
            startupLabel.SetText("Startup project…");
            startupLabel.AddCssClass("dim-label");
            startupButton.TooltipText = "Choose the project F5 debugs and Ctrl+F5 runs";
        }
        selectStartupAction?.SetState(GLib.Variant.NewString(startup?.Project ?? ""));
        selectProfileAction?.SetState(GLib.Variant.NewString(profile?.Name ?? ""));

        // Rebuild the menu only when its entries change (this runs on every tab switch).
        var runnable = AllProjects().Where(IsExecutable)
            .OrderBy(x => KindOf(x) switch { ProjectKind.Web => 0, ProjectKind.Worker => 1, _ => 2 })
            .ThenBy(x => Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase).ToList();
        var signature = string.Join("\n", runnable) + "\n|" + startup?.Project + "|"
            + string.Join("\n", profiles.Select(x => x.Name + " " + x.ApplicationUrl));
        if (signature == startupMenuSignature) return;
        startupMenuSignature = signature;

        var menu = Gio.Menu.New();
        var projects = Gio.Menu.New();
        foreach (var project in runnable)
        {
            var item = Gio.MenuItem.New($"{Path.GetFileNameWithoutExtension(project)}  ({KindLabel(project)})", null);
            item.SetActionAndTargetValue("win.select-startup", GLib.Variant.NewString(project));
            projects.AppendItem(item);
        }
        if (runnable.Count == 0) projects.Append("No runnable project (Web, Worker or Exe)", "win.none");
        menu.AppendSection("Startup project", projects);
        if (startup is { } chosen)
        {
            var launch = Gio.Menu.New();
            foreach (var x in profiles)
            {
                var item = Gio.MenuItem.New(x.ApplicationUrl is { } url ? $"{x.Name}  ·  {url}" : x.Name, null);
                item.SetActionAndTargetValue("win.select-profile", GLib.Variant.NewString(x.Name));
                launch.AppendItem(item);
            }
            if (LaunchSettingsPath(chosen.Project) is not null) launch.Append("Edit launchSettings.json", "win.open-launch-settings");
            else launch.Append("No Properties/launchSettings.json", "win.none");
            menu.AppendSection($"Launch profile · {Path.GetFileNameWithoutExtension(chosen.Project)}", launch);
        }
        startupButton.MenuModel = menu;
    }

    /// <summary>
    /// The project F5 / Ctrl+F5 runs: the startup project set by the user, else the current file's project when it is
    /// runnable, else the only runnable project, else the one the user picks (remembered per folder). Runnable = Web,
    /// Worker or OutputType Exe, not a test project (ProjectInfo.Kind) — no file has to be open.
    /// </summary>
    async Task<string?> ChooseStartupProjectAsync(bool force = false)
    {
        if (session.Folder is not { } folder) return null;
        if (!force && ResolveStartupProject() is { } startup) return startup.Project;
        var executables = AllProjects().Where(IsExecutable).ToList();
        if (executables.Count == 0)
        {
            await Ui.ShowErrorAsync(window, "Nothing to run", "The solution has no runnable project (Web, Worker or OutputType Exe).");
            return null;
        }
        // Web apps first: they are what one usually starts.
        var shown = executables.OrderBy(p => KindOf(p) switch { ProjectKind.Web => 0, ProjectKind.Worker => 1, _ => 2 })
            .ThenBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        var choice = await Ui.ChooseAsync(window, "Startup project", "F5 debugs and Ctrl+F5 runs this project.",
            ["Cancel", .. shown.Select(p => $"{Path.GetFileNameWithoutExtension(p)}  ({KindLabel(p)})")], 0, 1);
        if (choice <= 0) return null;
        SetStartupProject(shown[choice - 1]);
        return shown[choice - 1];
    }

    static string KindLabel(string project) => KindOf(project) switch
    {
        ProjectKind.Web => "web",
        ProjectKind.Worker => "worker",
        ProjectKind.Console => "console",
        _ => "",
    };

    void SetStartupProject(string project)
    {
        if (session.Folder is not { } folder) return;
        state.StartupProject[folder] = project;
        explorer.StartupProject = project;
        UpdateStartupButton();
        Status($"Startup project: {Path.GetFileNameWithoutExtension(project)}");
    }

    /// <summary>F5: continue when paused, else debug the given project (explorer) or the startup project.</summary>
    async Task StartOrContinueAsync(string? target = null)
    {
        if (debug is not null)
        {
            if (paused) await StepAsync(s => s.ContinueAsync(stoppedThread));
            return;
        }
        if (FindAdapterOrExplain() is not { } adapter) return;
        if ((target ?? await ChooseStartupProjectAsync()) is not { } project) return;

        foreach (var tab in editors.Tabs) breakpoints.Set(tab.Path, tab.BreakpointLines());
        if (!await RunBuildAsync(BuildAction.Build, project)) return;

        var profile = LaunchProfileFor(project);
        browserOpened = profile is not { LaunchBrowser: true }; // open the browser once, when the app listens
        launchProfile = profile;
        await StartSessionAsync(project, profile?.Name, async () =>
        {
            var launch = await DebugTargets.ForProjectAsync(project, Configuration, profile, CancellationToken.None);
            bottom.AppendOutput($"> netcoredbg: {launch.Program}");
            return await Task.Run(() => DebugSession.StartAsync(adapter, launch, breakpoints.All(), OnProgramOutput, CancellationToken.None, BreakpointCondition));
        });
    }

    string? FindAdapterOrExplain()
    {
        if (DebugTargets.FindAdapter() is { } adapter) return adapter;
        _ = Ui.ShowErrorAsync(window, "Debugger not installed", DebugTargets.InstallHint);
        return null;
    }

    /// <summary>Starts (launch or attach) a session and wires its events; shared by F5 and test debugging.</summary>
    async Task<bool> StartSessionAsync(string project, string? detail, Func<Task<DebugSession>> start)
    {
        var name = Path.GetFileNameWithoutExtension(project) + (detail is null ? "" : $" ({detail})");
        bottom.Debug.Clear();
        bottom.Debug.SetState($"Starting {name}…");
        ShowPage(BottomPanel.OutputPage);
        try
        {
            unverifiedBreakpoints.Clear();
            hitCounts.Clear();
            boundLines.Clear();
            var session = await start();
            debug = session;
            debugProject = project;
            session.Stopped += info => Ui.Post(() => _ = OnStoppedAsync(info));
            session.Continued += () => Ui.Post(ClearPause);
            session.Terminated += code => Ui.Post(() => _ = OnTerminatedAsync(session, code));
            session.BreakpointChanged += bp => Ui.Post(() =>
            {
                RememberBoundLines([bp]);
                var set = unverifiedBreakpoints.TryGetValue(bp.Path, out var s) ? s : unverifiedBreakpoints[bp.Path] = [];
                if (bp.Verified) set.Remove(bp.RequestedLine);
                else set.Add(bp.RequestedLine);
                ShowBreakpoints(bp.Path);
            });
            foreach (var path in breakpoints.All().Keys) _ = SendBreakpointsAsync(path); // refresh verified state
            bottom.Debug.SetState($"Running {name}");
            Status($"Debugging {name} — F9 breakpoint, Shift+F5 stop", 10);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Debugging failed to start");
            bottom.Debug.SetState("Not debugging.");
            await Ui.ShowErrorAsync(window, "Cannot start debugging", e.Message);
            return false;
        }
        finally { UpdateDebugState(); }
    }

    // ---------- debugging a test ----------

    [System.Text.RegularExpressions.GeneratedRegex(@"Process Id:\s*(\d+)")]
    private static partial System.Text.RegularExpressions.Regex TestHostProcess();

    /// <summary>
    /// Debugs the test at the caret. VSTest: dotnet test with VSTEST_HOST_DEBUG=1 makes the testhost print its id and
    /// wait; the debugger attaches to it. Microsoft.Testing.Platform: the test project is a program, launched under
    /// the debugger with --filter-method.
    /// </summary>
    async Task DebugTestAtCursorAsync()
    {
        if (debug is not null)
        {
            Status("A debugging session is running. Stop it first (Shift+F5).");
            return;
        }
        if (editors.Active is not { } tab || CurrentProject is not { } project) return;
        var text = tab.GetText();
        var name = await Task.Run(() => TestDiscovery.TestNameAt(text, TextOffsets.CharToUtf16(text, tab.CursorOffset)));
        if (name is null)
        {
            Status("Place the caret inside a test method or test class.");
            return;
        }
        await DebugTestsAsync(project, [name], name);
    }

    async Task DebugTestsInFileAsync()
    {
        if (debug is not null)
        {
            Status("A debugging session is running. Stop it first (Shift+F5).");
            return;
        }
        if (await TestsInActiveFileAsync() is not var (project, classes)) return;
        await DebugTestsAsync(project, classes, Path.GetFileName(editors.Active!.Path));
    }

    /// <summary>Debugs the given tests ("Ns.Class.Method" / "Ns.Class.*") of one test project.</summary>
    async Task DebugTestsAsync(string project, IReadOnlyList<string> names, string name)
    {
        if (session.Folder is not { } folder) return;
        lastTestRun = () => debug is null ? DebugTestsAsync(project, names, name) : Task.CompletedTask;
        if (FindAdapterOrExplain() is not { } adapter) return;
        foreach (var t in editors.Tabs) breakpoints.Set(t.Path, t.BreakpointLines());
        browserOpened = true;
        launchProfile = null;

        if (TestRunner.DetectMode(Path.GetDirectoryName(project)!) == TestRunnerMode.MicrosoftTestingPlatform)
        {
            if (!await RunBuildAsync(BuildAction.Build, project)) return;
            await StartSessionAsync(project, name, async () =>
            {
                var launch = await DebugTargets.ForProjectAsync(project, Configuration, null, CancellationToken.None);
                launch = launch with { Arguments = names.SelectMany(n => new[] { "--filter-method", n }).ToList() };
                bottom.AppendOutput($"> netcoredbg: {launch.Program} {string.Join(' ', launch.Arguments)}");
                return await Task.Run(() => DebugSession.StartAsync(adapter, launch, breakpoints.All(), OnProgramOutput, CancellationToken.None, BreakpointCondition));
            });
            return;
        }

        // VSTest: run the tests and attach when the testhost reports its process id.
        if (!BeginOperation($"Debug test {name}")) return;
        var ct = operationCts!.Token;
        bottom.SetTestsRunning(name, [(project, names)]);
        bottom.Debug.SetState("Waiting for the test host…");
        var attached = 0;
        void OnOutput(string line)
        {
            bottom.AppendOutput(line);
            if (TestHostProcess().Match(line) is { Success: true } m && Interlocked.Exchange(ref attached, 1) == 0)
            {
                var pid = int.Parse(m.Groups[1].Value);
                Ui.Post(() => _ = StartSessionAsync(project, name, () =>
                    Task.Run(() => DebugSession.AttachAsync(adapter, pid, folder, breakpoints.All(), OnProgramOutput, CancellationToken.None, BreakpointCondition))));
            }
        }
        try
        {
            var environment = new Dictionary<string, string?> { ["VSTEST_HOST_DEBUG"] = "1" };
            var result = await Task.Run(() => tests.RunAsync(project, folder, Configuration, names, OnOutput, ct, environment), CancellationToken.None);
            bottom.SetTestResults(result);
            RememberTestOutcomes(result);
            ShowPage(BottomPanel.TestsPage);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Debugging a test failed");
            bottom.Tests.StopRunning("Debugging the test failed: " + e.Message);
            bottom.AppendOutput("Error: " + e.Message);
        }
        finally { EndOperation(); }
    }

    async Task RunFailedTestsAsync()
    {
        var failed = bottom.Tests.Failed;
        if (failed.Count == 0)
        {
            Status("No failed tests in the last run.");
            return;
        }
        // VSTest and MTP both take the full method names; theory cases collapse to their method.
        var names = failed.Select(t => t.FullName).Distinct().ToList();
        await RunTestsAsync(debugProjectForTests ?? SolutionTarget, names);
    }

    /// <summary>A test from the results without a source location: find its method in the symbol index.</summary>
    void FindTest(LightweightIDE.DotNet.Testing.TestCaseResult test)
    {
        var method = test.FullName.IndexOf('(') is > 0 and var p ? test.FullName[..p] : test.FullName;
        var shortName = method[(method.LastIndexOf('.') + 1)..];
        var className = method.LastIndexOf('.') is > 0 and var dot ? method[..dot] : "";
        var hit = session.Search?.Symbols(shortName, 50)
            .FirstOrDefault(h => h.Name == shortName && className.EndsWith(h.Container ?? "", StringComparison.Ordinal));
        if (hit is not null) Navigate(new Location(hit.Path, hit.Line, hit.Column, hit.Name.Length));
        else Status($"Cannot find {method} in the index.");
    }

    /// <summary>Program output arrives in chunks; the Output panel takes whole lines.</summary>
    void OnProgramOutput(string category, string text)
    {
        if (category == "telemetry") return;

        lock (programOutput)
        {
            programOutput.Append(text);
            var all = programOutput.ToString();
            var end = all.LastIndexOf('\n');
            if (end < 0) return;
            // Debugger.Log output: ASP.NET's Debug logger repeats every console log line as "Category: Level: message".
            foreach (var line in all[..end].Split('\n'))
            {
                if (DebugLoggerLine().IsMatch(line)) continue;
                bottom.AppendOutput(line.TrimEnd('\r'));
                if (!browserOpened && ListeningLine().Match(line) is { Success: true } listening)
                {
                    browserOpened = true;
                    var url = listening.Groups[1].Value.TrimEnd('/');
                    if (launchProfile?.LaunchUrl is { Length: > 0 } path) url += "/" + path.TrimStart('/');
                    Ui.Post(() => OpenExternally(url));
                }
            }
            programOutput.Clear().Append(all[(end + 1)..]);
        }
    }

    async Task StopDebuggingAsync()
    {
        if (debug is not { } session) return;
        bottom.Debug.SetState("Stopping…");
        await session.StopAsync();
    }

    async Task OnTerminatedAsync(DebugSession session, int? code)
    {
        if (debug != session) return;
        debug = null;
        ClearPause();
        bottom.Debug.Clear();
        var name = debugProject is null ? "Program" : Path.GetFileNameWithoutExtension(debugProject);
        bottom.Debug.SetState(code is { } c ? $"{name} exited with code {c}." : $"{name} ended.");
        Status(code is { } exit ? $"{name} exited with code {exit}" : $"{name} ended");
        unverifiedBreakpoints.Clear();
        foreach (var path in breakpoints.All().Keys) ShowBreakpoints(path);
        OnProgramOutput("stdout", "\n");
        UpdateDebugState();
        await session.DisposeAsync();
    }

    // ---------- launch profiles ----------

    /// <summary>The chosen launch profile of a project, else its first "Project" profile.</summary>
    LaunchProfile? LaunchProfileFor(string project)
    {
        var profiles = DebugTargets.ReadLaunchProfiles(project);
        return state.LaunchProfile.GetValueOrDefault(project) is { } name && profiles.FirstOrDefault(p => p.Name == name) is { } chosen
            ? chosen
            : profiles.FirstOrDefault();
    }

    async Task ChooseLaunchProfileAsync()
    {
        if (await ChooseStartupProjectAsync() is not { } project) return;
        var profiles = DebugTargets.ReadLaunchProfiles(project).Take(8).ToList();
        if (profiles.Count == 0)
        {
            Status($"{Path.GetFileNameWithoutExtension(project)} has no launch profiles (Properties/launchSettings.json).");
            return;
        }
        var current = LaunchProfileFor(project)?.Name;
        var choice = await Ui.ChooseAsync(window, $"Launch profile of {Path.GetFileNameWithoutExtension(project)}",
            string.Join("\n", profiles.Select(p => $"{(p.Name == current ? "● " : "  ")}{p.Name}: {p.ApplicationUrl ?? "no URL"}{(p.LaunchBrowser ? " · opens the browser" : "")}")),
            ["Cancel", .. profiles.Select(p => p.Name)], 0, Math.Max(1, profiles.FindIndex(p => p.Name == current) + 1));
        if (choice <= 0) return;
        state.LaunchProfile[project] = profiles[choice - 1].Name;
        Status($"Run and F5 use the profile '{profiles[choice - 1].Name}' for {Path.GetFileNameWithoutExtension(project)}.");
        UpdateStartupButton();
    }

    // ---------- pause / step ----------

    async Task PauseAsync()
    {
        if (debug is not { } session || paused) return;
        var threads = await session.ThreadsAsync();
        if (threads.FirstOrDefault() is { } thread) await session.PauseAsync(thread.Id);
    }

    async Task StepAsync(Func<DebugSession, Task> step)
    {
        if (debug is not { } session || !paused) return;
        ClearPause();
        bottom.Debug.SetState("Running…");
        try { await step(session); }
        catch (Exception e) { Status("Debugger: " + e.Message); }
    }

    /// <summary>Hits per breakpoint (file, requested line) in this session, for hit count rules.</summary>
    readonly Dictionary<(string Path, int Line), int> hitCounts = [];
    /// <summary>The debugger may bind a breakpoint to a later line (the next statement): (file, bound line) → requested line.</summary>
    readonly Dictionary<(string Path, int Line), int> boundLines = [];

    void RememberBoundLines(IEnumerable<BreakpointState> states)
    {
        foreach (var state in states.Where(s => s.Verified)) boundLines[(state.Path, state.Line)] = state.RequestedLine;
    }

    /// <summary>
    /// A breakpoint with a hit count rule or a log message: decides whether to pause. Returns true when the IDE
    /// continued the program (rule not met, or a logpoint that wrote its message).
    /// </summary>
    async Task<bool> HandleBreakpointOptionsAsync(DebugSession session, StoppedInfo info)
    {
        if (info.Reason != "breakpoint" || breakpoints.Count == 0) return false;
        IReadOnlyList<DebugFrame> frames;
        try { frames = await session.StackTraceAsync(info.ThreadId, 1); }
        catch (Exception) { return false; }
        if (frames.FirstOrDefault() is not { Path: { } path } frame) return false;
        var line = boundLines.TryGetValue((path, frame.Line), out var requested) ? requested : frame.Line;
        var options = breakpoints.OptionsFor(path, line);
        if (options.IsDefault) return false;

        if (!session.SupportsConditions && !string.IsNullOrWhiteSpace(options.Condition))
        {
            var value = await EvaluateQuietAsync(session, options.Condition, frame.Id);
            if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return await ContinueAsync();
        }
        var hits = hitCounts[(path, line)] = hitCounts.GetValueOrDefault((path, line)) + 1;
        if (!options.Matches(hits)) return await ContinueAsync();
        if (!options.IsLogpoint) return false;

        var text = new System.Text.StringBuilder();
        foreach (var (part, isExpression) in BreakpointOptions.ParseMessage(options.LogMessage!))
            text.Append(isExpression ? await EvaluateQuietAsync(session, part, frame.Id) : part);
        bottom.AppendOutput($"[{Path.GetFileName(path)}:{line}] {text}");
        return await ContinueAsync();

        async Task<bool> ContinueAsync()
        {
            try { await session.ContinueAsync(info.ThreadId); }
            catch (Exception e) { logger.LogDebug(e, "continue after a breakpoint rule failed"); return false; }
            return true;
        }
    }

    static async Task<string> EvaluateQuietAsync(DebugSession session, string expression, int frameId)
    {
        try { return (await session.EvaluateAsync(expression, frameId)).Value; }
        catch (Exception e) { return $"<{expression}: {e.Message}>"; }
    }

    async Task OnStoppedAsync(StoppedInfo info)
    {
        if (debug is not { } session) return;
        if (await HandleBreakpointOptionsAsync(session, info)) return;
        paused = true;
        stoppedThread = info.ThreadId;
        UpdateDebugState();
        IReadOnlyList<DebugFrame> frames;
        try { frames = await session.StackTraceAsync(info.ThreadId); }
        catch (Exception e)
        {
            bottom.Debug.SetState("Paused (no stack: " + e.Message + ")");
            return;
        }
        // The first frame with source is where the user wants to look (framework frames have no file).
        var index = Math.Max(0, frames.ToList().FindIndex(f => f.Path is not null && File.Exists(f.Path)));
        bottom.Debug.ShowFrames(frames, index);
        var reason = info.Reason switch
        {
            "breakpoint" => "breakpoint",
            "step" => "step",
            "exception" => "exception: " + info.Text,
            "pause" => "paused",
            "entry" => "entry",
            _ => info.Reason,
        };
        bottom.Debug.SetState($"Paused ({reason}) — F5 continue, F10 step over, F11 step into, Shift+F11 step out");
        ShowPage(BottomPanel.DebugPage);
        foreach (var tab in editors.Tabs) tab.DebugEvaluator = EvaluateForHoverAsync;
        if (frames.ElementAtOrDefault(index) is { } frame) await SelectFrameAsync(frame);
        window.Present();
    }

    async Task SelectFrameAsync(DebugFrame frame)
    {
        if (debug is not { } session) return;
        currentFrame = frame;
        currentLineTab?.SetCurrentLine(null);
        currentLineTab = null;
        if (frame.Path is { } path && File.Exists(path) && editors.Open(path, frame.Line, Math.Max(1, frame.Column)) is { } tab)
        {
            tab.SetCurrentLine(frame.Line);
            currentLineTab = tab;
        }
        try
        {
            var scopes = await session.ScopesAsync(frame.Id);
            var shown = new List<(string, IReadOnlyList<DebugVariable>)>();
            foreach (var scope in scopes.Where(s => !s.Expensive))
                shown.Add((scope.Name, await session.VariablesAsync(scope.Reference)));
            await bottom.Debug.ShowVariablesAsync(shown);
        }
        catch (Exception e) { logger.LogDebug(e, "Reading variables failed"); }
    }

    async Task<string?> EvaluateForHoverAsync(string expression)
    {
        if (debug is not { } session || currentFrame is not { } frame || !paused) return null;
        try
        {
            var value = await session.EvaluateAsync(expression, frame.Id, "hover");
            return value.Type is { Length: > 0 } type ? $"{value.Value}  ({type})" : value.Value;
        }
        catch (Exception) { return null; }
    }

    void ClearPause()
    {
        paused = false;
        currentFrame = null;
        currentLineTab?.SetCurrentLine(null);
        currentLineTab = null;
        foreach (var tab in editors.Tabs) tab.DebugEvaluator = null;
        UpdateDebugState();
    }

    void UpdateDebugState()
    {
        var running = debug is not null;
        Enable("debug-start", session.IsOpen && (!running || paused));
        Enable("debug-stop", running);
        Enable("debug-pause", running && !paused);
        foreach (var n in new[] { "debug-step-over", "debug-step-into", "debug-step-out" }) Enable(n, running && paused);
        Enable("startup-project", session.IsOpen);
        debugLabel.SetText(!running ? "" : paused ? "⏸ Paused" : "▶ Debugging");
        debugLabel.Visible = running;
    }
}
