using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LightweightIDE.Language.Testing;

/// <summary>A test method found in source: project, file, 1-based line, "Ns.Class.Method" and "Ns.Class".</summary>
public sealed record DiscoveredTest(string Project, string File, int Line, string Name, string Class);

/// <param name="Line">1-based line of the class or method name.</param>
/// <param name="Class">For a method: the "Ns.Class.*" name of its class; null for a class.</param>
public sealed record TestMark(int Line, string Name, string? Class)
{
    public bool IsClass => Class is null;
    public string ShortName => IsClass ? Name[..^2].Split('.').Last() : Name.Split('.').Last();
}

/// <summary>
/// Finds tests from the syntax tree only (no compilation): names in the form the test runner filters take,
/// "Ns.Class.Method" for a method and "Ns.Class.*" for a whole class.
/// </summary>
public static class TestDiscovery
{
    /// <summary>xUnit, NUnit, MSTest and TUnit test attributes (the "Attribute" suffix removed).</summary>
    static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod", "TestTemplate",
    };

    /// <summary>The test method (or the class, when the caret is outside a method) enclosing <paramref name="position"/>.</summary>
    public static string? TestNameAt(string text, int position)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var node = tree.GetRoot().FindToken(Math.Min(position, text.Length)).Parent;
        var method = node?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var type = node?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null) return null;
        return method is null ? FullName(type) + ".*" : FullName(type) + "." + method.Identifier.ValueText;
    }

    /// <summary>
    /// "Ns.Class.*" for every class of the file that declares at least one test method. Abstract classes are skipped:
    /// their tests run through the derived classes.
    /// </summary>
    public static IReadOnlyList<string> TestClassesIn(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        return root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => !c.Modifiers.Any(SyntaxKind.AbstractKeyword) && c.Members.OfType<MethodDeclarationSyntax>().Any(IsTestMethod))
            .Select(c => FullName(c) + ".*")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Gutter marks: every test class (name "Ns.Class.*") and test method ("Ns.Class.Method") with the 1-based line of
    /// its name. Abstract classes are left out, like in <see cref="TestClassesIn"/>.
    /// </summary>
    public static IReadOnlyList<TestMark> Marks(string text)
    {
        if (!MayContainTests(text)) return [];
        var tree = CSharpSyntaxTree.ParseText(text);
        var marks = new List<TestMark>();
        foreach (var type in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (type.Modifiers.Any(SyntaxKind.AbstractKeyword)) continue;
            var methods = type.Members.OfType<MethodDeclarationSyntax>().Where(IsTestMethod).ToList();
            if (methods.Count == 0) continue;
            var name = FullName(type);
            marks.Add(new TestMark(LineOf(type.Identifier), name + ".*", null));
            foreach (var method in methods)
                marks.Add(new TestMark(LineOf(method.Identifier), name + "." + method.Identifier.ValueText, name + ".*"));
        }
        return marks;

        static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }

    /// <summary>Cheap text check before parsing: a file without any test attribute name has no tests.</summary>
    public static bool MayContainTests(string text) =>
        TestAttributes.Any(a => text.Contains(a, StringComparison.Ordinal)) || text.Contains("Fact", StringComparison.Ordinal)
        || text.Contains("Theory", StringComparison.Ordinal);

    /// <summary>
    /// Every test method of a test project, from the syntax of its C# files (no build): the Test Explorer tree before
    /// the first run. <see cref="DiscoveredTest.Class"/> is "Ns.Class" (without ".*").
    /// </summary>
    public static IReadOnlyList<DiscoveredTest> DiscoverProject(string projectPath, CancellationToken ct = default)
    {
        var result = new List<DiscoveredTest>();
        foreach (var file in CSharpFiles(Path.GetDirectoryName(Path.GetFullPath(projectPath))!))
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var mark in Marks(text))
                if (mark.Class is { } cls) result.Add(new DiscoveredTest(projectPath, file, mark.Line, mark.Name, cls[..^2]));
        }
        return result;
    }

    /// <summary>Test classes of every C# file under <paramref name="folder"/> (bin, obj and hidden folders skipped).</summary>
    public static IReadOnlyList<(string File, IReadOnlyList<string> Classes)> TestClassesUnder(string folder, CancellationToken ct = default)
    {
        var result = new List<(string, IReadOnlyList<string>)>();
        foreach (var file in CSharpFiles(folder))
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            // Cheap filter before parsing: no test attribute name, no tests.
            if (!MayContainTests(text)) continue;
            var classes = TestClassesIn(text);
            if (classes.Count > 0) result.Add((file, classes));
        }
        return result;
    }

    static IEnumerable<string> CSharpFiles(string folder)
    {
        var pending = new Stack<string>([folder]);
        while (pending.TryPop(out var dir))
        {
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir, "*.cs");
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files.Order(StringComparer.Ordinal)) yield return f;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.')) continue;
                pending.Push(d);
            }
        }
    }

    public static bool IsTestMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Any(a =>
        {
            var name = a.Name switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax q => q.Name.Identifier.ValueText,
                SimpleNameSyntax s => s.Identifier.ValueText,
                _ => a.Name.ToString(),
            };
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^"Attribute".Length];
            // Custom xUnit attributes (SkippableFact, IntegrationTheory, …) derive from Fact / Theory.
            return TestAttributes.Contains(name) || name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal);
        });

    static string FullName(TypeDeclarationSyntax type)
    {
        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse();
        var nested = type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).Reverse();
        return string.Join('.', ns.Concat(nested));
    }
}
