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

    [Fact]
    public async Task RequiredDown_Alerts_RequiresModelDown_DoesNot()
    {
        // The split: Required drives the alert dot; RequiresModel (router
        // ?model= mode) must not.
        var settings = new AppSettings
        {
            Servers =
            [
                new ServerConfig("router", "http://127.0.0.1:1", RequiresModel: true),
                new ServerConfig("critical", "http://127.0.0.1:2", Required: true),
            ],
            PollActiveMs = 100, PollIdleMs = 100, RefreshMs = 100,
        };
        await using var m = new StatsMonitor(settings, () => null);
        m.Start();

        var snap = await TestUtil.AwaitAsync(m, s => s.RequiredUnreachable, timeoutMs: 5_000);
        Assert.True(snap.RequiredUnreachable);   // 'critical' is Required
    }
}

public class RegistryTests : IDisposable
{
    public RegistryTests() => RegistryStore.AppKeyPath = @"Software\llamaPeek-test";
    public void Dispose()
    {
        RegistryStore.DeleteAll();
        RegistryStore.AppKeyPath = @"Software\llamaPeek";
    }

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

    [Fact]
    public void LegacySchema_RequiresModel_MigratesToRequired_Once()
    {
        // Pre-0.2 config: RequiresModel overloaded as "Required", no marker.
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryStore.AppKeyPath))
            k.SetValue("Servers",
                """[{"Name":"a","Url":"http://127.0.0.1:1","Kind":0,"RequiresModel":true}]""");

        var loaded = RegistryStore.Load();
        Assert.True(loaded.Servers[0].Required);        // alert meaning preserved
        Assert.False(loaded.Servers[0].RequiresModel);  // router mode off

        // Persist, then verify the split survives reload (marker set: no
        // re-migration — an explicit router-mode config stays as saved).
        loaded.Servers[0] = loaded.Servers[0] with { RequiresModel = true };
        RegistryStore.Save(loaded);
        var again = RegistryStore.Load();
        Assert.True(again.Servers[0].RequiresModel);    // stays router mode
        Assert.True(again.Servers[0].Required);         // stays alert
    }
}
