using LightweightIDE.Language.Classification;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

public class SemanticHighlighterTests
{
    const string Code = """
        namespace Shop;
        public interface IOrders { int Count { get; } }
        public enum Color { Red }
        public class Orders : IOrders
        {
            const int Max = 10;
            static int created;
            private readonly int limit;
            public int Count => limit;
            public int Add(int amount)
            {
                var total = amount + Max;
                created++;
                if (total > 0) return total;
                return (int)Color.Red;
            }
        }
        """;

    static async Task<IReadOnlyList<SemanticSpan>> SpansAsync(string code)
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        return await new SemanticHighlighter(workspace).GetSpansAsync("/tmp/sem/A.cs", code, 0, code.Length, TestContext.Current.CancellationToken);
    }

    static ISet<string> KindsAt(IReadOnlyList<SemanticSpan> spans, string code, string word, int occurrence = 0)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; i++) index = code.IndexOf(word, index + 1, StringComparison.Ordinal);
        return spans.Where(s => s.Start <= index && index < s.End).Select(s => s.Kind).ToHashSet();
    }

    [Theory]
    [InlineData("IOrders", 0, "interface")]
    [InlineData("Orders :", 0, "class")]
    [InlineData("Color", 0, "enum")]
    [InlineData("Red", 1, "enum-member")]
    [InlineData("Max", 1, "constant")]
    [InlineData("limit", 1, "field")]
    [InlineData("Count", 1, "property")]
    [InlineData("Add", 0, "method")]
    [InlineData("amount", 1, "parameter")]
    [InlineData("total", 1, "local")]
    [InlineData("return", 0, "control-keyword")]
    [InlineData("Shop", 0, "namespace")]
    public async Task Identifiers_GetTheirKind(string word, int occurrence, string kind)
    {
        var spans = await SpansAsync(Code);
        Assert.Contains(kind, KindsAt(spans, Code, word, occurrence));
    }

    [Fact]
    public async Task StaticMembers_AreMarkedStatic_InAdditionToTheirKind()
    {
        var spans = await SpansAsync(Code);
        var kinds = KindsAt(spans, Code, "created", 1);
        Assert.Contains("field", kinds);
        Assert.Contains("static", kinds);
        Assert.DoesNotContain("static", KindsAt(spans, Code, "limit", 1));
    }

    [Fact]
    public async Task OnlyTheRequestedRange_IsClassified()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var start = Code.IndexOf("public int Add", StringComparison.Ordinal);
        var spans = await new SemanticHighlighter(workspace).GetSpansAsync("/tmp/sem/A.cs", Code, start, Code.Length, TestContext.Current.CancellationToken);
        Assert.NotEmpty(spans);
        Assert.All(spans, s => Assert.True(s.End >= start));
        Assert.All(SemanticHighlighter.Kinds, k => Assert.Matches("^[a-z-]+$", k));
    }
}
