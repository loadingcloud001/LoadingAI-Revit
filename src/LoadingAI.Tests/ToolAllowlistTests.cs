using LoadingAI.Gate;
using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class ToolAllowlistTests
{
    [Fact]
    public void SessionAllow_IsPerTool_CaseInsensitive_AndClearable()
    {
        var list = new ToolAllowlist();
        Assert.False(list.IsSessionAllowed("set_parameter_by_id"));
        list.AddSession("set_parameter_by_id");
        Assert.True(list.IsSessionAllowed("SET_PARAMETER_BY_ID"));
        Assert.False(list.IsSessionAllowed("delete_elements"));
        list.ClearSession();
        Assert.False(list.IsSessionAllowed("set_parameter_by_id"));
    }

    [Fact]
    public void BlankNames_AreIgnored()
    {
        var list = new ToolAllowlist();
        list.AddSession("  ");
        list.AddSession("");
        Assert.Empty(list.SessionSnapshot());
    }

    [Theory]
    [InlineData("once", ApprovalScope.Once)]
    [InlineData("session", ApprovalScope.Session)]
    [InlineData("ALWAYS", ApprovalScope.Always)]
    [InlineData(null, ApprovalScope.Once)]
    [InlineData("forever", ApprovalScope.Once)]
    public void Scope_UnknownMeansTheNarrowestOne(string? text, ApprovalScope expected)
        => Assert.Equal(expected, ApprovalScopes.Parse(text));

    [Fact]
    public void MergeAlways_PaneCanRemoveButNeverAdd()
    {
        var current = new[] { "set_comments_on_selection", "numbering_auto" };
        var incoming = new[] { "numbering_auto", "delete_elements", "NUMBERING_AUTO", " " };
        Assert.Equal(new[] { "numbering_auto" }, ToolAllowlist.MergeAlways(current, incoming));
    }

    [Fact]
    public void MergeAlways_NullsAreEmpty()
        => Assert.Empty(ToolAllowlist.MergeAlways(null, null));
}

public class SettingsLevelTests
{
    private static AppSettings WithProvider(Action<AppSettings>? tweak = null)
    {
        var s = new AppSettings
        {
            ActiveProviderId = "deepseek",
            Providers = new List<ProviderConfig> { new() { Id = "deepseek", Name = "DeepSeek", ApiKey = "sk-secret" } }
        };
        tweak?.Invoke(s);
        return s;
    }

    [Fact]
    public void OldFileWithAutoApprove_BecomesBypass()
    {
        var s = ProviderSettings.Normalize(WithProvider(x => x.AutoApprove = true));
        Assert.Equal("bypass", s.PermissionLevel);
        Assert.True(s.AutoApprove);
    }

    [Fact]
    public void OldFileWithoutAutoApprove_BecomesAskBeforeChanges()
    {
        var s = ProviderSettings.Normalize(WithProvider());
        Assert.Equal("confirm", s.PermissionLevel);
        Assert.False(s.AutoApprove);
    }

    [Fact]
    public void AutoApprove_FollowsTheLevel()
    {
        var s = ProviderSettings.Normalize(WithProvider(x => { x.PermissionLevel = "autoEdit"; x.AutoApprove = true; }));
        Assert.Equal("autoEdit", s.PermissionLevel);
        Assert.False(s.AutoApprove);
    }

    [Fact]
    public void Merge_APaneThatSendsALevel_Wins()
    {
        var current = WithProvider(x => x.PermissionLevel = "confirm");
        var incoming = WithProvider(x => x.PermissionLevel = "bypass");
        var merged = ProviderSettings.MergeFromWeb(current, incoming);
        Assert.Equal("bypass", merged.PermissionLevel);
        Assert.True(merged.AutoApprove);
    }

    [Fact]
    public void Merge_AnOlderPaneThatOnlySendsAutoApprove_TogglesBypass()
    {
        var current = WithProvider(x => x.PermissionLevel = "autoEdit");
        var on = ProviderSettings.MergeFromWeb(current, WithProvider(x => x.AutoApprove = true));
        Assert.Equal("bypass", on.PermissionLevel);

        var off = ProviderSettings.MergeFromWeb(on, WithProvider(x => x.AutoApprove = false));
        Assert.Equal("confirm", off.PermissionLevel);

        var keep = ProviderSettings.MergeFromWeb(current, WithProvider());
        Assert.Equal("autoEdit", keep.PermissionLevel);
    }

    [Fact]
    public void Merge_AnInvalidLevelIsIgnored()
    {
        var current = WithProvider(x => x.PermissionLevel = "plan");
        var merged = ProviderSettings.MergeFromWeb(current, WithProvider(x => x.PermissionLevel = "godmode"));
        Assert.Equal("plan", merged.PermissionLevel);
    }

    [Fact]
    public void Merge_PaneCannotAddToTheAlwaysList_ButCanRemove()
    {
        var current = WithProvider(x => x.AlwaysAllowTools = new List<string> { "numbering_auto", "set_comments_on_selection" });
        var incoming = WithProvider(x => x.AlwaysAllowTools = new List<string> { "numbering_auto", "delete_elements" });
        var merged = ProviderSettings.MergeFromWeb(current, incoming);
        Assert.Equal(new[] { "numbering_auto" }, merged.AlwaysAllowTools.ToArray());
    }

    [Fact]
    public void Merge_CarriesInstructionsAndTheSelectSwitch_Sanitised()
    {
        var current = WithProvider();
        var incoming = WithProvider(x => { x.Instructions = "  Use mm.\u0000 "; x.SelectChangedAfterTurn = false; });
        var merged = ProviderSettings.MergeFromWeb(current, incoming);
        Assert.Equal("Use mm.", merged.Instructions);
        Assert.False(merged.SelectChangedAfterTurn);
    }

    [Fact]
    public void RedactForWeb_ShowsTheLevelAndTheAlwaysList_NotTheKey()
    {
        var s = WithProvider(x => { x.PermissionLevel = "autoEdit"; x.AlwaysAllowTools = new List<string> { "numbering_auto" }; x.Instructions = "Use mm."; });
        var web = ProviderSettings.RedactForWeb(s);
        Assert.Equal("autoEdit", web.PermissionLevel);
        Assert.Equal(new[] { "numbering_auto" }, web.AlwaysAllowTools.ToArray());
        Assert.Equal("Use mm.", web.Instructions);
        Assert.NotEqual("sk-secret", web.Providers[0].ApiKey);
    }

    [Fact]
    public void RedactForWeb_ForAnOldFile_ReportsBypassFromAutoApprove()
    {
        var web = ProviderSettings.RedactForWeb(WithProvider(x => x.AutoApprove = true));
        Assert.Equal("bypass", web.PermissionLevel);
        Assert.True(web.AutoApprove);
    }

    [Fact]
    public void NewDefaults_AreAskBeforeChanges_AndSelectChanged()
    {
        var s = ProviderSettings.CreateDefault();
        Assert.Equal("confirm", s.PermissionLevel);
        Assert.True(s.SelectChangedAfterTurn);
        Assert.Empty(s.AlwaysAllowTools);
    }
}
