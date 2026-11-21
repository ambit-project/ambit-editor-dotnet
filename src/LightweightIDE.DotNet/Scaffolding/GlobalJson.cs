using System.Text.Json;
using System.Text.Json.Nodes;

namespace LightweightIDE.DotNet.Scaffolding;

/// <summary>The SDK part of a global.json (null = not set).</summary>
public sealed record GlobalJsonSettings(string? Version, string? RollForward, bool? AllowPrerelease, string? TestRunner);

/// <summary>Reads and writes global.json, keeping every property the IDE does not manage (msbuild-sdks, paths, …).</summary>
public static class GlobalJson
{
    public const string FileName = "global.json";

    /// <summary>Roll-forward policies of the dotnet host, most useful first.</summary>
    public static readonly IReadOnlyList<string> RollForwardPolicies =
        ["latestFeature", "latestPatch", "disable", "patch", "feature", "minor", "latestMinor", "major", "latestMajor"];

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The global.json the dotnet CLI uses for a folder: the nearest one in it or above it.</summary>
    public static string? Find(string folder)
    {
        for (var dir = Path.GetFullPath(folder); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, FileName);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    public static GlobalJsonSettings Read(string path)
    {
        var root = Parse(File.ReadAllText(path));
        var sdk = root["sdk"] as JsonObject;
        return new GlobalJsonSettings(
            sdk?["version"]?.GetValue<string>(),
            sdk?["rollForward"]?.GetValue<string>(),
            sdk?["allowPrerelease"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null,
            (root["test"] as JsonObject)?["runner"]?.GetValue<string>());
    }

    /// <summary>
    /// Sets sdk.version and sdk.rollForward (creating the file if needed). <paramref name="testRunner"/>: null keeps the
    /// current value, empty removes it, otherwise sets test.runner (e.g. Microsoft.Testing.Platform).
    /// </summary>
    public static void Write(string path, string version, string rollForward, string? testRunner = null)
    {
        var root = File.Exists(path) ? Parse(File.ReadAllText(path)) : new JsonObject();
        if (root["sdk"] is not JsonObject sdk)
        {
            sdk = new JsonObject();
            root["sdk"] = sdk;
        }
        sdk["version"] = version;
        sdk["rollForward"] = rollForward;
        if (version.Contains('-')) sdk["allowPrerelease"] = true;
        if (testRunner is not null)
        {
            if (testRunner.Length == 0)
            {
                if (root["test"] is JsonObject t)
                {
                    t.Remove("runner");
                    if (t.Count == 0) root.Remove("test");
                }
            }
            else
            {
                if (root["test"] is not JsonObject test)
                {
                    test = new JsonObject();
                    root["test"] = test;
                }
                test["runner"] = testRunner;
            }
        }
        File.WriteAllText(path, root.ToJsonString(Indented) + "\n");
    }

    static JsonObject Parse(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
        ?? new JsonObject();
}
