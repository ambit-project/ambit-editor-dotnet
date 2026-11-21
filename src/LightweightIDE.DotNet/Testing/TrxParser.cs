using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LightweightIDE.DotNet.Testing;

/// <summary>Reads Visual Studio TRX result files (written by both VSTest and Microsoft.Testing.Platform).</summary>
public static partial class TrxParser
{
    static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    [GeneratedRegex(@"\sin\s(?<file>/[^:]+):line\s(?<line>\d+)")]
    private static partial Regex StackLocation();

    public static IReadOnlyList<TestCaseResult> Parse(string trxPath)
    {
        var doc = XDocument.Load(trxPath);
        var definitions = doc.Descendants(Ns + "UnitTest").ToDictionary(
            e => (string?)e.Attribute("id") ?? "",
            e =>
            {
                var method = e.Element(Ns + "TestMethod");
                return (ClassName: (string?)method?.Attribute("className") ?? "", Method: (string?)method?.Attribute("name") ?? (string?)e.Attribute("name") ?? "");
            });

        var results = new List<TestCaseResult>();
        foreach (var r in doc.Descendants(Ns + "UnitTestResult"))
        {
            var testId = (string?)r.Attribute("testId") ?? "";
            var displayName = (string?)r.Attribute("testName") ?? "";
            definitions.TryGetValue(testId, out var def);
            var className = def.ClassName ?? "";
            var outcome = ((string?)r.Attribute("outcome")) switch
            {
                "Passed" => TestOutcome.Passed,
                "Failed" or "Error" or "Timeout" or "Aborted" => TestOutcome.Failed,
                "NotExecuted" or "Inconclusive" or "Ignored" => TestOutcome.Skipped,
                _ => TestOutcome.Other,
            };
            var duration = TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d : TimeSpan.Zero;
            var error = r.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
            var message = error?.Element(Ns + "Message")?.Value;
            var stack = error?.Element(Ns + "StackTrace")?.Value;
            string? file = null;
            var line = 0;
            if (stack is not null && StackLocation().Match(stack) is { Success: true } m)
            {
                file = m.Groups["file"].Value;
                line = int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture);
            }
            var shortName = displayName.StartsWith(className + ".", StringComparison.Ordinal) ? displayName[(className.Length + 1)..] : displayName;
            results.Add(new TestCaseResult(string.IsNullOrEmpty(className) ? displayName : $"{className}.{shortName}", className, shortName,
                outcome, duration, message?.Trim(), stack?.Trim(), file, line, def.Method is { Length: > 0 } m2 ? m2 : null));
        }
        return results;
    }
}
