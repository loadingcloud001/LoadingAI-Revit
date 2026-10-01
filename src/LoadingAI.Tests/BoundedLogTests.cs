using System.IO;
using System.Text;
using LoadingAI.Revit;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Bug 7: the host logs appended forever with no rotation or retention, so a
/// long-running install accumulated prompts, model names and element ids in
/// plaintext without bound. <c>BoundedLog</c> now caps each family at two windows.
/// </summary>
public class BoundedLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loadingai-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private string Path_(string name) => Path.Combine(_root, name);

    [Fact]
    public void AppendCreatesTheFileAndWritesLines()
    {
        var p = Path_("a.log");
        BoundedLog.Append(p, "one", _gate);
        BoundedLog.Append(p, "two", _gate);

        var lines = File.ReadAllLines(p);
        Assert.Equal(new[] { "one", "two" }, lines);
    }

    [Fact]
    public void AppendCreatesMissingDirectories()
    {
        var p = Path.Combine(_root, "nested", "deeper", "a.log");
        BoundedLog.Append(p, "hello", _gate);
        Assert.True(File.Exists(p));
    }

    [Fact]
    public void OversizedLogRotatesInsteadOfGrowingForever()
    {
        var p = Path_("big.log");
        var gate = new object();

        // Push well past the 2 MiB budget.
        var chunk = new string('x', 64 * 1024);
        for (int i = 0; i < 80; i++) BoundedLog.Append(p, chunk, gate);

        Assert.True(File.Exists(p + ".1"), "the previous generation should be kept");
        Assert.True(new FileInfo(p + ".1").Length > 0, "the kept generation should not be empty");

        // The live file is back under budget, so the bound actually holds.
        Assert.True(new FileInfo(p).Length < BoundedLog.MaxBytes, "live log should be under the cap");
    }

    [Fact]
    public void OnlyTwoGenerationsAreKept()
    {
        var p = Path_("churn.log");
        var gate = new object();
        var chunk = new string('y', 64 * 1024);
        for (int i = 0; i < 200; i++) BoundedLog.Append(p, chunk, gate);

        Assert.True(File.Exists(p));
        Assert.True(File.Exists(p + ".1"));
        Assert.False(File.Exists(p + ".2"), "no third generation should accumulate");
    }

    [Fact]
    public void TotalBytesCountsBothGenerations()
    {
        var p = Path_("sum.log");
        var gate = new object();
        var chunk = new string('z', 64 * 1024);
        for (int i = 0; i < 80; i++) BoundedLog.Append(p, chunk, gate);

        var total = BoundedLog.TotalBytes(p);
        Assert.Equal(
            new FileInfo(p).Length + new FileInfo(p + ".1").Length,
            total);
    }

    [Fact]
    public void SmallLogIsNeverRotated()
    {
        var p = Path_("small.log");
        BoundedLog.Append(p, "tiny", _gate);
        Assert.False(File.Exists(p + ".1"), "a log under budget must not rotate");
    }

    [Fact]
    public void AppendNeverThrowsOnAnUnwritablePath()
    {
        // Logging is best-effort and must never break a tool call.
        var p = Path.Combine(_root, "nope", "\0invalid", "x.log");
        var ex = Record.Exception(() => BoundedLog.Append(p, "line", _gate));
        Assert.Null(ex);
    }

    [Fact]
    public void ResolvePlacesLogsUnderAppDataLoadingAi()
    {
        var p = BoundedLog.Resolve("unit-test.jsonl");
        Assert.EndsWith(Path.Combine("LoadingAI", "unit-test.jsonl"), p);
        Assert.Contains("Roaming", p);
    }

    [Fact]
    public void HostLogsAreRoutedThroughTheBoundedAppender()
    {
        var src = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI.Core", "Llm", "DebugLog.cs"));
        Assert.Contains("BoundedLog.Append", src);
        Assert.DoesNotContain("File.AppendAllText", src);
    }

    private readonly object _gate = new();
}
