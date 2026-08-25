using System.IO;

namespace LoadingAI.Revit;

public static class HostLog
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LoadingAI", "host.log");

    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (Gate) File.AppendAllText(FilePath, $"{DateTime.Now:O} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
