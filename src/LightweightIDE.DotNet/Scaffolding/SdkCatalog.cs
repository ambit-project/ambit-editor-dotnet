using System.Collections.Concurrent;
using LightweightIDE.DotNet.Build;

namespace LightweightIDE.DotNet.Scaffolding;

/// <summary>An installed .NET SDK.</summary>
public sealed record InstalledSdk(string Version)
{
    public int Major => int.TryParse(Version.Split('.')[0], out var m) ? m : 0;
    public int Feature => Version.Split('.') is { Length: > 2 } p && int.TryParse(new string(p[2].TakeWhile(char.IsDigit).ToArray()), out var f) ? f : 0;
    public bool IsPreview => Version.Contains('-');

    /// <summary>`dotnet new sln --format slnx` exists since SDK 9.0.200; SDK 10+ creates .slnx by default.</summary>
    public bool SupportsSlnx => Major >= 10 || (Major == 9 && Feature >= 200);

    public string DefaultFramework => $"net{Major}.0";

    public override string ToString() => Version;
}

/// <summary>Installed SDKs and what their templates offer. Each query runs dotnet in a folder whose global.json pins the SDK.</summary>
public static class SdkCatalog
{
    static readonly ConcurrentDictionary<(string Sdk, string Template), IReadOnlyList<string>> frameworks = new();

    public static async Task<IReadOnlyList<InstalledSdk>> ListAsync(CancellationToken ct = default) =>
        (await SdkInfo.ListSdksAsync(ct).ConfigureAwait(false))
            .Select(v => new InstalledSdk(v))
            .OrderByDescending(s => s.Major).ThenByDescending(s => s.Feature).ThenByDescending(s => s.Version, StringComparer.Ordinal)
            .ToList();

    /// <summary>Target frameworks the template of this SDK accepts (from `dotnet new &lt;template&gt; -h`), newest first.</summary>
    public static async Task<IReadOnlyList<string>> FrameworksAsync(string sdkVersion, string template, CancellationToken ct = default)
    {
        if (frameworks.TryGetValue((sdkVersion, template), out var cached)) return cached;
        string? help;
        using (var pinned = PinnedSdkDirectory.Create(sdkVersion))
            help = await ProcessRunner.CaptureAsync("dotnet", ["new", template, "-h"], pinned.Path, ct).ConfigureAwait(false);
        var list = ParseFrameworkChoices(help ?? "");
        if (list.Count == 0) list = [new InstalledSdk(sdkVersion).DefaultFramework];
        frameworks[(sdkVersion, template)] = list;
        return list;
    }

    /// <summary>Choices listed under the "-f, --framework" option of `dotnet new` help.</summary>
    public static IReadOnlyList<string> ParseFrameworkChoices(string help)
    {
        var lines = help.Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("--framework", StringComparison.Ordinal));
        if (start < 0) return [];
        var result = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("Default:", StringComparison.Ordinal) || line.StartsWith('-')) break;
            var word = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (word is not null && word.StartsWith("net", StringComparison.Ordinal) && word.Length > 3 && char.IsDigit(word[3])) result.Add(word);
        }
        return result.Distinct().ToList();
    }
}

/// <summary>A temporary folder with a global.json that pins one SDK, so dotnet commands run with exactly that SDK.</summary>
public sealed class PinnedSdkDirectory : IDisposable
{
    public string Path { get; }

    PinnedSdkDirectory(string path) => Path = path;

    public static PinnedSdkDirectory Create(string sdkVersion)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lightweight-ide", "sdk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        GlobalJson.Write(System.IO.Path.Combine(path, GlobalJson.FileName), sdkVersion, "disable");
        return new PinnedSdkDirectory(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
