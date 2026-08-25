namespace LoadingAI.Tools;

public static class ToolDiagnostics
{
    /// <summary>
    /// Explains why a requested tool cannot run: disabled (with owning plugin),
    /// unknown with a close-match suggestion, or plain unknown.
    /// </summary>
    public static string DescribeMissing(
        string requestedName,
        IReadOnlyCollection<string> enabledToolNames,
        IReadOnlyCollection<(string PluginId, string ToolName)> discoveredTools)
    {
        foreach (var (pluginId, toolName) in discoveredTools)
        {
            if (!toolName.Equals(requestedName, StringComparison.OrdinalIgnoreCase)) continue;
            bool enabled = enabledToolNames.Any(n => n.Equals(toolName, StringComparison.OrdinalIgnoreCase));
            if (enabled)
                return $"Tool '{toolName}' exists and is enabled but was not offered in this request. Call it again using the exact name '{toolName}'.";
            return $"Tool '{toolName}' is currently DISABLED (plugin '{pluginId}'). Tell the user to open the Plugins tab, enable it, then retry.";
        }

        var best = enabledToolNames
            .Select(n => (Name: n, Score: Similarity(n, requestedName)))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best.Name != null && best.Score >= SuggestionThreshold)
            return $"Tool '{requestedName}' does not exist. Did you mean '{best.Name}'?";
        return $"Tool '{requestedName}' does not exist in this installation. Use only tools from your Enabled tools list.";
    }

    public const int SuggestionThreshold = 55;

    /// <summary>Normalized Levenshtein similarity, 0..100.</summary>
    public static int Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 100;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 100;

        var dp = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) dp[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = dp[0];
            dp[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int temp = dp[j];
                dp[j] = Math.Min(
                    Math.Min(dp[j] + 1, dp[j - 1] + 1),
                    prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = temp;
            }
        }

        int dist = dp[b.Length];
        int maxLen = Math.Max(a.Length, b.Length);
        return (int)Math.Round(100.0 * (maxLen - dist) / maxLen);
    }
}
