namespace LoadingAI.Tools;

/// <summary>
/// Raised when a caller stops waiting but the underlying operation may still run.
/// The action id is retained by the host so the eventual outcome can be inspected.
/// </summary>
public sealed class BridgeOperationUnknownException : Exception
{
    public string ActionId { get; }

    public BridgeOperationUnknownException(string actionId, string message, Exception? inner = null)
        : base(message, inner)
    {
        ActionId = actionId;
    }
}
