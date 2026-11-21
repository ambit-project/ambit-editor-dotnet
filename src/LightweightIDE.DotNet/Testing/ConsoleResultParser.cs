using System.Text;
using System.Text.RegularExpressions;

namespace LightweightIDE.DotNet.Testing;

/// <summary>
/// Reads the per-test lines <c>dotnet test --output detailed</c> prints for Microsoft.Testing.Platform when no TRX
/// report is available:
/// <code>
/// passed Ns.Class.Method (12ms)
/// failed Ns.Class.Other (1s 733ms)
///   from /path/Tests.dll (net10.0|x64)
///   Assert.Equal() Failure: …
///     at Ns.Class.Other() in /src/ClassTests.cs:line 17
/// </code>
/// </summary>
public static partial class ConsoleResultParser
{
    [GeneratedRegex(@"^(?<outcome>passed|failed|skipped) (?<name>.+) \((?<duration>(?:\d+(?:h|m|s|ms)\s?)+)\)$")]
    private static partial Regex ResultLine();

    [GeneratedRegex(@"(?<value>\d+)(?<unit>ms|h|m|s)")]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"\sin\s(?<file>/[^:]+):line\s(?<line>\d+)")]
    private static partial Regex StackLocation();

    public static IReadOnlyList<TestCaseResult> Parse(IEnumerable<string> lines)
    {
        var results = new List<TestCaseResult>();
        (TestOutcome Outcome, string Name, TimeSpan Duration)? current = null;
        var detail = new List<string>();
        var inOutputSection = false;
        foreach (var raw in lines)
        {
            var line = Ansi().Replace(raw, "").TrimEnd();
            if (ResultLine().Match(line) is { Success: true } m)
            {
                Flush();
                current = (m.Groups["outcome"].Value switch
                {
                    "passed" => TestOutcome.Passed,
                    "failed" => TestOutcome.Failed,
                    _ => TestOutcome.Skipped,
                }, m.Groups["name"].Value, Duration(m.Groups["duration"].Value));
                continue;
            }
            if (current is null) continue;
            if (line.Length == 0 || !char.IsWhiteSpace(line[0])) { Flush(); continue; }
            var trimmed = line.Trim();
            if (trimmed is "Standard output" or "Error output") inOutputSection = true;
            if (inOutputSection || trimmed.StartsWith("from ", StringComparison.Ordinal) && detail.Count == 0) continue;
            detail.Add(line);
        }
        Flush();
        return results;

        void Flush()
        {
            if (current is { } c)
            {
                var stackStart = detail.FindIndex(l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal));
                var message = Join(stackStart < 0 ? detail : detail.Take(stackStart));
                var stack = stackStart < 0 ? null : Join(detail.Skip(stackStart));
                string? file = null;
                var lineNumber = 0;
                if (stack is not null && StackLocation().Match(stack) is { Success: true } s)
                {
                    file = s.Groups["file"].Value;
                    lineNumber = int.Parse(s.Groups["line"].Value);
                }
                var method = TestCaseResult.Normalize(c.Name);
                var className = method.LastIndexOf('.') is > 0 and var dot ? method[..dot] : "";
                var shortName = className.Length > 0 && c.Name.StartsWith(className + ".", StringComparison.Ordinal) ? c.Name[(className.Length + 1)..] : c.Name;
                results.Add(new TestCaseResult(c.Name, className, shortName, c.Outcome, c.Duration, message, stack, file, lineNumber));
            }
            current = null;
            detail.Clear();
            inOutputSection = false;
        }
    }

    static string? Join(IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        foreach (var l in lines) text.AppendLine(l.Trim());
        var result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    static TimeSpan Duration(string text)
    {
        var total = TimeSpan.Zero;
        foreach (Match part in DurationPart().Matches(text))
        {
            var value = int.Parse(part.Groups["value"].Value);
            total += part.Groups["unit"].Value switch
            {
                "h" => TimeSpan.FromHours(value),
                "m" => TimeSpan.FromMinutes(value),
                "s" => TimeSpan.FromSeconds(value),
                _ => TimeSpan.FromMilliseconds(value),
            };
        }
        return total;
    }
}
