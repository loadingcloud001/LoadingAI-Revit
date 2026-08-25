using System.Text.Json;

namespace LoadingAI.Llm;

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
        if (c is List<ChatContentPart> parts) return string.Join("\n", parts.Where(p => p.Type == "text").Select(p => p.Text ?? ""));
        if (c is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString() ?? "";
            if (je.ValueKind == JsonValueKind.Array)
            {
                var texts = new List<string>();
                foreach (var el in je.EnumerateArray())
                {
                    if (el.TryGetProperty("type", out var t) && t.GetString() == "text" && el.TryGetProperty("text", out var tx))
                        texts.Add(tx.GetString() ?? "");
                }
                return string.Join("\n", texts);
            }
            if (je.ValueKind == JsonValueKind.Null) return "";
            return je.GetRawText();
        }
        return c.ToString() ?? "";
    }

    public static bool IsMultimodal(object? c) => c is List<ChatContentPart> || (c is JsonElement je && je.ValueKind == JsonValueKind.Array);

    public static object? StripImages(object? c)
    {
        if (c is List<ChatContentPart> parts)
        {
            var texts = parts.Where(p => p.Type == "text").Select(p => p.Text ?? "").ToList();
            var imgs = parts.Count(p => p.Type != "text");
            if (imgs > 0) texts.Add($"[{imgs} image(s) omitted]");
            return string.Join("\n", texts);
        }
        if (c is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            var imgs = 0;
            foreach (var el in je.EnumerateArray())
            {
                var typ = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (typ == "text" && el.TryGetProperty("text", out var tx))
                    texts.Add(tx.GetString() ?? "");
                else if (typ is "image_url" or "input_image")
                    imgs++;
            }
            if (imgs > 0) texts.Add($"[{imgs} image(s) omitted]");
            return string.Join("\n", texts);
        }
        return c;
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
