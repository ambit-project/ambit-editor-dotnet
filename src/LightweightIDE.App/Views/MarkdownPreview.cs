using System.Diagnostics;
using System.Text;
using LightweightIDE.Editor.Markdown;

namespace LightweightIDE.App.Views;

/// <summary>
/// Native Markdown preview: a read-only GtkTextView with tags (no browser). Local images are shown as pictures,
/// links to files open in the IDE, #anchors scroll the preview, web links open in the default browser.
/// </summary>
public sealed class MarkdownPreview : IDisposable
{
    const int MaxImageWidth = 720;
    readonly Gtk.TextView view = Gtk.TextView.New();
    readonly Gtk.TextBuffer buffer;
    readonly Gtk.ScrolledWindow scrolled;
    readonly Dictionary<string, Gtk.TextTag> tags = new(StringComparer.Ordinal);
    readonly List<(int Start, int End, string Url)> links = [];
    readonly Dictionary<string, int> anchors = new(StringComparer.Ordinal);
    /// <summary>Pictures with their decoded size; resized when the preview width changes.</summary>
    readonly List<(Gtk.Picture Picture, int Width, int Height)> pictures = [];
    string baseDirectory = "/";
    int laidOutWidth;
    bool disposed;

    public Gtk.Widget Widget => scrolled;
    public Gtk.Adjustment VerticalAdjustment => scrolled.GetVadjustment()!;

    /// <summary>A link to a local file (absolute path, without the #fragment).</summary>
    public event Action<string>? OpenFileRequested;

    public MarkdownPreview()
    {
        buffer = view.Buffer!;
        view.Editable = false;
        view.CursorVisible = false;
        view.WrapMode = Gtk.WrapMode.WordChar;
        view.LeftMargin = view.RightMargin = 24;
        view.TopMargin = 16;
        view.PixelsBelowLines = 2;
        view.AddCssClass("markdown-preview");
        scrolled = Ui.Scrolled(view);
        CreateTags();

        var click = Gtk.GestureClick.New();
        click.Button = 1;
        click.OnReleased += (_, e) => OnClick(e.X, e.Y);
        view.AddController(click);

        // GTK 4 has no size-allocate signal: check the width a few times a second (cheap) and refit the pictures.
        Ui.Every(250, () =>
        {
            if (disposed) return false;
            var width = view.GetWidth();
            if (width > 0 && Math.Abs(width - laidOutWidth) > 8)
            {
                laidOutWidth = width;
                foreach (var (picture, w, h) in pictures) Fit(picture, w, h);
            }
            return true;
        });
    }

    public void Dispose() => disposed = true;

    /// <summary>Picture size: its own size, but never wider than the preview's text column.</summary>
    void Fit(Gtk.Picture picture, int width, int height)
    {
        var available = view.GetWidth() > 0 ? Math.Max(120, view.GetWidth() - view.LeftMargin - view.RightMargin - 16) : width;
        var w = Math.Min(width, available);
        picture.SetSizeRequest(w, width == 0 ? 0 : height * w / width);
    }

    // ---------- tags ----------

    static Gdk.RGBA Rgba(string color)
    {
        var rgba = new Gdk.RGBA();
        rgba.Parse(color);
        return rgba;
    }

    Gtk.TextTag Tag(string name, Action<Gtk.TextTag> setup)
    {
        var tag = Gtk.TextTag.New(name);
        setup(tag);
        buffer.TagTable!.Add(tag);
        tags[name] = tag;
        return tag;
    }

