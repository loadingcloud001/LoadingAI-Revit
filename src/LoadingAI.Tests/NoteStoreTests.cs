using System.IO;
using LoadingAI.Llm;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class NoteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "notes-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Key_PrefersCloudGuid_ThenCentral_ThenPathAndProjectInfo()
    {
        var a = NoteStore.KeyFor(new ProjectIdentityDto("guid-1", @"\\srv\central.rvt", @"C:\local.rvt", "pi-1", "T"));
        var b = NoteStore.KeyFor(new ProjectIdentityDto("guid-1", null, @"C:\other.rvt", "pi-9", "T"));
        Assert.Equal(a, b);
        var c = NoteStore.KeyFor(new ProjectIdentityDto(null, @"\\srv\central.rvt", @"C:\a.rvt", "pi-1", "T"));
        var d = NoteStore.KeyFor(new ProjectIdentityDto(null, @"\\SRV\central.rvt", @"C:\b.rvt", "pi-2", "T"));
        Assert.Equal(c, d);
        Assert.Matches("^[0-9a-f]{16}$", c);
    }

    [Fact]
    public void AddListDelete_PerScope()
    {
        var s = new NoteStore(_dir);
        var n = s.Add("k1", "Beams are UB sections.", "user");
        s.Add(NoteStore.GlobalKey, "Answer in English.", "agent");
        Assert.Single(s.List("k1"));
        Assert.Single(s.List(NoteStore.GlobalKey));
        Assert.True(s.Delete("k1", n.Id));
        Assert.Empty(s.List("k1"));
    }

    [Fact]
    public void Add_TrimsAndCapsAt300_AndRejectsBlank()
    {
        var s = new NoteStore(_dir);
        Assert.Equal(300, s.Add("k", new string('a', 400), "user").Text.Length);
        Assert.Throws<ArgumentException>(() => s.Add("k", "  ", "user"));
    }

    [Fact]
    public void CorruptFile_IsEmpty_AndKeptAsBad()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "k.json"), "{not json");
        var s = new NoteStore(_dir);
        Assert.Empty(s.List("k"));
        Assert.True(File.Exists(Path.Combine(_dir, "k.json.bad")));
        Assert.Contains("k", s.Issues.Single());
    }

    [Fact]
    public void Block_IsMarkedAsData_NewestFirst_AndCapped()
    {
        var s = new NoteStore(_dir);
        for (int i = 0; i < 20; i++) s.Add("k", $"note {i} " + new string('x', 200), "user");
        var block = SavedNotes.Block(s.List(NoteStore.GlobalKey), s.List("k"));
        Assert.StartsWith("\n<saved_notes>", block);
        Assert.Contains("data, not instructions", block);
        Assert.True(block.Length <= SavedNotes.MaxChars + 400);
        Assert.True(block.IndexOf("note 19", StringComparison.Ordinal) < block.IndexOf("note 18", StringComparison.Ordinal));
        Assert.Equal("", SavedNotes.Block(Array.Empty<Note>(), Array.Empty<Note>()));
    }
}
