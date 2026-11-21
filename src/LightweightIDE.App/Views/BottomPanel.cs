using System.Collections.Concurrent;
using System.Text;
using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.Testing;

namespace LightweightIDE.App.Views;

/// <summary>A navigable result: file + 1-based line/column.</summary>
public sealed record Location(string Path, int Line, int Column, int Length = 0);

/// <summary>Problems, Output, Terminal, Test Results and Find Results.</summary>
public sealed class BottomPanel
{
    public const int ProblemsPage = 0, OutputPage = 1, TerminalPage = 2, TestsPage = 3, ResultsPage = 4, DebugPage = 5, HttpPage = 6;
    const int MaxRows = 3000;
    const int MaxOutputLines = 20_000;

    readonly Gtk.Notebook notebook = Gtk.Notebook.New();
    /// <summary>Folder results are shown relative to.</summary>
    public string Root { get; set; } = "/";

    // Problems
    readonly Gtk.ListBox problemList = Gtk.ListBox.New();
    readonly Gtk.Label problemsTab = Ui.Label("Problems");
    readonly Gtk.ToggleButton showErrors = Gtk.ToggleButton.NewWithLabel("Errors");
    readonly Gtk.ToggleButton showWarnings = Gtk.ToggleButton.NewWithLabel("Warnings");
    readonly Gtk.ToggleButton showInfo = Gtk.ToggleButton.NewWithLabel("Info");
    readonly Gtk.ToggleButton currentFileOnly = Gtk.ToggleButton.NewWithLabel("Current file");
    List<BuildDiagnostic> buildDiagnostics = [];
    readonly Dictionary<string, IReadOnlyList<BuildDiagnostic>> liveDiagnostics = new(StringComparer.Ordinal);
    List<BuildDiagnostic> shownProblems = [];
    string? currentFile;

    // Output
    readonly Gtk.TextView output = Gtk.TextView.New();
    readonly ConcurrentQueue<string> pendingOutput = new();
    int outputLines;

    // Tests

    // Results
    readonly Gtk.ListBox resultList = Gtk.ListBox.New();
    readonly Gtk.Label resultTitle = Ui.Label("", "dim-label");
    List<Location> shownResults = [];

    public TerminalView Terminal { get; } = new();
    public DebugPanel Debug { get; } = new();
    public TestResultsView Tests { get; } = new();
    public HttpPanel Http { get; } = new();
    public Gtk.Widget Widget => notebook;
    public event Action<Location>? Navigate;

    public BottomPanel()
    {
        notebook.AddCssClass("bottom-panel");
        notebook.AppendPage(BuildProblems(), problemsTab);
        notebook.AppendPage(BuildOutput(), Ui.Label("Output"));
        notebook.AppendPage(Terminal.Widget, Ui.Label("Terminal"));
        notebook.AppendPage(BuildTests(), Tests.TabLabel);
        notebook.AppendPage(BuildResults(), Ui.Label("Find Results"));
        notebook.AppendPage(Debug.Widget, Ui.Label("Debug"));
        notebook.AppendPage(Http.Widget, Ui.Label("HTTP"));

        // Output is appended in batches on the UI thread.
        Ui.Every(100, () =>
        {
            FlushOutput();
            return true;
        });
    }

    public void Show(int page) => notebook.SetCurrentPage(page);
    public int CurrentPage => notebook.GetCurrentPage();

    // ---------- problems ----------

    Gtk.Widget BuildProblems()
    {
        var box = Ui.VBox();
        var bar = Ui.HBox(4);
        bar.AddCssClass("panel-toolbar");
        foreach (var t in new[] { showErrors, showWarnings, showInfo, currentFileOnly })
        {
            t.AddCssClass("flat");
            t.OnToggled += (_, _) => RenderProblems();
            bar.Append(t);
        }
        showErrors.Active = showWarnings.Active = true;
        box.Append(bar);
        problemList.AddCssClass("result-list");
        problemList.OnRowActivated += (_, e) =>
        {
            var d = shownProblems.ElementAtOrDefault(e.Row.GetIndex());
            if (d?.FilePath is not null && d.Line > 0) Navigate?.Invoke(new Location(d.FilePath, d.Line, d.Column));
        };
        box.Append(Ui.Scrolled(problemList));
        return box;
    }

