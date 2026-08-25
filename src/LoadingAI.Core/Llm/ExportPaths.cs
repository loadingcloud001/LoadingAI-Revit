using System.IO;

namespace LoadingAI.Revit;

public static class ExportPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LoadingAI", "exports");

    public static string ResolveWrite(string? userPath, string defaultFileName)
    {
        Directory.CreateDirectory(Root);
        if (string.IsNullOrWhiteSpace(userPath))
            return Path.Combine(Root, defaultFileName);

        var candidate = Path.IsPathRooted(userPath)
            ? Path.GetFullPath(userPath)
            : Path.GetFullPath(Path.Combine(Root, userPath));

        if (!IsUnderRoot(candidate))
            throw new InvalidOperationException($"Path must be under {Root}");

        var dir = Path.GetDirectoryName(candidate);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return candidate;
    }

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

    public static string ResolveDirectory(string? userFolder)
    {
        Directory.CreateDirectory(Root);
        if (string.IsNullOrWhiteSpace(userFolder)) return Root;
        var candidate = Path.IsPathRooted(userFolder)
            ? Path.GetFullPath(userFolder)
            : Path.GetFullPath(Path.Combine(Root, userFolder));
        if (!IsUnderRoot(candidate))
            throw new InvalidOperationException($"Folder must be under {Root}");
        Directory.CreateDirectory(candidate);
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

    public static string SanitizeFileName(string? name, string fallback = "export")
    {
        var raw = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        if (raw.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(0, raw.Length - 4);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        cleaned = cleaned.Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }
}
