// Polyfill for net48 (Revit 2024), which lacks the compiler's "init" accessor support
// type (System.Runtime.CompilerServices.IsExternalInit). net8.0/net10.0 supply it in
// the BCL, so the guard keeps this assembly from declaring a duplicate type there.
// RequiredMemberAttribute and CompilerFeatureRequiredAttribute come along for the same
// reason: the formwork types moved here from a plugin that declared its own copies.
#if NETFRAMEWORK
using System;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; set; }
    }

    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }
}
#endif