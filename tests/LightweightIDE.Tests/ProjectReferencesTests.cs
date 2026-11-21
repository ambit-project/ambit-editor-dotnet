using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.Language.Testing;

namespace LightweightIDE.Tests;

public class ProjectReferencesTests
{
    const string Empty = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    static string WithReferences(params string[] includes) =>
        $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{string.Concat(includes.Select(i => $"<ProjectReference Include=\"{i}\" />"))}</ItemGroup></Project>""";

    [Fact]
    public void Read_ResolvesRelativeWindowsPathsAndSkipsProperties()
    {
        using var dir = new TempDir();
        var api = dir.Write("src/Api/Api.csproj", WithReferences(@"..\Domain\Domain.csproj", "$(Root)/X/X.csproj"));

        var references = ProjectReferences.Read(api);

        Assert.Equal([Path.Combine(dir.Path, "src/Domain/Domain.csproj")], references);
    }

    [Fact]
    public void WouldCreateCycle_DetectsTransitiveDependency()
    {
        using var dir = new TempDir();
        var domain = dir.Write("Domain/Domain.csproj", Empty);
        var app = dir.Write("App/App.csproj", WithReferences("../Domain/Domain.csproj"));
        var api = dir.Write("Api/Api.csproj", WithReferences("../App/App.csproj"));

        Assert.True(ProjectReferences.WouldCreateCycle(domain, api));   // Api → App → Domain
        Assert.True(ProjectReferences.WouldCreateCycle(domain, domain));
        Assert.False(ProjectReferences.WouldCreateCycle(api, domain));
    }

    [Fact]
    public void ReadPackages_TakesVersionsFromTheProjectOrCentralFile()
    {
        using var dir = new TempDir();
        dir.Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.2.0" />
                <PackageVersion Include="xunit.v3" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """);
        var project = dir.Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <PackageReference Include="xunit.v3" VersionOverride="3.1.0" />
              <PackageReference Include="Serilog" />
              <PackageReference Include="Unknown" />
            </ItemGroup></Project>
            """);

        var packages = ProjectReferences.ReadPackages(project);

        Assert.Equal([("Serilog", "4.2.0"), ("Unknown", null), ("xunit.v3", "3.1.0")], packages);
    }

    [Fact]
    public async Task ApplyAsync_AddsAndRemovesWithTheCli()
    {
        using var dir = new TempDir();
        var a = dir.Write("A/A.csproj", Empty);
        var b = dir.Write("B/B.csproj", Empty);
        var c = dir.Write("C/C.csproj", Empty);
        var ct = TestContext.Current.CancellationToken;

        await ProjectReferences.ApplyAsync(a, [b, c], [], _ => { }, ct);
        Assert.Equal([b, c], ProjectReferences.Read(a).Order(StringComparer.Ordinal));

        await ProjectReferences.ApplyAsync(a, [], [b], _ => { }, ct);
        Assert.Equal([c], ProjectReferences.Read(a));
    }
}

public class TestDiscoveryTests
{
    const string Source = """
        using Xunit;
        namespace Shop.Tests;

        public class OrderTests
        {
            [Fact] public void Creates() { }
            [Xunit.Theory, InlineData(1)] public void Totals(int n) { }

            public class Nested
            {
                [SkippableFact] public void Works() { }
            }
        }

        public abstract class BaseTests { [Fact] public void Inherited() { } }

        public class Helpers { public void NotATest() { } }

        [TestClass]
        public class MsTests { [TestMethod] public void Runs() { } }
        """;

    [Fact]
    public void TestClassesIn_FindsClassesWithTestMethods()
    {
        var classes = TestDiscovery.TestClassesIn(Source);

        Assert.Equal(["Shop.Tests.OrderTests.*", "Shop.Tests.OrderTests.Nested.*", "Shop.Tests.MsTests.*"], classes);
    }

    [Fact]
    public void TestNameAt_ReturnsMethodOrClass()
    {
        Assert.Equal("Shop.Tests.OrderTests.Creates", TestDiscovery.TestNameAt(Source, Source.IndexOf("Creates", StringComparison.Ordinal)));
        Assert.Equal("Shop.Tests.Helpers.*", TestDiscovery.TestNameAt(Source, Source.IndexOf("class Helpers", StringComparison.Ordinal) + 6));
        Assert.Null(TestDiscovery.TestNameAt(Source, 0));
    }

    [Fact]
    public void Marks_GiveClassAndMethodLines()
    {
        var marks = TestDiscovery.Marks(Source);

        var lines = Source.Split('\n');
        int LineOf(string text) => Array.FindIndex(lines, l => l.Contains(text, StringComparison.Ordinal)) + 1;
        Assert.Contains(new TestMark(LineOf("public class OrderTests"), "Shop.Tests.OrderTests.*", null), marks);
        Assert.Contains(new TestMark(LineOf("void Creates"), "Shop.Tests.OrderTests.Creates", "Shop.Tests.OrderTests.*"), marks);
        Assert.Contains(new TestMark(LineOf("void Works"), "Shop.Tests.OrderTests.Nested.Works", "Shop.Tests.OrderTests.Nested.*"), marks);
        Assert.DoesNotContain(marks, m => m.Name.Contains("BaseTests", StringComparison.Ordinal) || m.Name.Contains("Helpers", StringComparison.Ordinal));
        Assert.Empty(TestDiscovery.Marks("class A { void B() { } }"));
    }

    [Fact]
    public void SyntaxCheck_BeforeFormatOnType()
    {
        Assert.True(LightweightIDE.Language.Formatting.FormattingService.IsSyntaxValid("class A { void B() { if (true) { } } }"));
        Assert.False(LightweightIDE.Language.Formatting.FormattingService.IsSyntaxValid("class A { void B() { if (true) { } }"));
        Assert.False(LightweightIDE.Language.Formatting.FormattingService.IsSyntaxValid("class A { void B() { var x = ; } }"));
    }

    [Fact]
    public void DiscoverProject_ListsMethodsWithFileAndLine()
    {
        using var dir = new TempDir();
        var project = dir.Write("Shop.Tests/Shop.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var file = dir.Write("Shop.Tests/OrderTests.cs", Source);
        dir.Write("Shop.Tests/bin/Debug/Copy.cs", Source);

        var tests = TestDiscovery.DiscoverProject(project, TestContext.Current.CancellationToken);

        Assert.Equal(["Shop.Tests.OrderTests.Creates", "Shop.Tests.OrderTests.Totals", "Shop.Tests.OrderTests.Nested.Works", "Shop.Tests.MsTests.Runs"],
            tests.Select(t => t.Name));
        var creates = tests[0];
        Assert.Equal((project, file, "Shop.Tests.OrderTests"), (creates.Project, creates.File, creates.Class));
        Assert.Equal(Source.Split('\n').ToList().FindIndex(l => l.Contains("void Creates")) + 1, creates.Line);
    }

    [Fact]
    public void TestClassesUnder_SkipsBinAndObj()
    {
        using var dir = new TempDir();
        dir.Write("Tests/OrderTests.cs", Source);
        dir.Write("Tests/obj/Generated.cs", "class G { [Fact] public void X() { } }");
        dir.Write("Tests/Plain.cs", "class P { }");

        var found = TestDiscovery.TestClassesUnder(dir.Path, TestContext.Current.CancellationToken);

        Assert.Equal([Path.Combine(dir.Path, "Tests/OrderTests.cs")], found.Select(f => f.File));
    }
}

public class SolutionProjectsTests
{
    const string Empty = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    [Fact]
    public async Task AddAndRemove_ChangeTheSolutionOnly()
    {
        using var dir = new TempDir();
        var solution = dir.Write("Demo.slnx", "<Solution />");
        var lib = dir.Write("libs/Lib/Lib.csproj", Empty);
        var ct = TestContext.Current.CancellationToken;

        await SolutionProjects.AddAsync(solution, [lib], "shared", _ => { }, ct);
        var text = File.ReadAllText(solution);
        Assert.Contains("libs/Lib/Lib.csproj", text);
        Assert.Contains("/shared/", text);

        await SolutionProjects.RemoveAsync(solution, [lib], _ => { }, ct);
        Assert.DoesNotContain("Lib.csproj", File.ReadAllText(solution));
        Assert.True(File.Exists(lib));
    }

    [Fact]
    public async Task Add_ReportsTheCliError()
    {
        using var dir = new TempDir();
        var solution = dir.Write("Demo.slnx", "<Solution />");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SolutionProjects.AddAsync(solution, [Path.Combine(dir.Path, "Missing/Missing.csproj")], null, _ => { }, TestContext.Current.CancellationToken));
        Assert.Contains("dotnet sln add failed", error.Message);
    }

    [Fact]
    public void ReferencedBy_ListsDirectUsers()
    {
        using var dir = new TempDir();
        var domain = dir.Write("Domain/Domain.csproj", Empty);
        var app = dir.Write("App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Domain\Domain.csproj" /></ItemGroup></Project>""");
        var web = dir.Write("Web/Web.csproj", Empty);

        Assert.Equal([app], SolutionProjects.ReferencedBy(domain, [domain, app, web]));
    }

    [Fact]
    public void BreakpointStore_ClearFile_RemovesOnlyThatFile()
    {
        var store = new LightweightIDE.Editor.BreakpointStore();
        store.Toggle("/a.cs", 3);
        store.Toggle("/a.cs", 7);
        store.Toggle("/b.cs", 1);
        var changed = new List<string>();
        store.Changed += changed.Add;

        Assert.Equal(2, store.ClearFile("/a.cs"));
        Assert.Equal(0, store.ClearFile("/a.cs"));
        Assert.Equal(["/a.cs"], changed);
        Assert.Equal(1, store.Count);
    }
}
