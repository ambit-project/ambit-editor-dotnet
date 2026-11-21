namespace LightweightIDE.Editor;

public enum HitRule { Always, Equal, AtLeast, MultipleOf }

/// <summary>
/// What a breakpoint does when hit. <see cref="Condition"/> is a C# expression evaluated by the debugger;
/// the hit rule and the log message are applied by the IDE (netcoredbg supports neither).
/// </summary>
public sealed record BreakpointOptions(string? Condition = null, HitRule Hit = HitRule.Always, int HitCount = 1, string? LogMessage = null)
{
    public static readonly BreakpointOptions Default = new();

    public bool IsDefault => string.IsNullOrWhiteSpace(Condition) && Hit == HitRule.Always && string.IsNullOrWhiteSpace(LogMessage);

    /// <summary>A logpoint writes the message to Output and continues instead of pausing.</summary>
    public bool IsLogpoint => !string.IsNullOrWhiteSpace(LogMessage);

    /// <summary>True when the <paramref name="hits"/>-th hit (1-based) should stop or log.</summary>
    public bool Matches(int hits) => Hit switch
    {
        HitRule.Equal => hits == HitCount,
        HitRule.AtLeast => hits >= HitCount,
        HitRule.MultipleOf => HitCount > 0 && hits % HitCount == 0,
        _ => true,
    };

    /// <summary>Short text for tooltips and the status bar.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Condition)) parts.Add($"when {Condition.Trim()}");
        if (Hit != HitRule.Always)
            parts.Add(Hit switch
            {
                HitRule.Equal => $"on hit {HitCount}",
                HitRule.AtLeast => $"from hit {HitCount}",
                _ => $"every {HitCount} hits",
            });
        if (IsLogpoint) parts.Add($"logs \"{LogMessage!.Trim()}\"");
        return parts.Count == 0 ? "breakpoint" : string.Join(", ", parts);
    }

    /// <summary>Splits a log message into literal text and {expression} parts ("{{" and "}}" are literal braces).</summary>
    public static IReadOnlyList<(string Text, bool IsExpression)> ParseMessage(string message)
    {
        var parts = new List<(string, bool)>();
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < message.Length; i++)
        {
            var c = message[i];
            if (c == '{' && i + 1 < message.Length && message[i + 1] == '{') { text.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < message.Length && message[i + 1] == '}') { text.Append('}'); i++; continue; }
            if (c == '{' && message.IndexOf('}', i + 1) is var end and > 0)
            {
                if (text.Length > 0) { parts.Add((text.ToString(), false)); text.Clear(); }
                var expression = message[(i + 1)..end].Trim();
                if (expression.Length > 0) parts.Add((expression, true));
                i = end;
                continue;
            }
            text.Append(c);
        }
        if (text.Length > 0) parts.Add((text.ToString(), false));
        return parts;
    }
}
