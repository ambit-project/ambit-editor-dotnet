using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.DotNet.SolutionDiscovery;

namespace LightweightIDE.Tests;

public class SolutionTests
{
    [Fact]
    public async Task ReadsSlnxFoldersAndProjects()
    {
        using var dir = new TempDir();
        dir.Write("src/Api/Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>""");
        dir.Write("tests/Api.Tests/Api.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks></PropertyGroup><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>""");
        dir.Write("App.slnx", """
            <Solution>
              <Folder Name="/src/"><Project Path="src/Api/Api.csproj" /></Folder>
              <Folder Name="/tests/"><Project Path="tests/Api.Tests/Api.Tests.csproj" /></Folder>
            </Solution>
            """);

        var root = await SolutionLoader.DiscoverAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(root.Solution);
        Assert.Equal(["Api", "Api.Tests"], root.Solution.Projects.Select(p => p.Name));
        Assert.Equal("/src/", root.Solution.Projects[0].FolderPath);
        Assert.Equal(Path.Combine(dir.Path, "src/Api/Api.csproj"), root.Solution.Projects[0].Path);
        Assert.Equal(root.Solution.Path, root.BuildTarget);

        var info = ProjectInfo.Read(root.Solution.Projects[1].Path);
        Assert.True(info.IsTestProject);
        Assert.Equal(["net10.0", "net11.0"], info.TargetFrameworks);
        Assert.Equal("Microsoft.NET.Sdk.Web", ProjectInfo.Read(root.Solution.Projects[0].Path).Sdk);
    }

    [Theory]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Web" />""", ProjectKind.Web)]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk.Worker" />""", ProjectKind.Worker)]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""", ProjectKind.Console)]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>""", ProjectKind.Console)]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk" />""", ProjectKind.Library)]
    [InlineData("""<Project><Sdk Name="Microsoft.NET.Sdk.Web" /></Project>""", ProjectKind.Web)]
    // xUnit v3 / Microsoft.Testing.Platform test projects are OutputType Exe, but are not run with dotnet run.
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>""", ProjectKind.Test)]
    public void DetectsProjectKind(string csproj, ProjectKind expected)
    {
        using var dir = new TempDir();
        var info = ProjectInfo.Read(dir.Write("App/App.csproj", csproj));
        Assert.Equal(expected, info.Kind);
        Assert.Equal(expected is ProjectKind.Web or ProjectKind.Worker or ProjectKind.Console, info.IsRunnable);
    }

    [Fact]
    public async Task ReadsClassicSln()
    {
        using var dir = new TempDir();
        dir.Write("A/A.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        dir.Write("Legacy.sln", """

            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "A", "A\A.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);
        var root = await SolutionLoader.DiscoverAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        var project = Assert.Single(root.Solution!.Projects);
        Assert.Equal(Path.Combine(dir.Path, "A", "A.csproj"), project.Path);
    }

    [Fact]
    public async Task FolderWithoutSolution_FindsProjects()
    {
        using var dir = new TempDir();
        dir.Write("one/One.csproj", "<Project />");
        dir.Write("one/bin/Debug/Copy.csproj", "<Project />");
        var root = await SolutionLoader.DiscoverAsync(dir.Path, ct: TestContext.Current.CancellationToken);
        Assert.Null(root.Solution);
        Assert.Equal(Path.Combine(dir.Path, "one/One.csproj"), Assert.Single(root.LooseProjects));
        Assert.Equal(root.LooseProjects[0], root.BuildTarget);
    }

    [Fact]
    public void FileTree_SkipsGeneratedDirectories_AndSortsDirectoriesFirst()
    {
        using var dir = new TempDir();
        dir.Write("b.cs", "");
        dir.Write("A/x.cs", "");
        dir.Write("bin/out.dll", "");
        dir.Write("obj/project.assets.json", "");
        dir.Write(".git/HEAD", "ref: refs/heads/feature/x");
        dir.Write("node_modules/p/index.js", "");
        var names = FileTree.ListChildren(dir.Path).Select(e => e.Name).ToList();
        Assert.Equal(["A", "b.cs"], names);
        Assert.Equal("feature/x", GitInfo.CurrentBranch(Path.Combine(dir.Path, "A")));
        Assert.True(FileTree.IsInExcludedDirectory(dir.Path, Path.Combine(dir.Path, "src", "obj", "x.cs")));
        Assert.Equal(2, FileTree.EnumerateFiles(dir.Path, TestContext.Current.CancellationToken).Count());
    }
}
