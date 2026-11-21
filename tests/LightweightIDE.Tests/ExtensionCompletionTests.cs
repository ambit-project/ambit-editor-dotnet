using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.SolutionDiscovery;
using LightweightIDE.Language.Completion;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

/// <summary>
/// Extension methods (classic <c>this</c> parameter and C# 14 <c>extension(...)</c> blocks) from the project and from
/// a NuGet package are offered right after the dot, even when their namespace is not imported yet.
/// </summary>
[Trait("Category", "Integration")]
public class ExtensionCompletionTests
{
    [Fact]
    public async Task Builder_OffersLocalAndPackageExtensionMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        var sdks = await SdkInfo.ListSdksAsync(ct);
        Assert.SkipUnless(sdks.Any(s => s.StartsWith("10", StringComparison.Ordinal)), "SDK 10 not installed");

        using var dir = new TempDir();
        dir.Write("global.json", """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        var project = dir.Write("Api/Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="Serilog.AspNetCore" Version="10.0.0" /></ItemGroup>
            </Project>
            """);
        dir.Write("Api/Extensions/ServiceSetup.cs", """
            namespace Api.Extensions;
            public static class ServiceSetup
            {
                public static IServiceCollection AddServices(this IServiceCollection services) => services;
                public static WebApplicationBuilder AddAppServices(this WebApplicationBuilder builder) => builder;
            }
            """);
        dir.Write("Api/Extensions/WebSetup.cs", """
            namespace Api.Web;
            public static class WebSetup
            {
                extension(WebApplicationBuilder builder)
                {
                    public WebApplicationBuilder AddModules() => builder;
                    public bool IsLocal => true;
                }
            }
            """);
        var servicesText = """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.
            var app = builder.Build();
            app.Run();
            """;
        var programPath = dir.Write("Api/Program.cs", servicesText);
        var restoreOutput = new List<string>();
        var restore = await ProcessRunner.RunAsync("dotnet", ["restore", project], dir.Path, (l, _) => { lock (restoreOutput) restoreOutput.Add(l); }, cancellationToken: ct);
        Assert.True(restore == 0, string.Join('\n', restoreOutput));

        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        await workspace.LoadAsync(await SolutionLoader.DiscoverAsync(Path.Combine(dir.Path, "Api"), ct: ct));
        Assert.Equal(WorkspaceState.Ready, workspace.State);
        var completions = new CompletionProvider(workspace);
        // Builds the import index from every reference (normally started by the workspace becoming ready).
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(1, await completions.WarmUpAsync(ct));
        TestContext.Current.TestOutputHelper!.WriteLine($"warm-up {watch.ElapsedMilliseconds} ms");
        watch.Restart();

        // builder.Services. — a local extension method and one from a package, both outside the usings
        workspace.UpdateText(programPath, servicesText);
        var position = servicesText.IndexOf("Services.", StringComparison.Ordinal) + "Services.".Length;
        var session = await completions.GetCompletionsAsync(programPath, servicesText, position, explicitInvoke: false, ct);
        Assert.NotNull(session);
        TestContext.Current.TestOutputHelper!.WriteLine($"first list {watch.ElapsedMilliseconds} ms");
        var shown = Names(CompletionProvider.Filter(session.Items, ""));
        Assert.Contains("AddServices", shown);
        Assert.Contains("AddSerilog", shown);
        // Typed "Add": alphabetical among the other members, not after all of them.
        var add = Names(CompletionProvider.Filter(session.Items, "Add"));
        Assert.True(add.IndexOf("AddServices") < add.IndexOf("AddSession"), string.Join(", ", add));

        // Committing adds the using.
        var addServices = session.Items.First(i => i.DisplayText == "AddServices");
        Assert.False(addServices.InScope);
        Assert.Equal("Api.Extensions", addServices.Suffix);

        // builder. — classic extension method and C# 14 extension block members (method and property)
        var builderText = servicesText.Replace("builder.Services.", "builder.");
        workspace.UpdateText(programPath, builderText);
        var builderPosition = builderText.IndexOf("builder.\n", StringComparison.Ordinal) + "builder.".Length;
        var onBuilder = await completions.GetCompletionsAsync(programPath, builderText, builderPosition, explicitInvoke: false, ct);
        Assert.NotNull(onBuilder);
        var builderShown = Names(CompletionProvider.Filter(onBuilder.Items, ""));
        Assert.Contains("AddAppServices", builderShown);
        Assert.Contains("AddModules", builderShown);
        Assert.Contains("IsLocal", builderShown);
        Assert.False(onBuilder.Items.First(i => i.DisplayText == "IsLocal").InScope);
        var edit = await completions.GetCommitEditAsync(onBuilder, onBuilder.Items.First(i => i.DisplayText == "AddModules"), ct);
        Assert.Contains("using Api.Web;", edit.NewText);
    }

    static List<string> Names(IReadOnlyList<CompletionEntry> items) => items.Select(i => i.DisplayText).ToList();
}
