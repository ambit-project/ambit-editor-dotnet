using System.Collections.Immutable;
using LightweightIDE.DotNet.SolutionDiscovery;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.Language.RoslynWorkspace;

public enum WorkspaceState { NotLoaded, Loading, Ready, Failed }

/// <summary>
/// Owns the Roslyn MSBuildWorkspace for the opened folder.
/// Loading evaluates projects out of process (Roslyn's BuildHost runs with the SDK chosen by the folder's
/// global.json, so .NET 10 and .NET 11 projects both load). Syntax trees and compilations stay lazy: a project is
/// compiled only when a feature needs a semantic model from one of its documents.
/// Unsaved editor text is applied as an overlay on top of <see cref="Solution"/>.
/// </summary>
public sealed class WorkspaceService(ILogger<WorkspaceService> logger, LanguageOptions options) : IDisposable
{
    readonly Lock gate = new();
    MSBuildWorkspace? workspace;
    Solution? solution;
    Task? loading;
    CancellationTokenSource? loadCts;
    readonly Dictionary<string, SourceText> overlays = new(StringComparer.Ordinal);

    public WorkspaceState State { get; private set; } = WorkspaceState.NotLoaded;
    public string? StatusMessage { get; private set; }
    public ImmutableList<string> LoadErrors { get; private set; } = [];

    public event Action? StateChanged;

    /// <summary>The text of a C# file changed (editor or disk); the path is the file's.</summary>
    public event Action<string>? DocumentChanged;

    int nugetWarnings;

    static bool IsNuGetWarning(string message) =>
        message.Contains("severity vulnerability", StringComparison.Ordinal)
        || message.Contains("package source mapping", StringComparison.Ordinal)
        || message.Contains("package sources defined", StringComparison.Ordinal);

    public Solution? CurrentSolution
    {
        get { lock (gate) return solution; }
    }

    public Task LoadAsync(WorkspaceRoot root, IProgress<string>? progress = null)
    {
        lock (gate)
        {
            if (loading is not null) return loading;
            loadCts = new CancellationTokenSource();
            loading = Task.Run(() => LoadCoreAsync(root, progress, loadCts.Token));
            return loading;
        }
    }

