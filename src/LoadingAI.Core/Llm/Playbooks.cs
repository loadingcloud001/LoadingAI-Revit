using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LoadingAI.Llm;

/// <summary>One saved procedure: the header fields plus the steps the model follows.</summary>
public sealed record Playbook(string Name, string Description, IReadOnlyList<string> Tools, string? Mode, string Body, string Source, string File);

/// <summary>
/// Parses one playbook file: a "---" header (name, description, tools, mode) and a body of steps.
/// Pure; a bad file comes back as an error the caller can show, never an exception.
/// </summary>
public static class PlaybookParser
{
    public const int MaxBodyChars = 8000;
    private static readonly Regex NameRule = new("^[a-z0-9][a-z0-9-]{0,39}$");

    /// <summary>The playbook, or null with an error saying why the file was refused.</summary>
    public static Playbook? Parse(string text, string file, string source, out string? error)
    {
        error = null;
        var t = (text ?? "").Replace("\r\n", "\n");
        var fileName = Path.GetFileName(file);
        if (!t.StartsWith("---\n")) { error = $"{fileName}: no '---' header."; return null; }
        var end = t.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) { error = $"{fileName}: header not closed with '---'."; return null; }
        var header = t.Substring(4, end - 4);
        var body = t.Substring(Math.Min(t.Length, end + 4)).Trim();

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in header.Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i > 0) map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }

        var name = map.TryGetValue("name", out var n) ? n : "";
        if (!NameRule.IsMatch(name)) { error = $"{fileName}: name must be lower-case letters, digits and '-'."; return null; }
        if (!map.TryGetValue("description", out var desc) || desc.Length == 0) { error = $"{fileName}: description is missing."; return null; }
        var mode = map.TryGetValue("mode", out var m) && m.Length > 0 ? m.ToLowerInvariant() : null;
        if (mode != null && mode != "plan" && mode != "build" && mode != "ask") { error = $"{fileName}: mode must be ask, plan or build."; return null; }
        if (body.Length == 0) { error = $"{fileName}: the playbook has no steps."; return null; }
        if (body.Length > MaxBodyChars) body = body.Substring(0, MaxBodyChars);

        var tools = map.TryGetValue("tools", out var tl)
            ? tl.Trim('[', ']').Split(',').Select(x => x.Trim().Trim('"', '\'')).Where(x => x.Length > 0).ToList()
            : new List<string>();
        return new Playbook(name, desc, tools, mode, body, source, file);
    }

    /// <summary>The user message a /name command sends: the steps first, then the user's own note.</summary>
    public static string Compose(Playbook pb, string? extra)
    {
        var sb = new StringBuilder($"Follow the playbook \"{pb.Name}\":\n{pb.Body}");
        if (!string.IsNullOrWhiteSpace(extra)) sb.Append("\n\nUser's note for this run: ").Append(extra!.Trim());
        return sb.ToString();
    }
}

/// <summary>
/// The saved procedures: the shipped starters (read-only) and then the user's own folder.
/// A user file with the same name wins; files that do not parse are reported, not hidden.
/// </summary>
public sealed class PlaybookCatalog
{
    public const int MaxListed = 30;

    public IReadOnlyList<Playbook> Items { get; }
    public IReadOnlyList<string> Issues { get; }

    private PlaybookCatalog(IReadOnlyList<Playbook> items, IReadOnlyList<string> issues) { Items = items; Issues = issues; }

    public static string DefaultUserDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "Playbooks");

    public static PlaybookCatalog Load(string? starterDir, string? userDir)
    {
        var byName = new Dictionary<string, Playbook>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();
        void Scan(string? dir, string source)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string text;
                try { text = File.ReadAllText(f); } catch (Exception ex) { issues.Add($"{Path.GetFileName(f)}: {ex.Message}"); continue; }
                var pb = PlaybookParser.Parse(text, f, source, out var err);
                if (pb == null) { issues.Add(err!); continue; }
                byName[pb.Name] = pb;
            }
        }
        Scan(starterDir, "starter");
        Scan(userDir, "user");
        return new PlaybookCatalog(byName.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(), issues);
    }

    public Playbook? Get(string name) => Items.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The prompt list: names and descriptions only. The body is read with open_playbook.</summary>
    public string PromptList()
    {
        if (Items.Count == 0) return "";
        var sb = new StringBuilder("\n# Playbooks\nThe user's saved procedures. Read one with open_playbook(name) when the task matches.\n");
        foreach (var p in Items.Take(MaxListed)) sb.Append("- ").Append(p.Name).Append(": ").Append(p.Description).Append('\n');
        return sb.ToString();
    }
}
