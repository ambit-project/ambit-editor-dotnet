namespace LightweightIDE.App.Views;

/// <summary>Small GTK helpers. Everything here must be called on the GTK main thread unless stated otherwise.</summary>
public static class Ui
{
    /// <summary>Queues an action on the GTK main loop. Safe to call from any thread.</summary>
    public static void Post(Action action) =>
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
        {
            action();
            return false;
        });

    /// <summary>Runs an action once after a delay on the main loop; returns the source id.</summary>
    public static uint After(int milliseconds, Action action) =>
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, (uint)milliseconds, () =>
        {
            action();
            return false;
        });

    public static uint Every(int milliseconds, Func<bool> tick) =>
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, (uint)milliseconds, () => tick());

    public static void Cancel(ref uint sourceId)
    {
        if (sourceId != 0) GLib.Functions.SourceRemove(sourceId);
        sourceId = 0;
    }

    public static Gtk.Label Label(string text, string? cssClass = null, float xalign = 0)
    {
        var label = Gtk.Label.New(text);
        label.Xalign = xalign;
        if (cssClass is not null) label.AddCssClass(cssClass);
        return label;
    }

    public static Gtk.Button IconButton(string icon, string tooltip, string? action = null)
    {
        var b = Gtk.Button.NewFromIconName(icon);
        b.TooltipText = tooltip;
        b.AddCssClass("flat");
        if (action is not null) b.ActionName = action;
        return b;
    }

    public static Gtk.Button TextButton(string text, string tooltip, string? action = null)
    {
        var b = Gtk.Button.NewWithLabel(text);
        b.TooltipText = tooltip;
        b.AddCssClass("flat");
        if (action is not null) b.ActionName = action;
        return b;
    }

    public static Gtk.ScrolledWindow Scrolled(Gtk.Widget child)
    {
        var s = Gtk.ScrolledWindow.New();
        s.SetChild(child);
        s.Hexpand = true;
        s.Vexpand = true;
        return s;
    }

    public static Gtk.Box HBox(int spacing = 0) => Gtk.Box.New(Gtk.Orientation.Horizontal, spacing);
    public static Gtk.Box VBox(int spacing = 0) => Gtk.Box.New(Gtk.Orientation.Vertical, spacing);

    /// <summary>Asks a question with buttons; returns the index of the chosen button (cancel = cancelIndex).</summary>
    public static async Task<int> ChooseAsync(Gtk.Window parent, string message, string detail, string[] buttons, int cancelIndex, int defaultIndex = 0)
    {
        var dialog = Gtk.AlertDialog.NewWithProperties([]);
        dialog.Message = message;
        dialog.Detail = detail;
        dialog.Buttons = buttons;
        dialog.CancelButton = cancelIndex;
        dialog.DefaultButton = defaultIndex;
        dialog.Modal = true;
        try { return await dialog.ChooseAsync(parent); }
        catch (GLib.GException) { return cancelIndex; }
    }

    public static async Task ShowErrorAsync(Gtk.Window parent, string message, string detail) =>
        await ChooseAsync(parent, message, detail, ["Close"], 0);

    /// <summary>Single-line text prompt (rename, new file, go to line). Returns null when cancelled.</summary>
    public static Task<string?> PromptAsync(Gtk.Window parent, string title, string label, string initial, Func<string, string?>? validate = null)
    {
        var tcs = new TaskCompletionSource<string?>();
        var window = Gtk.Window.New();
        window.Title = title;
        window.SetTransientFor(parent);
        window.Modal = true;
        window.Resizable = false;
        window.SetDefaultSize(420, -1);
        window.AddCssClass("prompt");

        var box = VBox(6);
        box.MarginTop = box.MarginBottom = box.MarginStart = box.MarginEnd = 12;
        box.Append(Label(label));
        var entry = Gtk.Entry.New();
        entry.SetText(initial);
        box.Append(entry);
        var error = Label("", "error");
        error.Visible = false;
        box.Append(error);
        var buttons = HBox(6);
        buttons.Halign = Gtk.Align.End;
        var cancel = Gtk.Button.NewWithLabel("Cancel");
        var ok = Gtk.Button.NewWithLabel("OK");
        ok.AddCssClass("suggested-action");
        buttons.Append(cancel);
        buttons.Append(ok);
        box.Append(buttons);
        window.SetChild(box);

        void Accept()
        {
            var text = entry.GetText();
            if (validate?.Invoke(text) is { } message)
            {
                error.SetText(message);
                error.Visible = true;
                return;
            }
            tcs.TrySetResult(text);
            window.Close();
        }
        ok.OnClicked += (_, _) => Accept();
        entry.OnActivate += (_, _) => Accept();
        cancel.OnClicked += (_, _) => window.Close();
        window.OnCloseRequest += (_, _) =>
        {
            tcs.TrySetResult(null);
            return false;
        };
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != Gdk.Constants.KEY_Escape) return false;
            window.Close();
            return true;
        };
        window.AddController(keys);
        window.Present();
        entry.GrabFocus();
        // select the file name without extension
        var dot = initial.LastIndexOf('.');
        entry.SelectRegion(0, dot > 0 ? dot : initial.Length);
        return tcs.Task;
    }

    public static bool IsDarkTheme()
    {
        var settings = Gtk.Settings.GetDefault();
        if (settings is null) return false;
        return settings.GtkApplicationPreferDarkTheme || (settings.GtkThemeName?.Contains("dark", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static string Escape(string s) => GLib.Functions.MarkupEscapeText(s, -1);
}
