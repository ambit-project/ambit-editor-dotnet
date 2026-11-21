using System.Text;
using LightweightIDE.Editor;
using LightweightIDE.Editor.FileOperations;

namespace LightweightIDE.Tests;

public class TextFileTests
{
    [Fact]
    public void Utf8WithBomAndCrLf_RoundTripsByteForByte()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "a.cs");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("class Zażółć\r\n{\r\n}\r\n")).ToArray();
        File.WriteAllBytes(path, bytes);

        var doc = EditorDocument.Open(path);
        Assert.Equal("class Zażółć\n{\n}\n", doc.SavedText);
        Assert.Equal("UTF-8 BOM", doc.EncodingName);
        Assert.Equal(LineEnding.CrLf, doc.LineEnding);

        doc.Save(doc.SavedText);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void InvalidUtf8_FallsBackToLatin1WithoutLosingBytes()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "legacy.txt");
        var bytes = new byte[] { (byte)'a', 0xE9, (byte)'b', (byte)'\n' };
        File.WriteAllBytes(path, bytes);

        var doc = EditorDocument.Open(path);
        Assert.Equal("ISO-8859-1", doc.EncodingName);
        doc.Save(doc.SavedText);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void NewFile_DefaultsToUtf8WithoutBom()
    {
        using var dir = new TempDir();
        var path = dir.Write("b.cs", "");
        var doc = EditorDocument.Open(path);
        Assert.Equal("UTF-8", doc.EncodingName);
    }

    [Fact]
    public void ExternalChange_IsDetected()
    {
        using var dir = new TempDir();
        var path = dir.Write("c.cs", "one");
        var doc = EditorDocument.Open(path);
        Assert.Equal(DiskState.InSync, doc.CheckDisk());
        File.WriteAllText(path, "two, longer");
        Assert.Equal(DiskState.ChangedOnDisk, doc.CheckDisk());
        Assert.Equal("two, longer", doc.Reload());
        Assert.Equal(DiskState.InSync, doc.CheckDisk());
        File.Delete(path);
        Assert.Equal(DiskState.DeletedOnDisk, doc.CheckDisk());
    }

    [Fact]
    public void Dirty_ComparesWithSavedText()
    {
        using var dir = new TempDir();
        var doc = EditorDocument.Open(dir.Write("d.cs", "x"));
        Assert.False(doc.IsDirty("x"));
        Assert.True(doc.IsDirty("xy"));
    }

    [Fact]
    public void Binary_IsDetected()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "x.bin");
        File.WriteAllBytes(path, [1, 2, 0, 3]);
        Assert.True(TextFile.LooksBinary(path));
        Assert.False(TextFile.LooksBinary(dir.Write("y.cs", "class Y {}")));
    }
}

public class UnsavedBackupStoreTests
{
    [Fact]
    public void Save_List_Load_Delete()
    {
        using var dir = new TempDir();
        var store = new LightweightIDE.Editor.UnsavedBackupStore(Path.Combine(dir.Path, "backup"));
        store.Save(new("/repo/src/A.cs", "class A { }", DateTime.UtcNow.AddMinutes(-1), null));
        store.Save(new("/repo/src/B.cs", "class B { ąę }", DateTime.UtcNow, DateTime.UtcNow));
        store.Save(new("/other/C.cs", "x", DateTime.UtcNow, null));

        Assert.Equal(["/repo/src/B.cs", "/repo/src/A.cs"], store.List("/repo").Select(b => b.Path));
        Assert.Equal("class B { ąę }", store.Load("/repo/src/B.cs")!.Text);
        Assert.Equal(3, store.List().Count);

        store.Save(new("/repo/src/A.cs", "class A { int x; }", DateTime.UtcNow, null)); // overwrite
        Assert.Equal("class A { int x; }", store.Load("/repo/src/A.cs")!.Text);
        store.Delete("/repo/src/A.cs");
        Assert.Null(store.Load("/repo/src/A.cs"));
        store.DeleteAll();
        Assert.Empty(store.List());
    }
}
