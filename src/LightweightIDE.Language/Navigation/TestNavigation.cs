using System.Text.RegularExpressions;
using System.Collections.Immutable;
using LightweightIDE.Language.RoslynWorkspace;
using LightweightIDE.Language.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace LightweightIDE.Language.Navigation;

/// <summary>A test or tested type found for the code at the caret, and why it was chosen.</summary>
public sealed record TestTarget(SourceLocation Location, string Name, string Reason);

/// <param name="ToTests">True: from production code to its tests; false: from a test to the tested code.</param>
public sealed record TestNavigationResult(bool ToTests, string Subject, IReadOnlyList<TestTarget> Targets);

/// <summary>
/// Go to Tests / Tested Code (Ctrl+Shift+G), both ways.
/// From production code: test classes named after the type (OrderService → OrderServiceTests, OrderServiceIntegrationTests,
/// OrderServiceShould …), then the tests that use it — the test methods that call the method at the caret, or the test
/// classes that reference the type. From a test: the type named like the test class without its suffix, otherwise the
/// production types the test class uses most.
/// </summary>
public sealed partial class TestNavigation(WorkspaceService workspace)
{
    const int MaxTargets = 50;

    [GeneratedRegex(@"(IntegrationTests?|UnitTests?|Tests?|Specs?|Should|Fixture)$")]
    private static partial Regex TestSuffix();

    readonly Dictionary<string, bool> testProjects = new(StringComparer.Ordinal);

    bool IsTestProject(Project project)
    {
        if (project.FilePath is not { } path) return false;
        lock (testProjects)
        {
            if (!testProjects.TryGetValue(path, out var isTest)) testProjects[path] = isTest = LightweightIDE.DotNet.ProjectSystem.ProjectInfo.Read(path).IsTestProject;
            return isTest;
        }
    }

    public async Task<TestNavigationResult?> FindAsync(string path, string text, int position, CancellationToken ct)
    {
        var document = workspace.GetDocumentOrMisc(path, text);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (model is null || root is null) return null;
        var token = root.FindToken(Math.Clamp(position, 0, Math.Max(0, root.FullSpan.End - 1)));
        var typeSyntax = token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            ?? root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeSyntax is null || model.GetDeclaredSymbol(typeSyntax, ct) is not INamedTypeSymbol type) return null;
        var methodSyntax = token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var method = methodSyntax is null ? null : model.GetDeclaredSymbol(methodSyntax, ct) as IMethodSymbol;
        var solution = document.Project.Solution;