    void CreateTags()
    {
        // Neutral translucent colours work on light and dark themes.
        var codeBackground = Rgba("rgba(127,127,127,0.16)");
        Tag("bold", t => t.Weight = 700);
        Tag("italic", t => t.Style = Pango.Style.Italic);
        Tag("strike", t => t.Strikethrough = true);
        Tag("code", t => { t.Family = "monospace"; t.BackgroundRgba = codeBackground; });
        Tag("codeblock", t => { t.Family = "monospace"; t.ParagraphBackgroundRgba = codeBackground; t.LeftMargin = 36; });
        Tag("link", t => { t.ForegroundRgba = Rgba("#3b82f6"); t.Underline = Pango.Underline.Single; });
        Tag("muted", t => t.ForegroundRgba = Rgba("rgba(127,127,127,0.9)"));
        Tag("quote", t => { t.Style = Pango.Style.Italic; t.ForegroundRgba = Rgba("rgba(127,127,127,1)"); });
        Tag("table", t => { t.Family = "monospace"; t.WrapMode = Gtk.WrapMode.None; }); // a wrapped row breaks the grid
        Tag("th", t => t.Weight = 700);
        var scales = new[] { 1.8, 1.45, 1.2, 1.05 };
        for (var i = 0; i < scales.Length; i++)
        {
            var scale = scales[i];
            Tag($"h{i + 1}", t => { t.Scale = scale; t.Weight = 700; t.PixelsAboveLines = 10; t.PixelsBelowLines = 4; });
        }
        for (var level = 1; level <= 8; level++)
        {
            var margin = 24 + level * 22;
            Tag($"indent{level}", t => t.LeftMargin = margin);
        }
    }

    IEnumerable<Gtk.TextTag> TagsFor(MdRun run)
    {
        var s = run.Style;
        if (s.HasFlag(MdStyle.Heading1)) yield return tags["h1"];
        if (s.HasFlag(MdStyle.Heading2)) yield return tags["h2"];
        if (s.HasFlag(MdStyle.Heading3)) yield return tags["h3"];
        if (s.HasFlag(MdStyle.Heading4)) yield return tags["h4"];
        if (s.HasFlag(MdStyle.Bold)) yield return tags["bold"];
        if (s.HasFlag(MdStyle.Italic)) yield return tags["italic"];
        if (s.HasFlag(MdStyle.Strike)) yield return tags["strike"];
        if (s.HasFlag(MdStyle.Code)) yield return tags["code"];
        if (s.HasFlag(MdStyle.CodeBlock)) yield return tags["codeblock"];
        if (s.HasFlag(MdStyle.Link)) yield return tags["link"];
        if (s.HasFlag(MdStyle.Muted)) yield return tags["muted"];
        if (s.HasFlag(MdStyle.Quote)) yield return tags["quote"];
        if (s.HasFlag(MdStyle.Table)) yield return tags["table"];
        if (s.HasFlag(MdStyle.TableHeader)) yield return tags["th"];
        if (run.Indent > 0 && !s.HasFlag(MdStyle.CodeBlock)) yield return tags[$"indent{Math.Min(run.Indent, 8)}"];
    }

    // ---------- rendering ----------

    /// <summary>Re-renders, keeping the scroll position. <paramref name="baseDirectory"/> resolves relative links and images.</summary>
    public void Render(string markdown, string baseDirectory)
    {
        this.baseDirectory = baseDirectory;
        var document = MarkdownLayout.Build(markdown);
        var adjustment = VerticalAdjustment;
        var ratio = adjustment.Upper > adjustment.PageSize ? adjustment.Value / (adjustment.Upper - adjustment.PageSize) : 0;

        buffer.SetText("", 0);
        pictures.Clear();
        links.Clear();
        anchors.Clear();
        var imagesByRun = document.Images.ToLookup(i => i.AfterRun);
        foreach (var image in imagesByRun[-1]) InsertImage(image);
        for (var i = 0; i < document.Runs.Count; i++)
        {
            var run = document.Runs[i];
            buffer.GetEndIter(out var end);
            var start = end.GetOffset();
            buffer.Insert(end, run.Text, -1);
            buffer.GetEndIter(out end);
            var from = IterAt(start);
            foreach (var tag in TagsFor(run)) buffer.ApplyTag(tag, from, end);
            if (run.Url is not null) links.Add((start, end.GetOffset(), run.Url));
            if (run.Style.HasFlag(MdStyle.Heading1) || run.Style.HasFlag(MdStyle.Heading2) || run.Style.HasFlag(MdStyle.Heading3) || run.Style.HasFlag(MdStyle.Heading4))
                anchors.TryAdd(Slug(run.Text), start);
            foreach (var image in imagesByRun[i]) InsertImage(image);
        }

        // Restore the position once the new content is measured.
        Ui.After(30, () =>
        {
            var a = VerticalAdjustment;
            a.Value = ratio * Math.Max(0, a.Upper - a.PageSize);
        });
    }

