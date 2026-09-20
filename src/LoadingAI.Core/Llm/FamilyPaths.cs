using System.IO;

namespace LoadingAI.Revit;

public static class FamilyPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LoadingAI", "families");

    public static string ResolveRead(string userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            throw new InvalidOperationException("filePath is required.");
        var candidate = Path.IsPathRooted(userPath)
            ? Path.GetFullPath(userPath)
            : Path.GetFullPath(Path.Combine(Root, userPath));
        if (!IsUnderRoot(candidate))
            throw new InvalidOperationException($"Path must be under {Root}");
        return candidate;
    }

    public static bool IsUnderRoot(string fullPath)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(fullPath);
        if (target.Equals(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) return true;
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
