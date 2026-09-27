namespace LoadingAI.Tools;

public enum ToolExecutionStatus
{
    Succeeded,
    Partial,
    Failed,
    Cancelled,
    Unknown
}

public enum ToolWriteCertainty
{
    NoWrite,
    Committed,
    NotCommitted,
    Unknown
}

/// <summary>
/// Authoritative host-facing result for one tool call. Text remains the model-facing
/// report, while status and write certainty drive audit and transaction decisions.
/// </summary>
public sealed record ToolExecutionResult(
    string Text,
    ToolExecutionStatus Status,
    ToolWriteCertainty WriteCertainty,
    int ChangedCount = 0,
    int FailedCount = 0)
{
    public bool IsSafeToAssimilate =>
        (Status == ToolExecutionStatus.Succeeded || Status == ToolExecutionStatus.Partial)
        && WriteCertainty == ToolWriteCertainty.Committed;

    public static ToolExecutionResult Succeeded(
        string text,
        ToolWriteCertainty writeCertainty = ToolWriteCertainty.NoWrite,
        int changedCount = 0,
        int failedCount = 0)
        => new(text ?? "", ToolExecutionStatus.Succeeded, writeCertainty, changedCount, failedCount);

    public static ToolExecutionResult Partial(
        string text,
        ToolWriteCertainty writeCertainty,
        int changedCount = 0,
        int failedCount = 0)
        => new(text ?? "", ToolExecutionStatus.Partial, writeCertainty, changedCount, failedCount);

    public static ToolExecutionResult Failed(
        string text,
        ToolWriteCertainty writeCertainty = ToolWriteCertainty.Unknown,
        int changedCount = 0,
        int failedCount = 0)
        => new(text ?? "", ToolExecutionStatus.Failed, writeCertainty, changedCount, failedCount);

    public static ToolExecutionResult Cancelled(
        string text,
        ToolWriteCertainty writeCertainty = ToolWriteCertainty.Unknown)
        => new(text ?? "", ToolExecutionStatus.Cancelled, writeCertainty);

    public static ToolExecutionResult Unknown(
        string text,
        ToolWriteCertainty writeCertainty = ToolWriteCertainty.Unknown)
        => new(text ?? "", ToolExecutionStatus.Unknown, writeCertainty);

    internal static ToolExecutionResult FromLegacy(string text, ToolTier tier)
    {
        text ??= "";
        if (text.Length == 0)
            return Failed("Tool returned no result.");

        if (text.StartsWith("Cancelled", System.StringComparison.OrdinalIgnoreCase))
            return Cancelled(text);

        if (text.StartsWith("Tool error", System.StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Failed:", System.StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Tool ", System.StringComparison.OrdinalIgnoreCase) && text.Contains(" failed:", System.StringComparison.OrdinalIgnoreCase))
            return Failed(text);

        return tier == ToolTier.L1
            ? Succeeded(text, ToolWriteCertainty.NoWrite)
            : Succeeded(text, ToolWriteCertainty.Committed);
    }
}
