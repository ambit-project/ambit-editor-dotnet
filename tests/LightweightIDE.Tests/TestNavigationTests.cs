using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.SolutionDiscovery;
using LightweightIDE.Language.Navigation;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

/// <summary>Go to Tests / Tested Code on a real two-project solution (MSBuildWorkspace).</summary>
[Trait("Category", "Integration")]
public class TestNavigationTests
{
    [Fact]
    public async Task FindsTestsByNameAndUse_AndTheTestedClassBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var sdks = await SdkInfo.ListSdksAsync(ct);
        Assert.SkipUnless(sdks.Any(s => s.StartsWith("10", StringComparison.Ordinal)), "SDK 10 not installed");

        using var dir = new TempDir();
        dir.Write("global.json", """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        dir.Write("Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var serviceText = """
            namespace Shop;
            public class OrderService
            {
                public int Total(int a, int b) => a + b;
                public int Count() => 1;
            }
            public class Unrelated { }
            """;
        var servicePath = dir.Write("Shop/OrderService.cs", serviceText);
        dir.Write("Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Shop/Shop.csproj" /></ItemGroup>
            </Project>
            """);
        dir.Write("Shop.Tests/Fact.cs", "public sealed class FactAttribute : System.Attribute { }");
        var testsText = """
            using Shop;
            public class OrderServiceTests
            {
                [Fact] public void Adds() => System.Console.WriteLine(new OrderService().Total(1, 2));
                [Fact] public void Counts() => System.Console.WriteLine(new OrderService().Count());
            }
            public class CheckoutFlowTests
            {
                [Fact] public void Pays() => System.Console.WriteLine(new OrderService().Total(3, 4));
            }
            """;
        var testsPath = dir.Write("Shop.Tests/OrderServiceTests.cs", testsText);
        var solution = Path.Combine(dir.Path, "Shop.slnx");
        File.WriteAllText(solution, """<Solution><Project Path="Shop/Shop.csproj" /><Project Path="Shop.Tests/Shop.Tests.csproj" /></Solution>""");
        Assert.Equal(0, await ProcessRunner.RunAsync("dotnet", ["restore", solution], dir.Path, (_, _) => { }, cancellationToken: ct));

        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        await workspace.LoadAsync(await SolutionLoader.DiscoverAsync(dir.Path, ct: ct));
        var navigation = new TestNavigation(workspace);

        // Class → test class named after it, then the other test class that uses it.
        var byClass = await navigation.FindAsync(servicePath, serviceText, serviceText.IndexOf("class OrderService", StringComparison.Ordinal) + 6, ct);
        Assert.True(byClass!.ToTests);
        Assert.Equal(["OrderServiceTests", "CheckoutFlowTests"], byClass.Targets.Select(t => t.Name));
        Assert.Equal("named after OrderService", byClass.Targets[0].Reason);

        // Method → the class named after the type, then the test methods that call it.
        var byMethod = await navigation.FindAsync(servicePath, serviceText, serviceText.IndexOf("a + b", StringComparison.Ordinal), ct);
        Assert.Equal("OrderService.Total()", byMethod!.Subject);
        Assert.Equal(["OrderServiceTests", "OrderServiceTests.Adds", "CheckoutFlowTests.Pays"], byMethod.Targets.Select(t => t.Name));

        // Nothing for an unused type.
        var none = await navigation.FindAsync(servicePath, serviceText, serviceText.IndexOf("Unrelated", StringComparison.Ordinal), ct);
        Assert.Empty(none!.Targets);

        // Test → tested class by name; a test class with another name → the type it uses most.
        var back = await navigation.FindAsync(testsPath, testsText, testsText.IndexOf("Adds", StringComparison.Ordinal), ct);
        Assert.False(back!.ToTests);
        Assert.Equal(("OrderService", servicePath), (back.Targets.Single().Name, back.Targets.Single().Location.Path));
        var flow = await navigation.FindAsync(testsPath, testsText, testsText.IndexOf("Pays", StringComparison.Ordinal), ct);
        Assert.Equal("OrderService", flow!.Targets.First().Name);
        Assert.StartsWith("used", flow.Targets.First().Reason, StringComparison.Ordinal);
    }
}
