using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace LightweightIDE.Language.Completion;

/// <summary>The caret is in a generic argument list: <c>name&lt;A, B, |</c> (found in the text, the code is incomplete).</summary>
public sealed record GenericArgumentContext(string Name, int NameStart, int ReceiverEnd, IReadOnlyList<string> Before, int Index);

/// <summary>
/// Types that fit where the caret is, to put them first in completion:
/// <list type="bullet">
/// <item>a generic argument with constraints — <c>services.AddScoped&lt;IShop, |</c> (TImplementation : class, TService)
/// gives the classes implementing IShop; works for any generic method or type with constraints;</item>
/// <item><c>new</c> where an interface or abstract class is expected — <c>IShop shop = new |</c>.</item>
/// </list>
/// </summary>
public static class SmartTypes
{
    /// <summary>Text-based detection (the parser often reads an unfinished <c>Add&lt;A, </c> as comparisons).</summary>
    public static GenericArgumentContext? FindGenericArgument(string text, int position)
    {
        var depth = 0;
        var i = position - 1;
        for (; i >= 0; i--)
        {
            var c = text[i];
            if (c == '>') depth++;
            else if (c == '<')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (c is ';' or '{' or '}' or '(' or ')' or '=' or '"' or '\n' && depth == 0) return null;
            else if (!(char.IsLetterOrDigit(c) || c is '_' or '.' or ',' or ' ' or '\t' or '?' or '[' or ']' or '\r' or '<' or '>')) return null;
        }
        if (i <= 0) return null;
        var nameEnd = i;
        while (nameEnd > 0 && char.IsWhiteSpace(text[nameEnd - 1])) nameEnd--;
        var nameStart = nameEnd;
        while (nameStart > 0 && (char.IsLetterOrDigit(text[nameStart - 1]) || text[nameStart - 1] == '_')) nameStart--;
        if (nameStart == nameEnd || char.IsDigit(text[nameStart])) return null;
        var name = text[nameStart..nameEnd];
        var receiverEnd = nameStart > 0 && text[nameStart - 1] == '.' ? nameStart - 1 : -1;

        // Arguments before the caret, split at top-level commas.
        var inside = text[(i + 1)..position];
        var parts = new List<string>();
        var level = 0;
        var start = 0;
        for (var k = 0; k < inside.Length; k++)
        {
            if (inside[k] == '<') level++;
            else if (inside[k] == '>') level--;
            else if (inside[k] == ',' && level == 0)
            {
                parts.Add(inside[start..k].Trim());
                start = k + 1;
            }
        }
        return new GenericArgumentContext(name, nameStart, receiverEnd, parts, parts.Count);
    }

