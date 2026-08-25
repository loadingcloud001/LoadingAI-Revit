using System.Reflection;
using System.Runtime.Loader;

namespace LoadingAI.Plugins.Core;

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name ?? "";
        if (name.StartsWith("LoadingAI", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Autodesk.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("RevitAPI", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("RevitAPIUI", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
