using LightweightIDE.DotNet.Formatting;
using LightweightIDE.DotNet.Packages;
using LightweightIDE.Editor;
using LightweightIDE.Language.Formatting;
using LightweightIDE.Language.Refactoring;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

public class NavigationHistoryTests
{
    static NavigationPoint P(string path, int line) => new(path, line, 1);

    [Fact]
    public void BackAndForward_WalkTheJumps()
    {
        var history = new NavigationHistory();
        history.Record(P("/a.cs", 10), P("/b.cs", 5));
        history.Record(P("/b.cs", 5), P("/c.cs", 1));

        Assert.Equal(P("/b.cs", 5), history.GoBack(P("/c.cs", 1)));
        Assert.Equal(P("/a.cs", 10), history.GoBack(P("/b.cs", 5)));
        Assert.Null(history.GoBack(P("/a.cs", 10)));
        Assert.Equal(P("/b.cs", 5), history.GoForward(P("/a.cs", 10)));
        Assert.Equal(P("/c.cs", 1), history.GoForward(P("/b.cs", 5)));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void NewJump_ClearsForward_AndSameLineIsIgnored()
    {
        var history = new NavigationHistory();
        history.Record(P("/a.cs", 1), P("/a.cs", 1));
        Assert.False(history.CanGoBack);

        history.Record(P("/a.cs", 1), P("/b.cs", 1));
        history.GoBack(P("/b.cs", 1));
        Assert.True(history.CanGoForward);
        history.Record(P("/a.cs", 1), P("/c.cs", 1));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void RenameAndRemove_UpdateEntries()
    {
        var history = new NavigationHistory();
        history.Record(P("/src/old/A.cs", 3), P("/x.cs", 1));
        history.Record(P("/x.cs", 1), P("/y.cs", 1));
        history.Rename("/src/old", "/src/new");
        Assert.Contains(P("/src/new/A.cs", 3), history.BackEntries);
        history.Remove("/x.cs");
        Assert.DoesNotContain(history.BackEntries, e => e.Path == "/x.cs");
    }

    [Fact]
    public void Capacity_DropsOldest()
    {
        var history = new NavigationHistory(capacity: 3);
        for (var i = 0; i < 10; i++) history.Record(P("/a.cs", i * 10), P("/b.cs", i));
        Assert.Equal(3, history.BackEntries.Count());
        Assert.Equal(70, history.BackEntries.First().Line);
    }
}

public class TextDiffTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("", "a\nb\n")]
    [InlineData("a\nb\n", "")]
    [InlineData("class A{int b;}", "class A\n{\n    int b;\n}\n")]
    [InlineData("a\r\nb\r\nc\r\n", "a\r\nB\r\nc\r\n")]
    [InlineData("one\ntwo\nthree\nfour\n", "zero\none\nthree\nfour\nfive\n")]
    public void Apply_ReproducesNewText(string oldText, string newText) =>
        Assert.Equal(newText, TextDiff.Apply(oldText, TextDiff.Compute(oldText, newText)));

    [Fact]
    public void Apply_ReproducesNewText_ForRandomEdits()
    {
        var random = new Random(42);
        string[] pool = ["{", "}", "    int x;", "", "// c", "return 1;", "a", "b", "\t", "x\r"];
        for (var round = 0; round < 500; round++)
        {
            string Make() => string.Join(random.Next(3) == 0 ? "\r\n" : "\n", Enumerable.Range(0, random.Next(0, 25)).Select(_ => pool[random.Next(pool.Length)]));
            var a = Make();
            var b = random.Next(4) == 0 ? Make() : Mutate(a, random);
            Assert.Equal(b, TextDiff.Apply(a, TextDiff.Compute(a, b)));
        }
    }

    static string Mutate(string text, Random random)
    {
        var lines = text.Split('\n').ToList();
        for (var i = 0; i < random.Next(1, 4); i++)
        {
            var at = random.Next(lines.Count + 1);
            if (random.Next(2) == 0 && lines.Count > 0 && at < lines.Count) lines.RemoveAt(at);
            else lines.Insert(at, "    inserted " + i);
        }
        return string.Join('\n', lines);
    }

