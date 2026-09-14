namespace LoadingAI.Llm;

public static class ToolSelector
{
    public const int DefaultMax = 24;

    private static readonly HashSet<string> AlwaysKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_model_summary", "find_elements_by_filter", "get_element_details",
        "get_selection_summary", "get_active_view_info", "get_visible_elements_in_view",
        "hide_in_view", "isolate_in_view", "reset_temporary_view",
        "delete_elements", "move_elements", "save_document"
    };

    private static readonly HashSet<string> Preferred = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_wall_metrics", "get_quantities", "get_levels_and_grids",
        "quantity_takeoff_by_category", "get_warnings", "create_wall",
        "set_comments_on_selection", "get_rooms", "get_sheets_views",
        "color_by_parameter"
    };

    public static List<ToolDefinition> Select(IReadOnlyList<ToolDefinition> tools, string userText, int max = DefaultMax)
    {
        if (tools.Count <= max) return tools.ToList();
        var lower = (userText ?? "").ToLowerInvariant();
        var guaranteed = tools.Where(t => AlwaysKeep.Contains(t.Function.Name ?? "")).ToList();
        var remainingSlots = Math.Max(0, max - guaranteed.Count);
        var fill = tools
            .Where(t => !AlwaysKeep.Contains(t.Function.Name ?? ""))
            .Select(t => (Tool: t, Score: Score(t, lower)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.Function.Name, StringComparer.OrdinalIgnoreCase)
            .Take(remainingSlots)
            .Select(x => x.Tool);
        return guaranteed.Concat(fill).ToList();
    }

    public static int Score(ToolDefinition t, string lowerUser)
    {
        var n = (t.Function.Name ?? "").ToLowerInvariant();
        var d = (t.Function.Description ?? "").ToLowerInvariant();
        int score = 0;
        if (AlwaysKeep.Contains(n)) score += 100;
        if (Preferred.Contains(n)) score += 40;
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

    private static readonly string[] Keywords =
    {
        "wall","door","window","floor","roof","level","grid","sheet","view","param","excel",
        "workset","phase","material","room","clash","health","export","import","copy","replace",
        "number","create","rotate","mirror","join","purge","ifc","takeoff","color","construction",
        "schedule","tag","dimension","crop","template","comment","delete","move","hide","isolate",
        "link","rvt","revit","cad","dwg","pdf","family","type","instance","selection","element",
        "category","visible","active","temporary","override","reset","save","warning",
        "formwork","split","beam","column","slab","opening","rebar","panel","structure",
        "storey","story"
    };
}
