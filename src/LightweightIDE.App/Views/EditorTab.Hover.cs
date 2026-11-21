using LightweightIDE.Language.Navigation;

namespace LightweightIDE.App.Views;

/// <summary>
/// Hover popup for C#: quick info, diagnostics or the debugger value, plus links — Go to definition, Implementations,
/// and the related types (a variable's type, a method's return / parameter types, a class's base and interfaces).
/// A GTK tooltip cannot be clicked, so this is a popover that stays while the pointer is on the word or on the popup.
/// </summary>
public sealed partial class EditorTab
{
    Gtk.Popover? hoverPopover;
    (int Start, int End, string Text, HoverTargets? Targets)? hoverShown;
    uint hoverHideTimer;
    bool pointerInHover;

    /// <summary>A link in the hover popup: open this file at this 1-based line and column.</summary>
    public event Action<EditorTab, string, int, int>? NavigateRequested;
    /// <summary>Hover popup → Implementations of the symbol at this character offset.</summary>
    public event Action<EditorTab, int>? ImplementationsRequested;

    void SetUpHover()
    {
        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, e) =>
        {
            if (hoverShown is not { } shown) return;
            // The popover is a child of the view: motion over it reaches this controller too.
            if (PointerOverPopover())
            {
                Ui.Cancel(ref hoverHideTimer);
                return;
            }
            View.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int)e.X, (int)e.Y, out var bx, out var by);
            var onWord = View.GetIterAtLocation(out var it, bx, by) && it.GetOffset() >= shown.Start && it.GetOffset() <= shown.End;
            if (onWord) Ui.Cancel(ref hoverHideTimer);
            else ScheduleHoverHide();
        };
        motion.OnLeave += (_, _) => ScheduleHoverHide();
        View.AddController(motion);

        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, _) =>
        {
            HideHover();
            return false;
        };
        View.AddController(keys);
        var click = Gtk.GestureClick.New();
        // The popover is a child of the view, so presses on its links arrive here too: only clicks on the text hide it.
        click.OnPressed += (_, _) => { if (!PointerOverPopover()) HideHover(); };
        View.AddController(click);
        View.OnMap += (_, _) =>
        {
            if (View.GetVadjustment() is { } adjustment) adjustment.OnValueChanged += (_, _) => HideHover();
        };
    }

    void ShowHover(int start, int end, string text, HoverTargets? targets)
    {
        Ui.Cancel(ref hoverHideTimer);
        if (hoverShown is { } shown && shown.Start == start && shown.End == end && shown.Text == text && ReferenceEquals(shown.Targets, targets)
            && hoverPopover is { Visible: true })
            return;
        HideHover();

        var box = Ui.VBox(6);
        box.MarginTop = box.MarginBottom = 6;
        box.MarginStart = box.MarginEnd = 8;
        if (text.Length > 0)
        {
            var label = Ui.Label(text);
            label.Wrap = true;
            label.MaxWidthChars = 90;
            label.Selectable = false;
            label.AddCssClass("hover-text");
            box.Append(label);
        }
        var links = Ui.HBox(4);
        if (targets?.Definition is { } definition)
            links.Append(Link("Go to definition", $"{targets.Kind} {targets.Name} (F12)", () => NavigateRequested?.Invoke(this, definition.Path, definition.Line, definition.Column)));
        if (targets is { CanHaveImplementations: true, Definition: not null })
            links.Append(Link("Implementations", "Implementations, derived classes, overrides (Ctrl+Alt+B)", () => ImplementationsRequested?.Invoke(this, start)));
        foreach (var link in targets?.Links ?? [])
        {
            var location = link.Location;
            links.Append(Link($"{link.Kind} {link.Name}", $"Go to {link.Kind} {link.Name} ({System.IO.Path.GetFileName(location.Path)}:{location.Line})",
                () => NavigateRequested?.Invoke(this, location.Path, location.Line, location.Column)));
        }
        if (links.GetFirstChild() is not null)
        {
            if (text.Length > 0) box.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
            // Wrap the links when there are many (Gtk.FlowBox keeps them in rows).
            var flow = Gtk.FlowBox.New();
            flow.SelectionMode = Gtk.SelectionMode.None;
            flow.MaxChildrenPerLine = 4;
            var count = 0;
            for (var c = links.GetFirstChild(); c is not null; c = c.GetNextSibling()) count++;
            flow.MinChildrenPerLine = (uint)Math.Min(count, 3);
            flow.ColumnSpacing = 2;
            while (links.GetFirstChild() is { } child)
            {
                links.Remove(child);
                flow.Append(child);
            }
            box.Append(flow);
        }
        if (box.GetFirstChild() is null) return;

        var popover = Gtk.Popover.New();
        popover.SetParent(View);
        popover.Autohide = false; // keep the focus in the editor; hidden by pointer, keys, clicks and scrolling
        popover.HasArrow = false;
        popover.Position = Gtk.PositionType.Bottom;
        popover.AddCssClass("hover-popover");
        popover.SetChild(box);
        var motion = Gtk.EventControllerMotion.New();
        motion.OnEnter += (_, _) =>
        {
            pointerInHover = true;
            Ui.Cancel(ref hoverHideTimer);
        };
        motion.OnLeave += (_, _) =>
        {
            pointerInHover = false;
            ScheduleHoverHide();
        };
        popover.AddController(motion);

        var a = IterAt(start);
        var b = IterAt(end);
        View.GetIterLocation(a, out var first);
        View.GetIterLocation(b, out var last);
        View.BufferToWindowCoords(Gtk.TextWindowType.Widget, first.X, first.Y, out var x, out var y);
        var width = last.Y == first.Y ? Math.Max(1, last.X - first.X) : 1;
        popover.SetPointingTo(new Gdk.Rectangle { X = x, Y = y, Width = width, Height = first.Height });
        hoverPopover = popover;
        hoverShown = (start, end, text, targets);
        popover.Popup();

        Gtk.Button Link(string label, string tooltip, Action action)
        {
            var button = Gtk.Button.NewWithLabel(label);
            button.AddCssClass("flat");
            button.AddCssClass("hover-link");
            button.TooltipText = tooltip;
            button.OnClicked += (_, _) =>
            {
                HideHover();
                action();
            };
            return button;
        }
    }

    void ScheduleHoverHide()
    {
        if (hoverPopover is null || hoverHideTimer != 0) return;
        hoverHideTimer = Ui.After(350, () =>
        {
            hoverHideTimer = 0;
            if (!pointerInHover && !PointerOverPopover()) HideHover();
        });
    }

    /// <summary>
    /// Whether the pointer is over the hover popup. Asked from GDK (position relative to the popup's own surface):
    /// on X11 the popup is a separate surface and neither the view nor the popup reliably gets motion / crossing
    /// events when the pointer moves between them.
    /// </summary>
    bool PointerOverPopover()
    {
        if (hoverPopover is not { Visible: true } popover || popover.GetNative()?.GetSurface() is not { } surface) return false;
        if (View.GetDisplay().GetDefaultSeat()?.GetPointer() is not { } pointer) return false;
        // GirCore 0.8 has no public wrapper for gdk_surface_get_device_position; the generated internal binding works.
        if (!Gdk.Internal.Surface.GetDevicePosition(surface.Handle.DangerousGetHandle(), pointer.Handle.DangerousGetHandle(), out var x, out var y, out _))
            return false;
        return x >= -4 && y >= -8 && x <= surface.GetWidth() + 4 && y <= surface.GetHeight() + 4;
    }

    void HideHover()
    {
        Ui.Cancel(ref hoverHideTimer);
        pointerInHover = false;
        hoverShown = null;
        if (hoverPopover is not { } popover) return;
        hoverPopover = null;
        popover.Popdown();
        popover.Unparent();
    }
}
