using System.Xml.Linq;

namespace LightweightIDE.DotNet.ProjectSystem;

/// <summary>What a project is, as far as running it goes.</summary>
public enum ProjectKind { Library, Console, Web, Worker, Test }

/// <summary>Cheap facts read straight from the .csproj XML (no MSBuild evaluation).</summary>
public sealed record ProjectInfo(string Name, string Path, string Directory, string? Sdk, IReadOnlyList<string> TargetFrameworks, bool IsTestProject,
    string? OutputType = null)
{
    /// <summary>
    /// Test projects first (xUnit v3 and Microsoft.Testing.Platform projects are OutputType Exe too), then the SDK
    /// (Web, Worker), then OutputType Exe / WinExe.
    /// </summary>
    public ProjectKind Kind =>
        IsTestProject ? ProjectKind.Test
        : Sdk?.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true ? ProjectKind.Web
        : Sdk?.StartsWith("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase) == true ? ProjectKind.Worker
        : OutputType is { } type && (type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
            ? ProjectKind.Console
        : ProjectKind.Library;

    /// <summary>Can be started with dotnet run / the debugger (not a library, not a test project).</summary>
    public bool IsRunnable => Kind is ProjectKind.Console or ProjectKind.Web or ProjectKind.Worker;

    public static ProjectInfo Read(string projectPath)
    {
        var full = System.IO.Path.GetFullPath(projectPath);
        var name = System.IO.Path.GetFileNameWithoutExtension(full);
        string? sdk = null;
        var frameworks = new List<string>();
        var isTest = false;
        string? outputType = null;
        try
        {
            var doc = XDocument.Load(full);
            var root = doc.Root!;
            sdk = (string?)root.Attribute("Sdk")
                ?? (string?)root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name");
            foreach (var element in root.Descendants())
            {
                switch (element.Name.LocalName)
                {
                    case "TargetFramework":
                        frameworks.Add(element.Value.Trim());
                        break;
                    case "TargetFrameworks":
                        frameworks.AddRange(element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    case "OutputType":
                        outputType = element.Value.Trim();
                        break;
                    case "IsTestProject" when element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase):
                        isTest = true;
                        break;
                    case "PackageReference":
                        var include = (string?)element.Attribute("Include") ?? "";
                        if (include is "Microsoft.NET.Test.Sdk" or "xunit" or "xunit.v3" or "NUnit" or "MSTest" or "MSTest.TestFramework" or "TUnit")
                            isTest = true;
                        break;
                }
            }
            if (sdk?.StartsWith("MSTest.Sdk", StringComparison.Ordinal) == true) isTest = true;
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { }
        return new ProjectInfo(name, full, System.IO.Path.GetDirectoryName(full)!, sdk, frameworks.Distinct().ToList(), isTest, outputType);
    }
}
