using System.Globalization;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class ParamUnitsTests
{
    [Theory]
    [InlineData(ParamUnitKind.Length, 3000.0, 3000.0 / 304.8)]
    [InlineData(ParamUnitKind.Area, 10.0, 10.0 / 0.09290304)]
    [InlineData(ParamUnitKind.Volume, 2.0, 2.0 / 0.028316846592)]
    [InlineData(ParamUnitKind.Angle, 180.0, Math.PI)]
    [InlineData(ParamUnitKind.Plain, 42.5, 42.5)]
    public void ToInternal_ConvertsToolUnitsToRevitUnits(ParamUnitKind kind, double tool, double expected)
        => Assert.Equal(expected, ParamUnits.ToInternal(kind, tool), 9);

    [Theory]
    [InlineData(ParamUnitKind.Length, 3000.0)]
    [InlineData(ParamUnitKind.Area, 12.345)]
    [InlineData(ParamUnitKind.Volume, 0.75)]
    [InlineData(ParamUnitKind.Angle, 37.5)]
    [InlineData(ParamUnitKind.Plain, 7.0)]
    public void FromInternal_RoundTrips(ParamUnitKind kind, double tool)
        => Assert.Equal(tool, ParamUnits.FromInternal(kind, ParamUnits.ToInternal(kind, tool)), 9);

    [Fact]
    public void Format_PrintsTheToolUnit()
    {
        Assert.Equal("3000 mm", ParamUnits.Format(ParamUnitKind.Length, 3000.0 / 304.8));
        Assert.Equal("10 m²", ParamUnits.Format(ParamUnitKind.Area, 10.0 / 0.09290304));
        Assert.Equal("2 m³", ParamUnits.Format(ParamUnitKind.Volume, 2.0 / 0.028316846592));
        Assert.Equal("180°", ParamUnits.Format(ParamUnitKind.Angle, Math.PI));
        Assert.Equal("0.5", ParamUnits.Format(ParamUnitKind.Plain, 0.5));
    }

    [Fact]
    public void FormatNumber_HasNoSuffixAndNoNegativeZero()
    {
        Assert.Equal("3000", ParamUnits.FormatNumber(ParamUnitKind.Length, 3000.0 / 304.8));
        Assert.Equal("0", ParamUnits.FormatNumber(ParamUnitKind.Length, -0.0));
        Assert.Equal("0", ParamUnits.FormatNumber(ParamUnitKind.Length, -1e-12));
    }

    [Fact]
    public void Format_IgnoresTheCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1234.5 mm", ParamUnits.Format(ParamUnitKind.Length, 1234.5 / 304.8));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData(ParamUnitKind.Length, "3000", 3000.0)]
    [InlineData(ParamUnitKind.Length, "3000 mm", 3000.0)]
    [InlineData(ParamUnitKind.Length, "3000mm", 3000.0)]
    [InlineData(ParamUnitKind.Length, "3 m", 3000.0)]
    [InlineData(ParamUnitKind.Length, "30 cm", 300.0)]
    [InlineData(ParamUnitKind.Length, "12 in", 304.8)]
    [InlineData(ParamUnitKind.Length, "1 ft", 304.8)]
    [InlineData(ParamUnitKind.Length, "-250.5", -250.5)]
    [InlineData(ParamUnitKind.Length, "1,000.5", 1000.5)]
    [InlineData(ParamUnitKind.Area, "2.5 m²", 2.5)]
    [InlineData(ParamUnitKind.Area, "2.5 m2", 2.5)]
    [InlineData(ParamUnitKind.Volume, "0.4 m³", 0.4)]
    [InlineData(ParamUnitKind.Angle, "90", 90.0)]
    [InlineData(ParamUnitKind.Angle, "90°", 90.0)]
    [InlineData(ParamUnitKind.Plain, "12.5", 12.5)]
    public void TryParse_ReadsNumbersInToolUnits(ParamUnitKind kind, string text, double expectedToolValue)
    {
        Assert.True(ParamUnits.TryParse(kind, text, out var internalValue, out var error), error);
        Assert.Equal(ParamUnits.ToInternal(kind, expectedToolValue), internalValue, 9);
    }

    [Fact]
    public void TryParse_ReadsRadiansForAngles()
    {
        Assert.True(ParamUnits.TryParse(ParamUnitKind.Angle, "1.5707963 rad", out var internalValue, out _));
        Assert.Equal(Math.PI / 2, internalValue, 6);
    }

    [Theory]
    [InlineData(ParamUnitKind.Length, "")]
    [InlineData(ParamUnitKind.Length, "   ")]
    [InlineData(ParamUnitKind.Length, "abc")]
    [InlineData(ParamUnitKind.Length, "mm")]
    [InlineData(ParamUnitKind.Length, "3,5")]
    [InlineData(ParamUnitKind.Length, "1.2.3")]
    [InlineData(ParamUnitKind.Length, "3 kg")]
    [InlineData(ParamUnitKind.Area, "3 mm")]
    [InlineData(ParamUnitKind.Plain, "3 mm")]
    public void TryParse_RefusesWhatItCannotRead(ParamUnitKind kind, string text)
    {
        Assert.False(ParamUnits.TryParse(kind, text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_NamesTheAcceptedUnitsInTheError()
    {
        ParamUnits.TryParse(ParamUnitKind.Length, "3 kg", out _, out var error);
        Assert.Contains("mm", error);
    }

    [Fact]
    public void TryParse_RejectsADecimalCommaInsteadOfReadingItAsThousands()
    {
        ParamUnits.TryParse(ParamUnitKind.Length, "3,5", out _, out var error);
        Assert.Contains("decimal", error, StringComparison.OrdinalIgnoreCase);
    }
}
