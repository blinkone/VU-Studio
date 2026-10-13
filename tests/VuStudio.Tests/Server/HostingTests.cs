using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using VuStudio.Server.Api;
using VuStudio.Server.Hosting;
using VuStudio.Tests.Session;

namespace VuStudio.Tests.Server;

public class RuntimeOptionsTests
{
    private static Func<string, string?> Env(string? port) => name => name == "VUSTUDIO_PORT" ? port : null;

    [Fact]
    public void Port_comes_from_the_command_line_then_from_VUSTUDIO_PORT()
    {
        Assert.Equal(6001, RuntimeOptions.Parse(["--port", "6001"], Env("7002")).Port);
        Assert.Equal(6001, RuntimeOptions.Parse(["--port=6001"], Env("7002")).Port);
        Assert.Equal(7002, RuntimeOptions.Parse([], Env("7002")).Port);
        Assert.Equal(7002, RuntimeOptions.Parse(["--port", "nope"], Env("7002")).Port);
        Assert.Null(RuntimeOptions.Parse([], Env(null)).Port);
        Assert.Null(RuntimeOptions.Parse([], Env("70000")).Port);
        Assert.Null(RuntimeOptions.Parse([], Env("abc")).Port);
    }
}

public class UnknownApiTests
{
    [Fact]
    public async Task Unknown_api_paths_are_404_not_the_spa()
    {
        await using var host = await TestHost.StartAsync(app =>
        {
            app.MapGet("/api/info", () => "info");
            app.MapUnknownApi();
            app.MapFallback(() => Results.Content("<!doctype html>", "text/html"));
        });
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/a/b/c", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/nope", null, ct)).StatusCode);
        Assert.Equal("info", await client.GetStringAsync("/api/info", ct));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/history", ct)).StatusCode);
    }
}
