namespace LoadingAI
{
    /// <summary>
    /// Small net48 (Revit 2024) compatibility helpers. net48 lacks Math.Clamp,
    /// StringSplitOptions.TrimEntries, string.Contains(StringComparison), and
    /// Enumerable.Contains(predicate). Kept unconditional so all builds compile
    /// identically; on modern TFMs members simply delegate to the BCL.
    /// </summary>
    internal static class Polyfills
    {
        public static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        public static long Clamp(long value, long min, long max)
            => value < min ? min : (value > max ? max : value);

        public static double Clamp(double value, double min, double max)
            => value < min ? min : (value > max ? max : value);

        public static string[] SplitTrim(this string value, char separator)
            => value.Split(separator)
                    .Select(s => s.Trim())
                    .Where(s => s.Length != 0)
                    .ToArray();
    }
}

