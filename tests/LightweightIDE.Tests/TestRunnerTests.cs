using LightweightIDE.DotNet.Testing;

namespace LightweightIDE.Tests;

public class TestRunnerTests
{
    [Fact]
    public void DefaultsToVSTest()
    {
        using var dir = new TempDir();
        dir.Write("global.json", """{ "sdk": { "version": "11.0.100" } }""");
        Assert.Equal(TestRunnerMode.VSTest, TestRunner.DetectMode(dir.Path));
    }

    [Fact]
    public void GlobalJsonSelectsMicrosoftTestingPlatform()
    {
        using var dir = new TempDir();
        dir.Write("global.json", """
            {
              // comments are allowed
              "test": { "runner": "Microsoft.Testing.Platform" },
            }
            """);
        Directory.CreateDirectory(Path.Combine(dir.Path, "src", "A"));
        Assert.Equal(TestRunnerMode.MicrosoftTestingPlatform, TestRunner.DetectMode(Path.Combine(dir.Path, "src", "A")));
    }

    [Fact]
    public void MtpArgumentsUseSolutionFlagAndReportTrx()
    {
        var args = TestRunner.Arguments(TestRunnerMode.MicrosoftTestingPlatform, "/s/A.slnx", "Debug", "/tmp/r", null, trx: true);
        Assert.Equal(["test", "--solution", "/s/A.slnx", "--configuration", "Debug", "--results-directory", "/tmp/r", "--no-banner", "--report-trx"], args);
    }

    [Fact]
    public void VsTestArgumentsUseTrxLogger()
    {
        var args = TestRunner.Arguments(TestRunnerMode.VSTest, "/s/A.csproj", "Debug", "/tmp/r", ["Foo.*"], trx: true);
        Assert.Contains("trx", args);
        Assert.Equal("/s/A.csproj", args[1]);
        Assert.Equal("FullyQualifiedName~Foo.", args[^1]);
    }

    [Fact]
    public void VsTestFilter_MethodsClassesAndTheoryCases()
    {
        Assert.Equal(@"FullyQualifiedName=Shop.Tests.A.Add|FullyQualifiedName~Shop.Tests.B.|FullyQualifiedName=Shop.Tests.A.Div",
            TestRunner.VsTestFilter(["Shop.Tests.A.Add", "Shop.Tests.B.*", "Shop.Tests.A.Div(x: 1)", "Shop.Tests.A.Div(x: 2)"]));
    }

    [Fact]
    public void MtpArguments_RepeatFilterMethod()
    {
        var args = TestRunner.Arguments(TestRunnerMode.MicrosoftTestingPlatform, "/s/T.csproj", "Debug", "/tmp/r", ["N.A.X", "N.A.Y(1)", "N.A.Y(2)"], trx: false);
        Assert.Equal(["--filter-method", "N.A.X", "--filter-method", "N.A.Y"], args.SkipWhile(a => a != "--filter-method").ToList());
    }

    [Fact]
    public void ParsesTrx()
    {
        using var dir = new TempDir();
        var trx = dir.Write("r.trx", """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Calc.Tests.MathTests.Adds" outcome="Passed" duration="00:00:00.0120000" />
                <UnitTestResult testId="2" testName="Calc.Tests.MathTests.Divides" outcome="Failed" duration="00:00:00.5000000">
                  <Output><ErrorInfo><Message>Assert.Equal() Failure</Message>
                  <StackTrace>   at Calc.Tests.MathTests.Divides() in /src/Calc.Tests/MathTests.cs:line 17</StackTrace></ErrorInfo></Output>
                </UnitTestResult>
                <UnitTestResult testId="3" testName="Calc.Tests.MathTests.Skipped" outcome="NotExecuted" duration="00:00:00" />
              </Results>
              <TestDefinitions>
                <UnitTest id="1" name="Adds"><TestMethod className="Calc.Tests.MathTests" name="Adds" /></UnitTest>
                <UnitTest id="2" name="Divides"><TestMethod className="Calc.Tests.MathTests" name="Divides" /></UnitTest>
                <UnitTest id="3" name="Skipped"><TestMethod className="Calc.Tests.MathTests" name="Skipped" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """);
        var results = TrxParser.Parse(trx);
        Assert.Equal(3, results.Count);
        var failed = Assert.Single(results, r => r.Outcome == TestOutcome.Failed);
        Assert.Equal("Divides", failed.Name);
        Assert.Equal("Calc.Tests.MathTests", failed.ClassName);
        Assert.Equal("/src/Calc.Tests/MathTests.cs", failed.SourceFile);
        Assert.Equal(17, failed.SourceLine);
        Assert.Equal(TimeSpan.FromMilliseconds(500), failed.Duration);
        Assert.Single(results, r => r.Outcome == TestOutcome.Skipped);
    }

