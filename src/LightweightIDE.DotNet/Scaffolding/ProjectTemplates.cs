namespace LightweightIDE.DotNet.Scaffolding;

/// <summary>A dotnet new template the IDE offers. <see cref="Folder"/> is where new projects go (src or tests).</summary>
public sealed record ProjectTemplate(string ShortName, string Name, string Folder, string Suffix, bool IsTest);

/// <summary>Only web and backend project types (see the product scope): no desktop, mobile or MAUI templates.</summary>
public static class ProjectTemplates
{
    public static readonly IReadOnlyList<ProjectTemplate> All =
    [
        new("webapi", "ASP.NET Core Web API", "src", ".Api", false),
        new("web", "ASP.NET Core Empty", "src", ".Web", false),
        new("blazor", "Blazor Web App", "src", ".Web", false),
        new("grpc", "gRPC Service", "src", ".Grpc", false),
        new("worker", "Worker Service", "src", ".Worker", false),
        new("classlib", "Class Library", "src", ".Core", false),
        new("xunit", "xUnit Test Project", "tests", ".Tests", true),
        new("mstest", "MSTest Test Project", "tests", ".Tests", true),
        new("nunit", "NUnit Test Project", "tests", ".Tests", true),
    ];

    public static ProjectTemplate Get(string shortName) => All.First(t => t.ShortName == shortName);
}
