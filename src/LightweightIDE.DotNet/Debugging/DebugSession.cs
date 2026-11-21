using System.Diagnostics;
using System.Text.Json.Nodes;
using LightweightIDE.DotNet.Build;

namespace LightweightIDE.DotNet.Debugging;

public sealed record LaunchConfiguration(
    string Program,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    bool StopAtEntry = false);

public sealed record DebugThread(int Id, string Name);

/// <summary>A stack frame; Path is null for frames without source (framework code). Line/column are 1-based.</summary>
public sealed record DebugFrame(int Id, string Name, string? Path, int Line, int Column);

public sealed record DebugScope(string Name, int Reference, bool Expensive);

/// <summary>A variable; <see cref="Reference"/> &gt; 0 means it has children (fields, items).</summary>
public sealed record DebugVariable(string Name, string Value, string? Type, int Reference);

public sealed record StoppedInfo(int ThreadId, string Reason, string? Text);

/// <summary>A breakpoint as the debugger sees it: Verified = bound to code; the line may move to the nearest statement.</summary>
public sealed record BreakpointState(string Path, int RequestedLine, int Line, bool Verified, string? Message);

/// <summary>
/// One debugging session with netcoredbg (Debug Adapter Protocol over stdio): launch, breakpoints, stepping,
/// stack, variables, evaluation. Events come from the reader thread.
/// </summary>
public sealed partial class DebugSession : IAsyncDisposable
{
    readonly Process adapter;
    readonly DapClient client;
    readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    JsonNode? capabilities;
    /// <summary>Condition (C# expression) of the breakpoint at a file and line, or null.</summary>
    Func<string, int, string?>? conditionOf;

    /// <summary>The adapter evaluates breakpoint conditions itself (netcoredbg does).</summary>
    public bool SupportsConditions => (bool?)capabilities?["supportsConditionalBreakpoints"] == true;
    int exited;

    public event Action<StoppedInfo>? Stopped;
    public event Action? Continued;
    /// <summary>(category: stdout / stderr / console, text). Program output and debugger messages.</summary>
    public event Action<string, string>? Output;
    /// <summary>The program ended or the adapter exited. Raised once.</summary>
    public event Action<int?>? Terminated;
    public event Action<BreakpointState>? BreakpointChanged;

    public bool IsRunning => Volatile.Read(ref exited) == 0;

    DebugSession(Process adapter)
    {
        this.adapter = adapter;
        client = new DapClient(adapter.StandardOutput.BaseStream, adapter.StandardInput.BaseStream);
        client.EventReceived += OnEvent;
        client.Closed += () => RaiseTerminated(null);
    }

    /// <summary>Starts netcoredbg, launches the program with the breakpoints already set, and returns when it runs.</summary>
    public static Task<DebugSession> StartAsync(string adapterPath, LaunchConfiguration launch,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints, Action<string, string> output, CancellationToken ct,
        Func<string, int, string?>? conditionOf = null)
    {
        var env = new JsonObject();
        foreach (var (k, v) in launch.Environment) env[k] = v;
        var request = new JsonObject
        {
            ["name"] = ".NET Launch",
            ["type"] = "coreclr",
            ["request"] = "launch",
            ["program"] = launch.Program,
            ["args"] = new JsonArray(launch.Arguments.Select(a => (JsonNode)a!).ToArray()),
            ["cwd"] = launch.WorkingDirectory,
            ["env"] = env,
            ["stopAtEntry"] = launch.StopAtEntry,
            ["justMyCode"] = true,
        };
        return ConnectAsync(adapterPath, launch.WorkingDirectory, "launch", request, breakpoints, output, ct, conditionOf);
    }

    /// <summary>Attaches netcoredbg to a running .NET process (e.g. a testhost waiting for a debugger).</summary>
    public static Task<DebugSession> AttachAsync(string adapterPath, int processId, string workingDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints, Action<string, string> output, CancellationToken ct,
        Func<string, int, string?>? conditionOf = null) =>
        ConnectAsync(adapterPath, workingDirectory, "attach",
            new JsonObject { ["name"] = ".NET Attach", ["type"] = "coreclr", ["request"] = "attach", ["processId"] = processId, ["justMyCode"] = true },
            breakpoints, output, ct, conditionOf);

