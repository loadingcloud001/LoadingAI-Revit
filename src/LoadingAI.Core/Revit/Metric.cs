namespace LoadingAI.Revit;

/// <summary>
/// LLM-facing units for Hong Kong work. Revit API stays in feet internally;
/// convert at the tool boundary. Length mm, area m², volume m³.
/// </summary>
public static class Metric
{
    public const double MmPerFoot = 304.8;
    public const double SqMPerSqFt = 0.09290304;
    public const double CuMPerCuFt = 0.028316846592;

    public static double FtToMm(double ft) => ft * MmPerFoot;
    public static double MmToFt(double mm) => mm / MmPerFoot;
    public static double Ft2ToM2(double ft2) => ft2 * SqMPerSqFt;
    public static double Ft3ToM3(double ft3) => ft3 * CuMPerCuFt;

    public static string Length(double ft) => $"{FtToMm(ft):F0} mm";
    public static string Area(double ft2) => $"{Ft2ToM2(ft2):F2} m²";
    public static string Volume(double ft3) => $"{Ft3ToM3(ft3):F3} m³";
}
