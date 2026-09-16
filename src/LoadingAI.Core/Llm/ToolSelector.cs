namespace LoadingAI.Llm;

public static class ToolSelector
{
    public const int DefaultMax = 24;

    private static readonly HashSet<string> AlwaysKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_model_summary", "get_selection_summary", "get_element_details"
    };

    private static readonly HashSet<string> Preferred = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_wall_metrics", "get_quantities", "get_levels_and_grids",
        "quantity_takeoff_by_category", "get_warnings", "create_wall",
        "set_comments_on_selection", "get_rooms", "get_sheets_views",
        "color_by_parameter"
    };

    public static List<ToolDefinition> Select(IReadOnlyList<ToolDefinition> tools, string userText, int max = DefaultMax)
        => Select(tools, userText, null, max);

    public static List<ToolDefinition> Select(
        IEnumerable<LoadingAI.Tools.ToolEntry> entries,
        string userText,
        int max = DefaultMax)
    {
        var list = entries as IList<LoadingAI.Tools.ToolEntry> ?? entries.ToList();
        var defs = list.Select(e => e.Definition).ToList();
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in list)
        {
            var name = e.Definition.Function.Name ?? "";
            if (name.Length == 0 || e.Tags.Count == 0 || map.ContainsKey(name)) continue;
            map[name] = e.Tags;
        }
        return Select(defs, userText, map, max);
    }

    public static List<ToolDefinition> Select(
        IReadOnlyList<ToolDefinition> tools,
        string userText,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? tagMap,
        int max = DefaultMax)
    {
        if (tools.Count <= max) return tools.ToList();
        var lower = (userText ?? "").ToLowerInvariant();
        var active = ActiveTags(lower);
        var guaranteed = tools.Where(t => AlwaysKeep.Contains(t.Function.Name ?? "")).ToList();
        var remainingSlots = Math.Max(0, max - guaranteed.Count);
        var fill = tools
            .Where(t => !AlwaysKeep.Contains(t.Function.Name ?? ""))
            .Select(t => (Tool: t, Score: Score(t, lower, TagsFor(t.Function.Name, tagMap), active)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.Function.Name, StringComparer.OrdinalIgnoreCase)
            .Take(remainingSlots)
            .Select(x => x.Tool);
        return guaranteed.Concat(fill).ToList();
    }

    public static int Score(ToolDefinition t, string lowerUser)
        => Score(t, lowerUser, InferTags(t.Function.Name), ActiveTags(lowerUser));

    public static IReadOnlyList<string> InferTags(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Length == 0) return Array.Empty<string>();
        var tags = new List<string>();
        if (n.StartsWith("get_", StringComparison.Ordinal)
            || n.StartsWith("find_", StringComparison.Ordinal)
            || n.StartsWith("audit_", StringComparison.Ordinal)
            || n == "sync_info"
            || n.Contains("takeoff")
            || n == "hello")
            tags.Add("query");
        if (n.Contains("view") || n.Contains("color") || n.Contains("isolate") || n.Contains("hide")
            || n.Contains("crop") || n.Contains("template") || n.Contains("sheet") || n.Contains("schedule")
            || n.Contains("tag_all") || n.Contains("dimension") || n.Contains("path_of_travel"))
            tags.Add("view");
        if (n.StartsWith("export_", StringComparison.Ordinal) || n == "purge_unused")
            tags.Add("export");
        if (n.Contains("split_beams") || n.Contains("split_floors") || n.Contains("formwork") || n.Contains("structure"))
            tags.Add("structure");
        if (n.StartsWith("set_", StringComparison.Ordinal) || n.StartsWith("batch_", StringComparison.Ordinal)
            || n.StartsWith("create_", StringComparison.Ordinal) || n.StartsWith("move_", StringComparison.Ordinal)
            || n.StartsWith("delete_", StringComparison.Ordinal) || n.StartsWith("save_", StringComparison.Ordinal)
            || n.StartsWith("rename_", StringComparison.Ordinal) || n.StartsWith("apply_", StringComparison.Ordinal)
            || n.StartsWith("duplicate_", StringComparison.Ordinal) || n.StartsWith("rotate_", StringComparison.Ordinal)
            || n.StartsWith("mirror_", StringComparison.Ordinal) || n.StartsWith("split_", StringComparison.Ordinal)
            || n.StartsWith("join_", StringComparison.Ordinal) || n.StartsWith("import_", StringComparison.Ordinal)
            || n.StartsWith("copy_", StringComparison.Ordinal) || n.StartsWith("replace_", StringComparison.Ordinal)
            || n.StartsWith("numbering_", StringComparison.Ordinal) || n.StartsWith("load_", StringComparison.Ordinal)
            || n.StartsWith("change_", StringComparison.Ordinal) || n.StartsWith("workset_", StringComparison.Ordinal)
            || n.StartsWith("reload_", StringComparison.Ordinal) || n.StartsWith("override_", StringComparison.Ordinal)
            || n.StartsWith("color_by", StringComparison.Ordinal) || n.StartsWith("isolate_", StringComparison.Ordinal)
            || n.StartsWith("hide_", StringComparison.Ordinal)
            || n == "purge_unused" || n == "cleanup_formwork" || n == "place_view_on_sheet"
            || n == "tag_all" || n == "path_of_travel" || n == "formwork_setup"
            || n == "reset_temporary_view")
            tags.Add("write");
        if (n.Contains("excel") || n.Contains("workset") || n.Contains("clash")
            || n.Contains("numbering") || n.Contains("replace_text") || n.Contains("copy_params")
            || n.Contains("join_geometry") || n == "split_wall" || n.Contains("design_option")
            || n.Contains("family_instance_array"))
            tags.Add("compat");
        return tags.Count == 0 ? Array.Empty<string>() : tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int Score(ToolDefinition t, string lowerUser, IReadOnlyList<string> tags, HashSet<string> activeTags)
    {
        var n = (t.Function.Name ?? "").ToLowerInvariant();
        var d = (t.Function.Description ?? "").ToLowerInvariant();
        int score = 0;
        if (AlwaysKeep.Contains(n)) score += 100;
        if (Preferred.Contains(n)) score += 40;
        if (activeTags.Count > 0 && tags.Count > 0 && tags.Any(tag => activeTags.Contains(tag)))
            score += 50;
        if (!string.IsNullOrEmpty(lowerUser) && lowerUser.Contains(n)) score += 80;
        var tokens = n.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tok in tokens)
        {
            if (tok.Length < 3) continue;
            if (lowerUser.Contains(tok)) score += 25;
            if (d.Contains(tok) && lowerUser.Contains(tok)) score += 5;
        }
        foreach (var kw in Keywords)
        {
            if (!lowerUser.Contains(kw)) continue;
            if (n.Contains(kw)) score += 15;
            else if (d.Contains(kw)) score += 8;
        }
        return score;
    }

    private static IReadOnlyList<string> TagsFor(string? name, IReadOnlyDictionary<string, IReadOnlyList<string>>? tagMap)
    {
        if (!string.IsNullOrEmpty(name) && tagMap != null && tagMap.TryGetValue(name!, out var declared) && declared.Count > 0)
            return declared;
        return InferTags(name);
    }

    private static HashSet<string> ActiveTags(string lower)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ContainsAny(lower, "summary", "detail", "rooms", "list ", "how many", "what ", "count", "warning", "quantity", "takeoff", "selection"))
            tags.Add("query");
        if (ContainsAny(lower, "view", "isolate", "hide", "color", "sheet", "schedule", "crop", "template", "3d", "section", "tag "))
            tags.Add("view");
        if (ContainsAny(lower, "delete", "move", "create", "save", "rename", "rotate", "mirror", "join", "import", "set ", "load "))
            tags.Add("write");
        if (ContainsAny(lower, "export", "pdf", "dwg", "ifc", "purge", "excel", "csv"))
            tags.Add("export");
        if (ContainsAny(lower, "split", "beam", "column", "slab", "formwork", "structure", "panel"))
            tags.Add("structure");
        if (ContainsAny(lower, "excel", "workset", "clash", "compat"))
            tags.Add("compat");
        return tags;
    }

    private static bool ContainsAny(string lower, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (lower.Contains(n)) return true;
        }
        return false;
    }

    private static readonly string[] Keywords =
    {
        "wall","door","window","floor","roof","level","grid","sheet","view","param","excel",
        "workset","phase","material","room","clash","health","export","import","copy","replace",
        "number","create","rotate","mirror","join","purge","ifc","takeoff","color","construction",
        "schedule","tag","dimension","crop","template","comment","delete","move","hide","isolate",
        "link","rvt","revit","cad","dwg","pdf","family","type","instance","selection","element",
        "category","visible","active","temporary","override","reset","save","warning",
        "formwork","split","beam","column","slab","opening","rebar","panel","structure"
    };
}
