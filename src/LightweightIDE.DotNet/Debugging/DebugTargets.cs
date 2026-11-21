using System.Text.Json;
using System.Text.Json.Nodes;
using LightweightIDE.DotNet.Build;

namespace LightweightIDE.DotNet.Debugging;

/// <summary>A launch profile from Properties/launchSettings.json (commandName "Project").</summary>
public sealed record LaunchProfile(string Name, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Arguments, string? ApplicationUrl,
    bool LaunchBrowser = false, string? LaunchUrl = null);

/// <summary>Finds the debugger and turns a project into a launch configuration.</summary>
public static class DebugTargets
{
    /// <summary>
    /// netcoredbg: the copy the IDE manages (~/.local/share/lightweight-ide/netcoredbg), then PATH.
    /// Null when it is not installed.
    /// </summary>
    public static string? FindAdapter()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var candidates = new[] { Path.Combine(data, "lightweight-ide", "netcoredbg", "netcoredbg") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p, "netcoredbg")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public const string InstallHint =
        "Install netcoredbg (MIT, Samsung): download netcoredbg-linux-amd64.tar.gz from https://github.com/Samsung/netcoredbg/releases " +
        "and extract it to ~/.local/share/lightweight-ide/ (the IDE looks for ~/.local/share/lightweight-ide/netcoredbg/netcoredbg).";

    /// <summary>Launch profiles of a project that run the project itself (IIS / Docker profiles are skipped).</summary>
    public static IReadOnlyList<LaunchProfile> ReadLaunchProfiles(string projectPath)
    {
        var file = Path.Combine(Path.GetDirectoryName(projectPath)!, "Properties", "launchSettings.json");
        if (!File.Exists(file)) return [];
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var result = new List<LaunchProfile>();
            foreach (var (name, profile) in root?["profiles"]?.AsObject() ?? [])
            {
                if (profile is null || (string?)profile["commandName"] != "Project") continue;
                var env = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in profile["environmentVariables"]?.AsObject() ?? []) env[k] = v?.ToString() ?? "";
                var url = (string?)profile["applicationUrl"];
                if (url is not null && !env.ContainsKey("ASPNETCORE_URLS")) env["ASPNETCORE_URLS"] = url;
                var args = ((string?)profile["commandLineArgs"])?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                result.Add(new LaunchProfile(name, env, args, url, (bool?)profile["launchBrowser"] == true, (string?)profile["launchUrl"]));
            }
            return result;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { return []; }
    }

    /// <summary>
    /// The program to debug: the project's output assembly (TargetPath) for the configuration, its working directory and
    /// the environment of its first launch profile. The project must be built first.
    /// </summary>
    public static async Task<LaunchConfiguration> ForProjectAsync(string projectPath, string configuration, LaunchProfile? profile, CancellationToken ct)
    {
        var properties = await GetPropertiesAsync(projectPath, configuration, null, ct).ConfigureAwait(false);
        if (properties.GetValueOrDefault("OutputType") is not ("Exe" or "WinExe"))
            throw new InvalidOperationException($"{Path.GetFileName(projectPath)} is a library (OutputType {properties.GetValueOrDefault("OutputType")}); debug an executable project.");
        var target = properties.GetValueOrDefault("TargetPath");
        if (string.IsNullOrEmpty(target) && properties.GetValueOrDefault("TargetFrameworks") is { Length: > 0 } frameworks)
        {
            // Multi-targeting: the first framework.
            var first = frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
            target = (await GetPropertiesAsync(projectPath, configuration, first, ct).ConfigureAwait(false)).GetValueOrDefault("TargetPath");
        }
        if (string.IsNullOrEmpty(target)) throw new InvalidOperationException("Cannot find the project's output assembly (TargetPath).");
        if (!File.Exists(target)) throw new InvalidOperationException($"{target} does not exist. Build the project first.");

        profile ??= ReadLaunchProfiles(projectPath).FirstOrDefault();
        var env = new Dictionary<string, string>(profile?.Environment ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        env.TryAdd("ASPNETCORE_ENVIRONMENT", "Development");
        env.TryAdd("DOTNET_ENVIRONMENT", "Development");
        var workingDirectory = properties.GetValueOrDefault("RunWorkingDirectory") is { Length: > 0 } wd ? wd : Path.GetDirectoryName(projectPath)!;
        return new LaunchConfiguration(target, profile?.Arguments ?? [], workingDirectory, env);
    }

    static async Task<Dictionary<string, string>> GetPropertiesAsync(string projectPath, string configuration, string? framework, CancellationToken ct)
    {
        List<string> args = ["msbuild", projectPath, "-getProperty:TargetPath", "-getProperty:OutputType", "-getProperty:TargetFrameworks",
            "-getProperty:RunWorkingDirectory", $"-p:Configuration={configuration}"];
        if (framework is not null) args.Add($"-p:TargetFramework={framework}");
        var json = await ProcessRunner.CaptureAsync("dotnet", args, Path.GetDirectoryName(projectPath)!, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("dotnet msbuild -getProperty failed.");
        return ParseProperties(json);
    }

    /// <summary>Output of `dotnet msbuild -getProperty:A -getProperty:B`: {"Properties": {"A": "...", "B": "..."}}.</summary>
    public static Dictionary<string, string> ParseProperties(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = json.IndexOf('{');
        if (start < 0) return result;
        foreach (var (k, v) in JsonNode.Parse(json[start..])?["Properties"]?.AsObject() ?? []) result[k] = v?.ToString() ?? "";
        return result;
    }
}
