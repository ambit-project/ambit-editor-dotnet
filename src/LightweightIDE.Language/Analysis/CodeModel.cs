using System.Collections.Concurrent;
using System.Diagnostics;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.Language.Analysis;

/// <summary>A type declared in the solution's sources.</summary>
public sealed record SourceType(string Name, string FullName, string Id, TypeKind Kind, bool IsAbstract, bool IsStatic, string Project, string? Path, int Line);

/// <summary>
/// What the IDE knows about the open solution, computed in the background after the workspace loads, like the
/// solution analysis of Rider / Visual Studio: every project is compiled once (in dependency order, so later requests
/// are warm) and a type hierarchy index is built — which classes implement an interface or derive from a class,
/// also for interfaces from packages (IHostedService, IValidator&lt;T&gt;, …). An edited project is re-indexed alone,
/// a moment after typing stops.
/// </summary>
public sealed class CodeModel : IDisposable
{
    readonly WorkspaceService workspace;
    readonly ILogger<CodeModel> logger;
    readonly Lock gate = new();
    // base type id (DocumentationCommentId of the original definition) → project → types deriving from it / implementing it.
    Dictionary<string, Dictionary<ProjectId, List<SourceType>>> derived = new(StringComparer.Ordinal);
    Dictionary<ProjectId, List<SourceType>> typesByProject = [];
    readonly ConcurrentDictionary<ProjectId, byte> dirty = new();
    CancellationTokenSource? runCts;
    Timer? debounce;

    public CodeModel(WorkspaceService workspace, ILogger<CodeModel> logger)
    {
        this.workspace = workspace;
        this.logger = logger;
        workspace.StateChanged += OnWorkspaceState;
        workspace.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Projects analysed / all; both 0 before the solution loads.</summary>
    public (int Done, int Total) Progress { get; private set; }
    public bool IsReady { get; private set; }
    public int TypeCount { get; private set; }
    public TimeSpan Duration { get; private set; }

    /// <summary>Raised (on a background thread) when the progress or the index changes.</summary>
    public event Action? Changed;

    /// <summary>Concrete and abstract types deriving from / implementing <paramref name="baseType"/>, transitively.</summary>
    public IReadOnlyList<SourceType> Implementations(INamedTypeSymbol baseType)
    {
        var id = Id(baseType.OriginalDefinition);
        if (id is null) return [];
        var result = new List<SourceType>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            if (!derived.TryGetValue(id, out var byProject)) return [];
            foreach (var type in byProject.Values.SelectMany(t => t))
                if (seen.Add(type.Id)) result.Add(type);
        }
        return result.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Waits until the first analysis has finished (or the timeout passes).</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (!IsReady && watch.Elapsed < timeout) await Task.Delay(50, ct).ConfigureAwait(false);
        return IsReady;
    }

    void OnWorkspaceState()
    {
        if (workspace.State != WorkspaceState.Ready)
        {
            if (workspace.State is WorkspaceState.Loading or WorkspaceState.NotLoaded)
            {
                runCts?.Cancel();
                IsReady = false;
                Progress = (0, 0);
                Changed?.Invoke();
            }
            return;
        }
        runCts?.Cancel();
        var cts = runCts = new CancellationTokenSource();
        _ = Task.Run(() => AnalyzeAllAsync(cts.Token));
    }

    void OnDocumentChanged(string path)
    {
        if (!IsReady || workspace.CurrentSolution is not { } solution) return;
        foreach (var id in solution.GetDocumentIdsWithFilePath(path)) dirty[id.ProjectId] = 0;
        debounce ??= new Timer(_ => _ = Task.Run(ReindexDirtyAsync));
        debounce.Change(1500, Timeout.Infinite);
    }

