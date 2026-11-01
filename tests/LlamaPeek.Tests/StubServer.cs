using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using LlamaPeek.Core;

namespace LlamaPeek.Tests;

/// <summary>Kestrel stub: one responder function, path -> JSON body.</summary>
public sealed class StubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; }

    public static async Task<StubServer> StartAsync(Func<string, string> responder)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Run(async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(responder(ctx.Request.Path.Value ?? "/"));
        });
        await app.StartAsync();
        return new StubServer(app, app.Urls.First().TrimEnd('/'));
    }

    private StubServer(WebApplication app, string url) { _app = app; Url = url; }

    public AppSettings Settings(ServerKind kind = ServerKind.Auto,
                                int pollActiveMs = 200, int pollIdleMs = 500) =>
        new()
        {
            Servers = [new ServerConfig("stub", Url, kind)],
            PollActiveMs = pollActiveMs,
            PollIdleMs = pollIdleMs,
            GraceMs = 1000,
            RefreshMs = 100,
            Smoothing = 0.4,
        };

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

public static class TestUtil
{
    /// <summary>Polls a condition until true or the deadline; returns the last snapshot.</summary>
    public static async Task<Snapshot> AwaitAsync(StatsMonitor m, Func<Snapshot, bool> cond,
                                                  int timeoutMs = 15_000)
    {
        Snapshot snap = m.Current();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            snap = m.Current();
            if (cond(snap)) return snap;
            await Task.Delay(100);
        }
        return snap;
    }
}