    [Fact]
    public void Compute_KeepsUnchangedLinesOutOfTheEdits()
    {
        var oldText = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}")) + "\n";
        var newText = oldText.Replace("line 100\n", "line 100 changed\n");
        var edit = Assert.Single(TextDiff.Compute(oldText, newText));
        Assert.Equal(" changed", edit.NewText);
        Assert.Equal(0, edit.Length);
    }
}

public class RenameServiceTests
{
    const string Code = """
        namespace Shop;

        public class Calculator
        {
            public int Add(int a, int b) => a + b;
            public int Twice(int a) => Add(a, a);
        }
        """;

    static int PositionOf(string text, string marker) => text.IndexOf(marker, StringComparison.Ordinal) + 1;

    [Fact]
    public async Task Plan_RenamesDeclarationAndUses()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var service = new RenameService(workspace);
        var position = PositionOf(Code, "Add(int");

        var target = await service.GetTargetAsync("/tmp/Shop/Calculator.cs", Code, position, ct);
        Assert.NotNull(target);
        Assert.Equal("Add", target.Name);
        Assert.Null(target.Error);

        var plan = await service.PlanAsync("/tmp/Shop/Calculator.cs", Code, position, "Sum", ct);
        var file = Assert.Single(plan.Files);
        Assert.Equal(2, file.Changes);
        Assert.Contains("public int Sum(int a, int b)", file.NewText);
        Assert.Contains("=> Sum(a, a)", file.NewText);
    }

    [Fact]
    public async Task Target_FromMetadata_CannotBeRenamed()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var target = await new RenameService(workspace).GetTargetAsync("/tmp/Shop/Calculator.cs", Code, PositionOf(Code, "int Add"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(target?.Error);
    }

    [Theory]
    [InlineData("Sum", true)]
    [InlineData("_value2", true)]
    [InlineData("@class", true)]
    [InlineData("class", false)]
    [InlineData("2abc", false)]
    [InlineData("a-b", false)]
    [InlineData("", false)]
    public void ValidateName(string name, bool valid) => Assert.Equal(valid, RenameService.ValidateName(name) is null);
}

public class FormattingTests
{
    [Fact]
    public void Manifest_WithCSharpier_IsFound()
    {
        using var dir = new TempDir();
        dir.Write(".config/dotnet-tools.json", """
            { "version": 1, "isRoot": true,
              "tools": { "csharpier": { "version": "1.4.0", "commands": ["csharpier"], "rollForward": false } } }
            """);
        var file = dir.Write("src/App/Program.cs", "class A {}");
        var tool = CSharpierClient.Find(file);
        Assert.NotNull(tool);
        Assert.True(tool.IsLocal);
        Assert.Equal("1.4.0", tool.Version);
        Assert.Equal(dir.Path, tool.WorkingDirectory);
        Assert.Equal(["csharpier", "server"], tool.ServerArguments);
    }

    [Fact]
    public void OldCSharpier_UsesServerFlag()
    {
        var tool = new CSharpierTool("dotnet", ["csharpier"], "/tmp", "0.30.6", true);
        Assert.Equal(["csharpier", "--server"], tool.ServerArguments);
    }

    [Fact]
    public void Manifest_WithoutCSharpier_IsIgnored()
    {
        using var dir = new TempDir();
        var manifest = dir.Write(".config/dotnet-tools.json", """{ "version": 1, "tools": { "dotnet-ef": { "version": "10.0.0" } } }""");
        Assert.Null(CSharpierClient.ReadManifestVersion(manifest));
    }

    [Fact]
    public async Task Roslyn_FormatsWhenSelected()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        using var csharpier = new CSharpierClient(NullLogger<CSharpierClient>.Instance);
        var service = new FormattingService(workspace, csharpier, new FormattingOptions { Engine = FormatterEngine.Roslyn });
        var result = await service.FormatAsync("/tmp/x/A.cs", "class A{int b;   void M(){}}", TestContext.Current.CancellationToken);
        Assert.Null(result.Error);
        Assert.Contains("int b;", result.Text);
        Assert.DoesNotContain("int b;   void", result.Text);
    }
}

