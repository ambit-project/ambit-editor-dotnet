using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;

namespace LightweightIDE.Language.Navigation;

/// <summary>A source location. Line and column are 1-based; LineText is the trimmed source line for result lists.</summary>
public sealed record SourceLocation(string Path, int Line, int Column, int Start, int Length, string LineText);

public sealed record DefinitionResult(string SymbolDisplay, IReadOnlyList<SourceLocation> Locations)
{
    /// <summary>The symbol lives in a referenced assembly (no source available).</summary>
    public bool IsMetadata => Locations.Count == 0;
}

/// <summary>A type the hover popup links to.</summary>
public sealed record HoverLink(string Name, string Kind, SourceLocation Location);

/// <summary>The hovered symbol's definition (null: metadata only), related types and whether it has implementations.</summary>
public sealed record HoverTargets(string Name, string Kind, SourceLocation? Definition, IReadOnlyList<HoverLink> Links, bool CanHaveImplementations);

public sealed record ReferencesResult(string SymbolDisplay, IReadOnlyList<SourceLocation> Locations, bool Truncated);

/// <summary>Go to Definition, Find All References and hover text, using Roslyn's SymbolFinder and QuickInfoService.</summary>
public sealed class NavigationService(WorkspaceService workspace, LanguageOptions options)
{
    async Task<(ISymbol? Symbol, Document Document)> SymbolAtAsync(string path, string text, int position, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model!, position, document.Project.Solution.Workspace, ct).ConfigureAwait(false);
        return (symbol, document);
    }

    public async Task<DefinitionResult?> GoToDefinitionAsync(string path, string text, int position, CancellationToken ct)
    {
        var (symbol, document) = await SymbolAtAsync(path, text, position, ct).ConfigureAwait(false);
        if (symbol is null) return null;
        // Partial methods, implementations of generated declarations, etc.
        var source = await SymbolFinder.FindSourceDefinitionAsync(symbol, document.Project.Solution, ct).ConfigureAwait(false) ?? symbol;
        var locations = new List<SourceLocation>();
        foreach (var location in source.Locations.Where(l => l.IsInSource))
            locations.Add(await ToSourceLocationAsync(document.Project.Solution, location, ct).ConfigureAwait(false));
        return new DefinitionResult(source.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), locations);
    }

    public async Task<ReferencesResult?> FindReferencesAsync(string path, string text, int position, CancellationToken ct)
    {
        var (symbol, document) = await SymbolAtAsync(path, text, position, ct).ConfigureAwait(false);
        if (symbol is null) return null;
        var solution = document.Project.Solution;
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false);
        var locations = new List<SourceLocation>();
        var truncated = false;
        foreach (var reference in references)
        {
            foreach (var definition in reference.Definition.Locations.Where(l => l.IsInSource))
                locations.Add(await ToSourceLocationAsync(solution, definition, ct).ConfigureAwait(false));
            foreach (var location in reference.Locations)
            {
                if (locations.Count >= options.MaxReferences) { truncated = true; break; }
                locations.Add(await ToSourceLocationAsync(solution, location.Location, ct).ConfigureAwait(false));
            }
        }
        var distinct = locations.DistinctBy(l => (l.Path, l.Start)).OrderBy(l => l.Path, StringComparer.Ordinal).ThenBy(l => l.Start).ToList();
        return new ReferencesResult(symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), distinct, truncated);
    }

    /// <summary>
    /// Go to Implementation: implementations of an interface or interface member, derived classes of a class,
    /// overrides of a virtual / abstract member.
    /// </summary>
    public async Task<ReferencesResult?> FindImplementationsAsync(string path, string text, int position, CancellationToken ct)
    {
        var (symbol, document) = await SymbolAtAsync(path, text, position, ct).ConfigureAwait(false);
        if (symbol is null) return null;
        var solution = document.Project.Solution;
        var found = new List<ISymbol>();
        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface } type:
                found.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: ct).ConfigureAwait(false));
                break;
            case INamedTypeSymbol { TypeKind: TypeKind.Class } type:
                found.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: ct).ConfigureAwait(false));
                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol:
                found.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false));
                if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride)
                    found.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false));
                break;
        }
        var locations = new List<SourceLocation>();
        foreach (var location in found.SelectMany(s => s.Locations).Where(l => l.IsInSource))
            locations.Add(await ToSourceLocationAsync(solution, location, ct).ConfigureAwait(false));
        var distinct = locations.DistinctBy(l => (l.Path, l.Start)).OrderBy(l => l.Path, StringComparer.Ordinal).ThenBy(l => l.Start).ToList();
        return new ReferencesResult(symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), distinct, false);
    }

    /// <summary>Hover text: Roslyn quick info (signature, documentation, nullability), as plain text.</summary>
    public async Task<string?> GetQuickInfoAsync(string path, string text, int position, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        var service = QuickInfoService.GetService(document);
        if (service is null) return null;
        var info = await service.GetQuickInfoAsync(document, position, ct).ConfigureAwait(false);
        if (info is null) return null;
        var parts = info.Sections.Select(s => s.Text.Trim()).Where(t => t.Length > 0);
        var result = string.Join("\n\n", parts);
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// What the hover popup can jump to: the symbol's own definition, the types it is about (a variable's or member's
    /// type, a method's return and parameter types, a type's base class and interfaces; generic arguments and array
    /// elements unwrapped), and whether Go to Implementation makes sense. Only types with source in the solution.
    /// </summary>
    public async Task<HoverTargets?> GetHoverTargetsAsync(string path, string text, int position, CancellationToken ct)
    {
        var (symbol, document) = await SymbolAtAsync(path, text, position, ct).ConfigureAwait(false);
        if (symbol is null) return null;
        var solution = document.Project.Solution;
        var source = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, ct).ConfigureAwait(false) ?? symbol;
        SourceLocation? definition = null;
        if (source.Locations.FirstOrDefault(l => l.IsInSource) is { } own)
            definition = await ToSourceLocationAsync(solution, own, ct).ConfigureAwait(false);

        var related = new List<ITypeSymbol>();
        switch (source)
        {
            case ILocalSymbol local: related.Add(local.Type); break;
            case IParameterSymbol parameter: related.Add(parameter.Type); break;
            case IFieldSymbol field: related.Add(field.Type); break;
            case IPropertySymbol property: related.Add(property.Type); break;
            case IEventSymbol @event: related.Add(@event.Type); break;
            case IMethodSymbol method:
                if (method.MethodKind != MethodKind.Constructor) related.Add(method.ReturnType);
                related.AddRange(method.Parameters.Select(p => p.Type));
                if (method.ContainingType is { } owner) related.Add(owner);
                break;
            case INamedTypeSymbol type:
                if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType) related.Add(baseType);
                related.AddRange(type.Interfaces);
                break;
        }
        var links = new List<HoverLink>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { source.OriginalDefinition };
        foreach (var type in related.SelectMany(Unwrap))
        {
            if (links.Count >= 8) break;
            var definitionType = type.OriginalDefinition;
            if (!seen.Add(definitionType) || definitionType.Locations.FirstOrDefault(l => l.IsInSource) is not { } location) continue;
            links.Add(new HoverLink(definitionType.Name, KindOf(definitionType), await ToSourceLocationAsync(solution, location, ct).ConfigureAwait(false)));
        }
        var implementable = source switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => true,
            INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: false, IsStatic: false } => true,
            IMethodSymbol or IPropertySymbol or IEventSymbol => source.IsAbstract || source.IsVirtual || source.IsOverride
                || source.ContainingType?.TypeKind == TypeKind.Interface,
            _ => false,
        };
        return new HoverTargets(source.Name, KindOf(source), definition, links, implementable);

        static IEnumerable<ITypeSymbol> Unwrap(ITypeSymbol type) => type switch
        {
            IArrayTypeSymbol array => Unwrap(array.ElementType),
            INamedTypeSymbol { IsGenericType: true } named => new[] { named }.Concat(named.TypeArguments.SelectMany(Unwrap)),
            ITypeParameterSymbol => [],
            _ => [type],
        };
    }

    static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
        INamedTypeSymbol { IsRecord: true } => "record",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "struct",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "delegate",
        INamedTypeSymbol => "class",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        ILocalSymbol => "local",
        IParameterSymbol => "parameter",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    internal static async Task<SourceLocation> ToSourceLocationAsync(Solution solution, Location location, CancellationToken ct)
    {
        var span = location.GetLineSpan();
        var tree = location.SourceTree!;
        var text = await tree.GetTextAsync(ct).ConfigureAwait(false);
        var lineText = text.Lines[span.StartLinePosition.Line].ToString().Trim();
        return new SourceLocation(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            location.SourceSpan.Start, location.SourceSpan.Length, lineText);
    }
}
