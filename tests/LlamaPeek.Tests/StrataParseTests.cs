using LlamaPeek.Core;

namespace LlamaPeek.Tests;

/// <summary>StrataSource.Parse pinned to payloads captured live from serve/server.py
/// (slots_view emits "slot" not "id", and NO per-slot prompt_total/prefill_tok_s_mean;
/// top-level fields only exist when slots[] is empty).</summary>
public class StrataParseTests
{
    [Fact]
    public void TopLevel_Reading_CapturesProgressAndServerRate()
    {
        // Live capture: 20k-token prompt on --prefill auto:16384.
        var r = StrataSource.Parse("""
        {"live":{"state":"reading","phase":"reading the prompt","queued":0,
          "prompt_read":10240,"prompt_total":20078,"prompt_tokens":20078,
          "generated":0,"prefill_tok_s_mean":1267.1,"slots":[]}}
        """);
        var top = Assert.Single(r);
        Assert.True(top.IsTopLevel);
        Assert.Equal(Phase.Prefill, top.State);
        Assert.Equal(10240, top.PromptRead);
        Assert.Equal(20078, top.PromptTotalAbs);
        Assert.Equal(1267.1, top.ServerPrefillRate);
    }

    [Fact]
    public void ShortPrompt_NoProgressFields_PctStaysUnavailable()
    {
        // Live capture: 9k-token prompt (under the first PP boundary) — the engine
        // reports no progress at all for the entire read.
        var r = StrataSource.Parse("""
        {"live":{"state":"reading","queued":0,"prompt_read":null,"prompt_total":null,
          "prompt_tokens":9078,"generated":0,"prefill_tok_s_mean":null,"slots":[]}}
        """);
        var top = Assert.Single(r);
        Assert.Equal(Phase.Prefill, top.State);
        Assert.Null(top.PromptRead);
        Assert.Null(top.PromptTotalAbs);   // TrustworthyPct gate keeps % blank
    }

    [Fact]
    public void BatchSlots_SlotKeyAndShape()
    {
        // Live shape of slots_view(): "slot" key, per-slot prompt_read only while
        // reading, no prompt_total, no prefill_tok_s_mean, no top-level entry.
        var r = StrataSource.Parse("""
        {"live":{"state":"generating","queued":0,"parallel":2,
          "slots":[
            {"slot":0,"state":"reading","prompt_tokens":5000,
             "prompt_read":4096,"generated":0,"elapsed_s":3.2,"tok_s":null},
            {"slot":1,"state":"idle","held_tokens":0},
            {"slot":2,"state":"decoding","prompt_tokens":1200,
             "prompt_read":null,"generated":45,"elapsed_s":8.1,"tok_s":31.2}]}}
        """);
        Assert.Equal(3, r.Count);
        Assert.All(r, x => Assert.False(x.IsTopLevel));

        Assert.Equal(0, r[0].SlotId);
        Assert.Equal(Phase.Prefill, r[0].State);
        Assert.Equal(4096, r[0].PromptRead);
        Assert.Null(r[0].PromptTotalAbs);        // server never emits it per slot

        Assert.Equal(1, r[1].SlotId);
        Assert.Equal(Phase.Idle, r[1].State);

        Assert.Equal(2, r[2].SlotId);
        Assert.Equal(Phase.Decode, r[2].State);
        Assert.Equal(45, r[2].Generated);
    }

    [Fact]
    public void BatchEnabled_SoloRequest_TopLevelCarriesProgress_WhenSlotsAllIdle()
    {
        // Live shape with engine batch enabled and ONE request running: it sits
        // on the solo path — slots[] reports every slot idle while the real
        // progress is in the top-level fields (server.py "outside_slots").
        var r = StrataSource.Parse("""
        {"live":{"state":"reading","queued":0,"parallel":2,"outside_slots":1,
          "prompt_read":10240,"prompt_total":105474,"prompt_tokens":105474,
          "generated":0,"prefill_tok_s_mean":1697.4,
          "slots":[{"slot":0,"state":"idle","held_tokens":0},
                   {"slot":1,"state":"idle","held_tokens":0}]}}
        """);
        var top = Assert.Single(r, x => x.IsTopLevel);
        Assert.Equal(Phase.Prefill, top.State);
        Assert.Equal(10240, top.PromptRead);
        Assert.Equal(105474, top.PromptTotalAbs);
    }

    [Fact]
    public void BatchEnabled_SlotBusy_SlotsOwnState_TopLevelSuppressed()
    {
        // Once a slot is busy it owns the state: top-level fields mix requests
        // when two overlap and must not be added.
        var r = StrataSource.Parse("""
        {"live":{"state":"generating","queued":0,"parallel":2,"outside_slots":0,
          "prompt_read":null,"prompt_total":null,"prompt_tokens":5000,
          "generated":120,"prefill_tok_s_mean":null,
          "slots":[{"slot":0,"state":"decoding","prompt_tokens":5000,
                    "prompt_read":null,"generated":120,"elapsed_s":4.0,"tok_s":30.0}]}}
        """);
        Assert.All(r, x => Assert.False(x.IsTopLevel));
        Assert.Single(r);
        Assert.Equal(Phase.Decode, r[0].State);
    }

    [Fact]
    public void Strata_NewTask_CounterRegression_RestartsBaselines_NotStuckAt99()
    {
        // Strata readings carry no task id: when a new prompt restarts the
        // absolute counters, the old high-water must not survive — it pinned
        // processed > total and froze the % at the 99% cap.
        var agg = new Aggregator(0.0);
        var t0 = DateTime.UtcNow;

        void Feed(Phase state, long read, long total, long gen) =>
            agg.FeedBatch("s", [new SlotReading
            {
                SlotId = 0, IsTopLevel = true, State = state, IsProcessing = true,
                PromptRead = read, PromptTotalAbs = total, Generated = gen,
            }], t0);

        Feed(Phase.Prefill, 100_000, 105_000, 0);
        Assert.InRange(agg.Read().PrefillPct ?? 0.0, 94.0, 96.0); // task 1 ~95%
        Feed(Phase.Decode, 105_000, 105_000, 500);

        // New task: counters restart low. % must reflect the NEW task.
        Feed(Phase.Prefill, 1_000, 30_000, 0);
        var snap = agg.Read();
        Assert.NotNull(snap.PrefillPct);
        Assert.InRange(snap.PrefillPct!.Value, 1.0, 5.0);
        Assert.Equal(1_000, snap.PrefillProcessed);
        Assert.Equal(30_000, snap.PrefillTotal);
    }
}
