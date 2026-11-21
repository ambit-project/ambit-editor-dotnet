namespace LightweightIDE.Language.Formatting;

/// <summary>A replacement in UTF-16 offsets of the original text.</summary>
public sealed record TextReplacement(int Start, int Length, string NewText);

/// <summary>
/// Line diff (Myers) narrowed to characters inside each changed block. Applying these small edits instead of
/// replacing the whole buffer keeps the caret, the selection and the scroll position where they were.
/// </summary>
public static class TextDiff
{
    const int MaxEditDistance = 4000;

    public static IReadOnlyList<TextReplacement> Compute(string oldText, string newText)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return [];
        var a = SplitLines(oldText);
        var b = SplitLines(newText);

        // Common prefix / suffix lines make the diff of a typical edit trivial.
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        var oldMiddle = a.GetRange(prefix, a.Count - prefix - suffix);
        var newMiddle = b.GetRange(prefix, b.Count - prefix - suffix);
        var offset = a.Take(prefix).Sum(l => l.Length);

        var result = new List<TextReplacement>();
        var hunks = Myers(oldMiddle, newMiddle)
            ?? [(0, oldMiddle.Count, 0, newMiddle.Count)]; // too different: one block
        var oldLine = 0;
        foreach (var (oldStart, oldCount, newStart, newCount) in hunks)
        {
            offset += oldMiddle.Skip(oldLine).Take(oldStart - oldLine).Sum(l => l.Length);
            var removed = string.Concat(oldMiddle.Skip(oldStart).Take(oldCount));
            var inserted = string.Concat(newMiddle.Skip(newStart).Take(newCount));
            result.Add(Narrow(offset, removed, inserted));
            offset += removed.Length;
            oldLine = oldStart + oldCount;
        }
        return result;
    }

    /// <summary>Applies replacements (non-overlapping, relative to <paramref name="text"/>).</summary>
    public static string Apply(string text, IReadOnlyList<TextReplacement> replacements)
    {
        var builder = new System.Text.StringBuilder(text);
        foreach (var r in replacements.OrderByDescending(r => r.Start)) builder.Remove(r.Start, r.Length).Insert(r.Start, r.NewText);
        return builder.ToString();
    }

    /// <summary>Trims characters both sides share, so only the real change is replaced.</summary>
    static TextReplacement Narrow(int offset, string removed, string inserted)
    {
        var p = 0;
        while (p < removed.Length && p < inserted.Length && removed[p] == inserted[p]) p++;
        var s = 0;
        while (s < removed.Length - p && s < inserted.Length - p && removed[^(s + 1)] == inserted[^(s + 1)]) s++;
        // Never split a surrogate pair or a CRLF.
        while (p > 0 && (char.IsHighSurrogate(removed[p - 1]) || removed[p - 1] == '\r')) p--;
        while (s > 0 && (char.IsLowSurrogate(removed[^s]) || removed[^s] == '\n' && removed.Length - s - 1 >= p && removed[^(s + 1)] == '\r')) s--;
        return new TextReplacement(offset + p, removed.Length - p - s, inserted.Substring(p, inserted.Length - p - s));
    }

    /// <summary>Lines including their terminators, so offsets add up to the text length.</summary>
    static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>Myers O((N+M)·D) diff; returns changed blocks (oldStart, oldCount, newStart, newCount) or null when D is too large.</summary>
    static List<(int OldStart, int OldCount, int NewStart, int NewCount)>? Myers(List<string> a, List<string> b)
    {
        int n = a.Count, m = b.Count;
        var trace = new List<int[]>();
        int[] previous = [];
        var found = -1;
        for (var d = 0; d <= Math.Min(n + m, MaxEditDistance) && found < 0; d++)
        {
            var v = new int[2 * d + 1];
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (d == 0) x = 0;
                else if (k == -d || (k != d && previous[k - 1 + d - 1] < previous[k + 1 + d - 1])) x = previous[k + 1 + d - 1];
                else x = previous[k - 1 + d - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[k + d] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
            trace.Add(v);
            previous = v;
        }
        if (found < 0) return null;

        // Walk back from (n, m) collecting single-line edits, then merge adjacent ones into blocks.
        var deletes = new List<int>();          // old line indexes
        var inserts = new List<(int At, int New)>(); // inserted new line index, at old position
        int cx = n, cy = m;
        for (var d = found; d > 0; d--)
        {
            var prev = trace[d - 1];
            var k = cx - cy;
            var down = k == -d || (k != d && prev[k - 1 + d - 1] < prev[k + 1 + d - 1]);
            var prevK = down ? k + 1 : k - 1;
            var prevX = prev[prevK + d - 1];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY && (down ? cy - 1 > prevY : cx - 1 > prevX)) { cx--; cy--; }
            if (down) inserts.Add((prevX, prevY));
            else deletes.Add(prevX);
            cx = prevX;
            cy = prevY;
        }

        var deleted = deletes.ToHashSet();
        var insertedAt = inserts.GroupBy(i => i.At).ToDictionary(g => g.Key, g => g.Select(i => i.New).Order().ToList());
        var blocks = new List<(int, int, int, int)>();
        int oi = 0, ni = 0;
        while (oi <= n)
        {
            var hasInsert = insertedAt.TryGetValue(oi, out var newLines);
            if (!hasInsert && (oi == n || !deleted.Contains(oi)))
            {
                if (oi == n) break;
                oi++;
                ni++;
                continue;
            }
            // A block: inserts at this position and/or a run of deleted lines (with inserts between them).
            int oldStart = oi, newStart = ni, oldCount = 0, newCount = 0;
            while (true)
            {
                if (insertedAt.TryGetValue(oi, out newLines)) { newCount += newLines.Count; ni += newLines.Count; insertedAt.Remove(oi); }
                if (oi < n && deleted.Contains(oi)) { oldCount++; oi++; continue; }
                break;
            }
            blocks.Add((oldStart, oldCount, newStart, newCount));
        }
        return blocks;
    }
}
