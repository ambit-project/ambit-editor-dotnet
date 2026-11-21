using LightweightIDE.Editor;

namespace LightweightIDE.App.Views;

/// <summary>
/// Right click on a breakpoint (or Ctrl+Shift+F8): condition, hit count rule and log message. Saving with everything
/// empty leaves a plain breakpoint.
/// </summary>
public sealed class BreakpointDialog : FormDialog
{
    static readonly string[] HitLabels = ["Always", "When the hit count is", "When the hit count is at least", "When the hit count is a multiple of"];

    readonly Gtk.Entry condition = Gtk.Entry.New();
    readonly Gtk.DropDown hitRule = Strings(HitLabels);
    readonly Gtk.SpinButton hitCount = Gtk.SpinButton.NewWithRange(1, 1_000_000, 1);
    readonly Gtk.Entry logMessage = Gtk.Entry.New();
    readonly Action<BreakpointOptions> saved;

    public BreakpointDialog(Gtk.Window parent, string file, int line, BreakpointOptions current, bool conditionsByDebugger, Action<BreakpointOptions> saved)
        : base(parent, $"Breakpoint — {file}:{line}", "Save")
    {
        this.saved = saved;
        condition.PlaceholderText = "C# expression, e.g. order.Total > 100 (empty: always)";
        condition.SetText(current.Condition ?? "");
        hitRule.Selected = (uint)current.Hit;
        hitCount.Value = Math.Max(1, current.HitCount);
        logMessage.PlaceholderText = "e.g. total = {order.Total} (empty: pause)";
        logMessage.SetText(current.LogMessage ?? "");

        var hit = Ui.HBox(8);
        hitRule.Hexpand = true;
        hit.Append(hitRule);
        hit.Append(hitCount);
        UpdateHitCount();
        hitRule.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") UpdateHitCount(); };

        AddRow("Condition", condition, conditionsByDebugger
            ? "Evaluated by the debugger in the breakpoint's scope; the program pauses only when it is true."
            : "Evaluated by the IDE when the breakpoint is hit.");
        AddRow("Hit count", hit, "Counted by the IDE in this debugging session, after the condition.");
        AddRow("Log message", logMessage,
            "A logpoint: writes the message to Output and continues instead of pausing. {expression} is replaced by its value.");
        var hint = Ui.Label("Expressions in {braces} are evaluated where the breakpoint is hit; {{ and }} are literal braces.", "dim-label");
        hint.Wrap = true;
        AddWide(hint);
        condition.OnActivate += (_, _) => Ok.Activate();
        logMessage.OnActivate += (_, _) => Ok.Activate();
    }

    void UpdateHitCount() => hitCount.Sensitive = hitRule.Selected != 0;

    protected override string? Validate() =>
        hitRule.Selected != 0 && hitCount.Value < 1 ? "The hit count must be at least 1." : null;

    protected override Task RunAsync(CancellationToken ct)
    {
        saved(new BreakpointOptions(
            string.IsNullOrWhiteSpace(condition.GetText()) ? null : condition.GetText().Trim(),
            (HitRule)hitRule.Selected,
            (int)hitCount.Value,
            string.IsNullOrWhiteSpace(logMessage.GetText()) ? null : logMessage.GetText().Trim()));
        return Task.CompletedTask;
    }
}
