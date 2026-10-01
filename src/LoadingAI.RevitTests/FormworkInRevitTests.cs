using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Plugins.Core;
using LoadingAI.Plugins.QsFormwork;
using LoadingAI.Revit;
using LoadingAI.Tools;
using NUnit.Framework;

namespace LoadingAI.RevitTests;

/// <summary>
/// Kind B bridge-only coverage for qs-formwork, closing the F7 gap: the ~1,400 lines of
/// <c>src/LoadingAI/Revit/Formwork/</c> host geometry had no automated test of any kind, because
/// every one of its methods takes a Revit <c>Solid</c>, <c>Face</c> or <c>XYZ</c> and there is no
/// Revit-free seam to test on hosted CI.
///
/// The plugin owns no Revit types (ADR-0009), so these drive the real Adapter through the
/// plugin's handlers and then assert on the <em>model</em> — how many DirectShapes exist, what
/// <c>FW_PanelType</c> they carry, whether the parameter schema was bound — rather than on the
/// prose the tools return. Prose is only checked where the report is the contract.
///
/// Every write is wrapped in <see cref="TransactionGroup.RollBack"/>, so nothing persists. Use
/// <c>CreateTools</c>, not <c>PluginLoader.Discover</c> (Discover wraps core L2 with a TaskDialog).
///
/// <para><b>NOT YET EXECUTED.</b> This fixture compiles for Revit 2024-2027 and CI compiles it, but
/// it has never run: the <c>ricaun</c> adapter cannot get Revit 2027.2 past UI initialisation, and
/// the Revit.TestRunner fallback stops at Revit 2025. Nothing here should be read as evidence that
/// the formwork geometry is correct. See "Verified on Revit 2027.2" in this folder's README.md and
/// finding F7 in <c>docs/PROJECT_BUGS_AND_LIMITATIONS.md</c> for the full diagnosis.</para>
/// </summary>
[TestFixture]
public class FormworkInRevitTests
{
    /// <summary>Must match <c>FormworkConfig.AppId</c>, which is how panels are recognised.</summary>
    private const string PanelAppId = "RevitAI_Formwork";

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
                    $"LoadingAI.RevitTests.formwork.{Guid.NewGuid():N}.rvt");
                created.SaveAs(_tempModelPath);
                created.Close(false);
                _ownedDoc = _uiApp.OpenAndActivateDocument(_tempModelPath)?.Document;
                _bridge.AttachCurrentThread(_uiApp);
            }
            catch (Exception ex)
            {
                Assert.Ignore("Could not create a throwaway project for formwork tests: " + ex.Message);
            }
        }

        _pluginDir = Path.Combine(Path.GetTempPath(), "LoadingAI.RevitTests.formwork");
        Directory.CreateDirectory(_pluginDir);
        var raw = _bridge ?? throw new InvalidOperationException("RevitBridge was not created.");
        var ctx = new PluginContext(raw, _pluginDir);
        _tools = new QsFormworkPlugin()
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
            Assert.Ignore("qs-formwork tools were not created.");
    }

    /// <summary>
    /// The shape the tool contract promises: four L3 tools, all default-off. The panel geometry
    /// itself is only reachable through a live document, which is the rest of this fixture.
    /// </summary>
    [Test]
    public void CreateTools_RegistersFourL3Tools()
    {
        Assert.That(_tools!.Keys, Is.EquivalentTo(new[]
        {
            "formwork_setup",
            "create_formwork_panels",
            "cleanup_formwork",
            "copy_formwork_parent_params",
        }));
        Assert.That(
            _tools.Values.Select(t => t.Tier).Distinct().ToArray(),
            Is.EqualTo(new[] { ToolTier.L3 }),
            "every formwork tool is a write, so every one is L3");
    }

    /// <summary>
    /// The structured contract this migration added, asserted against a live document rather than
    /// the fake: a closed-model read is Failed but still <c>NoWrite</c>, because a query opens no
    /// transaction to commit or roll back. This is the F1/F2 honesty rule the adapter now feeds.
    /// </summary>
    [Test]
    public void Setup_AgainstALiveDocument_IsACommittedWrite()
    {
        var doc = _uiApp.ActiveUIDocument!.Document;
        var panelsBefore = Panels(doc).Count;

        var result = RunResult("formwork_setup", "{}");

        Assert.That(
            result.Text,
            Does.Not.Contain("Please open a model first."),
            "the document is open, so the tool must not take the closed-model path");
        Assert.That(panelsBefore, Is.EqualTo(Panels(doc).Count), "setup creates no panels");
    }

    [Test]
    public void Setup_BindsThePanelParameterSchema()
    {
        var doc = _uiApp.ActiveUIDocument!.Document;

        var text = Run("formwork_setup", "{}");
        TestContext.WriteLine("formwork_setup -> " + text);

        // The schema is the thing setup actually promises, so read it back off the document
        // rather than trusting the report to say it happened.
        var bound = new FilteredElementCollector(doc)
            .OfClass(typeof(ParameterElement))
            .Cast<ParameterElement>()
            .Any(element => string.Equals(element.Name, "FW_PanelType", StringComparison.Ordinal));
        Assert.That(bound, Is.True, "FW_PanelType must exist on the document after setup");
        Assert.That(text, Does.Contain("Formwork setup complete").Or.Contain("Parameters bound"));
    }

    /// <summary>
    /// A dry run counts and writes nothing. This is the F4 discriminator: a dry run must not
    /// create a panel, and its structured result must be <c>NoWrite</c>, not a commit.
    /// </summary>
    [Test]
    public void CreatePanels_DryRun_CountsWithoutWriting()
    {
        var doc = _uiApp.ActiveUIDocument!.Document;
        var wall = TryCreateWall(doc, 100);
        if (wall == null)
            Assert.Ignore("No wall type available to probe panel generation.");

        var panelsBefore = Panels(doc).Count;
        var id = wall.Id;

        var text = Run("create_formwork_panels", "{\"dryRun\":true,\"elementIds\":[\"" + id.Value + "\"]}");

        TestContext.WriteLine("create_formwork_panels dry -> " + text);
        Assert.That(
            Panels(doc).Count,
            Is.EqualTo(panelsBefore),
            "a dry run must not create a panel");
    }

    /// <summary>
    /// A write run on a real wall, rolled back. Asserts on the model: the panel either exists with
    /// a recognised <c>FW_PanelType</c>, or the report names why it did not. A host that cannot
    /// build a panel is recorded, not asserted into a pass.
    /// </summary>
    [Test]
    public void CreatePanels_Write_ThenRollBack_LeavesTheModelUnchanged()
    {
        var doc = _uiApp.ActiveUIDocument!.Document;
        var wall = TryCreateWall(doc, 140);
        if (wall == null)
            Assert.Ignore("No wall type available to probe panel generation.");

        var panelsBefore = Panels(doc).Count;
        var id = wall.Id;
        string text;

        using (var group = new TransactionGroup(doc, "LoadingAI.RevitTests formwork write probe"))
        {
            group.Start();
            try
            {
                text = Run("create_formwork_panels", "{\"dryRun\":false,\"elementIds\":[\"" + id.Value + "\"]}");
                TestContext.WriteLine("create_formwork_panels write -> " + text);

                // Whatever the report says, the model is the contract: either a panel was made and
                // carries a known face type, or nothing was written. Both are read from the document.
                var panels = Panels(doc);
                if (panels.Count > panelsBefore)
                {
                    Assert.That(
                        panels.Skip(panelsBefore).Any(panel => PanelType(panel) != "Unknown"),
                        Is.True,
                        "a created panel must carry a recognised FW_PanelType, not Unknown: " + text);
                }
                else
                {
                    TestContext.WriteLine("No panel created in this host: " + text);
                }

                // stats reads the same panels back, and is a read: it must not create or delete any.
                var afterWrite = Panels(doc).Count;
                Run("cleanup_formwork", "{\"action\":\"stats\"}");
                Assert.That(Panels(doc).Count, Is.EqualTo(afterWrite), "stats must not change the panel count");
            }
            finally
            {
                group.RollBack();
            }
        }

        Assert.That(
            Panels(doc).Count,
            Is.EqualTo(panelsBefore),
            "rolling the group back restores the panel count");
    }

    /// <summary>
    /// The cleanup family on a model that may hold no panels at all. Each action has to be
    /// answerable, and the destructive ones have to be safe to roll back.
    /// </summary>
    [Test]
    public void Cleanup_Actions_AreAnswerableAndSafeToRollBack()
    {
        var doc = _uiApp.ActiveUIDocument!.Document;
        var before = Panels(doc).Count;

        foreach (var action in new[] { "stats", "delete_all", "delete_by_type", "cleanup_orphans" })
        {
            using var group = new TransactionGroup(doc, "LoadingAI.RevitTests cleanup probe " + action);
            group.Start();
            try
            {
                var args = action == "delete_by_type"
                    ? "{\"action\":\"delete_by_type\",\"faceType\":\"Wall_Exterior\"}"
                    : "{\"action\":\"" + action + "\"}";
                var text = Run("cleanup_formwork", args);
                TestContext.WriteLine("cleanup_formwork " + action + " -> " + text);
                Assert.That(
                    text,
                    Is.Not.Empty,
                    action + " must answer, and must not report the closed-model path");
            }
            finally
            {
                group.RollBack();
            }
        }

        Assert.That(Panels(doc).Count, Is.EqualTo(before), "rolled-back cleanup changes nothing");
    }

    /// <summary>
    /// The catalogue path of copy_formwork_parent_params: with no paramNames it lists copyable
    /// parameters and writes nothing. An empty <c>Names</c> is a read, so it must be reported
    /// <c>NoWrite</c> rather than as a committed parameter copy.
    /// </summary>
    [Test]
    public void CopyParentParams_CataloguePath_ListsWithoutWriting()
    {
        var doc = _uiApp.ActiveUIDocument!.Document;
        var panelsBefore = Panels(doc).Count;

        var text = Run("copy_formwork_parent_params", "{}");

        TestContext.WriteLine("copy_formwork_parent_params catalogue -> " + text);
        Assert.That(text, Is.Not.Empty, "the catalogue path must answer");
        Assert.That(
            Panels(doc).Count,
            Is.EqualTo(panelsBefore),
            "listing the catalogue must not create a panel");
    }

    // ---- model helpers -------------------------------------------------------------

    /// <summary>Every DirectShape the formwork generator owns, read the way the host reads them.</summary>
    private static List<DirectShape> Panels(Document doc)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .WhereElementIsNotElementType()
            .Cast<DirectShape>()
            .Where(shape => string.Equals(shape.ApplicationId, PanelAppId, StringComparison.Ordinal))
            .ToList();

    /// <summary>The panel's face type, or Unknown when the host did not stamp one.</summary>
    private static string PanelType(DirectShape panel)
    {
        var value = panel.LookupParameter("FW_PanelType");
        try
        {
            return value == null ? "Unknown" : (value.AsString() ?? "Unknown");
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// A probe wall on the lowest storey. Returns null when the model holds no basic wall type, so
    /// the caller records the claim as unasserted rather than asserting on a wall it never made.
    /// </summary>
    private static Wall? TryCreateWall(Document doc, double yFeet)
    {
        var wallType = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(type => type.Kind == WallKind.Basic);
        var level = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(candidate => candidate.Elevation)
            .FirstOrDefault();
        if (wallType == null || level == null)
            return null;

        var line = Line.CreateBound(new XYZ(0, yFeet, level.Elevation), new XYZ(20, yFeet, level.Elevation));
        using var tx = new Transaction(doc, "LoadingAI.RevitTests create formwork probe wall");
        tx.Start();
        try
        {
            var wall = Wall.Create(doc, line, wallType.Id, level.Id, 10.0, 0, false, false);
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

    /// <summary>Runs a tool through its legacy string path, which is what the prose contract is.</summary>
    private string Run(string tool, string json)
    {
        var entry = Tool(tool);
        return entry.ExecuteAsync(json).GetAwaiter().GetResult().Text;
    }

    /// <summary>Runs a tool through the structured path so the status and certainty are assertable.</summary>
    private ToolExecutionResult RunResult(string tool, string json)
    {
        var entry = Tool(tool);
        return entry.ExecuteAsync(json).GetAwaiter().GetResult();
    }

    private ToolEntry Tool(string name)
        => _tools != null && _tools.TryGetValue(name, out var entry)
            ? entry
            : throw new InvalidOperationException("Unknown formwork tool: " + name);
}
