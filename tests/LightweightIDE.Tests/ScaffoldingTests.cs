using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.Scaffolding;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

public class GlobalJsonTests
{
    [Fact]
    public void Write_KeepsUnmanagedProperties()
    {
        using var dir = new TempDir();
        var path = dir.Write("global.json", """
            {
              // comment
              "sdk": { "version": "10.0.100", "rollForward": "latestMajor" },
              "msbuild-sdks": { "Microsoft.Build.Traversal": "4.1.0" },
              "test": { "runner": "Microsoft.Testing.Platform" }
            }
            """);
        GlobalJson.Write(path, "11.0.100", "latestFeature");

        var settings = GlobalJson.Read(path);
        Assert.Equal("11.0.100", settings.Version);
        Assert.Equal("latestFeature", settings.RollForward);
        Assert.Equal("Microsoft.Testing.Platform", settings.TestRunner);
        Assert.Contains("Microsoft.Build.Traversal", File.ReadAllText(path));
    }

    [Fact]
    public void Write_CreatesFile_AndRemovesTestRunner()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "global.json");
        GlobalJson.Write(path, "10.0.401", "disable", "Microsoft.Testing.Platform");
        Assert.Equal("Microsoft.Testing.Platform", GlobalJson.Read(path).TestRunner);
        GlobalJson.Write(path, "10.0.401", "disable", "");
        Assert.Null(GlobalJson.Read(path).TestRunner);
        Assert.DoesNotContain("\"test\"", File.ReadAllText(path));
    }

    [Fact]
    public void Find_WalksUp()
    {
        using var dir = new TempDir();
        var path = dir.Write("global.json", "{}");
        Directory.CreateDirectory(Path.Combine(dir.Path, "src", "App"));
        Assert.Equal(path, GlobalJson.Find(Path.Combine(dir.Path, "src", "App")));
    }
}

public class SdkCatalogTests
{
    [Fact]
    public void ParseFrameworkChoices_ReadsDotnetNewHelp()
    {
        const string help = """
              -f, --framework <choice>               The target framework for the project.
                                                     Type: choice
                                                       net11.0
                                                       net10.0  Target net10.0
                                                       net8.0   Target net8.0
                                                     Default: net11.0
              --no-restore                           If specified, skips the automatic
            """;
        Assert.Equal(["net11.0", "net10.0", "net8.0"], SdkCatalog.ParseFrameworkChoices(help));
    }

    [Theory]
    [InlineData("8.0.418", false, "net8.0")]
    [InlineData("9.0.100", false, "net9.0")]
    [InlineData("9.0.200", true, "net9.0")]
    [InlineData("10.0.401", true, "net10.0")]
    [InlineData("11.0.100-preview.3.25201.1", true, "net11.0")]
    public void Sdk_Capabilities(string version, bool slnx, string framework)
    {
        var sdk = new InstalledSdk(version);
        Assert.Equal(slnx, sdk.SupportsSlnx);
        Assert.Equal(framework, sdk.DefaultFramework);
    }
}

public class CentralPackagesTests
{
    [Fact]
    public void MigrateProject_MovesVersions_AndKeepsExistingCentralVersion()
    {
        using var dir = new TempDir();
        var props = dir.Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="xunit.v3" Version="3.2.0" />
              </ItemGroup>
            </Project>
            """);
        var project = dir.Write("tests/A.Tests/A.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit.v3" Version="3.0.0" />
                <PackageReference Include="coverlet.collector">
                  <Version>6.0.4</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(props, CentralPackages.FindEnabledFile(project));
        var moved = CentralPackages.MigrateProject(props, project);

        Assert.Equal(3, moved.Count);
        var csproj = File.ReadAllText(project);
        Assert.DoesNotContain("Version", csproj);
        var central = File.ReadAllText(props);
        Assert.Contains("""<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />""", central);
        Assert.Contains("""<PackageVersion Include="coverlet.collector" Version="6.0.4" />""", central);
        Assert.Contains("""<PackageVersion Include="xunit.v3" Version="3.2.0" />""", central); // existing wins
        Assert.DoesNotContain("3.0.0", central);
    }

    [Theory]
    [InlineData("Shop", true)]
    [InlineData("Shop.Api", true)]
    [InlineData("shop-api_2", true)]
    [InlineData("2Shop", false)]
    [InlineData("Shop Api", false)]
    [InlineData("Shop.", false)]
    [InlineData("../x", false)]
    public void ValidateName(string name, bool valid) => Assert.Equal(valid, SolutionScaffolder.ValidateName(name) is null);
}

/// <summary>Creates a real solution with the dotnet CLI and builds it (needs SDK 10 and the NuGet cache or network).</summary>
public class SolutionScaffolderIntegrationTests
{
    [Fact]
    public async Task CreateSolution_WithPinnedSdk_CpmAndTests_Builds()
    {
        var ct = TestContext.Current.CancellationToken;
        var sdk = (await SdkCatalog.ListAsync(ct)).FirstOrDefault(s => s.Major == 10 && !s.IsPreview);
        Assert.SkipWhen(sdk is null, "SDK 10 is not installed.");

        using var dir = new TempDir();
        var log = new List<string>();
        var request = new NewSolutionRequest(dir.Path, "Shop", sdk.Version, "disable", "net10.0", Slnx: true,
            ProjectTemplates.Get("webapi"), null, AddTests: true, DirectoryBuildProps: true, CentralPackageManagement: true,
            EditorConfig: true, CSharpier: false, GitInit: false);
        var solution = await new SolutionScaffolder(NullLogger<SolutionScaffolder>.Instance).CreateSolutionAsync(request, l => { lock (log) log.Add(l); }, ct);

        var folder = Path.Combine(dir.Path, "Shop");
        Assert.Equal(Path.Combine(folder, "Shop.slnx"), solution);
        Assert.Equal(sdk.Version, GlobalJson.Read(Path.Combine(folder, "global.json")).Version);
        Assert.Equal(sdk.Version, await SdkInfo.ResolveVersionAsync(folder, ct));
        Assert.True(File.Exists(Path.Combine(folder, "src", "Shop.Api", "Shop.Api.csproj")));
        Assert.True(File.Exists(Path.Combine(folder, "tests", "Shop.Tests", "Shop.Tests.csproj")));
        Assert.DoesNotContain("Version=", File.ReadAllText(Path.Combine(folder, "tests", "Shop.Tests", "Shop.Tests.csproj")));
        Assert.Contains("Shop.Api", File.ReadAllText(Path.Combine(folder, "tests", "Shop.Tests", "Shop.Tests.csproj")));

        var exit = await ProcessRunner.RunAsync("dotnet", ["build", solution, "-nologo", "-v", "q"], folder, (l, _) => { lock (log) log.Add(l); }, cancellationToken: ct);
        Assert.True(exit == 0, string.Join("\n", log.TakeLast(40)));
    }
}
