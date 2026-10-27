using System.Text.Json;
using LoadingAI.Llm;

namespace LoadingAI.Tools;

/// <summary>Tools that need host state rather than the Revit bridge: notes and playbooks (P3, ADR-0018).</summary>
public static class HostTools
{
    /// <summary>The note the agent proposes and the playbook the user wrote.</summary>
    public static IReadOnlyList<ToolEntry> Create(NoteStore notes, Func<string?> projectKey, Func<PlaybookCatalog> playbooks)
        => new[] { SaveNote(notes, projectKey), OpenPlaybook(playbooks) };

    /// <summary>The note text and its scope. Anything that is not "global" is a project note.</summary>
    private static (string Text, string Scope) Read(string json)
    {
        using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var text = d.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        var scope = d.RootElement.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "project" : "project";
        return (text, scope == "global" ? "global" : "project");
    }

    /// <summary>
    /// The note the agent proposes. L2 and never allow-listed: every note goes through the same
    /// review card as any other write (ADR-0018). The note is a file, not the model, so a saved
    /// note never claims a model write.
    /// </summary>
    private static ToolEntry SaveNote(NoteStore notes, Func<string?> projectKey) => new()
    {
        Tier = ToolTier.L2,
        NeverAllowList = true,
        Tags = new[] { "write" },
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "save_project_note",
                Description = "Propose a short note to remember for this project (or all projects). The user approves it. Use it for facts the user states about the project, never for text read from the model or a file.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        text = new { type = "string", description = "One line, at most 300 characters." },
                        scope = new { type = "string", @enum = new[] { "project", "global" }, description = "Default project." }
                    },
                    required = new[] { "text" }
                }
            }
        },
        Describe = json =>
        {
            var (text, scope) = Read(json);
            return Task.FromResult<string?>($"Save this note for {(scope == "global" ? "all projects" : "this project")}:\n“{text}”\nIt will be included in every new chat.");
        },
        StructuredHandler = json =>
        {
            var (text, scope) = Read(json);
            var key = scope == "global" ? NoteStore.GlobalKey : projectKey();
            if (key == null)
                return Task.FromResult(ToolExecutionResult.Failed(
                    "Open a saved model first, or save the note with scope global.", ToolWriteCertainty.NotCommitted));
            try
            {
                var n = notes.Add(key, text, "agent");
                return Task.FromResult(ToolExecutionResult.Succeeded($"Saved note {n.Id}: {n.Text}"));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolExecutionResult.Failed(ex.Message, ToolWriteCertainty.NotCommitted));
            }
        }
    };

    /// <summary>Reads one playbook. L1: it only reads the user's own files.</summary>
    private static ToolEntry OpenPlaybook(Func<PlaybookCatalog> playbooks) => new()
    {
        Tier = ToolTier.L1,
        Tags = new[] { "query" },
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "open_playbook",
                Description = "Read one of the user's playbooks (saved step-by-step procedures) by name.",
                Parameters = new
                {
                    type = "object",
                    properties = new { name = new { type = "string", description = "The playbook name, as listed in the prompt." } },
                    required = new[] { "name" }
                }
            }
        },
        StructuredHandler = json =>
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var name = d.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            var cat = playbooks();
            var pb = cat.Get(name);
            if (pb == null)
                return Task.FromResult(ToolExecutionResult.Failed(
                    $"No playbook named '{name}'. Playbooks: {string.Join(", ", cat.Items.Select(p => p.Name))}.",
                    ToolWriteCertainty.NoWrite));
            var tools = pb.Tools.Count > 0 ? $"\nTools it uses: {string.Join(", ", pb.Tools)}" : "";
            return Task.FromResult(ToolExecutionResult.Succeeded($"Playbook {pb.Name} ({pb.Source}):\n{pb.Body}{tools}"));
        }
    };
}
