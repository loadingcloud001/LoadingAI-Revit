using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoadingAI.Llm;

public sealed record CheckpointInfo(string Id, DateTime CreatedAt, string? Mode, string Preview, int MessageCount);

public static class CheckpointStore
{
    private static readonly Regex IdPattern = new(@"\A[0-9A-Za-z_]{1,64}\z");

    /// <summary>Checkpoint ids are timestamp + suffix. Anything else could walk out of the folder.</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "checkpoints");
    private static JsonSerializerOptions Opts => new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Save(IReadOnlyList<ChatMessage> history, string? mode)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var id = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 4);
            var preview = history.LastOrDefault(m => m.Role == "user") is { } last ? LlmJson.GetContentString(last.Content) : (history.LastOrDefault()?.Content?.ToString() ?? "");
            if (preview.Length > 80) preview = preview.Substring(0, 80);
            var payload = new { id, createdAt = DateTime.UtcNow, mode, preview, messages = history };
            var json = JsonSerializer.Serialize(payload, Opts);
            var path = Path.Combine(Dir, id + ".json");
            File.WriteAllText(path, json);
            // keep only last 20
            var files = Directory.GetFiles(Dir, "*.json").OrderByDescending(f => f).Skip(20).ToList();
            foreach (var f in files) try { File.Delete(f); } catch { }
            return id;
        }
        catch { return ""; }
    }

    public static IReadOnlyList<CheckpointInfo> List()
    {
        try
        {
            if (!Directory.Exists(Dir)) return Array.Empty<CheckpointInfo>();
            var list = new List<CheckpointInfo>();
            foreach (var f in Directory.GetFiles(Dir, "*.json").OrderByDescending(f => f).Take(20))
            {
                try
                {
                    var json = File.ReadAllText(f);
                    using var doc = JsonDocument.Parse(json);
                    var id = doc.RootElement.TryGetProperty("id", out var ide) ? ide.GetString() ?? Path.GetFileNameWithoutExtension(f) : Path.GetFileNameWithoutExtension(f);
                    var createdAt = doc.RootElement.TryGetProperty("createdAt", out var ca) && ca.TryGetDateTime(out var dt) ? dt : File.GetCreationTimeUtc(f);
                    var mode = doc.RootElement.TryGetProperty("mode", out var me) ? me.GetString() : null;
                    var preview = doc.RootElement.TryGetProperty("preview", out var pe) ? pe.GetString() ?? "" : "";
                    var msgCount = doc.RootElement.TryGetProperty("messages", out var me2) && me2.ValueKind == JsonValueKind.Array ? me2.GetArrayLength() : 0;
                    list.Add(new CheckpointInfo(id, createdAt, mode, preview, msgCount));
                }
                catch { }
            }
            return list;
        }
        catch { return Array.Empty<CheckpointInfo>(); }
    }

    public static List<ChatMessage>? Load(string id)
    {
        try
        {
            if (!IsValidId(id)) return null;
            var path = Path.Combine(Dir, id + ".json");
            if (!File.Exists(path)) path = Directory.GetFiles(Dir, id + "*.json").FirstOrDefault() ?? "";
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array) return null;
            var list = new List<ChatMessage>();
            foreach (var el in msgs.EnumerateArray())
            {
                try { var m = JsonSerializer.Deserialize<ChatMessage>(el.GetRawText(), Opts); if (m != null) list.Add(m); } catch { }
            }
            return list;
        }
        catch { return null; }
    }
}
