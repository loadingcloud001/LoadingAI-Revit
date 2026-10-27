namespace LoadingAI.Llm;

/// <summary>Whether a provider's model can read images (P1). The user's switch wins; otherwise a name guess. Pure.</summary>
public static class VisionSupport
{
    private static readonly string[] Hints =
    {
        "gpt-4o", "gpt-4.1", "gpt-5", "o3", "o4", "claude", "gemini", "-vl", "vl-", "vision", "llava", "pixtral", "grok-4"
    };

    public static bool Supports(ProviderConfig? p)
    {
        if (p == null) return false;
        if (p.SupportsImages.HasValue) return p.SupportsImages.Value;
        var m = (p.Model ?? "").ToLowerInvariant();
        if (m.Length == 0) return false;
        foreach (var h in Hints)
            if (m.Contains(h)) return true;
        return false;
    }
}
