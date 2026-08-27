using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.ViewTools;

public sealed class ViewToolsPlugin : IRevitToolPlugin, IRevitFullAccess
{
    public string Id => "view-tools";
    public string Name => "View & Schedule Tools";
    public string Version => "1.0.0";

    public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
    {
        yield return CreateSchedule(context);
        yield return GetScheduleData(context);
        yield return CreateFloorPlan(context);
        yield return Create3dView(context);
        yield return CreateSectionView(context);
        yield return PathOfTravelTool(context);
    }

    private static ToolEntry CreateSchedule(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_schedule",
                Description = "Create a category schedule (ScheduleCreation). Params: category, name optional, fields array optional (else first instance fields).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        category = new { type = "string" },
                        name = new { type = "string" },
                        fields = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "category" }
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var cat = args.Str("category");
            var name = args.Str("name");
            var fields = args.Strings("fields");
            if (string.IsNullOrWhiteSpace(cat)) return "category is required.";
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                var bic = RevitParse.ParseCategory(cat);
                if (bic == null) return $"Unknown category {cat}";
                try
                {
                    using var tx = new Transaction(doc, "LoadingAI for Revit: create schedule");
                    tx.Start();
                    var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(bic.Value), ElementId.InvalidElementId);
                    schedule.Name = string.IsNullOrWhiteSpace(name) ? $"{cat} Schedule" : name.Trim();
                    var added = AddScheduleFields(schedule, fields);
                    tx.Commit();
                    return $"Created schedule {schedule.Id} \"{schedule.Name}\" with {added} field(s).";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry GetScheduleData(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "get_schedule_data",
                Description = "Read schedule table cells. Params: scheduleId or name, rowLimit (default 25, max 40).",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        scheduleId = new { type = "string" },
                        name = new { type = "string" },
                        rowLimit = new { type = "integer" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var sid = args.Str("scheduleId");
            var name = args.Str("name");
            var limit = Math.Min(40, Math.Max(1, args.Int("rowLimit", 25)));
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var schedule = FindSchedule(doc, sid, name);
                    if (schedule == null) return "Schedule not found. Pass scheduleId or name.";
                    var body = schedule.GetTableData().GetSectionData(SectionType.Body);
                    var rows = Math.Min(body.NumberOfRows, limit);
                    var cols = body.NumberOfColumns;
                    var sb = new StringBuilder();
                    sb.AppendLine($"{schedule.Name} ({schedule.Id}) rows={body.NumberOfRows} cols={cols} showing={rows}");
                    for (var r = 0; r < rows; r++)
                    {
                        var cells = new List<string>(cols);
                        for (var c = 0; c < cols; c++)
                            cells.Add(schedule.GetCellText(SectionType.Body, r, c).Replace('\t', ' '));
                        sb.AppendLine(string.Join("\t", cells));
                    }
                    return sb.ToString();
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry CreateFloorPlan(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_floor_plan",
                Description = "Create a floor plan view on a level. Params: levelId or levelName, name optional.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        levelId = new { type = "string" },
                        levelName = new { type = "string" },
                        name = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var levelId = args.Str("levelId");
            var levelName = args.Str("levelName");
            var name = args.Str("name");
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var typeId = FindViewFamily(doc, ViewFamily.FloorPlan);
                    if (typeId == null) return "No FloorPlan view type found.";
                    var level = ResolveLevel(doc, levelId, levelName);
                    if (level == null) return "Level not found. Pass levelId or levelName.";
                    using var tx = new Transaction(doc, "LoadingAI for Revit: create floor plan");
                    tx.Start();
                    var view = ViewPlan.Create(doc, typeId, level.Id);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return $"Created floor plan {view.Id} \"{view.Name}\" on {level.Name}";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry Create3dView(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_3d_view",
                Description = "Create an isometric 3D view. Params: name optional.",
                Parameters = new
                {
                    type = "object",
                    properties = new { name = new { type = "string" } },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var name = Args.Parse(json).Str("name");
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var typeId = FindViewFamily(doc, ViewFamily.ThreeDimensional);
                    if (typeId == null) return "No 3D view type found.";
                    using var tx = new Transaction(doc, "LoadingAI for Revit: create 3D view");
                    tx.Start();
                    var view = View3D.CreateIsometric(doc, typeId);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return $"Created 3D view {view.Id} \"{view.Name}\"";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry CreateSectionView(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_section_view",
                Description = "Create a section (CreateViewSection). Params: elementId of a wall/curve, or x1,y1,x2,y2 in mm. Optional name, height, depth in mm.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementId = new { type = "string" },
                        x1 = new { type = "number" },
                        y1 = new { type = "number" },
                        x2 = new { type = "number" },
                        y2 = new { type = "number" },
                        z = new { type = "number" },
                        name = new { type = "string" },
                        height = new { type = "number" },
                        depth = new { type = "number" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var eid = args.Str("elementId");
            var name = args.Str("name");
            var height = Metric.MmToFt(args.Dbl("height", 3000));
            var depth = Metric.MmToFt(args.Dbl("depth", 2500));
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    XYZ? a = null, b = null;
                    if (!string.IsNullOrWhiteSpace(eid))
                    {
                        var el = doc.GetElement(RevitParse.ParseEid(eid));
                        if (el?.Location is LocationCurve lc && lc.Curve != null)
                        {
                            a = lc.Curve.GetEndPoint(0);
                            b = lc.Curve.GetEndPoint(1);
                        }
                        else return $"Element {eid} has no location curve (use a wall).";
                    }
                    else if (args.HasNumber("x1") && args.HasNumber("y1") && args.HasNumber("x2") && args.HasNumber("y2"))
                    {
                        var z = Metric.MmToFt(args.Dbl("z", 0));
                        a = new XYZ(Metric.MmToFt(args.Dbl("x1", 0)), Metric.MmToFt(args.Dbl("y1", 0)), z);
                        b = new XYZ(Metric.MmToFt(args.Dbl("x2", 0)), Metric.MmToFt(args.Dbl("y2", 0)), z);
                    }
                    if (a == null || b == null) return "Pass elementId or x1,y1,x2,y2.";

                    var typeId = FindViewFamily(doc, ViewFamily.Section);
                    if (typeId == null) return "No Section view type found.";
                    var box = SectionBox(a, b, height, depth);
                    using var tx = new Transaction(doc, "LoadingAI for Revit: create section");
                    tx.Start();
                    var view = ViewSection.CreateSection(doc, typeId, box);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return $"Created section {view.Id} \"{view.Name}\"";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static ToolEntry PathOfTravelTool(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "path_of_travel",
                Description = "Create path of travel on a floor plan (PathOfTravel.CreateMultiple). Params: startX,startY,endX,endY (mm) or starts/ends as [x,y] arrays. viewId optional. Max 5 pairs.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        startX = new { type = "number" },
                        startY = new { type = "number" },
                        endX = new { type = "number" },
                        endY = new { type = "number" },
                        starts = new { type = "array" },
                        ends = new { type = "array" },
                        viewId = new { type = "string" }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        Handler = async json =>
        {
            var args = Args.Parse(json);
            var viewId = args.Str("viewId");
            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var doc = session.As<UIApplication>().ActiveUIDocument?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    View? view = string.IsNullOrWhiteSpace(viewId)
                        ? doc.ActiveView
                        : doc.GetElement(RevitParse.ParseEid(viewId)) as View;
                    if (view is not ViewPlan)
                        return "path_of_travel requires a floor plan view. Pass viewId of a ViewPlan.";

                    var starts = args.Points("starts");
                    var ends = args.Points("ends");
                    if (starts.Count == 0 && args.HasNumber("startX") && args.HasNumber("startY"))
                        starts.Add(new XYZ(Metric.MmToFt(args.Dbl("startX", 0)), Metric.MmToFt(args.Dbl("startY", 0)), 0));
                    if (ends.Count == 0 && args.HasNumber("endX") && args.HasNumber("endY"))
                        ends.Add(new XYZ(Metric.MmToFt(args.Dbl("endX", 0)), Metric.MmToFt(args.Dbl("endY", 0)), 0));
                    if (starts.Count == 0 || ends.Count == 0) return "Pass startX/startY/endX/endY or starts/ends arrays.";
                    if (starts.Count != ends.Count) return "starts and ends must have the same count.";
                    if (starts.Count > 5) return "Max 5 path pairs per call.";

                    using var tx = new Transaction(doc, "LoadingAI for Revit: path of travel");
                    tx.Start();
                    var paths = PathOfTravel.CreateMultiple(view, starts, ends, out IList<PathOfTravelCalculationStatus> statuses);
                    tx.Commit();
                    var ok = paths.Count(p => p != null);
                    var fail = string.Join(", ", statuses.Select((s, i) => paths[i] == null ? $"#{i}:{s}" : null).Where(s => s != null));
                    var ids = string.Join(", ", paths.Where(p => p != null).Select(p => p!.Id.ToString()));
                    return $"Created {ok}/{starts.Count} path(s) in {view.Name}: {ids}" + (string.IsNullOrEmpty(fail) ? "" : $"\nFailed: {fail}");
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    private static int AddScheduleFields(ViewSchedule schedule, List<string> wanted)
    {
        var added = 0;
        foreach (var sf in schedule.Definition.GetSchedulableFields())
        {
            if (sf.FieldType != ScheduleFieldType.Instance && sf.FieldType != ScheduleFieldType.ElementType)
                continue;
            if (sf.ParameterId.Value == (long)BuiltInParameter.ALL_MODEL_MARK)
                continue;
            var fname = sf.GetName(schedule.Document);
            if (wanted.Count > 0)
            {
                if (!wanted.Any(w => fname.Equals(w, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            else if (added >= 8) break;
            schedule.Definition.AddField(sf);
            added++;
        }
        return added;
    }

    private static ViewSchedule? FindSchedule(Document doc, string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return doc.GetElement(RevitParse.ParseEid(id)) as ViewSchedule;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static Level? ResolveLevel(Document doc, string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return doc.GetElement(RevitParse.ParseEid(id)) as Level;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (!string.IsNullOrWhiteSpace(name))
            return levels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return levels.OrderBy(l => l.Elevation).FirstOrDefault();
    }

    private static ElementId? FindViewFamily(Document doc, ViewFamily family)
        => new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family)?.Id;

    private static BoundingBoxXYZ SectionBox(XYZ a, XYZ b, double height, double depth)
    {
        var along = b - a;
        if (along.GetLength() < 1e-6) throw new InvalidOperationException("Section line is too short.");
        var viewDir = along.Normalize();
        var up = XYZ.BasisZ;
        var right = up.CrossProduct(viewDir);
        if (right.GetLength() < 1e-6)
        {
            up = XYZ.BasisY;
            right = up.CrossProduct(viewDir);
        }
        right = right.Normalize();
        up = viewDir.CrossProduct(right).Normalize();
        var t = Transform.Identity;
        t.Origin = (a + b) / 2;
        t.set_Basis(0, right);
        t.set_Basis(1, up);
        t.set_Basis(2, viewDir);
        var halfW = Math.Max(along.GetLength() / 2, 1);
        var halfH = Math.Max(height / 2, 1);
        var halfD = Math.Max(depth / 2, 0.5);
        return new BoundingBoxXYZ
        {
            Enabled = true,
            Min = new XYZ(-halfW, -halfH, -halfD),
            Max = new XYZ(halfW, halfH, halfD),
            Transform = t
        };
    }

    private readonly struct Args
    {
        private readonly JsonElement? _root;
        private Args(JsonElement? root) => _root = root;

        public static Args Parse(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return new Args(null);
                using var doc = JsonDocument.Parse(json);
                return new Args(doc.RootElement.Clone());
            }
            catch { return new Args(null); }
        }

        public string? Str(string name)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        public int Int(string name, int fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
            return fallback;
        }

        public double Dbl(string name, double fallback)
        {
            if (_root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
            return fallback;
        }

        public bool HasNumber(string name)
            => _root is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number;

        public List<string> Strings(string name)
        {
            var list = new List<string>();
            if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            return list;
        }

        public List<XYZ> Points(string name)
        {
            var list = new List<XYZ>();
            if (_root is not { } r || !r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var nums = item.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetDouble()).ToList();
                if (nums.Count >= 2) list.Add(new XYZ(Metric.MmToFt(nums[0]), Metric.MmToFt(nums[1]), nums.Count > 2 ? Metric.MmToFt(nums[2]) : 0));
            }
            return list;
        }
    }
}
