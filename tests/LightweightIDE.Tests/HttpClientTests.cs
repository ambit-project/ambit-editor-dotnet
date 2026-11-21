using System.Net;
using System.Text;
using LightweightIDE.DotNet.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

public class HttpFileTests
{
    const string Sample = """
        @baseURL = http://localhost:5000

        ### Login Platform User
        # @name login
        POST {{baseURL}}/api/auth/login
        Content-Type: application/json

        {
            "email": "j.smith@hr-agency.com"
        }

        ###
        @token = {{login.response.body.$.token}}

        ### posting
        # @name postOne
        GET {{baseURL}}/api/recruitment/job-posting?search=Spring
        Authorization: Bearer {{token}}


        ###
        @jobPostId1 = {{postOne.response.body.$.content[0].id}}
        ###
        DELETE {{baseURL}}/api/items/{{jobPostId1}}
            ?force=true
        """;

    [Fact]
    public void Parse_RequestsVariablesNamesHeadersAndBodies()
    {
        var file = HttpFile.Parse(Sample);
        Assert.Equal(3, file.Requests.Count);
        Assert.Equal(["baseURL", "token", "jobPostId1"], file.Variables.Select(v => v.Name));

        var login = file.Requests[0];
        Assert.Equal("login", login.Name);
        Assert.Equal("Login Platform User", login.Title);
        Assert.Equal("POST", login.Method);
        Assert.Equal("{{baseURL}}/api/auth/login", login.Url);
        Assert.Equal(("Content-Type", "application/json"), Assert.Single(login.Headers));
        Assert.Contains("\"email\"", login.Body);

        var get = file.Requests[1];
        Assert.Equal("postOne", get.Name);
        Assert.Null(get.Body); // blank lines only
        Assert.Equal(("Authorization", "Bearer {{token}}"), Assert.Single(get.Headers));

        Assert.Equal("{{baseURL}}/api/items/{{jobPostId1}}?force=true", file.Requests[2].Url);
        Assert.Same(get, file.RequestAt(get.Line + 1)); // the caret on a header line
        Assert.Same(login, file.Find("login"));
    }

    [Theory]
    [InlineData("$.token", "abc")]
    [InlineData("token", "abc")]
    [InlineData("$.content[1].id", "7")]
    [InlineData("content[0].Name", "first")] // case-insensitive fallback
    [InlineData("$.content[5].id", null)]
    public void SelectJson(string path, string? expected)
    {
        const string body = """{"token":"abc","content":[{"id":3,"name":"first"},{"id":7}]}""";
        Assert.Equal(expected, HttpVariableResolver.SelectJson(body, path));
    }

    [Fact]
    public void SelectJson_RootArray()
    {
        Assert.Equal("x1", HttpVariableResolver.SelectJson("""[{"jobApplicationId":"x1"}]""", "$.[0].jobApplicationId"));
    }
}

public class HttpClientServiceTests
{
    [Fact]
    public async Task Send_ResolvesVariables_SendsDependencyFirst_AndKeepsCookies()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = Random.Shared.Next(20000, 40000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var seen = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                var request = context.Request;
                var body = await new StreamReader(request.InputStream).ReadToEndAsync(ct);
                lock (seen) seen.Add($"{request.HttpMethod} {request.Url!.PathAndQuery} auth={request.Headers["Authorization"]} cookie={request.Headers["Cookie"]} body={body}");
                var reply = request.Url.AbsolutePath == "/login"
                    ? """{"token":"t-123"}"""
                    : """{"content":[{"id":42}]}""";
                if (request.Url.AbsolutePath == "/login") context.Response.AppendHeader("Set-Cookie", "session=s1; Path=/");
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(reply);
                await context.Response.OutputStream.WriteAsync(bytes, ct);
                context.Response.Close();
            }
        }, ct);

        var text = $$$"""
            @base = http://localhost:{{{port}}}
            ### login
            # @name login
            POST {{base}}/login
            Content-Type: application/json

            {"user":"{{$randomInt 5 6}}"}

            ###
            @token = {{login.response.body.$.token}}
            ### items
            GET {{base}}/items?q=a
            Authorization: Bearer {{token}}
            """;
        using var service = new HttpClientService(NullLogger<HttpClientService>.Instance);
        var line = text.Split('\n').ToList().FindIndex(l => l.StartsWith("GET", StringComparison.Ordinal)) + 1;
        var exchange = await service.SendAsync("/tmp/http/test.http", text, line, ct);
        await server;

        Assert.Null(exchange.Error);
        Assert.Equal(200, exchange.Status);
        Assert.True(exchange.IsJson);
        Assert.Equal("42", HttpVariableResolver.SelectJson(exchange.ResponseBody!, "$.content[0].id"));
        Assert.Equal(2, seen.Count);
        Assert.Equal("POST /login auth= cookie= body={\"user\":\"5\"}", seen[0]);       // sent first, automatically
        Assert.Equal("GET /items?q=a auth=Bearer t-123 cookie=session=s1 body=", seen[1]); // token and cookie reused
    }

    [Fact]
    public async Task UnknownVariable_IsReported()
    {
        using var service = new HttpClientService(NullLogger<HttpClientService>.Instance);
        var error = await Assert.ThrowsAsync<HttpVariableException>(() =>
            service.SendAsync("/tmp/http/x.http", "GET {{nope}}/x", 1, TestContext.Current.CancellationToken));
        Assert.Contains("nope", error.Message);
    }
}
