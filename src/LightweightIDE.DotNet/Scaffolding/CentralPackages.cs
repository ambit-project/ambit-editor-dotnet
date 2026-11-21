using System.Xml;
using System.Xml.Linq;

namespace LightweightIDE.DotNet.Scaffolding;

/// <summary>
/// Central Package Management helpers. Templates from `dotnet new` write versions into the project file
/// (&lt;PackageReference Include="x" Version="1.0" /&gt;), which breaks the build under CPM (NU1008), so new projects
/// are migrated: the version moves to Directory.Packages.props and the reference keeps only Include.
/// </summary>
public static class CentralPackages
{
    public const string FileName = "Directory.Packages.props";

    public const string NewFileContent = """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
          </ItemGroup>
        </Project>

        """;

    /// <summary>Directory.Packages.props that manages versions for a project, or null when CPM is off.</summary>
    public static string? FindEnabledFile(string projectOrFolder)
    {
        var start = Directory.Exists(projectOrFolder) ? projectOrFolder : Path.GetDirectoryName(Path.GetFullPath(projectOrFolder));
        for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, FileName);
            if (!File.Exists(file)) continue;
            try
            {
                var enabled = XDocument.Load(file).Descendants("ManagePackageVersionsCentrally").LastOrDefault()?.Value.Trim();
                return string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) ? file : null;
            }
            catch (Exception e) when (e is IOException or XmlException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Moves package versions from a project into the props file. A version already present in the props file wins
    /// (the solution keeps one version per package). Returns the package ids that were moved.
    /// </summary>
    public static IReadOnlyList<string> MigrateProject(string propsPath, string projectPath)
    {
        var project = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var props = XDocument.Load(propsPath, LoadOptions.PreserveWhitespace);
        var central = props.Descendants("PackageVersion")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var moved = new List<string>();
        foreach (var reference in project.Descendants("PackageReference").ToList())
        {
            var id = (string?)reference.Attribute("Include");
            var versionAttribute = reference.Attribute("Version");
            var versionElement = reference.Element("Version");
            var version = versionAttribute?.Value ?? versionElement?.Value;
            if (id is null || version is null) continue;
            versionAttribute?.Remove();
            versionElement?.Remove();
            if (central.Add(id)) AddPackageVersion(props, id, version);
            moved.Add(id);
        }
        if (moved.Count == 0) return moved;
        Save(project, projectPath);
        Save(props, propsPath);
        return moved;
    }

    static void AddPackageVersion(XDocument props, string id, string version)
    {
        var root = props.Root!;
        var group = root.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageVersion").Any())
            ?? root.Elements("ItemGroup").FirstOrDefault(g => !g.Elements().Any());
        if (group is null)
        {
            group = new XElement("ItemGroup", new XText("\n  "));
            root.Add(new XText("  "), group, new XText("\n"));
        }
        // Keep the file readable: one element per line, indented like the rest, sorted by id.
        var element = new XElement("PackageVersion", new XAttribute("Include", id), new XAttribute("Version", version));
        var after = group.Elements("PackageVersion").LastOrDefault(e => string.Compare((string?)e.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase) < 0);
        if (after is not null) after.AddAfterSelf(new XText("\n    "), element);
        else if (group.Elements("PackageVersion").FirstOrDefault() is { } first) first.AddBeforeSelf(element, new XText("\n    "));
        else
        {
            group.RemoveNodes();
            group.Add(new XText("\n    "), element, new XText("\n  "));
        }
    }

    static void Save(XDocument document, string path)
    {
        var settings = new XmlWriterSettings { OmitXmlDeclaration = document.Declaration is null, Indent = false, Encoding = new System.Text.UTF8Encoding(false) };
        using (var writer = XmlWriter.Create(path, settings)) document.Save(writer);
        // XmlWriter drops the final newline.
        var text = File.ReadAllText(path);
        if (!text.EndsWith('\n')) File.AppendAllText(path, "\n");
    }
}
