using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LightweightIDE.Indexing.Persistence;

/// <summary>
/// SQLite database holding the project index. One database per opened folder, stored under ~/.cache.
/// Everything here can be rebuilt from the source tree: deleting the file is always safe.
/// </summary>
public sealed class IndexDatabase : IDisposable
{
    public const int SchemaVersion = 1;

    readonly SqliteConnection connection;
    public string FilePath { get; }

    public IndexDatabase(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = filePath, Pooling = false }.ToString());
        connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-8000;");
        EnsureSchema();
    }

    public static string DefaultPathFor(string rootFolder)
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x
            ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(rootFolder))))[..16];
        var name = new string(Path.GetFileName(Path.GetFullPath(rootFolder).TrimEnd('/')).Where(char.IsLetterOrDigit).ToArray());
        return Path.Combine(cache, "lightweight-ide", "index", $"{name}-{hash}.db");
    }

    public SqliteConnection Connection => connection;

    void EnsureSchema()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version == SchemaVersion) return;
        if (version != 0)
        {
            // Schema changed: the index is a cache, drop it and rebuild.
            Execute("""
                DROP TABLE IF EXISTS content_fts; DROP TABLE IF EXISTS symbols_fts; DROP TABLE IF EXISTS files_fts;
                DROP TABLE IF EXISTS symbols; DROP TABLE IF EXISTS files;
                """);
        }
        Execute($"""
            CREATE TABLE files(
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                rel_path TEXT NOT NULL,
                name TEXT NOT NULL,
                project TEXT,
                mtime INTEGER NOT NULL,
                size INTEGER NOT NULL,
                content_indexed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE symbols(
                id INTEGER PRIMARY KEY,
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                container TEXT,
                line INTEGER NOT NULL,
                col INTEGER NOT NULL);
            CREATE INDEX symbols_file ON symbols(file_id);
            CREATE INDEX symbols_name ON symbols(name COLLATE NOCASE);
            -- trigram tokenizer: substring search ("Contr" finds "UserController")
            CREATE VIRTUAL TABLE files_fts USING fts5(rel_path, content='files', content_rowid='id', tokenize='trigram case_sensitive 0');
            CREATE VIRTUAL TABLE symbols_fts USING fts5(name, container, content='symbols', content_rowid='id', tokenize='trigram case_sensitive 0');
            -- contentless: source text stays on disk; matching rows are re-read to find the exact lines
            CREATE VIRTUAL TABLE content_fts USING fts5(body, content='', contentless_delete=1, tokenize='trigram case_sensitive 0');
            CREATE TRIGGER files_ai AFTER INSERT ON files BEGIN INSERT INTO files_fts(rowid, rel_path) VALUES (new.id, new.rel_path); END;
            CREATE TRIGGER files_ad AFTER DELETE ON files BEGIN
                INSERT INTO files_fts(files_fts, rowid, rel_path) VALUES ('delete', old.id, old.rel_path);
                DELETE FROM content_fts WHERE rowid = old.id;
            END;
            CREATE TRIGGER files_au AFTER UPDATE OF rel_path ON files BEGIN
                INSERT INTO files_fts(files_fts, rowid, rel_path) VALUES ('delete', old.id, old.rel_path);
                INSERT INTO files_fts(rowid, rel_path) VALUES (new.id, new.rel_path);
            END;
            CREATE TRIGGER symbols_ai AFTER INSERT ON symbols BEGIN INSERT INTO symbols_fts(rowid, name, container) VALUES (new.id, new.name, new.container); END;
            CREATE TRIGGER symbols_ad AFTER DELETE ON symbols BEGIN
                INSERT INTO symbols_fts(symbols_fts, rowid, name, container) VALUES ('delete', old.id, old.name, old.container);
            END;
            PRAGMA user_version = {SchemaVersion};
            """);
    }

    public void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        return cmd.ExecuteScalar();
    }

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public void Dispose() => connection.Dispose();
}
