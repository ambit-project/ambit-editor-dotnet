using System.Collections.Concurrent;
using System.Text;
using LightweightIDE.DotNet.ProjectSystem;
using LightweightIDE.Editor.FileOperations;
using LightweightIDE.Indexing.Persistence;
using Microsoft.Extensions.Logging;

namespace LightweightIDE.Indexing;

public sealed record IndexProgress(string Phase, int Done, int Total);

/// <summary>
/// Keeps the SQLite index in step with the source tree: full incremental scan on open (mtime + size), then
/// filesystem notifications for single files. All work runs on a background thread, one writer at a time.
/// </summary>
public sealed class FileIndexer : IDisposable
{
    readonly IndexDatabase db;
    readonly string root;
    readonly IndexOptions options;
    readonly ILogger logger;
    readonly ProjectLocator projects;
    readonly HashSet<string> contentExtensions;
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);
    FileSystemWatcher? watcher;
    Timer? debounce;

    public event Action<IndexProgress>? Progress;
    public event Action? Updated;
    /// <summary>Raised (on a background thread) with the paths a batch of filesystem events touched.</summary>
    public event Action<IReadOnlyList<string>>? PathsChanged;

    public FileIndexer(IndexDatabase db, string root, IndexOptions options, ILogger logger)
    {
        this.db = db;
        this.root = Path.GetFullPath(root);
        this.options = options;
        this.logger = logger;
        projects = new ProjectLocator(this.root);
        contentExtensions = new HashSet<string>(options.ContentExtensions, StringComparer.OrdinalIgnoreCase);
    }

    public string Root => root;

    /// <summary>Compares the tree with the index and re-indexes only new or changed files; removes deleted ones.</summary>
    public async Task SynchronizeAsync(bool force, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => Synchronize(force, ct), ct).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
        Updated?.Invoke();
    }

    void Synchronize(bool force, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        Progress?.Invoke(new IndexProgress("Scanning", 0, 0));
        projects.Invalidate();

        var known = new Dictionary<string, (long Id, long Mtime, long Size)>(StringComparer.Ordinal);
        using (var cmd = db.Command("SELECT id, path, mtime, size FROM files"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) known[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2), r.GetInt64(3));

        var changed = new List<(string Path, long Mtime, long Size)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in FileTree.EnumerateFiles(root, ct))
        {
            if (IsIgnoredFile(path)) continue;
            FileInfo info = new(path);
            if (!info.Exists) continue;
            seen.Add(path);
            var mtime = info.LastWriteTimeUtc.Ticks;
            if (force || !known.TryGetValue(path, out var k) || k.Mtime != mtime || k.Size != info.Length)
                changed.Add((path, mtime, info.Length));
        }
        var deleted = known.Keys.Where(p => !seen.Contains(p)).ToList();

        using (var tx = db.Connection.BeginTransaction())
        {
            foreach (var path in deleted) db.Execute("DELETE FROM files WHERE path = $p", ("$p", path));
            tx.Commit();
        }

        const int batch = 200;
        for (var i = 0; i < changed.Count; i += batch)
        {
            ct.ThrowIfCancellationRequested();
            using var tx = db.Connection.BeginTransaction();
            foreach (var (path, mtime, size) in changed.Skip(i).Take(batch)) IndexFile(path, mtime, size);
            tx.Commit();
            Progress?.Invoke(new IndexProgress("Indexing", Math.Min(i + batch, changed.Count), changed.Count));
        }
        if (changed.Count > 1000) db.Execute("INSERT INTO content_fts(content_fts) VALUES ('optimize')");
        logger.LogInformation("Index synchronized: {Files} files on disk, {Changed} re-indexed, {Deleted} removed in {Ms} ms",
            seen.Count, changed.Count, deleted.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        Progress?.Invoke(new IndexProgress("Ready", changed.Count, changed.Count));
    }

    /// <summary>Re-indexes or removes one file. Must run inside a transaction and under the write lock.</summary>
    void IndexFile(string path, long mtime, long size)
    {
        var rel = Path.GetRelativePath(root, path);
        var existingId = db.Scalar("SELECT id FROM files WHERE path = $p", ("$p", path));
        long id;
        if (existingId is long eid)
        {
            id = eid;
            db.Execute("DELETE FROM symbols WHERE file_id = $id", ("$id", id));
            db.Execute("DELETE FROM content_fts WHERE rowid = $id", ("$id", id));
            db.Execute("UPDATE files SET mtime = $m, size = $s, project = $proj, content_indexed = 0 WHERE id = $id",
                ("$m", mtime), ("$s", size), ("$proj", projects.FindProjectName(path)), ("$id", id));
        }
        else
        {
            id = (long)db.Scalar("""
                INSERT INTO files(path, rel_path, name, project, mtime, size) VALUES ($p, $r, $n, $proj, $m, $s) RETURNING id
                """, ("$p", path), ("$r", rel), ("$n", Path.GetFileName(path)), ("$proj", projects.FindProjectName(path)), ("$m", mtime), ("$s", size))!;
        }

        if (!contentExtensions.Contains(Path.GetExtension(path)) && !contentExtensions.Contains(Path.GetFileName(path))) return;
        if (size > options.MaxContentFileSizeKb * 1024L) return;

        string text;
        try
        {
            if (TextFile.LooksBinary(path)) return;
            text = TextFile.Read(path).Text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(e, "Cannot read {Path}", path);
            return;
        }

        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (IsGeneratedCode(text)) return;
            foreach (var s in SymbolExtractor.Extract(text))
                db.Execute("INSERT INTO symbols(file_id, name, kind, container, line, col) VALUES ($f, $n, $k, $c, $l, $col)",
                    ("$f", id), ("$n", s.Name), ("$k", s.Kind), ("$c", s.Container), ("$l", s.Line), ("$col", s.Column));
        }
        db.Execute("INSERT INTO content_fts(rowid, body) VALUES ($id, $b)", ("$id", id), ("$b", text));
        db.Execute("UPDATE files SET content_indexed = 1 WHERE id = $id", ("$id", id));
    }

    public static bool IsIgnoredFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') && name is not ".editorconfig" and not ".gitignore") return true;
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith('~')
               || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsGeneratedCode(string text)
    {
        var head = text.AsSpan(0, Math.Min(text.Length, 1024));
        return head.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts inotify-based watching. Changes are batched and applied after a short quiet period.</summary>
    public void StartWatching()
    {
        if (watcher is not null) return;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += (_, e) => Enqueue(e.FullPath);
            watcher.Created += (_, e) => Enqueue(e.FullPath);
            watcher.Deleted += (_, e) => Enqueue(e.FullPath);
            watcher.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
            watcher.Error += (_, e) =>
            {
                // Buffer overflow or inotify limit: fall back to a full incremental scan.
                logger.LogWarning(e.GetException(), "File watcher error, running a full scan");
                _ = SynchronizeAsync(false, CancellationToken.None);
            };
            watcher.EnableRaisingEvents = true;
            debounce = new Timer(_ => _ = FlushPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            // e.g. fs.inotify.max_user_watches reached on a huge tree; manual reindex still works.
            logger.LogWarning(e, "File watching unavailable for {Root}", root);
            watcher?.Dispose();
            watcher = null;
        }
    }

    void Enqueue(string path)
    {
        if (FileTree.IsInExcludedDirectory(root, path) || IsIgnoredFile(path)) return;
        pending[path] = 0;
        debounce?.Change(options.WatcherDebounceMs, Timeout.Infinite);
    }

    async Task FlushPendingAsync()
    {
        var paths = pending.Keys.ToList();
        foreach (var p in paths) pending.TryRemove(p, out _);
        if (paths.Count == 0) return;
        PathsChanged?.Invoke(paths);
        try
        {
            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var tx = db.Connection.BeginTransaction();
                foreach (var path in paths) ApplyChange(path);
                tx.Commit();
            }
            finally { writeLock.Release(); }
            Updated?.Invoke();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Incremental index update failed");
        }
    }

    void ApplyChange(string path)
    {
        if (Directory.Exists(path))
        {
            // New or renamed directory: index its files.
            projects.Invalidate();
            foreach (var f in FileTree.EnumerateFiles(path).Where(f => !IsIgnoredFile(f)))
                IndexFile(f, File.GetLastWriteTimeUtc(f).Ticks, new FileInfo(f).Length);
            return;
        }
        FileInfo info = new(path);
        if (info.Exists)
        {
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) projects.Invalidate();
            IndexFile(path, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        else
        {
            // File or whole directory removed.
            db.Execute("DELETE FROM files WHERE path = $p OR path LIKE $dir ESCAPE '\\'",
                ("$p", path), ("$dir", EscapeLike(path.TrimEnd('/') + "/") + "%"));
        }
    }

    /// <summary>Re-indexes a file right away (used after the editor saves it).</summary>
    public async Task ReindexFileAsync(string path, CancellationToken ct = default)
    {
        if (FileTree.IsInExcludedDirectory(root, path) || IsIgnoredFile(path)) return;
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var tx = db.Connection.BeginTransaction();
                ApplyChange(path);
                tx.Commit();
            }, ct).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
        Updated?.Invoke();
    }

    internal static string EscapeLike(string s) =>
        new StringBuilder(s).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").ToString();

    public void Dispose()
    {
        watcher?.Dispose();
        debounce?.Dispose();
        writeLock.Dispose();
    }
}
