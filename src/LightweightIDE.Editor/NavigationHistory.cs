namespace LightweightIDE.Editor;

/// <summary>A caret position in a file. Line and column are 1-based.</summary>
public sealed record NavigationPoint(string Path, int Line, int Column);

/// <summary>
/// Back / forward history of jumps (go to definition, search results, problems, go to line). Only jumps are
/// recorded, not every caret move: <see cref="Record"/> is called with the position before and after a jump.
/// </summary>
public sealed class NavigationHistory(int capacity = 100)
{
    readonly LinkedList<NavigationPoint> back = new();
    readonly Stack<NavigationPoint> forward = new();

    public bool CanGoBack => back.Count > 0;
    public bool CanGoForward => forward.Count > 0;
    public IEnumerable<NavigationPoint> BackEntries => back;

    /// <summary>Records a jump. Moves within the same line are ignored; a new jump clears the forward history.</summary>
    public void Record(NavigationPoint from, NavigationPoint to)
    {
        if (from.Path == to.Path && from.Line == to.Line) return;
        Push(from);
        forward.Clear();
    }

    /// <summary>Returns the previous position and remembers <paramref name="current"/> for <see cref="GoForward"/>.</summary>
    public NavigationPoint? GoBack(NavigationPoint? current)
    {
        if (back.Last?.Value is not { } last) return null;
        back.RemoveLast();
        if (current is not null && current != last) forward.Push(current);
        return last;
    }

    public NavigationPoint? GoForward(NavigationPoint? current)
    {
        if (!forward.TryPop(out var next)) return null;
        if (current is not null && current != next) Push(current);
        return next;
    }

    /// <summary>A file or folder was renamed: entries follow it.</summary>
    public void Rename(string oldPath, string newPath)
    {
        string Map(string p) =>
            p == oldPath ? newPath
            : p.StartsWith(oldPath.TrimEnd('/') + "/", StringComparison.Ordinal) ? newPath.TrimEnd('/') + p[oldPath.TrimEnd('/').Length..]
            : p;
        Replace(e => e with { Path = Map(e.Path) });
    }

    /// <summary>A file or folder was deleted: its entries are dropped.</summary>
    public void Remove(string path)
    {
        var prefix = path.TrimEnd('/') + "/";
        Replace(e => e.Path == path || e.Path.StartsWith(prefix, StringComparison.Ordinal) ? null : e);
    }

    void Push(NavigationPoint point)
    {
        if (back.Last?.Value == point) return;
        back.AddLast(point);
        while (back.Count > capacity) back.RemoveFirst();
    }

    void Replace(Func<NavigationPoint, NavigationPoint?> map)
    {
        var backItems = back.Select(map).OfType<NavigationPoint>().ToList();
        back.Clear();
        foreach (var e in backItems) back.AddLast(e);
        var forwardItems = forward.Reverse().Select(map).OfType<NavigationPoint>().ToList();
        forward.Clear();
        foreach (var e in forwardItems) forward.Push(e);
    }
}
