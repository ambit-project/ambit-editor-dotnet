using LightweightIDE.Language.Completion;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

/// <summary>The scenarios of docs/plans/intellisense.md that do not need the UI, on real Roslyn completion.</summary>
public class IntelliSenseTests
{
    static async Task<IReadOnlyList<CompletionEntry>> TopAsync(string marked, bool invoke = true, CompletionUsage? usage = null, string typed = "")
    {
        var position = marked.IndexOf('|');
        var code = marked.Remove(position, 1);
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var provider = new CompletionProvider(workspace);
        var session = await provider.GetCompletionsAsync("/tmp/intellisense/A.cs", code, position, invoke, TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        return CompletionProvider.Filter(session.Items, typed, usage: usage).Take(10).ToList();
    }

    const string Shops = """
        public interface IShopService { void Buy(); }
        public class ShopService : IShopService { public void Buy() { } }
        public class OnlineShopService : ShopService { }
        public abstract class AbstractShop : IShopService { public abstract void Buy(); }
        public class Unrelated { }
        public interface IServiceCollection { }
        public static class Registration
        {
            public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
                where TService : class where TImplementation : class, TService => services;
        }
        """;

    [Fact]
    public async Task DiRegistration_ImplementationsFirst()
    {
        var top = await TopAsync(Shops + "class Startup { void M(IServiceCollection services) { services.AddScoped<IShopService, | } }");
        Assert.Equal(["OnlineShopService", "ShopService"], top.Take(2).Select(e => e.DisplayText).Order());
        Assert.All(top.Take(2), e => Assert.Equal("implements IShopService", e.Smart));
        Assert.DoesNotContain(top.Take(2), e => e.DisplayText == "AbstractShop"); // not concrete
    }

    [Fact]
    public async Task DiRegistration_WhenNextLineMakesItLookLikeADeclaration()
    {
        var top = await TopAsync(Shops + """
            class Startup
            {
                void M(IServiceCollection services)
                {
                    services.AddScoped<IShopService, |
                    IShopService shop = new ShopService();
                }
            }
            """);
        Assert.Equal(["OnlineShopService", "ShopService"], top.Take(2).Select(e => e.DisplayText).Order());
    }

    [Fact]
    public async Task NewWithInterfaceExpected_ImplementationsFirst()
    {
        var top = await TopAsync(Shops + "class A { void M() { IShopService shop = new | } }");
        Assert.Contains(top.Take(3), e => e.DisplayText == "ShopService" && e.Smart is not null);
    }

    [Fact]
    public async Task ExpectedEnum_MembersFirst()
    {
        var top = await TopAsync("enum Color { Red, Green } class A { void M() { Color c = | } }");
        Assert.StartsWith("Color", top[0].DisplayText);
    }

    [Fact]
    public async Task LocalsAndParameters_BeforeTypes_AndNoUnimportedClutter()
    {
        var top = await TopAsync("class A { void M(int count) { var total = 1; | } }");
        Assert.Contains(top.Take(4), e => e.DisplayText == "count");
        Assert.Contains(top.Take(4), e => e.DisplayText == "total");
        Assert.All(top, e => Assert.True(e.InScope));
    }

    [Fact]
    public async Task CamelHumps_And_UnimportedTypeWithItsNamespace()
    {
        var top = await TopAsync("class A { void M() { var b = new | } }", typed: "StringB");
        var sb = top.First();
        Assert.Equal("StringBuilder", sb.DisplayText);
        Assert.False(sb.InScope);
        Assert.Equal("System.Text", sb.Suffix);

        var humps = await TopAsync("class ShopBasket { } class A { void M() { var b = new | } }", typed: "SBa");
        Assert.Equal("ShopBasket", humps[0].DisplayText); // CamelCase humps
    }

    [Fact]
    public async Task CommittingUnimportedType_AddsUsing()
    {
        const string code = "class A { void M() { var b = new StringBu } }";
        var position = code.IndexOf("StringBu", StringComparison.Ordinal) + "StringBu".Length;
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var provider = new CompletionProvider(workspace);
        var session = await provider.GetCompletionsAsync("/tmp/intellisense/A.cs", code, position, true, TestContext.Current.CancellationToken);
        var entry = CompletionProvider.Filter(session!.Items, "StringBu").First(e => e.DisplayText == "StringBuilder");
        var edit = await provider.GetCommitEditAsync(session, entry, TestContext.Current.CancellationToken);
        var result = code[..edit.Start] + edit.NewText + code[edit.End..];
        Assert.Contains("using System.Text;", result);
        Assert.Contains("new StringBuilder", result);
    }

    [Fact]
    public async Task PickedItems_Rise()
    {
        var usage = new CompletionUsage();
        var before = await TopAsync("class A { int alpha, beta, gamma; void M() { | } }", usage: usage);
        usage.Record("gamma");
        usage.Record("gamma");
        var after = await TopAsync("class A { int alpha, beta, gamma; void M() { | } }", usage: usage);
        Assert.True(after.ToList().FindIndex(e => e.DisplayText == "gamma") < before.ToList().FindIndex(e => e.DisplayText == "gamma") || after[0].DisplayText == "gamma");
    }

    [Theory]
    [InlineData("StringBuilder", "SB", true)]
    [InlineData("GetAllAsync", "gAA", true)]
    [InlineData("GetAllAsync", "GetAll", true)]
    [InlineData("StringBuilder", "Builder", true)]
    [InlineData("StringBuilder", "xyz", false)]
    public void Match(string candidate, string typed, bool matches) => Assert.Equal(matches, CompletionRanker.Match(candidate, typed) > 0);

    [Fact]
    public async Task ObjectMembers_BelowTheTypesOwnMembers()
    {
        var top = await TopAsync("class Repo { public void Save() { } } class A { void M(Repo r) { r.| } }");
        Assert.Equal("Save", top[0].DisplayText);
    }

    [Fact]
    public void Match_PrefixBeatsHumpsBeatsSubstring()
    {
        Assert.True(CompletionRanker.Match("Shop", "Sh") > CompletionRanker.Match("SomeHelper", "Sh"));
        Assert.True(CompletionRanker.Match("SomeHelper", "SH") > CompletionRanker.Match("PushHelper", "sh"));
    }
}

public class SignatureHelpTests
{
    static async Task<SignatureHelpResult?> At(string marked)
    {
        var position = marked.IndexOf('|');
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        return await new SignatureHelpProvider(workspace).GetAsync("/tmp/sig/A.cs", marked.Remove(position, 1), position, TestContext.Current.CancellationToken);
    }