    /// <summary>Type name → why it fits ("implements IShop").</summary>
    /// <param name="codeModel">The solution analysis: when ready, its type hierarchy index answers instantly.</param>
    public static async Task<IReadOnlyDictionary<string, string>> FindAsync(Document document, string text, int position, CancellationToken ct,
        LightweightIDE.Language.Analysis.CodeModel? codeModel = null)
    {
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null) return new Dictionary<string, string>();
        var required = FindGenericArgument(text, position) is { } generic
            ? RequiredByConstraints(model, text, generic, position, ct)
            : ExpectedAfterNew(model, position, ct);
        if (required.Count == 0) return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var solution = document.Project.Solution;
        foreach (var (baseType, mustBeConcrete) in required)
        {
            var verb = baseType.TypeKind == TypeKind.Interface ? "implements" : "derives from";
            if (baseType.TypeKind == TypeKind.Class && !baseType.IsAbstract) result.TryAdd(baseType.Name, "fits");
            if (codeModel is { IsReady: true })
            {
                foreach (var type in codeModel.Implementations(baseType).Where(t => t.Kind is TypeKind.Class or TypeKind.Struct && (!mustBeConcrete || !t.IsAbstract && !t.IsStatic)))
                    result.TryAdd(type.Name, $"{verb} {baseType.Name}");
                continue;
            }
            IEnumerable<INamedTypeSymbol> found = baseType.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindImplementationsAsync(baseType, solution, transitive: true, cancellationToken: ct).ConfigureAwait(false)
                : await SymbolFinder.FindDerivedClassesAsync(baseType, solution, transitive: true, cancellationToken: ct).ConfigureAwait(false);
            foreach (var type in found.Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct && (!mustBeConcrete || !t.IsAbstract && !t.IsStatic)))
                result.TryAdd(type.Name, $"{verb} {baseType.Name}");
        }
        return result;
    }

    /// <summary>For each constraint of the type parameter at the caret: the type the argument must implement or derive from.</summary>
    static List<(INamedTypeSymbol Type, bool Concrete)> RequiredByConstraints(SemanticModel model, string text, GenericArgumentContext context, int position, CancellationToken ct)
    {
        var result = new List<(INamedTypeSymbol, bool)>();
        var arity = context.Index + 1;
        foreach (var definition in Candidates(model, text, context, position, ct))
        {
            var parameters = definition switch
            {
                IMethodSymbol m => m.TypeParameters,
                INamedTypeSymbol t => t.TypeParameters,
                _ => [],
            };
            if (parameters.Length < arity) continue;
            var parameter = parameters[context.Index];
            // Earlier arguments, as written: they substitute constraints like "TImplementation : TService".
            var earlier = context.Before.Select(name => ResolveType(model, position, name)).ToList();
            foreach (var constraint in parameter.ConstraintTypes)
            {
                var actual = constraint is ITypeParameterSymbol tp && tp.Ordinal < earlier.Count ? earlier[tp.Ordinal] : constraint as INamedTypeSymbol;
                if (actual is INamedTypeSymbol named && named.SpecialType != SpecialType.System_Object) result.Add((named, true));
            }
        }
        return result;
    }

    static IEnumerable<ISymbol> Candidates(SemanticModel model, string text, GenericArgumentContext context, int position, CancellationToken ct)
    {
        var root = model.SyntaxTree.GetRoot(ct);
        if (context.ReceiverEnd > 0)
        {
            // receiver.Name<…>: methods (including extension methods) on the receiver's type.
            var receiverToken = root.FindToken(Math.Max(0, context.ReceiverEnd - 1));
            var receiver = receiverToken.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault(e => e.Span.End <= context.ReceiverEnd + 1);
            var type = receiver is null ? null : model.GetTypeInfo(receiver, ct).Type;
            if (type is null or IErrorTypeSymbol)
            {
                // Unfinished code is often parsed as a declaration ("services.AddScoped<A, B> x"), so the receiver
                // binds as a type name; look the identifier up as a variable / field / parameter instead.
                var start = context.ReceiverEnd;
                while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
                var identifier = text[start..context.ReceiverEnd];
                type = identifier.Length == 0 ? null : model.LookupSymbols(context.NameStart, name: identifier).Select(s => s switch
                {
                    ILocalSymbol l => l.Type,
                    IParameterSymbol p => p.Type,
                    IFieldSymbol f => f.Type,
                    IPropertySymbol p => p.Type,
                    INamedTypeSymbol t => t,
                    _ => null,
                }).FirstOrDefault(t => t is not null);
            }
            if (type is not null and not IErrorTypeSymbol)
                return model.LookupSymbols(context.NameStart, type, context.Name, includeReducedExtensionMethods: true)
                    .Select(s => s is IMethodSymbol { ReducedFrom: { } from } ? from : s.OriginalDefinition);
        }
        // Name<…> in scope: generic methods or types.
        return model.LookupSymbols(context.NameStart, name: context.Name)
            .Concat(model.LookupNamespacesAndTypes(context.NameStart, name: context.Name))
            .Select(s => s.OriginalDefinition)
            .Where(s => s is IMethodSymbol { IsGenericMethod: true } or INamedTypeSymbol { IsGenericType: true });
    }

    static ITypeSymbol? ResolveType(SemanticModel model, int position, string name)
    {
        if (name.Length == 0) return null;
        var syntax = SyntaxFactory.ParseTypeName(name);
        return model.GetSpeculativeTypeInfo(position, syntax, SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
    }

    /// <summary><c>IShop s = new |</c>, <c>s = new |</c>, <c>IShop F() =&gt; new |</c>: the declared / assigned type when it is abstract.</summary>
    static List<(INamedTypeSymbol Type, bool Concrete)> ExpectedAfterNew(SemanticModel model, int position, CancellationToken ct)
    {
        var root = model.SyntaxTree.GetRoot(ct);
        var token = root.FindToken(Math.Max(0, position - 1));
        if (!token.IsKind(SyntaxKind.NewKeyword))
        {
            token = token.GetPreviousToken();
            if (!token.IsKind(SyntaxKind.NewKeyword)) return [];
        }
        ITypeSymbol? expected = null;
        foreach (var node in token.Parent!.Ancestors())
        {
            switch (node)
            {
                case VariableDeclarationSyntax { Type.IsVar: false } declaration:
                    expected = model.GetTypeInfo(declaration.Type, ct).Type;
                    break;
                case AssignmentExpressionSyntax assignment:
                    expected = model.GetTypeInfo(assignment.Left, ct).Type;
                    break;
                case PropertyDeclarationSyntax property:
                    expected = model.GetTypeInfo(property.Type, ct).Type;
                    break;
                case ReturnStatementSyntax or ArrowExpressionClauseSyntax:
                    var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                    expected = member is null ? null : (model.GetDeclaredSymbol(member, ct) as IMethodSymbol)?.ReturnType;
                    break;
                default:
                    continue;
            }
            break;
        }
        return expected is INamedTypeSymbol { TypeKind: TypeKind.Interface } or INamedTypeSymbol { IsAbstract: true, TypeKind: TypeKind.Class }
            ? [((INamedTypeSymbol)expected, true)]
            : [];
    }
}
