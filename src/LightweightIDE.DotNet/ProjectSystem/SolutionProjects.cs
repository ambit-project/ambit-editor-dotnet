using LightweightIDE.DotNet.Build;

namespace LightweightIDE.DotNet.ProjectSystem;

/// <summary>Adds existing projects to a solution and removes them, with <c>dotnet sln add / remove</c>. Project files stay on disk.</summary>
public static class SolutionProjects
{
    /// <param name="solutionFolder">
    /// Solution folder ("tests" or "src/Shared") to put the projects in; null lets dotnet decide from the project's
    /// directory, like Add Project does.
    /// </param>
    public static Task AddAsync(string solution, IReadOnlyList<string> projects, string? solutionFolder, Action<string> output, CancellationToken ct)
    {
        List<string> args = ["sln", solution, "add", .. projects];
        if (solutionFolder is { Length: > 0 } folder) args.AddRange(["--solution-folder", folder]);
        return RunAsync(solution, args, output, ct);
    }

    public static Task RemoveAsync(string solution, IReadOnlyList<string> projects, Action<string> output, CancellationToken ct) =>
        RunAsync(solution, ["sln", solution, "remove", .. projects], output, ct);

    /// <summary>Projects among <paramref name="candidates"/> that reference <paramref name="project"/> directly.</summary>
    public static IReadOnlyList<string> ReferencedBy(string project, IEnumerable<string> candidates)
    {
        var full = Path.GetFullPath(project);
        return candidates.Where(c => Path.GetFullPath(c) != full && ProjectReferences.Read(c).Contains(full)).ToList();
    }

    static async Task RunAsync(string solution, IReadOnlyList<string> args, Action<string> output, CancellationToken ct)
    {
        output($"> dotnet {string.Join(' ', args)}");
        var lines = new List<string>();
        var exit = await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(Path.GetFullPath(solution))!, (line, _) =>
        {
            lines.Add(line);
            output(line);
        }, cancellationToken: ct).ConfigureAwait(false);
        if (exit != 0)
        {
            var reason = lines.LastOrDefault(l => l.Trim().Length > 0)?.Trim();
            throw new InvalidOperationException($"dotnet sln {args[2]} failed (exit code {exit}){(reason is null ? "" : ": " + reason)}");
        }
    }
}
