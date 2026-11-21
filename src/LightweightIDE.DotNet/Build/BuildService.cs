using Microsoft.Extensions.Logging;

namespace LightweightIDE.DotNet.Build;

public enum BuildAction { Build, Rebuild, Clean, Run, Restore }

public sealed record BuildRequest(BuildAction Action, string Target, string Configuration = "Debug");

public sealed record BuildResult(int ExitCode, bool Cancelled, IReadOnlyList<BuildDiagnostic> Diagnostics, TimeSpan Duration)
{
    public bool Succeeded => !Cancelled && ExitCode == 0;
    public int Errors => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
    public int Warnings => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
}

/// <summary>Runs the real .NET SDK (dotnet build/clean/run) and collects diagnostics from its console output.</summary>
public sealed class BuildService(ILogger<BuildService> logger)
{
    public static IReadOnlyList<string> Arguments(BuildRequest request)
    {
        var args = new List<string>();
        switch (request.Action)
        {
            case BuildAction.Build: args.AddRange(["build", request.Target]); break;
            case BuildAction.Rebuild: args.AddRange(["build", request.Target, "--no-incremental"]); break;
            case BuildAction.Clean: args.AddRange(["clean", request.Target]); break;
            case BuildAction.Restore: args.AddRange(["restore", request.Target]); break;
            case BuildAction.Run: args.AddRange(["run", "--project", request.Target]); break;
        }
        if (request.Action != BuildAction.Restore) args.AddRange(["--configuration", request.Configuration]);
        if (request.Action != BuildAction.Run)
            args.AddRange(["-nologo", "-tl:off", "-consoleLoggerParameters:NoSummary"]);
        return args;
    }

    public async Task<BuildResult> RunAsync(BuildRequest request, string workingDirectory, Action<string> onOutput, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var diagnostics = new List<BuildDiagnostic>();
        var seen = new HashSet<BuildDiagnostic>();
        var args = Arguments(request);
        onOutput($"> dotnet {string.Join(' ', args.Select(Quote))}");
        logger.LogInformation("Running dotnet {Arguments}", string.Join(' ', args));
        try
        {
            var exit = await ProcessRunner.RunAsync("dotnet", args, workingDirectory, (line, _) =>
            {
                onOutput(line);
                if (request.Action == BuildAction.Run) return;
                if (MsBuildOutputParser.TryParse(line) is { } d)
                    lock (diagnostics)
                        if (seen.Add(d)) diagnostics.Add(d);
            }, cancellationToken: ct).ConfigureAwait(false);
            var result = new BuildResult(exit, false, diagnostics, DateTime.UtcNow - started);
            onOutput($"{request.Action} finished in {result.Duration.TotalSeconds:0.0}s: exit {exit}, {result.Errors} error(s), {result.Warnings} warning(s).");
            return result;
        }
        catch (OperationCanceledException)
        {
            onOutput($"{request.Action} cancelled.");
            return new BuildResult(-1, true, diagnostics, DateTime.UtcNow - started);
        }
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
