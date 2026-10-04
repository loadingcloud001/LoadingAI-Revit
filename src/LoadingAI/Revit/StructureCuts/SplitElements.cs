using System.Globalization;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using LoadingAI.Geometry;
using LoadingAI.Tools;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// Shared Revit lookups for both split tools: resolving ids, reading the current selection, and the
/// category filters those tools agree on.
/// </summary>
internal static class SplitElements
{
    public static string Fmt(ElementId id)
        => id.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Warnings must not abort a multi-member write. A native abort still kills the process if
    /// Split is called on an unsafe member — <see cref="CurveMemberSplit.CanNativeSplit"/> is the
    /// real gate.
    /// </summary>
    public static SplitTransaction WriteTx(Document doc, string name) => new SplitTransaction(doc, name);

    /// <summary>
    /// The one write transaction a split run opens. It owns the failure handling: warnings are deleted so
    /// they cannot abort a multi-member write, and an error Revit cannot resolve rolls the whole batch
    /// back without a dialog. <see cref="Commit"/> then returns the report to give instead of a success,
    /// so a run can never describe a write that did not land.
    /// </summary>
    internal sealed class SplitTransaction : IDisposable
    {
        private readonly Transaction _tx;
        private readonly FailureCollector _failures = new FailureCollector();

        public SplitTransaction(Document doc, string name)
        {
            _tx = new Transaction(doc, name);
            var options = _tx.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(true);
            options.SetFailuresPreprocessor(_failures);
            _tx.SetFailureHandlingOptions(options);
        }

        public TransactionStatus Start() => _tx.Start();

        /// <returns>Null when the batch committed; otherwise the "Failed:" report the run must return.</returns>
        public string? Commit()
        {
            var status = _tx.Commit();
            if (status == TransactionStatus.Committed)
                return null;

            return "Failed: Revit rolled back the whole batch (" + status + ")" + _failures.Describe()
                + ". Nothing was changed. Try a smaller limit, or run dryRun first.";
        }

        public void Dispose() => _tx.Dispose();
    }

    private sealed class FailureCollector : IFailuresPreprocessor
    {
        private readonly List<string> _errors = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var warnings = new List<FailureMessageAccessor>();
            var hasError = false;
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    warnings.Add(failure);
                    continue;
                }

