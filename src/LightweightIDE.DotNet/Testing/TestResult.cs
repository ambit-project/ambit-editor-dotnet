namespace LightweightIDE.DotNet.Testing;

public enum TestOutcome { Passed, Failed, Skipped, Other }

/// <param name="Method">The test method's name from the test definition (TRX TestMethod/@name), when known: a custom
/// display name or theory arguments in <paramref name="FullName"/> do not change <see cref="Key"/> then.</param>
public sealed record TestCaseResult(
    string FullName,
    string ClassName,
    string Name,
    TestOutcome Outcome,
    TimeSpan Duration,
    string? ErrorMessage,
    string? StackTrace,
    string? SourceFile,
    int SourceLine,
    string? Method = null)
{
    /// <summary>"Ns.Class.Method" (nested classes with '.', theory cases folded): matches the names of the test tree.</summary>
    public string Key => Normalize(Method is { Length: > 0 } && ClassName.Length > 0 ? ClassName + "." + Method : FullName);

    /// <summary>"Ns.Outer+Inner.Method(x: 1)" → "Ns.Outer.Inner.Method".</summary>
    public static string Normalize(string name) => (name.IndexOf('(') is > 0 and var i ? name[..i] : name).Replace('+', '.');
}

public sealed record TestRunResult(int ExitCode, bool Cancelled, IReadOnlyList<TestCaseResult> Tests, TimeSpan Duration, string Mode)
{
    public int Passed => Tests.Count(t => t.Outcome == TestOutcome.Passed);
    public int Failed => Tests.Count(t => t.Outcome == TestOutcome.Failed);
    public int Skipped => Tests.Count(t => t.Outcome == TestOutcome.Skipped);
}
