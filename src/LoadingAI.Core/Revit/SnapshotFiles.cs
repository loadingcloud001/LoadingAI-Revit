namespace LoadingAI.Revit;

/// <summary>Where view snapshots go and how many are kept (P1). Pure apart from file I/O.</summary>
public static class SnapshotFiles
{
    /// <summary>How many snapshots are kept in the folder; the oldest go first.</summary>
    public const int Keep = 50;

    /// <summary>The per-user folder snapshots are written to.</summary>
    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "snapshots");

    /// <summary>A fresh PNG path in <paramref name="dir"/>. The stamp keeps the files sortable by name.</summary>
    public static string NewPath(string dir, DateTime utcNow)
        => Path.Combine(dir, $"{utcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 6)}.png");

    /// <summary>Deletes the snapshots beyond <paramref name="keep"/>, newest first. Never throws.</summary>
    public static void Prune(string dir, int keep = Keep)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files.Skip(keep))
                try { f.Delete(); } catch { }
        }
        catch { }
    }
}
