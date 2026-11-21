using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightweightIDE.Editor;

/// <summary>An unsaved editor buffer kept on disk so a crash or a hard reset does not lose it.</summary>
public sealed record UnsavedBackup(string Path, string Text, DateTime SavedAtUtc, DateTime? DiskLastWriteUtc);

/// <summary>
/// Backups of dirty buffers, one JSON file per document (named by a hash of its path). Written atomically while
/// the user types, deleted when the file is saved, closed without saving, or the IDE exits normally — so whatever
/// is left at startup is from a crash and is offered for recovery.
/// </summary>
public sealed class UnsavedBackupStore(string directory)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string Directory => directory;

    string FileFor(string path) =>
        System.IO.Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..20] + ".json");

    public void Save(UnsavedBackup backup)
    {
        System.IO.Directory.CreateDirectory(directory);
        var file = FileFor(backup.Path);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(backup, Json));
        File.Move(temp, file, overwrite: true);
    }

    public void Delete(string path)
    {
        try { File.Delete(FileFor(path)); }
        catch (IOException) { }
    }

    public UnsavedBackup? Load(string path) => Read(FileFor(path));

    /// <summary>All backups (optionally only files under <paramref name="folder"/>), newest first.</summary>
    public IReadOnlyList<UnsavedBackup> List(string? folder = null)
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        var prefix = folder?.TrimEnd('/') + "/";
        return System.IO.Directory.EnumerateFiles(directory, "*.json")
            .Select(Read)
            .OfType<UnsavedBackup>()
            .Where(b => folder is null || b.Path.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(b => b.SavedAtUtc)
            .ToList();
    }

    public void DeleteAll()
    {
        if (!System.IO.Directory.Exists(directory)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }

    static UnsavedBackup? Read(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<UnsavedBackup>(File.ReadAllText(file), Json) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
