using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace LightweightIDE.Language.Classification;

/// <summary>A classified range (UTF-16 offsets) with the IDE's kind name, e.g. "class", "local", "static".</summary>
public readonly record struct SemanticSpan(int Start, int End, string Kind);

/// <summary>
/// Semantic highlighting: what each identifier is (class, interface, method, local, parameter, field, static …),
/// from Roslyn's classifier — the one Visual Studio uses. Only a range is classified (the visible lines): 3–17 ms
/// for 80 lines on the reference solution, against 60–420 ms for a whole 2 000-line file. Lexical colors (keywords, strings,
/// comments, numbers) stay with GtkSourceView; the kinds here are layered on top.
/// </summary>
public sealed class SemanticHighlighter(WorkspaceService workspace)
{
    /// <summary>The kinds the editor colors; each has a style id "lw:&lt;kind&gt;" in the color scheme.</summary>
    public static readonly IReadOnlyList<string> Kinds =
    [
        "namespace", "class", "struct", "interface", "enum", "delegate", "type-parameter",
        "method", "extension-method", "property", "field", "constant", "event", "enum-member",
        "local", "parameter", "label", "control-keyword", "string-escape", "operator-overloaded",
        "static",
    ];

    static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        [ClassificationTypeNames.NamespaceName] = "namespace",
        [ClassificationTypeNames.ClassName] = "class",
        [ClassificationTypeNames.RecordClassName] = "class",
        [ClassificationTypeNames.StructName] = "struct",
        [ClassificationTypeNames.RecordStructName] = "struct",
        [ClassificationTypeNames.InterfaceName] = "interface",
        [ClassificationTypeNames.EnumName] = "enum",
        [ClassificationTypeNames.DelegateName] = "delegate",
        [ClassificationTypeNames.TypeParameterName] = "type-parameter",
        [ClassificationTypeNames.MethodName] = "method",
        [ClassificationTypeNames.ExtensionMethodName] = "extension-method",
        [ClassificationTypeNames.PropertyName] = "property",
        [ClassificationTypeNames.FieldName] = "field",
        [ClassificationTypeNames.ConstantName] = "constant",
        [ClassificationTypeNames.EventName] = "event",
        [ClassificationTypeNames.EnumMemberName] = "enum-member",
        [ClassificationTypeNames.LocalName] = "local",
        [ClassificationTypeNames.ParameterName] = "parameter",
        [ClassificationTypeNames.LabelName] = "label",
        [ClassificationTypeNames.ControlKeyword] = "control-keyword",
        [ClassificationTypeNames.StringEscapeCharacter] = "string-escape",
        [ClassificationTypeNames.OperatorOverloaded] = "operator-overloaded",
        [ClassificationTypeNames.StaticSymbol] = "static",
    };

    /// <summary>Kind of a Roslyn classification, or null when the lexical highlighting already covers it.</summary>
    public static string? KindOf(string classification) => Map.GetValueOrDefault(classification);

    public async Task<IReadOnlyList<SemanticSpan>> GetSpansAsync(string path, string text, int start, int end, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        if ((await document.GetTextAsync(ct).ConfigureAwait(false)).ToString() != text)
            document = document.WithText(SourceText.From(text));
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        var classified = await Classifier.GetClassifiedSpansAsync(document, TextSpan.FromBounds(start, end), ct).ConfigureAwait(false);
        var result = new List<SemanticSpan>();
        foreach (var span in classified)
            if (KindOf(span.ClassificationType) is { } kind)
                result.Add(new SemanticSpan(span.TextSpan.Start, span.TextSpan.End, kind));
        return result;
    }
}
