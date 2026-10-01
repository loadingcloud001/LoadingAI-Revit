using System.IO;
using System.Text.Json;

namespace LoadingAI.Revit;

public static class AuditLog
{
    private static readonly object _lock = new();
    private static string FilePath => BoundedLog.Resolve("audit.jsonl");

    public static void Append(string toolName, string userText, string elementIds, string result, string tier)
    {
        try
        {
            var entry = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                tool = toolName,
                tier,
                userText = Truncate(userText, 500),
                elementIds = Truncate(elementIds, 500),
                result = Truncate(result, 1000)
            };
            BoundedLog.Append(FilePath, JsonSerializer.Serialize(entry), _lock);
        }
        catch { }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
}
