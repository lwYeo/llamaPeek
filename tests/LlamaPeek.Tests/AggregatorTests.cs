using LlamaPeek.Core;

namespace LlamaPeek.Tests;

/// <summary>Aggregator lifecycle: phantom-state cleanup and counter regression.</summary>
public class AggregatorTests
{
    static SlotReading Prefill(long proc, long total = 16384, long? task = null) =>
        new()
        {
            SlotId = 0,
            TaskId = task,
            IsProcessing = true,
            PromptProcessed = proc,
            PromptTotal = total,
            PromptCache = 0,
            Decoded = 0,
        };

    [Fact]
    public void Clear_DropsServerState_MidTaskServerDeathLeavesNoPhantom()
    {
        var agg = new Aggregator(0.5);
        var t = DateTime.UtcNow;
        agg.Feed("srv", Prefill(2048), t);
        agg.Feed("srv", Prefill(4096), t.AddSeconds(1));

        var live = agg.Read();
        Assert.Equal(1, live.PpActive);          // prefilling, rate measured
        Assert.NotNull(live.PpRate);

        agg.Clear("srv");                        // poll failed -> runtime clears
        var after = agg.Read();
        Assert.Equal(0, after.PpActive);         // no phantom contribution
        Assert.Null(after.PpRate);
    }

    [Fact]
    public void FeedBatch_PrunesVanishedSlots()
    {
        var agg = new Aggregator(0.5);
        var t = DateTime.UtcNow;
        var s0 = Prefill(2048);
        var s1 = Prefill(2048); s1.SlotId = 1;
        agg.FeedBatch("srv", [s0, s1], t);
        Assert.Equal(2, agg.Read().PpActive);

        var s0b = Prefill(4096);
        var s1b = Prefill(4096); s1b.SlotId = 1;
        agg.FeedBatch("srv", [s0b, s1b], t.AddSeconds(1));
        Assert.Equal(2, agg.Read().PpActive);

        // slot 1 disappears from the response (freed mid-task): dropped.
        agg.FeedBatch("srv", [Prefill(6144)], t.AddSeconds(2));
        Assert.Equal(1, agg.Read().PpActive);
    }

    [Fact]
    public void CounterRegression_ResetsBaselines_NoTaskId()
    {
        // llama.cpp build without id_task: server restart zeroes counters.
        var agg = new Aggregator(0.5);
        var t = DateTime.UtcNow;
        agg.Feed("srv", Prefill(4096), t);
        agg.Feed("srv", Prefill(0), t.AddSeconds(1));        // restart: counters zeroed
        agg.Feed("srv", Prefill(2048), t.AddSeconds(2));
        agg.Feed("srv", Prefill(4096), t.AddSeconds(3));

        var snap = agg.Read();
        // Without regression detection the baselines stay at 4096 and the
        // rate never resumes: PpActive would be 0 and PpRate null.
        Assert.Equal(1, snap.PpActive);
        Assert.NotNull(snap.PpRate);
        Assert.InRange(snap.PpRate!.Value, 1024, 4096);
    }
}