                hasError = true;
                var text = Detail(failure);
                if (!string.IsNullOrWhiteSpace(text) && !_errors.Contains(text))
                    _errors.Add(text);
            }

            foreach (var warning in warnings)
                accessor.DeleteWarning(warning);

            // An error Revit cannot resolve would otherwise open its dialog inside this call and stall it
            // until someone answers. Roll the batch back and let Commit say why.
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }

        /// <summary>
        /// What Revit said, plus which elements it blames and which resolutions it offered, so a rolled-back
        /// batch can be traced from the report alone.
        /// </summary>
        private static string Detail(FailureMessageAccessor failure)
        {
            var text = failure.GetDescriptionText();
            try
            {
                var ids = failure.GetFailingElementIds().Select(id => id.Value.ToString(CultureInfo.InvariantCulture)).Take(5).ToList();
                var offered = Enum.GetValues(typeof(FailureResolutionType)).Cast<FailureResolutionType>()
                    .Where(type => type != FailureResolutionType.Invalid && failure.HasResolutionOfType(type))
                    .Select(type => type.ToString()).ToList();
                var parts = new List<string>();
                if (ids.Count > 0)
                    parts.Add("elements " + string.Join(", ", ids));
                if (offered.Count > 0)
                    parts.Add("Revit offered " + string.Join("/", offered));
                if (parts.Count > 0)
                    text += " [" + string.Join("; ", parts) + "]";
            }
            catch
            {
                // The description alone is still worth returning.
            }

            return text;
        }

        public string Describe()
            => _errors.Count == 0 ? "" : ": " + string.Join("; ", _errors.Take(3));
    }

    /// <summary>The element behind an id an earlier page of this run collected, or null when it no longer exists.</summary>
    public static Element? ElementOf(Document doc, string token)
    {
        try
        {
            return doc.GetElement(RevitParse.ParseEid(token));
        }
        catch
        {
            return null;
        }
    }

    public static void Select(UIDocument uidoc, IReadOnlyList<ElementId> created)
    {
        if (created.Count == 0)
            return;

        try
        {
            uidoc.Selection.SetElementIds(created.ToList());
            uidoc.ShowElements(created[0]);
        }
        catch
        {
            // Selecting is a convenience; it must never fail the run.
        }
    }

    public static List<Element> Resolve(Document doc, IReadOnlyList<string> ids, Func<Element, bool> wanted)
        => Resolve(doc, ids, wanted, out _);

    /// <summary>
    /// Ids the caller already named. A blank token is ignored. Anything else that does not parse,
    /// is missing, or fails <paramref name="wanted"/> is returned in <paramref name="missed"/>
    /// rather than dropped, so an explicit id list cannot collapse into "select elements first".
    /// </summary>
    public static List<Element> Resolve(
        Document doc,
        IReadOnlyList<string> ids,
        Func<Element, bool> wanted,
        out List<string> missed)
    {
        var list = new List<Element>();
        missed = new List<string>();
        foreach (var s in ids)
        {
            if (string.IsNullOrWhiteSpace(s))
                continue;

            var token = s.Trim();
            Element? el;
            try
            {
                el = doc.GetElement(RevitParse.ParseEid(token));
            }
            catch
            {
                missed.Add(token);
                continue;
            }

            if (el != null && wanted(el))
                list.Add(el);
            else
                missed.Add(token);
        }

        return list;
    }

    /// <summary>
    /// Per-id answer when the caller supplied ids and none of them became a host. Does not
    /// mention the Revit selection: those ids were the work set.
    /// </summary>
    public static string MissedIdsMessage(int supplied, IReadOnlyList<string> missed, string noun)
    {
        var count = supplied.ToString(CultureInfo.InvariantCulture);
        var message = "0 of " + count + " ids matched " + noun + ".";
        if (missed.Count == 0)
            return message;

        return message + " " + MissedNote(missed);
    }

    /// <summary>
    /// Names supplied ids that did not become hosts. Empty when every supplied id matched, so a
    /// normal run does not grow a footnote.
    /// </summary>
    public static string MissedNote(IReadOnlyList<string> missed)
        => missed.Count == 0 ? "" : "Missed: " + SplitReport.Ids(missed) + ".";

    public static List<Element> GetSelection(UIDocument uidoc)
        => uidoc.Selection.GetElementIds()
            .Select(id => uidoc.Document.GetElement(id))
            .Where(el => el != null)
            .Cast<Element>()
            .ToList();

    public static bool IsFloorLike(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_Floors
           || el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation;

    public static bool IsBeam(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;

    public static bool IsColumn(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;

    public static bool IsWall(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_Walls;

    public static bool IsLevel(Element el) => el is Level;

    public static bool IsGrid(Element el) => el is Grid;

    public static Curve? LocationCurveOf(Element element)
        => element.Location is LocationCurve location ? location.Curve : null;

    public static bool IsHost(Element el)
        => IsWall(el) || IsColumn(el) || IsBeam(el) || IsFloorLike(el);

    /// <summary>
    /// Optional category filter for the Naviate-style tools. Empty means walls, columns, beams and floors.
    /// </summary>
    public static Func<Element, bool> HostsIn(IReadOnlyList<string> categories)
    {
        if (categories.Count == 0)
            return IsHost;

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in categories)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            wanted.Add(raw.Trim());
        }

        if (wanted.Count == 0)
            return IsHost;

        return el =>
            (Match(wanted, "wall", "walls") && IsWall(el))
            || (Match(wanted, "column", "columns") && IsColumn(el))
            || (Match(wanted, "beam", "beams", "framing") && IsBeam(el))
            || (Match(wanted, "floor", "floors", "slab", "slabs") && IsFloorLike(el));
    }

    /// <summary>
    /// No hosts, and the caller did not name ids. "Please select elements first." is only the
    /// selection-scope case. Explicit ids never reach this sentence.
    /// </summary>
    public static string EmptyHostMessage(SplitOptions options)
        => string.Equals(options.Scope, "selection", StringComparison.OrdinalIgnoreCase)
            ? "Please select elements first."
            : "No hosts found.";

    /// <summary>
    /// Shared empty-host choice. Supplied ids that matched nothing get a per-id answer.
    /// "Please select elements first." stays only when there were no ids and the scope is selection.
    /// </summary>
    public static string NoHostMessage(SplitOptions options, SplitBatch batch, string noun)
        => NoHostMessage(options, batch, noun, "No hosts found.");

    public static string NoHostMessage(SplitOptions options, SplitBatch batch, string noun, string noneInScope)
    {
        if (batch.SuppliedIds > 0)
            return MissedIdsMessage(batch.SuppliedIds, batch.Missed, noun);

        return string.Equals(options.Scope, "selection", StringComparison.OrdinalIgnoreCase)
            ? "Please select elements first."
            : noneInScope;
    }

    /// <summary>Category phrase for a missed-id answer. Empty categories are the four host kinds.</summary>
    public static string HostNoun(IReadOnlyList<string> categories)
    {
        var names = categories
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToList();
        return names.Count == 0
            ? "hosts"
            : "category " + string.Join(", ", names);
    }

    private static bool Match(HashSet<string> wanted, params string[] names)
    {
        foreach (var name in names)
        {
            if (wanted.Contains(name))
                return true;
        }

        return false;
    }

    /// <summary>How many elements hang off this one: tags, dimensions, hosted families.</summary>
    public static int Dependent(Element element)
    {
        try
        {
            return element.GetDependentElements(null)?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    public static void AddSkip(List<string> parts, int count, string label)
    {
        if (count > 0)
            parts.Add($"{count} {label}");
    }

    /// <summary>
    /// Loaded-link cutters added to the host set. Flag off never enters here. An unloaded link is
    /// noted and not treated as considered. Explicit host ids are never resolved through a link.
    /// </summary>
    public static LinkCutters Links(Document doc, Func<Element, bool> wanted)
    {
        var result = new LinkCutters();
        if (doc == null)
            return result;

        IList<Element> links;
        try
        {
            links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .WhereElementIsNotElementType()
                .ToElements();
        }
        catch
        {
            return result;
        }

        foreach (var element in links)
        {
            if (element is not RevitLinkInstance link)
                continue;

            Document? linked;
            try
            {
                linked = link.GetLinkDocument();
            }
            catch
            {
                result.Notes.Add(LinkCutterMath.UnloadedNote);
                continue;
            }

            if (linked == null)
            {
                result.Notes.Add(LinkCutterMath.UnloadedNote);
                continue;
            }

            result.Loaded++;
            Transform transform;
            try
            {
                transform = link.GetTotalTransform();
            }
            catch
            {
                result.Notes.Add(LinkCutterMath.TransformFailedNote);
                continue;
            }

            IList<Element> members;
            try
            {
                members = new FilteredElementCollector(linked)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch
            {
                continue;
            }

            foreach (var member in members)
            {
                if (!wanted(member))
                    continue;

                var station = LinkedCutter.Try(member, transform);
                if (station == null)
                {
                    if (member is Level or Grid)
                        result.Notes.Add(LinkCutterMath.TiltedSkip);
                    continue;
                }

                result.Cutters.Add(station);
            }
        }

        return result;
    }

    public sealed class LinkCutters
    {
        public int Loaded { get; set; }

        public List<LinkedCutter> Cutters { get; } = new();

        public List<string> Notes { get; } = new();

        public string Report()
        {
            var distinct = new List<string>();
            foreach (var note in Notes)
            {
                if (!distinct.Contains(note))
                    distinct.Add(note);
            }

            var text = $" Links considered: {Loaded} loaded, {Cutters.Count} link cutters.";
            if (distinct.Count > 0)
                text += " " + string.Join("; ", distinct) + ".";
            return text;
        }
    }

    /// <summary>
    /// One linked cutter already in host space. Geometry readers must use these fields, never the
    /// raw link element: its location and box are still link-local.
    /// </summary>
    public sealed class LinkedCutter
    {
        private LinkedCutter(Element source, Transform toHost, Curve? curve, IReadOnlyList<Solid> solids, BoundingBoxXYZ? box, double? elevation, LinkCutterMath.GridStation? grid)
        {
            Source = source;
            ToHost = toHost;
            Curve = curve;
            Solids = solids;
            Box = box;
            Elevation = elevation;
            Grid = grid;
        }

        public Element Source { get; }

        public Transform ToHost { get; }

        public Curve? Curve { get; }

        public IReadOnlyList<Solid> Solids { get; }

        public BoundingBoxXYZ? Box { get; }

        public double? Elevation { get; }

        public LinkCutterMath.GridStation? Grid { get; }

        public double TypeDepth
        {
            get
            {
                try
                {
                    var type = Source.Document?.GetElement(Source.GetTypeId());
                    if (type == null)
                        return 0;
                    var height = type.get_Parameter(BuiltInParameter.STRUCTURAL_SECTION_COMMON_HEIGHT);
                    if (height == null || !height.HasValue)
                        height = type.LookupParameter("Height");
                    if (height != null && height.HasValue && height.StorageType == StorageType.Double)
                        return height.AsDouble();
                }
                catch
                {
                    return 0;
                }

                return 0;
            }
        }

        public string TypeName
        {
            get
            {
                try
                {
                    return Source.Document?.GetElement(Source.GetTypeId())?.Name ?? Source.Name ?? "";
                }
                catch
                {
                    return Source.Name ?? "";
                }
            }
        }

        public List<List<Point2>> Rings(Plane2D plane) => GeometryRevit.RingsOf(Solids, plane, Box);

        public List<List<Point2>> Edges(Plane2D plane, out List<Point2> centreline)
        {
            centreline = new List<Point2>();
            if (Curve == null)
                return new List<List<Point2>>();

            var half = GeometryRevit.HalfWidthOf(Source, GeometryRevit.ProjectAll(plane, GeometryRevit.Tessellate(Curve)), Box);
            return GeometryRevit.BeamEdgesOf(Curve, plane, half, out centreline);
        }

        public (double Bot, double Top)? ZRange()
        {
            var solid = GeometryRevit.SolidZ(Solids);
            if (solid != null)
                return solid;
            if (Box == null)
                return null;
            return (Box.Min.Z, Box.Max.Z);
        }

        public static LinkedCutter? Try(Element source, Transform toHost)
        {
            if (source is Level level)
                return TryLevel(level, toHost);
            if (source is Grid grid)
                return TryGrid(grid, toHost);

            var curve = GeometryRevit.CurveInHost(LocationCurveOf(source), toHost);
            var solids = GeometryRevit.SolidsInHost(source, toHost);
            var box = GeometryRevit.BoxInHost(source.get_BoundingBox(null), toHost);
            if (curve == null && solids.Count == 0 && box == null)
                return null;

            return new LinkedCutter(source, toHost, curve, solids, box, null, null);
        }

        private static LinkedCutter? TryLevel(Level level, Transform toHost)
        {
            try
            {
                var origin = toHost.OfPoint(new XYZ(0, 0, level.Elevation));
                var normal = toHost.OfVector(XYZ.BasisZ);
                if (LinkCutterMath.IsTiltedLevel(GeometryRevit.ToVec3(normal)))
                    return null;

                return new LinkedCutter(level, toHost, null, Array.Empty<Solid>(), null, origin.Z, null);
            }
            catch
            {
                return null;
            }
        }

        private static LinkedCutter? TryGrid(Grid grid, Transform toHost)
        {
            try
            {
                var curve = grid.Curve;
                if (curve == null)
                    return null;

                var moved = GeometryRevit.CurveInHost(curve, toHost);
                if (moved == null)
                    return null;

                var start = moved.GetEndPoint(0);
                var end = moved.GetEndPoint(1);
                if (LinkCutterMath.IsTiltedGrid(GeometryRevit.ToVec3(start), GeometryRevit.ToVec3(end)))
                    return null;

                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                if ((dx * dx) + (dy * dy) <= 1e-12)
                    return null;

                var station = new LinkCutterMath.GridStation(GeometryRevit.ToVec3(start), GeometryRevit.ToVec3(end));
                return new LinkedCutter(grid, toHost, moved, Array.Empty<Solid>(), null, null, station);
            }
            catch
            {
                return null;
            }
        }
    }
}
