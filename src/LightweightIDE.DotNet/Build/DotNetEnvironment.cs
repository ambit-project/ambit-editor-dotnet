using System.Collections;

namespace LightweightIDE.DotNet.Build;

/// <summary>
/// When the IDE (or a test host) is started by the dotnet CLI it inherits variables such as MSBuildSDKsPath and
/// MSBUILD_EXE_PATH that point at that CLI's SDK. Passed on to a child 'dotnet' that resolves a different SDK through
/// global.json (e.g. SDK 10 vs SDK 11), they make MSBuild mix two SDKs and fail. Child processes and Roslyn's
/// BuildHost must see a clean environment so every project uses the SDK its own global.json selects.
/// </summary>
public static class DotNetEnvironment
{
    static readonly string[] ExactNames =
    [
        "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly",
        "MSBuildToolsPath", "MSBUILDNOINPROCNODE", "DOTNET_HOST_PATH", "MSBuildStartupDirectory",
        "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
        "MSBuildCacheEnabled", "_MSBUILDTLENABLED", "VSTEST_HOST_DEBUG", "TESTINGPLATFORM_DOTNETTEST_EXECUTIONID",
    ];

    public static bool ShouldRemove(string name) =>
        ExactNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.StartsWith("DOTNET_MSBUILD_SDK_RESOLVER", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("MSBUILDNODE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes inherited SDK-specific variables from a child process environment.</summary>
    public static void Clean(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.Where(ShouldRemove).ToList()) environment.Remove(key);
    }

    /// <summary>Removes them from the current process (call once at startup, before MSBuildWorkspace is created).</summary>
    public static void CleanCurrentProcess()
    {
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string name && ShouldRemove(name))
                Environment.SetEnvironmentVariable(name, null);
    }
}
