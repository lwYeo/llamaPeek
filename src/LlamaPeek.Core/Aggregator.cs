using System.Collections.Concurrent;

namespace LlamaPeek.Core;

/// <summary>
/// Maintains per-slot state across polls, infers phase, computes rates,
/// sums within a phase only, and tracks running maxima since the last Reset.
/// </summary>
public sealed class Aggregator(double smoothing)
{
    private sealed class SlotState
    {
        public RateMeter Pp = null!, Tg = null!;
        public long? LastTaskId;
        public long PrevProcessed, PrevDecoded, PrevRead;
        public long HighWater;           // prompt denominator high-water per task
        public bool TotalGrew;           // sticky per task: chunked prefill, % suppressed
        public Phase Phase = Phase.Idle;
        // current values for the snapshot
        public long CurProcessed, CurTotal;      // llama.cpp: processed, highwater
        public long CurRead, CurTotalAbs;        // strata: absolute read, total
        public bool TrustworthyPct = true;
        public void Init(double s) { Pp = new RateMeter(s); Tg = new RateMeter(s); }
        public void ResetTask()
        {
            Pp.Reset(); Tg.Reset();
            PrevProcessed = PrevDecoded = PrevRead = 0;
            HighWater = 0; TotalGrew = false; TrustworthyPct = true;
            Phase = Phase.Idle;
        }
    }

    private readonly ConcurrentDictionary<string, SlotState> _states = new();
    private double? _tgMax, _ppMax;
    private readonly object _maxLock = new();   // Read() mutates maxima; Current() is called from pool + UI threads

    public void Feed(string server, SlotReading r, DateTime nowUtc)
    {
        var key = Key(server, r);
        var st = _states.GetOrAdd(key, _ => { var s = new SlotState(); s.Init(smoothing); return s; });
        lock (st)
        {
            // A task change invalidates every baseline.
            if (r.TaskId is { } tid && tid != st.LastTaskId)
            {
                st.LastTaskId = tid;
                st.ResetTask();
            }

            if (r.State is { } strataState)
                FeedStrata(st, r, strataState, nowUtc);
            else
                FeedLlamaCpp(st, r, nowUtc);
        }
    }

    /// <summary>Feed a whole poll result for one server, then drop state for
    /// slots that vanished from the response. A slot that dies mid-task keeps
    /// its last phase and rate, and Read() would sum that phantom forever.</summary>
    public void FeedBatch(string server, IReadOnlyList<SlotReading> readings, DateTime nowUtc)
    {
        foreach (var r in readings) Feed(server, r, nowUtc);
        var live = readings.Select(r => Key(server, r)).ToHashSet();
        foreach (var key in _states.Keys.ToList())
            if (key.StartsWith(server + "|", StringComparison.Ordinal) && !live.Contains(key))
                _states.TryRemove(key, out _);
    }

    /// <summary>Drop all state for a server (poll failed / server down): its
    /// slots' last rates and phases must not keep contributing to snapshots.</summary>
    public void Clear(string server)
    {
        foreach (var key in _states.Keys.ToList())
            if (key.StartsWith(server + "|", StringComparison.Ordinal))
                _states.TryRemove(key, out _);
    }

    private static string Key(string server, SlotReading r) =>
        $"{server}|{(r.IsTopLevel ? "top" : r.SlotId)}";

    private static void FeedLlamaCpp(SlotState st, SlotReading r, DateTime nowUtc)
    {
        long proc = r.PromptProcessed ?? 0;
        long decoded = r.Decoded ?? 0;

        // Counter regression = server restarted (counters zeroed). Without a
        // task id to key off, the old high-water baselines would freeze PP/TG
        // until the new counters climbed past the old values.
        if (proc < st.PrevProcessed || decoded < st.PrevDecoded)
            st.ResetTask();

        bool ppMoved = proc > st.PrevProcessed;
        bool tgMoved = decoded > st.PrevDecoded;

        if (ppMoved)
        {
            st.Pp.Update(proc, nowUtc);
            st.Phase = Phase.Prefill;
        }
        if (tgMoved)
        {
            // PP->TG handover: adopt the first decode reading, don't blend.
            st.Tg.Update(decoded, nowUtc, replace: st.Phase != Phase.Decode);
            if (!ppMoved) st.Phase = Phase.Decode;
        }
        if (!ppMoved && !tgMoved && !r.IsProcessing)
            st.Phase = Phase.Idle;

        // Denominator: uncached prompt size. If it ever grows mid-task the prompt
        // is chunked and the percentage is a lie for this task — suppress, sticky.
        long total = (r.PromptTotal ?? 0) - (r.PromptCache ?? 0);
        if (total > st.HighWater)
        {
            if (st.HighWater > 0) { st.TotalGrew = true; st.TrustworthyPct = false; }
            st.HighWater = total;
        }

        st.PrevProcessed = Math.Max(st.PrevProcessed, proc);
        st.PrevDecoded = Math.Max(st.PrevDecoded, decoded);
        st.CurProcessed = st.PrevProcessed;
        st.CurTotal = st.HighWater;
    }

