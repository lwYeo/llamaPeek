using System.Text.Json;

namespace LlamaPeek.Core;

/// <summary>
/// Parses strata GET /metrics -> live. Two shapes (verified 0.1.39-0.1.55):
/// a request running alone sits OUTSIDE the slot table (top-level fields only);
/// batched requests get per-slot entries with state/prompt_tokens/generated.
/// prompt_read is an ABSOLUTE position (reused prefix counts toward it), reported
/// once per 8192-token chunk. prefill_tok_s_mean is STALE until the request's
/// first chunk. Slot tok_s is generated/elapsed_s and lies for batched slots,
/// so TG is measured from generated deltas, never from tok_s.
/// </summary>
public static class StrataSource
{
    public static List<SlotReading> Parse(string json)
    {
        var readings = new List<SlotReading>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("live", out var live) ||
            live.ValueKind != JsonValueKind.Object)
            return readings;

        var topState = StateOf(live);
        var top = new SlotReading
        {
            SlotId = 0,
            IsTopLevel = true,
            State = topState,
            IsProcessing = topState != Phase.Idle,
            PromptRead = GetLong(live, "prompt_read"),
            PromptTotalAbs = GetLong(live, "prompt_total"),
            PromptTotal = GetLong(live, "prompt_tokens"),
            Generated = GetLong(live, "generated"),
            ServerPrefillRate = GetDouble(live, "prefill_tok_s_mean"),
        };

        var slots = live.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().ToList() : [];
        foreach (var slot in slots)
        {
            if (slot.ValueKind != JsonValueKind.Object) continue;
            var st = StateOf(slot);
            readings.Add(new SlotReading
            {
                SlotId = (int)(GetLong(slot, "id") ?? readings.Count),
                State = st,
                IsProcessing = st != Phase.Idle,
                PromptTotal = GetLong(slot, "prompt_tokens"),
                Generated = GetLong(slot, "generated"),
                PromptRead = GetLong(slot, "prompt_read"),
                PromptTotalAbs = GetLong(slot, "prompt_total"),
                ServerPrefillRate = GetDouble(slot, "prefill_tok_s_mean"),
            });
        }

        // Top-level request only exists when nothing is batched; once slots[] has
        // entries it owns the state (top-level fields mix requests when two overlap).
        if (slots.Count == 0)
            readings.Add(top);

        return readings;
    }

    static Phase StateOf(JsonElement obj)
    {
        var s = obj.TryGetProperty("state", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        return s switch
        {
            "reading" => Phase.Prefill,
            "generating" or "decoding" => Phase.Decode,
            _ => Phase.Idle,
        };
    }

    static long? GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var n) ? n : null;

    static double? GetDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetDouble(out var d) ? d : null;
}
