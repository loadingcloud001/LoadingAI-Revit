using System.IO;
using Xunit;

namespace LoadingAI.Tests;

public class PluginProjectContractTests
{
    [Theory]
    [InlineData("Plugins/core-tools/CoreToolsPlugin.csproj", false)]
    [InlineData("Plugins/export-tools/ExportToolsPlugin.csproj", true)]
    [InlineData("Plugins/view-tools/ViewToolsPlugin.csproj", true)]
    [InlineData("Plugins/modeling-tools/ModelingToolsPlugin.csproj", true)]
    [InlineData("Plugins/structure-cuts/StructureCutsPlugin.csproj", true)]
    [InlineData("Plugins/qs-formwork/QsFormworkPlugin.csproj", true)]
    public void ShippedPlugin_DoesNotReferenceHost(string relative, bool requiresRevitUtil)
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.DoesNotContain("src\\LoadingAI\\LoadingAI.csproj", csproj);
        Assert.DoesNotContain("src/LoadingAI/LoadingAI.csproj", csproj);
        Assert.Contains("LoadingAI.Core.csproj", csproj);
        if (requiresRevitUtil)
            Assert.Contains("LoadingAI.RevitUtil.csproj", csproj);
        else
        {
            Assert.DoesNotContain("LoadingAI.RevitUtil.csproj", csproj);
            Assert.DoesNotContain("RevitAPI", csproj);
            Assert.DoesNotContain("Nice3point.Revit", csproj);
        }
    }

    [Fact]
    public void HelloPlugin_IsBridgeOnly()
    {
        var root = RepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "Plugins", "example.hello", "HelloPlugin.csproj"));
        var src = File.ReadAllText(Path.Combine(root, "Plugins", "example.hello", "HelloPlugin.cs"));
        Assert.DoesNotContain("LoadingAI.csproj", csproj);
        Assert.Contains("LoadingAI.Core.csproj", csproj);
        Assert.DoesNotContain("IRevitFullAccess", src);
        Assert.Contains("context.Bridge", src);
    }

    [Fact]
    public void NonFullAccessPlugins_DoNotMentionAutodeskRevit()
    {
        var pluginsDir = Path.Combine(RepoRoot(), "Plugins");
        Assert.True(Directory.Exists(pluginsDir), "Plugins/ missing");
        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var csFiles = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            if (csFiles.Length == 0) continue;
            var combined = string.Join("\n", csFiles.Select(File.ReadAllText));
            if (combined.IndexOf("IRevitFullAccess", StringComparison.Ordinal) >= 0)
                continue;

            Assert.DoesNotContain("Autodesk.Revit", combined);
            foreach (var csprojPath in Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories))
            {
                var csproj = File.ReadAllText(csprojPath);
                Assert.DoesNotContain("RevitAPI", csproj);
                Assert.DoesNotContain("Nice3point.Revit", csproj);
            }
        }
    }

    [Fact]
    public void CoreToolsPlugin_IsBridgeOnly_NoFullAccessType()
    {
        var root = RepoRoot();
        var bridge = File.ReadAllText(Path.Combine(root, "Plugins", "core-tools", "CoreToolsPlugin.cs"));
        Assert.DoesNotContain("IRevitFullAccess", bridge);
        Assert.Contains("CreateBridge", bridge);
        Assert.Contains("Id => \"core-tools\"", bridge);
        Assert.False(File.Exists(Path.Combine(root, "Plugins", "core-tools", "CoreToolsFullAccessPlugin.cs")));
        var pluginAllTools = File.ReadAllText(Path.Combine(root, "Plugins", "core-tools", "AllTools.cs"));
        Assert.DoesNotContain("CreateFullAccess", pluginAllTools);
        Assert.DoesNotContain("Autodesk.Revit", pluginAllTools);
        Assert.DoesNotContain("IRevitFullAccess", pluginAllTools);
    }

    [Fact]
    public void CoreToolsCoreSources_DoNotMentionAutodeskOrFullAccess()
    {
        var coreDir = Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core");
        foreach (var path in Directory.GetFiles(coreDir, "*.cs"))
        {
                var src = File.ReadAllText(path);
                Assert.False(src.Contains("Autodesk.Revit"), path + " must not mention Autodesk.Revit");
                Assert.False(src.Contains("IRevitFullAccess"), path + " must not mention IRevitFullAccess");
                Assert.False(src.Contains("FullAccess"), path + " must not use FullAccess");
        }
    }

    [Fact]
    public void PluginContext_CachesAdapterRevitBridge()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginContext.cs"));
        Assert.Contains("private readonly IRevitBridge _bridge;", src);
        Assert.Contains("_bridge = new AdapterRevitBridge(_raw);", src);
        Assert.Contains("public IRevitBridge Bridge => _bridge;", src);
        Assert.DoesNotContain("public IRevitBridge Bridge => new AdapterRevitBridge", src);
    }

    [Fact]
    public void QueryViewParamFactories_SucceedWithNullFullAccess()
    {
        var ctx = new Fakes.FakePluginContext(new Fakes.FakeRevitBridge { HasActiveDocument = true });
        Assert.Null(ctx.FullAccess);
        Assert.Contains(LoadingAI.Tools.QueryTools.Create(ctx), t => t.Definition.Function.Name == "get_model_summary");
        Assert.Contains(LoadingAI.Tools.QueryTools.Create(ctx), t => t.Definition.Function.Name == "get_worksets");
        Assert.Contains(LoadingAI.Tools.QueryTools.Create(ctx), t => t.Definition.Function.Name == "get_phases");
        Assert.Contains(LoadingAI.Tools.QueryTools.Create(ctx), t => t.Definition.Function.Name == "get_clash_by_bb");
        Assert.Contains(LoadingAI.Tools.QueryTools.Create(ctx), t => t.Definition.Function.Name == "get_model_health_score");
        Assert.Contains(LoadingAI.Tools.QueryTools.Create(ctx), t => t.Definition.Function.Name == "sync_info");
        Assert.Contains(LoadingAI.Tools.QueryTools.Create(ctx), t => t.Definition.Function.Name == "audit_warnings_fix");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "isolate_in_view");
        Assert.Contains(LoadingAI.Tools.ParamTools.Create(ctx), t => t.Definition.Function.Name == "set_parameter_by_id");
        Assert.Contains(LoadingAI.Tools.ParamTools.Create(ctx), t => t.Definition.Function.Name == "rename_elements");
        Assert.Contains(LoadingAI.Tools.ParamTools.Create(ctx), t => t.Definition.Function.Name == "apply_construction_dates");
        Assert.Contains(LoadingAI.Tools.ParamTools.Create(ctx), t => t.Definition.Function.Name == "export_params_to_excel");
        Assert.Contains(LoadingAI.Tools.ParamTools.Create(ctx), t => t.Definition.Function.Name == "import_excel_to_params");
        Assert.Contains(LoadingAI.Tools.ParamTools.Create(ctx), t => t.Definition.Function.Name == "copy_params");
        Assert.Contains(LoadingAI.Tools.ParamTools.Create(ctx), t => t.Definition.Function.Name == "replace_text");
        Assert.Contains(LoadingAI.Tools.ParamTools.Create(ctx), t => t.Definition.Function.Name == "numbering_auto");
        Assert.Contains(LoadingAI.Tools.GeometryTools.Create(ctx), t => t.Definition.Function.Name == "create_wall");
        Assert.Contains(LoadingAI.Tools.GeometryTools.Create(ctx), t => t.Definition.Function.Name == "create_floor_by_outline");
        Assert.Contains(LoadingAI.Tools.DocumentTools.Create(ctx), t => t.Definition.Function.Name == "save_document");
        Assert.Contains(LoadingAI.Tools.DocumentTools.Create(ctx), t => t.Definition.Function.Name == "reload_links");
        Assert.Contains(LoadingAI.Tools.DocumentTools.Create(ctx), t => t.Definition.Function.Name == "workset_assign");
        Assert.DoesNotContain(LoadingAI.Tools.QueryTools.Create(ctx), t => t.Definition.Function.Name == "save_document");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "override_color_in_view");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "color_by_construction_schedule");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "color_by_parameter");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "create_sheet");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "place_view_on_sheet");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "duplicate_view_ex");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "set_view_template");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "set_crop_box");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "create_dimension");
        Assert.Contains(LoadingAI.Tools.ViewTools.Create(ctx), t => t.Definition.Function.Name == "tag_all");
        // Host AllTools is parse wrappers; plugin AllTools.CreateBridge lives in
        // Plugins/core-tools/AllTools.cs (same type name — do not call it from this assembly).
        var pluginAllTools = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "AllTools.cs"));
        Assert.DoesNotContain("ViewColorTools", pluginAllTools);
        Assert.DoesNotContain("ParamMutateTools", pluginAllTools);
        Assert.DoesNotContain("QueryHealthTools", pluginAllTools);
        Assert.DoesNotContain("ParamExcelTools", pluginAllTools);
        Assert.DoesNotContain("SheetDocTools", pluginAllTools);
        Assert.DoesNotContain("WorksharingTools", pluginAllTools);
        Assert.DoesNotContain("IfcCostTools", pluginAllTools);
        Assert.DoesNotContain("CreateFullAccess", pluginAllTools);
        Assert.DoesNotContain("Autodesk.Revit", pluginAllTools);
        var querySrc = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.Contains("Name = \"get_worksets\"", querySrc);
        Assert.Contains("Name = \"get_phases\"", querySrc);
        Assert.Contains("Name = \"get_clash_by_bb\"", querySrc);
        Assert.Contains("Name = \"get_model_health_score\"", querySrc);
        Assert.Contains("Name = \"sync_info\"", querySrc);
        Assert.Contains("Name = \"audit_warnings_fix\"", querySrc);
        Assert.Contains("Name = \"quantity_takeoff_by_category\"", querySrc);
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
