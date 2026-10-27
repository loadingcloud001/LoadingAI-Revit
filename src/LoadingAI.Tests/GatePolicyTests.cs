using LoadingAI.Gate;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>ADR-0017: one policy decides run, ask or block. These rows are the permission table.</summary>
public class GatePolicyTests
{
    private static GateDecision Decide(
        string level, ToolTier tier, string? mode = "build", bool destructive = false, bool dryRun = false,
        bool presets = false, bool session = false, bool always = false)
        => GatePolicy.Decide(new GateRequest
        {
            Level = level, Mode = mode, Tier = tier, Destructive = destructive, DryRun = dryRun,
            HasPresets = presets, SessionAllowed = session, AlwaysAllowed = always
        });

    [Theory]
    [InlineData("readonly", "ask")]
    [InlineData("plan", "plan")]
    [InlineData("confirm", "build")]
    [InlineData("autoEdit", "build")]
    [InlineData("bypass", "build")]
    public void ReadTools_RunAtEveryLevel(string level, string mode)
        => Assert.Equal(GateDecision.Run, Decide(level, ToolTier.L1, mode));

    [Theory]
    [InlineData("readonly", "ask", ToolTier.L2)]
    [InlineData("readonly", "ask", ToolTier.L3)]
    [InlineData("plan", "plan", ToolTier.L2)]
    [InlineData("plan", "plan", ToolTier.L3)]
    [InlineData("readonly", null, ToolTier.L3)]
    [InlineData("plan", "auto", ToolTier.L2)]
    public void ReadOnlyAndPlan_NeverWrite(string level, string? mode, ToolTier tier)
        => Assert.Equal(GateDecision.Block, Decide(level, tier, mode));

    [Theory]
    [InlineData("readonly")]
    [InlineData("plan")]
    public void TypedBuildAtReadOnlyOrPlan_AsksForEveryWrite(string level)
    {
        // "Run plan" sends /build. It may write, but each write still shows a card.
        Assert.Equal(GateDecision.Ask, Decide(level, ToolTier.L2, "build"));
        Assert.Equal(GateDecision.Ask, Decide(level, ToolTier.L3, "build"));
    }

    [Fact]
    public void AskBeforeChanges_AsksForL2AndL3()
    {
        Assert.Equal(GateDecision.Ask, Decide("confirm", ToolTier.L2));
        Assert.Equal(GateDecision.Ask, Decide("confirm", ToolTier.L3));
    }

    [Fact]
    public void AutoEdit_RunsL2_AsksL3()
    {
        Assert.Equal(GateDecision.Run, Decide("autoEdit", ToolTier.L2));
        Assert.Equal(GateDecision.Ask, Decide("autoEdit", ToolTier.L3));
    }

    [Fact]
    public void AutoEdit_StillAsksForADestructiveL2Tool()
        => Assert.Equal(GateDecision.Ask, Decide("autoEdit", ToolTier.L2, destructive: true));

    [Theory]
    [InlineData(ToolTier.L2, false)]
    [InlineData(ToolTier.L3, false)]
    [InlineData(ToolTier.L3, true)]
    public void Bypass_RunsEverything(ToolTier tier, bool destructive)
        => Assert.Equal(GateDecision.Run, Decide("bypass", tier, destructive: destructive));

    [Theory]
    [InlineData("confirm")]
    [InlineData("autoEdit")]
    [InlineData("bypass")]
    [InlineData("readonly")]
    public void DryRun_RunsAtEveryLevel(string level)
        => Assert.Equal(GateDecision.Run, Decide(level, ToolTier.L3, ModeFor(level), dryRun: true));

    private static string ModeFor(string level) => PermissionLevels.ModeFor(level);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AllowedTool_RunsWithoutACard(bool session, bool always)
        => Assert.Equal(GateDecision.Run, Decide("confirm", ToolTier.L3, session: session, always: always));

