// Polyfill for netstandard2.0 / older runtimes that lack the compiler's
// "init" accessor support type (System.Runtime.CompilerServices.IsExternalInit).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
