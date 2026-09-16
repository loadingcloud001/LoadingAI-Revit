using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Plugins.Core;
using LoadingAI.Revit;
using LoadingAI.Tools;
using NUnit.Framework;

namespace LoadingAI.RevitTests;

/// <summary>
/// First in-Revit slice. Runs on the Revit UI thread (ricaun / TestRunner).
/// Uses <see cref="RevitBridge.AttachCurrentThread"/> so awaiting the adapter
/// cannot deadlock on ExternalEvent.
/// Do not exercise App.Bridge, ribbon, HostPage, WebView2, or TaskDialog here.
/// </summary>
[TestFixture]
public class InRevitSmokeTests
{
    private UIApplication _uiApp = null!;
    private RevitBridge? _bridge;
    private string? _tempModelPath;
    private Document? _ownedDoc;

    [OneTimeSetUp]
    public void OneTimeSetUp(UIApplication uiApp)
    {
        _uiApp = uiApp;
        _bridge = new RevitBridge();
        _bridge.AttachCurrentThread(_uiApp);
        if (_uiApp.ActiveUIDocument?.Document != null)
            return;

        try
        {
            var doc = _uiApp.Application.NewProjectDocument(UnitSystem.Metric);
            _tempModelPath = Path.Combine(
                Path.GetTempPath(),
                $"LoadingAI.RevitTests.{Guid.NewGuid():N}.rvt");
            doc.SaveAs(_tempModelPath);
            doc.Close(false);
            _ownedDoc = _uiApp.OpenAndActivateDocument(_tempModelPath)?.Document;
            _bridge.AttachCurrentThread(_uiApp);
        }
        catch (Exception ex)
        {
            Assert.Ignore(
                "Could not create a throwaway project for in-Revit tests: " + ex.Message);
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try { _bridge?.Dispose(); } catch { }
        _bridge = null;
        try
        {
            if (_ownedDoc is { IsValidObject: true })
                _ownedDoc.Close(false);
        }
        catch
        {
            // Revit may already have closed the document with the host process.
        }

        if (!string.IsNullOrEmpty(_tempModelPath))
        {
            try { File.Delete(_tempModelPath); } catch { /* temp leftover is fine */ }
            try { File.Delete(_tempModelPath + ".backup"); } catch { }
        }
    }

    [SetUp]
    public void RequireDocument()
    {
        if (_uiApp.ActiveUIDocument?.Document == null)
            Assert.Ignore("Open a Revit model before running in-Revit tests.");
    }

    [Test]
    public void ActiveDocument_IsOpen()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        Assert.That(doc, Is.Not.Null);
        Assert.That(doc.Title, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void GetModelSummary_ViaAdapter_ReturnsFormattedModel()
    {
        // ExternalEvent.Create is only legal in a standard API execution.
        // ricaun's async NUnit continuation is not one — reuse the fixture bridge
        // constructed in OneTimeSetUp. AttachCurrentThread makes RunAsync
        // invoke immediately, so GetAwaiter().GetResult() cannot deadlock.
        var raw = _bridge ?? throw new InvalidOperationException("RevitBridge was not created.");
        var ctx = new PluginContext(raw, "builtin", fullAccessGranted: false);
        var tool = QueryTools.Create(ctx).Single(t => t.Definition.Function.Name == "get_model_summary");

        var text = tool.Handler("{}").GetAwaiter().GetResult();

        Assert.That(text, Does.StartWith("Model:"));
        Assert.That(text, Does.Contain("Views:"));
        Assert.That(text, Does.Contain("Walls:"));
        Assert.That(raw.HasActiveDocument, Is.True);
    }

    [Test]
    public void PluginLoader_Discover_RegistersCoreQueryTool()
    {
        var raw = _bridge ?? throw new InvalidOperationException("RevitBridge was not created.");
        var plugins = PluginLoader.Discover(raw, new PluginContextFactory(raw));

        var core = plugins.FirstOrDefault(p => p.Meta.Id == "core-tools");
        if (core == null)
            Assert.Ignore("core-tools is not in AppData; rebuild the host without SkipHostPluginBuild.");
        var names = core.Tools.Select(t => t.Definition.Function.Name).ToList();
        Assert.That(names, Does.Contain("get_model_summary"));
        Assert.That(names, Does.Contain("isolate_in_view"));
        Assert.That(names, Does.Contain("set_parameter_by_id"));
    }

    [Test]
    public void L2_Transaction_RollsBack()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var info = doc.ProjectInformation;
        var param = info.get_Parameter(BuiltInParameter.PROJECT_NUMBER)
            ?? info.LookupParameter("Comments");
        if (param == null || param.IsReadOnly || param.StorageType != StorageType.String)
            Assert.Ignore("No writable string parameter on Project Information.");

        var original = param.AsString() ?? "";
        using (var tx = new Transaction(doc, "LoadingAI.RevitTests rollback probe"))
        {
            tx.Start();
            param.Set("qaqc-rollback-probe");
            Assert.That(param.AsString(), Is.EqualTo("qaqc-rollback-probe"));
            tx.RollBack();
        }

        Assert.That(param.AsString() ?? "", Is.EqualTo(original));
    }
}
