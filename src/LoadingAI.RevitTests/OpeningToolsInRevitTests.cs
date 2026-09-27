using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using LoadingAI.Plugins;
using LoadingAI.Plugins.Core;
using LoadingAI.Revit;
using LoadingAI.Tools;
using NUnit.Framework;

namespace LoadingAI.RevitTests;

/// <summary>
/// Live Revit 2027 contract for opening-tools. Writes run inside a TransactionGroup
/// that is always rolled back, so these tests never persist model changes.
/// </summary>
[TestFixture]
public class OpeningToolsInRevitTests
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
        var runtimeAllowlist = PluginAllowlist.ForRuntime();
        var installedHashes = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", uiApp.Application.VersionNumber, "LoadingAI",
            PluginAllowlist.ShippedHashesFileName);
        if (File.Exists(installedHashes))
            PluginAllowlist.Current = new PluginAllowlist(
                runtimeAllowlist.AllowlistPath, installedHashes, runtimeAllowlist.DevPluginsDir);
        if (_uiApp.ActiveUIDocument?.Document != null)
            return;

        try
        {
            var doc = _uiApp.Application.NewProjectDocument(UnitSystem.Metric);
            _tempModelPath = Path.Combine(
                Path.GetTempPath(),
                $"LoadingAI.OpeningTools.RevitTests.{Guid.NewGuid():N}.rvt");
            doc.SaveAs(_tempModelPath);
            doc.Close(false);
            _ownedDoc = _uiApp.OpenAndActivateDocument(_tempModelPath)?.Document;
            _bridge.AttachCurrentThread(_uiApp);

        }
        catch (Exception ex)
        {
            Assert.Ignore("Could not create a throwaway project for opening-tools tests: " + ex.Message);
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
            Assert.Ignore("Open a Revit model before running opening-tools tests.");
    }

    [Test]
    public void Discover_LoadsOpeningTools_AsDefaultDisabledL3()
    {
        var raw = _bridge ?? throw new InvalidOperationException("RevitBridge was not created.");
        var plugins = PluginLoader.Discover(raw, new PluginContextFactory(raw));
        var opening = plugins.SingleOrDefault(p => p.Meta.Id == "opening-tools");

        Assert.That(opening, Is.Not.Null, "opening-tools must pass the shipped hash gate and load.");
        Assert.That(opening!.Meta.Version, Is.EqualTo("1.0.0"));
        Assert.That(PluginLoader.Issues.Where(i => i.PluginId == "opening-tools"), Is.Empty);

        var tools = opening.Meta.Tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        Assert.That(tools.Select(t => t.Name), Is.EqualTo(new[]
        {
            "create_openings_from_clash",
            "cut_hosts_by_openings",
        }));
        Assert.That(tools.Select(t => t.Tier), Is.All.EqualTo("L3"));
        Assert.That(tools.Select(t => t.EnabledByDefault), Is.All.False);
    }

    [Test]
    public void DryRun_PlansRealConduitWallClash_WithoutWriting()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var before = OpeningInstances(doc).Select(x => x.Id).ToHashSet();

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests opening-tools dry-run");
        group.Start();
        try
        {
            var fixture = CreateWallAndConduit(doc);
            var tool = CreateTool("create_openings_from_clash");
            var args = JsonSerializer.Serialize(new
            {
                mepIds = new[] { fixture.Conduit.Id.ToString() },
                hostIds = new[] { fixture.Wall.Id.ToString() },
                shape = "circle",
                clearanceMm = 0,
                limit = 10,
                dryRun = true,
            });

            var result = tool.ExecuteAsync(args).GetAwaiter().GetResult();

            Assert.That(result.Status, Is.EqualTo(ToolExecutionStatus.Succeeded), result.Text);
            Assert.That(result.WriteCertainty, Is.EqualTo(ToolWriteCertainty.NoWrite), result.Text);
            Assert.That(result.ChangedCount, Is.Zero);
            if (!result.Text.Contains("Planned 1 opening(s).")) Assert.Fail(result.Text + Environment.NewLine + FixtureDiagnostics(doc, fixture));
            Assert.That(result.Text, Does.Contain("No model changes."), result.Text);
            Assert.That(OpeningInstances(doc).Select(x => x.Id).ToHashSet(), Is.EquivalentTo(before));
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void Write_PlacesAndPunchesRealClash_ThenRollsBack()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var before = OpeningInstances(doc).Select(x => x.Id).ToHashSet();

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests opening-tools write");
        group.Start();
        WallAndConduit fixture;
        FamilyInstance opening;
        ElementId wallId = ElementId.InvalidElementId;
        ElementId conduitId = ElementId.InvalidElementId;
        try
        {
            fixture = CreateWallAndConduit(doc);
            wallId = fixture.Wall.Id;
            conduitId = fixture.Conduit.Id;
            var tool = CreateTool("create_openings_from_clash");
            var args = JsonSerializer.Serialize(new
            {
                mepIds = new[] { fixture.Conduit.Id.ToString() },
                hostIds = new[] { fixture.Wall.Id.ToString() },
                shape = "circle",
                clearanceMm = 0,
                limit = 10,
            });

            var result = tool.ExecuteAsync(args).GetAwaiter().GetResult();

            Assert.That(result.Status, Is.EqualTo(ToolExecutionStatus.Succeeded), result.Text);
            Assert.That(result.WriteCertainty, Is.EqualTo(ToolWriteCertainty.Committed), result.Text);
            Assert.That(result.ChangedCount, Is.EqualTo(1), result.Text);
            Assert.That(result.FailedCount, Is.Zero, result.Text);
            Assert.That(result.IsSafeToAssimilate, Is.True);

            opening = CreatedOpening(doc, before);
            Assert.That(InstanceVoidCutUtils.GetCuttingVoidInstances(fixture.Wall), Does.Contain(opening.Id));
        }
        finally
        {
            group.RollBack();
        }

        Assert.That(doc.GetElement(wallId), Is.Null);
        Assert.That(doc.GetElement(conduitId), Is.Null);
        Assert.That(OpeningInstances(doc).Select(x => x.Id).ToHashSet(), Is.EquivalentTo(before));
    }

    [Test]
    public void RepairCut_RestoresLostVoidCut_ThenRollsBack()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var before = OpeningInstances(doc).Select(x => x.Id).ToHashSet();

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests opening-tools repair-cut");
        group.Start();
        try
        {
            var fixture = CreateWallAndConduit(doc);
            var create = CreateTool("create_openings_from_clash");
            var createArgs = JsonSerializer.Serialize(new
            {
                mepIds = new[] { fixture.Conduit.Id.ToString() },
                hostIds = new[] { fixture.Wall.Id.ToString() },
                shape = "circle",
                clearanceMm = 0,
                limit = 10,
            });
            var placed = create.ExecuteAsync(createArgs).GetAwaiter().GetResult();
            Assert.That(placed.Status, Is.EqualTo(ToolExecutionStatus.Succeeded), placed.Text);

            var opening = CreatedOpening(doc, before);
            using (var remove = new Transaction(doc, "Remove void cut for opening-tools repair probe"))
            {
                remove.Start();
                InstanceVoidCutUtils.RemoveInstanceVoidCut(doc, fixture.Wall, opening);
                remove.Commit();
            }
            Assert.That(InstanceVoidCutUtils.GetCuttingVoidInstances(fixture.Wall), Does.Not.Contain(opening.Id));

            var repair = CreateTool("cut_hosts_by_openings");
            var repairArgs = JsonSerializer.Serialize(new
            {
                openingIds = new[] { opening.Id.ToString() },
                hostIds = new[] { fixture.Wall.Id.ToString() },
            });
            var result = repair.ExecuteAsync(repairArgs).GetAwaiter().GetResult();

            Assert.That(result.Status, Is.EqualTo(ToolExecutionStatus.Succeeded), result.Text);
            Assert.That(result.WriteCertainty, Is.EqualTo(ToolWriteCertainty.Committed), result.Text);
            Assert.That(result.ChangedCount, Is.EqualTo(1), result.Text);
            Assert.That(result.FailedCount, Is.Zero, result.Text);
            Assert.That(InstanceVoidCutUtils.GetCuttingVoidInstances(fixture.Wall), Does.Contain(opening.Id));
        }
        finally
        {
            group.RollBack();
        }
    }

    private ToolEntry CreateTool(string name)
    {
        var raw = _bridge ?? throw new InvalidOperationException("RevitBridge was not created.");
        var plugins = PluginLoader.Discover(raw, new PluginContextFactory(raw));
        var opening = plugins.Single(p => p.Meta.Id == "opening-tools");
        return opening.Tools.Single(t => t.Definition.Function.Name == name);
    }

    private static WallAndConduit CreateWallAndConduit(Document doc)
    {
        var level = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(x => x.Elevation)
            .First();
        var wallType = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .First(x => x.Kind == WallKind.Basic);

        using var tx = new Transaction(doc, "Create opening-tools runtime fixture");
        tx.Start();
        var wall = Wall.Create(
            doc,
            Line.CreateBound(new XYZ(-5, 0, 0), new XYZ(5, 0, 0)),
            wallType.Id,
            level.Id,
            2,
            -1,
            false,
            false);
        var conduit = Conduit.Create(
            doc,
            ElementId.InvalidElementId,
            new XYZ(0, -2, level.Elevation),
            new XYZ(0, 2, level.Elevation),
            level.Id);
        tx.Commit();
        return new WallAndConduit(wall, conduit);
    }

    private static string FixtureDiagnostics(Document doc, WallAndConduit fixture)
    {
        static string Box(Element e)
        {
            var b = e.get_BoundingBox(null);
            return b == null ? "null" : $"[{b.Min.X:F2},{b.Min.Y:F2},{b.Min.Z:F2}]-[{b.Max.X:F2},{b.Max.Y:F2},{b.Max.Z:F2}]";
        }
        static string Geo(Element e, bool hidden)
        {
            var opt = new Options { IncludeNonVisibleObjects = hidden };
            var g = e.get_Geometry(opt);
            var solids = 0;
            var volume = 0.0;
            if (g != null)
            {
                foreach (var o in g)
                {
                    if (o is Solid s && s.Volume > 1e-9) { solids++; volume += s.Volume; }
                    else if (o is GeometryInstance gi)
                    {
                        var inner = gi.GetInstanceGeometry();
                        if (inner == null) continue;
                        foreach (var io in inner)
                            if (io is Solid s2 && s2.Volume > 1e-9) { solids++; volume += s2.Volume; }
                    }
                }
            }
            return $"visibleSolids={solids}, volume={volume:F3}";
        }
        return $"activeView={doc.ActiveView.Name}; wallId={fixture.Wall.Id} cat={fixture.Wall.Category?.Id} box={Box(fixture.Wall)} geo={Geo(fixture.Wall, false)} hiddenGeo={Geo(fixture.Wall, true)}; conduitId={fixture.Conduit.Id} cat={fixture.Conduit.Category?.Id} box={Box(fixture.Conduit)} geo={Geo(fixture.Conduit, false)} hiddenGeo={Geo(fixture.Conduit, true)}";
    }
    private static FamilyInstance CreatedOpening(Document doc, IReadOnlyCollection<ElementId> before)
    {
        var created = OpeningInstances(doc).Where(x => !before.Contains(x.Id)).ToList();
        Assert.That(created, Has.Count.EqualTo(1), "exactly one opening family instance should be created");
        return created[0];
    }

    private static List<FamilyInstance> OpeningInstances(Document doc)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(x =>
            {
                try
                {
                    return x.Symbol?.FamilyName.StartsWith("LoadingAI_Opening", StringComparison.OrdinalIgnoreCase) == true;
                }
                catch
                {
                    return false;
                }
            })
            .ToList();

    private sealed record WallAndConduit(Wall Wall, Conduit Conduit);
}
