using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.DotNet.Http;

/// <summary>A sent request and its response (or the error). Bodies are text; headers keep their order.</summary>
public sealed record HttpExchange(
    string Title,
    string? Name,
    string Method,
    string Url,
    IReadOnlyList<(string Name, string Value)> RequestHeaders,
    string? RequestBody,
    int? Status,
    string? Reason,
    IReadOnlyList<(string Name, string Value)> ResponseHeaders,
    string? ResponseBody,
    string? ContentType,
    TimeSpan Elapsed,
    long Size,
    string? Error,
    DateTime At)
{
    public bool IsJson => ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Sends requests from .http files. Each file has its own session: cookies and the last response of every named
/// request (for <c>{{login.response.body.$.token}}</c>). A request that refers to a named request not sent yet sends
/// that one first. Untrusted HTTPS certificates are accepted only for localhost (the ASP.NET dev certificate).
/// </summary>
public sealed class HttpClientService(ILogger<HttpClientService> logger) : IDisposable
{
    const int MaxDependencyDepth = 8;
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    readonly Lock gate = new();

    sealed class Session : IDisposable
    {
        public readonly Dictionary<string, HttpExchange> Responses = new(StringComparer.Ordinal);
        public readonly HttpClient Client;

        public Session()
        {
            var handler = new SocketsHttpHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                        errors == SslPolicyErrors.None || sender is SslStream { TargetHostName: "localhost" or "127.0.0.1" or "::1" },
                },
            };
            Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        }

        public void Dispose() => Client.Dispose();
    }

    Session SessionFor(string path)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(path, out var session)) sessions[path] = session = new Session();
            return session;
        }
    }

    /// <summary>Forgets cookies and stored responses of a file.</summary>
    public void Reset(string path)
    {
        lock (gate)
            if (sessions.Remove(path, out var session)) session.Dispose();
    }

    /// <summary>Sends the request whose block contains <paramref name="line"/> (1-based).</summary>
    public Task<HttpExchange> SendAsync(string path, string text, int line, CancellationToken ct)
    {
        var file = HttpFile.Parse(text);
        var request = file.RequestAt(line) ?? throw new HttpVariableException("Place the caret inside a request (METHOD URL).");
        var environment = HttpEnvironments.Load(Path.GetDirectoryName(path)!);
        return SendBlockAsync(path, file, request, environment, 0, ct);
    }

    async Task<HttpExchange> SendBlockAsync(string path, HttpFile file, HttpRequestBlock request, IReadOnlyDictionary<string, string> environment,
        int depth, CancellationToken ct)
    {
        var session = SessionFor(path);
        var resolver = new HttpVariableResolver(file, request.Line, environment, Path.GetDirectoryName(path)!, async name =>
        {
            lock (gate)
                if (session.Responses.TryGetValue(name, out var known)) return known;
            if (depth >= MaxDependencyDepth) throw new HttpVariableException("Requests refer to each other in a loop.");
            var dependency = file.Find(name)!;
            var exchange = await SendBlockAsync(path, file, dependency, environment, depth + 1, ct).ConfigureAwait(false);
            if (exchange.Error is not null || exchange.Status is null or >= 400)
                throw new HttpVariableException($"Request '{name}' (sent first, because this one uses its response) failed: " +
                    (exchange.Error ?? $"{exchange.Status} {exchange.Reason}"));
            return exchange;
        });

        string url;
        var headers = new List<(string Name, string Value)>();
        string? body;
        url = await resolver.ResolveAsync(request.Url).ConfigureAwait(false);
        foreach (var (name, value) in request.Headers) headers.Add((name, await resolver.ResolveAsync(value).ConfigureAwait(false)));
        body = request.Body is null ? null : await resolver.ResolveAsync(request.Body).ConfigureAwait(false);

        var started = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), url);
            if (body is not null)
            {
                message.Content = new StringContent(body, Encoding.UTF8);
                message.Content.Headers.ContentType = null; // only what the file says
            }
            foreach (var (name, value) in headers)
                if (!message.Headers.TryAddWithoutValidation(name, value) && message.Content is not null)
                {
                    message.Content.Headers.Remove(name);
                    message.Content.Headers.TryAddWithoutValidation(name, value);
                }
            logger.LogInformation("HTTP {Method} {Url}", request.Method, url);
            using var response = await session.Client.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            started.Stop();
            var responseHeaders = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => (h.Key, v))).ToList();
            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = charset is null ? Encoding.UTF8 : TryEncoding(charset);
            var exchange = new HttpExchange(request.Title, request.Name, request.Method, url, headers, body, (int)response.StatusCode,
                response.ReasonPhrase, responseHeaders, encoding.GetString(bytes), response.Content.Headers.ContentType?.MediaType,
                started.Elapsed, bytes.LongLength, null, DateTime.Now);
            if (request.Name is not null) lock (gate) session.Responses[request.Name] = exchange;
            return exchange;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException
                                  && !ct.IsCancellationRequested)
        {
            started.Stop();
            return new HttpExchange(request.Title, request.Name, request.Method, url, headers, body, null, null, [], null, null,
                started.Elapsed, 0, e is TaskCanceledException ? "Timed out after 100 s." : e.Message, DateTime.Now);
        }
    }

    static Encoding TryEncoding(string name)
    {
        try { return Encoding.GetEncoding(name.Trim('"')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    /// <summary>Indented JSON for display; the text itself when it is not JSON.</summary>
    public static string Pretty(string body)
    {
        try { return JsonNode.Parse(body)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? body; }
        catch (JsonException) { return body; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var s in sessions.Values) s.Dispose();
            sessions.Clear();
        }
    }
}

/// <summary>
/// http-client.env.json (and http-client.env.json.user, which overrides it), searched from the file's folder upwards:
/// "$shared" plus the selected environment ("dev" when present, otherwise the first one).
/// </summary>
public static class HttpEnvironments
{
    public static IReadOnlyDictionary<string, string> Load(string directory, string? environment = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var dir = directory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, "http-client.env.json");
            if (!File.Exists(file)) continue;
            foreach (var path in new[] { file, file + ".user" })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject();
                    if (root is null) continue;
                    var name = environment ?? (root.ContainsKey("dev") ? "dev" : root.Select(kv => kv.Key).FirstOrDefault(k => k != "$shared"));
                    foreach (var section in new[] { "$shared", name })
                        if (section is not null && root[section] is JsonObject values)
                            foreach (var (k, v) in values) result[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "";
                }
                catch (Exception e) when (e is JsonException or IOException) { }
            }
            break;
        }
        return result;
    }
}
