using LlamaPeek.Core;

namespace LlamaPeek.Tests;

/// <summary>Display-side linger: PP/TG keep their last value for LingerMs
/// past idle; prefill % is retained indefinitely; Reset drops everything.</summary>
public class LingerStateTests
{
    static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(2000);
    static readonly DateTime T0 = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    static Snapshot Snap(double? tg = null, double? pp = null, double? pct = null,
                         long? proc = null, long? tot = null) =>
        new() { TgRate = tg, PpRate = pp, PrefillPct = pct,
                PrefillProcessed = proc, PrefillTotal = tot };

    [Fact]
    public void Idle_LingersWithinWindow_ClearsAfter()
    {
        var ls = new LingerState();
        Assert.Equal((60.0, 1000.0), ls.Apply(Snap(60, 1000), Linger, T0));

        // 1s after the last active reading: still shown.
        Assert.Equal((60.0, 1000.0), ls.Apply(Snap(), Linger, T0.AddSeconds(1)));

        // Past the 2s window: cleared.
        Assert.Equal(((double?)null, (double?)null), ls.Apply(Snap(), Linger, T0.AddSeconds(3)));
    }

    [Fact]
    public void FreshRate_ResetsWindow()
    {
        var ls = new LingerState();
        ls.Apply(Snap(tg: 60), Linger, T0);
        ls.Apply(Snap(tg: 70), Linger, T0.AddSeconds(1.5));   // new reading refreshes
        Assert.Equal(70.0, ls.Apply(Snap(), Linger, T0.AddSeconds(3)).Tg);  // 1.5s < 2s
    }

    [Fact]
    public void PrefillPct_RetainedIndefinitely()
    {
        var ls = new LingerState();
        ls.Apply(Snap(pct: 42, proc: 84, tot: 200), Linger, T0);

        var idle = Snap();
        for (int s = 1; s <= 10; s++) ls.Apply(idle, Linger, T0.AddSeconds(s));

        Assert.Equal(42.0, ls.Pct);
        Assert.Equal(84, ls.Processed);
        Assert.Equal(200, ls.Total);
    }

    [Fact]
    public void Reset_DropsAllRetainedValues()
    {
        var ls = new LingerState();
        ls.Apply(Snap(tg: 60, pp: 1000, pct: 42), Linger, T0);
        ls.Reset();

        var (tg, pp) = ls.Apply(Snap(), Linger, T0.AddMilliseconds(1));   // inside old window
        Assert.Null(tg);
        Assert.Null(pp);
        Assert.Null(ls.Pct);
    }
}
