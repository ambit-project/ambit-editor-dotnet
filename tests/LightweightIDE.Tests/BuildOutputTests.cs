using LightweightIDE.DotNet.Build;

namespace LightweightIDE.Tests;

public class BuildOutputTests
{
    [Fact]
    public void ParsesCompilerErrorWithProject()
    {
        var d = MsBuildOutputParser.TryParse("/src/App/Program.cs(12,5): error CS1002: ; expected [/src/App/App.csproj]");
        Assert.NotNull(d);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("CS1002", d.Code);
        Assert.Equal("; expected", d.Message);
        Assert.Equal("/src/App/Program.cs", d.FilePath);
        Assert.Equal(12, d.Line);
        Assert.Equal(5, d.Column);
        Assert.Equal("/src/App/App.csproj", d.Project);
    }

    [Fact]
    public void ParsesWarningWithRange()
    {
        var d = MsBuildOutputParser.TryParse("  /src/A.cs(3,10,3,20): warning CS8618: Non-nullable property 'Name' must contain a non-null value. [/src/A.csproj]");
        Assert.Equal(DiagnosticSeverity.Warning, d!.Severity);
        Assert.Equal(3, d.Line);
        Assert.Equal(10, d.Column);
    }

    [Fact]
    public void ParsesSdkErrorWithoutLocation()
    {
        var d = MsBuildOutputParser.TryParse("/usr/share/dotnet/sdk/11.0.100/Microsoft.Common.targets : error NETSDK1045: The current .NET SDK does not support targeting .NET 12.0. [/src/A.csproj]");
        Assert.Equal("NETSDK1045", d!.Code);
        Assert.Equal(0, d.Line);
    }

    [Theory]
    [InlineData("Build succeeded.")]
    [InlineData("  Determining projects to restore...")]
    [InlineData("    0 Error(s)")]
    public void IgnoresOrdinaryLines(string line) => Assert.Null(MsBuildOutputParser.TryParse(line));

    [Fact]
    public void BuildArguments_AlwaysPassConfiguration()
    {
        var args = BuildService.Arguments(new BuildRequest(BuildAction.Rebuild, "/s/A.slnx", "Release"));
        Assert.Equal(["build", "/s/A.slnx", "--no-incremental", "--configuration", "Release", "-nologo", "-tl:off", "-consoleLoggerParameters:NoSummary"], args);
    }
}