    async Task LoadCoreAsync(WorkspaceRoot root, IProgress<string>? progress, CancellationToken ct)
    {
        SetState(WorkspaceState.Loading, "Loading projects…");
        var errors = ImmutableList.CreateBuilder<string>();
        nugetWarnings = 0;
        try
        {
            var ws = MSBuildWorkspace.Create(new Dictionary<string, string>
            {
                ["Configuration"] = options.Configuration,
                // Analyzers and generators still run; skip things that are irrelevant for editing.
                ["RunAnalyzersDuringBuild"] = "false",
            });
            ws.RegisterWorkspaceFailedHandler(e =>
            {
                if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure) return;
                // NuGet warnings stored in project.assets.json (vulnerable packages, source mapping) are replayed as
                // "failures" although the project loads fine; `dotnet build` reports them where they belong.
                if (IsNuGetWarning(e.Diagnostic.Message))
                {
                    Interlocked.Increment(ref nugetWarnings);
                    return;
                }
                lock (errors) errors.Add(e.Diagnostic.Message);
                logger.LogWarning("Workspace: {Message}", e.Diagnostic.Message);
            });
            var reporter = new Progress<ProjectLoadProgress>(p =>
            {
                var message = $"{p.Operation} {Path.GetFileNameWithoutExtension(p.FilePath)}{(p.TargetFramework is null ? "" : $" ({p.TargetFramework})")}";
                StatusMessage = message;
                progress?.Report(message);
            });

            Solution loaded;
            if (root.Solution is not null)
            {
                loaded = await ws.OpenSolutionAsync(root.Solution.Path, progress: reporter, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                foreach (var project in root.LooseProjects)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ws.CurrentSolution.Projects.Any(p => string.Equals(p.FilePath, project, StringComparison.Ordinal))) continue;
                    try { await ws.OpenProjectAsync(project, progress: reporter, cancellationToken: ct).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{project}: {e.Message}"); }
                }
                loaded = ws.CurrentSolution;
            }

            lock (gate)
            {
                workspace = ws;
                solution = loaded;
                foreach (var (path, text) in overlays) solution = ApplyOverlay(solution, path, text);
            }
            LoadErrors = errors.ToImmutable();
            if (nugetWarnings > 0) logger.LogInformation("Workspace: {Count} NuGet warnings (vulnerable packages, source mapping) not shown as load errors", nugetWarnings);
            var count = loaded.Projects.Count();
            SetState(WorkspaceState.Ready, $"{count} project(s) loaded" + (errors.Count > 0 ? $", {errors.Count} problem(s)" : ""));
            logger.LogInformation("Workspace ready: {Projects} projects, {Errors} load errors", count, errors.Count);
        }
        catch (OperationCanceledException)
        {
            SetState(WorkspaceState.NotLoaded, "Loading cancelled");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Workspace load failed");
            LoadErrors = [e.Message];
            SetState(WorkspaceState.Failed, "Project load failed: " + e.Message);
        }
    }

    void SetState(WorkspaceState state, string message)
    {
        State = state;
        StatusMessage = message;
        StateChanged?.Invoke();
    }

    /// <summary>Records the editor's current text for a file (unsaved changes are visible to all features).</summary>
    public void UpdateText(string path, string text)
    {
        var source = SourceText.From(text);
        lock (gate)
        {
            overlays[path] = source;
            if (solution is not null) solution = ApplyOverlay(solution, path, source);
        }
        DocumentChanged?.Invoke(path);
    }

    /// <summary>The editor closed the file: drop the overlay and fall back to disk content.</summary>
    public void CloseDocument(string path)
    {
        lock (gate)
        {
            overlays.Remove(path);
            if (solution is null || !File.Exists(path)) return;
            try { solution = ApplyOverlay(solution, path, SourceText.From(File.ReadAllText(path))); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// A file changed on disk (watcher, rename, git checkout). Files open in the editor keep their overlay; other
    /// documents take the new disk content so Roslyn never works on stale text.
    /// </summary>
    public void RefreshFromDisk(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        lock (gate)
        {
            if (solution is null || overlays.ContainsKey(path) || !solution.GetDocumentIdsWithFilePath(path).Any()) return;
        }
        SourceText text;
        try { text = SourceText.From(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        lock (gate)
        {
            if (solution is not null && !overlays.ContainsKey(path)) solution = ApplyOverlay(solution, path, text);
        }
        DocumentChanged?.Invoke(path);
    }

    static Solution ApplyOverlay(Solution s, string path, SourceText text)
    {
        foreach (var id in s.GetDocumentIdsWithFilePath(path))
            s = s.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
        return s;
    }

    /// <summary>The document for a file. With several target frameworks the first project wins.</summary>
    public Document? FindDocument(string path)
    {
        var s = CurrentSolution;
        if (s is null) return null;
        var id = s.GetDocumentIdsWithFilePath(path).FirstOrDefault();
        return id is null ? null : s.GetDocument(id);
    }

    /// <summary>
    /// A document for a file that is not part of the loaded workspace (or before loading): a one-file ad hoc project
    /// referencing the runtime assemblies, so syntax errors and basic completion still work.
    /// </summary>
    public Document CreateMiscDocument(string path, string text)
    {
        var adhoc = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
        var project = adhoc.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "Miscellaneous", "Miscellaneous", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            metadataReferences: references));
        return adhoc.AddDocument(project.Id, Path.GetFileName(path), SourceText.From(text)).WithFilePath(path);
    }

    public Document GetDocumentOrMisc(string path, string text) => FindDocument(path) ?? CreateMiscDocument(path, text);

    public void Unload()
    {
        lock (gate)
        {
            loadCts?.Cancel();
            workspace?.Dispose();
            workspace = null;
            solution = null;
            loading = null;
            overlays.Clear();
        }
        SetState(WorkspaceState.NotLoaded, "");
    }

    /// <summary>Reloads projects (e.g. after a .csproj change), keeping unsaved overlays.</summary>
    public Task ReloadAsync(WorkspaceRoot root, IProgress<string>? progress = null)
    {
        Dictionary<string, SourceText> keep;
        lock (gate) keep = new(overlays);
        Unload();
        lock (gate) foreach (var (k, v) in keep) overlays[k] = v;
        return LoadAsync(root, progress);
    }

    public void Dispose() => Unload();
}
