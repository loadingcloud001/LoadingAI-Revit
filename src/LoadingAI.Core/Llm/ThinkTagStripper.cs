using System.Text;

namespace LoadingAI.Llm;

/// <summary>
/// Streaming-safe strip of <c>&lt;think&gt;…&lt;/think&gt;</c> so model reasoning never lands in the
/// Agent bubble. Incomplete open/close tags are held across chunks; inner text is reasoning.
/// Leftover tags on a flush (nested/malformed) are dropped so they cannot leak into chat.
/// </summary>
public sealed class ThinkTagStripper
{
    public const string OpenTag = "<think>";
    public const string CloseTag = "</think>";

    private readonly StringBuilder _hold = new();
    private int _thinkDepth;

    public sealed class Result
    {
        public Result(string visible, string reasoning)
        {
            Visible = visible;
            Reasoning = reasoning;
        }

        public string Visible { get; }
        public string Reasoning { get; }
    }

    /// <summary>Feed the next streamed chunk. Visible text is safe for the Agent bubble.</summary>
    public Result Push(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return new Result("", "");

        _hold.Append(chunk);
        return Drain(flush: false);
    }

    /// <summary>
    /// Drain leftover hold (incomplete tags, unclosed think). Call once after the last chunk or
    /// on a complete assistant string. Inner think is still reasoning; leftover tags are dropped.
    /// </summary>
    public Result Flush() => Drain(flush: true);

    /// <summary>Strip a complete string in one shot (final assistant text, non-stream replies).</summary>
    public static Result Strip(string? text)
    {
        var stripper = new ThinkTagStripper();
        var streamed = stripper.Push(text);
        var flushed = stripper.Flush();
        return new Result(
            streamed.Visible + flushed.Visible,
            streamed.Reasoning + flushed.Reasoning);
    }

    private Result Drain(bool flush)
    {
        var visible = new StringBuilder();
        var reasoning = new StringBuilder();

        while (_hold.Length > 0)
        {
            var buffer = _hold.ToString();
            if (_thinkDepth > 0)
            {
                var open = buffer.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
                var close = buffer.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                var next = MinPositive(open, close);
                if (next >= 0)
                {
                    reasoning.Append(buffer, 0, next);
                    if (open == next)
                    {
                        _hold.Remove(0, next + OpenTag.Length);
                        _thinkDepth++;
                    }
                    else
                    {
                        _hold.Remove(0, next + CloseTag.Length);
                        _thinkDepth--;
                    }
                    continue;
                }

                var keep = HoldPartialTag(buffer);
                if (keep == buffer.Length && !flush)
                    break;

                var take = buffer.Length - keep;
                if (flush)
                {
                    take = buffer.Length - keep;
                    _thinkDepth = 0;
                }

                if (take > 0)
                    reasoning.Append(buffer, 0, take);
                _hold.Remove(0, flush ? buffer.Length : take);
                break;
            }

            var visibleOpen = buffer.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
            var leftoverClose = buffer.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
            var visibleNext = MinPositive(visibleOpen, leftoverClose);
            if (visibleNext >= 0)
            {
                visible.Append(buffer, 0, visibleNext);
                if (visibleOpen == visibleNext)
                {
                    _hold.Remove(0, visibleNext + OpenTag.Length);
                    _thinkDepth = 1;
                }
                else
                {
                    _hold.Remove(0, visibleNext + CloseTag.Length);
                }
                continue;
            }

            var hold = HoldPartialTag(buffer);
            if (hold == buffer.Length && !flush)
                break;

            var visibleTake = buffer.Length - hold;
            if (visibleTake > 0)
                visible.Append(buffer, 0, visibleTake);
            _hold.Remove(0, flush ? buffer.Length : visibleTake);
            break;
        }

        if (flush)
        {
            _hold.Clear();
            _thinkDepth = 0;
            DropLeftoverTags(visible);
            DropLeftoverTags(reasoning);
        }

        return new Result(visible.ToString(), reasoning.ToString());
    }

    /// <summary>
    /// How many trailing characters might still grow into <paramref name="tag"/>. Incomplete
    /// prefixes must stay in the hold so a split <c>&lt;thi</c> / <c>nk&gt;</c> is not leaked.
    /// </summary>
    private static int HoldPartialTag(string buffer)
        => Math.Max(HoldPartialSuffix(buffer, OpenTag), HoldPartialSuffix(buffer, CloseTag));

    private static int HoldPartialSuffix(string buffer, string tag)
    {
        var max = Math.Min(buffer.Length, tag.Length - 1);
        for (var n = max; n > 0; n--)
        {
            if (StartsWithIgnoreCase(tag, buffer.Substring(buffer.Length - n, n)))
                return n;
        }

        return 0;
    }

    private static bool StartsWithIgnoreCase(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nested or leftover think tags must never appear in the Agent bubble.</summary>
    private static void DropLeftoverTags(StringBuilder text)
    {
        var raw = text.ToString();
        if (raw.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase) < 0
            && raw.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        text.Clear();
        var i = 0;
        while (i < raw.Length)
        {
            var open = IndexOfIgnoreCase(raw, OpenTag, i);
            var close = IndexOfIgnoreCase(raw, CloseTag, i);
            var next = MinPositive(open, close);
            if (next < 0)
            {
                text.Append(raw, i, raw.Length - i);
                break;
            }

            text.Append(raw, i, next - i);
            i = next + (open == next ? OpenTag.Length : CloseTag.Length);
        }
    }

    private static int IndexOfIgnoreCase(string haystack, string needle, int start)
        => haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);

    private static int MinPositive(int a, int b)
    {
        if (a < 0) return b;
        if (b < 0) return a;
        return a < b ? a : b;
    }
}
