using LightweightIDE.DotNet.Packages;

namespace LightweightIDE.Tests;

public class CentralPackageTests
{
    [Theory]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("2.0.0", "2.0.0-rc.1", 1)]
    [InlineData("2.0.0-beta.2", "2.0.0-alpha.9", 1)]
    [InlineData("10.0.0", "9.9.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    public void NuGetVersionComparer_Orders(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(NuGetVersionComparer.Instance.Compare(a, b)));
        Assert.Equal(-sign, Math.Sign(NuGetVersionComparer.Instance.Compare(b, a)));
    }

    [Fact]
    public void Read_ShowsUsage_SetVersionAndRemoveKeepTheFile()
    {
        using var dir = new TempDir();
        var props = dir.Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <!-- logging -->
                <PackageVersion Include="Serilog" Version="4.2.0" />
                <PackageVersion Include="Dapper" Version="2.1.66" />
                <PackageVersion Include="Unused.Thing" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        var a = dir.Write("src/A/A.csproj", """<Project><ItemGroup><PackageReference Include="Serilog" /><PackageReference Include="Dapper" VersionOverride="2.0.0" /></ItemGroup></Project>""");
        var b = dir.Write("src/B/B.csproj", """<Project><ItemGroup><PackageReference Include="serilog" /></ItemGroup></Project>""");

        var packages = CentralPackageService.Read(props, [a, b]);
        Assert.Equal(["Dapper", "Serilog", "Unused.Thing"], packages.Select(p => p.Id));
        Assert.Equal(2, packages[1].Projects.Count);              // case-insensitive ids
        Assert.Equal([a], packages[0].Overrides);
        Assert.Empty(packages[2].Projects);

        Assert.True(CentralPackageService.SetVersion(props, "serilog", "4.4.0"));
        Assert.True(CentralPackageService.Remove(props, "Unused.Thing"));
        var text = File.ReadAllText(props);
        Assert.Contains("""<PackageVersion Include="Serilog" Version="4.4.0" />""", text);
        Assert.DoesNotContain("Unused.Thing", text);
        Assert.Contains("<!-- logging -->", text);                 // comments and layout kept
        Assert.Contains("\n    <PackageVersion Include=\"Dapper\"", text);
    }

    [Fact]
    public void Conversion_PicksHighestVersion_ReportsConflicts_AndMigratesProjects()
    {
        using var dir = new TempDir();
        var a = dir.Write("src/A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.2.0" />
                <PackageReference Include="Dapper" Version="2.1.66" />
              </ItemGroup>
            </Project>
            """);
        var b = dir.Write("src/B/B.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.10.0" />
              </ItemGroup>
            </Project>
            """);
        var plan = CentralPackageService.PlanConversion(dir.Path, [a, b]);
        Assert.Equal("4.10.0", plan.Versions["Serilog"]);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("Serilog", conflict.Id);

        var changed = CentralPackageService.Convert(plan);
        Assert.Equal(2, changed.Count);
        Assert.DoesNotContain("Version=", File.ReadAllText(a));
        var packages = CentralPackageService.Read(plan.PropsPath, [a, b]);
        Assert.Equal("4.10.0", packages.Single(p => p.Id == "Serilog").Version);
        Assert.Equal(plan.PropsPath, LightweightIDE.DotNet.Scaffolding.CentralPackages.FindEnabledFile(a));
    }
}

public class PackageCompletionTests
{
    static PackageCompletionContext? At(string marked)
    {
        var position = marked.IndexOf('|');
        return PackageCompletion.At(marked.Remove(position, 1), position);
    }

    [Fact]
    public void Include_GivesTheTypedIdAndTheWholeValueSpan()
    {
        var context = At("""<ItemGroup><PackageVersion Include="Seri|log.Sinks" Version="1.0" /></ItemGroup>""");
        Assert.NotNull(context);
        Assert.Equal(PackageField.Id, context.Field);
        Assert.Equal("Seri", context.Typed);
        Assert.Equal("Serilog.Sinks".Length, context.ValueEnd - context.ValueStart);
    }

    [Fact]
    public void UnclosedValue_NeverReachesIntoTheNextLine()
    {
        var context = At("<ItemGroup>\n    <PackageVersion Include=\"Serilog.Sin|\n    <PackageVersion Include=\"coverlet\" Version=\"1.0\" />\n");
        Assert.NotNull(context);
        Assert.Equal("Serilog.Sin", context.Typed);
        Assert.Equal(context.ValueStart + "Serilog.Sin".Length, context.ValueEnd); // ends at the caret, not at the next line's quote

        var version = At("<PackageVersion Include=\"A\" Version=\"1.|\n<PackageVersion Include=\"B\" Version=\"2.0\" />");
        Assert.Equal("A", version!.PackageId);
        Assert.Equal(version.ValueStart + 2, version.ValueEnd);
    }

    [Fact]
    public void Version_KnowsThePackage_EvenWhenIncludeComesFirstOrLater()
    {
        Assert.Equal("Dapper", At("""<PackageVersion Include="Dapper" Version="2.|" />""")!.PackageId);
        Assert.Equal("Dapper", At("""<PackageReference Version="|" Include="Dapper" />""")!.PackageId);
        Assert.Equal(PackageField.Version, At("""<PackageReference Include="X" VersionOverride="1|" />""")!.Field);
    }

    [Theory]
    [InlineData("""<PackageVersion Include="A" |Version="1" />""")]       // between attributes
    [InlineData("""<ProjectReference Include="../A|.csproj" />""")]       // another element
    [InlineData("""<PackageVersion Include="A" Version="1" /> text|""")]  // outside the tag
    [InlineData("""<PackageVersion Condition="'$(X)' == '|'" />""")]      // another attribute
    public void Elsewhere_NoCompletion(string marked) => Assert.Null(At(marked));

    [Fact]
    public void ParseVersions_NewestFirst_AcrossSources()
    {
        var versions = PackageService.ParseVersions("""
            { "version": 2, "searchResult": [
              { "sourceName": "nuget.org", "packages": [ { "id": "A", "version": "1.9.0" }, { "id": "A", "version": "1.10.0" }, { "id": "A", "version": "2.0.0-rc.1" } ] },
              { "sourceName": "mirror", "packages": [ { "id": "A", "version": "1.10.0" } ] } ] }
            """);
        Assert.Equal(["2.0.0-rc.1", "1.10.0", "1.9.0"], versions);
    }
}
