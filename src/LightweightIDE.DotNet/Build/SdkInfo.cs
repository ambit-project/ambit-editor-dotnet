namespace LightweightIDE.DotNet.Build;

/// <summary>Which SDK the dotnet CLI resolves for a folder (honours global.json, so .NET 10 and .NET 11 both work).</summary>
public static class SdkInfo
{
    public static Task<string?> ResolveVersionAsync(string folder, CancellationToken ct = default) =>
        ProcessRunner.CaptureAsync("dotnet", ["--version"], folder, ct);

    public static async Task<IReadOnlyList<string>> ListSdksAsync(CancellationToken ct = default)
    {
        var output = await ProcessRunner.CaptureAsync("dotnet", ["--list-sdks"], Environment.CurrentDirectory, ct).ConfigureAwait(false);
        return output is null ? [] : output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')[0]).ToList();
    }
}