    [Fact]
    public void AllowList_NeverCoversADestructiveTool()
    {
        Assert.Equal(GateDecision.Ask, Decide("confirm", ToolTier.L3, destructive: true, session: true, always: true));
        Assert.Equal(GateDecision.Ask, Decide("autoEdit", ToolTier.L3, destructive: true, always: true));
    }

    [Fact]
    public void AllowList_NeverSkipsARunForm()
        => Assert.Equal(GateDecision.Ask, Decide("confirm", ToolTier.L3, presets: true, session: true, always: true));

    [Fact]
    public void AllowList_DoesNotLiftReadOnlyOrPlan()
    {
        Assert.Equal(GateDecision.Block, Decide("readonly", ToolTier.L3, "ask", always: true));
        Assert.Equal(GateDecision.Block, Decide("plan", ToolTier.L2, "plan", session: true));
    }

    [Fact]
    public void UnknownLevel_FallsBackToAsking()
        => Assert.Equal(GateDecision.Ask, Decide("godmode", ToolTier.L3));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void CanOfferAlways_OnlyForPlainNonDestructiveTools(bool destructive, bool presets, bool expected)
        => Assert.Equal(expected, GatePolicy.CanOfferAlways(destructive, presets));

    [Fact]
    public void NeverAllowList_IgnoresAllowLists_AndAsksAtAutoEdit()
    {
        // Read only only stays read only outside a typed /build, so the mode is a parameter here.
        GateDecision D(string level, string mode = "build", bool session = false, bool always = false) => GatePolicy.Decide(new GateRequest
        {
            Level = level, Mode = mode, Tier = ToolTier.L2, NeverAllowList = true, SessionAllowed = session, AlwaysAllowed = always
        });
        Assert.Equal(GateDecision.Ask, D("confirm", session: true));
        Assert.Equal(GateDecision.Ask, D("confirm", always: true));
        Assert.Equal(GateDecision.Ask, D("autoEdit"));
        Assert.Equal(GateDecision.Run, D("bypass"));
        Assert.Equal(GateDecision.Block, D("readonly", mode: "ask"));
        Assert.False(GatePolicy.CanOfferAlways(destructive: false, hasPresets: false, neverAllowList: true));
    }
}

public class PermissionLevelsTests
{
    [Theory]
    [InlineData("readonly", false, "readonly")]
    [InlineData("autoEdit", true, "autoEdit")]
    [InlineData(null, true, "bypass")]
    [InlineData(null, false, "confirm")]
    [InlineData("nonsense", false, "confirm")]
    [InlineData("nonsense", true, "bypass")]
    public void Resolve_PrefersAValidLevel_ElseTheLegacySwitch(string? level, bool legacy, string expected)
        => Assert.Equal(expected, PermissionLevels.Resolve(level, legacy));

    [Theory]
    [InlineData("readonly", "ask")]
    [InlineData("plan", "plan")]
    [InlineData("confirm", "build")]
    [InlineData("autoEdit", "build")]
    [InlineData("bypass", "build")]
    public void ModeFor_MatchesThePane(string level, string mode)
        => Assert.Equal(mode, PermissionLevels.ModeFor(level));

    [Fact]
    public void EffectiveFor_OnlyRaisesReadOnlyAndPlanWhenTheUserTypedBuild()
    {
        Assert.Equal("confirm", PermissionLevels.EffectiveFor("readonly", "build"));
        Assert.Equal("confirm", PermissionLevels.EffectiveFor("plan", "BUILD"));
        Assert.Equal("readonly", PermissionLevels.EffectiveFor("readonly", "ask"));
        Assert.Equal("bypass", PermissionLevels.EffectiveFor("bypass", "build"));
        Assert.Equal("autoEdit", PermissionLevels.EffectiveFor("autoEdit", null));
        Assert.Equal("confirm", PermissionLevels.EffectiveFor("junk", "build"));
    }
}
