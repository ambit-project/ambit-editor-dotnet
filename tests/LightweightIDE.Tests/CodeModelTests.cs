using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.SolutionDiscovery;
using LightweightIDE.Language.Analysis;
using LightweightIDE.Language.Completion;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

/// <summary>Solution analysis after the workspace loads: type hierarchy across projects, used by smart completion.</summary>
[Trait("Category", "Integration")]
public class CodeModelTests
{
    [Fact]
    public async Task IndexesImplementationsAcrossProjects_AndReindexesEdits()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        const string props = "<PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>";
        dir.Write("Core/Core.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{props}</Project>");
        dir.Write("Core/Shop.cs", "namespace Core; public interface IShopService { } public abstract class ShopBase : IShopService { }");
        var app = dir.Write("App/App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{props}<ItemGroup><ProjectReference Include=\"../Core/Core.csproj\" /></ItemGroup></Project>");
        var shops = dir.Write("App/Shops.cs", "namespace App; public class ShopService : Core.ShopBase { } public class Other { }");
        dir.Write("Shop.slnx", "<Solution><Project Path=\"Core/Core.csproj\" /><Project Path=\"App/App.csproj\" /></Solution>");
        Assert.Equal(0, await ProcessRunner.RunAsync("dotnet", ["restore", app], dir.Path, (_, _) => { }, cancellationToken: ct));

        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        using var model = new CodeModel(workspace, NullLogger<CodeModel>.Instance);
        await workspace.LoadAsync(await SolutionLoader.DiscoverAsync(dir.Path, ct: ct));
        Assert.True(await model.WaitReadyAsync(TimeSpan.FromSeconds(60), ct));
        Assert.Equal((2, 2), model.Progress);

        var shop = await InterfaceAsync(workspace, "Core.IShopService", ct);
        Assert.Equal(["ShopBase", "ShopService"], model.Implementations(shop).Select(t => t.Name));

        // Unsaved edit: a new implementation appears after the debounce.
        workspace.UpdateText(shops, "namespace App; public class ShopService : Core.ShopBase { } public class FastShop : Core.IShopService { }");
        for (var i = 0; i < 100 && model.Implementations(shop).All(t => t.Name != "FastShop"); i++) await Task.Delay(100, ct);
        shop = await InterfaceAsync(workspace, "Core.IShopService", ct);
        Assert.Equal(["FastShop", "ShopBase", "ShopService"], model.Implementations(shop).Select(t => t.Name));

        // Smart completion uses the index.
        const string code = "namespace App; public class ShopService : Core.ShopBase { } public class FastShop : Core.IShopService { } class Startup { void M() { Core.IShopService s = new  } }";
        var position = code.IndexOf("new ", StringComparison.Ordinal) + 4;
        workspace.UpdateText(shops, code);
        var provider = new CompletionProvider(workspace, model);
        var session = await provider.GetCompletionsAsync(shops, code, position, true, ct);
        var top = CompletionProvider.Filter(session!.Items, "").Take(2).Select(e => e.DisplayText).Order();
        Assert.Equal(["FastShop", "ShopService"], top);
    }

    static async Task<INamedTypeSymbol> InterfaceAsync(WorkspaceService workspace, string name, CancellationToken ct)
    {
        var project = workspace.CurrentSolution!.Projects.First(p => p.Name == "Core");
        var compilation = await project.GetCompilationAsync(ct);
        return compilation!.GetTypeByMetadataName(name)!;
    }
}
