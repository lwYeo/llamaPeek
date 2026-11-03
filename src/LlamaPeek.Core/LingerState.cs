namespace LlamaPeek.Core;

/// <summary>
/// Display-side smoothing between snapshots: PP/TG rates LINGER past idle so
/// the PP→TG handover stays readable, and prefill % retains its last value
/// (the icon stays blue on idle). Pure logic — time is injected so it is
/// unit-testable; the tray renders whatever Apply returns.
/// </summary>
public sealed class LingerState
{
    double? _tg, _pp, _pct;
    DateTime _tgAt, _ppAt;

    /// <summary>Prefill token counts shown in the tooltip (retained with %).</summary>
    public long? Processed { get; private set; }
    public long? Total { get; private set; }

    /// <summary>The prefill % to display (last known, never auto-clears).</summary>
    public double? Pct => _pct;

    /// <summary>
    /// Fold a snapshot in and return the rates to display. A fresh rate
    /// updates the baseline; an idle snapshot keeps the last value while it
    /// is within the linger window.
    /// </summary>
    public (double? Tg, double? Pp) Apply(Snapshot s, TimeSpan linger, DateTime nowUtc)
    {
        if (s.PrefillPct is { } live)
        {
            _pct = live;
            Processed = s.PrefillProcessed;
            Total = s.PrefillTotal;
        }

        var tg = s.TgRate;
        var pp = s.PpRate;
        if (tg is { } tgv) { _tg = tgv; _tgAt = nowUtc; }
        else if (_tg is { } tgl && nowUtc - _tgAt <= linger) tg = tgl;
        if (pp is { } ppv) { _pp = ppv; _ppAt = nowUtc; }
        else if (_pp is { } ppl && nowUtc - _ppAt <= linger) pp = ppl;
        return (tg, pp);
    }

    /// <summary>Drop all retained values (monitor restart: the old
    /// aggregator's numbers must not show on the new one's first frames).</summary>
    public void Reset()
    {
        _tg = _pp = _pct = null;
        Processed = Total = null;
    }
}
