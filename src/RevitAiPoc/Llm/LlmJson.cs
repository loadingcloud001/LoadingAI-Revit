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
}
