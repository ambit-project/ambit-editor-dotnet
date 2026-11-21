using LightweightIDE.Editor.FileOperations;
using LightweightIDE.Indexing.Persistence;
using Microsoft.Data.Sqlite;

namespace LightweightIDE.Indexing;

public sealed record FileHit(string Path, string RelativePath, string Name, string? Project, int Score);

public sealed record SymbolHit(string Name, string Kind, string? Container, string Path, string RelativePath, int Line, int Column, int Score);

public sealed record TextHit(string Path, string RelativePath, int Line, int Column, string LineText);

public sealed record TextSearchOptions(bool MatchCase = false, bool WholeWord = false, int MaxResults = 2000);

/// <summary>
/// Read-only queries over the index. Each query opens its own connection (WAL lets reads run while the indexer
/// writes) and is safe to call from any background thread.
/// </summary>
public sealed class SearchService(string databasePath, string root)
{
    public const int MinTrigramLength = 3;

    SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        return c;
    }

    /// <summary>FTS5 phrase literal: the whole query is one quoted string, so user input never becomes FTS syntax.</summary>
    static string Phrase(string q) => "\"" + q.Replace("\"", "\"\"") + "\"";

    public IReadOnlyList<FileHit> QuickOpen(string query, int limit = 100, CancellationToken ct = default)
    {
        query = query.Trim();
        using var c = Open();
        var hits = new Dictionary<string, FileHit>(StringComparer.Ordinal);
        if (query.Length == 0)
        {
            Read("SELECT path, rel_path, name, project FROM files ORDER BY length(rel_path) LIMIT $l", ("$l", limit));
            return hits.Values.ToList();
        }
        if (query.Length >= MinTrigramLength)
            Read("SELECT f.path, f.rel_path, f.name, f.project FROM files_fts JOIN files f ON f.id = files_fts.rowid WHERE files_fts MATCH $q LIMIT 2000",
                ("$q", Phrase(query)));
        else
            Read("SELECT path, rel_path, name, project FROM files WHERE name LIKE $q ESCAPE '\\' LIMIT 2000",
                ("$q", FileIndexer.EscapeLike(query) + "%"));

        if (hits.Count < limit)
        {
            // Fuzzy fallback ("usrctl" -> UserController.cs): subsequence over all names.
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path, rel_path, name, project FROM files";
            using var r = cmd.ExecuteReader();
            while (r.Read() && hits.Count < 5000)
            {
                ct.ThrowIfCancellationRequested();
                var name = r.GetString(2);
                if (FuzzyMatcher.Score(query, name) is { } s && !hits.ContainsKey(r.GetString(0)))
                    hits[r.GetString(0)] = new FileHit(r.GetString(0), r.GetString(1), name, r.IsDBNull(3) ? null : r.GetString(3), s);
            }
        }
        return hits.Values.OrderByDescending(h => h.Score).ThenBy(h => h.RelativePath.Length).ThenBy(h => h.RelativePath, StringComparer.Ordinal).Take(limit).ToList();

        void Read(string sql, params (string, object)[] ps)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(2);
                var score = FuzzyMatcher.Score(query, name) ?? (FuzzyMatcher.Score(query, r.GetString(1)) ?? 2) / 2;
                if (query.Length == 0) score = 0;
                hits[r.GetString(0)] = new FileHit(r.GetString(0), r.GetString(1), name, r.IsDBNull(3) ? null : r.GetString(3), score);
            }
        }
    }

    /// <summary>Symbol search. "Order" finds OrderService; "Order.Add" finds Add inside a container matching Order.</summary>
    public IReadOnlyList<SymbolHit> Symbols(string query, int limit = 200, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        string? containerFilter = null;
        var name = query;
        var dot = query.LastIndexOf('.');
        if (dot > 0 && dot < query.Length - 1)
        {
            containerFilter = query[..dot];
            name = query[(dot + 1)..];
        }

        using var c = Open();
        using var cmd = c.CreateCommand();
        const string select = "SELECT s.name, s.kind, s.container, f.path, f.rel_path, s.line, s.col FROM ";
        if (name.Length >= MinTrigramLength)
        {
            cmd.CommandText = select + "symbols_fts JOIN symbols s ON s.id = symbols_fts.rowid JOIN files f ON f.id = s.file_id WHERE symbols_fts MATCH $q LIMIT 5000";
            cmd.Parameters.AddWithValue("$q", "name : " + Phrase(name));
        }
        else
        {
            cmd.CommandText = select + "symbols s JOIN files f ON f.id = s.file_id WHERE s.name LIKE $q ESCAPE '\\' LIMIT 5000";
            cmd.Parameters.AddWithValue("$q", FileIndexer.EscapeLike(name) + "%");
        }
        var hits = new List<SymbolHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var symbol = r.GetString(0);
            var container = r.IsDBNull(2) ? null : r.GetString(2);
            if (containerFilter is not null && (container is null || !container.Contains(containerFilter, StringComparison.OrdinalIgnoreCase))) continue;
            var kind = r.GetString(1);
            var score = symbol.Equals(name, StringComparison.Ordinal) ? 1000
                : symbol.Equals(name, StringComparison.OrdinalIgnoreCase) ? 900
                : symbol.StartsWith(name, StringComparison.Ordinal) ? 800
                : symbol.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? 700 : 500;
            if (kind is "class" or "interface" or "struct" or "record" or "record struct" or "enum" or "union") score += 50;
            hits.Add(new SymbolHit(symbol, kind, container, r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), score));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Name.Length).ThenBy(h => h.RelativePath, StringComparer.Ordinal).Take(limit).ToList();
    }

    /// <summary>
    /// Full-text search: the trigram index narrows the candidate files, then each candidate is read from disk to find
    /// exact line matches (so results always reflect the current file content).
    /// </summary>
    public IReadOnlyList<TextHit> FullText(string query, TextSearchOptions options, CancellationToken ct = default)
    {
        if (query.Length < MinTrigramLength) return [];
        var candidates = new List<(string Path, string Rel)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT f.path, f.rel_path FROM content_fts JOIN files f ON f.id = content_fts.rowid WHERE content_fts MATCH $q "
                + "ORDER BY CASE WHEN f.rel_path LIKE '%.cs' THEN 0 WHEN f.rel_path LIKE '%.md' THEN 2 ELSE 1 END, f.rel_path";
            cmd.Parameters.AddWithValue("$q", Phrase(query));
            using var r = cmd.ExecuteReader();
            while (r.Read()) candidates.Add((r.GetString(0), r.GetString(1)));
        }

        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hits = new List<TextHit>();
        foreach (var (path, rel) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = TextFile.Read(path).Text; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var lineNumber = 0;
            foreach (var line in text.AsSpan().EnumerateLines())
            {
                lineNumber++;
                var start = 0;
                while (start <= line.Length)
                {
                    var idx = line[start..].IndexOf(query.AsSpan(), comparison);
                    if (idx < 0) break;
                    idx += start;
                    if (!options.WholeWord || IsWholeWord(line, idx, query.Length))
                    {
                        hits.Add(new TextHit(path, rel, lineNumber, idx + 1, line.ToString().Trim()));
                        if (hits.Count >= options.MaxResults) return hits;
                        break; // one hit per line is enough for the list
                    }
                    start = idx + 1;
                }
            }
        }
        return hits;
    }

    static bool IsWholeWord(ReadOnlySpan<char> line, int index, int length)
    {
        static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        return (index == 0 || !IsWordChar(line[index - 1])) && (index + length >= line.Length || !IsWordChar(line[index + length]));
    }

    public (long Files, long Symbols) Stats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT count(*) FROM files), (SELECT count(*) FROM symbols)";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    public string Root => root;
}
