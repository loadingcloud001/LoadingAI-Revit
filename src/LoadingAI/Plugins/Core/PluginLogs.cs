using System;
using System.IO;

namespace LoadingAI.Plugins.Core;

/// <summary>Writes log lines for plugin-hosted messages to the shared plugins log.</summary>
public static class PluginLogs
{
    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "plugins.log");

    public static void Append(string pluginDir, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var plugin = Path.GetFileName(pluginDir) ?? "plugin";
            File.AppendAllText(LogPath, $"{DateTime.Now:O} [{plugin}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
