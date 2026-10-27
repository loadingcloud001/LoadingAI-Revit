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

/// <summary>Stable, machine-readable reasons for a <see cref="PluginLoadIssue"/>. The pane keys on these, never on the wording.</summary>
public static class PluginIssueCodes
{
    public const string AllowlistRefusal = "allowlist-refusal";
    public const string AllowlistUnreadable = "allowlist-unreadable";
    public const string ManifestMissing = "manifest-missing";
    public const string ManifestUnreadable = "manifest-unreadable";
    public const string DuplicatePlugin = "duplicate-plugin";
    public const string Shadowed = "shadowed";
    public const string ManifestDrift = "manifest-drift";
    public const string NoTools = "no-tools";
    public const string LoadFailed = "load-failed";
}

public sealed record PluginLoadIssue(
    string Folder,
    string PluginId,
    string Assembly,
    string Summary,
    string Detail,
    string Hint,
    string Sha256 = "",
    string Code = "")
{
    /// <summary>True for a note about a plugin that did load (a dev copy replacing an installed one, manifest drift); false for a failure.</summary>
    public bool Notice => Code == PluginIssueCodes.Shadowed || Code == PluginIssueCodes.ManifestDrift;
}

public static class PluginLoader
{
    private static readonly List<PluginLoadIssue> _issues = new();
    private static readonly object _issuesLock = new();

    public static IReadOnlyList<PluginLoadIssue> Issues
    {
        get { lock (_issuesLock) return _issues.ToList(); }
    }

