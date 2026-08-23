using System.Text.Json;
using RevitAiPoc.Tools;

namespace RevitAiPoc.Llm;

public sealed class AgentLoop
{
    private readonly OpenAiCompatibleClient _client;
    private readonly ToolRegistry _registry;
    private readonly List<ChatMessage> _history = new();

    public const string SystemPrompt =
        "You are a Revit BIM assistant. Use tools to read or change the active model. " +
        "Available capabilities: " +
        "query: get_model_summary, get_wall_metrics (pyRevit: Wall), get_selection_summary, get_active_view_info, get_visible_elements_in_view, find_elements_by_filter, get_element_details, get_warnings, get_levels_and_grids, get_quantities, get_document_info, get_worksets (pyRevit), get_phases, get_design_options, get_sheets_views (DiRoots), get_family_types (Dynamo archi-lab), get_materials, get_room_area (Dynamo), get_clash_by_bb (Navis), get_model_health_score, get_worksets, sync_info; " +
        "view/sheet-doc: isolate_in_view, hide_in_view, reset_temporary_view, override_color_in_view, color_by_construction_schedule, create_sheet (pyRevit Create Sheets), place_view_on_sheet, duplicate_view_ex, set_view_template (DiRoots), set_crop_box, create_dimension, tag_all, export_pdf, export_dwg (pyRevit); " +
        "param/excel: set_parameter_by_id, set_comments_on_selection, batch_set_parameter, rename_elements, apply_construction_dates, export_params_to_excel (DiRoots/pyRevit), import_excel_to_params, copy_params, replace_text, numbering_auto (pyRevit); " +
        "geometry/create: move_elements, delete_elements, create_wall, create_family_instance, duplicate_element, create_floor_by_outline (Dynamo Floor.ByOutline), create_roof, create_grid_level (Dynamo), create_family_instance_array, rotate_elements, mirror_elements, split_wall, join_geometry; " +
        "worksharing/health: reload_links, workset_assign (BIMOne), sync_info, purge_unused (pyRevit Purge), audit_warnings_fix; " +
        "ifc/cost: export_ifc, quantity_takeoff_by_category (Dynamo QTO). " +
        "For 'isolate walls' use isolate_in_view category=Walls; for 'what is in active view' use get_active_view_info; for 'find/count' use find_elements_by_filter; for 'color' use override_color_in_view; for 'color by construction date / bottom-up schedule' ALWAYS use color_by_construction_schedule (ONE call); for 'set construction date / bottom-up dates' use apply_construction_dates (ONE call); for 'wall area' use quantity_takeoff_by_category category=Walls or get_quantities; for 'sheets/views' use get_sheets_views; for 'excel' use export_params_to_excel/import_excel_to_params; for 'create sheet/floor/roof' use create_sheet/create_floor_by_outline/create_roof. " +
        "If a tool returns an error, tell the user what to do (open a model, select elements). " +
        "Do not claim you changed the model unless a write tool succeeded. Be concise. Always confirm destructive actions via tool's own TaskDialog. You have up to 64 tool rounds, but prefer composite tools; user can press Stop to cancel at any time.";

    public AgentLoop(OpenAiCompatibleClient client, ToolRegistry registry)
    {
        _client = client;
        _registry = registry;
        _history.Add(new ChatMessage { Role = "system", Content = SystemPrompt });
    }

    public IReadOnlyList<ChatMessage> History => _history;

    public void ClearHistory()
    {
        _history.Clear();
        _history.Add(new ChatMessage { Role = "system", Content = SystemPrompt });
    }

    public async Task<string> RunAsync(
        ProviderConfig provider,
        string userText,
        Func<string, string, Task<string>> toolExecutor,
        Action<string> onTrace,
        CancellationToken ct = default)
        => await RunAsync(provider, userText, toolExecutor, onTrace, null, ct);

