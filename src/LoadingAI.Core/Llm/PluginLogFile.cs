namespace LoadingAI.Revit;

/// <summary>
/// The shared <c>plugins.log</c>, bounded like every other host log (<see cref="BoundedLog"/>).
/// The loader and every plugin context write through here so no install can grow it without limit.
/// </summary>
public static class PluginLogFile
{
    private static readonly object Gate = new();

    public static string FilePath => BoundedLog.Resolve("plugins.log");

    public static void Append(string line) => BoundedLog.Append(FilePath, line, Gate);
}
