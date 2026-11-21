using LightweightIDE.DotNet.Testing;
using LightweightIDE.Language.Testing;

namespace LightweightIDE.App.Views;

/// <summary>▶ in the gutter next to test classes and methods, coloured by the last result of that test.</summary>
public sealed partial class EditorTab
{
    const string TestRunCategory = "test-run", TestPassedCategory = "test-passed", TestFailedCategory = "test-failed";
    readonly List<GtkSource.Mark> testMarks = [];
    IReadOnlyList<TestMark> tests = [];
    uint testTimer;
    int testRequest;

    /// <summary>Run (false) or debug (true) a test name ("Ns.Class.Method" / "Ns.Class.*") from the gutter.</summary>
    public event Action<EditorTab, string, bool>? TestRunRequested;

    /// <summary>Last known outcome of a test method by its name without arguments; set by the window.</summary>
    public Func<string, TestOutcome?>? TestOutcomeLookup { get; set; }

    void SetUpTestMarks()
    {
        static GtkSource.MarkAttributes Attributes(GdkPixbuf.Pixbuf? icon)
        {
            var attributes = GtkSource.MarkAttributes.New();
            if (icon is not null) attributes.SetPixbuf(icon);
            return attributes;
        }
        // Below breakpoints (10): a breakpoint on a test line keeps its red dot, the click still opens the menu.
        View.SetMarkAttributes(TestRunCategory, Attributes(DebugIcons.TestRunIcon), 5);
        View.SetMarkAttributes(TestPassedCategory, Attributes(DebugIcons.TestPassedIcon), 5);
        View.SetMarkAttributes(TestFailedCategory, Attributes(DebugIcons.TestFailedIcon), 5);
        View.ShowLineMarks = true;
        ScheduleTestMarks(0);
    }

    void ScheduleTestMarks(int delay = 400)
    {
        Ui.Cancel(ref testTimer);
        testTimer = Ui.After(delay, () =>
        {
            testTimer = 0;
            _ = UpdateTestMarksAsync();
        });
    }

    async Task UpdateTestMarksAsync()
    {
        var request = ++testRequest;
        var text = GetText();
        var found = await Task.Run(() => TestDiscovery.Marks(text));
        if (request != testRequest) return;
        tests = found;
        RefreshTestMarks();
    }

    /// <summary>Redraws the marks with the current outcomes (after a test run).</summary>
    public void RefreshTestMarks()
    {
        foreach (var mark in testMarks) if (!mark.GetDeleted()) Buffer.DeleteMark(mark);
        testMarks.Clear();
        var lineCount = Buffer.GetLineCount();
        foreach (var test in tests.Where(t => t.Line <= lineCount))
        {
            Buffer.GetIterAtLine(out var it, test.Line - 1);
            var category = OutcomeOf(test) switch
            {
                TestOutcome.Failed => TestFailedCategory,
                TestOutcome.Passed => TestPassedCategory,
                _ => TestRunCategory,
            };
            testMarks.Add(Buffer.CreateSourceMark(null, category, it));
        }
    }

    /// <summary>A method: its last outcome. A class: failed if any of its methods failed, passed if all passed.</summary>
    TestOutcome? OutcomeOf(TestMark test)
    {
        if (TestOutcomeLookup is not { } lookup) return null;
        if (!test.IsClass) return lookup(test.Name);
        var outcomes = tests.Where(t => t.Class == test.Name).Select(t => lookup(t.Name)).ToList();
        if (outcomes.Any(o => o == TestOutcome.Failed)) return TestOutcome.Failed;
        return outcomes.Count > 0 && outcomes.All(o => o is TestOutcome.Passed or TestOutcome.Skipped) && outcomes.Any(o => o == TestOutcome.Passed)
            ? TestOutcome.Passed : null;
    }

    /// <summary>The test whose mark is on this 1-based line, read from the mark positions (they move with edits).</summary>
    TestMark? TestMarkAt(int line)
    {
        for (var i = 0; i < testMarks.Count && i < tests.Count; i++)
        {
            if (testMarks[i].GetDeleted()) continue;
            Buffer.GetIterAtMark(out var it, testMarks[i]);
            if (it.GetLine() + 1 == line) return tests[i];
        }
        return null;
    }

    void ShowTestMenu(TestMark test, Gtk.TextIter at)
    {
        var popover = Gtk.Popover.New();
        popover.SetParent(View);
        popover.HasArrow = false;
        popover.Position = Gtk.PositionType.Right; // to the right of the ▶, over the text, never over the explorer
        var box = Ui.VBox(0);
        var what = test.IsClass ? $"tests in {test.ShortName}" : $"'{test.ShortName}'";
        var outcome = OutcomeOf(test);
        if (outcome is TestOutcome.Failed or TestOutcome.Passed)
        {
            var state = Ui.Label(outcome == TestOutcome.Failed ? "Last run: failed" : "Last run: passed", "dim-label");
            state.MarginStart = state.MarginEnd = 10;
            state.MarginTop = state.MarginBottom = 4;
            box.Append(state);
        }
        var line = at.GetLine() + 1;
        Item($"▶  Run {what}", () => TestRunRequested?.Invoke(this, test.Name, false));
        Item($"🐞  Debug {what}", () => TestRunRequested?.Invoke(this, test.Name, true));
        Item("●  Toggle Breakpoint", () => BreakpointToggleRequested?.Invoke(this, line));
        popover.SetChild(box);

        View.GetIterLocation(at, out var rect);
        View.BufferToWindowCoords(Gtk.TextWindowType.Widget, rect.X, rect.Y, out _, out var y);
        popover.SetPointingTo(new Gdk.Rectangle { X = 18, Y = y, Width = 1, Height = rect.Height });
        popover.OnClosed += (_, _) => Ui.Post(popover.Unparent);
        popover.Popup();

        void Item(string label, Action action)
        {
            var button = Gtk.Button.NewWithLabel(label);
            button.AddCssClass("flat");
            ((Gtk.Label)button.GetChild()!).Xalign = 0;
            button.OnClicked += (_, _) =>
            {
                popover.Popdown();
                action();
            };
            box.Append(button);
        }
    }
}
