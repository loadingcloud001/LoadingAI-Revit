#if NETFRAMEWORK
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
