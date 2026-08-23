using System.IO;
using System.Text.Json;

namespace RevitAiPoc.Llm;

public static class HistoryStore
{
    private static readonly object Gate = new();
    private static int _appendsSinceTrim;

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
            lock (Gate)
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
                _appendsSinceTrim++;
                if (_appendsSinceTrim >= 50)
                {
                    _appendsSinceTrim = 0;
                    TrimUnlocked(200);
                }
            }
        }
        catch { }
    }

    public static void Trim(int keep = 200)
    {
        try
        {
            lock (Gate) TrimUnlocked(keep);
        }
        catch { }
    }

    private static void TrimUnlocked(int keep)
    {
        if (!File.Exists(FilePath)) return;
        var lines = File.ReadAllLines(FilePath);
        if (lines.Length > keep)
            File.WriteAllLines(FilePath, lines[^keep..]);
    }
}
