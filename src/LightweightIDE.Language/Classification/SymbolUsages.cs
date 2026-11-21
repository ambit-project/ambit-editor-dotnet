using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LightweightIDE.Language.Classification;

/// <summary>An occurrence of the symbol under the caret (UTF-16 offsets); <see cref="IsDefinition"/> for its declaration.</summary>
public readonly record struct UsageSpan(int Start, int End, bool IsDefinition);

/// <summary>
/// The other occurrences of the symbol under the caret in the same file, as Rider / VS highlight them. Only the
/// file's own semantic model is used: every identifier with the same name is resolved and compared, so the cost stays
/// a few milliseconds (SymbolFinder.FindReferences cascades to implementations across the solution: up to 4 s on
/// the reference solution). A constructor counts as its type, an extension method call as the method.
/// </summary>
public sealed class SymbolUsages(WorkspaceService workspace)
{
    public async Task<IReadOnlyList<UsageSpan>> GetAsync(string path, string text, int position, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        if ((await document.GetTextAsync(ct).ConfigureAwait(false)).ToString() != text)
            document = document.WithText(SourceText.From(text));
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return [];
        position = Math.Clamp(position, 0, text.Length);
        var token = root.FindToken(position);
        // Caret right after an identifier ("name|") counts as on it.
        if (!token.IsKind(SyntaxKind.IdentifierToken) && position > 0) token = root.FindToken(position - 1);
        if (!token.IsKind(SyntaxKind.IdentifierToken) || token.Parent is IdentifierNameSyntax { IsVar: true }) return [];

        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null) return [];
        var (target, _) = SymbolOf(model, token, ct);
        if (target is null || target.Kind is SymbolKind.Namespace or SymbolKind.ErrorType) return [];

        var name = token.ValueText;
        var result = new List<UsageSpan>();
        foreach (var other in root.DescendantTokens())
        {
            if (!other.IsKind(SyntaxKind.IdentifierToken) || other.ValueText != name) continue;
            ct.ThrowIfCancellationRequested();
            var (symbol, declared) = SymbolOf(model, other, ct);
            if (symbol is not null && SymbolEqualityComparer.Default.Equals(symbol, target))
                result.Add(new UsageSpan(other.SpanStart, other.Span.End, declared));
        }
        return result;
    }

    /// <summary>The symbol a name token declares or refers to, normalized for comparison.</summary>
    static (ISymbol? Symbol, bool Declared) SymbolOf(SemanticModel model, SyntaxToken token, CancellationToken ct)
    {
        var parent = token.Parent;
        if (parent is null) return (null, false);
        var declared = model.GetDeclaredSymbol(parent, ct);
        if (declared is not null && DeclaresWith(parent, token)) return (Normalize(declared), true);
        var info = model.GetSymbolInfo(parent, ct);
        var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
        symbol ??= model.GetAliasInfo(parent, ct)?.Target;
        return (Normalize(symbol), false);
    }

    /// <summary>True when the token is the declaration's own name (not, say, a type name inside it).</summary>
    static bool DeclaresWith(SyntaxNode node, SyntaxToken token) => node switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier == token,
        DelegateDeclarationSyntax d => d.Identifier == token,
        MethodDeclarationSyntax m => m.Identifier == token,
        ConstructorDeclarationSyntax c => c.Identifier == token,
        DestructorDeclarationSyntax d => d.Identifier == token,
        PropertyDeclarationSyntax p => p.Identifier == token,
        EventDeclarationSyntax e => e.Identifier == token,
        VariableDeclaratorSyntax v => v.Identifier == token,
        ParameterSyntax p => p.Identifier == token,
        TypeParameterSyntax t => t.Identifier == token,
        EnumMemberDeclarationSyntax e => e.Identifier == token,
        LocalFunctionStatementSyntax l => l.Identifier == token,
        SingleVariableDesignationSyntax s => s.Identifier == token,
        ForEachStatementSyntax f => f.Identifier == token,
        CatchDeclarationSyntax c => c.Identifier == token,
        LabeledStatementSyntax l => l.Identifier == token,
        FromClauseSyntax f => f.Identifier == token,
        LetClauseSyntax l => l.Identifier == token,
        JoinIntoClauseSyntax j => j.Identifier == token,
        QueryContinuationSyntax q => q.Identifier == token,
        _ => false,
    };

    static ISymbol? Normalize(ISymbol? symbol) => symbol switch
    {
        null => null,
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor } m => m.ContainingType.OriginalDefinition,
        IMethodSymbol { ReducedFrom: { } reduced } => reduced.OriginalDefinition,
        IMethodSymbol { PartialDefinitionPart: { } definition } => definition.OriginalDefinition,
        IAliasSymbol alias => alias.Target.OriginalDefinition,
        _ => symbol.OriginalDefinition,
    };
}
