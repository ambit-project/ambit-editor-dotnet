namespace LightweightIDE.Editor;

/// <summary>Line breakpoints per file (1-based lines). The window persists them and sends them to the debugger.</summary>
public sealed class BreakpointStore
{
    readonly Dictionary<string, SortedSet<int>> files = new(StringComparer.Ordinal);
    /// <summary>Non-default options (condition, hit count, log message) per file and line.</summary>
    readonly Dictionary<string, Dictionary<int, BreakpointOptions>> options = new(StringComparer.Ordinal);

    /// <summary>Raised with the file whose breakpoints changed.</summary>
    public event Action<string>? Changed;

    public IReadOnlyList<int> For(string path) => files.TryGetValue(path, out var lines) ? lines.ToList() : [];

    public IReadOnlyDictionary<string, IReadOnlyList<int>> All() =>
        files.Where(f => f.Value.Count > 0).ToDictionary(f => f.Key, f => (IReadOnlyList<int>)f.Value.ToList(), StringComparer.Ordinal);

    public int Count => files.Values.Sum(l => l.Count);

    public BreakpointOptions OptionsFor(string path, int line) =>
        options.TryGetValue(path, out var lines) && lines.TryGetValue(line, out var o) ? o : BreakpointOptions.Default;

    /// <summary>Lines of a file with non-default options.</summary>
    public IReadOnlyDictionary<int, BreakpointOptions> OptionsFor(string path) =>
        options.TryGetValue(path, out var lines) ? lines : new Dictionary<int, BreakpointOptions>();

    /// <summary>Sets the options of a breakpoint, adding the breakpoint when the line has none.</summary>
    public void SetOptions(string path, int line, BreakpointOptions value)
    {
        if (!files.TryGetValue(path, out var lines)) files[path] = lines = [];
        lines.Add(line);
        if (value.IsDefault) RemoveOptions(path, line);
        else
        {
            if (!options.TryGetValue(path, out var byLine)) options[path] = byLine = [];
            byLine[line] = value;
        }
        Changed?.Invoke(path);
    }

    void RemoveOptions(string path, int line)
    {
        if (!options.TryGetValue(path, out var byLine)) return;
        byLine.Remove(line);
        if (byLine.Count == 0) options.Remove(path);
    }

    /// <summary>Adds or removes a breakpoint; returns true when the line now has one.</summary>
    public bool Toggle(string path, int line)
    {
        if (!files.TryGetValue(path, out var lines)) files[path] = lines = [];
        var added = lines.Add(line);
        if (!added)
        {
            lines.Remove(line);
            RemoveOptions(path, line);
        }
        Changed?.Invoke(path);
        return added;
    }

    /// <summary>Replaces a file's breakpoints (e.g. the editor moved them with edits). Raises Changed only on a change.</summary>
    public void Set(string path, IEnumerable<int> lines)
    {
        var next = new SortedSet<int>(lines.Where(l => l > 0));
        if (files.TryGetValue(path, out var current) && current.SetEquals(next)) return;
        if (next.Count == 0 && current is null) return;
        // Edits moved the breakpoints: options follow them in order when the count is the same, otherwise only
        // the lines that still have a breakpoint keep theirs.
        if (options.TryGetValue(path, out var byLine) && current is not null)
        {
            var moved = new Dictionary<int, BreakpointOptions>();
            if (current.Count == next.Count)
            {
                foreach (var (old, @new) in current.Zip(next))
                    if (byLine.TryGetValue(old, out var o)) moved[@new] = o;
            }
            else
            {
                foreach (var (line, o) in byLine) if (next.Contains(line)) moved[line] = o;
            }
            if (moved.Count > 0) options[path] = moved;
            else options.Remove(path);
        }
        files[path] = next;
        Changed?.Invoke(path);
    }

    /// <summary>Removes the breakpoints of one file; returns how many there were.</summary>
    public int ClearFile(string path)
    {
        options.Remove(path);
        if (!files.Remove(path, out var lines) || lines.Count == 0) return 0;
        Changed?.Invoke(path);
        return lines.Count;
    }

    public void Clear()
    {
        var paths = files.Keys.ToList();
        files.Clear();
        options.Clear();
        foreach (var p in paths) Changed?.Invoke(p);
    }

    /// <summary>A file or folder was renamed: breakpoints follow it.</summary>
    public void Rename(string oldPath, string newPath)
    {
        var prefix = oldPath.TrimEnd('/') + "/";
        foreach (var path in files.Keys.Where(p => p == oldPath || p.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            var target = path == oldPath ? newPath : newPath.TrimEnd('/') + path[oldPath.TrimEnd('/').Length..];
            files[target] = files[path];
            files.Remove(path);
            if (options.Remove(path, out var moved)) options[target] = moved;
            Changed?.Invoke(path);
            Changed?.Invoke(target);
        }
    }

    public Dictionary<string, List<int>> Export() => files.Where(f => f.Value.Count > 0).ToDictionary(f => f.Key, f => f.Value.ToList());

    public void Import(Dictionary<string, List<int>>? saved, Dictionary<string, Dictionary<int, BreakpointOptions>>? savedOptions = null)
    {
        if (saved is null) return;
        foreach (var (path, lines) in saved) files[path] = new SortedSet<int>(lines);
        foreach (var (path, byLine) in savedOptions ?? [])
        {
            if (!files.TryGetValue(path, out var lines)) continue;
            var kept = byLine.Where(o => lines.Contains(o.Key) && !o.Value.IsDefault).ToDictionary(o => o.Key, o => o.Value);
            if (kept.Count > 0) options[path] = kept;
        }
    }

    public Dictionary<string, Dictionary<int, BreakpointOptions>> ExportOptions() =>
        options.ToDictionary(f => f.Key, f => new Dictionary<int, BreakpointOptions>(f.Value), StringComparer.Ordinal);
}
