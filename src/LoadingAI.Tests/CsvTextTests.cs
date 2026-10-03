using System.Globalization;
using System.IO;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class CsvTextTests
{
    [Theory]
    [InlineData("hello", "\"hello\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData(null, "\"\"")]
    [InlineData("=SUM(A1:A9)", "\"'=SUM(A1:A9)\"")]
    [InlineData("@cmd", "\"'@cmd\"")]
    [InlineData("+1+1", "\"'+1+1\"")]
    [InlineData("-HYPERLINK(\"x\")", "\"'-HYPERLINK(\"\"x\"\")\"")]
    [InlineData("\tfoo", "\"'\tfoo\"")]
    public void Cell_QuotesAndNeutralisesFormulas(string? input, string expected)
        => Assert.Equal(expected, CsvText.Cell(input));

    [Theory]
    [InlineData("-5.2")]
    [InlineData("+3")]
    public void Cell_LeavesPlainSignedNumbersAlone(string input)
        => Assert.Equal("\"" + input + "\"", CsvText.Cell(input));

    [Fact]
    public void Number_IgnoresTheCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("12.50", CsvText.Number(12.5, "0.00"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Row_JoinsQuotedCells()
        => Assert.Equal("\"a\",\"b,c\"", CsvText.Row(new[] { "a", "b,c" }));
}

public class ExportPathsHardeningTests
{
    [Theory]
    [InlineData(@"export_\..\..\..\x.csv")]
    [InlineData("export_/../../../x.csv")]
    [InlineData("..")]
    [InlineData("a:b*c?.csv")]
    public void ADefaultNameCannotLeaveTheExportRoot(string defaultName)
    {
        var path = ExportPaths.ResolveWrite(null, defaultName);
        Assert.True(ExportPaths.IsUnderRoot(path), path);
        Assert.Equal(Path.GetFullPath(ExportPaths.Root), Path.GetDirectoryName(Path.GetFullPath(path))!.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AnOrdinaryDefaultNameIsKept()
        => Assert.EndsWith("takeoff_Doors_20261003_120000.csv", ExportPaths.ResolveWrite(null, "takeoff_Doors_20261003_120000.csv"));

    [Fact]
    public void Source_BothCsvWritersUseTheHelpers()
    {
        string Read(params string[] p) => File.ReadAllText(Path.Combine(new[] { AdapterSource.RepoRoot() }.Concat(p).ToArray()));
        var param = Read("Plugins", "core-tools", "Core", "ParamTools.cs");
        var query = Read("Plugins", "core-tools", "Core", "QueryTools.cs");
        Assert.Contains("CsvText.Cell(v)", param);
        Assert.DoesNotContain("Replace(\"\\\"\", \"\\\"\\\"\")", param);
        Assert.Contains("CsvText.Number(", query);
        Assert.DoesNotContain(":F2}", query);
        Assert.DoesNotContain("DateTime.Now:yyyyMMdd_HHmmss", param + query);
    }
}
