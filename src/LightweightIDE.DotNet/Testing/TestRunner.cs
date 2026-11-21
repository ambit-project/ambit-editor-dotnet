using System.Text.Json;
using LightweightIDE.DotNet.Build;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.DotNet.Testing;

public enum TestRunnerMode { VSTest, MicrosoftTestingPlatform }

/// <summary>How the results of a run are read.</summary>
public enum TestReport
{
    /// <summary>VSTest <c>--logger trx</c>, MTP <c>--report-trx</c> (Microsoft.Testing.Extensions.TrxReport).</summary>
    Trx,
    /// <summary>MTP <c>--report-xunit-trx</c>: built into xUnit v3, no extra package.</summary>
    XunitTrx,
    /// <summary>MTP <c>--output detailed</c>: the per-test lines of the console output.</summary>
    Console,
}

/// <summary>
/// Runs <c>dotnet test</c> and reads results from TRX files.
/// VSTest mode (default in .NET 10 and 11): <c>--logger trx</c>.
/// Microsoft.Testing.Platform mode (global.json "test.runner", or DOTNET_TEST_RUNNER in .NET 11):
/// <c>--project/--solution</c> and <c>--report-trx</c>, which needs Microsoft.Testing.Extensions.TrxReport in each test app;
/// without it xUnit v3's own <c>--report-xunit-trx</c> is tried, and last the per-test lines of the console output
/// (<c>--output detailed</c>) are read. What worked is remembered per target, so later runs start with it.
/// </summary>
public sealed class TestRunner(ILogger<TestRunner> logger)
{
    /// <summary>MTP exit code for invalid command line arguments (e.g. --report-trx without the TrxReport extension).</summary>
    const int MtpInvalidArguments = 5;

