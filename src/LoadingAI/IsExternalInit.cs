// Polyfill for net48 (Revit 2024), which lacks the compiler's "init" accessor support
// type (System.Runtime.CompilerServices.IsExternalInit). net8.0/net10.0 supply it in
// the BCL, so the guard keeps this assembly from declaring a duplicate type there.
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif