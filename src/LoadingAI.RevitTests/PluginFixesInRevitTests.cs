using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Revit;
using LoadingAI.Tools;
using NUnit.Framework;

namespace LoadingAI.RevitTests;

/// <summary>
/// Live Revit 2027 checks for plan 2026-10-03-plugin-revit-fixes.md. Every test writes inside a
/// TransactionGroup that is rolled back in <c>finally</c>, so nothing persists. CI compiles this file
/// and does not run it (see README.md in this folder). A test that needs something the throwaway
/// project does not have calls Assert.Ignore and says what to load.
/// </summary>
[TestFixture]
public class PluginFixesInRevitTests
{
    private UIApplication _uiApp = null!;
    private RevitBridge? _bridge;
    private AdapterRevitBridge? _adapter;
    private string? _tempModelPath;
    private Document? _ownedDoc;

    private AdapterRevitBridge Adapter
        => _adapter ??= new AdapterRevitBridge(_bridge ?? throw new InvalidOperationException("RevitBridge was not created."));

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
            _tempModelPath = Path.Combine(Path.GetTempPath(), $"LoadingAI.PluginFixes.{Guid.NewGuid():N}.rvt");
            doc.SaveAs(_tempModelPath);
            doc.Close(false);
            _ownedDoc = _uiApp.OpenAndActivateDocument(_tempModelPath)?.Document;
            _bridge.AttachCurrentThread(_uiApp);
        }
        catch (Exception ex)
        {
            Assert.Ignore("Could not create a throwaway project for the plugin-fix tests: " + ex.Message);
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
        catch { /* Revit may already have closed it. */ }

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
            Assert.Ignore("Open a Revit model before running the plugin-fix tests.");
    }

    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static Level NewLevel(Document doc, string name, double elevationMm)
    {
        using var tx = new Transaction(doc, "LoadingAI.RevitTests level " + name);
        tx.Start();
        var level = Level.Create(doc, Metric.MmToFt(elevationMm));
        level.Name = name;
        tx.Commit();
        return level;
    }

    private static Wall NewWall(Document doc, Level level, double x1Mm, double y1Mm, double x2Mm, double y2Mm, double heightMm = 3000)
    {
        var type = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
        using var tx = new Transaction(doc, "LoadingAI.RevitTests wall");
        tx.Start();
        var wall = Wall.Create(
            doc,
            Line.CreateBound(new XYZ(Metric.MmToFt(x1Mm), Metric.MmToFt(y1Mm), 0), new XYZ(Metric.MmToFt(x2Mm), Metric.MmToFt(y2Mm), 0)),
            type.Id, level.Id, Metric.MmToFt(heightMm), 0, false, false);
        tx.Commit();
        return wall;
    }

    /// <summary>A loaded family type that stands on a level by itself, inactive ones first.</summary>
    private static FamilySymbol? LevelBasedSymbol(Document doc)
        => new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => PlacementRules.Refusal(s.Name, s.Family.FamilyPlacementType.ToString()) == null)
            .OrderBy(s => s.IsActive)
            .FirstOrDefault();

    // ---- Task 16 -------------------------------------------------------------------------

    [Test]
    public void CreateFamilyInstance_PutsZAboveTheNamedLevel_AndActivatesInsideTheTransaction()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var symbol = LevelBasedSymbol(doc);
        if (symbol == null) Assert.Ignore("Load a level-based family (a column or a piece of furniture) into the open model.");

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_family_instance z");
        group.Start();
        try
        {
            var upper = NewLevel(doc, "LAI-Test Upper", 6000);
            var result = Run(Adapter.CreateFamilyInstanceAsync(symbol!.Name, 0, 0, symbol.FamilyName, upper.Name, 500));

            Assert.That(result.Ok, Is.True, result.Message);
            var instance = doc.GetElement(AllTools.ParseEid(result.ElementIds![0]));
            var z = ((LocationPoint)instance.Location).Point.Z;
            Assert.That(z, Is.EqualTo(upper.Elevation + Metric.MmToFt(500)).Within(1e-4), "z is a height above the level, not an absolute elevation (R3)");
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void CreateFamilyInstance_UnknownLevel_IsRefused_NotPlacedOnTheFirstLevel()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var symbol = LevelBasedSymbol(doc);
        if (symbol == null) Assert.Ignore("Load a level-based family into the open model.");

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_family_instance level");
        group.Start();
        try
        {
            var before = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount();
            var result = Run(Adapter.CreateFamilyInstanceAsync(symbol!.Name, 0, 0, symbol.FamilyName, "No Such Level"));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("No Such Level"));
            Assert.That(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount(), Is.EqualTo(before));
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void CreateWall_UsesTheRequestedHeight_AndABasicType()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_wall height");
        group.Start();
        try
        {
            var result = Run(Adapter.CreateWallAsync(level.Name, 0, 0, 4000, 0, null, 3500));

            Assert.That(result.Ok, Is.True, result.Message);
            var wall = (Wall)doc.GetElement(AllTools.ParseEid(result.ElementIds![0]));
            Assert.That(wall.WallType.Kind, Is.EqualTo(WallKind.Basic));
            Assert.That(wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble(), Is.EqualTo(Metric.MmToFt(3500)).Within(1e-4));
        }
        finally
        {
            group.RollBack();
        }
    }

    // ---- Task 1 of the main plan (R1). It passes once unit-aware parameter writes are merged. --------

    [Test]
    public void SetParameter_LengthIsMillimetres_NotFeet()
    {
        var doc = _uiApp.ActiveUIDocument.Document;

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests set_parameter units");
        group.Start();
        try
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var wall = NewWall(doc, level, 0, 0, 4000, 0);

            var result = Run(Adapter.SetParameterAsync(wall.Id.ToString(), "Unconnected Height", "3000"));

            Assert.That(result.Ok, Is.True, result.Message);
            Assert.That(
                wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble(),
                Is.EqualTo(Metric.MmToFt(3000)).Within(1e-4),
                "R1: 3000 means 3000 mm; before the units task it stored 3000 feet");
        }
        finally
        {
            group.RollBack();
        }
    }

    // ---- Task 17 -------------------------------------------------------------------------

    [Test]
    public void MoveElements_MovesAHostedDoorWithItsWallExactlyOnce()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted);
        if (symbol == null) Assert.Ignore("Load a door or window family (a wall-hosted one) into the open model.");

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests move wall and door");
        group.Start();
        try
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var wall = NewWall(doc, level, 0, 0, 6000, 0);
            FamilyInstance door;
            using (var tx = new Transaction(doc, "LoadingAI.RevitTests door"))
            {
                tx.Start();
                if (!symbol!.IsActive) symbol.Activate();
                door = doc.Create.NewFamilyInstance(
                    new XYZ(Metric.MmToFt(3000), 0, level.Elevation), symbol, wall, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                tx.Commit();
            }

            var before = ((LocationPoint)door.Location).Point.X;
            var result = Run(Adapter.MoveElementsAsync(new[] { wall.Id.ToString(), door.Id.ToString() }, 1000, 0, 0));

            Assert.That(result.Ok, Is.True, result.Message);
            var after = ((LocationPoint)door.Location).Point.X;
            Assert.That(after - before, Is.EqualTo(Metric.MmToFt(1000)).Within(1e-4), "the door moves with its wall once, not twice (R10)");
        }
        finally
        {
            group.RollBack();
        }
    }
}
