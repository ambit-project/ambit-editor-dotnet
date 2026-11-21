namespace LightweightIDE.Indexing;

/// <summary>Scores a query against a candidate: exact > prefix > substring > camel-case/subsequence. Null = no match.</summary>
public static class FuzzyMatcher
{
    public static int? Score(string query, string candidate)
    {
        if (query.Length == 0) return 0;
        if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        var stem = Path.GetFileNameWithoutExtension(candidate);
        if (stem.Equals(query, StringComparison.OrdinalIgnoreCase)) return 950;
        if (candidate.StartsWith(query, StringComparison.Ordinal)) return 900;
        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 850;
        var idx = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return 700 - Math.Min(idx, 100);

        // Subsequence; bonus for matches at word starts (upper case after lower, after '.', '_' or '/').
        var qi = 0;
        var score = 300;
        var previousMatch = -2;
        for (var ci = 0; ci < candidate.Length && qi < query.Length; ci++)
        {
            if (char.ToLowerInvariant(candidate[ci]) != char.ToLowerInvariant(query[qi])) continue;
            var boundary = ci == 0 || char.IsUpper(candidate[ci]) && char.IsLower(candidate[ci - 1]) || candidate[ci - 1] is '.' or '_' or '/' or '-';
            if (boundary) score += 10;
            if (previousMatch == ci - 1) score += 5;
            previousMatch = ci;
            qi++;
        }
        return qi == query.Length ? score - candidate.Length / 4 : null;
    }
}
