using System.IO;
using System.Reflection;
using System.Text.Json;
using RevitAiPoc.Revit;
using RevitAiPoc.Tools;

namespace RevitAiPoc.Plugins.Core;

public sealed class DiscoveredPlugin
{
    public PluginMeta Meta { get; init; } = new();
    public List<ToolEntry> Tools { get; init; } = new();
}

public static class PluginLoader
{
    public static string RuntimePluginsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitAiPoc", "Plugins");

    public static string DevPluginsDir =>
        Path.Combine(Path.GetDirectoryName(typeof(PluginLoader).Assembly.Location)!, "Plugins", "dev");

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitAiPoc", "plugins.log");

    public static List<DiscoveredPlugin> Discover(RevitBridge bridge, QueryCache cache)
    {
        var result = new List<DiscoveredPlugin>();
        // 1) Core built-in
        try
        {
            var core = new CorePlugin();
            var tools = core.CreateTools(bridge, cache).ToList();
            var meta = new PluginMeta
            {
                Id = core.Id,
                Name = core.Name,
                Version = core.Version,
                License = "MIT",
                Author = "Core",
                Verified = true,
                FolderPath = "builtin",
                Tools = tools.Select(t => new PluginToolMeta
                {
                    Name = t.Definition.Function.Name,
                    Tier = t.Tier.ToString(),
                    Description = t.Definition.Function.Description ?? "",
                    EnabledByDefault = t.Tier != ToolTier.L3
                }).ToList()
            };
            result.Add(new DiscoveredPlugin { Meta = meta, Tools = tools });
        }
        catch (Exception ex) { Log($"core failed: {ex}"); }

        // 2) Scan runtime plugins dir + dev dir
        foreach (var dir in new[] { RuntimePluginsDir, DevPluginsDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pluginFolder in Directory.GetDirectories(dir))
            {
                try { var dp = LoadOne(pluginFolder, bridge, cache); if (dp != null) result.Add(dp); }
                catch (Exception ex) { Log($"{pluginFolder} failed: {ex.Message}"); }
            }
            // also handle loose manifest files directly under Plugins/
            foreach (var manifest in Directory.GetFiles(dir, "plugin.json"))
            {
                try
                {
                    var folder = Path.GetDirectoryName(manifest)!;
                    var dp = LoadOne(folder, bridge, cache);
                    if (dp != null && !result.Any(r => r.Meta.Id == dp.Meta.Id)) result.Add(dp);
                }
                catch { }
            }
        }

        // Deduplicate by tool name: core wins
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in result) foreach (var t in p.Tools) seen.Add(t.Definition.Function.Name);

        return result;
    }

    private static DiscoveredPlugin? LoadOne(string folder, RevitBridge bridge, QueryCache cache)
    {
        var manifestPath = Path.Combine(folder, "plugin.json");
        PluginMeta? meta = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                meta = JsonSerializer.Deserialize<PluginMeta>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex) { Log($"bad manifest {manifestPath}: {ex.Message}"); }
        }
        // Find dll
        string? dll = null;
        if (meta != null && !string.IsNullOrWhiteSpace(meta.EntryAssembly))
            dll = Path.Combine(folder, meta.EntryAssembly);
        if (dll == null || !File.Exists(dll))
            dll = Directory.GetFiles(folder, "*.dll").FirstOrDefault();
        if (dll == null || !File.Exists(dll)) return null;

        meta ??= new PluginMeta { Id = Path.GetFileName(folder), Name = Path.GetFileName(folder), Verified = false, FolderPath = folder };
        meta.FolderPath = folder;
        if (string.IsNullOrWhiteSpace(meta.EntryAssembly)) meta.EntryAssembly = Path.GetFileName(dll);

        var asm = Assembly.LoadFrom(dll);
        var pluginTypes = asm.GetTypes().Where(t => typeof(IRevitToolPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).ToList();
        var tools = new List<ToolEntry>();
        foreach (var pt in pluginTypes)
        {
            try
            {
                var inst = (IRevitToolPlugin)Activator.CreateInstance(pt)!;
                var ts = inst.CreateTools(bridge, cache).ToList();
                tools.AddRange(ts);
                // If manifest tools empty, populate from reflection
                if (meta.Tools.Count == 0)
                {
                    foreach (var te in ts) meta.Tools.Add(new PluginToolMeta { Name = te.Definition.Function.Name, Tier = te.Tier.ToString(), Description = te.Definition.Function.Description ?? "", EnabledByDefault = te.Tier != ToolTier.L3 });
                }
                // override meta with plugin instance id/name if manifest missing
                if (string.IsNullOrWhiteSpace(meta.Id) || meta.Id == Path.GetFileName(folder)) { meta.Id = inst.Id; meta.Name = inst.Name; meta.Version = inst.Version; }
            }
            catch (Exception ex) { Log($"plugin {folder} type {pt.Name} failed: {ex.Message}"); }
        }
        if (tools.Count == 0) return null;
        return new DiscoveredPlugin { Meta = meta, Tools = tools };
    }

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
