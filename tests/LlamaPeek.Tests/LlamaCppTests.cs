using LlamaPeek.Core;

namespace LlamaPeek.Tests;

/// <summary>llama.cpp /slots behaviour: batch-jump rates and chunked-total suppression.</summary>
public class LlamaCppTests
{
    [Fact]
    public async Task BatchJump_RateMeasuredAcrossChanges()
    {
        var start = DateTime.UtcNow;
        // n_prompt_tokens_processed advances 2048 (one --batch-size) every 2 s;
        // total fixed => true rate 1024 t/s, percentage trustworthy.
        string Responder(string path) => path switch
        {
            "/health" => """{"status":"ok"}""",
            "/slots" => Slots(2048 * (int)((DateTime.UtcNow - start).TotalSeconds / 2), 16384),
            _ => "{}",
        };
        await using var stub = await StubServer.StartAsync(Responder);
        await using var m = new StatsMonitor(stub.Settings(), () => null);
        m.Start();

        var snap = await TestUtil.AwaitAsync(m, s => s.PpRate is >= 500);
        Assert.NotNull(snap.PpRate);
        Assert.InRange(snap.PpRate!.Value, 850, 1250);   // ~1024, not 8200 (poll-interval bug)
        Assert.Equal(1, snap.PpActive);
        Assert.NotNull(snap.PrefillPct);                 // stable denominator -> % shown
        Assert.InRange(snap.PrefillPct!.Value, 1, 99);
    }

    [Fact]
    public async Task ChunkedTotal_PercentageSuppressed()
    {
        var start = DateTime.UtcNow;
        // total stays just ahead of processed (chunked prefill) => % is a lie.
        string Responder(string path) => path switch
        {
            "/health" => """{"status":"ok"}""",
            "/slots" => Slots(
                proc: 2048 * (int)((DateTime.UtcNow - start).TotalSeconds / 2),
                total: 2048 * (int)((DateTime.UtcNow - start).TotalSeconds / 2) + 2048),
            _ => "{}",
        };
        await using var stub = await StubServer.StartAsync(Responder);
        await using var m = new StatsMonitor(stub.Settings(), () => null);
        m.Start();

        var snap = await TestUtil.AwaitAsync(m, s => s.PpRate is >= 500);
        Assert.NotNull(snap.PpRate);       // rate still measured
        Assert.Null(snap.PrefillPct);      // sticky: denominator grew -> suppressed
    }

    static string Slots(long proc, long total) =>
        $$"""
        [{"id":0,"is_processing":true,"id_task":7,"n_ctx":8192,
          "n_prompt_tokens":{{total}},"n_prompt_tokens_processed":{{proc}},
          "n_prompt_tokens_cache":0,
          "next_token":[{"id":0,"content":"x","n_decoded":0}]}]
        """;
}
