namespace LoadingAI.Llm;

public static class ToolSelector
{
    public const int DefaultMax = 20;

    private static readonly HashSet<string> AlwaysKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_model_summary", "find_elements_by_filter", "get_wall_metrics",
        "get_active_view_info", "get_element_details", "get_quantities",
        "get_levels_and_grids", "quantity_takeoff_by_category",
        "hide_in_view", "isolate_in_view", "reset_temporary_view",
        "get_visible_elements_in_view", "get_selection_summary",
        "get_warnings", "delete_elements", "move_elements", "save_document",
        "create_wall", "set_comments_on_selection",
        "get_rooms", "get_sheets_views", "color_by_parameter",
        "create_openings_from_clash", "cut_hosts_by_openings"
    };

    private static readonly (string Needle, string Token)[] Aliases =
    {
        ("刪", "delete"), ("删", "delete"), ("移除", "delete"),
        ("移動", "move"), ("移动", "move"), ("移 ", "move"),
        ("存檔", "save"), ("存档", "save"),
        ("建牆", "wall"), ("建墙", "wall"),
        ("警告", "warning"),
        ("隔離", "isolate"), ("隔离", "isolate"),
        ("房間", "room"), ("房间", "room"),
        ("圖紙", "sheet"), ("图纸", "sheet"),
        ("上色", "color"), ("顏色", "color"), ("颜色", "color"),
        ("開口", "opening"), ("开洞", "opening"), ("開洞", "opening"),
        ("切洞", "cut"), ("切牆", "cut"), ("切墙", "cut"),
    };

    public static List<ToolDefinition> Select(IReadOnlyList<ToolDefinition> tools, string userText, int max = DefaultMax)
    {
        if (tools.Count <= max) return tools.ToList();
        var lower = (userText ?? "").ToLowerInvariant();
        var ranked = tools
            .Select(t => (Tool: t, Score: Score(t, lower)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.Function.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Tool)
            .ToList();
        return ranked;
    }

    public static int Score(ToolDefinition t, string lowerUser)
    {
        var n = (t.Function.Name ?? "").ToLowerInvariant();
        var d = (t.Function.Description ?? "").ToLowerInvariant();
        int score = 0;
        if (AlwaysKeep.Contains(n)) score += 100;
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
        foreach (var (needle, token) in Aliases)
        {
            if (!lowerUser.Contains(needle.ToLowerInvariant()) && !lowerUser.Contains(needle)) continue;
            if (n.Contains(token) || d.Contains(token)) score += 40;
        }
        // "移 300" / "往 Y 移" — single 移 is too short for Keywords
        if ((lowerUser.Contains("移") || lowerUser.Contains("move")) && (n.Contains("move") || d.Contains("move")))
            score += 40;
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
        "opening","cut","clash","void"
    };
}
