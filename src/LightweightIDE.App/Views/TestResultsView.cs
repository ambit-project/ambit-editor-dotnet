using System.Text.RegularExpressions;
using LightweightIDE.DotNet.Testing;
using LightweightIDE.Language.Testing;

namespace LightweightIDE.App.Views;

/// <summary>
/// Test explorer: the tests found in the source (project → class → method), known before the first run, with the
/// latest result of each test merged in. Run / Debug the selected node; the selected test's message and stack trace
/// with clickable "in File.cs:line N" frames.
/// </summary>
public sealed partial class TestResultsView
{
    const int MaxRows = 5000;
    readonly Gtk.Paned root = Gtk.Paned.New(Gtk.Orientation.Horizontal);
    readonly Gtk.Label summary = Ui.Label("No tests found yet.", "dim-label");
    readonly Gtk.ListBox list = Gtk.ListBox.New();
    readonly Gtk.TextView detail = Gtk.TextView.New();
    readonly Gtk.TextTag linkTag = Gtk.TextTag.New("stack-link");
    readonly Gtk.ToggleButton failedOnly = Gtk.ToggleButton.NewWithLabel("Failed Only");
    readonly List<(int Start, int End, string Path, int Line)> links = [];
    readonly List<Node> rows = [];
    /// <summary>Collapsed project / class nodes (by key). Classes start collapsed unless something in them failed.</summary>
    readonly HashSet<string> collapsed = new(StringComparer.Ordinal);
    readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    IReadOnlyList<DiscoveredTest> discovered = [];
    /// <summary>Latest results per method ("Ns.Class.Method", data-driven cases folded), across runs.</summary>
    readonly Dictionary<string, List<TestCaseResult>> latest = new(StringComparer.Ordinal);
    IReadOnlyList<TestCaseResult> lastRun = [];
    string runSummary = "";
    /// <summary>What the current run covers: (project or solution/folder target, filter; null = all its tests).</summary>
    IReadOnlyList<(string Target, IReadOnlyList<string>? Filter)>? running;
    /// <summary>Methods the last run covered that got no result (e.g. names that do not match the source).</summary>
    readonly HashSet<string> noResult = new(StringComparer.Ordinal);

    enum Kind { Project, Class, Method, Case }

    /// <summary>A row: Filter is what dotnet test runs for it (null = the whole project).</summary>
    sealed record Node(Kind Kind, string Key, string Label, string? Project, IReadOnlyList<string>? Filter, string? File, int Line,
        TestOutcome? Outcome, TestCaseResult? Result, int Depth, bool Running = false);

    [GeneratedRegex(@" in (/[^\n]+?\.cs):line (\d+)")]
    private static partial Regex StackFrame();

    public Gtk.Widget Widget => root;
    public Gtk.Label TabLabel { get; } = Ui.Label("Test Results");
    public IReadOnlyList<TestCaseResult> Failed => lastRun.Where(t => t.Outcome == TestOutcome.Failed).ToList();

    /// <summary>A stack frame or a test with a known source location was activated.</summary>
    public event Action<Location>? Navigate;
    /// <summary>A test without a source location was activated: find it by name.</summary>
    public event Action<TestCaseResult>? FindTest;
    /// <summary>Run (false) or debug (true) the selected node: project path and filter (null = all tests of the project).</summary>
    public event Action<string, IReadOnlyList<string>?, bool>? RunRequested;

