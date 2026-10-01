using System.IO;
using System.Text.Json;

namespace LoadingAI.Revit;

/// <summary>
/// Lightweight diagnostic log at %AppData%\LoadingAI\debug.jsonl. Size-bounded by
/// <see cref="BoundedLog"/>: the log keeps the two most recent windows and nothing
/// older, so a long session cannot grow it without limit.
/// </summary>
public static class DebugLog
{
    private static readonly object Lock = new();
    public static string FilePath => BoundedLog.Resolve("debug.jsonl");

    public static void Write(string kind, object data)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                kind,
                data
            });
            BoundedLog.Append(FilePath, line, Lock);
        }
        catch { }
    }
}
