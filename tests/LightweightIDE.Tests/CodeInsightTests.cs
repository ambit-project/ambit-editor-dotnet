using LightweightIDE.Language.Classification;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

public class CodeInsightTests
{
    const string Code = """
        using System;
        using System.Text;
        using System.Linq;
        namespace Shop;
        public class Orders
        {
            private int unusedField;
            private int writeOnly;
            private int count;
            private void Unused() { }
            public int Add(int amount, int extra)
            {
                int assigned = 5;
                writeOnly = 3;
                count += amount;
                var total = count + amount;
                return total + new[] { 1 }.Count();
            }
            public int Count => count;
        }
        """;

    static int At(string word, int occurrence = 0)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; i++) index = Code.IndexOf(word, index + 1, StringComparison.Ordinal);
        return index;
    }

    static string Text(int start, int end) => Code[start..end];

    static async Task<IReadOnlyList<UsageSpan>> UsagesAsync(int position)
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        return await new SymbolUsages(workspace).GetAsync("/tmp/ins/A.cs", Code, position, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Usages_OfAField_IncludeTheDeclarationAndEveryUse()
    {
        var usages = await UsagesAsync(At("count +=") + 2);
        Assert.Equal(4, usages.Count);
        Assert.All(usages, u => Assert.Equal("count", Text(u.Start, u.End)));
        Assert.Single(usages, u => u.IsDefinition);
        Assert.Equal(At("count;"), usages.Single(u => u.IsDefinition).Start);
    }

    [Fact]
    public async Task Usages_OfAParameter_StayInsideTheMethod()
    {
        var usages = await UsagesAsync(At("amount,"));
        Assert.Equal(3, usages.Count);
        Assert.All(usages, u => Assert.Equal("amount", Text(u.Start, u.End)));
    }

    [Fact]
    public async Task Usages_CaretRightAfterTheName_Counts()
    {
        var usages = await UsagesAsync(At("total +") + "total".Length);
        Assert.Equal(2, usages.Count);
    }

    [Fact]
    public async Task Usages_OfAType_IncludeConstructorsAndCreations()
    {
        const string code = """
            public class Box
            {
                public Box() { }
                public static Box Make() => new Box();
            }
            """;
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var usages = await new SymbolUsages(workspace).GetAsync("/tmp/ins/B.cs", code, code.IndexOf("new Box", StringComparison.Ordinal) + 5,
            TestContext.Current.CancellationToken);
        Assert.Equal(4, usages.Count);
        Assert.Equal(2, usages.Count(u => u.IsDefinition)); // class name and constructor name
    }

    [Theory]
    [InlineData("var total")]
    [InlineData("return total")]
    [InlineData("{ 1 }")]
    public async Task Usages_NotOnAName_AreEmpty(string where)
    {
        Assert.Empty(await UsagesAsync(At(where)));
    }

    [Fact]
    public async Task Unnecessary_FindsUsingsMembersParametersAndValues()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var spans = await new UnnecessaryCode(workspace).GetAsync("/tmp/ins/A.cs", Code, TestContext.Current.CancellationToken, includeMiscellaneous: true);
        var faded = spans.Select(s => (s.Id, Text: Text(s.Start, s.End))).ToList();
        Assert.Contains(("IDE0005", "using System.Text;"), faded);
        Assert.Contains(("IDE0051", "unusedField"), faded);
        Assert.Contains(("IDE0052", "writeOnly"), faded);
        Assert.Contains(faded, f => f.Id == "IDE0051" && f.Text.StartsWith("private void Unused()", StringComparison.Ordinal));
        Assert.Contains(faded, f => f.Id == "IDE0060" && f.Text.EndsWith("extra", StringComparison.Ordinal));
        Assert.Contains(faded, f => f.Id == "IDE0059" && f.Text == "assigned");
        Assert.DoesNotContain(faded, f => f.Text.Contains("count", StringComparison.Ordinal) || f.Text.Contains("amount", StringComparison.Ordinal));
        Assert.DoesNotContain(faded, f => f.Text == "using System.Linq;");
    }

    [Fact]
    public async Task Unnecessary_SkipsFilesOutsideAProject()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        Assert.Empty(await new UnnecessaryCode(workspace).GetAsync("/tmp/ins/A.cs", Code, TestContext.Current.CancellationToken));
    }
}
