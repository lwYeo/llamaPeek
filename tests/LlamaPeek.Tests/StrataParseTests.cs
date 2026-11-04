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
}
