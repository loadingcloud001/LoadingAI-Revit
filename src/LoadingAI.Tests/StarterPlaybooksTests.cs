using System.IO;
using System.Linq;
using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class StarterPlaybooksTests
{
    /// <summary>
    /// Tools the same release adds that are not in a plugin.json on this branch:
    /// the core-tools query resolve_names and the two host tools (save_project_note,
    /// open_playbook). A playbook may name these.
    /// </summary>
    private static readonly string[] PendingTools =
    {
        "resolve_names", "save_project_note", "open_playbook"
    };

    [Fact]
    public void AllFourStarters_ParseAndOnlyNameRealTools()
    {
        var dir = Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Playbooks");
        var cat = PlaybookCatalog.Load(dir, null);
        Assert.Empty(cat.Issues);
        Assert.Equal(new[] { "beam-split", "export-package", "model-qa", "sheet-setup" }, cat.Items.Select(p => p.Name).ToArray());

        var pluginDir = Path.Combine(AdapterSource.RepoRoot(), "Plugins");
        var known = File.ReadAllText(Path.Combine(pluginDir, "core-tools", "plugin.json"))
                  + string.Concat(Directory.GetFiles(pluginDir, "plugin.json", SearchOption.AllDirectories).Select(File.ReadAllText))
                  + " " + string.Join(" ", PendingTools.Select(t => $"\"{t}\""));
        foreach (var tool in cat.Items.SelectMany(p => p.Tools))
            Assert.Contains($"\"{tool}\"", known);
    }

    [Fact]
    public void PackScript_CopiesPlaybooks()
        => Assert.Contains("Playbooks", File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "scripts", "pack-addin.ps1")));
}
