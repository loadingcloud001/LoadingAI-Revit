using System.IO;
using System.Text.Json;

namespace RevitAiPoc.Llm;

public static class HistoryStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RevitAiPoc", "history.jsonl");

    public static void Append(string role, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var entry = new { timestamp = DateTime.UtcNow.ToString("o"), role, text = text.Length > 2000 ? text[..2000] + "..." : text };
            var line = JsonSerializer.Serialize(entry);
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        catch { }
    }

    // Keep last N lines trimmed on each append to avoid unbounded growth
    public static void Trim(int keep = 200)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var lines = File.ReadAllLines(FilePath);
            if (lines.Length > keep)
            {
                File.WriteAllLines(FilePath, lines[^keep..]);
            }
        }
        catch { }
    }
}
