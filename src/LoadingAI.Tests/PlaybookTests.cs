using System;
using System.IO;
using System.Linq;
using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class PlaybookTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Write(string folder, string file, string text)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, file);
        File.WriteAllText(p, text);
        return p;
    }

    private const string Good = "---\nname: model-qa\ndescription: Check warnings.\ntools: [get_warnings, capture_view]\nmode: build\n---\n1. Run get_warnings.\n";

    [Fact]
    public void Parse_ReadsHeaderAndBody()
    {
        var pb = PlaybookParser.Parse(Good, "x.md", "user", out var error);
        Assert.Null(error);
        Assert.Equal("model-qa", pb!.Name);
        Assert.Equal("Check warnings.", pb.Description);
        Assert.Equal(new[] { "get_warnings", "capture_view" }, pb.Tools);
        Assert.Equal("build", pb.Mode);
        Assert.Equal("1. Run get_warnings.", pb.Body);
    }

    [Theory]
    [InlineData("no header at all")]
    [InlineData("---\ndescription: x\n---\nbody")]
    [InlineData("---\nname: Bad Name!\ndescription: x\n---\nbody")]
    [InlineData("---\nname: ok\ndescription: x\nmode: bypass\n---\nbody")]
    [InlineData("---\nname: ok\ndescription: x\n---\n")]
    public void Parse_Rejects(string text)
    {
        Assert.Null(PlaybookParser.Parse(text, "x.md", "user", out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Catalog_UserOverridesStarter_AndReportsBadFiles()
    {
        Write("starters", "model-qa.md", Good);
        Write("starters", "export.md", Good.Replace("model-qa", "export-package"));
        Write("user", "mine.md", Good.Replace("Check warnings.", "My QA."));
        Write("user", "broken.md", "nope");
        var cat = PlaybookCatalog.Load(Path.Combine(_root, "starters"), Path.Combine(_root, "user"));
        Assert.Equal(new[] { "export-package", "model-qa" }, cat.Items.Select(p => p.Name).ToArray());
        Assert.Equal("My QA.", cat.Get("model-qa")!.Description);
        Assert.Equal("user", cat.Get("model-qa")!.Source);
        Assert.Contains(cat.Issues, i => i.Contains("broken.md"));
    }

    [Fact]
    public void ListBlock_NamesOnly_AtMost30()
    {
        for (int i = 0; i < 35; i++) Write("user", $"p{i}.md", Good.Replace("model-qa", $"p{i:00}"));
        var cat = PlaybookCatalog.Load(null, Path.Combine(_root, "user"));
        var block = cat.PromptList();
        Assert.Contains("open_playbook", block);
        Assert.DoesNotContain("Run get_warnings", block);
        Assert.Equal(30, block.Split('\n').Count(l => l.StartsWith("- p")));
    }

    [Fact]
    public void Compose_PutsTheUserTextAfterTheBody()
    {
        var pb = PlaybookParser.Parse(Good, "x.md", "user", out _)!;
        var text = PlaybookParser.Compose(pb, "only Level 2");
        Assert.StartsWith("Follow the playbook \"model-qa\":", text);
        Assert.EndsWith("only Level 2", text);
    }
}
