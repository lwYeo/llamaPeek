using LlamaPeek.Core;

namespace LlamaPeek.Tests;

/// <summary>strata /metrics live behaviour: reading state, chunked prompt_read,
/// server prefill rate, and TG from generated deltas.</summary>
public class StrataTests
{
    private readonly DateTime _start = DateTime.UtcNow;

    [Fact]
    public async Task Prefill_PctAndServerRate()
    {
        string Responder(string path) => path switch
        {
            "/health" => """{"status":"ok","service":"strata"}""",
            "/metrics" => Metrics(),
            _ => "{}",
        };
        await using var stub = await StubServer.StartAsync(Responder);
        await using var m = new StatsMonitor(stub.Settings(), () => null);
        m.Start();

        var snap = await TestUtil.AwaitAsync(m, s => s.PpRate is > 0 && s.PrefillPct is not null);
        Assert.Equal(1, snap.PpActive);
        Assert.InRange(snap.PpRate!.Value, 1900, 2100);   // server prefill_tok_s_mean = 2000
        Assert.InRange(snap.PrefillPct!.Value, 1, 99);
        Assert.NotNull(snap.PrefillProcessed);
        Assert.Equal(32768, snap.PrefillTotal);
    }

    [Fact]
    public async Task Decode_TgFromGeneratedDeltas()
    {
        string Responder(string path) => path switch
        {
            "/health" => """{"status":"ok","service":"strata"}""",
            "/metrics" => Metrics(),
            _ => "{}",
        };
        await using var stub = await StubServer.StartAsync(Responder);
        await using var m = new StatsMonitor(stub.Settings(), () => null);
        m.Start();

        // Decode starts 3 s in; generated advances 10 tokens every 100 ms => ~100 t/s.
        var snap = await TestUtil.AwaitAsync(m, s => s.TgRate is > 30, timeoutMs: 20_000);
        Assert.Equal(1, snap.TgActive);
        Assert.InRange(snap.TgRate!.Value, 60, 160);
    }

    private string Metrics()
    {
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        if (t < 3)
            return $$"""
            {"live":{"state":"reading","queued":0,
              "prompt_read":{{8192 * (int)(t / 1.5) + 8192}},"prompt_total":32768,
              "prompt_tokens":32768,"generated":null,"elapsed_s":{{t:F1}},
              "tok_s":null,"prefill_tok_s_mean":2000.0,"slots":[]} }
            """;
        long gen = 10 * (long)((t - 3) / 0.1);
        return $$"""
        {"live":{"state":"generating","queued":0,
          "prompt_read":32768,"prompt_total":32768,"prompt_tokens":32768,
          "generated":{{gen}},"elapsed_s":{{t:F1}},"tok_s":100.0,
          "prefill_tok_s_mean":2000.0,"slots":[]} }
        """;
    }
}
