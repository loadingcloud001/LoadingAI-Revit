using LoadingAI.Revit;
using Xunit;

namespace LoadingAI.Tests;

public class MetricUnitsTests
{
    [Fact]
    public void FtToMm_OneFoot_Is304_8()
    {
        Assert.Equal(304.8, Metric.FtToMm(1.0), 6);
    }

    [Fact]
    public void MmToFt_RoundTrips()
    {
        Assert.Equal(1000.0, Metric.FtToMm(Metric.MmToFt(1000.0)), 6);
    }

    [Fact]
    public void AreaAndVolume_Convert()
    {
        Assert.Equal(0.09290304, Metric.Ft2ToM2(1.0), 8);
        Assert.Equal(0.028316846592, Metric.Ft3ToM3(1.0), 12);
    }

    [Fact]
    public void Formatters_UseMetricLabels()
    {
        Assert.Contains("mm", Metric.Length(1.0));
        Assert.Contains("m²", Metric.Area(1.0));
        Assert.Contains("m³", Metric.Volume(1.0));
        Assert.DoesNotContain("ft", Metric.Length(1.0));
    }
}
