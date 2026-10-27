using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoadingAI.Revit;

namespace LoadingAI.Llm;

public sealed record Note(string Id, string Text, DateTime CreatedUtc, string Source);

/// <summary>Project and global notes the agent reads at the start of each chat (P3, ADR-0018). One JSON file per key.</summary>
public sealed class NoteStore
{
    public const string GlobalKey = "global";
    public const int MaxNoteChars = 300;

    private readonly string _dir;
    private readonly object _lock = new();
    private readonly List<string> _issues = new();

    public NoteStore(string? dir = null)
        => _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "memory");

    public IReadOnlyList<string> Issues { get { lock (_lock) return _issues.ToList(); } }

    /// <summary>
    /// The key one project is stored under: the cloud model GUID when there is one, else the
    /// worksharing central path, else the local path plus the project information id. A GUID
    /// survives a local move; a central path survives a re-sync. Hashed so the file name says
    /// nothing about the model.
    /// </summary>
    public static string KeyFor(ProjectIdentityDto id)
    {
        var basis = !string.IsNullOrWhiteSpace(id.CloudModelGuid) ? "cloud:" + id.CloudModelGuid!.ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(id.CentralPath) ? "central:" + id.CentralPath!.ToLowerInvariant()
            : "file:" + (id.PathName ?? "").ToLowerInvariant() + "|" + id.ProjectInfoUniqueId;
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
        return string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
    }

    public IReadOnlyList<Note> List(string key) { lock (_lock) return Load(key); }

    public Note Add(string key, string text, string source)
    {
        var clean = UserInstructions.Sanitize(text)?.Replace('\n', ' ');
        if (string.IsNullOrWhiteSpace(clean)) throw new ArgumentException("A note needs text.");
        var body = clean!;
        if (body.Length > MaxNoteChars) body = body.Substring(0, MaxNoteChars);
        var note = new Note(Guid.NewGuid().ToString("N").Substring(0, 8), body, DateTime.UtcNow, source == "agent" ? "agent" : "user");
        lock (_lock) { var all = Load(key).ToList(); all.Add(note); Save(key, all); }
        return note;
    }

    public bool Update(string key, string id, string text)
    {
        var clean = UserInstructions.Sanitize(text)?.Replace('\n', ' ');
        if (string.IsNullOrWhiteSpace(clean)) return false;
        var body = clean!;
        lock (_lock)
        {
            var all = Load(key).ToList();
            var i = all.FindIndex(n => n.Id == id);
            if (i < 0) return false;
            all[i] = all[i] with
            {
                Text = body.Length > MaxNoteChars ? body.Substring(0, MaxNoteChars) : body,
                Source = "user"
            };
            Save(key, all);
            return true;
        }
    }

    public bool Delete(string key, string id)
    {
        lock (_lock)
        {
            var all = Load(key).ToList();
            var removed = all.RemoveAll(n => n.Id == id) > 0;
            if (removed) Save(key, all);
            return removed;
        }
    }

    public void Clear(string key) { lock (_lock) Save(key, new List<Note>()); }

    private string PathFor(string key) => Path.Combine(_dir, key + ".json");

    private IReadOnlyList<Note> Load(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return Array.Empty<Note>();
        try { return JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(path)) ?? new List<Note>(); }
        catch
        {
            try { File.Copy(path, path + ".bad", true); File.Delete(path); } catch { }
            _issues.Add($"Notes file {key}.json could not be read and was kept as {key}.json.bad.");
            return Array.Empty<Note>();
        }
    }

    private void Save(string key, List<Note> notes)
    {
        Directory.CreateDirectory(_dir);
        var tmp = PathFor(key) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(notes));
        if (File.Exists(PathFor(key))) File.Delete(PathFor(key));
        File.Move(tmp, PathFor(key));
    }
}
