using Autodesk.Revit.DB;
using LoadingAI.Revit;

namespace LoadingAI.Revit.Formwork;

internal static class FormworkConfig
{
    public const string AppId = "RevitAI_Formwork";
    public const string AppVersion = "1.0.0";
    public const string SchemaGuid = "c3d4e5f6-a7b8-9012-cdef-234567890123";
    public const string SchemaName = "FormworkPanelData";
    public const string SharedParamGroup = "Formwork QS Parameters";
    public const string SharedParamFile = "RevitAI_Formwork_SharedParameters.txt";

    public const double FaceAreaMinSqFt = 0.1;
    public const double CoverRatioDelete = 0.90;
    public const int MaxTrimOperations = 500;
    public const int MaxCollisionChecks = 500;
    public const double MinIntersectionVolM3 = 0.0005;
    public const double CoincidentNudgeMm = 1.5;
    public const double DiffVanishKeepRatio = 0.70;
    public const double MaxAspectRatio = 50.0;
    public const double CylFacetWidthMm = 250.0;
    public const double MinHoleKeepMm = 200.0;
    public const double PolygonFillMaxDiagMm = 1500.0;
    public const double ArcFillMaxDiagMm = 3000.0;
    public const double DedupCentroidTolMm = 150.0;
    public const double DedupNormalTol = 0.1;
    public const double DedupAreaRatioTol = 0.15;
    public const double EpsVol = 1e-6;
    public const double BboxOverlapTolFt = 0.05;
    public const int MaxParentsForCatalog = 20;

    public static double MmToFt(double mm) => Metric.MmToFt(mm);
    public static double SheetThicknessFt(double thicknessMm) => Metric.MmToFt(thicknessMm);
    public static double MinIntersectionVolCuFt => MinIntersectionVolM3 / Metric.CuMPerCuFt;
    public static double DedupCentroidTolFt => Metric.MmToFt(DedupCentroidTolMm);
    public static double MinHoleKeepFt => Metric.MmToFt(MinHoleKeepMm);
    public static double PolygonFillMaxDiagFt => Metric.MmToFt(PolygonFillMaxDiagMm);
    public static double ArcFillMaxDiagFt => Metric.MmToFt(ArcFillMaxDiagMm);

    public static readonly BuiltInCategory[] TargetCategories =
    {
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_Stairs,
    };

    /// <summary>
    /// Aliased from Core rather than re-listed here: the plugin builds its tool schema from the
    /// same array, so a face type can never be generated but refused by the schema, or offered
    /// to the model and silently dropped by the generator (ADR-0013). <see cref="FaceTypes"/>
    /// still holds the names used for comparison and priority inside the algorithms.
    /// </summary>
    public static readonly string[] AllFaceTypes = FormworkDefaults.AllFaceTypes;

    public static readonly IReadOnlyDictionary<string, int> FacePriority = new Dictionary<string, int>
    {
        [FaceTypes.WallExterior] = 80,
        [FaceTypes.WallInterior] = 80,
        [FaceTypes.SlabBottom] = 10,
        [FaceTypes.SlabSide] = 70,
        [FaceTypes.BeamBottom] = 90,
        [FaceTypes.BeamSide] = 85,
        [FaceTypes.ColumnSide] = 95,
        [FaceTypes.FoundationBottom] = 50,
        [FaceTypes.FoundationSide] = 60,
        [FaceTypes.StairsSoffit] = 40,
        [FaceTypes.StairsSide] = 55,
        [FaceTypes.Unknown] = 0,
    };

