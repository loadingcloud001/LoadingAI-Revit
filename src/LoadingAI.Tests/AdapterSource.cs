using System.IO;

namespace LoadingAI.Tests;

/// <summary>
/// Reads the <c>AdapterRevitBridge</c> sources. The adapter is one partial class
/// split across <c>AdapterRevitBridge*.cs</c> along the IRevitBridge seams, so a
/// single-file read would silently miss most of the implementation.
/// </summary>
internal static class AdapterSource
{
    /// <summary>Every adapter partial concatenated, in a stable order.</summary>
    public static string All()
    {
        var dir = Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit");
        var files = Directory.GetFiles(dir, "AdapterRevitBridge*.cs");
        Array.Sort(files, StringComparer.Ordinal);
        return string.Join("\n", files.Select(File.ReadAllText));
    }

    /// <summary>
    /// The source of the single partial containing <paramref name="marker"/>, so a
    /// test can assert on one method without dragging in unrelated slices.
    /// </summary>
    public static string Containing(string marker)
    {
        var dir = Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit");
        var files = Directory.GetFiles(dir, "AdapterRevitBridge*.cs");
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            // IndexOf, not Contains(string, StringComparison): the 2024 target is net48.
            if (text.IndexOf(marker, StringComparison.Ordinal) >= 0) return text;
        }
        throw new FileNotFoundException($"No adapter partial contains '{marker}'.");
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln from " + AppContext.BaseDirectory);
    }
}
