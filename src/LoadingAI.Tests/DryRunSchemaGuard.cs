using System.Text.RegularExpressions;

namespace LoadingAI.Tests;

/// <summary>
/// Detects a tool schema that declares a <c>dryRun</c> property without ever handling it.
/// <para>
/// Why this matters: <c>ChatSession</c> skips <b>both</b> L3 gates whenever the merged args
/// JSON carry a truthy <c>dryRun</c> (ADR-0004, generic check by design). That is only safe
/// because every tool that declares the key also honours it and writes nothing. A tool that
/// advertises <c>dryRun</c> and ignores it lets any caller strip the web approval card and the
/// native <c>Confirm L3:</c> dialog off a destructive write by passing one boolean.
/// </para>
/// <para>
/// Schema property bindings are C# anonymous-object members (<c>dryRun = new { ... }</c> or
/// <c>dryRun = DryRunProperty()</c>), not JSON, so detection keys off the binding. Prose that
/// merely mentions <c>dryRun</c> in a description is not a declaration.
/// </para>
/// </summary>
public static class DryRunSchemaGuard
{
    private static readonly Regex Declaration = new(
        "(?m)^\\s*dryRun\\s*=|\"dryRun\"\\s*:",
        RegexOptions.Compiled);

    private static readonly string[] Handling =
    {
        "ToolPresets.IsDryRun(",
        "Bool(\"dryRun\")"
    };

    /// <summary>True when <paramref name="src"/> declares a dryRun schema property.</summary>
    public static bool Declares(string src)
        => !string.IsNullOrEmpty(src) && Declaration.IsMatch(src);

    /// <summary>True when <paramref name="src"/> declares a dryRun schema property and never handles it.</summary>
    public static bool IsUnhandled(string src)
    {
        if (!Declares(src)) return false;
        return !Array.Exists(Handling, h => src.Contains(h));
    }
}