public class PackageServiceTests
{
    [Fact]
    public void ParseList_MergesFrameworks_AndMarksOutdated()
    {
        var result = PackageService.ParseList("""
            { "version": 1, "projects": [ { "path": "/p/A.csproj", "frameworks": [
              { "framework": "net10.0", "topLevelPackages": [
                { "id": "Serilog", "requestedVersion": "4.2.0", "resolvedVersion": "4.2.0", "latestVersion": "4.4.0" },
                { "id": "Dapper", "requestedVersion": "2.1.66", "resolvedVersion": "2.1.66", "latestVersion": "2.1.66" } ] },
              { "framework": "net11.0", "topLevelPackages": [
                { "id": "Serilog", "requestedVersion": "4.2.0", "resolvedVersion": "4.2.0", "latestVersion": "4.4.0" } ] } ] } ] }
            """);
        Assert.Equal(["Dapper", "Serilog"], result.Packages.Select(p => p.Id));
        var serilog = result.Packages[1];
        Assert.True(serilog.IsOutdated);
        Assert.Equal("net10.0, net11.0", serilog.Framework);
        Assert.False(result.Packages[0].IsOutdated);
    }

    [Fact]
    public void ParseSearch_DeduplicatesAndKeepsProblems()
    {
        var result = PackageService.ParseSearch("""
            { "version": 2, "problems": [], "searchResult": [
              { "sourceName": "github", "problems": [ { "text": "Failed to retrieve metadata", "problemType": "Error" } ], "packages": [] },
              { "sourceName": "nuget.org", "packages": [
                { "id": "Serilog.Sinks.File", "latestVersion": "7.0.0", "totalDownloads": 1331996489, "owners": "serilog" } ] },
              { "sourceName": "mirror", "packages": [ { "id": "serilog.sinks.file", "latestVersion": "7.0.0" } ] } ] }
            """);
        var hit = Assert.Single(result.Packages);
        Assert.Equal("nuget.org", hit.Source);
        Assert.Equal(1331996489, hit.TotalDownloads);
        Assert.Equal("github: Failed to retrieve metadata", Assert.Single(result.Problems));
    }

    [Fact]
    public void CentralPackagesFile_IsFoundOnlyWhenEnabled()
    {
        using var dir = new TempDir();
        var project = dir.Write("src/A/A.csproj", "<Project />");
        Assert.Null(PackageService.FindCentralPackagesFile(project));
        var props = dir.Write("Directory.Packages.props",
            "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Assert.Equal(props, PackageService.FindCentralPackagesFile(project));
    }
}

public class RenameFileTests
{
    const string Code = """
        namespace Shop;
        public class Calculator
        {
            public int Add(int a, int b) => a + b;
        }
        """;

    [Fact]
    public async Task MethodRename_DoesNotRenameFile_TypeRenameDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var service = new RenameService(workspace);
        const string path = "/tmp/Shop/Calculator.cs";

        var method = await service.PlanAsync(path, Code, Code.IndexOf("Add(", StringComparison.Ordinal) + 1, "Sum", ct);
        Assert.Null(Assert.Single(method.Files).NewPath);

        var type = await service.PlanAsync(path, Code, Code.IndexOf("Calculator", StringComparison.Ordinal) + 1, "Calc", ct);
        Assert.Equal("/tmp/Shop/Calc.cs", Assert.Single(type.Files).NewPath);
    }
}

