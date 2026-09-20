using System.IO;
using System.Reflection;
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
        var ctx = new PluginContext(raw, _pluginDir);
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

    /// <summary>
    /// The S1 discriminator. A horizontal wall that spans storeys is cut into one wall per storey,
    /// each based on its own storey and as tall as that storey. Before S1 the same wall reported
    /// "no level hit" and the model was untouched, because the range the tool searched for a crossing
    /// was the wall's own horizontal location curve, which a storey plane never crosses.
    /// Asserted on the model (which walls exist, on which storeys, how tall), not on the text alone.
    /// </summary>
    [Test]
    public void Write_SplitByLevels_HorizontalWall_CutsOneWallPerStorey()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var wallsBefore = CountWalls(doc);
        var idsBefore = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .WhereElementIsNotElementType()
            .ToElementIds();

        using (var group = new TransactionGroup(doc, "LoadingAI.RevitTests structure-cuts storey stack probe"))
        {
            group.Start();
            try
            {
                var baseLevel = LowestStorey(doc);
                if (baseLevel == null)
                    Assert.Ignore("The model holds no storey to base a wall on.");

                // The shipped templates carry a single storey, and a storey stack needs storeys to cut
                // at. The ladder is built here rather than the probe being skipped; everything asserted
                // below is still read from the model, never assumed.
                EnsureStackStoreys(doc, baseLevel.Elevation);

                var levels = AllStoreys(doc);
                var wall = TryCreateTallWall(doc);
                if (wall == null)
                    Assert.Ignore("No wall type / levels available to probe a storey stack.");
                if (!TrySetHeight(doc, wall, StackHeightFeet))
                    Assert.Ignore("The probe wall refuses its own height, so its top cannot be placed.");

                var baseZ = baseLevel.Elevation + Param(wall, BuiltInParameter.WALL_BASE_OFFSET);
                var topZ = baseZ + Param(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);

                // The storeys actually crossed, read from the model rather than assumed, and using the
                // tool's own rule: strictly between the wall base and the wall top.
                var interior = Storeys(levels, baseZ, topZ);
                Assert.That(
                    interior.Count,
                    Is.GreaterThanOrEqualTo(2),
                    "the probe wall has to span at least two storeys for this test to mean anything");

                // The split deletes the original, and a Revit element wrapper throws once its element is
                // gone, so the id is taken before the write rather than read back after it.
                var wallId = wall.Id;

                var text = Run("split_by_levels", "{\"dryRun\":false,\"scope\":\"selection\",\"elementIds\":[\""
                    + wallId.Value
                    + "\"],\"levelIds\":["
                    + string.Join(",", levels.Select(level => "\"" + level.Id.Value + "\""))
                    + "]}");

                Assert.That(text, Does.Not.Contain("no level hit"), text);
                Assert.That(text, Does.Contain("Split 1 elements into " + (interior.Count + 1) + " pieces."), text);
                Assert.That(text, Does.Contain("re-created: 1"), text);
                Assert.That(doc.GetElement(wallId), Is.Null, "the original wall must not survive the split");

                var pieces = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .Where(candidate => !idsBefore.Contains(candidate.Id))
                    .OrderBy(BaseZ)
                    .ToList();

                Assert.That(
                    CountWalls(doc),
                    Is.EqualTo(wallsBefore + interior.Count + 1),
                    "the probe wall is added after the baseline, then replaced by one wall per storey");
                Assert.That(pieces.Count, Is.EqualTo(interior.Count + 1));

                var expected = new List<Level> { baseLevel };
                expected.AddRange(interior);
                Assert.That(
                    pieces.Select(piece => doc.GetElement(piece.LevelId)?.Id.Value),
                    Is.EquivalentTo(expected.Select(level => level.Id.Value)),
                    "each piece must be based on its own storey");

                Assert.That(BaseZ(pieces[0]), Is.EqualTo(baseZ).Within(StoreyTolFt));
                // Index-from-end would need System.Index, which the 2024 (net48) leg has no reference for.
                var lastPiece = pieces[pieces.Count - 1];
                Assert.That(BaseZ(lastPiece) + Height(lastPiece), Is.EqualTo(topZ).Within(StoreyTolFt));
                Assert.That(
                    pieces.Sum(Height),
                    Is.EqualTo(topZ - baseZ).Within(StoreyTolFt),
                    "the pieces together must still be the wall they replaced");


                // A wall inside one storey has no interior storey to cut at, so it keeps today's
                // reason — the new path must not swallow the case that reason was written for.
                var shortWall = TryCreateSideWall(doc, baseLevel, 40, 3.0);
                if (shortWall != null)
                {
                    var shortText = Run("split_by_levels", "{\"dryRun\":false,\"scope\":\"selection\",\"elementIds\":[\""
                        + shortWall.Id.Value + "\"]}");
                    Assert.That(shortText, Does.Contain("no level hit"), shortText);
                    Assert.That(doc.GetElement(shortWall.Id), Is.Not.Null, "a wall inside one storey must be left alone");
                }
                else
                {
                    TestContext.WriteLine("Single-storey wall skip not asserted: no wall type available.");
                }

                // The grids tool shares CurveMemberSplit's cut and rebuild helpers with this one, so it
                // has to keep answering on the same wall. It is not the case being fixed here.
                var gridWall = TryCreateSideWall(doc, baseLevel, 120, StackHeightFeet);
                if (gridWall != null)
                {
                    var gridText = Run("split_by_grids", "{\"dryRun\":false,\"scope\":\"selection\",\"elementIds\":[\""
                        + gridWall.Id.Value + "\"]}");
                    Assert.That(gridText, Does.Contain("grid").IgnoreCase, gridText);
                    if (gridText.Contains("No grids found."))
                        Assert.That(doc.GetElement(gridWall.Id), Is.Not.Null, "no grids means no write");
                }
                else
                {
                    TestContext.WriteLine("Grids smoke not asserted: no wall type available.");
                }

                // A curtain wall spans the same storeys but is not a curve member, so it keeps the skip
                // it had before this slice — by name, and only checked ahead of the new storey path.
                // The template may hold no curtain type, in which case the claim is not asserted here.
                var curtain = TryCreateWallOfKind(doc, WallKind.Curtain, baseLevel, 200, StackHeightFeet);
                if (curtain != null)
                {
                    var curtainText = Run("split_by_levels", "{\"dryRun\":false,\"scope\":\"selection\",\"elementIds\":[\""
                        + curtain.Id.Value + "\"]}");
                    Assert.That(curtainText, Does.Contain("curtain"), curtainText);
                    Assert.That(doc.GetElement(curtain.Id), Is.Not.Null, "a curtain wall is skipped, never cut");
                }
                else
                {
                    TestContext.WriteLine("Curtain skip not asserted: the model holds no curtain wall type.");
                }

                var stacked = TryCreateWallOfKind(doc, WallKind.Stacked, baseLevel, 240, StackHeightFeet);
                if (stacked != null)
                {
                    var stackedText = Run("split_by_levels", "{\"dryRun\":false,\"scope\":\"selection\",\"elementIds\":[\""
                        + stacked.Id.Value + "\"]}");

                    // The tool's host filter takes walls in OST_Walls. A stacked wall carrying any other
                    // category never reaches the host list at all, so its skip is unreachable through the
                    // tool. That is recorded with the category it does carry, not asserted as if it were
                    // reached — and asserted in full on any host where the filter does take it.
                    var reachable = doc.GetElement(stacked.Id) != null
                        && stacked.Category?.Id.Value == (long)BuiltInCategory.OST_Walls;

                    if (reachable)
                    {
                        Assert.That(stackedText, Does.Contain("stacked"), stackedText);
                        Assert.That(doc.GetElement(stacked.Id), Is.Not.Null, "a stacked wall is skipped, never cut");
                    }
                    else
                    {
                        TestContext.WriteLine(
                            "Stacked skip not reachable through the tool in this host: the created wall carries category "
                            + (stacked.Category?.Id.Value.ToString() ?? "none")
                            + ", and the host filter takes OST_Walls only.");
                    }
                }
                else
                {
                    TestContext.WriteLine("Stacked skip not asserted: the model holds no stacked wall type.");
                }

                // A dry run counts the storeys it would cut at and still opens no transaction, so the
                // wall is reported and left standing. Before this slice the same wall counted zero.
                var dryWall = TryCreateSideWall(doc, baseLevel, 280, StackHeightFeet);
                if (dryWall != null)
                {
                    var dryId = dryWall.Id;
                    var dryText = Run("split_by_levels", "{\"dryRun\":true,\"scope\":\"selection\",\"elementIds\":[\""
                        + dryId.Value + "\"]}");
                    Assert.That(dryText, Does.StartWith("Dry-run."), dryText);
                    Assert.That(
                        dryText,
                        Does.Contain("Would split 1 members into " + (interior.Count + 1) + " pieces"),
                        dryText);
                    Assert.That(doc.GetElement(dryId), Is.Not.Null, "a dry run must not write");
                }
                else
                {
                    TestContext.WriteLine("Dry-run not asserted: no wall type available.");
                }

                // Paging is a host window taken before any storey work: capped to the same wall the uncapped
                // run cuts, and past the end of the window nothing is written.
                var pagedWall = TryCreateSideWall(doc, baseLevel, 320, StackHeightFeet);
                if (pagedWall != null)
                {
                    // The host is gone once it is cut, so its id is taken while the wrapper is live.
                    var pagedId = pagedWall.Id;
                    var pagedText = Run(
                        "split_by_levels",
                        "{\"dryRun\":false,\"scope\":\"selection\",\"limit\":1,\"continueFrom\":0,\"elementIds\":[\""
                        + pagedId.Value + "\"]}");
                    Assert.That(pagedText, Does.Contain("Split 1 elements into " + (interior.Count + 1) + " pieces."), pagedText);
                    Assert.That(doc.GetElement(pagedId), Is.Null, "a capped run still cuts the host it took");
                }
                else
                {
                    TestContext.WriteLine("limit not asserted: no wall type available.");
                }

                var beyondWall = TryCreateSideWall(doc, baseLevel, 360, StackHeightFeet);
                if (beyondWall != null)
                {
                    var beyondId = beyondWall.Id;
                    var beyondText = Run(
                        "split_by_levels",
                        "{\"dryRun\":false,\"scope\":\"selection\",\"continueFrom\":1,\"elementIds\":[\""
                        + beyondId.Value + "\"]}");
                    Assert.That(beyondText, Does.Not.Contain("Split 1 elements"), beyondText);
                    Assert.That(doc.GetElement(beyondId), Is.Not.Null, "a host past the window is left alone");
                }
                else
                {
                    TestContext.WriteLine("continueFrom not asserted: no wall type available.");
                }

                // A top the wall's own height cannot place is named, not folded into "no level hit":
                // the storeys are there, the top is what is missing. It needs an attach target, so the
                // assertion stands only when the host gives us one that actually detaches the top.
                // The passive template ships no floor and no roof, so a wall top has nothing to attach
                // to until the test puts a slab in the model at the wall's own top elevation.
                var attachTarget = TryEnsureAttachTarget(doc, baseLevel, topZ + 3.0);
                if (TryCreateAttachedTopWall(doc, baseLevel, 80, StackHeightFeet, out var attached, out var unattachable))
                {
                    var attachedText = Run("split_by_levels", "{\"dryRun\":false,\"scope\":\"selection\",\"elementIds\":[\""
                        + attached!.Id.Value + "\"]}");
                    Assert.That(attachedText, Does.Contain("top not at wall height"), attachedText);
                    Assert.That(attachedText, Does.Not.Contain("no level hit"), attachedText);
                    Assert.That(doc.GetElement(attached.Id), Is.Not.Null, "an unreconstructible top must be left alone");
                }
                else
                {
                    TestContext.WriteLine("Top-attached skip not asserted in this host: " + unattachable);
                }

                // A slanted wall's location curve rises through storey planes, but its pieces cannot be
                // re-created from a Z-rising line: Revit refuses that curve for a wall, so this host
                // reports "split failed" both before and after this slice. What is asserted is that the
                // slant is not diverted to the storey stack: no "top not at wall height", never the
                // stack's own success shape. Asserting a cut here would be claiming an API it does not have.
                var slant = TryCreateSlantedWall(doc, baseLevel, 160, 25, StackHeightFeet, out var slantResidual);
                if (slant != null)
                {
                    var slantId = slant.Id;
                    var before = new HashSet<ElementId>(
                        new FilteredElementCollector(doc).OfClass(typeof(Wall)).WhereElementIsNotElementType().ToElementIds());

                    var slantText = Run("split_by_levels", "{\"dryRun\":false,\"scope\":\"selection\",\"elementIds\":[\""
                        + slantId.Value
                        + "\"],\"levelIds\":["
                        + string.Join(",", levels.Select(level => "\"" + level.Id.Value + "\""))
                        + "]}");

                    Assert.That(
                        slantText,
                        Does.Not.Contain("top not at wall height"),
                        "the storey stack path is for horizontal walls only: " + slantText);

                    if (slantText.Contains("Split 1 elements into"))
                    {
                        var slantPieces = new FilteredElementCollector(doc)
                            .OfClass(typeof(Wall))
                            .WhereElementIsNotElementType()
                            .Cast<Wall>()
                            .Where(candidate => !before.Contains(candidate.Id))
                            .ToList();

                        // The curve cut rebuilds every piece on the wall's own storey; the storey stack
                        // gives each piece its own. One level across the pieces is the difference.
                        Assert.That(slantPieces.Count, Is.GreaterThan(1), slantText);
                        Assert.That(
                            slantPieces.Select(piece => piece.LevelId.Value).Distinct().Count(),
                            Is.EqualTo(1),
                            "a slanted wall must keep the curve cut, not the storey stack");
                    }
                    else
                    {
                        TestContext.WriteLine("Slanted wall did not cut in this host: " + slantText);
                    }
                }
                else
                {
                    // No Z-rising location curve is constructible in this host, so the slanted half of
                    // the guard cannot be asserted here. It is named, with the API refusals that stopped
                    // the fixture, rather than passed over: the guard's rise test stays reviewable.
                    TestContext.WriteLine("Slanted wall not asserted in this host: " + slantResidual);
                }

                // The storey stack deletes the wall it replaces, and Revit deletes what hangs off that
                // wall with it. Before this slice the same wall was never split at all, so this is the
                // first path that can take a hosted door with it — silently, since the write is one
                // undo step but the report said nothing about what it took with the host. The report
                // has to name the count, the way the curve path already does. The probe runs last so
                // that a host which cannot host anything costs only this probe, and it gates on the
                // prerequisite (a dependent really hangs off the wall, read through the same API the
                // tool reads, before anything is split) rather than on the observation it is here to
                // make, so a wall with nothing to lose is never asserted on.
                var hostedWall = TryCreateSideWall(doc, baseLevel, 400, StackHeightFeet);
                if (hostedWall == null)
                    Assert.Ignore("No wall type available to host an element on a probe wall.");

                if (!TryHostDependent(doc, hostedWall, out var unhosted))
                    Assert.Ignore(unhosted);

                var hostedId = hostedWall.Id;
                var hostedText = Run("split_by_levels", "{\"dryRun\":false,\"scope\":\"selection\",\"elementIds\":[\""
                    + hostedId.Value + "\"]}");

                Assert.That(
                    hostedText,
                    Does.Contain("Split 1 elements into " + (interior.Count + 1) + " pieces."),
                    hostedText);
                Assert.That(
                    hostedText,
                    Does.Contain("Dependent elements to re-check:"),
                    "the wall carried a hosted element, so the split has to report it: " + hostedText);
                Assert.That(DependentCount(hostedText), Is.GreaterThanOrEqualTo(1), hostedText);
            }
            finally
            {
                group.RollBack();
            }
        }

        Assert.That(CountWalls(doc), Is.EqualTo(wallsBefore));
    }

    /// <summary>The tool's own tolerance for "a storey strictly inside the wall": 30 mm.</summary>
    private const double StoreyTolFt = 30.0 / 304.8;

    /// <summary>The storey the probe ladder is spaced at, and the wall height that crosses two of them.</summary>
    private const double StoreyFeet = 10.0;

    private const double StackHeightFeet = 3.0 * StoreyFeet;

    private static List<Level> Storeys(IReadOnlyList<Level> levels, double baseZ, double topZ)
        => levels
            .Where(level => level.Elevation > baseZ + StoreyTolFt && level.Elevation < topZ - StoreyTolFt)
            .GroupBy(level => level.Elevation)
            .Select(group => group.First())
            .OrderBy(level => level.Elevation)
            .ToList();

    private static List<Level> AllStoreys(Document doc)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(level => level.Elevation)
            .ToList();

    private static Level? LowestStorey(Document doc) => AllStoreys(doc).FirstOrDefault();

    /// <summary>
    /// Builds the storeys a storey stack needs. A template that already carries them is left alone,
    /// and a storey that already sits where one is wanted is not duplicated.
    /// </summary>
    private static void EnsureStackStoreys(Document doc, double baseZ)
    {
        for (var step = 1; step <= 2; step++)
        {
            var elevation = baseZ + (StoreyFeet * step);
            if (AllStoreys(doc).Any(level => Math.Abs(level.Elevation - elevation) <= StoreyTolFt))
                continue;

            using var tx = new Transaction(doc, "LoadingAI.RevitTests build a storey ladder");
            tx.Start();
            try
            {
                // One transaction per storey: a storey the host refuses must not take the whole
                // ladder down with it.
                Level.Create(doc, elevation).Name = "LoadingAI probe storey " + step;
                tx.Commit();
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                    tx.RollBack();
            }
        }
    }

    /// <summary>Places the wall's top by its own unconnected height, which is what the path requires.</summary>
    private static bool TrySetHeight(Document doc, Wall wall, double heightFeet)
    {
        using var tx = new Transaction(doc, "LoadingAI.RevitTests set probe wall height");
        tx.Start();
        try
        {
            var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (height == null || height.IsReadOnly || !height.Set(heightFeet))
            {
                tx.RollBack();
                return false;
            }

            tx.Commit();
            return true;
        }
        catch
        {
            if (tx.GetStatus() == TransactionStatus.Started)
                tx.RollBack();
            return false;
        }
    }

    private static double Param(Element element, BuiltInParameter builtIn)
        => element.get_Parameter(builtIn)?.AsDouble() ?? 0;

    private static double BaseZ(Wall wall)
        => ((wall.Document.GetElement(wall.LevelId) as Level)?.Elevation ?? 0)
            + Param(wall, BuiltInParameter.WALL_BASE_OFFSET);

    private static double Height(Wall wall) => Param(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);

    private static Wall? TryCreateSideWall(Document doc, Level baseLevel, double y, double heightFeet)
    {
        var wallType = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(type => type.Kind == WallKind.Basic);
        if (wallType == null)
            return null;

        var line = Line.CreateBound(new XYZ(0, y, 0), new XYZ(10, y, 0));
        using var tx = new Transaction(doc, "LoadingAI.RevitTests create side probe wall");
        tx.Start();
        try
        {
            var wall = Wall.Create(doc, line, wallType.Id, baseLevel.Id, heightFeet, 0, false, false);
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

    /// <summary>
    /// Hangs a real dependent on the probe wall and reports success only once the live wall shows one:
    /// a door or window type when the model holds one, otherwise a plain wall opening, which any metric
    /// project accepts. The check reads the same API the tool reads, so the caller can assert on the
    /// report only when the wall genuinely had something to lose. Returns false with a reason the
    /// caller records verbatim when the host cannot host anything on this wall.
    /// </summary>
    private static bool TryHostDependent(Document doc, Wall wall, out string reason)
    {
        var level = doc.GetElement(wall.LevelId) as Level;
        var familySymbols = new List<FamilySymbol>();
        foreach (var symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
        {
            var category = symbol.Category;
            if (category == null)
                continue;

            var categoryId = category.Id.Value;
            if (categoryId == (long)BuiltInCategory.OST_Doors
                || categoryId == (long)BuiltInCategory.OST_Windows)
            {
                familySymbols.Add(symbol);
            }
        }

        var placed = false;
        reason = "the host holds no door or window type, and refused a wall opening";

        using var tx = new Transaction(doc, "LoadingAI.RevitTests host an element on the probe wall");
        tx.Start();
        try
        {
            if (level != null)
            {
                foreach (var symbol in familySymbols)
                {
                    try
                    {
                        if (!symbol.IsActive)
                            symbol.Activate();
                        doc.Create.NewFamilyInstance(
                            PointInWall(wall, 0.5, 5.0), symbol, wall, level,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        placed = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        reason = "the host refused a door or window on the probe wall: " + ex.Message;
                    }
                }
            }

            if (!placed)
            {
                try
                {
                    // An opening's corners live in the plane of the wall, so they are taken on the face
                    // rather than on the location curve: a centreline point is not in that plane.
                    doc.Create.NewOpening(
                        wall,
                        OnWallFace(wall, PointInWall(wall, 0.3, 5.0)),
                        OnWallFace(wall, PointInWall(wall, 0.7, 9.0)));
                    placed = true;
                }
                catch (Exception ex)
                {
                    reason = "the host refused a wall opening on the probe wall: " + ex.Message;
                }
            }

            if (placed)
                tx.Commit();
            else
                tx.RollBack();
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started)
                tx.RollBack();
            reason = "hosting an element on the probe wall failed: " + ex.Message;
            return false;
        }

        if (!placed)
            return false;

        if (DependentCountOf(wall) > 0)
            return true;

        // The count is read from the live wall, before anything is split, through the same API the tool
        // reads. When it is zero here the tool would also read zero and the report would rightly have no
        // dependent line, so the probe has nothing to prove and says so instead of asserting.
        reason = familySymbols.Count == 0
            ? "the model holds no door or window type to host, and a wall opening is not reported as a dependent of its host"
            : "the host does not report the door or window it accepted as a dependent of the probe wall";
        return false;
    }

    /// <summary>A point on the wall's own line, the given fraction along it, at a height above its base.</summary>
    private static XYZ PointInWall(Wall wall, double along, double aboveBaseFeet)
    {
        var location = wall.Location as LocationCurve;
        if (location == null)
            return XYZ.Zero;

        var at = location.Curve.Evaluate(along, true);
        return new XYZ(at.X, at.Y, BaseZ(wall) + aboveBaseFeet);
    }

    /// <summary>The same point moved onto the wall's face, where a wall opening's corners live.</summary>
    private static XYZ OnWallFace(Wall wall, XYZ point)
    {
        try
        {
            return point + wall.Orientation * (wall.Width / 2.0);
        }
        catch
        {
            return point;
        }
    }

    /// <summary>The live dependent count for an element, read through the same API the tool reads.</summary>
    private static int DependentCountOf(Element element)
    {
        try
        {
            return element.GetDependentElements(null)?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Reads the dependent count back out of a split report, or 0 when the line is absent.</summary>
    private static int DependentCount(string report)
    {
        const string marker = "Dependent elements to re-check: ";
        var at = report.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return 0;

        var digits = string.Empty;
        for (var i = at + marker.Length; i < report.Length && char.IsDigit(report[i]); i++)
            digits += report[i];

        return int.TryParse(digits, out var count) ? count : 0;
    }

    /// <summary>
    /// Creates a wall of a named kind, for the two shapes the storey path must not touch. Returns null
    /// when the model holds no type of that kind or the host refuses the write, so the caller records
    /// the claim as unasserted rather than asserting it on a wall it never made.
    /// </summary>
    private static Wall? TryCreateWallOfKind(Document doc, WallKind kind, Level baseLevel, double y, double heightFeet)
    {
        var wallType = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(type => type.Kind == kind);
        if (wallType == null)
            return null;

        var line = Line.CreateBound(new XYZ(0, y, 0), new XYZ(10, y, 0));
        using var tx = new Transaction(doc, "LoadingAI.RevitTests create " + kind + " probe wall");
        tx.Start();
        try
        {
            var wall = Wall.Create(doc, line, wallType.Id, baseLevel.Id, heightFeet, 0, false, false);
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

    /// <summary>
    /// Creates a wall whose <em>location curve</em> rises in Z, so a storey plane does cross it. Three
    /// routes are tried, because Revit is free to accept a Z-rising curve on none of them; every route's
    /// outcome is recorded and <paramref name="residual"/> names what stopped the last one. A wall whose
    /// location curve is still level is not returned: that wall is a horizontal wall, and treating it as
    /// the slanted case would assert the opposite of the rule under test.
    /// </summary>
    private static Wall? TryCreateSlantedWall(
        Document doc,
        Level baseLevel,
        double y,
        double riseFeet,
        double heightFeet,
        out string residual)
    {
        residual = "no wall type in the model";
        var wallType = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(type => type.Kind == WallKind.Basic);
        if (wallType == null)
        {
            return null;
        }

        // Route 1: a Z-rising location line straight into Wall.Create. Both heights are tried because
        // Revit's own check is on the curve, not on the height.
        var baseZ = baseLevel.Elevation;
        var refusals = new List<string>();
        foreach (var height in new[] { 0.0, heightFeet })
        {
            var line = Line.CreateBound(new XYZ(0, y, baseZ), new XYZ(10, y, baseZ + riseFeet));
            using var tx = new Transaction(doc, "LoadingAI.RevitTests create slanted probe wall");
            tx.Start();
            try
            {
                var wall = Wall.Create(doc, line, wallType.Id, baseLevel.Id, height, 0, false, false);
                tx.Commit();
                if (CurveRises(wall))
                    return wall;
            }
            catch (Exception ex)
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                    tx.RollBack();
                refusals.Add("Create height=" + height + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Route 2: the profile overload — a planar loop whose bottom edge rises in Z.
        var profile = TryCreateSlantedProfileWall(doc, baseLevel, y, riseFeet, wallType, out var profileRefusal);
        if (profile != null && CurveRises(profile))
            return profile;
        refusals.Add(profileRefusal);

        // Route 3: slant the location curve of a wall that already exists.
        var slanted = TrySlantLocationCurve(doc, baseLevel, y, riseFeet, heightFeet, wallType, out var slantRefusal);
        if (slanted != null)
            return slanted;
        refusals.Add(slantRefusal);

        residual = string.Join("; ", refusals.Where(text => text.Length > 0));
        return null;
    }

    /// <summary>
    /// Revit refuses a Z-rising location line outright, so the only route left to a wall whose location
    /// curve rises is the profile overload: a planar loop whose bottom edge rises in Z, extruded along
    /// the plane's normal. Both normal orientations are tried because the loop's winding decides the
    /// sign; a loop the API accepts may still come back with a level base, which is recorded, not hidden.
    /// </summary>
    private static Wall? TryCreateSlantedProfileWall(
        Document doc,
        Level level,
        double y,
        double riseFeet,
        WallType wallType,
        out string refusal)
    {
        const double length = 10.0;
        const double storey = 10.0;
        var z = level.Elevation;
        var profile = new List<Curve>
        {
            Line.CreateBound(new XYZ(0, y, z), new XYZ(length, y, z + riseFeet)),
            Line.CreateBound(new XYZ(length, y, z + riseFeet), new XYZ(length, y, z + riseFeet + storey)),
            Line.CreateBound(new XYZ(length, y, z + riseFeet + storey), new XYZ(0, y, z + storey)),
            Line.CreateBound(new XYZ(0, y, z + storey), new XYZ(0, y, z)),
        };

        refusal = "profile loop: no normal accepted";
        foreach (var candidate in new[] { XYZ.BasisY, XYZ.BasisY.Negate() })
        {
            using var tx = new Transaction(doc, "LoadingAI.RevitTests create slanted profile wall");
            tx.Start();
            try
            {
                var wall = Wall.Create(doc, profile, wallType.Id, level.Id, false, candidate);
                tx.Commit();
                refusal = "profile loop: accepted, location curve " + CurveSpan(wall);
                return wall;
            }
            catch (Exception ex)
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                    tx.RollBack();
                refusal = "profile loop normal=" + candidate.Y.ToString("0.##")
                    + ": " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        return null;
    }

    /// <summary>
    /// Slants the location curve of a wall that already exists. Returns null unless the wall's own curve
    /// really ends up rising, so a wall Revit levels off again is never handed back as the slanted case.
    /// </summary>
    private static Wall? TrySlantLocationCurve(
        Document doc,
        Level level,
        double y,
        double riseFeet,
        double heightFeet,
        WallType wallType,
        out string refusal)
    {
        refusal = "location curve: not slanted";
        var baseZ = level.Elevation;
        using var tx = new Transaction(doc, "LoadingAI.RevitTests slant a wall location curve");
        tx.Start();
        try
        {
            var flat = Line.CreateBound(new XYZ(0, y, baseZ), new XYZ(10, y, baseZ));
            var wall = Wall.Create(doc, flat, wallType.Id, level.Id, heightFeet, 0, false, false);
            if (wall.Location is LocationCurve location)
                location.Curve = Line.CreateBound(new XYZ(0, y, baseZ), new XYZ(10, y, baseZ + riseFeet));

            tx.Commit();
            if (CurveRises(wall))
                return wall;

            refusal = "location curve: accepted then levelled back to " + CurveSpan(wall);
            return null;
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started)
                tx.RollBack();
            refusal = "location curve: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>True when the wall's own location curve crosses a storey plane, i.e. it is not horizontal.</summary>
    private static bool CurveRises(Wall wall)
    {
        if (wall.Location is not LocationCurve location)
            return false;

        var z0 = location.Curve.GetEndPoint(0).Z;
        var z1 = location.Curve.GetEndPoint(1).Z;
        return Math.Abs(z1 - z0) > StoreyTolFt;
    }

    private static string CurveSpan(Wall wall)
    {
        if (wall.Location is not LocationCurve location)
            return "no location curve";
        var z0 = location.Curve.GetEndPoint(0).Z;
        var z1 = location.Curve.GetEndPoint(1).Z;
        return z0.ToString("0.##") + ".." + z1.ToString("0.##");
    }

    /// <summary>
    /// Builds a wall that spans storeys while its top is placed by an attachment rather than by its own
    /// height — the wall this path must refuse to rebuild. Returns false with a reason when the model
    /// holds nothing a wall top can attach to, or when the attachment leaves the own height in charge.
    /// </summary>
    private static bool TryCreateAttachedTopWall(
        Document doc,
        Level baseLevel,
        double y,
        double heightFeet,
        out Wall? wall,
        out string reason)
    {
        wall = null;
        reason = "no attach target (floor or roof) in the model";

        var target = new FilteredElementCollector(doc)
            .OfClass(typeof(Floor))
            .FirstOrDefault() ?? (Element?)new FilteredElementCollector(doc)
            .OfClass(typeof(RoofBase))
            .FirstOrDefault();
        if (target == null)
            return false;

        var created = TryCreateSideWall(doc, baseLevel, y, heightFeet);
        if (created == null)
        {
            reason = "no wall type available";
            return false;
        }

        using var tx = new Transaction(doc, "LoadingAI.RevitTests attach probe wall top");
        tx.Start();
        try
        {
            if (!TryAttachWallTop(doc, created, target.Id, out var why))
            {
                tx.RollBack();
                reason = why;
                return false;
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started)
                tx.RollBack();
            reason = "attaching a wall top threw: " + ex.Message;
            return false;
        }

        // Only claim the wall when the attachment actually moved the top away from the height the
        // wall reports, which is the condition the tool refuses to rebuild through.
        var baseZ = baseLevel.Elevation + Param(created, BuiltInParameter.WALL_BASE_OFFSET);
        var placed = baseZ + Param(created, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
        var box = created.get_BoundingBox(null);
        if (box != null
            && Math.Abs(box.Min.Z - baseZ) <= StoreyTolFt
            && Math.Abs(box.Max.Z - placed) <= StoreyTolFt)
        {
            reason = "the attachment landed on the wall's own height, so the top is still reconstructible";
            return false;
        }

        wall = created;
        return true;
    }

    /// <summary>
    /// Attaches the wall's top to <paramref name="targetId"/> through the host's own attachment API.
    /// Wall.AddAttachment / Wall.IsValidTargetAttachment / AttachmentLocation only exist from the
    /// Revit 2025 reference assemblies on, so they are reached reflectively: the 2024 (net48) leg still
    /// compiles this fixture, and a host without the API reports the gap as the reason rather than
    /// failing the build. The reason is surfaced, never swallowed — the caller records it as a residual.
    /// </summary>
    private static bool TryAttachWallTop(Document doc, Wall wall, ElementId targetId, out string why)
    {
        var api = typeof(Wall).Assembly;
        var locationType = api.GetType("Autodesk.Revit.DB.AttachmentLocation");
        if (locationType == null)
        {
            why = "this host has no wall attachment API (Revit 2025+ only): "
                + api.GetName().Name + " " + api.GetName().Version;
            return false;
        }

        var valid = FindApiMethod(typeof(Wall), "IsValidTargetAttachment", typeof(Document), typeof(ElementId));
        var attach = FindApiMethod(typeof(Wall), "AddAttachment", typeof(ElementId), locationType);
        if (valid == null || attach == null)
        {
            why = "this host has no wall attachment API (Revit 2025+ only)";
            return false;
        }

        if (!(bool)(InvokeApi(valid, wall, doc, targetId) ?? false))
        {
            why = "the host refuses this element as a wall attachment target";
            return false;
        }

        InvokeApi(attach, wall, targetId, Enum.Parse(locationType, "Top"));
        why = string.Empty;
        return true;
    }

    private static MethodInfo? FindApiMethod(Type type, string name, params Type[] args)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        {
            if (method.Name != name)
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length != args.Length)
                continue;

            var match = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != args[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return method;
        }

        return null;
    }

    private static object? InvokeApi(MethodInfo method, object? instance, params object[] args)
        => method.Invoke(method.IsStatic ? null : instance, args);

    /// <summary>
    /// Puts a slab in the model whose top sits at <paramref name="z"/>, so a wall top has something to
    /// attach to. Only used to build the unreconstructible-top case; the template ships no slab.
    /// </summary>
    private static Element? TryEnsureAttachTarget(Document doc, Level level, double z)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(Floor))
            .WhereElementIsNotElementType()
            .FirstOrDefault();
        if (existing != null)
            return existing;

        var floorType = new FilteredElementCollector(doc)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .FirstOrDefault(type => !type.IsFoundationSlab);
        if (floorType == null)
            return null;

        var corner = level.Elevation;
        var loop = new CurveLoop();
        loop.Append(Line.CreateBound(new XYZ(-20, -20, corner), new XYZ(60, -20, corner)));
        loop.Append(Line.CreateBound(new XYZ(60, -20, corner), new XYZ(60, 400, corner)));
        loop.Append(Line.CreateBound(new XYZ(60, 400, corner), new XYZ(-20, 400, corner)));
        loop.Append(Line.CreateBound(new XYZ(-20, 400, corner), new XYZ(-20, -20, corner)));

        using var tx = new Transaction(doc, "LoadingAI.RevitTests create attach target");
        tx.Start();
        try
        {
            var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id);
            floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(z - corner);
            floor.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM)?.Set(z - corner);
            tx.Commit();
            return floor;
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started)
                tx.RollBack();
            TestContext.WriteLine("Attach target not created in this host: " + ex.Message);
            return null;
        }
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