    public void SetBuildDiagnostics(IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        buildDiagnostics = diagnostics.ToList();
        RenderProblems();
    }

    public void SetLiveDiagnostics(string path, IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) liveDiagnostics.Remove(path);
        else liveDiagnostics[path] = diagnostics;
        RenderProblems();
    }

    public void ClearLive(string path)
    {
        if (liveDiagnostics.Remove(path)) RenderProblems();
    }

    public void SetCurrentFile(string? path)
    {
        currentFile = path;
        if (currentFileOnly.Active) RenderProblems();
    }

    /// <summary>All problems: live analysis for open files replaces build results for those files.</summary>
    public IReadOnlyList<BuildDiagnostic> AllProblems() =>
        buildDiagnostics.Where(d => d.FilePath is null || !liveDiagnostics.ContainsKey(d.FilePath))
            .Concat(liveDiagnostics.Values.SelectMany(v => v))
            .OrderBy(d => d.Severity)
            .ThenBy(d => d.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Line)
            .ToList();

    void RenderProblems()
    {
        var all = AllProblems();
        var errors = all.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = all.Count(d => d.Severity == DiagnosticSeverity.Warning);
        problemsTab.SetText(errors + warnings == 0 ? "Problems" : $"Problems ({errors} / {warnings})");
        showErrors.Label = $"Errors {errors}";
        showWarnings.Label = $"Warnings {warnings}";
        showInfo.Label = $"Info {all.Count - errors - warnings}";

        shownProblems = all.Where(d => d.Severity switch
            {
                DiagnosticSeverity.Error => showErrors.Active,
                DiagnosticSeverity.Warning => showWarnings.Active,
                _ => showInfo.Active,
            })
            .Where(d => !currentFileOnly.Active || d.FilePath == currentFile)
            .Take(MaxRows)
            .ToList();
        problemList.RemoveAll();
        foreach (var d in shownProblems)
        {
            var row = Ui.HBox(8);
            row.Append(Gtk.Image.NewFromIconName(d.Severity switch
            {
                DiagnosticSeverity.Error => "dialog-error-symbolic",
                DiagnosticSeverity.Warning => "dialog-warning-symbolic",
                _ => "dialog-information-symbolic",
            }));
            var message = Ui.Label(d.Message);
            message.Hexpand = true;
            message.Ellipsize = Pango.EllipsizeMode.End;
            message.TooltipText = d.Message;
            row.Append(message);
            row.Append(Ui.Label(d.Code, "dim-label"));
            var where = d.FilePath is null ? d.Project ?? "" : $"{Relative(d.FilePath)}:{d.Line}";
            row.Append(Ui.Label(where, "dim-label"));
            if (d.Source == DiagnosticSource.Live) row.Append(Ui.Label("live", "badge"));
            problemList.Append(row);
        }
    }

    /// <summary>Next problem after the given location (F8).</summary>
    public Location? NextProblem(string? path, int line)
    {
        var withLocation = AllProblems().Where(d => d.FilePath is not null && d.Line > 0 && d.Severity != DiagnosticSeverity.Info).ToList();
        if (withLocation.Count == 0) return null;
        var next = withLocation.FirstOrDefault(d => string.CompareOrdinal(d.FilePath, path) > 0 || (d.FilePath == path && d.Line > line))
                   ?? withLocation[0];
        return new Location(next.FilePath!, next.Line, next.Column);
    }

    // ---------- output ----------

    Gtk.Widget BuildOutput()
    {
        output.Editable = false;
        output.Monospace = true;
        output.CursorVisible = false;
        output.AddCssClass("output");
        var box = Ui.VBox();
        var bar = Ui.HBox(4);
        bar.AddCssClass("panel-toolbar");
        var clear = Ui.IconButton("edit-clear-all-symbolic", "Clear output");
        clear.OnClicked += (_, _) => ClearOutput();
        bar.Append(clear);
        box.Append(bar);
        box.Append(Ui.Scrolled(output));
        var click = Gtk.GestureClick.New();
        click.OnPressed += (_, e) =>
        {
            if (e.NPress == 2) NavigateFromOutputLine(e.X, e.Y);
        };
        output.AddController(click);
        return box;
    }

    /// <summary>Thread-safe: queue a line for the Output panel.</summary>
    public void AppendOutput(string line) => pendingOutput.Enqueue(line);

    public void ClearOutput()
    {
        pendingOutput.Clear();
        output.Buffer!.SetText("", 0);
        outputLines = 0;
    }

    void FlushOutput()
    {
        if (pendingOutput.IsEmpty) return;
        var sb = new StringBuilder();
        var n = 0;
        while (n < 5000 && pendingOutput.TryDequeue(out var line))
        {
            sb.Append(line).Append('\n');
            n++;
        }
        var buffer = output.Buffer!;
        var end = new Gtk.TextIter();
        buffer.GetEndIter(out end);
        buffer.Insert(end, sb.ToString(), -1);
        outputLines += n;
        if (outputLines > MaxOutputLines)
        {
            var start = new Gtk.TextIter();
            var cut = new Gtk.TextIter();
            buffer.GetStartIter(out start);
            buffer.GetIterAtLine(out cut, outputLines - MaxOutputLines);
            buffer.Delete(start, cut);
            outputLines = MaxOutputLines;
        }
        buffer.GetEndIter(out end);
        var mark = buffer.CreateMark(null, end, false);
        output.ScrollToMark(mark, 0, false, 0, 1);
        buffer.DeleteMark(mark);
    }

    void NavigateFromOutputLine(double x, double y)
    {
        output.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int)x, (int)y, out var bx, out var by);
        var it = new Gtk.TextIter();
        if (!output.GetIterAtLocation(out it, bx, by)) return;
        var start = it.Copy();
        start.SetLineOffset(0);
        var end = start.Copy();
        end.ForwardToLineEnd();
        var line = output.Buffer!.GetText(start, end, false);
        if (MsBuildOutputParser.TryParse(line) is { FilePath: not null, Line: > 0 } d)
            Navigate?.Invoke(new Location(d.FilePath, d.Line, d.Column));
    }

    // ---------- tests ----------

    Gtk.Widget BuildTests()
    {
        Tests.Navigate += l => Navigate?.Invoke(l);
        return Tests.Widget;
    }

    public void SetTestsRunning(string what, IReadOnlyList<(string Target, IReadOnlyList<string>? Filter)>? runs = null) => Tests.SetRunning(what, runs);

    public void SetTestResults(TestRunResult result) => Tests.SetResults(result);
    public void SetDiscoveredTests(IReadOnlyList<LightweightIDE.Language.Testing.DiscoveredTest> tests) => Tests.SetDiscovered(tests);

    // ---------- find results ----------

    Gtk.Widget BuildResults()
    {
        var box = Ui.VBox();
        resultTitle.AddCssClass("panel-toolbar");
        box.Append(resultTitle);
        resultList.AddCssClass("result-list");
        resultList.OnRowActivated += (_, e) =>
        {
            if (shownResults.ElementAtOrDefault(e.Row.GetIndex()) is { } l) Navigate?.Invoke(l);
        };
        box.Append(Ui.Scrolled(resultList));
        return box;
    }

    public void SetResults(string title, IReadOnlyList<(Location Location, string Text)> results)
    {
        resultTitle.SetText(title);
        resultList.RemoveAll();
        shownResults = results.Take(MaxRows).Select(r => r.Location).ToList();
        foreach (var (location, text) in results.Take(MaxRows))
        {
            var row = Ui.HBox(8);
            row.Append(Ui.Label($"{Relative(location.Path)}:{location.Line}", "result-location"));
            var t = Ui.Label(text);
            t.Hexpand = true;
            t.Ellipsize = Pango.EllipsizeMode.End;
            row.Append(t);
            resultList.Append(row);
        }
        Show(ResultsPage);
    }

    string Relative(string path) => path.StartsWith(Root, StringComparison.Ordinal) ? Path.GetRelativePath(Root, path) : path;
}
