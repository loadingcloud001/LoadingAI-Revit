using System;
using System.IO;
using LoadingAI.Revit;

namespace LoadingAI.Plugins.Core;

/// <summary>Writes log lines for plugin-hosted messages to the shared, size-bounded plugins log.</summary>
public static class PluginLogs
{
    public static void Append(string pluginDir, string message)
    {
        var plugin = Path.GetFileName((pluginDir ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(plugin)) plugin = "plugin";
        var clean = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
        PluginLogFile.Append($"{DateTime.Now:O} [{plugin}] {clean}");
    }
}