    public static TestRunnerMode DetectMode(string startDirectory)
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_TEST_RUNNER");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase) ? TestRunnerMode.MicrosoftTestingPlatform : TestRunnerMode.VSTest;

        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var globalJson = Path.Combine(dir.FullName, "global.json");
            if (!File.Exists(globalJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(globalJson), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("test", out var test) && test.TryGetProperty("runner", out var runner)
                    && runner.GetString()?.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase) == true)
                    return TestRunnerMode.MicrosoftTestingPlatform;
            }
            catch (Exception e) when (e is JsonException or IOException) { }
            return TestRunnerMode.VSTest; // nearest global.json wins
        }
        return TestRunnerMode.VSTest;
    }

    /// <summary>
    /// VSTest filter for test names: "Ns.Class.Method" runs that method (all cases of a theory), "Ns.Class.*" the class.
    /// Arguments of data-driven cases ("Method(x: 1)") are dropped: VSTest's FullyQualifiedName has none.
    /// </summary>
    public static string VsTestFilter(IReadOnlyList<string> tests) =>
        string.Join("|", tests.Select(t => t.EndsWith(".*", StringComparison.Ordinal)
            ? "FullyQualifiedName~" + Escape(t[..^1])
            : "FullyQualifiedName=" + Escape(WithoutArguments(t))).Distinct());

    static string WithoutArguments(string test) => test.IndexOf('(') is > 0 and var i ? test[..i] : test;

    /// <summary>VSTest filter values escape the filter syntax characters with a backslash.</summary>
    static string Escape(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~') builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, TestReport> mtpReports = new(StringComparer.Ordinal);

    /// <param name="tests">Test names to run ("Ns.Class.Method" or "Ns.Class.*"); null runs everything.</param>
    public static IReadOnlyList<string> Arguments(TestRunnerMode mode, string target, string configuration, string resultsDirectory, IReadOnlyList<string>? tests, bool trx) =>
        Arguments(mode, target, configuration, resultsDirectory, tests, trx ? TestReport.Trx : TestReport.Console);

    public static IReadOnlyList<string> Arguments(TestRunnerMode mode, string target, string configuration, string resultsDirectory, IReadOnlyList<string>? tests, TestReport report)
    {
        var trx = report == TestReport.Trx;
        var args = new List<string> { "test" };
        if (mode == TestRunnerMode.VSTest)
        {
            if (!Directory.Exists(target)) args.Add(target);
            args.AddRange(["--configuration", configuration, "--results-directory", resultsDirectory, "-nologo", "-tl:off"]);
            if (trx) args.AddRange(["--logger", "trx"]);
            if (tests is { Count: > 0 }) args.AddRange(["--filter", VsTestFilter(tests)]);
        }
        else
        {
            if (target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
                args.AddRange(["--solution", target]);
            else if (!Directory.Exists(target))
                args.AddRange(["--project", target]);
            args.AddRange(["--configuration", configuration, "--results-directory", resultsDirectory, "--no-banner"]);
            if (report == TestReport.Trx) args.Add("--report-trx");
            else if (report == TestReport.XunitTrx) args.Add("--report-xunit-trx");
            else args.AddRange(["--output", "detailed"]);
            foreach (var test in (tests ?? []).Select(WithoutArguments).Distinct()) args.AddRange(["--filter-method", test]);
        }
        return args;
    }

    public async Task<TestRunResult> RunAsync(string target, string workingDirectory, string configuration, IReadOnlyList<string>? tests, Action<string> onOutput,
        CancellationToken ct, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var mode = DetectMode(Directory.Exists(target) ? target : Path.GetDirectoryName(target)!);
        var started = DateTime.UtcNow;
        var resultsDir = Path.Combine(Path.GetTempPath(), "lightweight-ide", "test-results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resultsDir);
        var console = new List<string>();
        try
        {
            int exit;
            var report = TestReport.Trx;
            if (mode == TestRunnerMode.VSTest) exit = await RunOnce(report).ConfigureAwait(false);
            else
            {
                report = mtpReports.GetValueOrDefault(target, TestReport.Trx);
                while (true)
                {
                    exit = await RunOnce(report).ConfigureAwait(false);
                    // Exit 5 = invalid arguments: the report option is not registered in this test app.
                    if (exit != MtpInvalidArguments || report == TestReport.Console || HasTrx()) break;
                    var next = report == TestReport.Trx ? TestReport.XunitTrx : TestReport.Console;
                    onOutput(report == TestReport.Trx
                        ? "--report-trx was rejected (Microsoft.Testing.Extensions.TrxReport missing). Trying xUnit v3's --report-xunit-trx."
                        : "--report-xunit-trx was rejected (not xUnit v3). Reading the results from the console output.");
                    report = next;
                }
                if (exit != MtpInvalidArguments) mtpReports[target] = report;
            }
            List<TestCaseResult> results = Directory.EnumerateFiles(resultsDir, "*.trx", SearchOption.AllDirectories)
                .SelectMany(f =>
                {
                    try { return TrxParser.Parse(f); }
                    catch (Exception e) { logger.LogWarning(e, "Cannot parse {Trx}", f); return []; }
                })
                .ToList();
            if (results.Count == 0 && report == TestReport.Console) results = ConsoleResultParser.Parse(console).ToList();
            var label = mode == TestRunnerMode.VSTest ? mode.ToString() : $"{mode}, {report switch { TestReport.Trx => "TRX", TestReport.XunitTrx => "xUnit TRX", _ => "console" }}";
            var result = new TestRunResult(exit, false, results, DateTime.UtcNow - started, label);
            onOutput($"Tests finished: {result.Passed} passed, {result.Failed} failed, {result.Skipped} skipped (exit {exit}).");
            return result;
        }
        catch (OperationCanceledException)
        {
            onOutput("Test run cancelled.");
            return new TestRunResult(-1, true, [], DateTime.UtcNow - started, mode.ToString());
        }
        finally
        {
            try { Directory.Delete(resultsDir, recursive: true); } catch (IOException) { }
        }

        bool HasTrx() => Directory.EnumerateFiles(resultsDir, "*.trx", SearchOption.AllDirectories).Any();

        Task<int> RunOnce(TestReport report)
        {
            console.Clear();
            var args = Arguments(mode, target, configuration, resultsDir, tests, report);
            onOutput($"> dotnet {string.Join(' ', args)}");
            logger.LogInformation("Running dotnet {Arguments}", string.Join(' ', args));
            return ProcessRunner.RunAsync("dotnet", args, workingDirectory, (line, _) =>
            {
                lock (console) console.Add(line);
                onOutput(line);
            }, environment, ct);
        }
    }
}
