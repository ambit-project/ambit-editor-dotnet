using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LightweightIDE.Indexing;

public sealed record IndexedSymbol(string Name, string Kind, string? Container, int Line, int Column);

/// <summary>
/// Extracts declared symbols from one C# file using the syntax tree only (no compilation, no project load),
/// so indexing a large solution stays cheap. Parses with the latest language version, which includes C# 15.
/// </summary>
public static class SymbolExtractor
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview, DocumentationMode.None);

    public static IReadOnlyList<IndexedSymbol> Extract(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var root = tree.GetRoot();
        var result = new List<IndexedSymbol>();
        foreach (var node in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or ExtensionBlockDeclarationSyntax))
        {
            switch (node)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    Add(ns.Name.ToString(), "namespace", null, ns.Name);
                    break;
                case BaseTypeDeclarationSyntax type:
                    Add(type.Identifier.ValueText, KindOf(type), ContainerOf(type), type.Identifier);
                    break;
                case DelegateDeclarationSyntax del:
                    Add(del.Identifier.ValueText, "delegate", ContainerOf(del), del.Identifier);
                    break;
                case MethodDeclarationSyntax m:
                    Add(m.Identifier.ValueText, "method", ContainerOf(m), m.Identifier);
                    break;
                case ConstructorDeclarationSyntax c:
                    Add(c.Identifier.ValueText, "constructor", ContainerOf(c), c.Identifier);
                    break;
                case PropertyDeclarationSyntax p:
                    Add(p.Identifier.ValueText, "property", ContainerOf(p), p.Identifier);
                    break;
                case EventDeclarationSyntax e:
                    Add(e.Identifier.ValueText, "event", ContainerOf(e), e.Identifier);
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                        Add(v.Identifier.ValueText, f.Modifiers.Any(SyntaxKind.ConstKeyword) ? "constant" : "field", ContainerOf(f), v.Identifier);
                    break;
                case EventFieldDeclarationSyntax ef:
                    foreach (var v in ef.Declaration.Variables)
                        Add(v.Identifier.ValueText, "event", ContainerOf(ef), v.Identifier);
                    break;
                case EnumMemberDeclarationSyntax em:
                    Add(em.Identifier.ValueText, "enum member", ContainerOf(em), em.Identifier);
                    break;
            }
        }
        return result;

        void Add(string name, string kind, string? container, SyntaxNodeOrToken at)
        {
            if (string.IsNullOrEmpty(name)) return;
            var pos = at.GetLocation()!.GetLineSpan().StartLinePosition;
            result.Add(new IndexedSymbol(name, kind, container, pos.Line + 1, pos.Character + 1));
        }
    }

    static string KindOf(BaseTypeDeclarationSyntax type) => type switch
    {
        ClassDeclarationSyntax => "class",
        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        UnionDeclarationSyntax => "union", // C# 15
        _ => "type",
    };

    /// <summary>Namespace-qualified name of the enclosing type, or the namespace for top-level types.</summary>
    static string? ContainerOf(SyntaxNode node)
    {
        var parts = new Stack<string>();
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case BaseTypeDeclarationSyntax t: parts.Push(t.Identifier.ValueText); break;
                case BaseNamespaceDeclarationSyntax ns: parts.Push(ns.Name.ToString()); break;
            }
        }
        if (parts.Count == 0)
        {
            // file-scoped namespace is a sibling-level ancestor, handled above; top-level code has no container
            return null;
        }
        return string.Join('.', parts);
    }
}
