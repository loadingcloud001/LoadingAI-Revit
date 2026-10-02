using LoadingAI.Llm;
using Xunit;

namespace LoadingAI.Tests;

[Collection("builtin-provider")]
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
    public void BindForCall_UsesTheGateway_WithoutSendingAKeyOfItsOwn()
    {
        var stored = DefaultLlm.ForSettings();
        var bound = DefaultLlm.BindForCall(stored);
        Assert.Equal(DefaultLlm.GatewayBaseUrl, bound.BaseUrl);
        Assert.Equal(DefaultLlm.ApiModel, bound.Model);
        Assert.True(string.IsNullOrEmpty(bound.ApiKey));
        Assert.Equal(DefaultLlm.ApiKind, bound.ApiKind);
        Assert.NotEqual(DefaultLlm.DisplayModel, bound.Model);
        Assert.DoesNotContain("minimax", bound.BaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApiBaseUrl_PointsAtTheGateway_UnlessDirectModeIsSwitchedOn()
    {
        Assert.Equal(DefaultLlm.GatewayBaseUrl, DefaultLlm.ApiBaseUrl);
        Assert.False(DefaultLlm.DirectUpstreamEnabled);
    }

    [Fact]
    public void DirectUpstream_RequiresBothTheFlagAndALocallySuppliedKey()
    {
        var previous = Environment.GetEnvironmentVariable(DefaultLlm.DirectModeEnvVar);
        var previousKey = DefaultLlm.KeySource;
        try
        {
            Environment.SetEnvironmentVariable(DefaultLlm.DirectModeEnvVar, "1");
            DefaultLlm.KeySource = () => null;
            Assert.False(DefaultLlm.DirectUpstreamEnabled);
            Assert.Equal(DefaultLlm.GatewayBaseUrl, DefaultLlm.ApiBaseUrl);

            DefaultLlm.KeySource = () => "sk-test-not-real";
            Assert.True(DefaultLlm.DirectUpstreamEnabled);
            Assert.Equal(DefaultLlm.DirectApiBaseUrl, DefaultLlm.ApiBaseUrl);
            Assert.Equal("sk-test-not-real", DefaultLlm.BindForCall(DefaultLlm.ForSettings()).ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultLlm.DirectModeEnvVar, previous);
            DefaultLlm.KeySource = previousKey;
        }
    }

    [Fact]
    public void GatewayBaseUrl_FallsBackToTheShippedEndpoint_WhenCleared()
    {
        var previous = DefaultLlm.GatewayBaseUrl;
        try
        {
            DefaultLlm.GatewayBaseUrl = "https://example.test/v1/";
            Assert.Equal("https://example.test/v1", DefaultLlm.ApiBaseUrl);
            DefaultLlm.GatewayBaseUrl = "  ";
            Assert.Equal(DefaultLlm.PublicGatewayBaseUrl, DefaultLlm.ApiBaseUrl);
        }
        finally
        {
            DefaultLlm.GatewayBaseUrl = previous;
        }
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
        bound.ApiKey = "mutated";
        Assert.Equal("sk-user", p.ApiKey);
        Assert.False(ReferenceEquals(p, bound));
    }

    [Fact]
    public void GetActiveForCall_BindsBuiltinWire()
    {
        var s = ProviderSettings.CreateDefault();
        var bound = ProviderSettings.GetActiveForCall(s);
        Assert.NotNull(bound);
        Assert.Equal(DefaultLlm.GatewayBaseUrl, bound!.BaseUrl);
        Assert.Equal(DefaultLlm.ApiModel, bound.Model);
        Assert.True(string.IsNullOrEmpty(bound.ApiKey));
        Assert.True(string.IsNullOrEmpty(ProviderSettings.GetActive(s)!.ApiKey));
    }

    [Fact]
    public void DisplayStrings_DoNotNameTheUpstreamVendor()
    {
        Assert.Equal("loadingai", DefaultLlm.Id);
        Assert.Equal("LoadingAI", DefaultLlm.DisplayName);
        Assert.Equal("LoadingAI", DefaultLlm.DisplayModel);
        Assert.Equal(DefaultLlm.GatewayBaseUrl, DefaultLlm.ApiBaseUrl);
        Assert.Equal("MiniMax-M3.1-Flash-Preview", DefaultLlm.ApiModel);
        Assert.DoesNotContain("minimax", DefaultLlm.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("minimax", DefaultLlm.DisplayModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("minimax", DefaultLlm.ApiBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_DropsProvidersThatNameTheUpstream()
    {
        var s = new AppSettings
        {
            ActiveProviderId = "minimax-custom",
            Providers = new List<ProviderConfig>
            {
                new() { Id = "deepseek", Name = "DeepSeek", ApiKey = "sk-user", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat" },
                new() { Id = "minimax-custom", Name = "MiniMax", BaseUrl = DefaultLlm.ApiBaseUrl, Model = DefaultLlm.ApiModel, ApiKey = "sk-user" }
            }
        };
        ProviderSettings.Normalize(s);
        Assert.DoesNotContain(s.Providers, p => p.Id == "minimax-custom");
        Assert.DoesNotContain(s.Providers, p => (p.Name + p.Model + p.BaseUrl).IndexOf("minimax", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.Equal(DefaultLlm.Id, s.ActiveProviderId);
        Assert.Contains(s.Providers, p => p.Id == "deepseek");
    }

    [Fact]
    public void RedactForWeb_OmitsProvidersThatNameTheUpstream()
    {
        var s = ProviderSettings.CreateDefault();
        s.Providers.Add(new ProviderConfig { Id = "leak", Name = "MiniMax", BaseUrl = DefaultLlm.ApiBaseUrl, Model = DefaultLlm.ApiModel });
        var redacted = ProviderSettings.RedactForWeb(s);
        Assert.DoesNotContain(redacted.Providers, p => (p.Name + p.Model + p.BaseUrl).IndexOf("minimax", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
