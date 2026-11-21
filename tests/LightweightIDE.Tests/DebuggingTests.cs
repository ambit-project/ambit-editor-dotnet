using LightweightIDE.Editor;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using LightweightIDE.DotNet.Debugging;

namespace LightweightIDE.Tests;

public class DapClientTests
{
    static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
    }

    [Fact]
    public async Task ReadMessage_ParsesFramesInSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream([.. Frame("""{"type":"event","event":"initialized"}"""), .. Frame("""{"type":"event","event":"ąę"}""")]);
        Assert.Equal("initialized", (string?)(await DapClient.ReadMessageAsync(stream, ct))!["event"]);
        Assert.Equal("ąę", (string?)(await DapClient.ReadMessageAsync(stream, ct))!["event"]); // length is in bytes, not chars
        Assert.Null(await DapClient.ReadMessageAsync(stream, ct));
    }

    [Fact]
    public async Task Request_GetsItsResponse_AndEventsAreRaised()
    {
        var ct = TestContext.Current.CancellationToken;
        // client → adapter and adapter → client pipes
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);

        await using var client = new DapClient(clientIn, toAdapter);
        var events = new List<string>();
        client.EventReceived += (name, _) => { lock (events) events.Add(name); };

        // Fake adapter: answer the request, then send an event.
        var adapter = Task.Run(async () =>
        {
            var request = await DapClient.ReadMessageAsync(adapterIn, ct);
            var seq = (int)request!["seq"]!;
            await fromAdapter.WriteAsync(Frame("""{"seq":1,"type":"event","event":"output","body":{"output":"hi"}}"""), ct);
            await fromAdapter.WriteAsync(Frame($$$"""{"seq":2,"type":"response","request_seq":{{{seq}}},"success":true,"command":"threads","body":{"threads":[{"id":7,"name":"Main"}]}}"""), ct);
            var failing = await DapClient.ReadMessageAsync(adapterIn, ct);
            await fromAdapter.WriteAsync(Frame($$$"""{"seq":3,"type":"response","request_seq":{{{(int)failing!["seq"]!}}},"success":false,"command":"evaluate","message":"no frame"}"""), ct);
        }, ct);

        var body = await client.RequestAsync("threads", null, ct);
        Assert.Equal(7, (int)body!["threads"]![0]!["id"]!);
        var error = await Assert.ThrowsAsync<DapException>(() => client.RequestAsync("evaluate", new JsonObject { ["expression"] = "x" }, ct));
        Assert.Contains("no frame", error.Message);
        await adapter;
        lock (events) Assert.Contains("output", events);
    }
}

public class DebugTargetsTests
{
    [Fact]
    public void LaunchProfiles_UseProjectProfiles_WithUrlAndEnvironment()
    {
        using var dir = new TempDir();
        var project = dir.Write("src/Api/Api.csproj", "<Project />");
        dir.Write("src/Api/Properties/launchSettings.json", """
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "launchBrowser": true,
                  "launchUrl": "swagger",
                  "applicationUrl": "http://localhost:5080",
                  "commandLineArgs": "--seed demo",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "FEATURE_X": "1" }
                },
                "IIS Express": { "commandName": "IISExpress" }
              }
            }
            """);
        var profile = Assert.Single(DebugTargets.ReadLaunchProfiles(project));
        Assert.Equal("http", profile.Name);
        Assert.Equal("http://localhost:5080", profile.Environment["ASPNETCORE_URLS"]);
        Assert.Equal("1", profile.Environment["FEATURE_X"]);
        Assert.Equal(["--seed", "demo"], profile.Arguments);
        Assert.True(profile.LaunchBrowser);
        Assert.Equal("swagger", profile.LaunchUrl);
    }

    [Fact]
    public void ParseProperties_ReadsMsbuildJson()
    {
        var properties = DebugTargets.ParseProperties("""
            {
              "Properties": {
                "TargetPath": "/src/Api/bin/Debug/net10.0/Api.dll",
                "OutputType": "Exe",
                "TargetFrameworks": ""
              }
            }
            """);
        Assert.Equal("/src/Api/bin/Debug/net10.0/Api.dll", properties["TargetPath"]);
        Assert.Equal("Exe", properties["OutputType"]);
    }
}

