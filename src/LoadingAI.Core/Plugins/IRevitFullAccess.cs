using System;
using System.Threading;
using System.Threading.Tasks;

namespace LoadingAI.Plugins;

/// <summary>
/// Opaque handle to the active Revit UI context. Carried revit-free through
/// Core (netstandard2.0, no Autodesk.Revit reference); the App unwraps it
/// back to a Revit UIApplication on the Revit UI thread.
/// </summary>
public sealed class FullAccessSession
{
    private readonly object _payload;

    public FullAccessSession(object payload) => _payload = payload;

    /// <summary>
    /// Casts the opaque Revit context to the Revit type the plugin needs
    /// (e.g. Autodesk.Revit.UI.UIApplication). Plugins that opt into raw
    /// access already reference Autodesk.Revit, so this is safe.
    /// </summary>
    public T As<T>() where T : class => (T)_payload;
}

/// <summary>
/// Opt-in marker. A plugin that implements this interface on its plugin class
/// requests raw (untyped) access to the active Revit session. The host then
/// supplies a non-null <see cref="IPluginContext.FullAccess"/> (typed
/// <see cref="IRawRevitAccess"/>) so the plugin can drive the UIApplication
/// directly. Plugins that do not implement this only see the safe, revit-free
/// <see cref="IRevitBridge"/>.
/// </summary>
/// <remarks>
/// This is intentionally an empty marker: satisfying it must not force a
/// plugin to write code it never calls. The actual capability lives on
/// <see cref="IRawRevitAccess"/>, which the host implements and hands out via
/// <see cref="IPluginContext.FullAccess"/>.
/// </remarks>
public interface IRevitFullAccess
{
}

/// <summary>
/// Host-supplied raw Revit access capability, exposed to opt-in plugins via
/// <see cref="IPluginContext.FullAccess"/>. Implemented by the host (not by
/// the plugin); the plugin only implements the <see cref="IRevitFullAccess"/>
/// marker to request it.
/// </summary>
public interface IRawRevitAccess
{
    /// <summary>
    /// Runs <paramref name="func"/> on the Revit UI thread with the active
    /// session. The returned task completes when the callback returns.
    /// </summary>
    Task<T> RunRevitAsync<T>(
        Func<FullAccessSession, T> func,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}
