using System.IO;
using System.Text.Json;
using LoadingAI.Plugins;
using LoadingAI.Plugins.Core;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ShippedManifestTests
{
    private static (string Folder, IRevitToolPlugin Plugin)[] Shipped() => new (string, IRevitToolPlugin)[]
    {
        ("core-tools", new LoadingAI.Plugins.CoreTools.CoreToolsPlugin()),
        ("export-tools", new LoadingAI.Plugins.ExportTools.ExportToolsPlugin()),
        ("view-tools", new LoadingAI.Plugins.ViewTools.ViewToolsPlugin()),
        ("modeling-tools", new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()),
        ("structure-cuts", new LoadingAI.Plugins.StructureCuts.StructureCutsPlugin()),
        ("qs-formwork", new LoadingAI.Plugins.QsFormwork.QsFormworkPlugin()),
        ("opening-tools", new LoadingAI.Plugins.OpeningTools.OpeningToolsPlugin())
    };

    private static PluginMeta Manifest(string folder)
    {
        var json = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", folder, "plugin.json"));
        return JsonSerializer.Deserialize<PluginMeta>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void EveryShippedManifest_MatchesItsCode()
    {
        var context = new FakePluginContext(new FakeRevitBridge { HasActiveDocument = true });
        foreach (var (folder, plugin) in Shipped())
        {
            var meta = Manifest(folder);
            Assert.Equal(plugin.Id, meta.Id);
            Assert.Equal(plugin.Version, meta.Version);
            Assert.Empty(PluginLoader.ManifestDrift(meta, plugin.CreateTools(context)));
        }
    }

    [Fact]
    public void ManifestDrift_ReportsMissingExtraTierAndDefault()
    {
        ToolEntry Entry(string name, ToolTier tier) => new()
        {
            Tier = tier,
            Definition = new LoadingAI.Llm.ToolDefinition { Function = new LoadingAI.Llm.ToolFunction { Name = name } }
        };

        var meta = new PluginMeta
        {
            Tools =
            {
                new PluginToolMeta { Name = "read", Tier = "L1", EnabledByDefault = true },
                new PluginToolMeta { Name = "write", Tier = "L1", EnabledByDefault = true },
                new PluginToolMeta { Name = "danger", Tier = "L3", EnabledByDefault = true },
                new PluginToolMeta { Name = "ghost", Tier = "L1", EnabledByDefault = true }
            }
        };
        var problems = PluginLoader.ManifestDrift(meta, new[]
        {
            Entry("read", ToolTier.L1), Entry("write", ToolTier.L2), Entry("danger", ToolTier.L3), Entry("extra", ToolTier.L1)
        });

        Assert.Contains(problems, p => p.Contains("'write'") && p.Contains("L1") && p.Contains("L2"));
        Assert.Contains(problems, p => p.Contains("'danger'") && p.Contains("enabledByDefault"));
        Assert.Contains(problems, p => p.Contains("'ghost'") && p.Contains("registers no such tool"));
        Assert.Contains(problems, p => p.Contains("'extra'") && p.Contains("does not list"));
        Assert.DoesNotContain(problems, p => p.Contains("'read'"));
    }

    [Fact]
    public void Source_LoadOneChecksDriftAndLogsIt()
    {
        var src = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
        Assert.Contains("ManifestDrift(meta, ts)", src);
    }
}
