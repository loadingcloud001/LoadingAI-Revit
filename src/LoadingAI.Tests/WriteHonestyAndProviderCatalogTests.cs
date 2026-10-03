using System.IO;
using System.Collections.Generic;
using System.Linq;
using LoadingAI.Llm;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Regression cover for the write-honesty and provider-catalog bugs found in the
/// 2026-09-29 review. The Adapter cases are source-level because they need a live
/// Revit Document; the catalog cases are behavioural.
/// </summary>
public class WriteHonestyAndProviderCatalogTests
{
    /// <summary>ApiKinds the HTTP client actually speaks. "anthropic" (/messages) is not one of them.</summary>
    private static readonly HashSet<string> ImplementedApiKinds =
        new(System.StringComparer.OrdinalIgnoreCase) { "openai", "responses" };

    // Bug 1 (2026-09-29): RotateElementsAsync swallowed per-element failures with `catch { }` and then
    // returned Ok=true with the full requested count, so a rotate that moved 9 of 12 elements reported
    // "Rotated 12 elements". The set is now rotated in one call: Revit rotates every element or throws,
    // and the throw rolls the transaction back.
    [Fact]
    public void RotateElements_IsAllOrNothing()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> RotateElementsAsync");
        var i = src.IndexOf("public Task<CommandResultDto> RotateElementsAsync", System.StringComparison.Ordinal);
        Assert.True(i >= 0, "RotateElementsAsync not found");
        var end = src.IndexOf("public Task<CommandResultDto> MirrorElementsAsync", i, System.StringComparison.Ordinal);
        Assert.True(end > i, "could not bound the RotateElementsAsync slice");
        var slice = src.Substring(i, end - i);

        Assert.DoesNotContain("foreach (var id in eids)", slice);
        Assert.DoesNotContain("RotateElement(doc, id, axis, rad)", slice);

        var rotate = slice.IndexOf("ElementTransformUtils.RotateElements(doc, eids, axis, rad);", System.StringComparison.Ordinal);
        Assert.True(rotate >= 0, "the set must be rotated in one call");
        var commit = slice.IndexOf("tx.Commit();", rotate, System.StringComparison.Ordinal);
        Assert.True(commit > rotate, "the commit follows the rotate");
        var rollback = slice.IndexOf("tx.RollBack();", commit, System.StringComparison.Ordinal);
        Assert.True(rollback > commit, "a throw from the rotate lands in the catch that rolls back");

        // The success count is only reachable on the all-rotated path.
        Assert.Contains("$\"Rotated {eids.Count} elements", slice);
    }

    // Bug 1 companion: ReloadLinksAsync already reports ok/total honestly. Lock it
    // so the rotate fix does not get copy-pasted into a regression here.
    [Fact]
    public void ReloadLinks_ReportsActualSuccessCount()
    {
        var src = AdapterSource.All();
        Assert.Contains("$\"Reloaded {ok}/{links.Count} RVT links\"", src);
    }

    // Bug 2: the catalog advertised a direct Anthropic provider whose /messages
    // transport the client explicitly rejects, so users could configure and "test"
    // a provider that could never complete a chat.
    [Fact]
    public void NoCatalogPresetAdvertisesAnUnimplementedTransport()
    {
        foreach (var preset in ProviderCatalog.Presets)
        {
            Assert.True(
                ImplementedApiKinds.Contains(preset.ApiKind ?? ""),
                $"preset '{preset.Id}' uses ApiKind '{preset.ApiKind}', which the client does not implement");
        }
    }

    [Fact]
    public void AnthropicGuidancePointsAtAWorkingEndpoint()
    {
        var anthropic = ProviderCatalog.Find("anthropic");
        Assert.NotNull(anthropic);
        Assert.True(ImplementedApiKinds.Contains(anthropic!.ApiKind ?? ""));
        Assert.Contains("openrouter", anthropic.BaseUrl, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openrouter", anthropic.Description, System.StringComparison.OrdinalIgnoreCase);
    }

    // The rejected-transport guidance must name a configuration that works.
    [Fact]
    public void AnthropicRejectionMessageNamesAWorkingConfiguration()
    {
        var client = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI.Core", "Llm", "OpenAiCompatibleClient.cs"));
        Assert.Contains("openrouter.ai/api/v1", client, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anthropic/claude", client, System.StringComparison.OrdinalIgnoreCase);
    }

}
