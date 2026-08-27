using LoadingAI.Llm;
using Xunit;

namespace LoadingAI.Tests;

public class DefaultLlmTests : IDisposable
{
    private readonly Func<string?> _prev;

    public DefaultLlmTests()
    {
        _prev = DefaultLlm.KeySource;
        DefaultLlm.KeySource = () => "sk-test-not-real";
    }

    public void Dispose()
    {
        DefaultLlm.KeySource = _prev;
    }

    [Fact]
    public void CreateDefault_ActivatesLoadingAI_WithoutStoringKey()
    {
        var s = ProviderSettings.CreateDefault();
        Assert.Equal(DefaultLlm.Id, s.ActiveProviderId);
        var p = s.Providers.First(x => x.Id == DefaultLlm.Id);
        Assert.Equal(DefaultLlm.DisplayName, p.Name);
        Assert.Equal(DefaultLlm.DisplayModel, p.Model);
        Assert.True(string.IsNullOrEmpty(p.ApiKey));
        Assert.True(string.IsNullOrEmpty(p.BaseUrl) || p.BaseUrl.IndexOf("minimax", StringComparison.OrdinalIgnoreCase) < 0);
    }

    [Fact]
    public void RedactForWeb_HidesEndpointTokenAndRealModel()
    {
        var s = ProviderSettings.CreateDefault();
        var redacted = ProviderSettings.RedactForWeb(s);
        var p = redacted.Providers.First(x => x.Id == DefaultLlm.Id);
        Assert.Equal(ProviderSettings.KeyMask, p.ApiKey);
        Assert.Equal(DefaultLlm.DisplayName, p.Name);
        Assert.Equal(DefaultLlm.DisplayModel, p.Model);
        Assert.DoesNotContain("minimax", (p.BaseUrl ?? "").ToLowerInvariant());
        Assert.DoesNotContain("sk-", p.ApiKey);
        Assert.DoesNotContain("MiniMax", p.Model);
    }

    [Fact]
    public void BindForCall_UsesConfiguredEndpointAndInjectsKey()
    {
        var stored = DefaultLlm.ForSettings();
        var bound = DefaultLlm.BindForCall(stored);
        Assert.Equal("https://api.minimaxi.com/v1", bound.BaseUrl);
        Assert.Equal(DefaultLlm.ApiModel, bound.Model);
        Assert.Equal("sk-test-not-real", bound.ApiKey);
        Assert.Equal("openai", bound.ApiKind);
        Assert.NotEqual(DefaultLlm.DisplayModel, bound.Model);
    }

    [Fact]
    public void MergeFromWeb_CannotInjectKeyOrEndpointIntoBuiltin()
    {
        var current = ProviderSettings.CreateDefault();
        var incoming = new AppSettings
        {
            ActiveProviderId = DefaultLlm.Id,
            Providers = new List<ProviderConfig>
            {
                new()
                {
                    Id = DefaultLlm.Id,
                    Name = "leak",
                    BaseUrl = "https://evil.example",
                    Model = "MiniMax-leak",
                    ApiKey = "sk-stolen"
                }
            }
        };
        var merged = ProviderSettings.MergeFromWeb(current, incoming);
        var p = merged.Providers.First(x => x.Id == DefaultLlm.Id);
        Assert.Equal(DefaultLlm.DisplayName, p.Name);
        Assert.Equal(DefaultLlm.DisplayModel, p.Model);
        Assert.True(string.IsNullOrEmpty(p.ApiKey));
        Assert.DoesNotContain("minimax", (p.BaseUrl ?? "").ToLowerInvariant());
        Assert.DoesNotContain("sk-stolen", p.ApiKey);
    }

    [Fact]
    public void EnsureBuiltin_DoesNotOverrideProviderThatAlreadyHasAKey()
    {
        var s = new AppSettings
        {
            ActiveProviderId = "deepseek",
            Providers = new List<ProviderConfig>
            {
                new() { Id = "deepseek", Name = "DeepSeek", ApiKey = "sk-user", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat" }
            }
        };
        ProviderSettings.Normalize(s);
        Assert.Equal("deepseek", s.ActiveProviderId);
        Assert.Contains(s.Providers, p => p.Id == DefaultLlm.Id);
    }

    [Fact]
    public void BindForCall_LeavesCustomProvidersAlone()
    {
        var p = new ProviderConfig { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini", ApiKey = "sk-user" };
        var bound = DefaultLlm.BindForCall(p);
        Assert.Equal("https://api.openai.com/v1", bound.BaseUrl);
        Assert.Equal("gpt-4o-mini", bound.Model);
        Assert.Equal("sk-user", bound.ApiKey);
    }

    [Fact]
    public void DisplayStrings_DoNotNameTheUpstreamVendor()
    {
        Assert.Equal("loadingai", DefaultLlm.Id);
        Assert.Equal("LoadingAI", DefaultLlm.DisplayName);
        Assert.Equal("LoadingAI", DefaultLlm.DisplayModel);
        Assert.DoesNotContain("minimax", DefaultLlm.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("minimax", DefaultLlm.DisplayModel, StringComparison.OrdinalIgnoreCase);
    }
}