        return IsTestProject(document.Project)
            ? await FindTestedAsync(solution, type, typeSyntax, model, ct).ConfigureAwait(false)
            : await FindTestsAsync(solution, type, method, ct).ConfigureAwait(false);
    }

    async Task<TestNavigationResult> FindTestsAsync(Solution solution, INamedTypeSymbol type, IMethodSymbol? method, CancellationToken ct)
    {
        var targets = new List<TestTarget>();
        var testDocuments = solution.Projects.Where(IsTestProject).SelectMany(p => p.Documents).ToHashSet();

        // 1. Test classes named after the type.
        var pattern = new Regex("^" + Regex.Escape(type.Name) + @"\w*?(Tests?|Specs?|Should|Fixture)$");
        foreach (var project in solution.Projects.Where(IsTestProject))
        {
            var declarations = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(project, type.Name, SymbolFilter.Type, ct).ConfigureAwait(false);
            foreach (var candidate in declarations.OfType<INamedTypeSymbol>().Where(t => pattern.IsMatch(t.Name)))
                foreach (var location in candidate.Locations.Where(l => l.IsInSource))
                    targets.Add(new TestTarget(await NavigationService.ToSourceLocationAsync(solution, location, ct).ConfigureAwait(false),
                        candidate.Name, $"named after {type.Name}"));
        }

        // 2. Tests that use it: test methods calling the method at the caret, else test classes referencing the type.
        var subject = (ISymbol?)method ?? type;
        var uses = new Dictionary<SyntaxNode, int>();
        var references = await SymbolFinder.FindReferencesAsync(subject, solution, testDocuments.ToImmutableHashSet(), ct).ConfigureAwait(false);
        foreach (var location in references.SelectMany(r => r.Locations))
        {
            var node = (await location.Location.SourceTree!.GetRootAsync(ct).ConfigureAwait(false)).FindNode(location.Location.SourceSpan);
            SyntaxNode? owner = method is not null
                ? node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault(TestDiscovery.IsTestMethod)
                : node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Members.OfType<MethodDeclarationSyntax>().Any(TestDiscovery.IsTestMethod));
            if (owner is not null) uses[owner] = uses.GetValueOrDefault(owner) + 1;
        }
        var verb = method is null ? $"uses {type.Name}" : $"calls {method.Name}()";
        foreach (var (owner, count) in uses.OrderByDescending(u => u.Value))
        {
            var (name, identifier) = owner switch
            {
                MethodDeclarationSyntax m => ($"{(m.Parent as TypeDeclarationSyntax)?.Identifier.ValueText}.{m.Identifier.ValueText}", m.Identifier),
                ClassDeclarationSyntax c => (c.Identifier.ValueText, c.Identifier),
                _ => ("", default),
            };
            var target = new TestTarget(await NavigationService.ToSourceLocationAsync(solution, identifier.GetLocation(), ct).ConfigureAwait(false),
                name, count > 1 ? $"{verb} ({count}×)" : verb);
            if (!targets.Any(t => t.Location.Path == target.Location.Path && t.Location.Line == target.Location.Line)) targets.Add(target);
        }
        var subjectName = method is null ? type.Name : $"{type.Name}.{method.Name}()";
        return new TestNavigationResult(true, subjectName, targets.Take(MaxTargets).ToList());
    }

    async Task<TestNavigationResult> FindTestedAsync(Solution solution, INamedTypeSymbol testType, TypeDeclarationSyntax testSyntax,
        SemanticModel model, CancellationToken ct)
    {
        var targets = new List<TestTarget>();
        var production = solution.Projects.Where(p => !IsTestProject(p)).ToList();

        // 1. The type named like the test class without its suffix (OrderServiceTests → OrderService).
        var name = TestSuffix().Replace(testType.Name, "");
        if (name.Length > 0 && name != testType.Name)
            foreach (var project in production)
                foreach (var candidate in (await SymbolFinder.FindSourceDeclarationsAsync(project, name, ignoreCase: false, SymbolFilter.Type, ct).ConfigureAwait(false))
                         .OfType<INamedTypeSymbol>())
                    foreach (var location in candidate.Locations.Where(l => l.IsInSource))
                        targets.Add(new TestTarget(await NavigationService.ToSourceLocationAsync(solution, location, ct).ConfigureAwait(false),
                            candidate.Name, $"{testType.Name} is named after it"));

        // 2. Otherwise the production types the test class uses most.
        if (targets.Count == 0)
        {
            var productionAssemblies = production.Select(p => p.AssemblyName).ToHashSet(StringComparer.Ordinal);
            var counts = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
            foreach (var identifier in testSyntax.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(identifier, ct).Symbol;
                var used = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                if (used is null || !used.Locations.Any(l => l.IsInSource) || !productionAssemblies.Contains(used.ContainingAssembly?.Name ?? "")) continue;
                counts[used.OriginalDefinition] = counts.GetValueOrDefault(used.OriginalDefinition) + 1;
            }
            foreach (var (used, count) in counts.OrderByDescending(c => c.Value).Take(5))
                foreach (var location in used.Locations.Where(l => l.IsInSource).Take(1))
                    targets.Add(new TestTarget(await NavigationService.ToSourceLocationAsync(solution, location, ct).ConfigureAwait(false),
                        used.Name, $"used {count}× in {testType.Name}"));
        }
        return new TestNavigationResult(false, testType.Name, targets);
    }
}
