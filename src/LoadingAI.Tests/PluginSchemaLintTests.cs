using System.Text.Json;
using LoadingAI.Plugins;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Lints every shipped tool schema and its host-owned form. Strict providers reject an array with no
/// <c>items</c>; a <c>required</c> name that is not a property cannot be satisfied; and a form field
/// whose kind cannot produce the schema's type (or whose options the schema does not list) fails
/// validation after the user has filled it in.
/// </summary>
public class PluginSchemaLintTests
{
    private static IEnumerable<IRevitToolPlugin> Plugins() => new IRevitToolPlugin[]
    {
        new LoadingAI.Plugins.CoreTools.CoreToolsPlugin(),
        new LoadingAI.Plugins.ExportTools.ExportToolsPlugin(),
        new LoadingAI.Plugins.ViewTools.ViewToolsPlugin(),
        new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin(),
        new LoadingAI.Plugins.StructureCuts.StructureCutsPlugin(),
        new LoadingAI.Plugins.QsFormwork.QsFormworkPlugin(),
        new LoadingAI.Plugins.OpeningTools.OpeningToolsPlugin()
    };

    [Fact]
    public void EveryShippedSchemaIsWellFormed_AndItsFormMatchesIt()
    {
        var context = new FakePluginContext(new FakeRevitBridge { HasActiveDocument = true });
        var problems = new List<string>();

        foreach (var plugin in Plugins())
        foreach (var tool in plugin.CreateTools(context))
        {
            var name = tool.Definition.Function.Name;
            var schema = JsonSerializer.SerializeToElement(tool.Definition.Function.Parameters);
            CheckSchema(name, "arguments", schema, problems);
            CheckForm(name, tool, schema, problems);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static void CheckSchema(string tool, string path, JsonElement schema, List<string> problems)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        var types = Types(schema);

        if (types.Contains("array") && !schema.TryGetProperty("items", out _))
            problems.Add($"{tool}: {path} is an array with no items");
        if (schema.TryGetProperty("items", out var items))
            CheckSchema(tool, path + "[]", items, problems);

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                foreach (var r in required.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.String && !props.TryGetProperty(r.GetString()!, out _))
                        problems.Add($"{tool}: required '{r.GetString()}' is not a property");
            foreach (var p in props.EnumerateObject())
                CheckSchema(tool, p.Name, p.Value, problems);
        }
    }

    private static void CheckForm(string tool, ToolEntry entry, JsonElement schema, List<string> problems)
    {
        if (entry.Presets.Count == 0) return;
        schema.TryGetProperty("properties", out var props);

        foreach (var field in entry.Presets)
        {
            if (props.ValueKind != JsonValueKind.Object || !props.TryGetProperty(field.Id, out var prop))
            {
                problems.Add($"{tool}: form field '{field.Id}' is not a schema property");
                continue;
            }

            var types = Types(prop);
            var ok = field.Kind switch
            {
                PresetFieldKind.Boolean => types.Contains("boolean"),
                PresetFieldKind.Number => types.Contains("number") || types.Contains("integer"),
                PresetFieldKind.String => types.Contains("string"),
                PresetFieldKind.Select => types.Contains("string") || types.Contains("array"),
                PresetFieldKind.PickElements => types.Contains("array"),
                PresetFieldKind.PickBox => types.Contains("array") || types.Contains("object"),
                _ => true
            };
            if (!ok) problems.Add($"{tool}: form field '{field.Id}' is {field.Kind} but the schema type is {string.Join("|", types)}");

            if (field.Kind == PresetFieldKind.Select && field.Options is { Count: > 0 })
            {
                var enumHost = types.Contains("array") && prop.TryGetProperty("items", out var items) ? items : prop;
                var listed = enumHost.ValueKind == JsonValueKind.Object
                             && enumHost.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Select(v => v.GetString() ?? "").OrderBy(s => s, StringComparer.Ordinal).ToList()
                    : new List<string>();
                var offered = field.Options.OrderBy(s => s, StringComparer.Ordinal).ToList();
                if (!listed.SequenceEqual(offered))
                    problems.Add($"{tool}: form field '{field.Id}' offers [{string.Join(",", offered)}] but the schema enum is [{string.Join(",", listed)}]");
            }
        }
    }

    private static HashSet<string> Types(JsonElement schema)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type)) return set;
        if (type.ValueKind == JsonValueKind.String) set.Add(type.GetString() ?? "");
        else if (type.ValueKind == JsonValueKind.Array)
            foreach (var t in type.EnumerateArray()) if (t.ValueKind == JsonValueKind.String) set.Add(t.GetString() ?? "");
        return set;
    }
}
