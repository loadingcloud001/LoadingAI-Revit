// Polyfill for C# `init` accessors / records on .NET Framework (net48).
// The compiler requires System.Runtime.CompilerServices.IsExternalInit to
// exist in the target assembly; net48 does not ship it.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
