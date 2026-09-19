using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Plugins.Core;
using LoadingAI.Plugins.StructureCuts;
using LoadingAI.Revit;
using LoadingAI.Tools;
using NUnit.Framework;

namespace LoadingAI.RevitTests;

/// <summary>
/// Kind B bridge-only coverage for structure-cuts. The plugin owns no Revit types any
/// more — every algorithm lives host-side behind <see cref="IRevitBridge"/> — so these
/// tests drive the real Adapter through the plugin's handlers and assert on the prose
/// the tools return. Construct tools with CreateTools —
/// do not PluginLoader.Discover this plugin here (Discover wraps core L2 with TaskDialog).
/// </summary>
[TestFixture]
public class StructureCutsInRevitTests
{
    private UIApplication _uiApp = null!;
    private RevitBridge? _bridge;
    private string? _tempModelPath;
    private Document? _ownedDoc;
    private string? _pluginDir;
    private Dictionary<string, ToolEntry>? _tools;

    [OneTimeSetUp]
    public void OneTimeSetUp(UIApplication uiApp)
    {
        _uiApp = uiApp;
        _bridge = new RevitBridge();
        _bridge.AttachCurrentThread(_uiApp);
        if (_uiApp.ActiveUIDocument?.Document == null)
        {
            try
            {
                var created = _uiApp.Application.NewProjectDocument(UnitSystem.Metric);
                _tempModelPath = Path.Combine(
                    Path.GetTempPath(),
                    $"LoadingAI.RevitTests.structure-cuts.{Guid.NewGuid():N}.rvt");
                created.SaveAs(_tempModelPath);
                created.Close(false);
                _ownedDoc = _uiApp.OpenAndActivateDocument(_tempModelPath)?.Document;
                _bridge.AttachCurrentThread(_uiApp);
            }
            catch (Exception ex)
            {
                Assert.Ignore(
                    "Could not create a throwaway project for structure-cuts tests: " + ex.Message);
            }
        }

        _pluginDir = Path.Combine(Path.GetTempPath(), "LoadingAI.RevitTests.structure-cuts");
        Directory.CreateDirectory(_pluginDir);
        var raw = _bridge ?? throw new InvalidOperationException("RevitBridge was not created.");
        var ctx = new PluginContext(raw, _pluginDir, fullAccessGranted: true);
        _tools = new StructureCutsPlugin()
            .CreateTools(ctx)
            .ToDictionary(t => t.Definition.Function.Name, StringComparer.Ordinal);
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
            try { File.Delete(_tempModelPath); } catch { }
            try { File.Delete(_tempModelPath + ".backup"); } catch { }
        }
    }

    [SetUp]
    public void RequireDocument()
    {
        if (_uiApp.ActiveUIDocument?.Document == null)
            Assert.Ignore("Open a Revit model before running in-Revit tests.");
        if (_tools == null)
            Assert.Ignore("structure-cuts tools were not created.");
        try
        {
            _uiApp.ActiveUIDocument.Selection.SetElementIds(new List<ElementId>());
        }
        catch
        {
            // Selection clear is best-effort for the empty-selection dry-run.
        }
    }

    [Test]
    public void CreateTools_RegistersFourL3SplitTools()
    {
        Assert.That(_tools!.Keys, Does.Contain("split_beams_by_columns"));
        Assert.That(_tools.Keys, Does.Contain("split_floors_by_beams"));
        Assert.That(_tools.Keys, Does.Contain("split_by_levels"));
        Assert.That(_tools.Keys, Does.Contain("split_by_grids"));
        Assert.That(_tools.Values.Select(t => t.Tier).Distinct().ToArray(), Is.EqualTo(new[] { ToolTier.L3 }));
    }

    [Test]
    public void DryRun_EmptySelection_AsksToSelectElements()
    {
        foreach (var name in new[]
        {
            "split_beams_by_columns",
            "split_floors_by_beams",
            "split_by_levels",
            "split_by_grids"
        })
        {
            var text = Run(name, "{\"dryRun\":true}");
            Assert.That(text, Is.EqualTo("Please select elements first."), name);
        }
    }

    [Test]
    public void DryRun_ScopeAll_EmptyMetric_ReturnsSkipReasons()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        if (CountCategory(doc, BuiltInCategory.OST_StructuralFraming) == 0)
        {
            Assert.That(
                Run("split_beams_by_columns", "{\"dryRun\":true,\"scope\":\"all\"}"),
                Is.EqualTo("No beams found."));
        }

        if (CountClass(doc, typeof(Floor)) == 0)
        {
            Assert.That(
                Run("split_floors_by_beams", "{\"dryRun\":true,\"scope\":\"all\"}"),
                Is.EqualTo("No floors found."));
        }

        if (CountHosts(doc) == 0)
        {
            Assert.That(
                Run("split_by_levels", "{\"dryRun\":true,\"scope\":\"all\"}"),
                Is.EqualTo("No hosts found."));
            Assert.That(
                Run("split_by_grids", "{\"dryRun\":true,\"scope\":\"all\"}"),
                Is.EqualTo("No hosts found."));
        }
        else
        {
            var levels = Run("split_by_levels", "{\"dryRun\":true,\"scope\":\"all\"}");
            Assert.That(levels, Does.StartWith("Dry-run.").Or.EqualTo("No levels found."));
            var grids = Run("split_by_grids", "{\"dryRun\":true,\"scope\":\"all\"}");
            Assert.That(grids, Does.StartWith("Dry-run.").Or.EqualTo("No grids found.").Or.EqualTo("No hosts found."));
        }
    }

    [Test]
    public void Write_SplitByLevels_ThenTransactionGroupRollsBack()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var wallsBefore = CountWalls(doc);
        long? wallId = null;

        using (var group = new TransactionGroup(doc, "LoadingAI.RevitTests structure-cuts write probe"))
        {
            group.Start();
            try
            {
                var wall = TryCreateTallWall(doc);
                if (wall == null)
                    Assert.Ignore("No wall type / levels available to probe a write.");

                wallId = wall.Id.Value;
                var json =
                    "{\"dryRun\":false,\"elementIds\":[\""
                    + wallId.Value
                    + "\"],\"scope\":\"selection\"}";
                var text = Run("split_by_levels", json);
                Assert.That(text, Does.Not.Contain("Please open a model first."));
                Assert.That(
                    text,
                    Does.Contain("split").IgnoreCase
                        .Or.Contain("Nothing to split")
                        .Or.Contain("No levels found."));
            }
            finally
            {
                group.RollBack();
            }
        }

        Assert.That(CountWalls(doc), Is.EqualTo(wallsBefore));
        if (wallId != null)
            Assert.That(doc.GetElement(new ElementId(wallId.Value)), Is.Null);
    }

    private string Run(string toolName, string json)
    {
        var raw = _bridge ?? throw new InvalidOperationException("RevitBridge was not created.");
        raw.AttachCurrentThread(_uiApp);
        return _tools![toolName].Handler(json).GetAwaiter().GetResult();
    }

    private static int CountWalls(Document doc) => CountClass(doc, typeof(Wall));

    private static int CountClass(Document doc, Type type)
        => new FilteredElementCollector(doc)
            .OfClass(type)
            .WhereElementIsNotElementType()
            .GetElementCount();

    private static int CountCategory(Document doc, BuiltInCategory category)
        => new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .GetElementCount();

    private static int CountHosts(Document doc)
        => CountClass(doc, typeof(Wall))
            + CountCategory(doc, BuiltInCategory.OST_StructuralColumns)
            + CountCategory(doc, BuiltInCategory.OST_StructuralFraming)
            + CountClass(doc, typeof(Floor));

    private static Wall? TryCreateTallWall(Document doc)
    {
        var wallType = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(t => t.Kind == WallKind.Basic);
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();
        if (wallType == null || levels.Count == 0)
            return null;

        var height = levels.Count > 1
            ? Math.Max(20.0, (levels[1].Elevation - levels[0].Elevation) * 2.0)
            : 20.0;
        var line = Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0));

        using var tx = new Transaction(doc, "LoadingAI.RevitTests create probe wall");
        tx.Start();
        try
        {
            var wall = Wall.Create(doc, line, wallType.Id, levels[0].Id, height, 0, false, false);
            tx.Commit();
            return wall;
        }
        catch
        {
            if (tx.GetStatus() == TransactionStatus.Started)
                tx.RollBack();
            return null;
        }
    }
}
