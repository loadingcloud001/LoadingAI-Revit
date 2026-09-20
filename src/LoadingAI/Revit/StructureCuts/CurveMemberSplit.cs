using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

using LoadingAI.Geometry;

namespace LoadingAI.Revit.StructureCuts;

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

    /// <summary>
    /// One wall cut into storeys. The sibling of <see cref="Plan"/> for a horizontal wall, whose
    /// location curve no storey plane can cross: this cut is by height, not by length.
    /// </summary>
    public sealed class Stack
    {
        public Stack(Wall wall, Curve curve, List<StackPiece> pieces)
        {
            Wall = wall;
            Curve = curve;
            Pieces = pieces;
        }

        public Wall Wall { get; }

        /// <summary>The wall's own footprint, reused by every piece at its own storey.</summary>
        public Curve Curve { get; }

        public List<StackPiece> Pieces { get; }
    }

    /// <summary>One piece of a storey stack: the storey it is based on, its base offset and its height.</summary>
    public sealed class StackPiece
    {
        public StackPiece(Level level, double offset, double height)
        {
            Level = level;
            Offset = offset;
            Height = height;
        }

        public Level Level { get; }

        public double Offset { get; }

        public double Height { get; }
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

    /// <summary>
    /// Unique cuts in member order, clustered pairs collapsed so a sliver is never asked of Revit.
    /// </summary>
    public static List<double> DistinctCuts(IEnumerable<double> cuts)
    {
        var wanted = new List<double>();
        foreach (var cut in cuts)
        {
            if (double.IsNaN(cut) || cut <= 0.0 || cut >= 1.0)
                continue;
            wanted.Add(cut);
        }

        wanted.Sort();
        var filtered = new List<double>();
        foreach (var cut in wanted)
        {
            if (filtered.Count == 0 || cut - filtered[filtered.Count - 1] > MinGapFraction)
                filtered.Add(cut);
        }

        return filtered;
    }

    /// <summary>
    /// Native <see cref="FamilyInstance.Split"/> is a process abort on Revit 2024–2027 when the
    /// member is grouped, pinned, joined, or not a line/arc. Recreate those instead. SubTransaction
    /// cannot catch a native abort.
    /// </summary>
    public static bool CanNativeSplit(FamilyInstance instance)
        => CanNativeSplit(instance, requireUnjoined: true);

    private static bool CanNativeSplit(FamilyInstance instance, bool requireUnjoined)
    {
        if (instance == null || !instance.CanSplit || instance.Pinned)
            return false;

        try
        {
            if (instance.GroupId != null && instance.GroupId != ElementId.InvalidElementId)
                return false;
        }
        catch
        {
            return false;
        }

        if (instance.Location is not LocationCurve location || location.Curve == null)
            return false;

        if (location.Curve is not Line && location.Curve is not Arc)
            return false;

        return !requireUnjoined || !Joined(location);
    }

    public static bool TryNativeSplit(Document doc, Plan plan, SplitOptions options, out List<ElementId> ids)
    {
        ids = new List<ElementId>();
        // Joined members are allowed into the sub-transaction so ends can be unjoined first.
        // Grouped, pinned and non-line/arc still abort the process — never call Split on those.
        if (plan.Host is not FamilyInstance instance || !CanNativeSplit(instance, requireUnjoined: false))
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
                        || !CanNativeSplit(current, requireUnjoined: false)
                        || current.Location is not LocationCurve location
                        || location.Curve == null)
                    {
                        sub.RollBack();
                        return false;
                    }

                    if (!TryUnjoinEnds(doc, current, location) || Joined(location)
                        || !CanNativeSplit(current))
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
                    var projected = live.Project(target);
                    if (projected == null)
                    {
                        sub.RollBack();
                        return false;
                    }

                    var local = (projected.Parameter - start) / span;
                    if (local <= 0.0 || local >= 1.0)
                    {
                        sub.RollBack();
                        return false;
                    }

                    // Snapshot endpoints before Split: the LocationCurve object is often invalidated
                    // by the native call, and holding it is a Revit 2024–2027 fatal (report 1034070969).
                    var originalStart = live.GetEndPoint(0);
                    var made = current.Split(local);
                    if (made == null || made == ElementId.InvalidElementId)
                    {
                        sub.RollBack();
                        return false;
                    }

                    if (KeepsStart(current, originalStart, target))
                    {
                        high.Add(made);
                    }
                    else if (doc.GetElement(made) is FamilyInstance other && KeepsStart(other, originalStart, target))
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
        var dependent = SplitElements.Dependent(plan.Host);

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

                doc.Delete(plan.Host.Id);
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
        var dependent = SplitElements.Dependent(wall);

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

                doc.Delete(wall.Id);
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

    /// <summary>
    /// One wall in, one wall per storey out: piece 0 on the wall's own storey with the wall's own base
    /// offset, then one piece per storey up to the wall top. The last piece's storey is the highest
    /// storey strictly below the top, and its height is what remains up to the top.
    /// <para>
    /// Like the curve path, the storey stack deletes the host it replaces and does not re-host its
    /// dependents; the count comes back to the caller, which reports it.
    /// </para>
    /// </summary>
    public static (List<ElementId> Ids, int ParamSkipped, int Dependent, bool Rollback) SplitWallStack(
        Document doc,
        Stack stack,
        SplitOptions options)
    {
        var empty = new List<ElementId>();
        if (stack.Pieces.Count < 2)
            return (empty, 0, 0, true);

        var wall = stack.Wall;
        var typeId = wall.GetTypeId();
        if (typeId == null || typeId == ElementId.InvalidElementId)
            return (empty, 0, 0, true);

        var structural = wall.StructuralUsage != StructuralWallUsage.NonBearing;

        var mark = options.UniqueMark ? ParamCopy.Mark(wall) : null;
        var snapshot = options.CopyParams ? ParamCopy.Take(wall) : null;
        var created = new List<ElementId>();
        var paramSkipped = 0;

        // Counted before the sub-transaction, as RecreateWall does: once the host is deleted its
        // dependents are gone with it and there is nothing left to count.
        var dependent = SplitElements.Dependent(wall);

        using (var sub = new SubTransaction(doc))
        {
            sub.Start();
            try
            {
                for (var i = 0; i < stack.Pieces.Count; i++)
                {
                    var piece = stack.Pieces[i];
                    Wall? made;
                    try
                    {
                        // Every piece keeps the wall's footprint and flip; only the storey it is based
                        // on, its base offset and its height change.
                        made = Wall.Create(
                            doc,
                            stack.Curve,
                            typeId,
                            piece.Level.Id,
                            piece.Height,
                            piece.Offset,
                            wall.Flipped,
                            structural);
                    }
                    catch
                    {
                        made = null;
                    }

                    if (made == null)
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, true);
                    }

                    var markFor = options.UniqueMark ? SplitMarks.For(mark, i) : null;
                    if (snapshot != null)
                        paramSkipped += ParamCopy.Apply(snapshot, made, markFor);
                    else
                        ParamCopy.SetMark(made, markFor);

                    // The copied instance parameters carry the original wall's height and base offset
                    // with them, so the piece's own storey is re-imposed after the copy.
                    if (!PinStorey(made, piece.Offset, piece.Height))
                    {
                        sub.RollBack();
                        return (empty, paramSkipped, 0, true);
                    }

                    created.Add(made.Id);
                }

                if (created.Count < 2)
                {
                    sub.RollBack();
                    return (empty, paramSkipped, 0, true);
                }

                doc.Delete(wall.Id);
                sub.Commit();
                return (created, paramSkipped, dependent, false);
            }
            catch
            {
                try { sub.RollBack(); } catch { }
                return (empty, paramSkipped, 0, true);
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
            ? $" Instance parameters copied; {paramSkipped} parameter(s) skipped."
            : " Instance parameters copied.";
    }

    private static bool Joined(LocationCurve location)
    {
        try
        {
            var atStart = location.get_ElementsAtJoin(0);
            var atEnd = location.get_ElementsAtJoin(1);
            return (atStart != null && atStart.Size > 0) || (atEnd != null && atEnd.Size > 0);
        }
        catch
        {
            // Unknown join status is treated as joined: native Split on a joined member is a
            // process abort that SubTransaction cannot catch.
            return true;
        }
    }

    /// <summary>
    /// Unjoin both ends so native Split can run. Unknown join status stays treated as joined and
    /// the caller must not Split. Never call Split while still joined.
    /// </summary>
    private static bool TryUnjoinEnds(Document doc, FamilyInstance instance, LocationCurve location)
    {
        try
        {
            UnjoinEnd(doc, instance, location, 0);
            UnjoinEnd(doc, instance, location, 1);
            return !Joined(location);
        }
        catch
        {
            return false;
        }
    }

    private static void UnjoinEnd(Document doc, FamilyInstance instance, LocationCurve location, int end)
    {
        try { StructuralFramingUtils.DisallowJoinAtEnd(instance, end); }
        catch { }

        ElementArray? partners;
        try
        {
            partners = location.get_ElementsAtJoin(end);
        }
        catch
        {
            return;
        }

        if (partners == null || partners.Size == 0)
            return;

        var copy = new List<Element>();
        foreach (Element partner in partners)
        {
            if (partner != null)
                copy.Add(partner);
        }

        foreach (var partner in copy)
        {
            try
            {
                if (JoinGeometryUtils.AreElementsJoined(doc, instance, partner))
                    JoinGeometryUtils.UnjoinGeometry(doc, instance, partner);
            }
            catch { }
        }
    }

    private static bool KeepsStart(FamilyInstance piece, XYZ originalStart, XYZ target)
    {
        if (piece.Location is not LocationCurve location)
            return false;

        var after = location.Curve;
        if (after == null)
            return false;

        return SamePoint(after.GetEndPoint(0), originalStart)
            && SamePoint(after.GetEndPoint(1), target);
    }

    private static bool SamePoint(XYZ a, XYZ b)
        => a != null && b != null && a.DistanceTo(b) <= GeometryRevit.Tol;

    /// <summary>
    /// Re-imposes the storey geometry on a stack piece. The instance parameter copy runs last so the
    /// engineer's parameters follow the wall, but it also carries the original wall's height and base
    /// offset, which would stretch the base piece up through the storeys above it and sink every piece
    /// above the first. The piece's own storey wins: too read-only to set is a rollback, not a
    /// silently wrong wall.
    /// </summary>
    private static bool PinStorey(Element piece, double offset, double height)
    {
        var baseOffset = piece.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
        var userHeight = piece.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
        if (baseOffset == null || userHeight == null)
            return false;

        return Set(baseOffset, offset) && Set(userHeight, height);
    }

    private static bool Set(Parameter param, double value)
    {
        try
        {
            if (Math.Abs(param.AsDouble() - value) <= GeometryRevit.Tol)
                return true;
            if (param.IsReadOnly)
                return false;
            return param.Set(value);
        }
        catch
        {
            return false;
        }
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
