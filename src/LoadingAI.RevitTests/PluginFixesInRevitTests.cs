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

    /// <summary>
    /// A level and a floor plan of their own, made the active view, so nothing in the open model (its
    /// walls, tags, or an active sheet) decides the outcome. Returns false with the reason when the
    /// model cannot host one.
    /// </summary>
    private static bool TryOwnPlan(UIDocument uidoc, string name, double elevationMm, out Level level, out ViewPlan view, out string why)
    {
        var doc = uidoc.Document;
        level = null!;
        view = null!;
        why = "";
        var planType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
        if (planType == null) { why = "The model has no floor plan view type."; return false; }

        level = NewLevel(doc, name, elevationMm);
        using (var tx = new Transaction(doc, "LoadingAI.RevitTests plan " + name))
        {
            tx.Start();
            view = ViewPlan.Create(doc, planType.Id, level.Id);
            tx.Commit();
        }

        try { uidoc.ActiveView = view; }
        catch (Exception ex) { why = "Could not activate the test view: " + ex.Message; return false; }
        return true;
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
            .Where(s => s.Category?.CategoryType == CategoryType.Model
                && s.Family.IsEditable && !s.Family.IsInPlace
                && s.Category.BuiltInCategory != BuiltInCategory.OST_CurtainWallPanels
                && PlacementRules.Refusal(s.Name, s.Family.FamilyPlacementType.ToString()) == null)
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
            Assert.That(result.ElementIds, Is.Not.Null.And.Not.Empty, "the tool names the instance it created: " + result.Message);
            var instance = doc.GetElement(RevitParse.ParseEid(result.ElementIds![0]));
            Assert.That(instance, Is.Not.Null, $"instance {result.ElementIds[0]} of '{symbol.FamilyName}: {symbol.Name}' ({symbol.Category?.Name}) is in the document");
            Assert.That(instance.Location, Is.InstanceOf<LocationPoint>(), $"'{symbol.FamilyName}: {symbol.Name}' places at a point");
            var z = ((LocationPoint)instance.Location).Point.Z;
            Assert.That(z, Is.EqualTo(upper.Elevation + Metric.MmToFt(500)).Within(1e-4),
                $"z is a height above the level, not an absolute elevation (R3). '{symbol.FamilyName}: {symbol.Name}' ({symbol.Category?.Name}, {symbol.Family.FamilyPlacementType}); level {upper.Elevation:F3} ft; tool said: {result.Message}");
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
            var wall = (Wall)doc.GetElement(RevitParse.ParseEid(result.ElementIds![0]));
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

    // ---- Task 18 -------------------------------------------------------------------------

    [Test]
    public void ColourOverrides_AreSolid_AndResetViewOverridesPutsTheOldLookBack()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var view = doc.ActiveView;
        if (view == null || view.IsTemplate) Assert.Ignore("Activate a normal, non-template view first.");

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests colour overrides");
        group.Start();
        try
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var wall = NewWall(doc, level, 0, 0, 4000, 0);
            Assert.That(view!.GetElementOverrides(wall.Id).ProjectionLineColor.IsValid, Is.False, "fixture: a new wall has no override");

            var painted = Run(Adapter.OverrideColorInViewAsync(new[] { wall.Id.ToString() }, null, "red"));
            Assert.That(painted.Ok, Is.True, painted.Message);
            var after = view.GetElementOverrides(wall.Id);
            Assert.That(after.ProjectionLineColor.Red, Is.EqualTo(255));
            Assert.That(after.SurfaceForegroundPatternId, Is.Not.EqualTo(ElementId.InvalidElementId), "a solid fill, so the colour shows when shaded (R7)");

            // A second colouring must not become the thing reset restores.
            Run(Adapter.OverrideColorInViewAsync(new[] { wall.Id.ToString() }, null, "blue"));

            var reset = Run(Adapter.ResetViewOverridesAsync());
            Assert.That(reset.Ok, Is.True, reset.Message);
            Assert.That(view.GetElementOverrides(wall.Id).ProjectionLineColor.IsValid, Is.False, "back to no override");
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void TagAll_PutsTheTagOnTheWall_NotOnTheOrigin()
    {
        var uidoc = _uiApp.ActiveUIDocument;
        var doc = uidoc.Document;
        var original = uidoc.ActiveView;

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests tag_all");
        group.Start();
        try
        {
            // The view is the test's own and holds one wall, so any tag owned by it is the wall's.
            if (!TryOwnPlan(uidoc, "LAI-Tag 0", 90000, out var level, out var view, out var why)) { Assert.Ignore(why); return; }
            NewWall(doc, level, 40000, 0, 44000, 0);

            var result = Run(Adapter.TagAllInActiveViewAsync("Walls"));
            if (!result.Ok) Assert.Ignore("Load a Wall Tag family into the open model. " + result.Message);

            var tags = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                .Where(t => t.OwnerViewId == view.Id).ToList();
            Assert.That(tags, Is.Not.Empty,
                $"tag_all said '{result.Message}' but no tag is owned by '{view.Name}' ({new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).GetElementCount()} tags in the model)");
            Assert.That(tags[0].TagHeadPosition.X, Is.EqualTo(Metric.MmToFt(42000)).Within(Metric.MmToFt(5000)), "R6: the tag sits on the wall, not at 0,0");
        }
        finally
        {
            try { uidoc.ActiveView = original; } catch { }
            group.RollBack();
        }
    }

    [Test]
    public void CreateDimension_BetweenTwoParallelWalls_MeasuresFaceToFace()
    {
        var uidoc = _uiApp.ActiveUIDocument;
        var doc = uidoc.Document;
        var original = uidoc.ActiveView;

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_dimension");
        group.Start();
        try
        {
            if (!TryOwnPlan(uidoc, "LAI-Dim 0", 95000, out var level, out _, out var why)) { Assert.Ignore(why); return; }
            var a = NewWall(doc, level, 0, 0, 5000, 0);
            var b = NewWall(doc, level, 0, 3000, 5000, 3000);

            var result = Run(Adapter.CreateDimensionBetweenWallsAsync(a.Id.ToString(), b.Id.ToString()));

            Assert.That(result.Ok, Is.True, result.Message + " (R5)");
            var dimension = (Dimension)doc.GetElement(RevitParse.ParseEid(result.ElementIds![0]));
            var expected = Metric.MmToFt(3000) - (a.Width + b.Width) / 2;
            Assert.That(dimension.Value!.Value, Is.EqualTo(expected).Within(1e-3));
        }
        finally
        {
            try { uidoc.ActiveView = original; } catch { }
            group.RollBack();
        }
    }

    [Test]
    public void ReloadLinks_WithNoLinks_SucceedsWithZeroOfZero()
    {
        var result = Run(Adapter.ReloadLinksAsync());
        if (new FilteredElementCollector(_uiApp.ActiveUIDocument.Document).OfClass(typeof(RevitLinkType)).GetElementCount() > 0)
            Assert.Ignore("This model has links; the outcome depends on them. Check R4 by hand.");

        Assert.That(result.Ok, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("Reloaded 0/0 RVT links"));
    }

    [Test]
    public void CreateSheet_WithAnUnknownTitleBlock_SaysWhatIsLoaded()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_sheet");
        group.Start();
        try
        {
            var result = Run(Adapter.CreateSheetAsync("LAI-T1", "Test", "No Such Block"));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("itle block"));
        }
        finally
        {
            group.RollBack();
        }
    }

    // ---- Task 19 -------------------------------------------------------------------------

    /// <summary>Scoped to the walls and levels the test made, so a model full of real walls cannot change the count.</summary>
    private static SplitRequestDto LevelRequest(IReadOnlyList<string> wallIds, IReadOnlyList<string> levelIds, int limit, int continueFrom, bool dryRun)
        => new(wallIds, levelIds, new[] { "walls" }, "all", continueFrom, limit, dryRun, false, true);

    /// <summary>Ids read once, up front: a write run deletes the walls it re-creates, and a deleted wall's Id throws.</summary>
    private static List<string> IdsOf(IEnumerable<Element> elements) => elements.Select(e => e.Id.ToString()).ToList();

    private static IEnumerable<Wall> AllWalls(Document doc)
        => new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>();

    [Test]
    public void SplitByLevels_InPages_LeavesNoWallUnsplit()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests split paging");
        group.Start();
        try
        {
            var baseLevel = NewLevel(doc, "LAI-Split 0", 0);
            var levels = new[] { baseLevel, NewLevel(doc, "LAI-Split 1", 3000), NewLevel(doc, "LAI-Split 2", 6000) };
            var wallIds = IdsOf(Enumerable.Range(0, 12).Select(i => NewWall(doc, baseLevel, 0, i * 1000, 5000, i * 1000, heightMm: 9000)));
            var levelIds = IdsOf(levels);

            var offset = 0;
            var pages = 0;
            while (true)
            {
                var result = Run(Adapter.SplitByLevelsAsync(LevelRequest(wallIds, levelIds, limit: 5, continueFrom: offset, dryRun: false)))!;
                Assert.That(result.Report, Does.Not.StartWith("Failed"), result.Report);
                pages++;
                var next = Regex.Match(result.Report, @"nextOffset = (\d+)");
                if (!next.Success) break;
                offset = int.Parse(next.Groups[1].Value);
                Assert.That(pages, Is.LessThan(10), "paging must terminate");
            }

            Assert.That(pages, Is.EqualTo(3), "12 walls at 5 per page");
            var testLevelIds = levels.Select(l => l.Id).ToHashSet();
            var tall = AllWalls(doc)
                .Where(w => testLevelIds.Contains(w.LevelId)
                    && w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() > Metric.MmToFt(3000) + 1e-3).ToList();
            Assert.That(tall, Is.Empty, "R8: every 9 m wall was cut at the storey planes; none was skipped by a shifted offset");
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void SplitByLevels_DryRun_ForecastsTheRebuildAndWritesNothing()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests split dry run");
        group.Start();
        try
        {
            var baseLevel = NewLevel(doc, "LAI-Split 0", 0);
            var levels = new[] { baseLevel, NewLevel(doc, "LAI-Split 1", 3000) };
            var walls = Enumerable.Range(0, 3).Select(i => NewWall(doc, baseLevel, 0, i * 1000, 5000, i * 1000, heightMm: 6000)).ToList();
            var wallIds = IdsOf(walls);
            var before = AllWalls(doc).Count();

            var result = Run(Adapter.SplitByLevelsAsync(LevelRequest(wallIds, IdsOf(levels), limit: 50, continueFrom: 0, dryRun: true)))!;

            Assert.That(result.Report, Does.StartWith("Dry-run. "));
            Assert.That(result.Report, Does.Contain("No changes made."));
            Assert.That(result.Report, Does.Contain("re-created: 3"));
            Assert.That(result.Created, Is.Zero);
            Assert.That(AllWalls(doc).Count(), Is.EqualTo(before));
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void SplitByLevels_JoinedWalls_NeverLeaveAHalfWrittenBatch()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests split joined");
        group.Start();
        try
        {
            var baseLevel = NewLevel(doc, "LAI-Join 0", 0);
            var levels = new[] { baseLevel, NewLevel(doc, "LAI-Join 1", 3000) };
            // Two walls that meet at a corner, which Revit joins. Re-creating joined walls can make Revit
            // raise "Can't keep elements joined" (seen on real Snowdon walls). Either the batch splits
            // both, or it rolls back whole and says so; it must never leave one wall split and one not.
            var walls = new List<Wall>
            {
                NewWall(doc, baseLevel, 0, 0, 5000, 0, heightMm: 6000),
                NewWall(doc, baseLevel, 5000, 0, 5000, 5000, heightMm: 6000),
            };
            var testLevelIds = levels.Select(l => l.Id).ToHashSet();

            var result = Run(Adapter.SplitByLevelsAsync(LevelRequest(IdsOf(walls), IdsOf(levels), limit: 50, continueFrom: 0, dryRun: false)))!;

            var count = AllWalls(doc).Count(w => testLevelIds.Contains(w.LevelId));
            if (result.Report.StartsWith("Failed"))
            {
                Assert.That(result.Report, Does.Contain("Nothing was changed"), result.Report);
                Assert.That(count, Is.EqualTo(2), "a rolled-back batch leaves both original walls: " + result.Report);
                Assert.Pass("Revit refused joined walls and the batch rolled back whole: " + result.Report);
            }

            Assert.That(count, Is.EqualTo(4), "each wall became a lower and an upper piece: " + result.Report);
        }
        finally
        {
            group.RollBack();
        }
    }
}
