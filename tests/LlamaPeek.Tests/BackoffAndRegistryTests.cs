using LlamaPeek.Core;

namespace LlamaPeek.Tests;

public class BackoffTests
{
    [Fact]
    public async Task Unreachable_Server_MarkedAndBacksOff()
    {
        // Port 1: connect refused immediately.
        var settings = new AppSettings
        {
            Servers = [new ServerConfig("dead", "http://127.0.0.1:1")],
            PollActiveMs = 100, PollIdleMs = 100, RefreshMs = 100,
        };
        await using var m = new StatsMonitor(settings, () => null);
        m.Start();

        var snap = await TestUtil.AwaitAsync(m, s => s.AnyUnreachable, timeoutMs: 5_000);
        Assert.True(snap.AnyUnreachable);
        Assert.Null(snap.PpRate);
        Assert.Null(snap.TgRate);
    }
}

public class RegistryTests
{
    [Fact]
    public void Settings_RoundTrip()
    {
        var s = new AppSettings
        {
            Servers =
            [
                new ServerConfig("strata", "http://127.0.0.1:11436", ServerKind.Auto, true),
                new ServerConfig("vision", "http://127.0.0.1:11437", ServerKind.LlamaCpp),
            ],
            PollActiveMs = 500, PollIdleMs = 2000, GraceMs = 2500,
            RefreshMs = 500, Smoothing = 0.4, LaunchAtStartup = false,
        };
        RegistryStore.Save(s);
        var loaded = RegistryStore.Load();
        Assert.Equal(2, loaded.Servers.Count);
        Assert.Equal("strata", loaded.Servers[0].Name);
        Assert.Equal(ServerKind.Auto, loaded.Servers[0].Kind);
        Assert.True(loaded.Servers[0].RequiresModel);
        Assert.Equal(ServerKind.LlamaCpp, loaded.Servers[1].Kind);
        Assert.Equal(500, loaded.PollActiveMs);
        Assert.Equal(0.4, loaded.Smoothing, 3);
    }

    [Fact]
    public void ApiKey_DpapiRoundTrip()
    {
        RegistryStore.SaveApiKey("sk-test-12345");
        Assert.Equal("sk-test-12345", RegistryStore.LoadApiKey());
        RegistryStore.SaveApiKey(null);
        Assert.Null(RegistryStore.LoadApiKey());
    }
}
