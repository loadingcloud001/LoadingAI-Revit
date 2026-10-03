using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LoadingAI.Tools;

/// <summary>
/// Makes lossless fixes to model-supplied tool arguments before <see cref="ToolArgumentValidator"/>
/// runs: a number where the schema wants a string (element ids), a numeric or boolean string where
/// it wants a number or boolean, a lone value where it wants an array, an enum value in the wrong
/// case, and a blank string for an optional number or boolean (a cleared form field). Anything
/// else is left alone for the validator to reject, so a malformed write still cannot turn into a
/// different operation (B1). The output is what the user approves and what runs.
/// </summary>
public static class ToolArgumentCoercer
{
    public static string Coerce(object? schema, string argsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(argsJson)) return argsJson;
            using var args = JsonDocument.Parse(argsJson);
            if (args.RootElement.ValueKind != JsonValueKind.Object) return argsJson;

            JsonElement schemaElement = schema is null ? default
                : schema is JsonElement given ? given
                : JsonSerializer.SerializeToElement(schema);
            if (schemaElement.ValueKind != JsonValueKind.Object) return argsJson;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                WriteValue(writer, args.RootElement, schemaElement);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return argsJson;
        }
    }

    private static void WriteValue(Utf8JsonWriter w, JsonElement value, JsonElement schema)
    {
        var types = ReadTypes(schema);

        if (types.Count == 1 && types[0] == "array" && IsWrappable(value))
        {
            w.WriteStartArray();
            WriteValue(w, value, ItemSchema(schema));
            w.WriteEndArray();
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(w, value, schema);
                return;
            case JsonValueKind.Array:
            {
                var items = ItemSchema(schema);
                w.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteValue(w, item, items);
                w.WriteEndArray();
                return;
            }
            case JsonValueKind.Number:
                if (Accepts(types, "string") && !Accepts(types, "number") && !Accepts(types, "integer"))
                {
                    w.WriteStringValue(value.GetRawText());
                    return;
                }
                break;
            case JsonValueKind.String:
                if (TryWriteCoercedString(w, value.GetString() ?? "", types, schema)) return;
                break;
        }

        value.WriteTo(w);
    }

    private static void WriteObject(Utf8JsonWriter w, JsonElement value, JsonElement schema)
    {
        var props = schema.ValueKind == JsonValueKind.Object
                    && schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : default;
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in req.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString()!);
        }

        w.WriteStartObject();
        foreach (var prop in value.EnumerateObject())
        {
            JsonElement propSchema = default;
            var declared = props.ValueKind == JsonValueKind.Object && props.TryGetProperty(prop.Name, out propSchema);
            if (declared && !required.Contains(prop.Name) && IsBlankForNonText(prop.Value, propSchema))
                continue;

            w.WritePropertyName(prop.Name);
            if (declared) WriteValue(w, prop.Value, propSchema);
            else prop.Value.WriteTo(w);
        }
        w.WriteEndObject();
    }

    private static bool TryWriteCoercedString(Utf8JsonWriter w, string text, IReadOnlyList<string> types, JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array)
        {
            string? canonical = null;
            foreach (var option in allowed.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.String) continue;
                var o = option.GetString()!;
                if (o == text) return false;
                if (canonical == null && string.Equals(o, text.Trim(), StringComparison.OrdinalIgnoreCase)) canonical = o;
            }
            if (canonical == null) return false;
            w.WriteStringValue(canonical);
            return true;
        }

        if (Accepts(types, "string")) return false; // a string is wanted: never reinterpret it

        var trimmed = text.Trim();
        if ((Accepts(types, "integer") || Accepts(types, "number"))
            && decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            w.WriteNumberValue(number);
            return true;
        }

        if (Accepts(types, "boolean"))
        {
            if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)) { w.WriteBooleanValue(true); return true; }
            if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)) { w.WriteBooleanValue(false); return true; }
        }

        return false;
    }

    private static bool IsWrappable(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => false
    };

    private static bool IsBlankForNonText(JsonElement value, JsonElement schema)
    {
        if (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString())) return false;
        var types = ReadTypes(schema);
        return types.Count > 0 && !Accepts(types, "string");
    }

    private static JsonElement ItemSchema(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("items", out var items) ? items : default;

    private static bool Accepts(IReadOnlyList<string> types, string type)
        => types.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> ReadTypes(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
            return Array.Empty<string>();
        if (type.ValueKind == JsonValueKind.String) return new[] { type.GetString() ?? "" };
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? "").ToList();
        return Array.Empty<string>();
    }
}
