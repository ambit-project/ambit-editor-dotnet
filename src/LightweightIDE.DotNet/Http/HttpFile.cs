using System.Text.RegularExpressions;

namespace LightweightIDE.DotNet.Http;

/// <summary>A file variable: <c>@name = value</c> (the value may contain {{…}} references). Line is 1-based.</summary>
public sealed record HttpVariable(string Name, string Value, int Line);

/// <summary>
/// One request of a .http file. Lines are 1-based: <see cref="Line"/> is the request line (METHOD URL),
/// <see cref="BlockStart"/>..<see cref="BlockEnd"/> the lines of its ### block.
/// </summary>
public sealed record HttpRequestBlock(
    string? Name,
    string Title,
    string Method,
    string Url,
    IReadOnlyList<(string Name, string Value)> Headers,
    string? Body,
    int Line,
    int BlockStart,
    int BlockEnd);

/// <summary>
/// A parsed .http file (the format of VS Code REST Client / Visual Studio / Rider): requests separated by ###,
/// <c>@var = value</c> file variables, <c># @name x</c> request names, headers, a body after the first blank line.
/// </summary>
public sealed partial class HttpFile
{
    static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    [GeneratedRegex(@"^@([A-Za-z_][\w.-]*)\s*=\s*(.*)$")]
    private static partial Regex VariableLine();

    [GeneratedRegex(@"^(#|//)\s*@name\s+(\S+)")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"^([!#$%&'*+\-.^_`|~\w]+)\s*:\s?(.*)$")]
    private static partial Regex HeaderLine();

    public IReadOnlyList<HttpRequestBlock> Requests { get; }
    public IReadOnlyList<HttpVariable> Variables { get; }

    HttpFile(IReadOnlyList<HttpRequestBlock> requests, IReadOnlyList<HttpVariable> variables)
    {
        Requests = requests;
        Variables = variables;
    }

    /// <summary>The request whose block contains a 1-based line (the caret).</summary>
    public HttpRequestBlock? RequestAt(int line) => Requests.FirstOrDefault(r => line >= r.BlockStart && line <= r.BlockEnd);

    public HttpRequestBlock? Find(string name) => Requests.LastOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    public static HttpFile Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var requests = new List<HttpRequestBlock>();
        var variables = new List<HttpVariable>();

        // Blocks start at "###" lines (or the file start).
        var starts = new List<(int Index, string Title)> { (0, "") };
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].TrimStart().StartsWith("###", StringComparison.Ordinal)) starts.Add((i + 1, lines[i].TrimStart()[3..].Trim()));
        for (var b = 0; b < starts.Count; b++)
        {
            var from = starts[b].Index;
            var to = b + 1 < starts.Count ? starts[b + 1].Index - 1 : lines.Length; // exclusive (the next ### line)
            ParseBlock(lines, from, to, starts[b].Title, requests, variables);
        }
        return new HttpFile(requests, variables);
    }

    static void ParseBlock(string[] lines, int from, int to, string title, List<HttpRequestBlock> requests, List<HttpVariable> variables)
    {
        string? name = null;
        var i = from;
        // Before the request line: blank lines, comments, @name, file variables.
        for (; i < to; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (NameLine().Match(line) is { Success: true } n) { name = n.Groups[2].Value; continue; }
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (VariableLine().Match(line) is { Success: true } v)
            {
                variables.Add(new HttpVariable(v.Groups[1].Value, v.Groups[2].Value.Trim(), i + 1));
                continue;
            }
            break;
        }
        if (i >= to) return; // a block with only variables or comments

        var requestLine = lines[i].Trim();
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string method, url;
        if (Methods.Contains(parts[0].ToUpperInvariant()) && parts.Length >= 2)
        {
            method = parts[0].ToUpperInvariant();
            url = parts[1];
        }
        else
        {
            method = "GET";
            url = parts[0];
        }
        var lineNumber = i + 1;
        i++;
        // Query continuation lines (?a=1 / &b=2).
        while (i < to && (lines[i].TrimStart().StartsWith('?') || lines[i].TrimStart().StartsWith('&')))
            url += lines[i++].Trim();

        var headers = new List<(string, string)>();
        for (; i < to; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; break; }
            if (line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            if (HeaderLine().Match(line.Trim()) is { Success: true } h) headers.Add((h.Groups[1].Value, h.Groups[2].Value.Trim()));
        }

        // Body: until the block ends; trailing blank lines and trailing "@var = …" lines (variables after the request) are not body.
        var bodyLines = lines[Math.Min(i, to)..to].ToList();
        while (bodyLines.Count > 0 && bodyLines[^1].Trim().Length == 0) bodyLines.RemoveAt(bodyLines.Count - 1);
        var body = bodyLines.Count == 0 ? null : string.Join("\n", bodyLines);
        requests.Add(new HttpRequestBlock(name, title.Length > 0 ? title : $"{method} {url}", method, url, headers, body, lineNumber,
            Math.Max(1, from), to));
    }
}
