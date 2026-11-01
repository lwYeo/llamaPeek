namespace LlamaPeek.Core;

public enum ServerKind { Auto, LlamaCpp, Strata }
public enum Phase { Idle, Prefill, Decode }

public sealed record ServerConfig(
    string Name,
    string Url,
    ServerKind Kind = ServerKind.Auto,
    bool RequiresModel = false);

public sealed class AppSettings
{
    public List<ServerConfig> Servers { get; set; } = new();
    public int PollActiveMs { get; set; } = 500;
    public int PollIdleMs { get; set; } = 2000;
    public int GraceMs { get; set; } = 2500;
    public int RefreshMs { get; set; } = 500;
    public int LingerMs { get; set; } = 2000;
    public double Smoothing { get; set; } = 0.7;
    public bool LaunchAtStartup { get; set; }

    public AppSettings Clone() => new()
    {
        Servers = Servers.Select(s => s with { }).ToList(),
        PollActiveMs = PollActiveMs,
        PollIdleMs = PollIdleMs,
        GraceMs = GraceMs,
        RefreshMs = Math.Clamp(RefreshMs, 100, 5000),
        LingerMs = LingerMs,
        Smoothing = Smoothing,
        LaunchAtStartup = LaunchAtStartup,
    };
}

/// <summary>One slot's raw counters from a single poll of one server.</summary>
public sealed class SlotReading
{
    public int SlotId;
    public bool IsTopLevel;             // strata: request outside the slot table
    public long? TaskId;                // llama.cpp id_task; null = absent
    public bool IsProcessing;
    // llama.cpp /slots
    public long? PromptTotal;
    public long? PromptProcessed;
    public long? PromptCache;
    public long? Decoded;
    // strata /metrics live
    public Phase? State;
    public long? PromptRead;            // absolute position, includes reused prefix
    public long? PromptTotalAbs;
    public long? Generated;
    public double? ServerPrefillRate;   // prefill_tok_s_mean (stale until first chunk)
}

/// <summary>Everything the three tray icons render, computed from all servers.</summary>
public sealed class Snapshot
{
    public double? TgRate;              // summed across decoding slots
    public double? PpRate;              // summed across prefilling slots
    public int TgActive;
    public int PpActive;
    public double? PrefillPct;          // null = denominator untrustworthy
    public long? PrefillProcessed;
    public long? PrefillTotal;
    public double? TgMax;               // running maxima since Restart
    public double? PpMax;
    public bool AnyUnreachable;
    public bool RequiredUnreachable;    // any RequiresModel server is down -> alert dot
    public DateTime UpdatedUtc;
}
