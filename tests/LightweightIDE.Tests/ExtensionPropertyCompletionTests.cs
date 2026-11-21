using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.SolutionDiscovery;
using LightweightIDE.Language.Completion;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

/// <summary>C# 14 extension blocks: instance and static extension properties in completion.</summary>
[Trait("Category", "Integration")]
public class ExtensionPropertyCompletionTests
{
    [Fact]
    public async Task ExtensionProperties_AreOffered()
    {
        var ct = TestContext.Current.CancellationToken;
        var sdks = await SdkInfo.ListSdksAsync(ct);
        Assert.SkipUnless(sdks.Any(s => s.StartsWith("10", StringComparison.Ordinal)), "SDK 10 not installed");
        using var dir = new TempDir();
        dir.Write("global.json", """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        var project = dir.Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        dir.Write("Lib/Imported.cs", """
            namespace Lib;
            public sealed class Order { public decimal Net { get; set; } }
            public static class OrderExtensions
            {
                extension(Order order)
                {
                    public decimal Gross => order.Net * 1.23m;
                    public bool IsFree { get => order.Net == 0; set { if (value) order.Net = 0; } }
                    public static Order Empty => new();
                    public Order WithNet(decimal net) => new() { Net = net };
                    public static Order Create() => new();
                    public T Map<T>(Func<Order, T> map) => map(order);
                }
                public static Order Classic(this Order order) => order;
            }
            """);
        dir.Write("Lib/Other.cs", """
            namespace Lib.Other;
            public static class SequenceExtensions
            {
                extension<T>(IEnumerable<T> source)
                {
                    public bool IsEmpty => !source.Any();
                    public T? Second() => source.Skip(1).FirstOrDefault();
                }
                extension(Lib.Order)
                {
                    public static int MaxLines => 100;
                }
            }
            """);
        var path = dir.Write("Lib/Use.cs", "namespace Lib;\nclass Use { void M(Order o, List<int> list) { } }\n");
        var restore = await ProcessRunner.RunAsync("dotnet", ["restore", project], dir.Path, (_, _) => { }, cancellationToken: ct);
        Assert.Equal(0, restore);
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        await workspace.LoadAsync(await SolutionLoader.DiscoverAsync(Path.Combine(dir.Path, "Lib"), ct: ct));
        Assert.Equal(WorkspaceState.Ready, workspace.State);
        var completions = new CompletionProvider(workspace);

        async Task<(CompletionSession Session, string Text)> At(string statement)
        {
            var text = $"namespace Lib;\nclass Use {{ void M(Order o, List<int> list) {{ {statement} }} }}\n";
            workspace.UpdateText(path, text);
            var position = text.IndexOf(statement, StringComparison.Ordinal) + statement.Length;
            var s = await completions.GetCompletionsAsync(path, text, position, explicitInvoke: false, ct);
            Assert.NotNull(s);
            return (s, text);
        }

        var (instance, _) = await At("o.");
        var (onType, _) = await At("Order.");
        var (onList, listText) = await At("list.");
        foreach (var (s, name) in new[] { (instance, "Gross"), (instance, "IsFree"), (onType, "Empty"), (onType, "MaxLines"), (onList, "IsEmpty") })
            Assert.Contains(CompletionProvider.Filter(s.Items, ""), i => i.DisplayText == name);
        // Out of the usings: the namespace is shown and committing adds the using, without "()".
        var isEmpty = onList.Items.First(i => i.DisplayText == "IsEmpty");
        Assert.False(isEmpty.InScope);
        Assert.Equal("extension · Lib.Other", isEmpty.Suffix);
        // In scope: marked with the declaring class; ordinary properties stay unmarked.
        Assert.Equal("extension · OrderExtensions", instance.Items.First(i => i.DisplayText == "Gross").Suffix);
        Assert.Equal("extension · OrderExtensions", instance.Items.First(i => i.DisplayText == "IsFree").Suffix);
        Assert.Null(instance.Items.First(i => i.DisplayText == "Net").Suffix);
        Assert.Equal("extension · OrderExtensions", onType.Items.First(i => i.DisplayText == "Empty").Suffix);
        Assert.Equal("extension · Lib.Other", onType.Items.First(i => i.DisplayText == "MaxLines").Suffix);
        Assert.Null(onList.Items.First(i => i.DisplayText == "Count").Suffix);
        // Methods of extension blocks are marked too; classic `this` extension methods are not.
        Assert.Equal("extension · OrderExtensions", instance.Items.First(i => i.DisplayText == "WithNet").Suffix);
        Assert.Equal("extension · OrderExtensions", instance.Items.First(i => i.DisplayText == "Map<>").Suffix);
        Assert.Null(instance.Items.First(i => i.DisplayText == "Classic").Suffix);
        Assert.Equal("extension · OrderExtensions", onType.Items.First(i => i.DisplayText == "Create").Suffix);
        Assert.Equal("extension · Lib.Other", onList.Items.First(i => i.DisplayText == "Second").Suffix);
        Assert.Null(onList.Items.First(i => i.DisplayText == "Where<>").Suffix);
        var (conditional, _) = await At("o?.");
        Assert.Equal("extension · OrderExtensions", conditional.Items.First(i => i.DisplayText == "Gross").Suffix);
        var edit = await completions.GetCommitEditAsync(onList, isEmpty, ct);
        Assert.Contains("using Lib.Other;", edit.NewText);
        Assert.DoesNotContain("IsEmpty(", edit.NewText);
        var description = await completions.GetDescriptionAsync(instance, instance.Items.First(i => i.DisplayText == "Gross"), ct);
        Assert.Equal("decimal OrderExtensions.extension(Order).Gross { get; }", description);
    }
}
