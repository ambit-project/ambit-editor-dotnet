using System.Runtime.InteropServices;
using LightweightIDE.App.Terminal;

namespace LightweightIDE.App.Views;

/// <summary>Integrated terminal (VTE) running the user's shell in the opened folder.</summary>
public sealed class TerminalView
{
    readonly Gtk.Box root = Ui.VBox();
    IntPtr terminal;
    Gtk.Widget? widget;

    public Gtk.Widget Widget => root;

    public TerminalView()
    {
        if (!Vte.IsAvailable())
        {
            var label = Ui.Label("Terminal unavailable: install libvte-2.91-gtk4-0 (sudo apt install libvte-2.91-gtk4-0).", "dim-label", 0.5f);
            label.Vexpand = true;
            root.Append(label);
        }
    }

    public bool IsAvailable => Vte.IsAvailable();

    /// <summary>Starts (or restarts) the shell in <paramref name="directory"/>.</summary>
    public void Start(string directory)
    {
        if (!IsAvailable) return;
        if (widget is not null) root.Remove(widget);

        terminal = Vte.TerminalNew();
        widget = Gtk.Widget.NewFromPointer(terminal, false);
        widget.Hexpand = true;
        widget.Vexpand = true;
        widget.AddCssClass("terminal");
        Vte.SetScrollbackLines(terminal, 10_000);
        Vte.SetMouseAutohide(terminal, true);
        ApplyColors();
        root.Append(widget);

        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s && File.Exists(s) ? s : "/bin/bash";
        var argv = Vte.AllocStringArray([shell]);
        var cwd = Marshal.StringToCoTaskMemUTF8(directory);
        try
        {
            // VTE_PTY_DEFAULT, G_SPAWN_DEFAULT, inherit environment, no timeout, no callback.
            Vte.SpawnAsync(terminal, 0, cwd, argv, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Vte.FreeStringArray(argv, 1);
            Marshal.FreeCoTaskMem(cwd);
        }
    }

    public void ApplyColors()
    {
        if (terminal == IntPtr.Zero) return;
        var dark = Ui.IsDarkTheme();
        var fg = dark ? new Vte.Rgba(0.85, 0.85, 0.85) : new Vte.Rgba(0.15, 0.15, 0.15);
        var bg = dark ? new Vte.Rgba(0.12, 0.12, 0.13) : new Vte.Rgba(1, 1, 1);
        Vte.SetColors(terminal, ref fg, ref bg, IntPtr.Zero, 0);
    }

    /// <summary>Types a command into the shell (as if the user typed it and pressed Enter).</summary>
    public void Run(string command)
    {
        if (terminal == IntPtr.Zero) return;
        Vte.FeedChild(terminal, command + "\n", -1);
        widget?.GrabFocus();
    }

    public void Focus() => widget?.GrabFocus();
}
