namespace LoadingAI.Llm;

/// <summary>
/// Protects and unprotects sensitive values (e.g. API keys) at rest.
/// The host application provides a platform-specific implementation
/// (e.g. Windows DPAPI via ProtectedData). A no-op fallback keeps the
/// Core buildable on netstandard2.0 where ProtectedData is unavailable.
/// </summary>
public interface IKeyProtector
{
    string ProtectKey(string? key);
    string UnprotectKey(string? stored);
}
