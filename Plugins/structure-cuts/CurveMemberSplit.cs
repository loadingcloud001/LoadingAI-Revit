using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

using LoadingAI.Geometry;

namespace LoadingAI.Plugins.StructureCuts;

/// <summary>
/// Native <see cref="FamilyInstance.Split"/> then location-curve re-creation for walls, columns and
/// beams. Shared so the four tools do not each re-implement the same two paths.
/// </summary>
internal static class CurveMemberSplit
{
    public const double MinFraction = 0.02;
    public const double MinGapFraction = 0.03;

    public sealed class Plan
    {
        public Plan(Element host, Curve curve, List<double> cuts)
        {
            Host = host;
            Curve = curve;
            Cuts = cuts;
        }

        public Element Host { get; }

        public Curve Curve { get; }

        public List<double> Cuts { get; }
    }

    public static (List<ElementId> Ids, int ParamSkipped, bool Rollback, bool Native, int Dependent) SplitAtCuts(
        Document doc,
        Plan plan,
        SplitOptions options)
    {
        if (TryNativeSplit(doc, plan, options, out var ids))
            return (ids, 0, false, true, 0);

        var (made, paramSkipped, dependent, ok) = Recreate(doc, plan, options);
        return ok
            ? (made, paramSkipped, false, false, dependent)
            : (new List<ElementId>(), paramSkipped, true, false, 0);
    }

    public static bool TryNativeSplit(Document doc, Plan plan, SplitOptions options, out List<ElementId> ids)
    {
        ids = new List<ElementId>();
        if (plan.Host is not FamilyInstance instance || !instance.CanSplit)
            return false;

        var steps = SplitSequence.Build(plan.Cuts, MinGapFraction);
        if (steps.Count == 0)
            return false;

        var mark = options.UniqueMark ? ParamCopy.Mark(plan.Host) : null;
        var high = new List<ElementId>();
        var low = plan.Host.Id;

        using (var sub = new SubTransaction(doc))
        {
            sub.Start();
            try
            {
                foreach (var step in steps)
                {
                    if (doc.GetElement(low) is not FamilyInstance current
                        || !current.CanSplit
                        || current.Location is not LocationCurve location
                        || location.Curve == null)
                    {
                        sub.RollBack();
                        return false;
                    }

                    var live = location.Curve;
                    var span = GeometryRevit.Span(live, out var start);
                    if (Math.Abs(span) <= 1e-12)
                    {
                        sub.RollBack();
                        return false;
                    }

                    var target = GeometryRevit.At(plan.Curve, step.Cut);
                    var local = (live.Project(target).Parameter - start) / span;
                    if (local <= 0.0 || local >= 1.0)
                    {
                        sub.RollBack();
                        return false;
                    }

                    var made = current.Split(local);
                    if (made == null || made == ElementId.InvalidElementId)
                    {
                        sub.RollBack();
                        return false;
                    }

                    if (KeepsStart(current, live, target))
                    {
                        high.Add(made);
                    }
                    else if (doc.GetElement(made) is FamilyInstance other && KeepsStart(other, live, target))
                    {
                        high.Add(low);
                        low = made;
                    }
                    else
                    {
                        sub.RollBack();
                        return false;
                    }
                }
            }
            catch
            {
                try { sub.RollBack(); } catch { }

                ids = new List<ElementId>();
                return false;
            }

            sub.Commit();
        }

        ids.Add(low);
        for (var i = high.Count - 1; i >= 0; i--)
            ids.Add(high[i]);

        if (ids.Count < 2)
        {
            ids = new List<ElementId>();
            return false;
        }

        for (var i = 1; mark != null && i < ids.Count; i++)
        {
            if (doc.GetElement(ids[i]) is Element piece)
                ParamCopy.SetMark(piece, SplitMarks.For(mark, i));
        }

        return true;
    }

    public static (List<ElementId> Ids, int ParamSkipped, int Dependent, bool Ok) Recreate(
        Document doc,
        Plan plan,
        SplitOptions options)
    {
        if (plan.Host is Wall)
            return RecreateWall(doc, plan, options);
        if (plan.Host is FamilyInstance)
            return RecreateInstance(doc, plan, options);

        return (new List<ElementId>(), 0, 0, false);
    }

