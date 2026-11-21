using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LightweightIDE.DotNet.Debugging;

/// <summary>A failed DAP request (success = false), with the adapter's message.</summary>
public sealed class DapException(string command, string message) : Exception($"{command}: {message}")
{
    public string Command { get; } = command;
}

/// <summary>
/// Debug Adapter Protocol transport: Content-Length framed JSON over two streams (the adapter's stdin/stdout).
/// Requests get a sequence number and complete when the matching response arrives; events are raised on the
/// reader thread.
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    readonly Stream input;
    readonly Stream output;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> pending = new();
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly CancellationTokenSource stop = new();
    readonly Task reader;
    int seq;

    /// <summary>(event name, body). Raised on the reader thread.</summary>
    public event Action<string, JsonNode?>? EventReceived;
    /// <summary>The adapter closed its output (exited).</summary>
    public event Action? Closed;

    /// <param name="input">Adapter → client (its stdout).</param>
    /// <param name="output">Client → adapter (its stdin).</param>
    public DapClient(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
        reader = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonNode?> RequestAsync(string command, JsonObject? arguments = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref seq);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        var message = new JsonObject { ["seq"] = id, ["type"] = "request", ["command"] = command };
        if (arguments is not null) message["arguments"] = arguments;
        await WriteAsync(message, ct).ConfigureAwait(false);
        await using (ct.Register(() => tcs.TrySetCanceled(ct)))
            return await tcs.Task.ConfigureAwait(false);
    }

    async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, ct).ConfigureAwait(false);
            await output.WriteAsync(body, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    /// <summary>Reads one framed message; null at end of stream.</summary>
    public static async Task<JsonNode?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            // Header lines are ASCII and end with \r\n; an empty line ends the header.
            line.Clear();
            while (true)
            {
                if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
                if (one[0] == '\n') break;
                if (one[0] != '\r') line.Append((char)one[0]);
            }
            if (line.Length == 0)
            {
                if (length >= 0) break;
                continue;
            }
            var text = line.ToString();
            if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(text["Content-Length:".Length..].Trim());
        }
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return JsonNode.Parse(buffer);
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(input, stop.Token).ConfigureAwait(false);
                if (message is null) break;
                switch ((string?)message["type"])
                {
                    case "response":
                        var id = (int?)message["request_seq"] ?? -1;
                        if (!pending.TryRemove(id, out var tcs)) break;
                        if ((bool?)message["success"] == true) tcs.TrySetResult(message["body"]);
                        else tcs.TrySetException(new DapException((string?)message["command"] ?? "?",
                            (string?)message["message"] ?? (string?)message["body"]?["error"]?["format"] ?? "request failed"));
                        break;
                    case "event":
                        EventReceived?.Invoke((string?)message["event"] ?? "", message["body"]);
                        break;
                    case "request":
                        // Reverse requests (runInTerminal, startDebugging) are not used: answer that they failed.
                        await WriteAsync(new JsonObject
                        {
                            ["seq"] = Interlocked.Increment(ref seq), ["type"] = "response", ["request_seq"] = (int?)message["seq"],
                            ["command"] = (string?)message["command"], ["success"] = false, ["message"] = "not supported",
                        }, stop.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or JsonException) { }
        finally
        {
            foreach (var (_, tcs) in pending) tcs.TrySetException(new IOException("The debug adapter closed the connection."));
            pending.Clear();
            Closed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { }
        stop.Dispose();
    }
}
