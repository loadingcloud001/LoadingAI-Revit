using System.IO;
using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins.Core;
using LoadingAI.Progress;
using LoadingAI.Protocol;
using Xunit;

namespace LoadingAI.Tests;

public class HostProtocolTests
{
    [Fact]
    public void TryParse_AllTwentyOneToHostKinds()
    {
        Assert.Equal(21, ToHostKind.All.Count);

        Assert.True(ToHostMessage.TryParse("""{"type":"send","text":"hi","mode":"plan","slash":"/plan","mentions":["Selection"]}""", out var send, out var err));
        Assert.Null(err);
        var s = Assert.IsType<ToHostMessage.Send>(send);
        Assert.Equal("hi", s.Text);
        Assert.Equal("plan", s.Mode);
        Assert.Equal("/plan", s.Slash);
        Assert.Equal(new[] { "selection" }, s.Mentions);

        AssertKind<ToHostMessage.Clear>("""{"type":"clear"}""");
        AssertKind<ToHostMessage.Cancel>("""{"type":"cancel"}""");
        AssertKind<ToHostMessage.RequestSettings>("""{"type":"requestSettings"}""");
        AssertKind<ToHostMessage.GetCatalog>("""{"type":"getCatalog"}""");
        AssertKind<ToHostMessage.ListPlugins>("""{"type":"listPlugins"}""");
        AssertKind<ToHostMessage.SaveToolSettings>("""{"type":"saveToolSettings"}""");
        AssertKind<ToHostMessage.ReloadPlugins>("""{"type":"reloadPlugins"}""");
        AssertKind<ToHostMessage.TrustPlugin>("""{"type":"trustPlugin","folder":"C:\\p","assembly":"a.dll"}""");
        AssertKind<ToHostMessage.ListCheckpoints>("""{"type":"listCheckpoints"}""");

        Assert.True(ToHostMessage.TryParse("""{"type":"saveSettings","settings":{"activeProviderId":"x","providers":[]}}""", out var save, out err));
        Assert.Null(err);
        var saved = Assert.IsType<ToHostMessage.SaveSettings>(save);
        Assert.Equal("x", saved.Settings!.ActiveProviderId);

        Assert.True(ToHostMessage.TryParse("""{"type":"testProvider","providerId":"openai","provider":{"id":"openai","name":"OpenAI"}}""", out var test, out err));
        var tp = Assert.IsType<ToHostMessage.TestProvider>(test);
        Assert.Equal("openai", tp.ProviderId);
        Assert.Equal("OpenAI", tp.Provider!.Name);

        Assert.True(ToHostMessage.TryParse("""{"type":"listModels","providerId":"openai"}""", out var models, out err));
        Assert.IsType<ToHostMessage.ListModels>(models);

        Assert.True(ToHostMessage.TryParse("""{"type":"approveTool","toolCallId":"abc","approved":true}""", out var ap, out err));
        var approve = Assert.IsType<ToHostMessage.ApproveTool>(ap);
        Assert.Equal("abc", approve.ToolCallId);
        Assert.True(approve.Approved);

        Assert.True(ToHostMessage.TryParse("""{"type":"submitPreset","toolCallId":"p1","approved":false,"values":{"a":1}}""", out var sub, out err));
        var preset = Assert.IsType<ToHostMessage.SubmitPreset>(sub);
        Assert.Equal("p1", preset.ToolCallId);
        Assert.False(preset.Approved);
        Assert.Contains("\"a\"", preset.ValuesJson);

        Assert.True(ToHostMessage.TryParse("""{"type":"presetPick","toolCallId":"p1","fieldId":"ids"}""", out var pick, out err));
        var pp = Assert.IsType<ToHostMessage.PresetPick>(pick);
        Assert.Equal("ids", pp.FieldId);

        Assert.True(ToHostMessage.TryParse("""{"type":"setPluginEnabled","toolName":"get_rooms","enabled":true}""", out var en, out err));
        Assert.True(Assert.IsType<ToHostMessage.SetPluginEnabled>(en).Enabled);

        Assert.True(ToHostMessage.TryParse("""{"type":"setPluginGroupEnabled","pluginId":"core-tools","enabled":false}""", out var grp, out err));
        Assert.False(Assert.IsType<ToHostMessage.SetPluginGroupEnabled>(grp).Enabled);

        Assert.True(ToHostMessage.TryParse("""{"type":"setPluginTierEnabled","tier":"L3","enabled":true}""", out var tier, out err));
        Assert.Equal("L3", Assert.IsType<ToHostMessage.SetPluginTierEnabled>(tier).Tier);

        Assert.True(ToHostMessage.TryParse("""{"type":"removePlugin","pluginId":"hello"}""", out var rem, out err));
        Assert.Equal("hello", Assert.IsType<ToHostMessage.RemovePlugin>(rem).PluginId);

        Assert.True(ToHostMessage.TryParse("""{"type":"resumeCheckpoint","id":"cp1"}""", out var resume, out err));
        Assert.Equal("cp1", Assert.IsType<ToHostMessage.ResumeCheckpoint>(resume).Id);
    }