    /// <summary>Issues that stopped a plugin from loading. Notices about plugins that did load do not count.</summary>
    public static int FailureCount
    {
        get { lock (_issuesLock) return _issues.Count(i => !i.Notice); }
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
        $@"put the plugin in {DevPluginsDir}, or click Trust this plugin… to record the SHA-256 in plugin-allowlist.json";

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

    /// <summary>
    /// Dropped into a plugin folder before it is deleted. A DLL that is still loaded blocks the delete
    /// part-way; the marker makes the next scan skip the folder and purge it instead of loading a
    /// half-deleted plugin.
    /// </summary>
    public const string RemoveMarkerName = ".remove";

    /// <summary>Marks a plugin folder for removal under the scanned roots, then tries to delete it now.</summary>
    public static bool TryRemove(string pluginId, string? folderPath, out string message)
        => TryRemove(pluginId, folderPath, new[] { RuntimePluginsDir, DevPluginsDir }, out message);

    /// <summary>Marks a plugin folder for removal, then tries to delete it now.</summary>
    public static bool TryRemove(string pluginId, string? folderPath, IReadOnlyList<string> roots, out string message)
    {
        if (!CanRemove(pluginId, folderPath, roots, out message)) return false;
        var folder = Path.GetFullPath(folderPath!);
        try
        {
            File.WriteAllText(Path.Combine(folder, RemoveMarkerName), DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            message = $"Could not mark {folder} for removal: {ex.Message}";
            return false;
        }

        try
        {
            Directory.Delete(folder, true);
            message = $"Removed {pluginId}.";
        }
        catch
        {
            // A partial recursive delete may already have removed the marker; put it back so the scan
            // skips the half-deleted folder and PurgeMarked finishes the job later.
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, RemoveMarkerName), DateTime.UtcNow.ToString("O"));
            }
            catch { }
            message = $"{pluginId} is in use. It will be deleted the next time plugins are scanned or Revit restarts.";
        }
        return true;
    }

    /// <summary>Deletes plugin folders carrying the removal marker. Returns how many were purged.</summary>
    public static int PurgeMarked(IEnumerable<string> roots)
    {
        var purged = 0;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, RemoveMarkerName))) continue;
                try { Directory.Delete(dir, true); purged++; }
                catch (Exception ex) { Log($"purge {dir} deferred: {ex.Message}"); }
            }
        }
        return purged;
    }

    /// <summary>
    /// Text for the Trust dialog. It repeats what the plugin's own plugin.json claims, labelled as a
    /// claim, and says plainly that a trusted DLL runs inside Revit with the engineer's permissions.
    /// </summary>
    public static string DescribeForTrust(string dll, string sha256, string pluginFolder)
    {
        var claims = "";
        try
        {
            var path = Path.Combine(pluginFolder, "plugin.json");
            if (File.Exists(path))
            {
                var meta = JsonSerializer.Deserialize<PluginMeta>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta != null)
                {
                    var tools = string.Join(", ", meta.Tools.Take(12).Select(t => $"{t.Name} ({t.Tier})"));
                    if (meta.Tools.Count > 12) tools += $", … {meta.Tools.Count - 12} more";
                    claims = $"\nIt calls itself \"{meta.Name}\" {meta.Version} by {meta.Author}, with tools: {tools}.\n"
                             + "These are the plugin's own claims; nothing has verified them.\n";
                }
            }
        }
        catch
        {
            // The dialog still works without the claims.
        }

        return $"{dll}\nSHA-256 {sha256}\n{claims}\n"
               + "A trusted plugin runs inside Revit with your permissions and can do anything you can, "
               + "including ignoring tool tiers and Confirm L3. Trust it only if you built it or have reviewed it.";
    }

    public static List<DiscoveredPlugin> Discover(RevitBridge bridge, IPluginContextFactory factory)
    {
        UnloadContexts();
        PurgeMarked(new[] { DevPluginsDir, RuntimePluginsDir });
        lock (_issuesLock) _issues.Clear();
        var result = new List<DiscoveredPlugin>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The dev folder is scanned first so a dev copy wins over an installed plugin with the same id
        // or tool name, and the order inside a folder is stable instead of whatever the file system returns.
        foreach (var dir in new[] { DevPluginsDir, RuntimePluginsDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pluginFolder in Directory.GetDirectories(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try { var dp = LoadOne(pluginFolder, bridge, factory, seen); if (dp != null) result.Add(dp); }
                catch (Exception ex)
                {
                    Log($"{pluginFolder} failed: {ex.Message}");
                    RecordIssue(pluginFolder, Path.GetFileName(pluginFolder), "", "plugin folder failed", ex.ToString(), Classify(ex.ToString()), code: PluginIssueCodes.LoadFailed);
                }
            }
            foreach (var manifest in Directory.GetFiles(dir, "plugin.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var folder = Path.GetDirectoryName(manifest)!;
                    var dp = LoadOne(folder, bridge, factory, seen);
                    if (dp != null) result.Add(dp);
                }
                catch (Exception ex)
                {
                    Log($"{manifest} failed: {ex.Message}");
                    RecordIssue(Path.GetDirectoryName(manifest) ?? dir, Path.GetFileName(Path.GetDirectoryName(manifest) ?? dir), Path.GetFileName(manifest), "loose manifest failed", ex.ToString(), Classify(ex.ToString()), code: PluginIssueCodes.LoadFailed);
                }
            }
        }

        var seenTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in result)
        {
            var kept = new List<ToolEntry>();
            foreach (var t in p.Tools)
            {
                var name = t.Definition.Function.Name;
                if (!seenTools.Add(name))
                {
                    Log($"skip duplicate tool '{name}' from {p.Meta.Id} (first wins)");
                    continue;
                }
                kept.Add(t);
            }
            p.Tools.Clear();
            p.Tools.AddRange(kept);
            p.Meta.Tools = p.Meta.Tools.Where(m => seenTools.Contains(m.Name) && p.Tools.Any(t => t.Definition.Function.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))).ToList();
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

    /// <summary>
    /// Reads and checks a folder's plugin.json. Pure file IO, so a test can drive it with a temp folder.
    /// A folder with no DLL is not a plugin and is not reported. A folder with a DLL and no usable
    /// manifest is reported: the loader never guesses which DLL to run (B5).
    /// </summary>
    public static bool TryReadManifest(string folder, out PluginMeta meta, out string dll, out PluginLoadIssue? issue)
    {
        meta = new PluginMeta();
        dll = "";
        issue = null;

        if (File.Exists(Path.Combine(folder, RemoveMarkerName)))
            return false; // marked for removal: skipped silently, purged by the next scan

        var manifestPath = Path.Combine(folder, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            if (Directory.GetFiles(folder, "*.dll").Length > 0)
                issue = new PluginLoadIssue(folder, Path.GetFileName(folder), "", "plugin.json missing",
                    $"{manifestPath} was not found.", "Add a plugin.json with an entryAssembly.", "", PluginIssueCodes.ManifestMissing);
            return false;
        }

        PluginMeta? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PluginMeta>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            Log($"bad manifest {manifestPath}: {ex.Message}");
            issue = new PluginLoadIssue(folder, Path.GetFileName(folder), "", "plugin.json unreadable",
                ex.Message, "Fix the JSON in plugin.json.", "", PluginIssueCodes.ManifestUnreadable);
            return false;
        }

        if (parsed == null || string.IsNullOrWhiteSpace(parsed.EntryAssembly)
            || !string.Equals(Path.GetFileName(parsed.EntryAssembly), parsed.EntryAssembly, StringComparison.Ordinal))
        {
            issue = new PluginLoadIssue(folder, parsed?.Id ?? Path.GetFileName(folder), parsed?.EntryAssembly ?? "",
                "entryAssembly missing or not a file name", "plugin.json must name one DLL in its own folder.",
                "Set \"entryAssembly\" to a file name such as LoadingAI.Plugins.Hello.dll.", "", PluginIssueCodes.ManifestUnreadable);
            return false;
        }

        var path = Path.Combine(folder, parsed.EntryAssembly);
        if (!File.Exists(path))
        {
            issue = new PluginLoadIssue(folder, string.IsNullOrWhiteSpace(parsed.Id) ? Path.GetFileName(folder) : parsed.Id,
                parsed.EntryAssembly, "entry assembly not found", $"{path} does not exist.",
                "Copy the plugin DLL next to plugin.json.", "", PluginIssueCodes.ManifestUnreadable);
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Id)) parsed.Id = Path.GetFileName(folder);
        parsed.FolderPath = folder;
        meta = parsed;
        dll = path;
        return true;
    }

    /// <summary>
    /// Registers a plugin id for this scan. The first folder to claim an id keeps it; because the dev
    /// folder is scanned first, a dev copy wins and the installed one is reported as a notice.
    /// </summary>
    public static bool TryClaimId(Dictionary<string, string> seen, PluginMeta meta, string folder, out PluginLoadIssue? issue)
    {
        issue = null;
        if (!seen.TryGetValue(meta.Id, out var first))
        {
            seen[meta.Id] = folder;
            return true;
        }

        var devWins = PluginAllowlist.IsUnderDirectory(first, DevPluginsDir);
        issue = new PluginLoadIssue(folder, meta.Id, meta.EntryAssembly,
            devWins ? "replaced by the dev copy" : "duplicate plugin id",
            devWins ? $"The dev copy in {first} replaces this installed copy." : $"Plugin id '{meta.Id}' is already provided by {first}.",
            devWins ? "Delete the dev copy to use the installed one." : "Remove one of the folders.",
            "", devWins ? PluginIssueCodes.Shadowed : PluginIssueCodes.DuplicatePlugin);
        return false;
    }

    private static DiscoveredPlugin? LoadOne(string folder, RevitBridge bridge, IPluginContextFactory factory, Dictionary<string, string> seen)
    {
        if (!TryReadManifest(folder, out var meta, out var dll, out var manifestIssue))
        {
            if (manifestIssue != null) RecordIssue(manifestIssue);
            return null;
        }

        if (!PluginAllowlist.Current.IsAllowed(dll, out var sha, out var why))
        {
            Log($"{folder} DLL not on allowlist: {why}");
            var unreadable = why.IndexOf("is unreadable", StringComparison.Ordinal) >= 0;
            var hint = unreadable ? "Fix or delete the unreadable allowlist file, then rescan." : AllowlistRefusalHint;
            var code = unreadable ? PluginIssueCodes.AllowlistUnreadable : PluginIssueCodes.AllowlistRefusal;
            RecordIssue(folder, meta.Id, Path.GetFileName(dll), "DLL not on allowlist", why, hint, sha, code);
            return null;
        }

        if (!TryClaimId(seen, meta, folder, out var claimIssue))
        {
            Log($"{folder}: {claimIssue!.Summary}: {claimIssue.Detail}");
            RecordIssue(claimIssue);
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
            try { asm = Assembly.LoadFrom(dll); }
            catch (Exception ex2)
            {
                RecordIssue(folder, meta.Id, Path.GetFileName(dll), "assembly load failed", ex2.ToString(), Classify(ex2.ToString()), code: PluginIssueCodes.LoadFailed);
                return null;
            }
        }
#endif

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            var detail = FormatLoaderExceptions(ex);
            Log($"partial type load {dll}: {ex.Message}; {detail}");
            RecordIssue(folder, meta.Id, Path.GetFileName(dll), "partial type load", detail, Classify(detail), code: PluginIssueCodes.LoadFailed);
        }

        var pluginTypes = types.Where(t => typeof(IRevitToolPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).ToList();
        var tools = new List<ToolEntry>();
        foreach (var pt in pluginTypes)
        {
            try
            {
                var inst = (IRevitToolPlugin)Activator.CreateInstance(pt)!;
                var ctx = factory.Create(folder);
                var ts = OverlayTags(inst.CreateTools(ctx), meta.Tools).ToList();
                tools.AddRange(ts);
                if (meta.Tools.Count == 0)
                {
                    foreach (var te in ts)
                        meta.Tools.Add(new PluginToolMeta { Name = te.Definition.Function.Name, Tier = te.Tier.ToString(), Description = te.Definition.Function.Description ?? "", EnabledByDefault = te.Tier != ToolTier.L3 });
                }
                else
                {
                    foreach (var problem in ManifestDrift(meta, ts))
                    {
                        Log($"{folder}: {problem}");
                        RecordIssue(folder, meta.Id, Path.GetFileName(dll), "plugin.json differs from the code", problem,
                            "The code wins. Fix plugin.json.", code: PluginIssueCodes.ManifestDrift);
                    }
                }
                if (string.IsNullOrWhiteSpace(meta.Id) || meta.Id == Path.GetFileName(folder)) { meta.Id = inst.Id; meta.Name = inst.Name; meta.Version = inst.Version; }
                TrackLifecycle(ctx, inst);
            }
            catch (Exception ex)
            {
                Log($"plugin {folder} type {pt.Name} failed: {ex.Message}");
                RecordIssue(folder, meta.Id, Path.GetFileName(dll), $"plugin type {pt.Name} failed", ex.ToString(), Classify(ex.ToString()), code: PluginIssueCodes.LoadFailed);
            }
        }
        if (tools.Count == 0)
        {
            var summary = pluginTypes.Count == 0
                ? "no IRevitToolPlugin types found"
                : "plugin produced no tools";
            Log($"{folder} {summary}");
            RecordIssue(folder, meta.Id, Path.GetFileName(dll), summary, summary, Classify(summary), code: PluginIssueCodes.NoTools);
            return null;
        }
        return new DiscoveredPlugin { Meta = meta, Tools = tools };
    }

    /// <summary>
    /// What plugin.json declares against what the plugin's code registered. Empty means they agree.
    /// The code is authoritative (the tier gate and the default-off rule read the code); the manifest
    /// is display data, so a difference is reported, never enforced.
    /// </summary>
    public static IReadOnlyList<string> ManifestDrift(PluginMeta meta, IEnumerable<ToolEntry> tools)
    {
        var problems = new List<string>();
        var byName = new Dictionary<string, ToolEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools)
        {
            var key = t.Definition.Function.Name ?? "";
            if (!byName.ContainsKey(key)) byName[key] = t;
        }

        foreach (var declared in meta.Tools)
        {
            if (!byName.TryGetValue(declared.Name, out var entry))
            {
                problems.Add($"plugin.json declares '{declared.Name}' but the plugin registers no such tool");
                continue;
            }
            if (!string.Equals(declared.Tier, entry.Tier.ToString(), StringComparison.OrdinalIgnoreCase))
                problems.Add($"'{declared.Name}' is {declared.Tier} in plugin.json but {entry.Tier} in code");
            var expectedDefault = entry.Tier != ToolTier.L3;
            if (declared.EnabledByDefault != expectedDefault)
                problems.Add($"'{declared.Name}' enabledByDefault is {declared.EnabledByDefault} in plugin.json but the host uses {expectedDefault}");
        }

        foreach (var name in byName.Keys)
            if (!meta.Tools.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"the plugin registers '{name}' but plugin.json does not list it");

        return problems;
    }

    internal static IEnumerable<ToolEntry> OverlayTags(IEnumerable<ToolEntry> entries, IReadOnlyList<PluginToolMeta> metaTools)
    {
        var declared = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var destructive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in metaTools)
        {
            if (string.IsNullOrWhiteSpace(m.Name)) continue;
            if (m.Destructive) destructive.Add(m.Name);
            if (m.Tags == null || m.Tags.Count == 0) continue;
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
            var isDestructive = e.Destructive || destructive.Contains(name);
            if (ReferenceEquals(tags, e.Tags) && isDestructive == e.Destructive)
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
                Destructive = isDestructive,
                NeedsVision = e.NeedsVision,
                NeverAllowList = e.NeverAllowList,
                Tier = e.Tier,
                Presets = e.Presets,
                Describe = e.Describe,
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

    private static void RecordIssue(string folder, string pluginId, string assembly, string summary, string detail, string hint, string sha256 = "", string code = "")
        => RecordIssue(new PluginLoadIssue(folder, pluginId ?? "", assembly ?? "", summary, detail ?? "", hint ?? "", sha256 ?? "", code ?? ""));

    private static void RecordIssue(PluginLoadIssue issue)
    {
        lock (_issuesLock) _issues.Add(issue);
    }

    private static void Log(string msg) => PluginLogFile.Append($"{DateTime.Now:O} {msg}");
}