    public TestResultsView()
    {
        var left = Ui.VBox();
        var bar = Ui.HBox(4);
        bar.AddCssClass("panel-toolbar");
        var runSelected = Ui.TextButton("▶ Run", "Run the selected project, class or test (double click a test opens its source)");
        runSelected.OnClicked += (_, _) => RunSelected(false);
        var debugSelected = Ui.TextButton("Debug", "Debug the selected project, class or test");
        debugSelected.OnClicked += (_, _) => RunSelected(true);
        bar.Append(runSelected);
        bar.Append(debugSelected);
        bar.Append(Ui.TextButton("Rerun", "Run the last tests again (Shift+Alt+R)", "win.test-rerun"));
        bar.Append(Ui.TextButton("Run Failed", "Run only the failed tests again", "win.test-run-failed"));
        bar.Append(Ui.TextButton("Run All", "Run all tests (Ctrl+Shift+T)", "win.test-solution"));
        failedOnly.AddCssClass("flat");
        failedOnly.TooltipText = "Show only failed tests";
        failedOnly.OnToggled += (_, _) => Rebuild();
        bar.Append(failedOnly);
        summary.Hexpand = true;
        summary.Ellipsize = Pango.EllipsizeMode.End;
        summary.MarginStart = 8;
        bar.Append(summary);
        left.Append(bar);
        list.AddCssClass("result-list");
        list.OnRowSelected += (_, e) => ShowDetail(e.Row is null ? null : rows.ElementAtOrDefault(e.Row.GetIndex()));
        list.OnRowActivated += (_, e) => Activate(rows.ElementAtOrDefault(e.Row.GetIndex()));
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (Selected is not { } node) return false;
            switch (e.Keyval)
            {
                case Gdk.Constants.KEY_Left when node.Kind is Kind.Project or Kind.Class or Kind.Method && IsExpanded(node):
                    Toggle(node);
                    return true;
                case Gdk.Constants.KEY_Right when node.Kind is Kind.Project or Kind.Class or Kind.Method && !IsExpanded(node):
                    Toggle(node);
                    return true;
            }
            return false;
        };
        list.AddController(keys);
        left.Append(Ui.Scrolled(list));

        detail.Editable = false;
        detail.Monospace = true;
        detail.WrapMode = Gtk.WrapMode.WordChar;
        detail.LeftMargin = 8;
        var blue = new Gdk.RGBA();
        blue.Parse("#3b82f6");
        linkTag.ForegroundRgba = blue;
        linkTag.Underline = Pango.Underline.Single;
        detail.Buffer!.TagTable!.Add(linkTag);
        var click = Gtk.GestureClick.New();
        click.Button = 1;
        click.OnReleased += (_, e) => OnDetailClick(e.X, e.Y);
        detail.AddController(click);

