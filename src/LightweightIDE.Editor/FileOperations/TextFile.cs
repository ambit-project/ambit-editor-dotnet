using System.Text;

namespace LightweightIDE.Editor.FileOperations;

public enum LineEnding { Lf, CrLf }

/// <summary>Text loaded from disk plus everything needed to write it back unchanged.</summary>
public sealed record TextFileContent(string Text, Encoding Encoding, bool HasBom, LineEnding LineEnding, DateTime LastWriteUtc, long Length);

/// <summary>
/// Reads and writes source files. Detects the encoding from the BOM, otherwise UTF-8 when the bytes are valid UTF-8,
/// otherwise Latin-1 (never loses bytes). Line endings are normalised to \n in memory and restored on save.
/// </summary>
public static class TextFile
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static TextFileContent Read(string path)
    {
        var info = new FileInfo(path);
        var bytes = File.ReadAllBytes(path);
        var (encoding, hasBom, bomLength) = Detect(bytes);
        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        var ending = text.Contains("\r\n", StringComparison.Ordinal) ? LineEnding.CrLf : LineEnding.Lf;
        if (ending == LineEnding.CrLf) text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return new TextFileContent(text, encoding, hasBom, ending, info.LastWriteTimeUtc, info.Length);
    }

    public static (Encoding Encoding, bool HasBom, int BomLength) Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return (new UTF8Encoding(true), true, 3);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return (Encoding.Unicode, true, 2);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return (Encoding.BigEndianUnicode, true, 2);
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return (new UTF8Encoding(false), false, 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1, false, 0);
        }
    }

    /// <summary>Writes atomically (temp file + rename) and returns the new on-disk timestamp.</summary>
    public static (DateTime LastWriteUtc, long Length) Write(string path, string text, Encoding encoding, bool hasBom, LineEnding ending)
    {
        if (ending == LineEnding.CrLf) text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (hasBom) stream.Write(encoding.GetPreamble());
            var withoutBom = encoding is UTF8Encoding ? new UTF8Encoding(false) : encoding;
            stream.Write(withoutBom.GetBytes(text));
        }
        if (File.Exists(path))
        {
            try { File.SetUnixFileMode(temp, File.GetUnixFileMode(path)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        File.Move(temp, path, overwrite: true);
        var info = new FileInfo(path);
        return (info.LastWriteTimeUtc, info.Length);
    }

    /// <summary>Heuristic for binary files: a NUL byte in the first 8 KB.</summary>
    public static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[8192];
        var read = stream.Read(buffer);
        var head = buffer[..read];
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return false;
        return head.IndexOf((byte)0) >= 0;
    }
}
