using System.Text.Json.Serialization;

namespace RevitAiPoc.Plugins.Core;

public sealed class PluginMeta
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "0.1.0";
    [JsonPropertyName("license")] public string License { get; set; } = "MIT";
    [JsonPropertyName("author")] public string Author { get; set; } = "Community";
    [JsonPropertyName("entryAssembly")] public string EntryAssembly { get; set; } = "";
    [JsonPropertyName("verified")] public bool Verified { get; set; } = false;
    [JsonPropertyName("tools")] public List<PluginToolMeta> Tools { get; set; } = new();
    // runtime only, not serialized
    [JsonIgnore] public string FolderPath { get; set; } = "";
}

public sealed class PluginToolMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tier")] public string Tier { get; set; } = "L1";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("enabledByDefault")] public bool EnabledByDefault { get; set; } = true;
}
