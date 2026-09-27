namespace LoadingAI.Core.Openings;

/// <summary>Revit-free key filter shared by plan and place scope handling.</summary>
public static class OpeningLevelFilter
{
    public static List<T> FilterByKey<T>(IEnumerable<T> items, Func<T, string> keySelector, string? forcedKey)
        => forcedKey == null
            ? items.ToList()
            : items.Where(item => string.Equals(keySelector(item), forcedKey, StringComparison.Ordinal)).ToList();
}
