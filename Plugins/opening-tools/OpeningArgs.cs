using System.Globalization;
using System.Text.Json;

namespace LoadingAI.Plugins.OpeningTools;

internal readonly struct OpeningArgs
{
    private readonly JsonElement? _root;
    private OpeningArgs(JsonElement? root) => _root = root;

    public static OpeningArgs Parse(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return new OpeningArgs(null);
            using var doc = JsonDocument.Parse(json);
            return new OpeningArgs(doc.RootElement.Clone());
        }
        catch { return new OpeningArgs(null); }
    }

    public string? Str(string name)
    {
        if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    public int Int(string name, int fallback)
    {
        if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return i;
        return fallback;
    }

    public double Dbl(string name, double fallback)
    {
        if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return fallback;
    }

    public List<string> Strings(string name)
    {
        var list = new List<string>();
        if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            else if (item.ValueKind == JsonValueKind.Number)
                list.Add(item.ToString());
        }
        return list;
    }
}
