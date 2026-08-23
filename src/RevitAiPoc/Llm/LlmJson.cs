using System.Text.Json;

namespace RevitAiPoc.Llm;

public static class LlmJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetContentString(object? c)
    {
        if (c == null) return "";
        if (c is string s) return s;
        if (c is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString() ?? "";
            if (je.ValueKind == JsonValueKind.Null) return "";
            return je.GetRawText();
        }
        return c.ToString() ?? "";
    }

    /// <summary>
    /// Providers require tool-call arguments to be a JSON object/array string.
    /// Stream merges can produce "" or concatenated objects like {}{}{}{}.
    /// </summary>
    public static string NormalizeToolArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var s = raw.Trim();
        try
        {
            using var d = JsonDocument.Parse(s);
            if (d.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return s;
        }
        catch { }

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            var reader = new Utf8JsonReader(bytes);
            if (JsonDocument.TryParseValue(ref reader, out var first) && first != null)
            {
                using (first)
                {
                    if (first.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        return first.RootElement.GetRawText();
                }
            }
        }
        catch { }

        return "{}";
    }
}
