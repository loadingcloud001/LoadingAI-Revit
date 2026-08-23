using System.IO;
using System.Text.Json;

namespace RevitAiPoc.Revit;

/// <summary>Lightweight diagnostic log at %AppData%\RevitAiPoc\debug.jsonl.</summary>
public static class DebugLog
{
    private static readonly object Lock = new();
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RevitAiPoc", "debug.jsonl");

    public static void Write(string kind, object data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                kind,
                data
            });
            lock (Lock) File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        catch { }
    }
}
