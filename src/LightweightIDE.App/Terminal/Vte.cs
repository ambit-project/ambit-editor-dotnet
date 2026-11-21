using System.Runtime.InteropServices;

namespace LightweightIDE.App.Terminal;

/// <summary>
/// Minimal binding to VTE for GTK4 (libvte-2.91-gtk4). GirCore has no VTE package, so the few functions the
/// terminal panel needs are called directly and the native widget is wrapped with Gtk.Widget.NewFromPointer.
/// </summary>
internal static partial class Vte
{
    const string Lib = "libvte-2.91-gtk4.so.0";

    [StructLayout(LayoutKind.Sequential)]
    public struct Rgba(double r, double g, double b, double a = 1)
    {
        public float Red = (float)r, Green = (float)g, Blue = (float)b, Alpha = (float)a;
    }

    public delegate void SpawnCallback(IntPtr terminal, int pid, IntPtr error, IntPtr userData);

    [LibraryImport(Lib, EntryPoint = "vte_terminal_new")]
    public static partial IntPtr TerminalNew();

    [LibraryImport(Lib, EntryPoint = "vte_terminal_spawn_async")]
    public static partial void SpawnAsync(IntPtr terminal, int ptyFlags, IntPtr workingDirectory, IntPtr argv, IntPtr envv, int spawnFlags,
        IntPtr childSetup, IntPtr childSetupData, IntPtr childSetupDataDestroy, int timeout, IntPtr cancellable, IntPtr callback, IntPtr userData);

    [LibraryImport(Lib, EntryPoint = "vte_terminal_feed_child", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void FeedChild(IntPtr terminal, string text, nint length);

    [LibraryImport(Lib, EntryPoint = "vte_terminal_set_scrollback_lines")]
    public static partial void SetScrollbackLines(IntPtr terminal, nint lines);

    [LibraryImport(Lib, EntryPoint = "vte_terminal_set_colors")]
    public static partial void SetColors(IntPtr terminal, ref Rgba foreground, ref Rgba background, IntPtr palette, nuint paletteSize);

    [LibraryImport(Lib, EntryPoint = "vte_terminal_set_mouse_autohide")]
    public static partial void SetMouseAutohide(IntPtr terminal, [MarshalAs(UnmanagedType.I4)] bool autohide);

    [LibraryImport(Lib, EntryPoint = "vte_terminal_copy_clipboard_format")]
    public static partial void CopyClipboard(IntPtr terminal, int format);

    [LibraryImport(Lib, EntryPoint = "vte_terminal_paste_clipboard")]
    public static partial void PasteClipboard(IntPtr terminal);

    public static bool IsAvailable()
    {
        try { return NativeLibrary.TryLoad(Lib, out _); }
        catch (Exception) { return false; }
    }

    /// <summary>Builds a NULL-terminated char*[] from strings; free with <see cref="FreeStringArray"/>.</summary>
    public static IntPtr AllocStringArray(IReadOnlyList<string> values)
    {
        var array = Marshal.AllocHGlobal(IntPtr.Size * (values.Count + 1));
        for (var i = 0; i < values.Count; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(values[i]));
        Marshal.WriteIntPtr(array, values.Count * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    public static void FreeStringArray(IntPtr array, int count)
    {
        for (var i = 0; i < count; i++) Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(array, i * IntPtr.Size));
        Marshal.FreeHGlobal(array);
    }
}
