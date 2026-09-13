using System.Text;
using System.Text.Json;

namespace LoadingAI.Tools;

/// <summary>
/// Prefill / merge helpers for optional <see cref="ToolEntry.Presets"/>.
/// Form values win over the original LLM args JSON.
/// </summary>
public static class ToolPresets
{
    public static bool HasPresets(ToolEntry entry)
        => entry.Presets is { Count: > 0 };

    public static string KindName(PresetFieldKind kind) => kind.ToString();

    public static Dictionary<string, object?> Prefill(IReadOnlyList<PresetField> fields, string? argsJson)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            if (string.IsNullOrWhiteSpace(f.Id)) continue;
            if (f.Default != null) values[f.Id] = f.Default;
        }

        if (!TryGetObject(argsJson, out var root)) return values;
        foreach (var f in fields)
        {
            if (string.IsNullOrWhiteSpace(f.Id)) continue;
            if (TryGetProperty(root, f.Id, out var el)
                && el.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                values[f.Id] = JsonSerializer.Deserialize<object>(el.GetRawText());
        }
        return values;
    }

    /// <summary>Overlay <paramref name="valuesJson"/> onto <paramref name="argsJson"/>. Overlay wins.</summary>
    public static string Merge(string? argsJson, string? valuesJson)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (TryGetObject(valuesJson, out var overlay))
            {
                foreach (var p in overlay.EnumerateObject())
                {
                    writer.WritePropertyName(p.Name);
                    p.Value.WriteTo(writer);
                    written.Add(p.Name);
                }
            }

            if (TryGetObject(argsJson, out var args))
            {
                foreach (var p in args.EnumerateObject())
                {
                    if (!written.Add(p.Name)) continue;
                    writer.WritePropertyName(p.Name);
                    p.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryGetObject(string? json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
