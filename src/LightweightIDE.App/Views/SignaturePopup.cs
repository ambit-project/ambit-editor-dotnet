using LightweightIDE.Language.Completion;

namespace LightweightIDE.App.Views;

/// <summary>Parameter info above the caret: the overload, the active parameter in bold, its documentation, "1/3".</summary>
public sealed class SignaturePopup
{
    readonly Gtk.Popover popover = Gtk.Popover.New();
    readonly Gtk.Label label = Gtk.Label.New("");
    SignatureHelpResult? result;
    int shown;

    public bool IsVisible => popover.Visible;
    public int ArgumentListStart => result?.ArgumentListStart ?? -1;
    public bool HasOverloads => result?.Signatures.Count > 1;

    public SignaturePopup(Gtk.Widget parent)
    {
        popover.SetParent(parent);
        popover.Autohide = false;
        popover.HasArrow = false;
        popover.CanFocus = false;
        popover.Position = Gtk.PositionType.Top;
        popover.AddCssClass("signature-help");
        label.Xalign = 0;
        label.Wrap = true;
        label.MaxWidthChars = 90;
        label.MarginStart = label.MarginEnd = 6;
        popover.SetChild(label);
    }

    public void Show(SignatureHelpResult help, Gdk.Rectangle caret)
    {
        // Keep the overload the user chose with Up/Down while typing in the same argument list.
        var keep = result is not null && result.ArgumentListStart == help.ArgumentListStart && result.Signatures.Count == help.Signatures.Count;
        result = help;
        shown = keep ? shown : help.ActiveSignature;
        Render();
        popover.SetPointingTo(caret);
        if (!popover.Visible) popover.Popup();
    }

    public void Cycle(int delta)
    {
        if (result is null || result.Signatures.Count < 2) return;
        shown = ((shown + delta) % result.Signatures.Count + result.Signatures.Count) % result.Signatures.Count;
        Render();
    }

    public void Hide()
    {
        result = null;
        if (popover.Visible) popover.Popdown();
    }

    void Render()
    {
        if (result is null) return;
        var s = result.Signatures[shown];
        var parameters = s.Parameters.Select((p, i) => i == result.ActiveParameter ? $"<b>{Ui.Escape(p)}</b>" : Ui.Escape(p));
        var text = $"{Ui.Escape(s.Prefix)}{string.Join(", ", parameters)}{Ui.Escape(s.Suffix)}";
        if (result.Signatures.Count > 1) text = $"<span alpha=\"60%\">{shown + 1}/{result.Signatures.Count} ↑↓</span>  " + text;
        if (s.Documentation is { Length: > 0 } doc) text += $"\n<span size=\"small\">{Ui.Escape(doc)}</span>";
        if (result.ActiveParameter >= 0 && result.ActiveParameter < s.ParameterDocumentation.Count && s.ParameterDocumentation[result.ActiveParameter] is { Length: > 0 } pd)
            text += $"\n<span size=\"small\"><b>{Ui.Escape(s.Parameters[result.ActiveParameter].Split(' ').Last())}</b>: {Ui.Escape(pd)}</span>";
        label.SetMarkup(text);
    }
}
