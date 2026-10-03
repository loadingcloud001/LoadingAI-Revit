using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LoadingAI.Plugins.StructureCuts;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class SplitRunTests
{
    private static readonly string[] Algorithms = { "BeamSplit.cs", "BeamCut.cs", "FloorSplit.cs", "LevelSplit.cs", "GridSplit.cs" };

    private static string HostDir() => Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Revit", "StructureCuts");

    private static string Host(string file) => File.ReadAllText(Path.Combine(HostDir(), file));

    // ---- the result ----------------------------------------------------------------------

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry Tool(FakePluginContext context, string name)
        => new StructureCutsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);

    [Fact]
    public async System.Threading.Tasks.Task SomeSplitsAndSomeFailures_IsPartial_AndTheWritesStay()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitBeamsResult = new SplitResultDto(
            "Split 2 beams into 5 pieces. Skipped: 1 split failed.", 2, 0, 1, new[] { "7001", "7002" }, null, 1);

        var result = await Tool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Partial, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async System.Threading.Tasks.Task EveryAttemptFailing_IsFailedAndWroteNothing()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitByLevelsResult = new SplitResultDto(
            "Split 0 elements into 0 pieces. Skipped: 4 split failed.", 0, 0, 4, new string[0], null, 4);

        var result = await Tool(context, "split_by_levels").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal(4, result.FailedCount);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async System.Threading.Tasks.Task SkipsAlone_StillAreNotFailures()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitBeamsResult = new SplitResultDto("Split 2 beams. Skipped 3: pinned.", 2, 0, 3, new[] { "1", "2" });

        var result = await Tool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void TheOutcomeCarriesTheFailureCountToTheDto()
    {
        var outcome = AdapterSource.Containing("public Task<SplitResultDto?> SplitBeamsByColumnsAsync");
        Assert.Contains("outcome.NewIds, null, outcome.Failed)", outcome);

        var text = Host("SplitOutcome.cs");
        Assert.Contains("public int Failed { get; set; }", text);
        Assert.Contains("int skipped, int failed = 0)", text);

        foreach (var file in Algorithms)
            Assert.Matches(@"outcome\?\.Record\([^;]*(skippedFailed|skips\.Failed|skips\.DeleteFailed)\);", Host(file));
    }

    // ---- the cursor ----------------------------------------------------------------------

    [Fact]
    public void EveryHostCollectionGoesThroughTheCursor_UnderTheToolsOwnName()
    {
        var scope = Host("SplitScope.cs");
        Assert.Contains("Cursors.Page(", scope);
        Assert.Contains("SplitCursorStore.KeyFor(command,", scope);
        Assert.Contains("SplitElements.Resolve(doc, ids, wanted, out missed)", scope);
        Assert.Contains("public int NextOffset => Offset + Consumed;", scope);

        var plugin = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "structure-cuts", "StructureCutsPlugin.cs"));
        foreach (var (file, command) in new[]
        {
            ("BeamSplit.cs", "split_beams_by_columns"),
            ("BeamCut.cs", "split_secondary_beams_by_main_beams"),
            ("FloorSplit.cs", "split_floors_by_beams"),
            ("LevelSplit.cs", "split_by_levels"),
            ("GridSplit.cs", "split_by_grids"),
        })
        {
            var call = Regex.Match(Host(file), @"SplitScope\.Collect\([^;]*\);");
            Assert.True(call.Success, file);
            Assert.Contains($"\"{command}\"", call.Value);
            Assert.Contains($"Name = \"{command}\"", plugin);
        }
    }

    // ---- the transaction -----------------------------------------------------------------

    [Fact]
    public void EveryRunReadsTheCommit_AndReturnsTheReportWhenRevitRolledBack()
    {
        foreach (var file in Algorithms)
        {
            var text = Host(file);
            var write = text.IndexOf("SplitElements.WriteTx(", System.StringComparison.Ordinal);
            var commit = text.IndexOf("var rolledBack = tx.Commit();", System.StringComparison.Ordinal);
            var back = text.IndexOf("return rolledBack;", System.StringComparison.Ordinal);
            Assert.True(write >= 0 && commit > write && back > commit, file);
            Assert.Equal(1, SourceSlice.Count(text, "tx.Commit();"));
        }
    }

    [Fact]
    public void AnErrorRollsTheBatchBack_WithoutADialog_AndWarningsStillGoQuietly()
    {
        var text = Host("SplitElements.cs");
        Assert.Contains("FailureProcessingResult.ProceedWithRollBack", text);
        Assert.Contains("accessor.DeleteWarning(", text);
        Assert.Contains("TransactionStatus.Committed", text);
        Assert.DoesNotContain("class SkipWarnings", text);
        Assert.Equal(1, SourceSlice.Count(text, "new Transaction("));
    }

    // ---- the dry run ---------------------------------------------------------------------

    [Fact]
    public void TheDryRunKeepsItsPinnedWords_AndAddsTheForecast()
    {
        foreach (var file in new[] { "BeamSplit.cs", "BeamCut.cs", "FloorSplit.cs", "LevelSplit.cs", "GridSplit.cs" })
        {
            var text = Host(file);
            Assert.Contains("\"Dry-run. \"", text);
            Assert.Contains("No changes made.", text);
            Assert.Contains("SplitForecast.Note(", text);
        }

        Assert.Contains("public static (int Native, int Recreated, int Dependents) Forecast(", Host("CurveMemberSplit.cs"));
    }

    [Fact]
    public void TheHostFolderStillHasFourteenFiles()
        => Assert.Equal(14, Directory.GetFiles(HostDir(), "*.cs").Length);

    [Fact]
    public void ContinueFromTellsTheModelWhatItPagesThrough()
    {
        var plugin = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "structure-cuts", "StructureCutsPlugin.cs"));
        Assert.Contains("nextOffset the previous result named", plugin);
        Assert.DoesNotContain("Skip this many matching hosts before applying limit", plugin);
    }
}