/// <summary>Real debugging with netcoredbg (skipped when it is not installed).</summary>
public class DebugSessionIntegrationTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task StopsAtBreakpoint_ReadsLocals_AndContinues(int sdkMajor)
    {
        var ct = TestContext.Current.CancellationToken;
        var adapter = DebugTargets.FindAdapter();
        Assert.SkipWhen(adapter is null, "netcoredbg is not installed.");
        var sdk = (await LightweightIDE.DotNet.Scaffolding.SdkCatalog.ListAsync(ct)).FirstOrDefault(s => s.Major == sdkMajor && !s.IsPreview);
        Assert.SkipWhen(sdk is null, $"SDK {sdkMajor} is not installed.");

        using var dir = new TempDir();
        LightweightIDE.DotNet.Scaffolding.GlobalJson.Write(Path.Combine(dir.Path, "global.json"), sdk.Version, "disable");
        dir.Write("App/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net{sdkMajor}.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        var program = dir.Write("App/Program.cs", """
            var total = 0; var name = "abc"; var when = new DateTime(2026, 11, 20);
            for (var i = 1; i <= 3; i++)
            {
                total += i;
            }
            Console.WriteLine($"total={total}");
            """);
        var project = Path.Combine(dir.Path, "App", "App.csproj");
        var log = new List<string>();
        var exit = await LightweightIDE.DotNet.Build.ProcessRunner.RunAsync("dotnet", ["build", project, "-nologo", "-v", "q"], dir.Path,
            (l, _) => { lock (log) log.Add(l); }, cancellationToken: ct);
        Assert.True(exit == 0, string.Join("\n", log));

        var launch = await DebugTargets.ForProjectAsync(project, "Debug", null, ct);
        var stopped = new TaskCompletionSource<StoppedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new System.Text.StringBuilder();
        var breakpoints = new Dictionary<string, IReadOnlyList<int>> { [program] = [4] };
        await using var session = await DebugSession.StartAsync(adapter, launch, breakpoints, (_, text) => { lock (output) output.Append(text); }, ct);
        session.Stopped += s => stopped.TrySetResult(s);
        session.Terminated += _ => terminated.TrySetResult();

        var stop = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        Assert.Equal("breakpoint", stop.Reason);
        var frames = await session.StackTraceAsync(stop.ThreadId);
        Assert.Equal(program, frames[0].Path);
        Assert.Equal(4, frames[0].Line);

        var scopes = await session.ScopesAsync(frames[0].Id);
        var locals = await session.VariablesAsync(scopes[0].Reference);
        Assert.Contains(locals, v => v.Name == "total" && v.Value == "0");
        Assert.Contains(locals, v => v.Name == "i" && v.Value == "1");
        Assert.Equal("10", (await session.EvaluateAsync("i * 10", frames[0].Id)).Value);
        Assert.Equal("2026", (await session.EvaluateAsync("when.Year", frames[0].Id)).Value);
        Assert.Equal("\"ABC\"", (await session.EvaluateAsync("name.ToUpper()", frames[0].Id)).Value);
        Assert.Equal("3", (await session.EvaluateAsync("name.Length", frames[0].Id)).Value); // netcoredbg workaround

        // Remove the breakpoint and let the program finish.
        await session.SetBreakpointsAsync(program, [], ct);
        await session.ContinueAsync(stop.ThreadId);
        await terminated.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        lock (output) Assert.Contains("total=6", output.ToString());
    }

    [Fact]
    public async Task ConditionalBreakpoint_StopsOnlyWhenTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var adapter = DebugTargets.FindAdapter();
        Assert.SkipWhen(adapter is null, "netcoredbg is not installed.");
        var sdk = (await LightweightIDE.DotNet.Scaffolding.SdkCatalog.ListAsync(ct)).FirstOrDefault(s => s.Major == 10 && !s.IsPreview);
        Assert.SkipWhen(sdk is null, "SDK 10 is not installed.");

        using var dir = new TempDir();
        LightweightIDE.DotNet.Scaffolding.GlobalJson.Write(Path.Combine(dir.Path, "global.json"), sdk.Version, "disable");
        dir.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);
        var program = dir.Write("App/Program.cs", """
            var total = 0;
            for (var i = 1; i <= 5; i++)
            {
                total += i;
            }
            Console.WriteLine($"total={total}");
            """);
        var project = Path.Combine(dir.Path, "App", "App.csproj");
        var exit = await LightweightIDE.DotNet.Build.ProcessRunner.RunAsync("dotnet", ["build", project, "-nologo", "-v", "q"], dir.Path, (_, _) => { }, cancellationToken: ct);
        Assert.Equal(0, exit);

        var launch = await DebugTargets.ForProjectAsync(project, "Debug", null, ct);
        var stops = System.Threading.Channels.Channel.CreateUnbounded<StoppedInfo>();
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var breakpoints = new Dictionary<string, IReadOnlyList<int>> { [program] = [4] };
        await using var session = await DebugSession.StartAsync(adapter, launch, breakpoints, (_, _) => { }, ct,
            (path, line) => path == program && line == 4 ? "i == 3" : null);
        Assert.True(session.SupportsConditions);
        session.Stopped += s => stops.Writer.TryWrite(s);
        session.Terminated += _ => terminated.TrySetResult();

        var stop = await stops.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), ct);
        var frames = await session.StackTraceAsync(stop.ThreadId);
        Assert.Equal("3", (await session.EvaluateAsync("i", frames[0].Id)).Value);
        await session.ContinueAsync(stop.ThreadId);
        await terminated.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        Assert.False(stops.Reader.TryRead(out _)); // i == 1, 2, 4, 5 did not stop
    }
}

public class BreakpointOptionsTests
{
    [Fact]
    public void HitRules()
    {
        Assert.True(new BreakpointOptions().Matches(1));
        Assert.Equal([3], Enumerable.Range(1, 6).Where(new BreakpointOptions(Hit: HitRule.Equal, HitCount: 3).Matches));
        Assert.Equal([3, 4, 5, 6], Enumerable.Range(1, 6).Where(new BreakpointOptions(Hit: HitRule.AtLeast, HitCount: 3).Matches));
        Assert.Equal([2, 4, 6], Enumerable.Range(1, 6).Where(new BreakpointOptions(Hit: HitRule.MultipleOf, HitCount: 2).Matches));
        Assert.True(new BreakpointOptions(Hit: HitRule.Always, HitCount: 7).IsDefault);
        Assert.False(new BreakpointOptions(LogMessage: "x").IsDefault);
    }

    [Fact]
    public void ParseMessage_SplitsExpressionsAndKeepsLiteralBraces()
    {
        var parts = BreakpointOptions.ParseMessage("total = {order.Total}, {{raw}} {  }end");

        Assert.Equal([("total = ", false), ("order.Total", true), (", {raw} ", false), ("end", false)], parts);
    }

    [Fact]
    public void Store_KeepsOptionsWhenLinesMove_AndDropsThemWithTheBreakpoint()
    {
        var store = new LightweightIDE.Editor.BreakpointStore();
        store.Toggle("/a.cs", 5);
        store.SetOptions("/a.cs", 10, new BreakpointOptions("x > 1"));
        Assert.Equal([5, 10], store.For("/a.cs"));

        store.Set("/a.cs", [7, 12]); // two lines inserted above
        Assert.Equal("x > 1", store.OptionsFor("/a.cs", 12).Condition);
        Assert.True(store.OptionsFor("/a.cs", 7).IsDefault);

        var copy = new LightweightIDE.Editor.BreakpointStore();
        copy.Import(store.Export(), store.ExportOptions());
        Assert.Equal("x > 1", copy.OptionsFor("/a.cs", 12).Condition);

        store.Toggle("/a.cs", 12);
        store.Toggle("/a.cs", 12);
        Assert.True(store.OptionsFor("/a.cs", 12).IsDefault);

        store.SetOptions("/a.cs", 7, new BreakpointOptions(LogMessage: "hi"));
        store.Rename("/a.cs", "/b.cs");
        Assert.True(store.OptionsFor("/b.cs", 7).IsLogpoint);
        store.SetOptions("/b.cs", 7, BreakpointOptions.Default);
        Assert.Empty(store.ExportOptions());
    }
}

public class BreakpointStoreTests
{
    [Fact]
    public void Toggle_Set_Rename_AndRoundTrip()
    {
        var store = new LightweightIDE.Editor.BreakpointStore();
        var changed = new List<string>();
        store.Changed += changed.Add;

        Assert.True(store.Toggle("/src/A.cs", 10));
        Assert.True(store.Toggle("/src/A.cs", 3));
        Assert.Equal([3, 10], store.For("/src/A.cs"));
        Assert.False(store.Toggle("/src/A.cs", 10));
        Assert.Equal([3], store.For("/src/A.cs"));

        changed.Clear();
        store.Set("/src/A.cs", [3]);
        Assert.Empty(changed); // no change, no event
        store.Set("/src/A.cs", [4]);
        Assert.Equal(["/src/A.cs"], changed);

        store.Rename("/src", "/lib");
        Assert.Equal([4], store.For("/lib/A.cs"));
        Assert.Empty(store.For("/src/A.cs"));

        var copy = new LightweightIDE.Editor.BreakpointStore();
        copy.Import(store.Export());
        Assert.Equal(store.All().Keys, copy.All().Keys);
    }
}
