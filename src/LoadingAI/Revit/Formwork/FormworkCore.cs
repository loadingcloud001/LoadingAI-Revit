using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LoadingAI.Tools;

namespace LoadingAI.Revit.Formwork;

internal static class FormworkCore
{
    /// <summary>
    /// Looks the formwork schema up, creating and finishing it on first use.
    /// <para>
    /// <c>SchemaBuilder.Finish()</c> is illegal while a <c>Transaction</c> is open, so every caller must
    /// invoke this <em>before</em> <c>tx.Start()</c>. Called inside an open transaction it is a plain
    /// <c>Schema.Lookup</c> hit once some earlier call has created the schema.
    /// </para>
    /// </summary>
    public static Schema GetOrCreateSchema()
    {
        var guid = new Guid(FormworkConfig.SchemaGuid);
        var existing = Schema.Lookup(guid);
        if (existing != null) return existing;

        var builder = new SchemaBuilder(guid);
        builder.SetSchemaName(FormworkConfig.SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("FWK_Key", typeof(string));
        builder.AddSimpleField("Version", typeof(string));
        return builder.Finish();
    }

    public static List<DirectShape> CollectPanels(Document doc)
    {
        var list = new List<DirectShape>();
        var col = new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShape))
            .WhereElementIsNotElementType();
        foreach (DirectShape ds in col)
        {
            if (string.Equals(ds.ApplicationId, FormworkConfig.AppId, StringComparison.Ordinal))
                list.Add(ds);
        }
        return list;
    }

    public static int DeletePanels(Document doc, IEnumerable<DirectShape> panels)
    {
        var ids = panels.Select(p => p.Id).ToList();
        if (ids.Count == 0) return 0;
        doc.Delete(ids);
        return ids.Count;
    }

    public static FormworkHostInfo ResolveHost(Element el, Document? sourceDoc)
    {
        if (el is Part part)
        {
            if (part.Excluded)
                return new FormworkHostInfo { IsPart = true, SourceElem = el, SkipReason = "excluded", CategoryName = "Parts" };

            foreach (LinkElementId se in part.GetSourceElementIds())
            {
                if (se.LinkInstanceId != ElementId.InvalidElementId || se.LinkedElementId != ElementId.InvalidElementId)
                    return new FormworkHostInfo { IsPart = true, SourceElem = el, SkipReason = "no_original", CategoryName = "Parts" };
            }

            var orig = part.GetSourceElementOriginalCategoryIds();
            if (orig == null || orig.Count == 0)
                return new FormworkHostInfo { IsPart = true, SourceElem = el, SkipReason = "no_original", CategoryName = "Parts" };

            var bic = BicFromCategoryId(orig.First());
            if (bic == null || !FormworkConfig.TargetCategories.Contains(bic.Value))
                return new FormworkHostInfo { IsPart = true, SourceElem = el, SkipReason = "unsupported_category", CategoryName = CategoryName(el) };

            Element source = el;
            foreach (LinkElementId se in part.GetSourceElementIds())
            {
                var hostId = se.HostElementId;
                if (hostId == ElementId.InvalidElementId) continue;
                var found = (sourceDoc ?? el.Document).GetElement(hostId);
                if (found != null) { source = found; break; }
            }

            return new FormworkHostInfo
            {
                IsPart = true,
                Category = bic,
                CategoryName = bic.Value.ToString().Replace("OST_", ""),
                SourceElem = source
            };
        }

        var cat = CategoryBic(el);
        var name = CategoryName(el);
        if (cat == null || !FormworkConfig.TargetCategories.Contains(cat.Value))
        {
            if (!NameLooksStructural(name))
                return new FormworkHostInfo { SourceElem = el, Category = cat, CategoryName = name, SkipReason = "unsupported_category" };
        }

        return new FormworkHostInfo
        {
            Category = cat,
            CategoryName = name,
            SourceElem = el
        };
    }

    public static bool IsCurtainWall(Element source)
    {
        try
        {
            return source is Wall wall && wall.WallType?.Kind == WallKind.Curtain;
        }
        catch { return false; }
    }

    public static List<Solid> GetHostSpaceSolids(FormworkSource source)
    {
        var opt = new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine
        };
        GeometryElement? geo;
        try { geo = source.Element.get_Geometry(opt); }
        catch { return new List<Solid>(); }
        if (geo == null) return new List<Solid>();

        var xf = source.ToHost ?? Transform.Identity;
        var solids = new List<Solid>();
        CollectSolids(geo, xf, solids);

        var split = new List<Solid>();
        foreach (var s in solids)
        {
            try
            {
                var subs = SolidUtils.SplitVolumes(s);
                if (subs != null && subs.Count > 0)
                {
                    foreach (var sub in subs)
                    {
                        if (sub != null && sub.Volume > FormworkConfig.EpsVol)
                            split.Add(sub);
                    }
                    continue;
                }
            }
            catch { /* keep original */ }
            if (s.Volume > FormworkConfig.EpsVol) split.Add(s);
        }
        return split.Count > 0 ? split : solids;
    }

    public static string ClassifyFaceType(XYZ? normal, string categoryName, BuiltInCategory? bic, Element source)
    {
        if (normal == null) return FaceTypes.Unknown;
        var nz = normal.Z;
        var n = categoryName ?? "";

        if (IsCat(bic, BuiltInCategory.OST_Walls, n, "wall"))
        {
            if (Math.Abs(nz) > 0.3) return FaceTypes.Unknown;
            if (source is Wall wall)
            {
                try
                {
                    var ori = wall.Orientation;
                    var nxy = new XYZ(normal.X, normal.Y, 0);
                    if (nxy.GetLength() < 1e-8) return FaceTypes.WallExterior;
                    nxy = nxy.Normalize();
                    var oxy = new XYZ(ori.X, ori.Y, 0);
                    if (oxy.GetLength() < 1e-8) return FaceTypes.WallExterior;
                    oxy = oxy.Normalize();
                    return nxy.DotProduct(oxy) >= 0 ? FaceTypes.WallExterior : FaceTypes.WallInterior;
                }
                catch { return FaceTypes.WallExterior; }
            }
            return FaceTypes.WallExterior;
        }

        if (IsCat(bic, BuiltInCategory.OST_Floors, n, "floor", "slab"))
        {
            if (nz > 0.3) return FaceTypes.Unknown;
            return nz < -0.3 ? FaceTypes.SlabBottom : FaceTypes.SlabSide;
        }

        if (IsCat(bic, BuiltInCategory.OST_StructuralFraming, n, "beam", "framing"))
        {
            if (nz > 0.7) return FaceTypes.Unknown;
            return nz < -0.3 ? FaceTypes.BeamBottom : FaceTypes.BeamSide;
        }

        if (IsCat(bic, BuiltInCategory.OST_StructuralColumns, n, "column") ||
            IsCat(bic, BuiltInCategory.OST_Columns, n, "column"))
        {
            if (Math.Abs(nz) > 0.7) return FaceTypes.Unknown;
            return FaceTypes.ColumnSide;
        }

        if (IsCat(bic, BuiltInCategory.OST_StructuralFoundation, n, "foundation"))
        {
            if (nz > 0.7) return FaceTypes.Unknown;
            return nz < -0.3 ? FaceTypes.FoundationBottom : FaceTypes.FoundationSide;
        }

        if (IsCat(bic, BuiltInCategory.OST_Stairs, n, "stair"))
        {
            if (nz > 0.7) return FaceTypes.Unknown;
            return nz < -0.3 ? FaceTypes.StairsSoffit : FaceTypes.StairsSide;
        }

        return FaceTypes.Unknown;
    }

    public static string ComputeFwkKey(FormworkSource source, FaceInfo face)
    {
        var uid = source.LinkUid ?? "";
        if (uid.Length > 8) uid = uid.Substring(0, 8);
        var c = face.Centroid ?? XYZ.Zero;
        var n = face.Normal ?? XYZ.Zero;
        return string.Format(CultureInfo.InvariantCulture,
            "{0}|{1}|{2}|{3:0.000},{4:0.000},{5:0.000}|{6:0.000},{7:0.000},{8:0.000}|{9:0.000}",
            uid, source.Element.Id, face.FaceType, c.X, c.Y, c.Z, n.X, n.Y, n.Z, face.Area);
    }

    public static string OwnerKey(FormworkSource source) => $"{source.LinkUid}|{source.Element.Id}";

    public static string GetLevelName(Element el)
    {
        var bips = new[]
        {
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.LEVEL_PARAM,
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,
            BuiltInParameter.FAMILY_LEVEL_PARAM
        };
        foreach (var bip in bips)
        {
            try
            {
                var p = el.get_Parameter(bip);
                var id = p?.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) continue;
                if (el.Document.GetElement(id) is Level lv) return lv.Name;
            }
            catch { /* BIP missing on this category */ }
        }

        foreach (var name in new[] { "Level", "Base Level", "Reference Level", "Base Constraint" })
        {
            var p = el.LookupParameter(name);
            var id = p?.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) continue;
            if (el.Document.GetElement(id) is Level lv) return lv.Name;
        }

        try
        {
            if (el.LevelId != ElementId.InvalidElementId && el.Document.GetElement(el.LevelId) is Level l2)
                return l2.Name;
        }
        catch { /* */ }

        return "";
    }

    public static string ElemName(Element el)
    {
        try { return string.IsNullOrWhiteSpace(el.Name) ? el.Id.ToString() : el.Name; }
        catch { return el.Id.ToString(); }
    }

    public static string FamilyName(Element el)
    {
        try
        {
            if (el is FamilyInstance fi) return fi.Symbol?.Family?.Name ?? "";
            var typeId = el.GetTypeId();
            if (typeId != ElementId.InvalidElementId && el.Document.GetElement(typeId) is ElementType et)
                return et.FamilyName ?? "";
        }
        catch { /* */ }
        return "";
    }

    public static string TypeName(Element el)
    {
        try
        {
            if (el is FamilyInstance fi) return fi.Symbol?.Name ?? "";
            var typeId = el.GetTypeId();
            if (typeId != ElementId.InvalidElementId && el.Document.GetElement(typeId) is ElementType et)
                return et.Name ?? "";
        }
        catch { /* */ }
        return "";
    }

    public static string CategoryName(Element el)
    {
        try { return el.Category?.Name ?? "Unknown"; }
        catch { return "Unknown"; }
    }

    public static BuiltInCategory? CategoryBic(Element el)
    {
        try { return el.Category?.BuiltInCategory; }
        catch
        {
            return BicFromCategoryId(el.Category?.Id);
        }
    }

    public static BuiltInCategory? BicFromCategoryId(ElementId? id)
    {
        if (id == null) return null;
        if (long.TryParse(id.ToString(), out var v))
        {
            try { return (BuiltInCategory)(int)v; }
            catch { return null; }
        }
        foreach (var bic in FormworkConfig.TargetCategories)
        {
            if (id.Equals(new ElementId(bic))) return bic;
        }
        if (id.Equals(new ElementId(BuiltInCategory.OST_GenericModel))) return BuiltInCategory.OST_GenericModel;
        return null;
    }

    public static bool IsCat(BuiltInCategory? bic, BuiltInCategory want, string name, params string[] needles)
    {
        if (bic == want) return true;
        var n = (name ?? "").ToLowerInvariant();
        return needles.Any(nd => n.Contains(nd));
    }

    public static List<FormworkSource> ResolveSources(Document doc, IReadOnlyList<string> elementIds)
    {
        var sources = new List<FormworkSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in elementIds)
        {
            Element? el;
            try { el = doc.GetElement(RevitParse.ParseEid(raw)); }
            catch { continue; }
            if (el == null) continue;

            if (el is RevitLinkInstance link)
            {
                var ld = link.GetLinkDocument();
                if (ld == null) continue;
                var xf = link.GetTotalTransform();
                foreach (var bic in FormworkConfig.TargetCategories)
                {
                    var col = new FilteredElementCollector(ld).OfCategory(bic).WhereElementIsNotElementType();
                    foreach (Element src in col)
                    {
                        var key = $"{link.UniqueId}|{src.Id}";
                        if (!seen.Add(key)) continue;
                        sources.Add(new FormworkSource
                        {
                            SourceDoc = ld,
                            Element = src,
                            ToHost = xf,
                            LinkUid = link.UniqueId,
                            LinkName = ld.Title ?? link.Name,
                            LinkInst = link
                        });
                    }
                }
                continue;
            }

            var host = ResolveHost(el, doc);
            if (host.SkipReason != null) continue;
            var own = $"|{el.Id}";
            if (!seen.Add(own)) continue;
            sources.Add(new FormworkSource
            {
                SourceDoc = doc,
                Element = el,
                ToHost = Transform.Identity,
                LinkUid = "",
                LinkName = doc.Title ?? "",
                LinkInst = null
            });
        }
        return sources;
    }

    public static List<FormworkSource> ExtraLinkSources(Document doc, IReadOnlyCollection<string> selectedLinkUids)
    {
        var extras = new List<FormworkSource>();
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).WhereElementIsNotElementType();
        foreach (RevitLinkInstance link in links)
        {
            if (selectedLinkUids.Contains(link.UniqueId)) continue;
            var ld = link.GetLinkDocument();
            if (ld == null) continue;
            var xf = link.GetTotalTransform();
            foreach (var bic in FormworkConfig.TargetCategories)
            {
                var col = new FilteredElementCollector(ld).OfCategory(bic).WhereElementIsNotElementType();
                foreach (Element src in col)
                {
                    extras.Add(new FormworkSource
                    {
                        SourceDoc = ld,
                        Element = src,
                        ToHost = xf,
                        LinkUid = link.UniqueId,
                        LinkName = ld.Title ?? link.Name,
                        LinkInst = link
                    });
                }
            }
        }
        return extras;
    }

    public static int LinkCount(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .WhereElementIsNotElementType()
            .GetElementCount();
    }

    public static void StoreSchema(DirectShape ds, Schema schema, string key)
    {
        try
        {
            var entity = new Entity(schema);
            entity.Set("FWK_Key", key);
            entity.Set("Version", FormworkConfig.AppVersion);
            ds.SetEntity(entity);
        }
        catch { /* schema write is best-effort */ }
    }

    public static double ParamArea(Element el, string name)
    {
        var p = el.LookupParameter(name);
        if (p == null) return 0;
        try { return p.AsDouble(); }
        catch { return 0; }
    }

    public static string ParamText(Element el, string name)
    {
        var p = el.LookupParameter(name);
        if (p == null) return "";
        try { return p.AsString() ?? ""; }
        catch { return ""; }
    }

    static bool NameLooksStructural(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        return new[] { "wall", "floor", "slab", "beam", "framing", "column", "foundation", "stair" }
            .Any(nd => n.Contains(nd));
    }

    static void CollectSolids(GeometryElement geo, Transform xf, List<Solid> solids)
    {
        foreach (GeometryObject obj in geo)
        {
            if (obj is Solid s && s.Faces != null && s.Faces.Size > 0 && s.Volume > FormworkConfig.EpsVol)
            {
                try
                {
                    solids.Add(xf.IsIdentity ? s : SolidUtils.CreateTransformed(s, xf));
                }
                catch { /* skip bad transform */ }
            }
            else if (obj is GeometryInstance gi)
            {
                try
                {
                    var inst = gi.GetInstanceGeometry();
                    if (inst != null) CollectSolids(inst, xf, solids);
                }
                catch { /* */ }
            }
        }
    }
}