    async Task AnalyzeAllAsync(CancellationToken ct)
    {
        var solution = workspace.CurrentSolution;
        if (solution is null) return;
        var watch = Stopwatch.StartNew();
        var order = solution.GetProjectDependencyGraph().GetTopologicallySortedProjects(ct).ToList();
        Progress = (0, order.Count);
        Changed?.Invoke();
        var types = new Dictionary<ProjectId, List<SourceType>>();
        var index = new Dictionary<string, Dictionary<ProjectId, List<SourceType>>>(StringComparer.Ordinal);
        try
        {
            foreach (var projectId in order)
            {
                ct.ThrowIfCancellationRequested();
                if (solution.GetProject(projectId) is { } project)
                {
                    try { await IndexProjectAsync(project, types, index, ct).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Analysis of {Project} failed", project.Name); }
                }
                Progress = (Progress.Done + 1, order.Count);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { return; }
        lock (gate)
        {
            typesByProject = types;
            derived = index;
        }
        TypeCount = types.Values.Sum(t => t.Count);
        Duration = watch.Elapsed;
        IsReady = true;
        logger.LogInformation("Solution analysis: {Projects} projects, {Types} types in {Ms} ms", order.Count, TypeCount, watch.ElapsedMilliseconds);
        Changed?.Invoke();
    }

    async Task ReindexDirtyAsync()
    {
        var solution = workspace.CurrentSolution;
        if (solution is null) return;
        foreach (var projectId in dirty.Keys.ToList())
        {
            dirty.TryRemove(projectId, out _);
            if (solution.GetProject(projectId) is not { } project) continue;
            try
            {
                var types = new Dictionary<ProjectId, List<SourceType>>();
                var index = new Dictionary<string, Dictionary<ProjectId, List<SourceType>>>(StringComparer.Ordinal);
                await IndexProjectAsync(project, types, index, CancellationToken.None).ConfigureAwait(false);
                lock (gate)
                {
                    // Replace this project's entries; other projects stay.
                    var nextDerived = new Dictionary<string, Dictionary<ProjectId, List<SourceType>>>(derived.Count, StringComparer.Ordinal);
                    foreach (var (key, byProject) in derived)
                    {
                        var copy = byProject.Where(p => p.Key != projectId).ToDictionary(p => p.Key, p => p.Value);
                        if (copy.Count > 0) nextDerived[key] = copy;
                    }
                    foreach (var (key, byProject) in index)
                        (nextDerived.TryGetValue(key, out var existing) ? existing : nextDerived[key] = [])[projectId] = byProject[projectId];
                    derived = nextDerived;
                    typesByProject = new Dictionary<ProjectId, List<SourceType>>(typesByProject) { [projectId] = types.GetValueOrDefault(projectId) ?? [] };
                    TypeCount = typesByProject.Values.Sum(t => t.Count);
                }
                Changed?.Invoke();
            }
            catch (Exception e) { logger.LogDebug(e, "Re-index of {Project} failed", project.Name); }
        }
    }

    static async Task IndexProjectAsync(Project project, Dictionary<ProjectId, List<SourceType>> types,
        Dictionary<string, Dictionary<ProjectId, List<SourceType>>> index, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
        if (compilation is null) return;
        var list = types[project.Id] = [];
        foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
        {
            ct.ThrowIfCancellationRequested();
            if (Id(type) is not { } id) continue;
            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            var span = location?.GetLineSpan();
            var info = new SourceType(type.Name, type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), id, type.TypeKind,
                type.IsAbstract, type.IsStatic, project.Name, span?.Path, span?.StartLinePosition.Line ?? 0);
            list.Add(info);
            var bases = new List<INamedTypeSymbol>();
            for (var b = type.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType) bases.Add(b);
            bases.AddRange(type.AllInterfaces);
            foreach (var b in bases)
                if (Id(b.OriginalDefinition) is { } baseId)
                {
                    if (!index.TryGetValue(baseId, out var byProject)) index[baseId] = byProject = [];
                    (byProject.TryGetValue(project.Id, out var l) ? l : byProject[project.Id] = []).Add(info);
                }
        }
    }

    static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
                foreach (var t in AllTypes(child)) yield return t;
            else if (member is INamedTypeSymbol type)
                foreach (var t in WithNested(type)) yield return t;
        }
    }

    static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        if (!type.IsImplicitlyDeclared) yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var t in WithNested(nested)) yield return t;
    }

    static string? Id(ISymbol symbol) => symbol.GetDocumentationCommentId();

    public void Dispose()
    {
        runCts?.Cancel();
        debounce?.Dispose();
        workspace.StateChanged -= OnWorkspaceState;
        workspace.DocumentChanged -= OnDocumentChanged;
    }
}
