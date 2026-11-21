using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LightweightIDE.DotNet.Http;

public sealed class HttpVariableException(string message) : Exception(message);

/// <summary>
/// Resolves {{…}} in a request: request variables (<c>login.response.body.$.token</c>, <c>x.response.headers.Location</c>),
/// file variables (<c>@baseURL = …</c>, the nearest definition above the request wins), environment variables
/// (http-client.env.json) and system variables (<c>$guid</c>, <c>$randomInt a b</c>, <c>$timestamp</c>,
/// <c>$datetime iso8601</c>, <c>$processEnv X</c>, <c>$dotenv X</c>).
/// </summary>
public sealed partial class HttpVariableResolver(
    HttpFile file,
    int requestLine,
    IReadOnlyDictionary<string, string> environment,
    string fileDirectory,
    Func<string, Task<HttpExchange>> responseOf)
{
    const int MaxDepth = 16;

    [GeneratedRegex(@"\{\{\s*(.+?)\s*\}\}")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^([A-Za-z_][\w-]*)\.(response|request)\.(body|headers)(?:\.(.*))?$")]
    private static partial Regex RequestReference();

    public Task<string> ResolveAsync(string template) => ResolveAsync(template, 0);

    async Task<string> ResolveAsync(string template, int depth)
    {
        if (depth > MaxDepth) throw new HttpVariableException("Variables refer to each other in a loop.");
        var result = new StringBuilder();
        var last = 0;
        foreach (Match m in Reference().Matches(template))
        {
            result.Append(template, last, m.Index - last);
            result.Append(await ValueOfAsync(m.Groups[1].Value, depth).ConfigureAwait(false));
            last = m.Index + m.Length;
        }
        result.Append(template, last, template.Length - last);
        return result.ToString();
    }

    async Task<string> ValueOfAsync(string expression, int depth)
    {
        if (expression.StartsWith('$')) return SystemVariable(expression);
        if (RequestReference().Match(expression) is { Success: true } r && file.Find(r.Groups[1].Value) is not null)
        {
            var exchange = await responseOf(r.Groups[1].Value).ConfigureAwait(false);
            var isResponse = r.Groups[2].Value == "response";
            var path = r.Groups[4].Success ? r.Groups[4].Value : "";
            if (r.Groups[3].Value == "headers")
            {
                var headers = isResponse ? exchange.ResponseHeaders : exchange.RequestHeaders;
                return headers.FirstOrDefault(h => h.Name.Equals(path, StringComparison.OrdinalIgnoreCase)).Value
                    ?? throw new HttpVariableException($"{r.Groups[1].Value} has no header '{path}'.");
            }
            var body = (isResponse ? exchange.ResponseBody : exchange.RequestBody) ?? "";
            return SelectJson(body, path) ?? throw new HttpVariableException($"'{path}' not found in the {r.Groups[2].Value} body of {r.Groups[1].Value}.");
        }
        // File variable: the nearest definition above the request, else any (variables may be defined anywhere).
        var variable = file.Variables.Where(v => v.Name == expression && v.Line < requestLine).MaxBy(v => v.Line)
                       ?? file.Variables.LastOrDefault(v => v.Name == expression);
        if (variable is not null) return await ResolveAsync(variable.Value, depth + 1).ConfigureAwait(false);
        if (environment.TryGetValue(expression, out var fromEnvironment)) return await ResolveAsync(fromEnvironment, depth + 1).ConfigureAwait(false);
        throw new HttpVariableException($"Unknown variable '{expression}'.");
    }

    string SystemVariable(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "$guid":
                return Guid.NewGuid().ToString();
            case "$randomInt":
                var min = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
                var max = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 1000;
                return Random.Shared.Next(min, max).ToString(CultureInfo.InvariantCulture);
            case "$timestamp":
                return Offset(DateTimeOffset.UtcNow, parts, 1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "$datetime":
            case "$localDatetime":
                var now = parts[0] == "$datetime" ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
                var format = parts.Length > 1 ? parts[1].Trim('"', '\'') : "iso8601";
                var value = Offset(now, parts, 2);
                return format switch
                {
                    "iso8601" => value.ToString("o", CultureInfo.InvariantCulture),
                    "rfc1123" => value.ToString("r", CultureInfo.InvariantCulture),
                    _ => value.ToString(format, CultureInfo.InvariantCulture),
                };
            case "$processEnv":
                return Environment.GetEnvironmentVariable(parts.ElementAtOrDefault(1) ?? "") ?? "";
            case "$dotenv":
                return DotEnv(parts.ElementAtOrDefault(1) ?? "");
            default:
                throw new HttpVariableException($"Unknown system variable '{parts[0]}'.");
        }
    }

    /// <summary>"$timestamp -1 d": an optional offset (number + unit y M w d h m s ms).</summary>
    static DateTimeOffset Offset(DateTimeOffset value, string[] parts, int index)
    {
        if (parts.Length < index + 2 || !int.TryParse(parts[index], out var amount)) return value;
        return parts[index + 1] switch
        {
            "y" => value.AddYears(amount),
            "M" => value.AddMonths(amount),
            "w" => value.AddDays(7 * amount),
            "d" => value.AddDays(amount),
            "h" => value.AddHours(amount),
            "m" => value.AddMinutes(amount),
            "s" => value.AddSeconds(amount),
            "ms" => value.AddMilliseconds(amount),
            _ => value,
        };
    }

    string DotEnv(string name)
    {
        for (var dir = fileDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, ".env");
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadLines(file))
            {
                var eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim() == name) return line[(eq + 1)..].Trim().Trim('"');
            }
            return "";
        }
        return "";
    }

    /// <summary>
    /// A value from a JSON body by path: "$.a.b[0].c", "$.[0].id", "a.b[0]" (the "$." is optional). An empty path
    /// or "*" returns the whole body. Strings come without quotes, other values as JSON.
    /// </summary>
    public static string? SelectJson(string body, string path)
    {
        if (path.Length == 0 || path == "*" || path == "$") return body;
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException) { return null; }
        var p = path.StartsWith('$') ? path[1..] : path;
        foreach (Match token in PathToken().Matches(p))
        {
            if (node is null) return null;
            if (token.Groups[2].Success)
            {
                if (node is not JsonArray array) return null;
                var index = int.Parse(token.Groups[2].Value, CultureInfo.InvariantCulture);
                node = index < array.Count ? array[index] : null;
            }
            else
            {
                if (node is not JsonObject obj) return null;
                var key = token.Groups[1].Value;
                // Case-insensitive fallback: APIs return camelCase, people type PascalCase.
                node = obj.TryGetPropertyValue(key, out var exact) ? exact
                    : obj.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
            }
        }
        return node switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString(),
        };
    }

    [GeneratedRegex(@"([^.\[\]]+)|\[(\d+)\]")]
    private static partial Regex PathToken();
}