    static async Task<DebugSession> ConnectAsync(string adapterPath, string workingDirectory, string command, JsonObject request,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints, Action<string, string> output, CancellationToken ct,
        Func<string, int, string?>? conditionOf)
    {
        var info = new ProcessStartInfo(adapterPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        info.ArgumentList.Add("--interpreter=vscode");
        DotNetEnvironment.Clean(info.Environment);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start the debugger.");
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output("console", e.Data + "\n"); };
        process.BeginErrorReadLine();

        var session = new DebugSession(process) { conditionOf = conditionOf };
        session.Output += output;
        try
        {
            await session.ConfigureAsync(command, request, breakpoints, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    async Task ConfigureAsync(string command, JsonObject request, IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints, CancellationToken ct)
    {
        capabilities = await client.RequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "lightweight-ide",
            ["clientName"] = "Lightweight IDE",
            ["adapterID"] = "coreclr",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["pathFormat"] = "path",
            ["supportsVariableType"] = true,
            ["supportsRunInTerminalRequest"] = false,
        }, ct).ConfigureAwait(false);

        // The launch / attach response arrives only after configurationDone: do not await it yet.
        var launched = client.RequestAsync(command, request, ct);

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await initialized.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        foreach (var (path, lines) in breakpoints)
            foreach (var state in await SetBreakpointsAsync(path, lines, ct).ConfigureAwait(false))
                BreakpointChanged?.Invoke(state);

        // Break on exceptions the user code does not handle, when the adapter offers that filter.
        var filters = capabilities?["exceptionBreakpointFilters"]?.AsArray()
            .Select(f => (string?)f?["filter"]).OfType<string>().Where(f => f is "user-unhandled").ToArray() ?? [];
        if (filters.Length > 0)
            await client.RequestAsync("setExceptionBreakpoints", new JsonObject { ["filters"] = new JsonArray(filters.Select(f => (JsonNode)f!).ToArray()) }, ct)
                .ConfigureAwait(false);

        if ((bool?)capabilities?["supportsConfigurationDoneRequest"] != false)
            await client.RequestAsync("configurationDone", null, ct).ConfigureAwait(false);
        await launched.ConfigureAwait(false);
    }

    void OnEvent(string name, JsonNode? body)
    {
        switch (name)
        {
            case "initialized":
                initialized.TrySetResult();
                break;
            case "stopped":
                Stopped?.Invoke(new StoppedInfo((int?)body?["threadId"] ?? 0, (string?)body?["reason"] ?? "pause",
                    (string?)body?["text"] ?? (string?)body?["description"]));
                break;
            case "continued":
                Continued?.Invoke();
                break;
            case "output":
                Output?.Invoke((string?)body?["category"] ?? "console", (string?)body?["output"] ?? "");
                break;
            case "breakpoint":
                if (body?["breakpoint"] is { } bp && (string?)bp["source"]?["path"] is { } path)
                    BreakpointChanged?.Invoke(new BreakpointState(path, (int?)bp["line"] ?? 0, (int?)bp["line"] ?? 0, (bool?)bp["verified"] == true,
                        (string?)bp["message"]));
                break;
            case "exited":
                RaiseTerminated((int?)body?["exitCode"]);
                break;
            case "terminated":
                RaiseTerminated(null);
                break;
        }
    }

    int? exitCode;

    void RaiseTerminated(int? code)
    {
        exitCode ??= code;
        if (Interlocked.Exchange(ref exited, 1) == 0)
        {
            // "exited" (with the code) usually comes right before "terminated"; give it a moment.
            _ = Task.Delay(100).ContinueWith(_ => Terminated?.Invoke(exitCode), TaskScheduler.Default);
        }
    }

    // ---------- requests ----------

    public async Task<IReadOnlyList<BreakpointState>> SetBreakpointsAsync(string path, IReadOnlyList<int> lines, CancellationToken ct = default)
    {
        var body = await client.RequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = path, ["name"] = Path.GetFileName(path) },
            ["breakpoints"] = new JsonArray(lines.Select(l =>
            {
                var bp = new JsonObject { ["line"] = l };
                if (SupportsConditions && conditionOf?.Invoke(path, l) is { Length: > 0 } condition) bp["condition"] = condition;
                return (JsonNode)bp;
            }).ToArray()),
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()),
        }, ct).ConfigureAwait(false);
        var result = body?["breakpoints"]?.AsArray() ?? [];
        return result.Select((bp, i) => new BreakpointState(path, lines.ElementAtOrDefault(i), (int?)bp?["line"] ?? lines.ElementAtOrDefault(i),
            (bool?)bp?["verified"] == true, (string?)bp?["message"])).ToList();
    }

    public Task ContinueAsync(int threadId) => client.RequestAsync("continue", new JsonObject { ["threadId"] = threadId });
    public Task NextAsync(int threadId) => client.RequestAsync("next", new JsonObject { ["threadId"] = threadId });
    public Task StepInAsync(int threadId) => client.RequestAsync("stepIn", new JsonObject { ["threadId"] = threadId });
    public Task StepOutAsync(int threadId) => client.RequestAsync("stepOut", new JsonObject { ["threadId"] = threadId });
    public Task PauseAsync(int threadId) => client.RequestAsync("pause", new JsonObject { ["threadId"] = threadId });

    public async Task<IReadOnlyList<DebugThread>> ThreadsAsync()
    {
        var body = await client.RequestAsync("threads").ConfigureAwait(false);
        return (body?["threads"]?.AsArray() ?? []).Select(t => new DebugThread((int?)t?["id"] ?? 0, (string?)t?["name"] ?? "")).ToList();
    }

    public async Task<IReadOnlyList<DebugFrame>> StackTraceAsync(int threadId, int levels = 100)
    {
        var body = await client.RequestAsync("stackTrace", new JsonObject { ["threadId"] = threadId, ["startFrame"] = 0, ["levels"] = levels })
            .ConfigureAwait(false);
        return (body?["stackFrames"]?.AsArray() ?? []).Select(f => new DebugFrame((int?)f?["id"] ?? 0, (string?)f?["name"] ?? "?",
            (string?)f?["source"]?["path"], (int?)f?["line"] ?? 0, (int?)f?["column"] ?? 0)).ToList();
    }

    public async Task<IReadOnlyList<DebugScope>> ScopesAsync(int frameId)
    {
        var body = await client.RequestAsync("scopes", new JsonObject { ["frameId"] = frameId }).ConfigureAwait(false);
        return (body?["scopes"]?.AsArray() ?? []).Select(s => new DebugScope((string?)s?["name"] ?? "", (int?)s?["variablesReference"] ?? 0,
            (bool?)s?["expensive"] == true)).ToList();
    }

    public async Task<IReadOnlyList<DebugVariable>> VariablesAsync(int reference)
    {
        var body = await client.RequestAsync("variables", new JsonObject { ["variablesReference"] = reference }).ConfigureAwait(false);
        return (body?["variables"]?.AsArray() ?? []).Select(v => new DebugVariable((string?)v?["name"] ?? "", (string?)v?["value"] ?? "",
            (string?)v?["type"], (int?)v?["variablesReference"] ?? 0)).ToList();
    }

    /// <summary>Evaluates an expression in a frame (watch / hover). Returns the value and a children reference.</summary>
    public async Task<DebugVariable> EvaluateAsync(string expression, int frameId, string context = "watch")
    {
        JsonNode? body;
        try
        {
            body = await client.RequestAsync("evaluate", new JsonObject { ["expression"] = expression, ["frameId"] = frameId, ["context"] = context })
                .ConfigureAwait(false);
        }
        catch (DapException e) when (e.Message.Contains("does not exist in the current context", StringComparison.Ordinal)
                                     && LengthProperty().IsMatch(expression))
        {
            // netcoredbg 3.2 cannot resolve a property named Length (s.Length fails, s.get_Length() works).
            var rewritten = LengthProperty().Replace(expression, ".get_Length()");
            body = await client.RequestAsync("evaluate", new JsonObject { ["expression"] = rewritten, ["frameId"] = frameId, ["context"] = context })
                .ConfigureAwait(false);
        }
        return new DebugVariable(expression, (string?)body?["result"] ?? "", (string?)body?["type"], (int?)body?["variablesReference"] ?? 0);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\.Length\b(?!\s*\()")]
    private static partial System.Text.RegularExpressions.Regex LengthProperty();

    /// <summary>Ends the program and the debugger.</summary>
    public async Task StopAsync()
    {
        if (!adapter.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.RequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or DapException) { }
        }
        RaiseTerminated(null);
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync().ConfigureAwait(false);
        try { if (!adapter.HasExited) adapter.Kill(entireProcessTree: true); }
        catch (Exception) { }
        adapter.Dispose();
    }
}
