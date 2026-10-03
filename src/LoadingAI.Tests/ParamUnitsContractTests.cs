using System.IO;
using System.Text.Json;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ParamUnitsContractTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { AdapterSource.RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void RevitParse_WritesDoublesThroughParamUnits()
    {
        var src = Read("src", "LoadingAI.RevitUtil", "RevitParse.cs");
        Assert.Contains("ParamUnits.TryParse(", src);
        Assert.Contains("UnitUtils.IsMeasurableSpec", src);
        Assert.DoesNotContain("p.Set(dv)", src);
        Assert.DoesNotContain("NumberStyles.Any", src);
    }

    [Fact]
    public void RevitUtil_ReferencesCore()
    {
        var csproj = Read("src", "LoadingAI.RevitUtil", "LoadingAI.RevitUtil.csproj");
        Assert.Contains("LoadingAI.Core.csproj", csproj);
    }

    [Fact]
    public void ElementDetails_PrintsUnitsNotInternalFeet()
    {
        var src = AdapterSource.Containing("public Task<ElementDetailsDto?> GetElementDetailsAsync");
        var start = src.IndexOf("public Task<ElementDetailsDto?> GetElementDetailsAsync", StringComparison.Ordinal);
        var method = src.Substring(start, src.IndexOf("public Task<FindResultDto> FindElementsFilteredAsync", start, StringComparison.Ordinal) - start);
        Assert.Contains("RevitParse.FormatDouble(p)", method);
        Assert.DoesNotContain("ToString(\"F3\")", method);
        Assert.DoesNotContain("Curve {lc.Curve}", method);
    }

    [Fact]
    public void ExportAndCopy_UseTheUnitHelpers()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> CopyParamsAsync");
        Assert.Contains("ParamUnits.FormatNumber(RevitParse.UnitKindOf(p), p.AsDouble())", src);
        Assert.Contains("AllTools.TryCopyParameter(el, sourceParam, targetParam", src);
        Assert.DoesNotContain("pSrc.AsValueString()", src);
    }

    [Theory]
    [InlineData("set_parameter_by_id")]
    [InlineData("batch_set_parameter")]
    [InlineData("export_params_to_excel")]
    [InlineData("import_excel_to_params")]
    [InlineData("copy_params")]
    public void ParamToolDescriptions_StateTheUnits(string tool)
    {
        var context = new FakePluginContext(new FakeRevitBridge());
        var entry = ParamTools.Create(context).First(e => e.Definition.Function.Name == tool);
        var text = entry.Definition.Function.Description + JsonSerializer.Serialize(entry.Definition.Function.Parameters);
        Assert.True(
            text.IndexOf("mm", StringComparison.Ordinal) >= 0 || text.IndexOf("as numbers", StringComparison.Ordinal) >= 0,
            tool + " must say which units its numbers use");
    }
}
