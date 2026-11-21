using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LightweightIDE.Editor.Markdown;

[Flags]
public enum MdStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Code = 8,
    Link = 16,
    Heading1 = 32,
    Heading2 = 64,
    Heading3 = 128,
    Heading4 = 256,
    CodeBlock = 512,
    Quote = 1024,
    Muted = 2048,
    Table = 4096,
    TableHeader = 8192,
}

/// <summary>A piece of text with one style. <see cref="Indent"/> is the list / quote nesting level.</summary>
public sealed record MdRun(string Text, MdStyle Style, int Indent = 0, string? Url = null);

/// <summary>A local image to show between runs (inserted after the run with index <see cref="AfterRun"/>).</summary>
public sealed record MdImage(int AfterRun, string Url, string Alt);

/// <summary>The preview as styled text: what the GTK text view renders. Built without GTK so it can be tested.</summary>
public sealed record MdDocument(IReadOnlyList<MdRun> Runs, IReadOnlyList<MdImage> Images)
{
    public string PlainText => string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// Turns Markdown (CommonMark + pipe tables, task lists, strikethrough, autolinks) into styled runs. The preview is
/// a native text view, not a browser: raw HTML is shown as code, tables are laid out in a monospace grid.
/// </summary>
public static partial class MarkdownLayout
{
    [GeneratedRegex("""<img\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();

    [GeneratedRegex("""\b(src|alt)\s*=\s*("([^"]*)"|'([^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex Attribute();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    /// <summary>src and alt of every &lt;img&gt; in an HTML fragment (README headers with a logo and badges).</summary>
    public static IEnumerable<(string Src, string Alt)> Images(string html)
    {
        foreach (Match img in ImgTag().Matches(html))
        {
            string? src = null, alt = null;
            foreach (Match a in Attribute().Matches(img.Value))
            {
                var value = a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                if (a.Groups[1].Value.Equals("src", StringComparison.OrdinalIgnoreCase)) src = value;
                else alt = value;
            }
            if (src is not null) yield return (src, alt ?? "");
        }
    }

    /// <summary>The text of an HTML fragment without its tags.</summary>
    public static string StripTags(string html) => System.Net.WebUtility.HtmlDecode(AnyTag().Replace(html, "")).Trim();

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    public static MdDocument Build(string markdown)
    {
        var writer = new Writer();
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        writer.Blocks(document, 0);
        return writer.Finish();
    }

    sealed class Writer
    {
        readonly List<MdRun> runs = [];
        readonly List<MdImage> images = [];

        public MdDocument Finish()
        {
            // Trim the trailing blank lines.
            while (runs.Count > 0 && runs[^1].Text.Trim('\n').Length == 0) runs.RemoveAt(runs.Count - 1);
            if (runs.Count > 0 && runs[^1].Text.EndsWith('\n')) runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd('\n') + "\n" };
            return new MdDocument(runs, images);
        }

        void Add(string text, MdStyle style, int indent, string? url = null)
        {
            if (text.Length == 0) return;
            // Merge with the previous run when nothing changes (fewer GTK tags).
            if (runs.Count > 0 && runs[^1].Style == style && runs[^1].Indent == indent && runs[^1].Url == url)
                runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            else runs.Add(new MdRun(text, style, indent, url));
        }

        void EndBlock(int indent, bool tight = false) => Add(tight ? "\n" : "\n\n", MdStyle.None, indent);

        public void Blocks(ContainerBlock container, int indent, MdStyle inherited = MdStyle.None, bool tight = false)
        {
            foreach (var block in container) Block(block, indent, inherited, tight);
        }

        void Block(Block block, int indent, MdStyle inherited, bool tight)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var level = heading.Level switch { 1 => MdStyle.Heading1, 2 => MdStyle.Heading2, 3 => MdStyle.Heading3, _ => MdStyle.Heading4 };
                    Inlines(heading.Inline, indent, inherited | level);
                    EndBlock(indent);
                    break;
                case ParagraphBlock paragraph:
                    Inlines(paragraph.Inline, indent, inherited);
                    EndBlock(indent, tight);
                    break;
                case ListBlock list:
                    var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var task = item.Descendants<TaskList>().FirstOrDefault();
                        var bullet = list.IsOrdered ? $"{number++}. " : task is null ? (indent % 2 == 0 ? "• " : "◦ ") : "";
                        Add(bullet, inherited | MdStyle.Muted, indent + 1);
                        Blocks(item, indent + 1, inherited, tight: !list.IsLoose);
                    }
                    if (!tight) Add("\n", MdStyle.None, indent);
                    break;
                case QuoteBlock quote:
                    Blocks(quote, indent + 1, inherited | MdStyle.Quote);
                    break;
                case FencedCodeBlock or CodeBlock:
                    var code = (LeafBlock)block;
                    var text = string.Join("\n", code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString()));
                    Add(text + "\n", MdStyle.CodeBlock, indent);
                    EndBlock(indent, tight: true);
                    break;
                case HtmlBlock html:
                    // Not a browser: show the images and the text of an HTML block, drop the markup.
                    var source = string.Join("\n", html.Lines.Lines.Take(html.Lines.Count).Select(l => l.ToString()));
                    var found = false;
                    foreach (var (src, alt) in Images(source))
                    {
                        images.Add(new MdImage(runs.Count - 1, src, alt));
                        found = true;
                    }
                    var plain = StripTags(source);
                    if (plain.Length > 0) Add(plain, inherited, indent);
                    if (plain.Length > 0 || found) EndBlock(indent);
                    break;
                case ThematicBreakBlock:
                    Add(new string('─', 48), MdStyle.Muted, indent);
                    EndBlock(indent);
                    break;
                case Table table:
                    TableBlock(table, indent);
                    break;
                case ContainerBlock other:
                    Blocks(other, indent, inherited, tight);
                    break;
            }
        }

        void TableBlock(Table table, int indent)
        {
            var rows = table.OfType<TableRow>().Select(r => (r.IsHeader, Cells: r.OfType<TableCell>().Select(CellText).ToList())).ToList();
            var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Count);
            var widths = Enumerable.Range(0, columns).Select(c => rows.Max(r => r.Cells.ElementAtOrDefault(c)?.Length ?? 0)).ToList();
            foreach (var (isHeader, cells) in rows)
            {
                var line = string.Join(" │ ", Enumerable.Range(0, columns).Select(c => (cells.ElementAtOrDefault(c) ?? "").PadRight(widths[c])));
                Add(line.TrimEnd() + "\n", isHeader ? MdStyle.Table | MdStyle.TableHeader : MdStyle.Table, indent);
                if (isHeader) Add(string.Join("─┼─", widths.Select(w => new string('─', w))) + "\n", MdStyle.Table | MdStyle.Muted, indent);
            }
            // A text grid cannot hold pictures: images from the cells follow the table, in reading order.
            foreach (var image in table.Descendants<LinkInline>().Where(l => l.IsImage && !string.IsNullOrEmpty(l.Url)))
            {
                var alt = new StringBuilder();
                PlainInlines(image, alt);
                images.Add(new MdImage(runs.Count - 1, image.Url!, alt.ToString()));
            }
            EndBlock(indent, tight: true);
        }

        static string CellText(TableCell cell)
        {
            var builder = new StringBuilder();
            foreach (var paragraph in cell.Descendants<ParagraphBlock>()) PlainInlines(paragraph.Inline, builder);
            return builder.ToString().Trim();
        }

        static void PlainInlines(ContainerInline? container, StringBuilder builder)
        {
            if (container is null) return;
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal: builder.Append(literal.Content.ToString()); break;
                    case CodeInline code: builder.Append(code.Content); break;
                    case LineBreakInline: builder.Append(' '); break;
                    case ContainerInline nested: PlainInlines(nested, builder); break;
                }
            }
        }

        void Inlines(ContainerInline? container, int indent, MdStyle style, string? url = null)
        {
            if (container is null) return;
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        Add(literal.Content.ToString(), style, indent, url);
                        break;
                    case CodeInline code:
                        Add(code.Content, style | MdStyle.Code, indent, url);
                        break;
                    case EmphasisInline emphasis:
                        var add = emphasis.DelimiterChar is '~' ? MdStyle.Strike : emphasis.DelimiterCount >= 2 ? MdStyle.Bold : MdStyle.Italic;
                        Inlines(emphasis, indent, style | add, url);
                        break;
                    case TaskList task:
                        // The literal after the marker starts with its own space.
                        Add(task.Checked ? "☑" : "☐", style | MdStyle.Muted, indent);
                        break;
                    case LinkInline { IsImage: true } image:
                        var alt = new StringBuilder();
                        PlainInlines(image, alt);
                        if (image.Url is { Length: > 0 } src) images.Add(new MdImage(runs.Count - 1, src, alt.ToString()));
                        else Add($"[{alt}]", style | MdStyle.Muted, indent);
                        break;
                    case LinkInline link:
                        Inlines(link, indent, style | MdStyle.Link, link.Url);
                        break;
                    case AutolinkInline auto:
                        Add(auto.Url, style | MdStyle.Link, indent, auto.IsEmail ? "mailto:" + auto.Url : auto.Url);
                        break;
                    case LineBreakInline:
                        Add(inline is LineBreakInline { IsHard: true } ? "\n" : " ", style, indent, url);
                        break;
                    case HtmlInline html:
                        foreach (var (imageSrc, imageAlt) in Images(html.Tag)) images.Add(new MdImage(runs.Count - 1, imageSrc, imageAlt));
                        break; // other inline tags (<br>, <sub>, <a>) are dropped, their text stays
                    case HtmlEntityInline entity:
                        Add(entity.Transcoded.ToString(), style, indent, url);
                        break;
                    case ContainerInline nested:
                        Inlines(nested, indent, style, url);
                        break;
                }
            }
        }
    }
}
