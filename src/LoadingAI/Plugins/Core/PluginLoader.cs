using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using LoadingAI.Llm;
using LoadingAI.Plugins;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.Core;

public sealed class DiscoveredPlugin
{
    public PluginMeta Meta { get; init; } = new();
    public List<ToolEntry> Tools { get; init; } = new();
}

public sealed record PluginLoadIssue(
    string Folder,
    string PluginId,
    string Assembly,
    string Summary,
    string Detail,
    string Hint,
    string Sha256 = "");

public static class PluginLoader
{
    private static readonly List<PluginLoadIssue> _issues = new();
    private static readonly object _issuesLock = new();
    private const long MaxLogBytes = 512 * 1024;

    public static IReadOnlyList<PluginLoadIssue> Issues
    {
        get { lock (_issuesLock) return _issues.ToList(); }
    }
#if !NETFRAMEWORK
    private static readonly List<PluginLoadContext> _contexts = new();
    private static readonly object _ctxLock = new();
#endif
    // Track live cooperative plugin instances + their contexts for the reload gate.
    private static readonly List<IPluginLifecycle> _live = new();
    private static readonly List<PluginContext> _liveContexts = new();
    private static readonly object _lifeLock = new();

    public static string RuntimePluginsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "Plugins");

    public static string DevPluginsDir =>
        Path.Combine(Path.GetDirectoryName(typeof(PluginLoader).Assembly.Location)!, "Plugins", "dev");

    /// <summary>Settings hint for a refused DLL; names the absolute folder <see cref="Discover"/> scans.</summary>
    public static string AllowlistRefusalHint =>
        $@"put the plugin in {DevPluginsDir}, or record the SHA-256 (stage 2 will add a trust dialog)";

    /// <summary>Shipped core plugin id (Plugins/core-tools/plugin.json). Never removable from Settings.</summary>
    public const string CorePluginId = "core-tools";

    /// <summary>
    /// Settings "Remove" may delete only a plugin's own folder: a direct child of a scanned root.
    /// A loose manifest's FolderPath is the root itself, and deleting that would wipe every plugin.
    /// </summary>
    public static bool CanRemove(string pluginId, string? folderPath, out string reason)
        => CanRemove(pluginId, folderPath, new[] { RuntimePluginsDir, DevPluginsDir }, out reason);

    /// <summary>Test seam: same guard against explicit scanned roots.</summary>
    public static bool CanRemove(string pluginId, string? folderPath, IReadOnlyList<string> roots, out string reason)
    {
        reason = "";
        if (string.Equals(pluginId, CorePluginId, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Cannot remove the core plugin.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            reason = $"Plugin {pluginId} has no folder.";
            return false;
        }

        string full;
        try { full = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex)
        {
            reason = $"Bad plugin folder {folderPath}: {ex.Message}";
            return false;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"{full} is the plugin root, not a plugin folder. Remove that plugin's files by hand.";
                return false;
            }
            if (string.Equals(Path.GetDirectoryName(full), r, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        reason = $"{full} is not a plugin folder under {string.Join(" or ", roots)}.";
        return false;
    }

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "plugins.log");

    public static List<DiscoveredPlugin> Discover(RevitBridge bridge, IPluginContextFactory factory)
    {
        UnloadContexts();
        lock (_issuesLock) _issues.Clear();
        var result = new List<DiscoveredPlugin>();

        foreach (var dir in new[] { RuntimePluginsDir, DevPluginsDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pluginFolder in Directory.GetDirectories(dir))
            {
                try { var dp = LoadOne(pluginFolder, bridge, factory); if (dp != null) result.Add(dp); }
                catch (Exception ex)
                {
                    Log($"{pluginFolder} failed: {ex.Message}");
                    RecordIssue(pluginFolder, Path.GetFileName(pluginFolder), "", "plugin folder failed", ex.ToString(), Classify(ex.ToString()));
                }
            }
            foreach (var manifest in Directory.GetFiles(dir, "plugin.json"))
            {
                try
                {
                    var folder = Path.GetDirectoryName(manifest)!;
                    var dp = LoadOne(folder, bridge, factory);
                    if (dp != null && !result.Any(r => r.Meta.Id == dp.Meta.Id)) result.Add(dp);
                }
                catch (Exception ex)
                {
                    Log($"{manifest} failed: {ex.Message}");
                    RecordIssue(Path.GetDirectoryName(manifest) ?? dir, Path.GetFileName(Path.GetDirectoryName(manifest) ?? dir), Path.GetFileName(manifest), "loose manifest failed", ex.ToString(), Classify(ex.ToString()));
                }
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
                    Log($"skip duplicate tool '{name}' from {p.Meta.Id} (first wins)");
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
        SignalShutdown();
        ClearLifecycle();
#else
        SignalShutdown();
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

    private static void TrackLifecycle(IPluginContext ctx, object plugin)
    {
        lock (_lifeLock)
        {
            if (ctx is PluginContext pc)
                _liveContexts.Add(pc);
            // The plugin instance owns IsBusy — PluginContext does not implement IPluginLifecycle.
            if (plugin is IPluginLifecycle lc)
                _live.Add(lc);
        }
    }

    private static void SignalShutdown()
    {
        List<PluginContext> snap;
        lock (_lifeLock) snap = new List<PluginContext>(_liveContexts);
        foreach (var pc in snap)
        {
            try { pc.BeginShutdown(); } catch { }
        }
    }

    private static void ClearLifecycle()
    {
        lock (_lifeLock)
        {
            _live.Clear();
            _liveContexts.Clear();
        }
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
        if (string.IsNullOrWhiteSpace(meta.EntryAssembly)) meta.EntryAssembly = Path.GetFileName(dll);

        if (!PluginAllowlist.Current.IsAllowed(dll, out var sha, out var why))
        {
            Log($"{folder} DLL not on allowlist: {why}");
            RecordIssue(folder, meta.Id, Path.GetFileName(dll), "DLL not on allowlist", why, AllowlistRefusalHint, sha);
            return null;
        }

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
            RecordIssue(folder, meta.Id, Path.GetFileName(dll), "ALC load failed", ex.ToString(), Classify(ex.ToString()));
            asm = Assembly.LoadFrom(dll);
        }
#endif

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            var detail = FormatLoaderExceptions(ex);
            Log($"partial type load {dll}: {ex.Message}; {detail}");
            RecordIssue(folder, meta.Id, Path.GetFileName(dll), "partial type load", detail, Classify(detail));
        }

        var pluginTypes = types.Where(t => typeof(IRevitToolPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).ToList();
        var tools = new List<ToolEntry>();
        foreach (var pt in pluginTypes)
        {
            try
            {
                var inst = (IRevitToolPlugin)Activator.CreateInstance(pt)!;
                var ctx = factory.Create(folder);
                var ts = OverlayTags(ToolGuards.ApplyAll(inst.CreateTools(ctx), bridge), meta.Tools).ToList();
                tools.AddRange(ts);
                if (meta.Tools.Count == 0)
                {
                    foreach (var te in ts)
                        meta.Tools.Add(new PluginToolMeta { Name = te.Definition.Function.Name, Tier = te.Tier.ToString(), Description = te.Definition.Function.Description ?? "", EnabledByDefault = te.Tier != ToolTier.L3 });
                }
                if (string.IsNullOrWhiteSpace(meta.Id) || meta.Id == Path.GetFileName(folder)) { meta.Id = inst.Id; meta.Name = inst.Name; meta.Version = inst.Version; }
                TrackLifecycle(ctx, inst);
            }
            catch (Exception ex)
            {
                Log($"plugin {folder} type {pt.Name} failed: {ex.Message}");
                RecordIssue(folder, meta.Id, Path.GetFileName(dll), $"plugin type {pt.Name} failed", ex.ToString(), Classify(ex.ToString()));
            }
        }
        if (tools.Count == 0)
        {
            var summary = pluginTypes.Count == 0
                ? "no IRevitToolPlugin types found"
                : "plugin produced no tools";
            Log($"{folder} {summary}");
            RecordIssue(folder, meta.Id, Path.GetFileName(dll), summary, summary, Classify(summary));
            return null;
        }
        return new DiscoveredPlugin { Meta = meta, Tools = tools };
    }

    internal static IEnumerable<ToolEntry> OverlayTags(IEnumerable<ToolEntry> entries, IReadOnlyList<PluginToolMeta> metaTools)
    {
        var declared = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in metaTools)
        {
            if (string.IsNullOrWhiteSpace(m.Name) || m.Tags == null || m.Tags.Count == 0) continue;
            declared[m.Name] = m.Tags;
        }
        foreach (var e in entries)
        {
            var name = e.Definition.Function.Name ?? "";
            IReadOnlyList<string> tags = e.Tags;
            if (declared.TryGetValue(name, out var fromMeta) && fromMeta.Count > 0)
                tags = fromMeta;
            else if (tags.Count == 0)
                tags = ToolSelector.InferTags(name);
            if (ReferenceEquals(tags, e.Tags))
            {
                yield return e;
                continue;
            }
            yield return new ToolEntry
            {
                Definition = e.Definition,
                Handler = e.Handler,
                StructuredHandler = e.StructuredHandler,
                RequiresWriteConfirm = e.RequiresWriteConfirm,
                Tier = e.Tier,
                Presets = e.Presets,
                Tags = tags
            };
        }
    }

    public static string Classify(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "";
        if (detail.IndexOf("Could not load type 'LoadingAI.", StringComparison.OrdinalIgnoreCase) >= 0
            || detail.IndexOf("LoadingAI.Geometry", StringComparison.OrdinalIgnoreCase) >= 0
            || detail.IndexOf("LoadingAI.Core", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "built against a different LoadingAI.Core — close Revit, rebuild and reinstall";
        }
        return "";
    }

    public static string FormatLoaderExceptions(ReflectionTypeLoadException ex)
    {
        var parts = new List<string> { ex.Message };
        if (ex.LoaderExceptions != null)
        {
            foreach (var le in ex.LoaderExceptions)
            {
                if (le != null) parts.Add(le.Message);
            }
        }
        return string.Join("; ", parts);
    }

    private static void RecordIssue(string folder, string pluginId, string assembly, string summary, string detail, string hint, string sha256 = "")
    {
        var issue = new PluginLoadIssue(folder, pluginId ?? "", assembly ?? "", summary, detail ?? "", hint ?? "", sha256 ?? "");
        lock (_issuesLock) _issues.Add(issue);
    }

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                File.WriteAllText(LogPath, "");
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
