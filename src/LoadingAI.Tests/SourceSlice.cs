using System;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>Cuts one method out of a source file so a text assertion cannot match its neighbour.</summary>
internal static class SourceSlice
{
    /// <summary>From the first <paramref name="start"/> up to, not including, the next <paramref name="end"/>.</summary>
    public static string Between(string source, string start, string end)
    {
        var i = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{start}' was not found.");
        var j = source.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        Assert.True(j > i, $"'{end}' was not found after '{start}'.");
        return source.Substring(i, j - i);
    }

    public static int Count(string source, string needle)
    {
        var n = 0;
        for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