    private static void FeedStrata(SlotState st, SlotReading r, Phase state, DateTime nowUtc)
    {
        if (state == Phase.Idle)
        {
            st.Phase = Phase.Idle;
            return;
        }

        // A new task restarts the absolute counters; strata readings carry no
        // task id to key off, so a regression (read/gen below the old
        // baseline) is the only signal. Without this reset the previous
        // task's high-water froze the prefill % at the 99% cap
        // (processed > total) and the PP baseline until the new task's
        // counters climbed past it.
        if ((r.PromptRead is { } pr && pr < st.PrevRead) ||
            (r.Generated is { } pg && pg < st.PrevDecoded))
            st.ResetTask();

        st.Phase = state;

        long read = r.PromptRead ?? 0;
        if (read > st.PrevRead)
        {
            st.Pp.Update(read, nowUtc);
            st.PrevRead = read;
        }
        // prefill_tok_s_mean is stale until this task's first chunk lands
        // (PromptRead > 0); trust the server figure once it has.
        if (r.ServerPrefillRate is { } srv && st.PrevRead > 0)
            st.Pp.SetDirect(srv);

        long gen = r.Generated ?? 0;
        if (gen > st.PrevDecoded)
        {
            st.Tg.Update(gen, nowUtc, replace: true); // slot tok_s lies; deltas only
            st.PrevDecoded = gen;
        }

        st.CurRead = st.PrevRead;
        st.CurTotalAbs = r.PromptTotalAbs ?? 0;
        st.TrustworthyPct = st.CurTotalAbs > 0;
    }

    public Snapshot Read()
    {
        double? ppSum = null, tgSum = null;
        double? ppMax = null, tgMax = null;
        int ppN = 0, tgN = 0;
        long pctNum = 0, pctDen = 0;
        bool pctOk = true, anyPrefill = false;

        foreach (var st in _states.Values)
        {
            lock (st)
            {
                switch (st.Phase)
                {
                    case Phase.Prefill:
                        ppN++;
                        if (st.Pp.Rate is { } pr) ppSum = (ppSum ?? 0) + pr;
                        anyPrefill = true;
                        if (st.CurTotalAbs > 0) { pctNum += st.CurRead; pctDen += st.CurTotalAbs; }
                        else { pctNum += st.CurProcessed; pctDen += st.CurTotal; }
                        pctOk &= st.TrustworthyPct;
                        break;
                    case Phase.Decode:
                        tgN++;
                        if (st.Tg.Rate is { } tr) tgSum = (tgSum ?? 0) + tr;
                        break;
                }
            }
        }

        lock (_maxLock)
        {
            if (ppSum is { } pp && (_ppMax is null || pp > _ppMax)) _ppMax = pp;
            if (tgSum is { } tg && (_tgMax is null || tg > _tgMax)) _tgMax = tg;
            ppMax = _ppMax; tgMax = _tgMax;
        }

        double? pct = anyPrefill && pctOk && pctDen > 0
            ? Math.Min(99.0, 100.0 * pctNum / pctDen)   // cap: total grows behind processed
            : null;

        return new Snapshot
        {
            PpRate = ppSum,
            TgRate = tgSum,
            PpActive = ppN,
            TgActive = tgN,
            PrefillPct = pct,
            PrefillProcessed = anyPrefill ? pctNum : null,
            PrefillTotal = anyPrefill && pctDen > 0 ? pctDen : null,
            PpMax = ppMax,
            TgMax = tgMax,
            UpdatedUtc = DateTime.UtcNow,
        };
    }

    public void Reset()
    {
        _states.Clear();
        lock (_maxLock) _tgMax = _ppMax = null;
    }
}
