using System;
using System.IO;
using System.Linq;
using LoadingAI.Llm;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// The two host tools: the note the agent proposes and the playbook the user wrote.
/// A note is L2 and never allow-listed (ADR-0018); a playbook is read-only and L1.
/// </summary>
public class HostToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ht-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task SaveProjectNote_IsL2_NeverAllowListed_AndSavesToTheProjectKey()
    {
        var store = new NoteStore(_dir);
        var tools = HostTools.Create(store, () => "k1", () => PlaybookCatalog.Load(null, null));
        var save = tools.Single(t => t.Definition.Function.Name == "save_project_note");
        Assert.Equal(ToolTier.L2, save.Tier);
        Assert.True(save.NeverAllowList);
        Assert.False(save.Destructive);
        var r = await save.ExecuteAsync(@"{""text"":""Beams are UB.""}");
        Assert.Equal(ToolExecutionStatus.Succeeded, r.Status);
        Assert.Equal("agent", store.List("k1").Single().Source);
        Assert.NotNull(save.Describe);
        Assert.Contains("Beams are UB.", await save.Describe!(@"{""text"":""Beams are UB.""}"));
    }

    [Fact]
    public async Task SaveProjectNote_GlobalScope_AndNoProject()
    {
        var store = new NoteStore(_dir);
        var tools = HostTools.Create(store, () => null, () => PlaybookCatalog.Load(null, null));
        var save = tools.Single(t => t.Definition.Function.Name == "save_project_note");
        Assert.Equal(ToolExecutionStatus.Failed, (await save.ExecuteAsync(@"{""text"":""x""}")).Status);
        Assert.Equal(ToolExecutionStatus.Succeeded, (await save.ExecuteAsync(@"{""text"":""x"",""scope"":""global""}")).Status);
    }

    [Fact]
    public async Task OpenPlaybook_ReturnsTheBody_OrTheList()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.md"), "---\nname: model-qa\ndescription: d\n---\n1. step");
        var tools = HostTools.Create(new NoteStore(_dir), () => "k", () => PlaybookCatalog.Load(null, _dir));
        var open = tools.Single(t => t.Definition.Function.Name == "open_playbook");
        Assert.Equal(ToolTier.L1, open.Tier);
        Assert.Contains("1. step", (await open.ExecuteAsync(@"{""name"":""model-qa""}")).Text);
        var miss = await open.ExecuteAsync(@"{""name"":""nope""}");
        Assert.Equal(ToolExecutionStatus.Failed, miss.Status);
        Assert.Contains("model-qa", miss.Text);
    }
}
