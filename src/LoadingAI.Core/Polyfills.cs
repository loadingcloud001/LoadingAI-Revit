namespace LoadingAI
{
    /// <summary>
    /// Small net48 / netstandard2.0 compatibility helpers. net48 lacks Math.Clamp
    /// and StringSplitOptions.TrimEntries. Kept unconditional so all builds compile
    /// identically; on modern TFMs members simply delegate to the BCL.
    /// Public so FullAccess plugins (core-tools) can clamp limits without referencing the host.
    /// </summary>
    public static class Polyfills
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