    public static readonly FormworkParamDef[] Parameters =
    {
        new("FW_ParentElementId", "Integer", "PG_IDENTITY_DATA", "Source element ID from linked model"),
        new("FW_ParentUniqueId", "Text", "PG_IDENTITY_DATA", "Source element UniqueId for cross-reference"),
        new("FW_ParentCategory", "Text", "PG_IDENTITY_DATA", "Category of source element"),
        new("FW_ParentFamily", "Text", "PG_IDENTITY_DATA", "Family name for grouping in schedules"),
        new("FW_ParentType", "Text", "PG_IDENTITY_DATA", "Type name for detailed classification"),
        new("FW_LinkName", "Text", "PG_IDENTITY_DATA", "Source linked model name"),
        new("FW_Level", "Text", "PG_IDENTITY_DATA", "Associated level for filtering and grouping"),
        new("FW_PanelType", "Text", "PG_IDENTITY_DATA", "Panel type (Wall_Exterior, Slab_Bottom, …)"),
        new("FW_Zone", "Text", "PG_IDENTITY_DATA", "Construction zone or phase for grouping"),
        new("FW_Mark", "Text", "PG_IDENTITY_DATA", "Panel mark/number for site identification"),
        new("FW_Area", "Area", "PG_AREA", "Gross panel area (before contact deduction)"),
        new("FW_AreaNet", "Area", "PG_AREA", "Net panel area (after contact deduction)"),
        new("FW_Thickness", "Length", "PG_GEOMETRY", "Panel thickness"),
        new("FW_ContactRatio", "Double", "PG_ANALYSIS_RESULTS", "Ratio of area in contact with other elements (0.0-1.0)"),
        new("FW_CreatedDate", "Text", "PG_IDENTITY_DATA", "Date panel was created"),
    };
}

internal static class FaceTypes
{
    public const string WallExterior = "Wall_Exterior";
    public const string WallInterior = "Wall_Interior";
    public const string SlabBottom = "Slab_Bottom";
    public const string SlabSide = "Slab_Side";
    public const string BeamBottom = "Beam_Bottom";
    public const string BeamSide = "Beam_Side";
    public const string ColumnSide = "Column_Side";
    public const string FoundationBottom = "Foundation_Bottom";
    public const string FoundationSide = "Foundation_Side";
    public const string StairsSoffit = "Stairs_Soffit";
    public const string StairsSide = "Stairs_Side";
    public const string Unknown = "Unknown";
}

internal readonly record struct FormworkParamDef(string Name, string Type, string Group, string Description);

internal sealed class FormworkHostInfo
{
    public bool IsPart { get; init; }
    public BuiltInCategory? Category { get; init; }
    public string CategoryName { get; init; } = "Unknown";
    public Element SourceElem { get; init; } = null!;
    public string? SkipReason { get; init; }
}

internal sealed class FaceInfo
{
    public Face Face { get; init; } = null!;
    public int FaceIndex { get; init; }
    public XYZ? Normal { get; init; }
    public double Area { get; init; }
    public XYZ Centroid { get; init; } = XYZ.Zero;
    public string FaceType { get; init; } = FaceTypes.Unknown;
    public string ElementCategory { get; init; } = "Unknown";
    public BuiltInCategory? ElementCategoryBic { get; init; }
    public FormworkHostInfo? HostInfo { get; set; }
}

internal sealed class FormworkSource
{
    public required Document SourceDoc { get; init; }
    public required Element Element { get; init; }
    public required Transform ToHost { get; init; }
    public required string LinkUid { get; init; }
    public required string LinkName { get; init; }
    public RevitLinkInstance? LinkInst { get; init; }
}

internal sealed class PanelCandidate
{
    public required FaceInfo FaceInfo { get; init; }
    public required FormworkSource Source { get; init; }
    public required string Key { get; init; }
    public required Solid OwnerSolid { get; init; }
    public List<Solid> Solids { get; set; } = new();
    public double ContactRatio { get; set; }
    public bool Skip { get; set; }
}

internal sealed class GenerateOptions
{
    public double ThicknessMm { get; init; } = FormworkDefaults.SheetThicknessMmDefault;
    public HashSet<string> FaceTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Collision { get; init; } = true;
    public bool DeleteExisting { get; init; }
    public bool IncludeUnselectedLinks { get; init; }
}

internal sealed class GenerateResult
{
    public int SelectedElements { get; set; }
    public int Candidates { get; set; }
    public int Duplicates { get; set; }
    public int CoverageSkipped { get; set; }
    public int PanelOverlapSkipped { get; set; }
    public int Trimmed { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int DeletedObsolete { get; set; }
    public int Failed { get; set; }
    public Dictionary<string, int> FailReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
}
