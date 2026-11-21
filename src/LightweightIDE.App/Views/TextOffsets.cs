namespace LightweightIDE.App.Views;

/// <summary>
/// GtkTextBuffer offsets count Unicode characters; Roslyn and .NET strings count UTF-16 code units.
/// They differ only when the text contains surrogate pairs (emoji, rare CJK), so the common case is free.
/// </summary>
public static class TextOffsets
{
    public static bool HasSurrogates(string text) => text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') >= 0;

    public static int CharToUtf16(string text, int charOffset)
    {
        if (!HasSurrogates(text)) return Math.Min(charOffset, text.Length);
        var chars = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (chars == charOffset) return i;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            chars++;
        }
        return text.Length;
    }

    public static int Utf16ToChar(string text, int utf16Index)
    {
        if (!HasSurrogates(text)) return utf16Index;
        var chars = 0;
        for (var i = 0; i < Math.Min(utf16Index, text.Length); i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            chars++;
        }
        return chars;
    }
}