public class CodeActionServiceTests
{
    static async Task<(CodeActionService Service, IReadOnlyList<CodeActionItem> Items)> ActionsAt(WorkspaceService workspace, string code, string marker)
    {
        var service = new CodeActionService(workspace, NullLogger<CodeActionService>.Instance);
        var position = code.IndexOf(marker, StringComparison.Ordinal) + 1;
        return (service, await service.GetActionsAsync("/tmp/actions/A.cs", code, position, position, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingUsing_IsOffered_AndApplied()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        const string code = "class A\n{\n    StringBuilder b = new StringBuilder();\n}\n";
        var (service, items) = await ActionsAt(workspace, code, "StringBuilder b");
        var add = Assert.Single(items, i => i.Title.Contains("using System.Text", StringComparison.Ordinal));
        Assert.Same(add, items[0]); // the using comes first
        Assert.Equal("CS0246", add.Fixes);

        var edit = Assert.Single(await service.ApplyAsync(add.Id, TestContext.Current.CancellationToken));
        Assert.StartsWith("using System.Text;", edit.NewText);
    }

    [Fact]
    public async Task ImplementInterface_IsOffered()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        const string code = "using System;\nclass A : IDisposable\n{\n}\n";
        var (service, items) = await ActionsAt(workspace, code, "IDisposable");
        var implement = items.First(i => i.Title.StartsWith("Implement interface", StringComparison.Ordinal));
        var edit = Assert.Single(await service.ApplyAsync(implement.Id, TestContext.Current.CancellationToken));
        Assert.Contains("public void Dispose()", edit.NewText);
    }

    [Fact]
    public async Task ImplementInterface_OnTheClassName_WhenTheBaseListIsOnAnotherLine()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        const string code = "using System;\npublic class Cached(\n    IDisposable inner)\n    : IDisposable\n{\n}\n";
        var (_, items) = await ActionsAt(workspace, code, "Cached");
        Assert.Contains(items, i => i.Title == "Implement interface");
    }

    [Fact]
    public async Task ImplementMissingMembers_FromInsideTheClass_PlainImplementationFirst()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        const string code = "using System;\nabstract class Base { public abstract int Count { get; } }\nclass A : Base, IDisposable\n{\n    int x;\n}\n";
        var service = new CodeActionService(workspace, NullLogger<CodeActionService>.Instance);
        var items = await service.GetImplementMembersAsync("/tmp/actions/A.cs", code, code.IndexOf("int x", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Title == "Implement interface");
        Assert.Contains(items, i => i.Title == "Implement abstract class");
        Assert.All(items.Take(2), i => Assert.DoesNotContain("explicitly", i.Title, StringComparison.Ordinal));
        var edit = Assert.Single(await service.ApplyAsync(items.First(i => i.Title == "Implement interface").Id, TestContext.Current.CancellationToken));
        Assert.Contains("public void Dispose()", edit.NewText);
    }

    [Fact]
    public async Task Refactorings_AreOffered_WithoutDiagnostics()
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        const string code = "class A\n{\n    int M(int x) { return x + 1; }\n}\n";
        var (_, items) = await ActionsAt(workspace, code, "return");
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.Null(i.Fixes));
        Assert.True(CodeActionService.ProviderCount > 100);
    }
}

public class GoToImplementationTests
{
    const string Code = """
        interface IShape { double Area(); }
        class Square : IShape { public double Area() => 4; }
        class Circle : IShape { public double Area() => 3.14; }
        abstract class Animal { public virtual string Sound() => ""; }
        class Dog : Animal { public override string Sound() => "woof"; }
        """;

    static async Task<LightweightIDE.Language.Navigation.ReferencesResult?> Find(string marker)
    {
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        var navigation = new LightweightIDE.Language.Navigation.NavigationService(workspace, new LanguageOptions());
        return await navigation.FindImplementationsAsync("/tmp/impl/Shapes.cs", Code, Code.IndexOf(marker, StringComparison.Ordinal) + 1,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Interface_ListsImplementingTypes()
    {
        var result = await Find("IShape {");
        Assert.Equal(2, result!.Locations.Count);
        Assert.All(result.Locations, l => Assert.Matches("class (Square|Circle)", l.LineText));
    }

    [Fact]
    public async Task InterfaceMethod_ListsImplementations()
    {
        var result = await Find("Area();");
        Assert.Equal(2, result!.Locations.Count);
    }

    [Fact]
    public async Task VirtualMethod_ListsOverrides_AndClass_ListsDerived()
    {
        Assert.Contains("class Dog", Assert.Single((await Find("Sound() => \"\""))!.Locations).LineText);
        Assert.Contains("class Dog", Assert.Single((await Find("Animal {"))!.Locations).LineText);
    }
}
