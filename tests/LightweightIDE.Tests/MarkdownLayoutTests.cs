using LightweightIDE.Editor.Markdown;

namespace LightweightIDE.Tests;

public class MarkdownLayoutTests
{
    [Fact]
    public void Headings_Emphasis_Code_AndLinks_GetStyles()
    {
        var doc = MarkdownLayout.Build("""
            # Title

            Some **bold**, *italic*, ~~gone~~ and `code` with a [link](docs/a.md).
            """);
        Assert.Contains(doc.Runs, r => r.Text == "Title" && r.Style.HasFlag(MdStyle.Heading1));
        Assert.Contains(doc.Runs, r => r.Text == "bold" && r.Style.HasFlag(MdStyle.Bold));
        Assert.Contains(doc.Runs, r => r.Text == "italic" && r.Style.HasFlag(MdStyle.Italic));
        Assert.Contains(doc.Runs, r => r.Text == "gone" && r.Style.HasFlag(MdStyle.Strike));
        Assert.Contains(doc.Runs, r => r.Text == "code" && r.Style.HasFlag(MdStyle.Code));
        Assert.Contains(doc.Runs, r => r.Text == "link" && r.Style.HasFlag(MdStyle.Link) && r.Url == "docs/a.md");
        Assert.Equal("Title\n\nSome bold, italic, gone and code with a link.\n", doc.PlainText);
    }

    [Fact]
    public void Lists_TaskLists_AndNesting()
    {
        var doc = MarkdownLayout.Build("""
            - one
              - nested
            - [x] done
            - [ ] todo

            1. first
            2. second
            """);
        var text = doc.PlainText;
        Assert.Contains("• one", text);
        Assert.Contains("◦ nested", text);
        Assert.Contains("☑ done", text);
        Assert.Contains("☐ todo", text);
        Assert.Contains("1. first\n2. second", text);
        Assert.Contains(doc.Runs, r => r.Text.Contains("nested") && r.Indent == 2);
    }

    [Fact]
    public void Tables_AreAlignedGrid()
    {
        var doc = MarkdownLayout.Build("""
            | Name | Size |
            |------|-----:|
            | a    | 10   |
            | long name | 2 |
            """);
        Assert.Contains("Name      │ Size", doc.PlainText);
        Assert.Contains("long name │ 2", doc.PlainText);
        Assert.Contains(doc.Runs, r => r.Style.HasFlag(MdStyle.TableHeader));
    }

    [Fact]
    public void CodeBlocks_Quotes_Images()
    {
        var doc = MarkdownLayout.Build("""
            > quoted

            ```csharp
            var x = 1;
            ```

            ![shot](docs/screens/main.png)
            """);
        Assert.Contains(doc.Runs, r => r.Text.Contains("quoted") && r.Style.HasFlag(MdStyle.Quote) && r.Indent == 1);
        Assert.Contains(doc.Runs, r => r.Text == "var x = 1;\n" && r.Style.HasFlag(MdStyle.CodeBlock));
        var image = Assert.Single(doc.Images);
        Assert.Equal("docs/screens/main.png", image.Url);
        Assert.Equal("shot", image.Alt);
    }
}

public class MarkdownHtmlTests
{
    [Fact]
    public void HtmlBlock_ShowsImagesAndText_WithoutMarkup()
    {
        var doc = MarkdownLayout.Build("""
            <p align="center"><img src="docs/logo.svg" alt="Logo" height="56"></p>
            <p align='center'>Small <b>and</b> fast</p>

            Text <img src='badge.png'> after
            """);
        Assert.Equal(["docs/logo.svg", "badge.png"], doc.Images.Select(i => i.Url));
        Assert.Equal("Logo", doc.Images[0].Alt);
        Assert.Contains("Small and fast", doc.PlainText);
        Assert.DoesNotContain("<", doc.PlainText);
    }
}

public class MarkdownTableImageTests
{
    [Fact]
    public void ImagesInTableCells_FollowTheTable()
    {
        var doc = MarkdownLayout.Build("""
            | Landing | Cart |
            | --- | --- |
            | ![Landing](a.png) | ![Cart](b.png) |
            """);
        Assert.Equal(["a.png", "b.png"], doc.Images.Select(i => i.Url));
        Assert.Contains("Landing │ Cart", doc.PlainText);
    }
}
