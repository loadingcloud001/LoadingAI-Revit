using System.IO;
using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class SnapshotFilesTests
{
    [Fact]
    public void Prune_KeepsTheNewest50()
    {
        var dir = Path.Combine(Path.GetTempPath(), "snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 55; i++)
        {
            var f = Path.Combine(dir, $"s{i:00}.png");
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(i));
        }
        SnapshotFiles.Prune(dir, 50);
        var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(50, left.Count);
        Assert.Equal("s05.png", left[0]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void NewPath_IsUnderTheFolder_AndPng()
    {
        var p = SnapshotFiles.NewPath("/tmp/snaps", new DateTime(2026, 10, 27, 9, 5, 3, DateTimeKind.Utc));
        // The separator differs per OS, so compare the folder with the one the path uses.
        Assert.StartsWith("/tmp/snaps", p.Replace('\\', '/'));
        Assert.Matches(@"20261027-090503-[0-9a-f]{6}\.png$", p);
    }
}
