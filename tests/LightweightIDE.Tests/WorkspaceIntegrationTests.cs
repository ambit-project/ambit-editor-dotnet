using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.SolutionDiscovery;
using LightweightIDE.Language.Completion;
using LightweightIDE.Language.Diagnostics;
using LightweightIDE.Language.Navigation;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

/// <summary>
/// Loads a real project with MSBuildWorkspace through the installed SDK. Runs once per SDK line:
/// net10.0 pinned to SDK 10 via global.json, net11.0 with C# 15 syntax on SDK 11.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceIntegrationTests
{
    public static TheoryData<string, string?, string> Targets => new()
    {
        { "net10.0", "10.0.100", "public sealed class Cat; public sealed class Dog;" },
        { "net11.0", null, "public sealed class Cat; public sealed class Dog; public union Pet(Cat, Dog);" },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task LoadsProject_AndProvidesLanguageFeatures(string tfm, string? sdk, string extraTypes)
    {
        var ct = TestContext.Current.CancellationToken;
        var sdks = await SdkInfo.ListSdksAsync(ct);
        Assert.SkipUnless(sdks.Any(s => s.StartsWith(tfm[3..5], StringComparison.Ordinal)), $"SDK for {tfm} not installed");

        using var dir = new TempDir();
        if (sdk is not null) dir.Write("global.json", $$"""{ "sdk": { "version": "{{sdk}}", "rollForward": "latestFeature" } }""");
        var project = dir.Write("Calc/Calc.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>{tfm}</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        var mathPath = dir.Write("Calc/MathOps.cs", $$"""
            namespace Calc;
            public static class MathOps
            {
                public static int Add(int a, int b) => a + b;
            }
            {{extraTypes}}
            """);
        var programText = """
            namespace Calc;
            public class Program
            {
                public static int Run() => MathOps.Add(1, 2) + MathOps.Add(3, 4);
            }
            """;
        var programPath = dir.Write("Calc/Program.cs", programText);
        var restoreOutput = new List<string>();
        var restore = await ProcessRunner.RunAsync("dotnet", ["restore", project], dir.Path, (l, _) => { lock (restoreOutput) restoreOutput.Add(l); }, cancellationToken: ct);
        Assert.True(restore == 0, string.Join('\n', restoreOutput));

        var options = new LanguageOptions();
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, options);
        var root = await SolutionLoader.DiscoverAsync(Path.Combine(dir.Path, "Calc"), ct: ct);
        await workspace.LoadAsync(root);
        Assert.Equal(WorkspaceState.Ready, workspace.State);
        Assert.Empty(workspace.LoadErrors);

        // Diagnostics: clean file, then an unsaved edit with a type error
        var diagnostics = new DiagnosticsService(workspace);
        Assert.DoesNotContain(await diagnostics.GetDocumentDiagnosticsAsync(programPath, programText, ct), d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
        var broken = programText.Replace("MathOps.Add(3, 4)", "MathOps.Add(\"x\", 4)");
        workspace.UpdateText(programPath, broken);
        var errors = await diagnostics.GetDocumentDiagnosticsAsync(programPath, broken, ct);
        Assert.Contains(errors, d => d.Diagnostic.Code == "CS1503");
        workspace.UpdateText(programPath, programText);
        Assert.Empty(await diagnostics.GetProjectDiagnosticsAsync(project, ct));

        // Go to definition / find references
        var navigation = new NavigationService(workspace, options);
        var addPosition = programText.IndexOf("Add(1", StringComparison.Ordinal);
        var definition = await navigation.GoToDefinitionAsync(programPath, programText, addPosition, ct);
        var target = Assert.Single(definition!.Locations);
        Assert.Equal(mathPath, target.Path);
        Assert.Equal(4, target.Line);

        var references = await navigation.FindReferencesAsync(programPath, programText, addPosition, ct);
        Assert.Equal(3, references!.Locations.Count); // declaration + 2 calls

        var hover = await navigation.GetQuickInfoAsync(programPath, programText, addPosition, ct);
        Assert.Contains("int MathOps.Add(int a, int b)", hover);

        // Completion after "MathOps."
        var typing = programText.Replace("MathOps.Add(1, 2) + MathOps.Add(3, 4)", "MathOps.");
        workspace.UpdateText(programPath, typing);
        var completions = new CompletionProvider(workspace);
        var position = typing.IndexOf("MathOps.", StringComparison.Ordinal) + "MathOps.".Length;
        var session = await completions.GetCompletionsAsync(programPath, typing, position, explicitInvoke: false, ct);
        var add = Assert.Single(session!.Items, i => i.DisplayText == "Add");
        var edit = await completions.GetCommitEditAsync(session, add, ct);
        Assert.Equal("Add", edit.NewText);

        // C# 15 on .NET 11: the union type is understood semantically
        if (tfm == "net11.0")
        {
            var unionUse = programText.Replace("public static int Run()", "public static string Name(Pet p) => p switch { Cat => \"cat\", Dog => \"dog\" };\n    public static int Run()");
            workspace.UpdateText(programPath, unionUse);
            Assert.DoesNotContain(await diagnostics.GetDocumentDiagnosticsAsync(programPath, unionUse, ct), d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
        }
    }
}
