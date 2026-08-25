using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.Core;

public sealed class DiscoveredPlugin
{
    public PluginMeta Meta { get; init; } = new();
    public List<ToolEntry> Tools { get; init; } = new();
}

public static class PluginLoader
{
#if !NETFRAMEWORK
    private static readonly List<PluginLoadContext> _contexts = new();
    private static readonly object _ctxLock = new();
#endif
    // Track live cooperative plugin instances for the reload gate.
    private static readonly List<IPluginLifecycle> _live = new();
    private static readonly object _lifeLock = new();

    public static string RuntimePluginsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "Plugins");

    public static string DevPluginsDir =>
        Path.Combine(Path.GetDirectoryName(typeof(PluginLoader).Assembly.Location)!, "Plugins", "dev");

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "plugins.log");

    public static List<DiscoveredPlugin> Discover(RevitBridge bridge, IPluginContextFactory factory)
    {
        UnloadContexts();
        var result = new List<DiscoveredPlugin>();
        try
        {
            var core = new CorePlugin();
            var coreCtx = factory.Create("builtin", requestFullAccess: true);
            var tools = ToolGuards.ApplyAll(core.CreateTools(coreCtx), bridge).ToList();
            JoinLifecycle(coreCtx);
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

        foreach (var dir in new[] { RuntimePluginsDir, DevPluginsDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pluginFolder in Directory.GetDirectories(dir))
            {
                try { var dp = LoadOne(pluginFolder, bridge, factory); if (dp != null) result.Add(dp); }
                catch (Exception ex) { Log($"{pluginFolder} failed: {ex.Message}"); }
            }
            foreach (var manifest in Directory.GetFiles(dir, "plugin.json"))
            {
                try
                {
                    var folder = Path.GetDirectoryName(manifest)!;
                    var dp = LoadOne(folder, bridge, factory);
                    if (dp != null && !result.Any(r => r.Meta.Id == dp.Meta.Id)) result.Add(dp);
                }
                catch { }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in result)
        {
            var kept = new List<ToolEntry>();
            foreach (var t in p.Tools)
            {
                var name = t.Definition.Function.Name;
                if (!seen.Add(name))
                {
                    Log($"skip duplicate tool '{name}' from {p.Meta.Id} (core wins)");
                    continue;
                }
                kept.Add(t);
            }
            p.Tools.Clear();
            p.Tools.AddRange(kept);
            p.Meta.Tools = p.Meta.Tools.Where(m => seen.Contains(m.Name) && p.Tools.Any(t => t.Definition.Function.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        return result;
    }

    /// <summary>
    /// Cooperative reload gate: waits for live cooperative plugins to report
    /// idle before their assemblies are discarded. On .NET Framework (Revit
    /// 2024) there is no unloadable ALC, so unloading is a no-op.
    /// </summary>
    private static void UnloadContexts()
    {
#if NETFRAMEWORK
        // No collectible AssemblyLoadContext on net48 — see plugin fallback.
        ClearLifecycle();
#else
        WaitForIdle();
        lock (_ctxLock)
        {
            foreach (var ctx in _contexts)
            {
                try { ctx.Unload(); } catch { }
            }
            _contexts.Clear();
        }
        ClearLifecycle();
        GC.Collect();
        GC.WaitForPendingFinalizers();
#endif
    }

    private static void JoinLifecycle(IPluginContext ctx)
    {
        if (ctx is IPluginLifecycle lc)
            lock (_lifeLock) _live.Add(lc);
    }

    private static void ClearLifecycle()
    {
        lock (_lifeLock) _live.Clear();
    }

    /// <summary>Waits (up to each plugin's MaxReloadWaitMs) for cooperative plugins to become idle.</summary>
    private static void WaitForIdle()
    {
        List<IPluginLifecycle> snap;
        lock (_lifeLock) snap = new List<IPluginLifecycle>(_live);
        foreach (var lc in snap)
        {
            var wait = lc.MaxReloadWaitMs > 0 ? lc.MaxReloadWaitMs : PluginLifecycleDefaults.MaxReloadWaitMs;
            var deadline = DateTime.UtcNow.AddMilliseconds(wait);
            while (lc.IsBusy && DateTime.UtcNow < deadline)
                Thread.Sleep(25);
            if (lc.IsBusy) Log("reload gate: plugin still busy; unloading anyway");
        }
    }

    private static DiscoveredPlugin? LoadOne(string folder, RevitBridge bridge, IPluginContextFactory factory)
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
        string? dll = null;
        if (meta != null && !string.IsNullOrWhiteSpace(meta.EntryAssembly))
            dll = Path.Combine(folder, meta.EntryAssembly);
        if (dll == null || !File.Exists(dll))
            dll = Directory.GetFiles(folder, "*.dll").FirstOrDefault();
        if (dll == null || !File.Exists(dll)) return null;

        meta ??= new PluginMeta { Id = Path.GetFileName(folder), Name = Path.GetFileName(folder), Verified = false, FolderPath = folder };
        meta.FolderPath = folder;
        meta.Verified = false;
        if (string.IsNullOrWhiteSpace(meta.EntryAssembly)) meta.EntryAssembly = Path.GetFileName(dll);

        Assembly asm;
#if NETFRAMEWORK
        // .NET Framework (Revit 2024): no collectible ALC — load directly.
        asm = Assembly.LoadFrom(dll);
#else
        try
        {
            var alc = new PluginLoadContext(dll);
            lock (_ctxLock) _contexts.Add(alc);
            asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dll));
        }
        catch (Exception ex)
        {
            Log($"ALC load failed {dll}: {ex.Message}; fallback LoadFrom");
            asm = Assembly.LoadFrom(dll);
        }
#endif

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            Log($"partial type load {dll}: {ex.Message}");
        }

        var pluginTypes = types.Where(t => typeof(IRevitToolPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).ToList();
        var tools = new List<ToolEntry>();
        foreach (var pt in pluginTypes)
        {
            try
            {
                var inst = (IRevitToolPlugin)Activator.CreateInstance(pt)!;
                var requestFull = typeof(IRevitFullAccess).IsAssignableFrom(pt);
                var ctx = factory.Create(folder, requestFull);
                var ts = ToolGuards.ApplyAll(inst.CreateTools(ctx), bridge).ToList();
                tools.AddRange(ts);
                if (meta.Tools.Count == 0)
                {
                    foreach (var te in ts)
                        meta.Tools.Add(new PluginToolMeta { Name = te.Definition.Function.Name, Tier = te.Tier.ToString(), Description = te.Definition.Function.Description ?? "", EnabledByDefault = te.Tier != ToolTier.L3 });
                }
                if (string.IsNullOrWhiteSpace(meta.Id) || meta.Id == Path.GetFileName(folder)) { meta.Id = inst.Id; meta.Name = inst.Name; meta.Version = inst.Version; }
                JoinLifecycle(ctx);
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
