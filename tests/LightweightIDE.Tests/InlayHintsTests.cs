using LightweightIDE.Language.Classification;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

public class InlayHintsTests
{
    const string Code = """
        using System.Collections.Generic;
        using System.Linq;
        namespace Shop;
        public record Order(string Name, decimal Total);
        public class Orders
        {
            public static Order Create(string name, decimal total, bool urgent) => new(name, total);
            public static void SetName(string name) { }
            public void Run(List<Order> list)
            {
                var order = Create("a", 10m, true);
                var copy = new Order("b", 2);
                var count = 5;
                SetName("x");
                Create(order.Name, order.Total, urgent: false);
                foreach (var item in list) { }
                var names = list.Select(o => o.Name).ToList();
                var (first, second) = (order, copy);
            }
        }
        """;

    static async Task<IReadOnlyList<InlayHint>> HintsAsync()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        return await new InlayHintService(workspace).GetHintsAsync("/tmp/inlay/A.cs", Code, 0, Code.Length, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ParameterNames_OnlyBeforeLiterals()
    {
        var hints = (await HintsAsync()).Where(h => h.Kind == InlayHintKind.Parameter).ToList();

        // Create("a", 10m, true): all three are literals.
        Assert.Contains(hints, h => h.Text == "name:" && Code[h.Position] == '"' && Code[h.Position + 1] == 'a');
        Assert.Contains(hints, h => h.Text == "total:" && Code[h.Position..].StartsWith("10m", StringComparison.Ordinal));
        Assert.Contains(hints, h => h.Text == "urgent:" && Code[h.Position..].StartsWith("true", StringComparison.Ordinal));
        // new Order("b", 2): the constructor's parameters.
        Assert.Contains(hints, h => h.Text == "Name:" && Code[h.Position..].StartsWith("\"b\"", StringComparison.Ordinal));
        // SetName("x"): one argument that is not a flag, and the name repeats the method name.
        Assert.DoesNotContain(hints, h => Code[h.Position..].StartsWith("\"x\"", StringComparison.Ordinal));
        // order.Name is not a literal; urgent: false is already named.
        Assert.DoesNotContain(hints, h => Code[h.Position..].StartsWith("order.Name", StringComparison.Ordinal));
        Assert.DoesNotContain(hints, h => Code[h.Position..].StartsWith("false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Types_WhereTheCodeDoesNotShowThem()
    {
        var hints = (await HintsAsync()).Where(h => h.Kind == InlayHintKind.Type).ToList();
        string? TypeAfter(string name) => hints.FirstOrDefault(h => Code[..h.Position].EndsWith(name, StringComparison.Ordinal)).Text;

        Assert.Equal(": Order", TypeAfter("var order"));
        Assert.Null(TypeAfter("var copy"));   // new Order(...)
        Assert.Null(TypeAfter("var count"));  // literal
        Assert.Equal(": Order", TypeAfter("var item"));
        Assert.Equal(": List<string>", TypeAfter("var names"));
        Assert.Equal(": Order", TypeAfter("var (first"));
        // Lambda parameter: the type goes before the name.
        Assert.Contains(hints, h => h.Text == "Order" && Code[h.Position..].StartsWith("o => o.Name", StringComparison.Ordinal));
    }
}
