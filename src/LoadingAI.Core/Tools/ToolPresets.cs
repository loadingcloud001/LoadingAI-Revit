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

    /// <summary>
    /// True when the merged args JSON asks for a read-only preview (<c>dryRun</c> truthy).
    /// The host skips the L3 write confirm on it and plugins read it with the same helper,
    /// so both sides always agree on whether a call may write.
    /// </summary>
    public static bool IsDryRun(string? json)
    {
        if (!TryGetObject(json, out var root)) return false;
        if (!TryGetProperty(root, "dryRun", out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var n) && n == 1,
            JsonValueKind.String => IsTrueLiteral(value.GetString()),
            _ => false
        };
    }

    private static bool IsTrueLiteral(string? s)
        => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";

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

    /// <summary>
    /// Builds the read-only preview the pane shows before the preset form. Values come from
    /// <see cref="Prefill"/>, so resolved args win over the field default. Pure: no I/O,
    /// no transaction, no thread affinity (ADR-014).
    /// </summary>
    public static RunPreview BuildPreview(IReadOnlyList<PresetField> fields, string? argsJson)
    {
        var values = Prefill(fields, argsJson);
        var rows = new List<PreviewRow>(fields.Count);
        var warnings = new List<string>();

        foreach (var f in fields)
        {
            values.TryGetValue(f.Id, out var value);
            rows.Add(new PreviewRow { Label = f.Label, Detail = RenderDetail(value) });

            // `is { Length: > 0 } w` narrows string? to string on every target. Relying on
            // IsNullOrWhiteSpace's [NotNullWhen(false)] does not narrow on netstandard2.0, where
            // that annotation is absent, and the build warns.
            var warning = f.Warning;
            if (warning is { Length: > 0 } w && !string.IsNullOrWhiteSpace(w) && IsTruthyForWarning(value))
                warnings.Add(w);
        }

        return new RunPreview
        {
            Summary = fields.Count == 1 ? "1 setting to confirm" : $"{fields.Count} settings to confirm",
            Rows = rows,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Fault-isolated <see cref="BuildPreview"/>: never throws. A preview is enrichment, so a
    /// value that cannot be rendered yields no preview rather than a failed tool call.
    /// </summary>
    public static RunPreview? TryBuildPreview(IReadOnlyList<PresetField> fields, string? argsJson)
    {
        try { return BuildPreview(fields, argsJson); }
        catch { return null; }
    }

    private static string RenderDetail(object? value) => value switch
    {
        null => "-",
        JsonElement el => RenderElement(el),
        bool b => b ? "on" : "off",
        string s => s.Length > 0 ? s : "-",
        _ => value.ToString() is { Length: > 0 } text ? text : "-"
    };

    private static string RenderElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "-",
        JsonValueKind.True => "on",
        JsonValueKind.False => "off",
        JsonValueKind.String => el.GetString() is { Length: > 0 } s ? s : "-",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.Array => el.GetArrayLength() + " items",
        _ => "-"
    };

    /// <summary>
    /// Truthiness for preview warnings. Deliberately more liberal than <see cref="IsDryRun"/>:
    /// that one decides whether a confirm is skipped, where a false positive is dangerous; this
    /// one raises a warning, where a false negative leaves a destructive setting unflagged.
    /// </summary>
    private static bool IsTruthyForWarning(object? value) => value switch
    {
        null => false,
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Number => el.TryGetInt32(out var n) ? n != 0 : el.GetRawText() != "0",
            JsonValueKind.String => IsTruthyString(el.GetString()),
            JsonValueKind.Array => el.GetArrayLength() > 0,
            _ => true
        },
        bool b => b,
        string s => IsTruthyString(s),
        // A PresetField.Default is a boxed object, so it never takes the JsonElement arm above.
        // Zero is a safe value and must not raise a warning.
        int n => n != 0,
        long n => n != 0L,
        short n => n != 0,
        byte n => n != 0,
        double d => d != 0d,
        float f => f != 0f,
        decimal m => m != 0m,
        _ => true
    };

    /// <summary>
    /// A string is truthy unless it is blank, "0", or "false". A form Number field hands back "0"
    /// for zero and a checkbox can hand back "false"; neither is a reason to raise a warning.
    /// </summary>
    private static bool IsTruthyString(string? s)
        => !string.IsNullOrWhiteSpace(s)
           && !string.Equals(s, "0", StringComparison.Ordinal)
           && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);

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
