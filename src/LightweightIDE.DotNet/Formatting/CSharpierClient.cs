using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.DotNet.Formatting;

/// <summary>How CSharpier is started for a folder.</summary>
/// <param name="FileName">Executable: <c>dotnet</c> for a local tool, the tool itself for a global one.</param>
/// <param name="Arguments">Arguments before the server arguments (<c>csharpier</c> for <c>dotnet csharpier</c>).</param>
/// <param name="WorkingDirectory">Folder of the tool manifest (local tool) or of the file (global tool).</param>
/// <param name="Version">Version from the manifest; null for a global tool.</param>
public sealed record CSharpierTool(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string? Version, bool IsLocal)
{
    /// <summary>CSharpier 0.x used <c>--server</c>; 1.x has a <c>server</c> command.</summary>
    public IReadOnlyList<string> ServerArguments =>
        Version is { } v && v.StartsWith("0.", StringComparison.Ordinal) ? [.. Arguments, "--server"] : [.. Arguments, "server"];

    public string Description => IsLocal ? $"CSharpier {Version} (local tool)" : "CSharpier (global tool)";
}

public enum CSharpierStatus { Formatted, Ignored, Failed, UnsupportedFile }

public sealed record CSharpierResult(CSharpierStatus Status, string? Text, string? Error);

/// <summary>
/// Formats files with CSharpier installed as a dotnet tool. A local tool from <c>.config/dotnet-tools.json</c> wins
/// over a global one, so every repository formats with the version it pins. One <c>csharpier server</c> process per
/// tool is kept running (the same mode the Rider and VS Code plugins use), because starting dotnet per file is slow.
/// Protocol: the server prints <c>Started on &lt;port&gt;</c>; <c>POST /format</c> takes
/// <c>{ fileName, fileContents }</c> and returns <c>{ formattedFile, status, errorMessage }</c>.
/// </summary>
public sealed class CSharpierClient(ILogger<CSharpierClient> logger) : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly Dictionary<string, Server> servers = new(StringComparer.Ordinal);
    readonly SemaphoreSlim gate = new(1, 1);

    sealed record Server(Process Process, int Port);

    sealed record FormatRequest(string FileName, string FileContents);

    sealed record FormatResponse(string? FormattedFile, CSharpierStatus Status, string? ErrorMessage);

    /// <summary>Finds CSharpier for a file: the nearest tool manifest that lists it, else a global install.</summary>
    public static CSharpierTool? Find(string filePath)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            foreach (var manifest in new[] { Path.Combine(dir, ".config", "dotnet-tools.json"), Path.Combine(dir, "dotnet-tools.json") })
            {
                if (!File.Exists(manifest)) continue;
                if (ReadManifestVersion(manifest) is { } version) return new CSharpierTool("dotnet", ["csharpier"], dir, version, true);
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[] { Path.Combine(home, ".dotnet", "tools", "csharpier") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p, "csharpier")));
        var global = candidates.FirstOrDefault(File.Exists);
        return global is null ? null : new CSharpierTool(global, [], Path.GetDirectoryName(Path.GetFullPath(filePath))!, null, false);
    }

    /// <summary>The CSharpier version a dotnet tool manifest pins, or null when it does not list CSharpier.</summary>
    public static string? ReadManifestVersion(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("tools", out var tools)) return null;
            foreach (var tool in tools.EnumerateObject())
            {
                if (!tool.Name.Equals("csharpier", StringComparison.OrdinalIgnoreCase)) continue;
                return tool.Value.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }

    public async Task<CSharpierResult> FormatAsync(CSharpierTool tool, string filePath, string text, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var server = await GetServerAsync(tool, ct).ConfigureAwait(false);
            try
            {
                using var response = await http.PostAsJsonAsync($"http://127.0.0.1:{server.Port}/format", new FormatRequest(filePath, text), Json, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<FormatResponse>(Json, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Empty response from CSharpier.");
                return new CSharpierResult(result.Status, result.FormattedFile, result.ErrorMessage);
            }
            catch (HttpRequestException e) when (attempt == 0)
            {
                // The server died (e.g. the tool was updated): start a new one once.
                logger.LogWarning(e, "CSharpier server did not answer, restarting");
                await StopAsync(tool).ConfigureAwait(false);
            }
        }
    }

    static string Key(CSharpierTool tool) => tool.IsLocal ? "local:" + tool.WorkingDirectory : "global:" + tool.FileName;

    async Task<Server> GetServerAsync(CSharpierTool tool, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (servers.TryGetValue(Key(tool), out var running) && !running.Process.HasExited) return running;
            var server = await StartAsync(tool, ct).ConfigureAwait(false);
            servers[Key(tool)] = server;
            return server;
        }
        finally { gate.Release(); }
    }

    async Task<Server> StartAsync(CSharpierTool tool, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tool.FileName)
        {
            WorkingDirectory = tool.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in tool.ServerArguments) start.ArgumentList.Add(a);
        logger.LogInformation("Starting {Tool} server in {Folder}", tool.Description, tool.WorkingDirectory);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start CSharpier.");

        var errors = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                const string marker = "Started on ";
                var at = line.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0 && int.TryParse(line[(at + marker.Length)..].Trim(), out var port))
                {
                    // Keep draining stdout so the server never blocks on a full pipe.
                    _ = Task.Run(async () =>
                    {
                        try { while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is not null) { } }
                        catch (Exception) { }
                    }, CancellationToken.None);
                    return new Server(process, port);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        Kill(process);
        string detail;
        lock (errors) detail = errors.ToString().Trim();
        if (tool.IsLocal && (detail.Contains("dotnet tool restore", StringComparison.Ordinal) || detail.Contains("could not be found", StringComparison.OrdinalIgnoreCase)))
            detail = $"The local tool is not restored. Run `dotnet tool restore` in {tool.WorkingDirectory}.\n{detail}";
        throw new InvalidOperationException($"{tool.Description} did not start. {detail}".Trim());
    }

    async Task StopAsync(CSharpierTool tool)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (servers.Remove(Key(tool), out var server)) Kill(server.Process);
        }
        finally { gate.Release(); }
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
        process.Dispose();
    }

    public void Dispose()
    {
        foreach (var server in servers.Values) Kill(server.Process);
        servers.Clear();
        http.Dispose();
    }
}
