using System.Diagnostics;

namespace LightweightIDE.DotNet.Build;

public enum OutputStream { StdOut, StdErr }

/// <summary>Runs a process, streams its output line by line and kills the whole tree on cancellation.</summary>
public static class ProcessRunner
{
    public static async Task<int> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        Action<string, OutputStream> onLine,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        DotNetEnvironment.Clean(info.Environment);
        // Plain, parseable output from the SDK.
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["MSBUILDTERMINALLOGGER"] = "off";
        info.Environment["NO_COLOR"] = "1";
        // .NET 11: an ambient Configuration variable becomes the default -c. We always pass -c explicitly, but
        // remove it anyway so 'dotnet run'/'dotnet test' never pick up the IDE's own environment.
        info.Environment.Remove("Configuration");
        if (environment is not null)
            foreach (var (k, v) in environment)
                if (v is null) info.Environment.Remove(k); else info.Environment[k] = v;

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data, OutputStream.StdOut); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data, OutputStream.StdErr); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        process.WaitForExit(); // flush async output handlers
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>Runs a short command and returns trimmed stdout, or null on failure.</summary>
    public static async Task<string?> CaptureAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken ct = default)
    {
        var lines = new List<string>();
        try
        {
            var code = await RunAsync(fileName, arguments, workingDirectory, (l, s) => { if (s == OutputStream.StdOut) lock (lines) lines.Add(l); }, cancellationToken: ct)
                .ConfigureAwait(false);
            return code == 0 ? string.Join('\n', lines).Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
