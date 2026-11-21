using System.Text.RegularExpressions;

namespace LightweightIDE.DotNet.Build;

public enum DiagnosticSeverity { Error, Warning, Info }

public enum DiagnosticSource { Build, Live }

/// <summary>An error or warning with an optional location. Lines and columns are 1-based.</summary>
public sealed record BuildDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? FilePath,
    int Line,
    int Column,
    string? Project,
    DiagnosticSource Source = DiagnosticSource.Build);

/// <summary>Parses MSBuild "canonical" console messages, e.g.
/// <c>/src/Foo.cs(12,5): error CS1002: ; expected [/src/Foo.csproj]</c>.</summary>
public static partial class MsBuildOutputParser
{
    [GeneratedRegex(@"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning|info)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$")]
    private static partial Regex WithLocation();

    [GeneratedRegex(@"^\s*(?<file>[^:]+?)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$")]
    private static partial Regex WithoutLocation();

    public static BuildDiagnostic? TryParse(string line)
    {
        var m = WithLocation().Match(line);
        if (m.Success)
        {
            return new BuildDiagnostic(ParseSeverity(m.Groups["sev"].Value), m.Groups["code"].Value, m.Groups["msg"].Value,
                m.Groups["file"].Value.Trim(), int.Parse(m.Groups["line"].Value),
                m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 1,
                m.Groups["proj"].Success ? m.Groups["proj"].Value : null);
        }
        m = WithoutLocation().Match(line);
        if (m.Success)
        {
            var file = m.Groups["file"].Value.Trim();
            return new BuildDiagnostic(ParseSeverity(m.Groups["sev"].Value), m.Groups["code"].Value, m.Groups["msg"].Value,
                file.Contains('/') ? file : null, 0, 0, m.Groups["proj"].Success ? m.Groups["proj"].Value : null);
        }
        return null;
    }

    static DiagnosticSeverity ParseSeverity(string s) => s switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Info,
    };
}
