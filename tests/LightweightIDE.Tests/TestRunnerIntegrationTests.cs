using LightweightIDE.DotNet.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

/// <summary>A real xUnit v3 project on Microsoft.Testing.Platform without Microsoft.Testing.Extensions.TrxReport.</summary>
[Trait("Category", "Integration")]
public class TestRunnerIntegrationTests
{
    [Fact]
    public async Task MtpWithoutTrxReport_ReadsXunitTrx_AndRemembersIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        dir.Write("global.json", """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var project = dir.Write("T/T.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="xunit.v3" Version="4.0.3" /></ItemGroup>
            </Project>
            """);
        dir.Write("T/CartTests.cs", """
            using Xunit;
            namespace Shop.Tests;
            public class CartTests
            {
                [Fact] public void Adds() => Assert.Equal(2, 1 + 1);
                [Fact(DisplayName = "Dividing fails")] public void Divides() => Assert.Equal(3, 1 + 1);
                [Theory, InlineData(1), InlineData(2)] public void Total(int count) => Assert.True(count > 0);
            }
            """);
        var runner = new TestRunner(NullLogger<TestRunner>.Instance);
        var output = new List<string>();
        void Collect(string line) { lock (output) output.Add(line); }

        var first = await runner.RunAsync(project, dir.Path, "Debug", null, Collect, ct);
        Assert.True(first.Mode.Contains("xUnit TRX"), first.Mode + "\n" + string.Join("\n", output));
        Assert.Equal(["Shop.Tests.CartTests.Adds", "Shop.Tests.CartTests.Divides", "Shop.Tests.CartTests.Total", "Shop.Tests.CartTests.Total"],
            first.Tests.Select(t => t.Key).Order(StringComparer.Ordinal));
        Assert.Equal(1, first.Failed);

        // The next run starts with the report that worked: no rejected attempt.
        output.Clear();
        var second = await runner.RunAsync(project, dir.Path, "Debug", ["Shop.Tests.CartTests.Adds"], Collect, ct);
        Assert.DoesNotContain(output, l => l.Contains("was rejected", StringComparison.Ordinal));
        Assert.Equal("Shop.Tests.CartTests.Adds", Assert.Single(second.Tests).Key);

        // The last fallback reads the real console output of the same run.
        var lines = new List<string>();
        var args = TestRunner.Arguments(TestRunnerMode.MicrosoftTestingPlatform, project, "Debug", Path.Combine(dir.Path, "r"), null, TestReport.Console);
        await LightweightIDE.DotNet.Build.ProcessRunner.RunAsync("dotnet", args, dir.Path, (l, _) => { lock (lines) lines.Add(l); }, cancellationToken: ct);
        var fromConsole = ConsoleResultParser.Parse(lines);
        // The console has only the display name: a [Fact(DisplayName = …)] cannot be matched to its method there.
        Assert.Equal([("Dividing fails", TestOutcome.Failed), ("Shop.Tests.CartTests.Adds", TestOutcome.Passed),
                ("Shop.Tests.CartTests.Total", TestOutcome.Passed), ("Shop.Tests.CartTests.Total", TestOutcome.Passed)],
            fromConsole.Select(t => (t.Key, t.Outcome)).Order());
        Assert.Contains("Expected: 3", Assert.Single(fromConsole, t => t.Outcome == TestOutcome.Failed).ErrorMessage);
    }
}