    [Fact]
    public void TryParse_TrustPlugin_CarriesIdentifierOnly()
    {
        Assert.True(ToHostMessage.TryParse(
            """{"type":"trustPlugin","folder":"C:\\Users\\bim\\AppData\\Roaming\\LoadingAI\\Plugins\\hello","assembly":"hello.dll","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""",
            out var msg, out var err));
        Assert.Null(err);
        var trust = Assert.IsType<ToHostMessage.TrustPlugin>(msg);
        // A hash on the wire is ignored: the host re-hashes the DLL itself.
        Assert.Equal(@"C:\Users\bim\AppData\Roaming\LoadingAI\Plugins\hello", trust.Folder);
        Assert.Equal("hello.dll", trust.Assembly);

        Assert.True(ToHostMessage.TryParse("""{"type":"trustPlugin"}""", out var empty, out err));
        Assert.Null(err);
        var blank = Assert.IsType<ToHostMessage.TrustPlugin>(empty);
        Assert.Equal("", blank.Folder);
        Assert.Equal("", blank.Assembly);
    }

    [Fact]
    public void TryParse_UnknownType_Ignored()
    {
        Assert.True(ToHostMessage.TryParse("""{"type":"nope"}""", out var msg, out var err));
        Assert.Null(msg);
        Assert.Null(err);
    }