    public async Task<string> RunAsync(
        ProviderConfig provider,
        string userText,
        Func<string, string, Task<string>> toolExecutor,
        Action<string> onTrace,
        Action<string>? onDelta,
        CancellationToken ct = default)
    {
        _history.Add(new ChatMessage { Role = "user", Content = userText });
        try { HistoryStore.Append("user", userText); HistoryStore.Trim(); } catch { }

        var tools = _registry.GetToolDefinitions();
        // Mitigate provider tool limit (many OpenAI compatibles cap at ~32 tools). Keep payload <=30.
        if (tools.Count > 30)
        {
            var lower = userText.ToLowerInvariant();
            var core = new HashSet<string>{"get_model_summary","find_elements_by_filter","get_wall_metrics","get_active_view_info","get_element_details","get_quantities","get_levels_and_grids","quantity_takeoff_by_category"};
            var filtered = tools.Where(t=>{
                if (core.Contains(t.Function.Name)) return true;
                var n=t.Function.Name.ToLowerInvariant();
                var d=(t.Function.Description??"").ToLowerInvariant();
                // keep if name token appears in user text
                if (lower.Contains(n.Split('_')[0])) return true;
                // keep if description keyword overlaps
                foreach(var kw in new[]{"wall","door","window","floor","roof","level","grid","sheet","view","param","excel","sheet","workset","phase","material","room","clash","health","export","import","copy","replace","number","create","rotate","mirror","join","purge","ifc","takeoff","color","construction","schedule","health"})
                    if (lower.Contains(kw) && (n.Contains(kw) || d.Contains(kw))) return true;
                return false;
            }).ToList();
            if (filtered.Count >= 10 && filtered.Count <= 30) tools = filtered;
            else tools = tools.Take(30).ToList();
        }

        // Keep system prompt in sync with the actual tool payload so the model never
        // references a tool that was filtered out (prevents "Unknown tool" loops).
        var enabledList = string.Join(", ", tools.Select(t => t.Function.Name).OrderBy(n => n));
        if (_history.Count > 0 && _history[0].Role == "system")
            _history[0].Content = SystemPrompt + "\nEnabled tools for this request: " + enabledList;

        // Increased from 8 to 64 to support bottom-up batch tasks (e.g., coloring 18 levels needs ~36 tool calls).
        // User stop via CancellationToken remains the hard limit — this is effectively unlimited for practical tasks.
        const int MaxRounds = 64;
        for (int round = 0; round < MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            ChatCompletionResponse resp;
            try
            {
                resp = await _client.ChatAsync(provider, new List<ChatMessage>(_history), tools, onDelta, ct);
            }
            catch (OperationCanceledException)
            {
                var cancelMsg = "Cancelled.";
                _history.Add(new ChatMessage { Role = "assistant", Content = cancelMsg });
                onTrace(cancelMsg);
                return cancelMsg;
            }
            catch (Exception ex)
            {
                var msg = $"LLM error: {ex.Message}";
                _history.Add(new ChatMessage { Role = "assistant", Content = msg });
                try { HistoryStore.Append("assistant", msg); } catch { }
                onTrace(msg);
                return msg;
            }

            var choice = resp.Choices?.FirstOrDefault();
            var msgOut = choice?.Message;
            if (msgOut == null)
            {
                var fallback = "No response from model.";
                _history.Add(new ChatMessage { Role = "assistant", Content = fallback });
                return fallback;
            }

            // Add assistant message to history
            _history.Add(new ChatMessage
            {
                Role = msgOut.Role,
                Content = msgOut.Content,
                ToolCalls = msgOut.ToolCalls
            });

            // If no tool calls, we are done
            if (msgOut.ToolCalls == null || msgOut.ToolCalls.Count == 0)
            {
                var text = GetContentString(msgOut.Content);
                if (string.IsNullOrWhiteSpace(text) && choice?.FinishReason == "tool_calls")
                    text = "";
                onTrace(text);
                return text;
            }

            // Execute each tool call
            foreach (var tc in msgOut.ToolCalls)
            {
                onTrace($"Tool {tc.Function.Name}: {tc.Function.Arguments}");
                string toolResult;
                try
                {
                    toolResult = await toolExecutor(tc.Function.Name, tc.Function.Arguments);
                }
                catch (Exception ex)
                {
                    toolResult = $"Tool error: {ex.Message}";
                }

                onTrace($"Tool {tc.Function.Name} result: {Truncate(toolResult, 400)}");

                _history.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = tc.Id,
                    Name = tc.Function.Name,
                    Content = toolResult
                });
            }
            // loop to get final answer
        }

        var abort = "Max tool rounds (64) reached. I stopped to avoid an infinite loop. Say 'continue' to keep going, or press Stop. Partial work may have been applied.";
        _history.Add(new ChatMessage { Role = "assistant", Content = abort });
        return abort;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static string GetContentString(object? c)
    {
        if (c == null) return "";
        if (c is string s) return s;
        if (c is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString() ?? "";
            if (je.ValueKind == JsonValueKind.Null) return "";
            return je.GetRawText();
        }
        return c.ToString() ?? "";
    }
}
