using System.Collections.Concurrent;

namespace LightweightIDE.Language.Completion;

/// <summary>What the user picked before: picked items rise in later lists (per display text, this session).</summary>
public sealed class CompletionUsage
{
    readonly ConcurrentDictionary<string, (int Count, long Last)> picks = new(StringComparer.Ordinal);
    long clock;

    public void Record(string displayText) =>
        picks.AddOrUpdate(displayText, _ => (1, Interlocked.Increment(ref clock)), (_, p) => (p.Count + 1, Interlocked.Increment(ref clock)));

    /// <summary>0..500: how often and how recently the item was picked.</summary>
    public int Boost(string displayText)
    {
        if (!picks.TryGetValue(displayText, out var p)) return 0;
        var recency = (int)Math.Max(0, 200 - (Volatile.Read(ref clock) - p.Last) * 10);
        return Math.Min(300, p.Count * 60) + recency;
    }
}

/// <summary>
/// Orders completion items the way one expects in an IDE: the match with the typed text first (exact, prefix,
/// CamelCase humps, word part, substring, fuzzy), then relevance: implementations that satisfy a generic constraint
/// or the expected type, items Roslyn preselects, locals and parameters, members, types in scope, keywords, and last
/// the types from namespaces that are not imported yet. Items picked before rise.
/// </summary>
public static class CompletionRanker
{
    public static IReadOnlyList<CompletionEntry> Rank(IReadOnlyList<CompletionEntry> items, string typed, CompletionUsage? usage)
    {
        var ranked = items
            .Select(item => (Item: item, Match: typed.Length == 0 ? 1 : Match(item.FilterText, typed)))
            .Where(x => x.Match > 0)
            // Without typed text, unimported types only clutter the list. Unimported extension members stay: they
            // apply to the receiver (builder.Services. → AddServices from another namespace or a package).
            .Where(x => typed.Length > 0 || x.Item.InScope || x.Item.Smart is not null || !IsType(x.Item.Kind))
            .Select(x => (x.Item, x.Match, Relevance: Relevance(x.Item, usage)))
            .OrderByDescending(x => x.Match)
            .ThenByDescending(x => x.Relevance)
            // Roslyn prefixes unimported items with '~' to put them last; here the relevance above decides that.
            .ThenBy(x => (x.Item.SortText ?? x.Item.FilterText).TrimStart('~'), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Item.DisplayText.Length);
        return ranked.Select(x => x.Item).ToList();
    }

    /// <summary>How well the typed text matches; 0 = not at all. Higher buckets always win over relevance.</summary>
    public static int Match(string candidate, string typed)
    {
        if (typed.Length == 0) return 1;
        if (candidate == typed) return 9;
        if (candidate.StartsWith(typed, StringComparison.Ordinal)) return 8;
        if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 7;
        if (CamelHumps(candidate, typed)) return 6;
        if (WordPartStart(candidate, typed)) return 5;
        if (candidate.Contains(typed, StringComparison.OrdinalIgnoreCase)) return 4;
        if (typed.Length >= 3 && Subsequence(candidate, typed)) return 2;
        return 0;
    }

    static int Relevance(CompletionEntry e, CompletionUsage? usage)
    {
        var score = 0;
        if (e.Smart is not null) score += 2000;
        if (e.TargetMatch) score += 1500;
        // Roslyn's MatchPriority: Preselect is huge, locals in an argument 600, `new` types 200, keywords -1.
        score += e.Priority switch
        {
            >= 1_000_000 => 1200,
            > 0 => Math.Min(e.Priority, 800),
            < 0 => -100,
            _ => 0,
        };
        score += e.Kind switch
        {
            "Local" or "Parameter" or "RangeVariable" => 500,
            "Field" or "Property" or "Method" or "Event" or "EnumMember" or "Constant" or "ExtensionMethod" => 300,
            "Class" or "Record" or "Structure" or "RecordStruct" or "Interface" or "Enum" or "Delegate" => 200,
            "TypeParameter" => 250,
            "Namespace" => 100,
            "Keyword" => 50,
            _ => 0,
        };
        if (!e.InScope && IsType(e.Kind)) score -= 1000; // needs a using; extension members rank with the other members
        // Inherited from System.Object on every type: below the members of the type itself.
        if (e.Kind == "Method" && e.FilterText is "Equals" or "GetHashCode" or "GetType" or "ToString" or "MemberwiseClone" or "ReferenceEquals") score -= 150;
        if (usage is not null) score += usage.Boost(e.DisplayText);
        return score;
    }

    static bool IsType(string kind) => kind is "Class" or "Record" or "Structure" or "RecordStruct" or "Interface" or "Enum" or "Delegate";

    /// <summary>"SB" → StringBuilder, "gAs" → GetAllAsync: each typed char starts a hump, in order (lowercase allowed).</summary>
    static bool CamelHumps(string candidate, string typed)
    {
        var humps = new List<int>();
        for (var i = 0; i < candidate.Length; i++)
            if (i == 0 || char.IsUpper(candidate[i]) && !char.IsUpper(candidate[i - 1]) || candidate[i - 1] is '_' or '.' || char.IsDigit(candidate[i]) && !char.IsDigit(candidate[i - 1]))
                humps.Add(i);
        // Each typed character must be the start of a hump or continue the current hump.
        var t = 0;
        var position = 0;
        foreach (var start in humps)
        {
            if (t >= typed.Length) break;
            if (start < position) continue;
            if (char.ToUpperInvariant(candidate[start]) != char.ToUpperInvariant(typed[t])) continue;
            position = start;
            while (t < typed.Length && position < candidate.Length && char.ToUpperInvariant(candidate[position]) == char.ToUpperInvariant(typed[t])
                   && (position == start || !humps.Contains(position)))
            {
                t++;
                position++;
                if (t < typed.Length && humps.Contains(position)) break;
            }
        }
        return t == typed.Length;
    }

    /// <summary>"Builder" matches StringBuilder at a word start.</summary>
    static bool WordPartStart(string candidate, string typed)
    {
        for (var i = 1; i < candidate.Length; i++)
            if ((char.IsUpper(candidate[i]) || candidate[i - 1] is '_' or '.') && string.Compare(candidate, i, typed, 0, typed.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
        return false;
    }

    static bool Subsequence(string candidate, string typed)
    {
        var t = 0;
        foreach (var c in candidate)
            if (t < typed.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(typed[t])) t++;
        return t == typed.Length;
    }
}