    [Fact]
    public void TryParse_BadJson_AndMissingType_Fail()
    {
        Assert.False(ToHostMessage.TryParse("{", out var msg, out var err));
        Assert.Null(msg);
        Assert.False(string.IsNullOrWhiteSpace(err));

        Assert.False(ToHostMessage.TryParse("{}", out msg, out err));
        Assert.Null(msg);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void TryParse_SaveSettingsMissingSettings_Fails()
    {
        Assert.False(ToHostMessage.TryParse("""{"type":"saveSettings"}""", out var msg, out var err));
        Assert.Null(msg);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void TryParse_SendAttachments_MaxFive_AndOversizedError()
    {
        var small = """{"id":"a","name":"x.png","mediaType":"image/png","dataUrl":"data:image/png,abc"}""";
        var atts = string.Join(",", Enumerable.Range(0, 6).Select(_ => small));
        Assert.True(ToHostMessage.TryParse($@"{{""type"":""send"",""text"":"""",""attachments"":[{atts}]}}", out var msg, out var err));
        Assert.Null(err);
        var send = Assert.IsType<ToHostMessage.Send>(msg);
        Assert.Equal(5, send.Attachments.Count);
        Assert.Empty(send.AttachmentErrors);

        var huge = "data:image/png," + new string('x', 6_300_000);
        var payload = JsonSerializer.Serialize(new { type = "send", text = "", attachments = new[] { new { id = "z", name = "big.png", mediaType = "image/png", dataUrl = huge } } });
        Assert.True(ToHostMessage.TryParse(payload, out msg, out err));
        send = Assert.IsType<ToHostMessage.Send>(msg);
        Assert.Empty(send.Attachments);
        Assert.Equal(new[] { ToHostMessage.OversizedImageError }, send.AttachmentErrors);

        Assert.True(ToHostMessage.TryParse("""{"type":"send","text":"t","attachments":[{"dataUrl":"data:text/plain,hi"}]}""", out msg, out err));
        send = Assert.IsType<ToHostMessage.Send>(msg);
        Assert.Empty(send.Attachments);
        Assert.Empty(send.AttachmentErrors);
    }

    [Fact]
    public void ToHostAndFromHostKinds_MatchBridgeTs()
    {
        var ts = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "bridge.ts"));
        var toHost = Slice(ts, "export type ToHost =", "export type PlanPayload");
        var fromHost = Slice(ts, "export type FromHost =", "declare global");
        foreach (var kind in ToHostKind.All)
            Assert.Contains($"type: '{kind}'", toHost);
        foreach (var kind in FromHostKind.All)
            Assert.Contains($"type: '{kind}'", fromHost);
        Assert.Equal(21, CountOccurrences(toHost, "type: '"));
        Assert.Equal(FromHostKind.All.Count, CountOccurrences(fromHost, "type: '"));
    }

    [Fact]
    public void HostPage_DispatchesTypedToHost_KeepsFromHostPosts()
    {
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "HostPage.xaml.cs"));
        var ui = string.Join("\n", Directory.GetFiles(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui"), "*.cs").Select(File.ReadAllText));
        Assert.Contains("ToHostMessage.TryParse", host);
        Assert.DoesNotContain("case \"send\"", host);
        Assert.DoesNotContain("case \"submitPreset\"", host);
        Assert.Contains("ToHostMessage.SubmitPreset", host);
        Assert.Contains("Confirm L3:", ui);
        Assert.Contains("new FromHostMessage.ToolPreset", ui);
        Assert.Contains("FromHostMessage.PresetPickResult", ui);
        var protocol = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Protocol", "HostProtocol.cs"));
        Assert.Contains("FromHostKind.ToolPreset", protocol);
        foreach (var kind in FromHostKind.All)
            Assert.Contains($"\"{kind}\"", ui + File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Protocol", "HostProtocol.cs")));
    }

    [Fact]
    public void FromHostMessage_SerializesAllTwentyFourKinds()
    {
        Assert.Equal(24, FromHostKind.All.Count);
        var messages = new FromHostMessage[]
        {
            new FromHostMessage.Status("s"),
            new FromHostMessage.Trace("t"),
            new FromHostMessage.ToolCall("c"),
            new FromHostMessage.ToolResult("r"),
            new FromHostMessage.ToolApproval(new FromHostMessage.ApprovalPayload("id", "tool", "L3", "{}", "d")),
            new FromHostMessage.ToolPreset(new FromHostMessage.PresetPayload("id", "tool", "L2", "d", Array.Empty<FromHostMessage.PresetFieldPayload>(), null)),
            new FromHostMessage.PresetPickResult("id", "field", Ok: true),
            new FromHostMessage.Plan(new FromHostMessage.PlanPayload("title", Array.Empty<FromHostMessage.PlanStepPayload>())),
            new FromHostMessage.Todo(Array.Empty<TodoItem>()),
            new FromHostMessage.Progress(ProgressSnapshot.Thinking()),
            new FromHostMessage.Context(new FromHostMessage.ContextPayload(0, null, "3D", null, null, null)),
            new FromHostMessage.Checkpoints(Array.Empty<CheckpointInfo>()),
            new FromHostMessage.Reasoning("why"),
            new FromHostMessage.Suggestions(new[] { "a" }),
            new FromHostMessage.Assistant("hi"),
            new FromHostMessage.Settings(new { }),
            new FromHostMessage.Catalog(Array.Empty<object>()),
            new FromHostMessage.TestResult(true, "ok"),
            new FromHostMessage.Models(Array.Empty<string>()),
            new FromHostMessage.Plugins(Array.Empty<object>()),
            FromHostMessage.QuotaOf(new QuotaStatus(), "loadingai"),
            new FromHostMessage.History(Array.Empty<FromHostMessage.HistoryMessage>()),
            new FromHostMessage.Error("e"),
            new FromHostMessage.Delta("d"),
        };
        Assert.Equal(FromHostKind.All.Count, messages.Length);
        var types = new HashSet<string>();
        foreach (var m in messages)
        {
            using var doc = JsonDocument.Parse(FromHostMessage.Serialize(m));
            var type = doc.RootElement.GetProperty("type").GetString();
            Assert.Equal(m.Type, type);
            Assert.Contains(type, FromHostKind.All);
            Assert.True(types.Add(type!), type);
        }
        Assert.Equal(FromHostKind.All.Count, types.Count);
    }

    [Fact]
    public void FromHostMessage_SerializeUsesRuntimeType()
    {
        FromHostMessage msg = new FromHostMessage.Status("working");
        var runtime = FromHostMessage.Serialize(msg);
        Assert.Contains("\"text\":\"working\"", runtime);
        var baseOnly = JsonSerializer.Serialize<FromHostMessage>(msg, FromHostMessage.JsonOptions);
        Assert.DoesNotContain("working", baseOnly);
        Assert.Contains("\"type\":\"status\"", baseOnly);
    }

    [Fact]
    public void FromHostMessage_QuotaOmitsTicket()
    {
        var quota = new QuotaStatus { Limit = 100, Used = 3, Remaining = 97, Ticket = "secret-ticket", Ok = true, Message = "ok" };
        var json = FromHostMessage.Serialize(FromHostMessage.QuotaOf(quota, "loadingai"));
        Assert.DoesNotContain("ticket", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"providerId\":\"loadingai\"", json);
        Assert.Contains("\"remaining\":97", json);
    }

    [Fact]
    public void FromHostMessage_ContextUsesActiveView()
    {
        var json = FromHostMessage.Serialize(new FromHostMessage.Context(
            new FromHostMessage.ContextPayload(2, "2 walls", "Level 1", 10, 1, "warn")));
        using var doc = JsonDocument.Parse(json);
        var ctx = doc.RootElement.GetProperty("context");
        Assert.Equal("Level 1", ctx.GetProperty("activeView").GetString());
        Assert.False(ctx.TryGetProperty("activeViewName", out _));
        Assert.False(ctx.TryGetProperty("activeViewType", out _));
    }

    [Fact]
    public void FromHostMessage_PluginsIncludesLoadIssues()
    {
        var issues = new[] { new PluginLoadIssue("f", "pid", "asm.dll", "sum", "det", "hint") };
        using var doc = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.Plugins(Array.Empty<object>(), issues)));
        Assert.Equal("pid", doc.RootElement.GetProperty("loadIssues")[0].GetProperty("pluginId").GetString());
    }

    [Fact]
    public void FromHostMessage_ProgressSerializesIndeterminate()
    {
        using var doc = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.Progress(ProgressSnapshot.Thinking())));
        var p = doc.RootElement.GetProperty("progress");
        Assert.Equal("thinking", p.GetProperty("phase").GetString());
        Assert.True(p.GetProperty("indeterminate").GetBoolean());
    }

    private static void AssertKind<T>(string json) where T : ToHostMessage
    {
        Assert.True(ToHostMessage.TryParse(json, out var msg, out var err), err ?? json);
        Assert.Null(err);
        Assert.IsType<T>(msg);
    }

    private static string Slice(string src, string start, string end)
    {
        var i = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, start);
        var j = src.IndexOf(end, i, StringComparison.Ordinal);
        Assert.True(j > i, end);
        return src.Substring(i, j - i);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln from " + AppContext.BaseDirectory);
    }
}