    private static (List<ElementId> Ids, int ParamSkipped, int Dependent, bool Ok) RecreateInstance(
        Document doc,
        Plan plan,
        SplitOptions options)
    {
        var empty = new List<ElementId>();
        if (plan.Host is not FamilyInstance instance)
            return (empty, 0, 0, false);

        var symbol = doc.GetElement(plan.Host.GetTypeId()) as FamilySymbol;
        if (symbol == null)
            return (empty, 0, 0, false);

        var levelId = plan.Host.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
        if (levelId == null || levelId == ElementId.InvalidElementId)
            levelId = plan.Host.LevelId;
        if (doc.GetElement(levelId) is not Level level)
            return (empty, 0, 0, false);

        var bounds = SplitSequence.Bounds(plan.Cuts, MinGapFraction);
        if (bounds.Count < 2)
            return (empty, 0, 0, false);

        var mark = options.UniqueMark ? ParamCopy.Mark(plan.Host) : null;
        var snapshot = options.CopyParams ? ParamCopy.Take(plan.Host) : null;
        var created = new List<ElementId>();
        var paramSkipped = 0;

        using (var sub = new SubTransaction(doc))
        {
            sub.Start();
            try
            {
                if (!symbol.IsActive)
                    symbol.Activate();

                for (var i = 0; i < bounds.Count - 1; i++)
                {
                    var piece = GeometryRevit.SubCurve(plan.Curve, bounds[i], bounds[i + 1]);
                    if (piece == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, false);
                    }

                    if (i == 0)
                    {
                        if (!RewriteLocation(plan.Host, piece))
                        {
                            sub.RollBack();
                            return (empty, paramSkipped, 0, false);
                        }

                        created.Add(plan.Host.Id);
                        continue;
                    }

                    var made = CreatePiece(doc, piece, symbol, level, instance.StructuralType);
                    if (made == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, false);
                    }

                    var markFor = options.UniqueMark ? SplitMarks.For(mark, i) : null;
                    if (snapshot != null)
                        paramSkipped += ParamCopy.Apply(snapshot, made, markFor);
                    else
                        ParamCopy.SetMark(made, markFor);

                    created.Add(made.Id);
                }

                if (created.Count < 2)
                {
                    sub.RollBack();
                    return (empty, paramSkipped, 0, false);
                }

                var dependent = SplitElements.Dependent(plan.Host);
                sub.Commit();
                return (created, paramSkipped, dependent, true);
            }
            catch
            {
                try { sub.RollBack(); } catch { }
                return (empty, paramSkipped, 0, false);
            }
        }
    }

    private static (List<ElementId> Ids, int ParamSkipped, int Dependent, bool Ok) RecreateWall(
        Document doc,
        Plan plan,
        SplitOptions options)
    {
        var empty = new List<ElementId>();
        if (plan.Host is not Wall wall)
            return (empty, 0, 0, false);

        var bounds = SplitSequence.Bounds(plan.Cuts, MinGapFraction);
        if (bounds.Count < 2)
            return (empty, 0, 0, false);

        var typeId = wall.GetTypeId();
        var levelId = wall.LevelId;
        if (levelId == ElementId.InvalidElementId)
            return (empty, 0, 0, false);

        var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
        if (height <= GeometryRevit.Tol)
            return (empty, 0, 0, false);

        var offset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
        var flip = wall.Flipped;
        var structural = wall.StructuralUsage != StructuralWallUsage.NonBearing;

        var mark = options.UniqueMark ? ParamCopy.Mark(wall) : null;
        var snapshot = options.CopyParams ? ParamCopy.Take(wall) : null;
        var created = new List<ElementId>();
        var paramSkipped = 0;

        using (var sub = new SubTransaction(doc))
        {
            sub.Start();
            try
            {
                for (var i = 0; i < bounds.Count - 1; i++)
                {
                    var piece = GeometryRevit.SubCurve(plan.Curve, bounds[i], bounds[i + 1]);
                    if (piece == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, false);
                    }

                    if (i == 0)
                    {
                        if (!RewriteLocation(wall, piece))
                        {
                            sub.RollBack();
                            return (empty, paramSkipped, 0, false);
                        }

                        created.Add(wall.Id);
                        continue;
                    }

                    Wall? made;
                    try
                    {
                        made = Wall.Create(doc, piece, typeId, levelId, height, offset, flip, structural);
                    }
                    catch
                    {
                        made = null;
                    }

                    if (made == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, false);
                    }

                    var markFor = options.UniqueMark ? SplitMarks.For(mark, i) : null;
                    if (snapshot != null)
                        paramSkipped += ParamCopy.Apply(snapshot, made, markFor);
                    else
                        ParamCopy.SetMark(made, markFor);

                    created.Add(made.Id);
                }

                if (created.Count < 2)
                {
                    sub.RollBack();
                    return (empty, paramSkipped, 0, false);
                }

                var dependent = SplitElements.Dependent(wall);
                sub.Commit();
                return (created, paramSkipped, dependent, true);
            }
            catch
            {
                try { sub.RollBack(); } catch { }
                return (empty, paramSkipped, 0, false);
            }
        }
    }

    public static string ParamNote(SplitOptions options, int native, int recreated, int paramSkipped)
    {
        if (!options.CopyParams)
            return " Copy params: off.";

        if (recreated == 0)
            return native > 0 ? " Native split preserved parameters." : "";

        return paramSkipped > 0
            ? $" Instance parameters copied to new pieces, {paramSkipped} skipped."
            : " Instance parameters copied to new pieces.";
    }

    private static bool KeepsStart(FamilyInstance piece, Curve before, XYZ target)
    {
        if (piece.Location is not LocationCurve location)
            return false;

        var after = location.Curve;
        if (after == null)
            return false;

        return SamePoint(after.GetEndPoint(0), before.GetEndPoint(0))
            && SamePoint(after.GetEndPoint(1), target);
    }

    private static bool SamePoint(XYZ a, XYZ b)
        => a != null && b != null && a.DistanceTo(b) <= GeometryRevit.Tol;

    private static bool RewriteLocation(Element host, Curve piece)
    {
        try
        {
            if (host.Location is LocationCurve location)
            {
                location.Curve = piece;
                return true;
            }
        }
        catch { }

        try
        {
            if (host.Location is LocationCurve fallback)
            {
                fallback.Curve = Line.CreateBound(piece.GetEndPoint(0), piece.GetEndPoint(1));
                return true;
            }
        }
        catch { }

        return false;
    }

    private static Element? CreatePiece(Document doc, Curve piece, FamilySymbol symbol, Level level, StructuralType structuralType)
    {
        try
        {
            var fresh = doc.Create.NewFamilyInstance(piece, symbol, level, structuralType);
            if (fresh != null)
                return fresh;
        }
        catch { }

        try
        {
            return doc.Create.NewFamilyInstance(Line.CreateBound(piece.GetEndPoint(0), piece.GetEndPoint(1)), symbol, level, structuralType);
        }
        catch
        {
            return null;
        }
    }
}