    Gtk.TextIter IterAt(int offset)
    {
        buffer.GetIterAtOffset(out var it, offset);
        return it;
    }

    void InsertImage(MdImage image)
    {
        buffer.GetEndIter(out var end);
        var path = ResolveLocal(image.Url);
        if (path is null || !File.Exists(path))
        {
            var start = end.GetOffset();
            buffer.Insert(end, $"[image: {(image.Alt.Length > 0 ? image.Alt : image.Url)}]", -1);
            buffer.GetEndIter(out end);
            buffer.ApplyTag(tags["link"], IterAt(start), end);
            links.Add((start, end.GetOffset(), image.Url));
            return;
        }
        try
        {
            // Decode at most MaxImageWidth wide: a README full of 2560 px screenshots would otherwise cost
            // ~16 MB of RAM per image.
            GdkPixbuf.Pixbuf.GetFileInfo(path, out var fileWidth, out _);
            var decodeWidth = fileWidth > 0 ? Math.Min(fileWidth, MaxImageWidth) : MaxImageWidth;
            var pixbuf = GdkPixbuf.Pixbuf.NewFromFileAtScale(path, decodeWidth, -1, true) ?? throw new IOException("Unsupported image.");
            var texture = Gdk.Texture.NewForPixbuf(pixbuf);
            var picture = Gtk.Picture.NewForPaintable(texture);
            picture.CanShrink = true;
            picture.ContentFit = Gtk.ContentFit.Contain;
            pictures.Add((picture, texture.GetWidth(), texture.GetHeight()));
            Fit(picture, texture.GetWidth(), texture.GetHeight());
            picture.TooltipText = image.Alt.Length > 0 ? $"{image.Alt} ({image.Url})" : image.Url;
            var anchor = buffer.CreateChildAnchor(end);
            view.AddChildAtAnchor(picture, anchor);
            buffer.GetEndIter(out end);
            buffer.Insert(end, "\n", -1);
        }
        catch (Exception e) when (e is GLib.GException or IOException)
        {
            buffer.Insert(end, $"[image: {image.Url} cannot be loaded]", -1);
        }
    }

    // ---------- links ----------

    string? ResolveLocal(string url)
    {
        if (url.Contains("://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.Ordinal)) return null;
        var path = Uri.UnescapeDataString(url.Split('#', '?')[0]);
        if (path.Length == 0) return null;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
    }

    void OnClick(double x, double y)
    {
        if (buffer.GetSelectionBounds(out _, out _)) return; // the user is selecting text
        view.WindowToBufferCoords(Gtk.TextWindowType.Widget, (int)x, (int)y, out var bx, out var by);
        if (!view.GetIterAtLocation(out var it, bx, by)) return;
        var offset = it.GetOffset();
        var link = links.FirstOrDefault(l => offset >= l.Start && offset < l.End);
        if (link.Url is not { } url) return;

        if (url.StartsWith('#'))
        {
            if (anchors.TryGetValue(url[1..].ToLowerInvariant(), out var target)) view.ScrollToIter(IterAt(target), 0, true, 0, 0);
            return;
        }
        if (ResolveLocal(url) is { } local)
        {
            if (File.Exists(local)) OpenFileRequested?.Invoke(local);
            else if (Directory.Exists(local)) OpenExternally(local);
            return;
        }
        OpenExternally(url);
    }

    static void OpenExternally(string target)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false })?.Dispose(); }
        catch (Exception) { }
    }

    /// <summary>GitHub-style anchor: lower case, spaces to '-', punctuation removed.</summary>
    public static string Slug(string heading)
    {
        var builder = new StringBuilder();
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') builder.Append(c);
            else if (c == ' ') builder.Append('-');
        }
        return builder.ToString();
    }
}