        root.SetStartChild(left);
        root.SetEndChild(Ui.Scrolled(detail));
        root.Position = 560;
    }

    Node? Selected => list.GetSelectedRow() is { } row ? rows.ElementAtOrDefault(row.GetIndex()) : null;

    /// <param name="runs">The targets and filters of the run: their tests show "running…" until the results come.</param>
    public void SetRunning(string what, IReadOnlyList<(string Target, IReadOnlyList<string>? Filter)>? runs = null)
    {
        running = runs;
        noResult.Clear();
        runSummary = $"Running tests: {what}…";
        TabLabel.SetText("Test Results …");
        Rebuild();
    }

    /// <summary>The run ended without results (it failed to start): the tests stop showing "running…".</summary>
    public void StopRunning(string message)
    {
        running = null;
        runSummary = message;
        TabLabel.SetText("Test Results");
        Rebuild();
    }

    /// <summary>Does the current run include this test method of this project?</summary>
    bool IsRunning(string method, string? project)
    {
        if (running is null) return false;
        foreach (var (target, filter) in running)
        {
            // A project runs its own tests; a solution or folder target runs every project's.
            if (project is not null && target.EndsWith("proj", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(project), StringComparison.Ordinal))
                continue;
            if (filter is null) return true;
            foreach (var f in filter)
                if (f.EndsWith(".*", StringComparison.Ordinal) ? method.StartsWith(f[..^1], StringComparison.Ordinal) : TestCaseResult.Normalize(f) == method)
                    return true;
        }
        return false;
    }

    /// <summary>Tests found in the source of the solution's test projects (replaces the previous list).</summary>
    public void SetDiscovered(IReadOnlyList<DiscoveredTest> tests)
    {
        discovered = tests;
        Rebuild();
    }

    public void SetResults(TestRunResult result)
    {
        lastRun = result.Tests;
        foreach (var group in result.Tests.GroupBy(t => t.Key)) latest[group.Key] = group.ToList();
        // Tests the run covered but did not report: say so instead of leaving them "not run".
        noResult.Clear();
        if (running is not null && !result.Cancelled)
        {
            var reported = result.Tests.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var t in discovered)
                if (!reported.Contains(t.Name) && IsRunning(t.Name, t.Project)) noResult.Add(t.Name);
        }
        running = null;
        runSummary = result.Cancelled ? "Cancelled."
            : result.Tests.Count == 0 ? $"No TRX results (exit code {result.ExitCode}, {result.Mode}). See Output."
            : $"{result.Passed} passed, {result.Failed} failed, {result.Skipped} skipped — {result.Duration.TotalSeconds:0.0}s ({result.Mode})";
        TabLabel.SetText(result.Failed > 0 ? $"Test Results ({result.Failed} ✗)" : "Test Results");
        // Classes with failures open; the first failed test is selected.
        foreach (var t in result.Tests.Where(t => t.Outcome == TestOutcome.Failed))
        {
            var cls = ClassOf(t.Key);
            expanded.Add("class|" + cls);
            collapsed.Remove("class|" + cls);
        }
        Rebuild();
        var firstFailed = rows.FindIndex(n => n.Kind is Kind.Method or Kind.Case && n.Outcome == TestOutcome.Failed);
        if (firstFailed >= 0 && list.GetRowAtIndex(firstFailed) is { } r) list.SelectRow(r);
    }

    static string ClassOf(string method) => method.LastIndexOf('.') is > 0 and var dot ? method[..dot] : method;

    static TestOutcome? Worst(IEnumerable<TestOutcome?> outcomes)
    {
        var list = outcomes.ToList();
        if (list.Any(o => o == TestOutcome.Failed)) return TestOutcome.Failed;
        if (list.Count > 0 && list.All(o => o is TestOutcome.Passed or TestOutcome.Skipped))
            return list.Any(o => o == TestOutcome.Passed) ? TestOutcome.Passed : TestOutcome.Skipped;
        return null;
    }

    TestOutcome? OutcomeOf(string method) =>
        latest.TryGetValue(method, out var cases) ? Worst(cases.Select(c => (TestOutcome?)c.Outcome)) : null;

    bool IsExpanded(Node node) => node.Kind switch
    {
        Kind.Project => !collapsed.Contains(node.Key),
        Kind.Class => expanded.Contains(node.Key),
        Kind.Method => expanded.Contains(node.Key),
        _ => false,
    };

    void Toggle(Node node)
    {
        var open = IsExpanded(node);
        if (node.Kind == Kind.Project)
        {
            if (open) collapsed.Add(node.Key); else collapsed.Remove(node.Key);
        }
        else if (open) expanded.Remove(node.Key);
        else expanded.Add(node.Key);
        var key = node.Key;
        Rebuild();
        var index = rows.FindIndex(n => n.Key == key);
        if (index >= 0 && list.GetRowAtIndex(index) is { } row)
        {
            list.SelectRow(row);
            row.GrabFocus();
        }
    }

    void Activate(Node? node)
    {
        if (node is null) return;
        if (node.Kind is Kind.Project or Kind.Class)
        {
            Toggle(node);
            return;
        }
        if (node.File is { } file && File.Exists(file)) Navigate?.Invoke(new Location(file, node.Line, 1));
        else if (node.Result is { } r && r.SourceFile is not null && File.Exists(r.SourceFile)) Navigate?.Invoke(new Location(r.SourceFile, r.SourceLine, 1));
        else if (node.Result is { } t) FindTest?.Invoke(t);
    }

    void RunSelected(bool debug)
    {
        if (Selected is not { } node)
        {
            summary.SetText("Select a project, class or test first.");
            return;
        }
        if (node.Project is null)
        {
            summary.SetText("The project of this test is unknown; run it from its file.");
            return;
        }
        RunRequested?.Invoke(node.Project, node.Filter, debug);
    }

    /// <summary>Builds the rows from the discovered tests plus results of tests that were not found in the source.</summary>
    void Rebuild()
    {
        var selectedKey = Selected?.Key;
        list.RemoveAll();
        rows.Clear();
        ShowDetail(null);

        // method key → (project, file, line); results add methods the source scan did not find (e.g. inherited).
        var methods = new Dictionary<string, (string? Project, string? File, int Line)>(StringComparer.Ordinal);
        foreach (var t in discovered) methods.TryAdd(t.Name, (t.Project, t.File, t.Line));
        var classProject = discovered.GroupBy(t => t.Class).ToDictionary(g => g.Key, g => g.First().Project, StringComparer.Ordinal);
        foreach (var key in latest.Keys)
            if (!methods.ContainsKey(key)) methods[key] = (classProject.GetValueOrDefault(ClassOf(key)), null, 0);

        var showFailedOnly = failedOnly.Active;
        var projects = methods
            .Where(m => !showFailedOnly || OutcomeOf(m.Key) == TestOutcome.Failed)
            .GroupBy(m => m.Value.Project ?? "")
            .OrderBy(g => g.Key.Length == 0 ? "￿" : Path.GetFileNameWithoutExtension(g.Key), StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var project in projects)
        {
            var projectPath = project.Key.Length == 0 ? null : project.Key;
            var projectNode = new Node(Kind.Project, "project|" + project.Key,
                projectPath is null ? "Other tests" : Path.GetFileNameWithoutExtension(projectPath), projectPath, null, null, 0,
                Worst(project.Select(m => OutcomeOf(m.Key))), null, 0, project.Any(m => IsRunning(m.Key, projectPath)));
            Add(projectNode, projectNode.Running ? $"running… ({project.Count()} tests)" : $"{project.Count()} tests");
            total += project.Count();
            if (!IsExpanded(projectNode)) continue;
            foreach (var cls in project.GroupBy(m => ClassOf(m.Key)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (rows.Count >= MaxRows) break;
                var outcome = Worst(cls.Select(m => OutcomeOf(m.Key)));
                var failed = cls.Count(m => OutcomeOf(m.Key) == TestOutcome.Failed);
                var classNode = new Node(Kind.Class, "class|" + cls.Key, cls.Key, projectPath, [cls.Key + ".*"],
                    cls.Select(m => m.Value.File).FirstOrDefault(f => f is not null), cls.Where(m => m.Value.File is not null).Select(m => m.Value.Line).DefaultIfEmpty(0).Min(),
                    outcome, null, 1, cls.Any(m => IsRunning(m.Key, projectPath)));
                Add(classNode, classNode.Running ? "running…" : failed > 0 ? $"{failed} of {cls.Count()} failed" : $"{cls.Count()} tests");
                if (!IsExpanded(classNode)) continue;
                foreach (var (method, (_, file, line)) in cls.OrderBy(m => m.Value.File is null ? 1 : 0).ThenBy(m => m.Value.Line).ThenBy(m => m.Key, StringComparer.Ordinal))
                {
                    var cases = latest.GetValueOrDefault(method);
                    var single = cases is { Count: 1 } ? cases[0] : null;
                    var methodNode = new Node(Kind.Method, "method|" + method, method[(cls.Key.Length + 1)..], projectPath, [method], file, line,
                        OutcomeOf(method), single, 2, IsRunning(method, projectPath));
                    var isRunning = IsRunning(method, projectPath);
                    var info = isRunning ? "running…"
                        : cases is null ? (noResult.Contains(method) ? "no result in the last run" : "not run")
                        : cases.Count == 1 ? $"{cases[0].Duration.TotalMilliseconds:0} ms"
                        : $"{cases.Count(c => c.Outcome == TestOutcome.Passed)} of {cases.Count} cases passed";
                    Add(methodNode, info);
                    if (cases is not { Count: > 1 } || !IsExpanded(methodNode)) continue;
                    foreach (var c in cases.OrderBy(c => c.Outcome == TestOutcome.Failed ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal))
                        Add(new Node(Kind.Case, "case|" + c.FullName, c.Name.Contains('(') ? c.Name[c.Name.IndexOf('(')..] : c.Name, projectPath, [method], file, line,
                            c.Outcome, c, 3), $"{c.Duration.TotalMilliseconds:0} ms");
                }
            }
        }
        summary.SetText(total == 0 ? (showFailedOnly ? "No failed tests." : "No tests found yet.")
            : runSummary.Length > 0 ? runSummary : $"{total} tests found — not run yet");
        if (selectedKey is not null && rows.FindIndex(n => n.Key == selectedKey) is >= 0 and var index && list.GetRowAtIndex(index) is { } row)
            list.SelectRow(row);
    }

    void Add(Node node, string info)
    {
        var box = Ui.HBox(6);
        box.MarginStart = 4 + node.Depth * 16;
        var expandable = node.Kind is Kind.Project or Kind.Class || node is { Kind: Kind.Method, Result: null } && latest.GetValueOrDefault(node.Filter![0]) is { Count: > 1 };
        box.Append(Ui.Label(expandable ? (IsExpanded(node) ? "▾" : "▸") : " ", "dim-label"));
        box.Append(Ui.Label(node.Running ? "⟳" : node.Outcome switch
        {
            TestOutcome.Passed => "✔",
            TestOutcome.Failed => "✘",
            TestOutcome.Skipped => "○",
            _ => "·",
        }, node.Running ? "dim-label" : node.Outcome switch
        {
            TestOutcome.Passed => "test-passed",
            TestOutcome.Failed => "test-failed",
            _ => "dim-label",
        }));
        var label = Ui.Label(node.Label, node.Kind is Kind.Project or Kind.Class ? "variable-name" : null);
        label.Hexpand = true;
        label.Ellipsize = node.Kind == Kind.Class ? Pango.EllipsizeMode.Start : Pango.EllipsizeMode.End;
        label.TooltipText = node.Kind == Kind.Project ? node.Project : node.Result?.FullName ?? node.Filter?.FirstOrDefault();
        box.Append(label);
        box.Append(Ui.Label(info, "dim-label"));
        list.Append(box);
        rows.Add(node);
    }

    void ShowDetail(Node? node)
    {
        links.Clear();
        var buffer = detail.Buffer!;
        var cases = node switch
        {
            { Kind: Kind.Case, Result: { } r } => [r],
            { Kind: Kind.Method } => latest.GetValueOrDefault(node.Filter![0]) ?? [],
            _ => [],
        };
        if (node is null)
        {
            buffer.SetText("", 0);
            return;
        }
        if (cases.Count == 0)
        {
            buffer.SetText(node.Kind switch
            {
                Kind.Method when node.Running => $"{node.Filter![0]}\nRunning…",
                Kind.Method when noResult.Contains(node.Filter![0]) => $"{node.Filter![0]}\nThe last run covered this test but reported no result for it "
                    + "(skipped by the filter, or reported under another name). See Output.",
                Kind.Method => $"{node.Filter![0]}\nNot run yet. ▶ Run runs it; double click opens the source.",
                Kind.Class => $"{node.Label}\n{node.Outcome?.ToString() ?? "Not run yet"}",
                _ => node.Project ?? node.Label,
            }, -1);
            return;
        }
        var text = string.Join("\n\n────────\n\n", cases.Select(t =>
            $"{t.FullName}\n{t.Outcome} in {t.Duration.TotalMilliseconds:0} ms" +
            (t.ErrorMessage is { Length: > 0 } || t.StackTrace is { Length: > 0 } ? $"\n\n{t.ErrorMessage}\n\n{t.StackTrace}" : "")));
        buffer.SetText(text, -1);
        foreach (Match m in StackFrame().Matches(text))
        {
            var group = m.Groups[1];
            var start = group.Index;
            var end = m.Index + m.Length;
            buffer.GetIterAtOffset(out var from, start);
            buffer.GetIterAtOffset(out var to, end);
            buffer.ApplyTag(linkTag, from, to);
            links.Add((start, end, group.Value, int.Parse(m.Groups[2].Value)));
        }
    }

    void OnDetailClick(double x, double y)
    {
        if (detail.Buffer!.GetSelectionBounds(out _, out _)) return;
        detail.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int)x, (int)y, out var bx, out var by);
        if (!detail.GetIterAtLocation(out var it, bx, by)) return;
        var offset = it.GetOffset();
        foreach (var link in links)
            if (offset >= link.Start && offset < link.End && File.Exists(link.Path))
            {
                Navigate?.Invoke(new Location(link.Path, link.Line, 1));
                return;
            }
    }
}