    const string Api = """
        class Api
        {
            /// <summary>Sends an order.</summary>
            /// <param name="id">The order id.</param>
            /// <param name="urgent">Ship today.</param>
            public void Send(int id, bool urgent = false) { }
            public void Send(string code) { }
            public Api(string name, int retries) { }
        }
        """;

    [Fact]
    public async Task Overloads_ActiveParameter_AndDocs()
    {
        var help = await At(Api + "class B { void M(Api a) { a.Send(1, | } }");
        Assert.NotNull(help);
        Assert.Equal(2, help.Signatures.Count);
        Assert.Equal(1, help.ActiveParameter);
        var active = help.Signatures[help.ActiveSignature];
        Assert.Equal(["int id", "bool urgent = false"], active.Parameters);
        Assert.Equal("Sends an order.", active.Documentation);
        Assert.Equal("Ship today.", active.ParameterDocumentation[1]);
    }

    [Fact]
    public async Task NamedArgument_SelectsItsParameter()
    {
        var help = await At(Api + "class B { void M(Api a) { a.Send(urgent: |) } }");
        Assert.Equal(1, help!.ActiveParameter);
    }

    [Fact]
    public async Task Constructor_AndTargetTypedNew()
    {
        Assert.Equal("Api(", (await At(Api + "class B { object M() => new Api(|) ; }"))!.Signatures[0].Prefix);
        var targetTyped = await At(Api + "class B { Api M() => new(\"x\", |); }");
        Assert.Equal(1, targetTyped!.ActiveParameter);
    }

    [Fact]
    public async Task OutsideArguments_Nothing() => Assert.Null(await At(Api + "class B { void M(Api a) { a.Send(1);| } }"));
}
