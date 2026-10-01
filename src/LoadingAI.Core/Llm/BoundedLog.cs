using System.IO;

namespace LoadingAI.Revit;

/// <summary>
/// Appends lines to a JSONL/plain log under %AppData%\LoadingAI with a hard size
/// bound. Every host log funnels through here so no install can grow unbounded:
/// a long-running session that logged prompts, model names and element ids would
/// otherwise fill the disk with plaintext the user never asked to keep.
/// </summary>
/// <remarks>
/// Rotation is size-based, not age-based, so it needs no timer and no background
/// work on the Revit UI thread. When the file crosses <see cref="MaxBytes"/> the
/// previous generation is kept as <c>&lt;name&gt;.1</c> and truncated, so the log
/// always holds the most recent two windows. Best-effort throughout: logging must
/// never break a tool call.
/// </remarks>
internal static class BoundedLog
{
    /// <summary>Rotate once the live file passes this size.</summary>
    internal const long MaxBytes = 2 * 1024 * 1024;

    internal static string Resolve(params string[] segments)
        => Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI" }
            .Concat(segments).ToArray());

    /// <summary>Appends one line, rotating first when the file is over budget.</summary>
    internal static void Append(string path, string line, object gate)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (gate)
            {
                RotateIfNeeded(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { }
    }

    private static void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;

        var previous = path + ".1";
        try
        {
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }
        catch
        {
            // Rotation failed (file locked by a viewer, for example). Truncate in
            // place instead so the size bound still holds.
            try
            {
                using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.Read);
                stream.Flush();
            }
            catch { }
        }
    }

    /// <summary>
    /// Total bytes the log family occupies, including the rotated generation.
    /// Used by the tests and by "clear logs" style diagnostics.
    /// </summary>
    internal static long TotalBytes(string path)
    {
        long total = 0;
        foreach (var candidate in new[] { path, path + ".1" })
            if (File.Exists(candidate)) total += new FileInfo(candidate).Length;
        return total;
    }
}
