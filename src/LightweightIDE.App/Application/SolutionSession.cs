using LightweightIDE.DotNet.Build;
using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.DotNet.SolutionDiscovery;
using LightweightIDE.Indexing;
using LightweightIDE.Indexing.Persistence;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.App.Application;

/// <summary>Everything that belongs to the currently opened folder: solution model, index, Roslyn workspace.</summary>
public sealed class SolutionSession(ILogger<SolutionSession> logger, ILoggerFactory loggerFactory, IndexOptions indexOptions,
    LanguageOptions languageOptions, WorkspaceService workspace) : IDisposable
{
    CancellationTokenSource? indexCts;

    public WorkspaceRoot? Root { get; private set; }
    public IndexDatabase? Database { get; private set; }
    public FileIndexer? Indexer { get; private set; }
    public SearchService? Search { get; private set; }
    public ProjectLocator? Projects { get; private set; }
    public string? SdkVersion { get; private set; }
    public WorkspaceService Workspace => workspace;

    public string? Folder => Root?.Folder;
    public bool IsOpen => Root is not null;

    /// <summary>Raised on a background thread.</summary>
    public event Action<IndexProgress>? IndexProgress;
    public event Action? IndexUpdated;
    public event Action<IReadOnlyList<string>>? PathsChanged;

    public async Task OpenAsync(string folder, string? preferredSolution, CancellationToken ct)
    {
        Close();
        var root = await SolutionLoader.DiscoverAsync(folder, preferredSolution, ct);
        Root = root;
        Projects = new ProjectLocator(root.Folder);
        logger.LogInformation("Opened {Folder}: solution {Solution}, {Loose} loose projects", folder, root.Solution?.Path, root.LooseProjects.Count);

        var dbPath = IndexDatabase.DefaultPathFor(root.Folder);
        try
        {
            Database = new IndexDatabase(dbPath);
        }
        catch (Exception e)
        {
            // Corrupt cache: delete and rebuild.
            logger.LogWarning(e, "Index database unusable, recreating {Path}", dbPath);
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) File.Delete(f);
            Database = new IndexDatabase(dbPath);
        }
        Search = new SearchService(dbPath, root.Folder);
        Indexer = new FileIndexer(Database, root.Folder, indexOptions, loggerFactory.CreateLogger<FileIndexer>());
        Indexer.Progress += p => IndexProgress?.Invoke(p);
        Indexer.Updated += () => IndexUpdated?.Invoke();
        Indexer.PathsChanged += paths =>
        {
            foreach (var path in paths) workspace.RefreshFromDisk(path);
            PathsChanged?.Invoke(paths);
        };
        Indexer.StartWatching();
        _ = ReindexAsync(force: false);

        _ = Task.Run(async () => SdkVersion = await SdkInfo.ResolveVersionAsync(root.Folder, CancellationToken.None), CancellationToken.None);
        if (languageOptions.LoadWorkspaceOnOpen) _ = workspace.LoadAsync(root);
    }

    public async Task ReindexAsync(bool force)
    {
        if (Indexer is null) return;
        indexCts?.Cancel();
        indexCts = new CancellationTokenSource();
        try { await Indexer.SynchronizeAsync(force, indexCts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e) { logger.LogError(e, "Indexing failed"); }
    }

    public void EnsureWorkspaceLoaded()
    {
        if (Root is not null && workspace.State is WorkspaceState.NotLoaded or WorkspaceState.Failed) _ = workspace.LoadAsync(Root);
    }

    /// <summary>The solution file changed (project added): read it again and reload the Roslyn workspace.</summary>
    public async Task RefreshSolutionAsync(CancellationToken ct = default)
    {
        if (Root is not { } current) return;
        var root = await SolutionLoader.DiscoverAsync(current.Folder, current.Solution?.Path, ct);
        Root = root;
        Projects = new ProjectLocator(root.Folder);
        _ = workspace.ReloadAsync(root);
    }

    /// <summary>global.json changed: resolve the SDK again and reload projects (the build host follows the SDK).</summary>
    public async Task RefreshSdkAsync(CancellationToken ct = default)
    {
        if (Root is not { } root) return;
        SdkVersion = await SdkInfo.ResolveVersionAsync(root.Folder, ct);
        _ = workspace.ReloadAsync(root);
    }

    public Task ReloadProjectsAsync() => Root is null ? Task.CompletedTask : workspace.ReloadAsync(Root);

    public string? ProjectFileFor(string? path) => path is null ? null : Projects?.FindProjectFile(path);

    public void Close()
    {
        indexCts?.Cancel();
        Indexer?.Dispose();
        Database?.Dispose();
        workspace.Unload();
        Indexer = null;
        Database = null;
        Search = null;
        Root = null;
        Projects = null;
        SdkVersion = null;
    }

    public void Dispose() => Close();
}
