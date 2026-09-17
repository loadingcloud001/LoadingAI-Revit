using System.IO;
using Xunit;

namespace LoadingAI.Tests;

public class ToolCatalogContractTests
{
    [Fact]
    public void ModelingPlugin_DoesNotDuplicateCoreTools()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "modeling-tools", "ModelingToolsPlugin.cs"));
        Assert.DoesNotContain("Name = \"set_parameter\"", src);
        Assert.DoesNotContain("Name = \"place_family_instance\"", src);
        Assert.DoesNotContain("Name = \"list_phases\"", src);
        Assert.Contains("Name = \"load_family\"", src);
        Assert.Contains("Name = \"change_element_type\"", src);
    }

    [Fact]
    public void CoreKeepsCanonicalWriteTools()
    {
        var param = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "ParamTools.cs"));
        var geo = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "GeometryTools.cs"));
        var query = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.Contains("Name = \"set_parameter_by_id\"", param);
        Assert.Contains("Name = \"create_family_instance\"", geo);
        Assert.Contains("Name = \"get_phases\"", query);
    }

    [Fact]
    public void ToolSurface_DoesNotAdvertiseFeet()
    {
        foreach (var rel in new[]
        {
            "Plugins/core-tools/Core/QueryTools.cs",
            "Plugins/core-tools/Core/GeometryTools.cs",
            "Plugins/view-tools/ViewToolsPlugin.cs",
            "Plugins/modeling-tools/ModelingToolsPlugin.cs",
            "Plugins/structure-cuts/StructureCutsPlugin.cs",
            "Plugins/qs-formwork/QsFormworkPlugin.cs",
        })
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("in feet", src);
            Assert.DoesNotContain("(feet)", src);
            Assert.DoesNotContain("sq ft", src);
            Assert.DoesNotContain("cu ft", src);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln from " + AppContext.BaseDirectory);
    }
}
