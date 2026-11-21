using System.Text;
using LightweightIDE.Editor.FileOperations;

namespace LightweightIDE.Editor;

public enum DiskState { InSync, ChangedOnDisk, DeletedOnDisk }

/// <summary>
/// State of one open file that does not depend on the widget toolkit: path, encoding, line ending, the text as
/// last saved, and the disk timestamp used to detect external changes.
/// </summary>
public sealed class EditorDocument
{
    public string Path { get; private set; }
    public Encoding Encoding { get; private set; }
    public bool HasBom { get; private set; }
    public LineEnding LineEnding { get; private set; }
    public DateTime DiskLastWriteUtc { get; private set; }
    public long DiskLength { get; private set; }
    public string SavedText { get; private set; }
    /// <summary>Monotonic text version, bumped by the editor on each change. Used to drop stale analysis results.</summary>
    public int Version { get; set; }

    EditorDocument(string path, TextFileContent content)
    {
        Path = path;
        SavedText = content.Text;
        Encoding = content.Encoding;
        HasBom = content.HasBom;
        LineEnding = content.LineEnding;
        DiskLastWriteUtc = content.LastWriteUtc;
        DiskLength = content.Length;
    }

    public static EditorDocument Open(string path) => new(System.IO.Path.GetFullPath(path), TextFile.Read(path));

    public string FileName => System.IO.Path.GetFileName(Path);
    public string EncodingName => Encoding switch
    {
        UTF8Encoding => HasBom ? "UTF-8 BOM" : "UTF-8",
        UnicodeEncoding u when u.CodePage == 1201 => "UTF-16 BE",
        UnicodeEncoding => "UTF-16 LE",
        _ => Encoding.WebName.ToUpperInvariant(),
    };

    public bool IsDirty(string currentText) => !string.Equals(currentText, SavedText, StringComparison.Ordinal);

    public void Save(string text)
    {
        (DiskLastWriteUtc, DiskLength) = TextFile.Write(Path, text, Encoding, HasBom, LineEnding);
        SavedText = text;
    }

    public void SaveAs(string newPath, string text)
    {
        Path = System.IO.Path.GetFullPath(newPath);
        Save(text);
    }

    /// <summary>Re-reads the file from disk and returns the new text.</summary>
    public string Reload()
    {
        var content = TextFile.Read(Path);
        SavedText = content.Text;
        Encoding = content.Encoding;
        HasBom = content.HasBom;
        LineEnding = content.LineEnding;
        DiskLastWriteUtc = content.LastWriteUtc;
        DiskLength = content.Length;
        return content.Text;
    }

    public void Rename(string newPath) => Path = System.IO.Path.GetFullPath(newPath);

    public DiskState CheckDisk()
    {
        var info = new FileInfo(Path);
        if (!info.Exists) return DiskState.DeletedOnDisk;
        return info.LastWriteTimeUtc != DiskLastWriteUtc || info.Length != DiskLength ? DiskState.ChangedOnDisk : DiskState.InSync;
    }

    /// <summary>Accept the current disk state without reloading (user chose to keep their edits).</summary>
    public void AcknowledgeDisk()
    {
        var info = new FileInfo(Path);
        if (!info.Exists) return;
        DiskLastWriteUtc = info.LastWriteTimeUtc;
        DiskLength = info.Length;
    }
}
