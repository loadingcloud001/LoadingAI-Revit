using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class SettingsMergeTests
{
    [Fact]
    public void Merge_PreservesPluginsWhenIncomingEmpty()
    {
        var current = new AppSettings
        {
            ActiveProviderId = "deepseek",
            Plugins = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["delete_elements"] = true },
            Providers = new List<ProviderConfig> { new() { Id = "deepseek", Name = "DeepSeek", ApiKey = "sk-secret" } }
        };
        var incoming = new AppSettings
        {
            ActiveProviderId = "deepseek",
            Providers = new List<ProviderConfig> { new() { Id = "deepseek", Name = "DeepSeek", ApiKey = ProviderSettings.KeyMask } }
        };
        var merged = ProviderSettings.MergeFromWeb(current, incoming);
        Assert.True(merged.Plugins["delete_elements"]);
        Assert.Equal("sk-secret", merged.Providers.First(p => p.Id == "deepseek").ApiKey);
    }

    [Fact]
    public void Merge_IgnoresStaleIncomingPlugins()
    {
        var current = new AppSettings
        {
            ActiveProviderId = "deepseek",
            Plugins = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["delete_elements"] = true },
            Providers = new List<ProviderConfig> { new() { Id = "deepseek", Name = "DeepSeek", ApiKey = "sk-secret" } }
        };
        var incoming = new AppSettings
        {
            ActiveProviderId = "deepseek",
            Plugins = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["delete_elements"] = false },
            Providers = new List<ProviderConfig> { new() { Id = "deepseek", Name = "DeepSeek", ApiKey = ProviderSettings.KeyMask } }
        };
        var merged = ProviderSettings.MergeFromWeb(current, incoming);
        Assert.True(merged.Plugins["delete_elements"]);
    }

    [Fact]
    public void RedactForWeb_DoesNotAliasStoredProvider()
    {
        var s = new AppSettings
        {
            Providers = new List<ProviderConfig> { new() { Id = "openai", Name = "OpenAI", ApiKey = "sk-live", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini" } }
        };
        var redacted = ProviderSettings.RedactForWeb(s);
        redacted.Providers[0].ApiKey = "mutated";
        redacted.Providers[0].BaseUrl = "https://evil.example";
        Assert.Equal("sk-live", s.Providers[0].ApiKey);
        Assert.Equal("https://api.openai.com/v1", s.Providers[0].BaseUrl);
    }

    [Fact]
    public void Redact_MasksKeys()
    {
        var s = new AppSettings
        {
            Providers = new List<ProviderConfig> { new() { Id = "a", ApiKey = "sk-live" } }
        };
        var redacted = ProviderSettings.RedactForWeb(s);
        Assert.Equal(ProviderSettings.KeyMask, redacted.Providers[0].ApiKey);
        Assert.Equal("sk-live", s.Providers[0].ApiKey);
    }

    [Fact]
    public void IsMaskedKey()
    {
        Assert.True(ProviderSettings.IsMaskedKey("••••••••"));
        Assert.False(ProviderSettings.IsMaskedKey("sk-abc"));
    }
}
