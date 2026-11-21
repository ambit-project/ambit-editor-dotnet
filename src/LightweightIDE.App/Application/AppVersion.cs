using System.Reflection;

namespace LightweightIDE.App.Application;

/// <summary>
/// The IDE's version: packaging/build-deb.sh passes -p:Version / -p:InformationalVersion and the SDK appends the
/// commit ("0.4.7+47f5e89…"). A build without a version (dotnet run, tests) is "dev".
/// </summary>
public static class AppVersion
{
    static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>"0.4.7", or "dev" for a local build.</summary>
    public static string Version { get; } = Parse(Informational).Version;

    /// <summary>Short commit hash the build was made from, or null.</summary>
    public static string? Commit { get; } = Parse(Informational).Commit;

    /// <summary>"0.4.7 (47f5e89)".</summary>
    public static string Display => Commit is null ? Version : $"{Version} ({Commit})";

    public static (string Version, string? Commit) Parse(string informational)
    {
        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;
        var commit = plus >= 0 ? informational[(plus + 1)..] : null;
        if (commit is { Length: > 7 }) commit = commit[..7];
        if (version is "" or "0.0.0" or "1.0.0") version = "dev";
        return (version, string.IsNullOrEmpty(commit) ? null : commit);
    }
}
