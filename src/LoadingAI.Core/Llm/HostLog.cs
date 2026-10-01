using System.IO;

namespace LoadingAI.Revit;

/// <summary>Host diagnostics at %AppData%\LoadingAI\host.log, size-bounded by <see cref="BoundedLog"/>.</summary>
public static class HostLog
{
    private static readonly object Gate = new();
    private static string FilePath => BoundedLog.Resolve("host.log");

    public static void Write(string msg)
    {
        try
        {
            BoundedLog.Append(FilePath, $"{DateTime.Now:O} {msg}", Gate);
        }
        catch { }
    }
}
