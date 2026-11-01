using System.Text.Json;

namespace LlamaPeek.Core;

/// <summary>Parses llama.cpp GET /slots into SlotReadings. Null-tolerant: fields
/// may be absent, null, or non-numeric; never throws on shape changes.</summary>
public static class LlamaCppSource
{
    public static List<SlotReading> Parse(string json)
    {
        var readings = new List<SlotReading>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return readings;

        foreach (var slot in doc.RootElement.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object) continue;
            var r = new SlotReading
            {
                SlotId = (int)(GetLong(slot, "id") ?? readings.Count),
                IsProcessing = GetBool(slot, "is_processing") ?? false,
                TaskId = GetLong(slot, "id_task"),
                PromptTotal = GetLong(slot, "n_prompt_tokens"),
                PromptProcessed = GetLong(slot, "n_prompt_tokens_processed"),
                PromptCache = GetLong(slot, "n_prompt_tokens_cache"),
            };
            // n_decoded lives in next_token[0].n_decoded
            if (slot.TryGetProperty("next_token", out var nt) &&
                nt.ValueKind == JsonValueKind.Array && nt.GetArrayLength() > 0)
                r.Decoded = GetLong(nt[0], "n_decoded");
            readings.Add(r);
        }
        return readings;
    }

    static long? GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var n) ? n : null;

    static bool? GetBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;
}
