using System.Text.RegularExpressions;
using LightweightIDE.DotNet.Build;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.DotNet.Scaffolding;

/// <summary>Everything the New Solution dialog asks for.</summary>
public sealed record NewSolutionRequest(
    string ParentDirectory,
    string Name,
    string SdkVersion,
    string RollForward,
    string Framework,
    bool Slnx,
    ProjectTemplate? FirstProject,
    string? FirstProjectName,
    bool AddTests,
    bool DirectoryBuildProps,
    bool CentralPackageManagement,
    bool EditorConfig,
    bool CSharpier,
    bool GitInit)
{
    public string Folder => Path.Combine(ParentDirectory, Name);
}

/// <param name="Framework">Null = the template's default for the folder's SDK.</param>
/// <param name="ReferenceProject">A project the new one references (typically the tested project).</param>
/// <param name="Restore">Restore after creating (off when the caller restores the whole solution later).</param>
public sealed record AddProjectRequest(string SolutionPath, ProjectTemplate Template, string Name, string? Framework, string? ReferenceProject, bool Restore = true);

/// <summary>
/// Creates solutions and projects with the dotnet CLI. global.json is written first, so every later `dotnet new`
/// runs with the chosen SDK and its own templates. Each step is logged; a failing step stops with its name.
/// </summary>
public sealed partial class SolutionScaffolder(ILogger<SolutionScaffolder> logger)
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$")]
    private static partial Regex NamePattern();

    /// <summary>Null when the name can be a solution, project and folder name; otherwise why not.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (!NamePattern().IsMatch(name)) return "Use letters, digits, '.', '-' or '_' and start with a letter.";
        if (name.EndsWith('.') || name.Contains("..", StringComparison.Ordinal)) return "The name cannot end with '.' or contain '..'.";
        return null;
    }

    public const string DirectoryBuildPropsContent = """
        <Project>
          <PropertyGroup>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <LangVersion>latest</LangVersion>
          </PropertyGroup>
        </Project>

        """;

    /// <summary>Creates the folder, global.json, solution, optional projects and files. Returns the solution path.</summary>
    public async Task<string> CreateSolutionAsync(NewSolutionRequest request, Action<string> output, CancellationToken ct)
    {
        if (ValidateName(request.Name) is { } invalid) throw new ArgumentException(invalid);
        var folder = request.Folder;
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException($"{folder} already exists and is not empty.");
        Directory.CreateDirectory(folder);

        output($"Creating {request.Name} with .NET SDK {request.SdkVersion} ({request.Framework}) in {folder}");
        GlobalJson.Write(Path.Combine(folder, GlobalJson.FileName), request.SdkVersion, request.RollForward);
        output($"Wrote global.json: sdk {request.SdkVersion}, rollForward {request.RollForward}");

        var sdk = await DotNetAsync(folder, output, ct, "--version").ConfigureAwait(false);
        List<string> sln = ["new", "sln", "-n", request.Name];
        if (new InstalledSdk(request.SdkVersion).SupportsSlnx) sln.AddRange(["--format", request.Slnx ? "slnx" : "sln"]);
        await RunAsync("Create solution", folder, sln, output, ct).ConfigureAwait(false);
        var solution = Directory.EnumerateFiles(folder, request.Name + ".sln*").First();

        if (request.DirectoryBuildProps)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "Directory.Build.props"), DirectoryBuildPropsContent, ct).ConfigureAwait(false);
            output("Wrote Directory.Build.props");
        }
        if (request.CentralPackageManagement)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, CentralPackages.FileName), CentralPackages.NewFileContent, ct).ConfigureAwait(false);
            output("Wrote Directory.Packages.props (Central Package Management)");
        }
        await RunAsync(".gitignore", folder, ["new", "gitignore"], output, ct).ConfigureAwait(false);
        if (request.EditorConfig) await RunAsync(".editorconfig", folder, ["new", "editorconfig"], output, ct).ConfigureAwait(false);

        string? main = null;
        if (request.FirstProject is { } template)
        {
            var name = string.IsNullOrWhiteSpace(request.FirstProjectName) ? request.Name + template.Suffix : request.FirstProjectName.Trim();
            main = await AddProjectAsync(new AddProjectRequest(solution, template, name, request.Framework, null, Restore: false), output, ct).ConfigureAwait(false);
        }
        if (request.AddTests)
            await AddProjectAsync(new AddProjectRequest(solution, ProjectTemplates.Get("xunit"), request.Name + ".Tests", request.Framework, main, Restore: false), output, ct)
                .ConfigureAwait(false);

        if (request.CSharpier)
        {
            await RunAsync("Tool manifest", folder, ["new", "tool-manifest"], output, ct).ConfigureAwait(false);
            await RunAsync("Install CSharpier", folder, ["tool", "install", "csharpier"], output, ct).ConfigureAwait(false);
        }
        if (request.GitInit)
        {
            var exit = await ProcessRunner.RunAsync("git", ["init", "-b", "main"], folder, (l, _) => output(l), cancellationToken: ct).ConfigureAwait(false);
            if (exit != 0) output("git init failed; the solution is fine without it.");
        }
        if (request.FirstProject is not null || request.AddTests)
            await RunAsync("Restore", folder, ["restore", solution], output, ct).ConfigureAwait(false);

        output($"Done: {solution} (SDK {sdk})");
        logger.LogInformation("Created solution {Solution} with SDK {Sdk}", solution, sdk);
        return solution;
    }

    /// <summary>Creates a project under src/ or tests/ next to the solution, adds it to the solution. Returns the project path.</summary>
    public async Task<string> AddProjectAsync(AddProjectRequest request, Action<string> output, CancellationToken ct)
    {
        if (ValidateName(request.Name) is { } invalid) throw new ArgumentException(invalid);
        var root = Path.GetDirectoryName(Path.GetFullPath(request.SolutionPath))!;
        var relative = Path.Combine(request.Template.Folder, request.Name);
        var directory = Path.Combine(root, relative);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException($"{directory} already exists and is not empty.");

        // No restore yet: under Central Package Management the template's versions must move to the props file first.
        List<string> args = ["new", request.Template.ShortName, "-n", request.Name, "-o", relative, "--no-restore"];
        if (request.Framework is not null) args.AddRange(["-f", request.Framework]);
        await RunAsync($"Create project {request.Name}", root, args, output, ct).ConfigureAwait(false);
        var project = Path.Combine(directory, request.Name + ".csproj");
        if (!File.Exists(project)) throw new InvalidOperationException($"dotnet new did not create {project}.");

        // `dotnet sln add` puts the project into a solution folder named after its directory (src / tests).
        await RunAsync("Add to solution", root, ["sln", request.SolutionPath, "add", project], output, ct).ConfigureAwait(false);
        if (request.ReferenceProject is { } reference)
            await RunAsync("Add project reference", root, ["add", project, "reference", reference], output, ct).ConfigureAwait(false);

        if (CentralPackages.FindEnabledFile(project) is { } props)
        {
            var moved = CentralPackages.MigrateProject(props, project);
            if (moved.Count > 0) output($"Central Package Management: moved {string.Join(", ", moved)} to {CentralPackages.FileName}");
        }
        if (request.Restore) await RunAsync("Restore", root, ["restore", project], output, ct).ConfigureAwait(false);
        return project;
    }

    async Task RunAsync(string step, string folder, IReadOnlyList<string> args, Action<string> output, CancellationToken ct)
    {
        output($"> dotnet {string.Join(' ', args)}");
        var exit = await ProcessRunner.RunAsync("dotnet", args, folder, (line, _) => output(line), cancellationToken: ct).ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException($"{step} failed (dotnet {args[0]} {args.ElementAtOrDefault(1)}, exit code {exit}). See Output.");
    }

    static async Task<string> DotNetAsync(string folder, Action<string> output, CancellationToken ct, params string[] args) =>
        await ProcessRunner.CaptureAsync("dotnet", args, folder, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The SDK selected by global.json is not installed.");
}