    [Fact]
    public void MtpArguments_XunitTrxAndConsoleReports()
    {
        var xunit = TestRunner.Arguments(TestRunnerMode.MicrosoftTestingPlatform, "/s/T.csproj", "Debug", "/tmp/r", null, TestReport.XunitTrx);
        Assert.Equal("--report-xunit-trx", xunit[^1]);
        var console = TestRunner.Arguments(TestRunnerMode.MicrosoftTestingPlatform, "/s/T.csproj", "Debug", "/tmp/r", null, TestReport.Console);
        Assert.Equal(["--output", "detailed"], console.TakeLast(2));
        Assert.DoesNotContain(console, a => a.StartsWith("--report", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesConsoleOutput()
    {
        string[] lines =
        [
            "Running tests from /src/T/bin/Debug/net10.0/T.dll (net10.0|x64)",
            "\u001b[32mpassed\u001b[m Shop.Tests.CartTests.Adds (12ms)",
            "  from /src/T/bin/Debug/net10.0/T.dll (net10.0|x64)",
            "failed Shop.Tests.CartTests.Divides (1s 733ms)",
            "  from /src/T/bin/Debug/net10.0/T.dll (net10.0|x64)",
            "  Assert.Equal() Failure: Values differ",
            "  Expected: 2",
            "    at Shop.Tests.CartTests.Divides() in /src/T/CartTests.cs:line 17",
            "  Standard output",
            "    noise",
            "passed Shop.Tests.CartTests.Total(count: 2, name: \"a (b)\") (3ms)",
            "skipped Shop.Tests.Outer+Inner.Later (0ms)",
            "Test run summary: Failed!",
        ];
        var results = ConsoleResultParser.Parse(lines);
        Assert.Equal(4, results.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(12), results[0].Duration);
        var failed = results[1];
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Equal("Shop.Tests.CartTests.Divides", failed.Key);
        Assert.Equal(TimeSpan.FromMilliseconds(1733), failed.Duration);
        Assert.Equal("Assert.Equal() Failure: Values differ\nExpected: 2", failed.ErrorMessage!.ReplaceLineEndings("\n"));
        Assert.Equal("/src/T/CartTests.cs", failed.SourceFile);
        Assert.Equal(17, failed.SourceLine);
        Assert.DoesNotContain("noise", failed.StackTrace);
        Assert.Equal("Shop.Tests.CartTests.Total", results[2].Key);
        Assert.Equal("Shop.Tests.CartTests", results[2].ClassName);
        Assert.Equal(TestOutcome.Skipped, results[3].Outcome);
        Assert.Equal("Shop.Tests.Outer.Inner.Later", results[3].Key);
    }

    [Fact]
    public void TrxKeyUsesTheMethodName_NotTheDisplayName()
    {
        using var dir = new TempDir();
        var trx = dir.Write("r.trx", """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="Adding two numbers works" outcome="Passed" duration="00:00:00.01" />
              </Results>
              <TestDefinitions>
                <UnitTest id="1" name="Adding two numbers works"><TestMethod className="Calc.Tests.MathTests" name="Adds" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """);
        Assert.Equal("Calc.Tests.MathTests.Adds", Assert.Single(TrxParser.Parse(trx)).Key);
    }
}
