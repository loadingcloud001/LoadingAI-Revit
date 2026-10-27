using System.Text;
using System.Text.Json;

namespace LoadingAI.Tools;

/// <summary>
/// Replaces "@rN" in elementIds / elementId with the saved ids before arguments are coerced and checked (P2),
/// so the review card and the tool see real ids. Only those two keys at the top level are touched.
/// </summary>
public static class ResultSetExpander
{
    public static bool TryExpand(string argsJson, ResultSetStore store, out string expanded, out string error)
    {
        expanded = argsJson;
        error = "";
        if (string.IsNullOrWhiteSpace(argsJson) || !argsJson.Contains("@r")) return true;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(argsJson); } catch { return true; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;
            var changed = false;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.NameEquals("elementIds") && (p.Value.ValueKind == JsonValueKind.Array || IsHandle(p.Value)))
                    {
                        var items = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().ToList() : new List<JsonElement> { p.Value };
                        var outIds = new List<string>();
                        foreach (var it in items)
                        {
                            var s = it.ValueKind == JsonValueKind.String ? it.GetString() ?? "" : it.GetRawText();
                            if (IsHandle(s))
                            {
                                if (!store.TryGet(s, out var ids)) { error = Unknown(s, store); return false; }
                                outIds.AddRange(ids);
                                changed = true;
                            }
                            else outIds.Add(s);
                        }
                        w.WritePropertyName("elementIds");
                        w.WriteStartArray();
                        foreach (var id in outIds.Distinct()) w.WriteStringValue(id);
                        w.WriteEndArray();
                    }
                    else if (p.NameEquals("elementId") && IsHandle(p.Value))
                    {
                        var h = p.Value.GetString()!;
                        if (!store.TryGet(h, out var ids)) { error = Unknown(h, store); return false; }
                        if (ids.Count != 1) { error = $"{h} holds {ids.Count} elements; elementId takes one. Pass it as elementIds or pick one id."; return false; }
                        w.WriteString("elementId", ids[0]);
                        changed = true;
                    }
                    else p.WriteTo(w);
                }
                w.WriteEndObject();
            }
            if (changed) expanded = Encoding.UTF8.GetString(ms.ToArray());
            return true;
        }
    }

    private static bool IsHandle(JsonElement e) => e.ValueKind == JsonValueKind.String && IsHandle(e.GetString());
    private static bool IsHandle(string? s) => s != null && s.Length > 2 && s.StartsWith("@r") && s.Substring(2).All(char.IsDigit);

    private static string Unknown(string handle, ResultSetStore store)
    {
        var saved = store.Handles;
        return saved.Count == 0
            ? $"{handle} is not a saved result set. No sets are saved in this chat."
            : $"{handle} is not a saved result set. Saved: {string.Join(", ", saved)}.";
    }
}
