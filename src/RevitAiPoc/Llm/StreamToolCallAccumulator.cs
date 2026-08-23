using System.Text.Json;

namespace RevitAiPoc.Llm;

public sealed class StreamToolCallAccumulator
{
    private readonly Dictionary<int, ToolCall> _calls = new();
    private readonly Dictionary<string, int> _idToIndex = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<int, ToolCall> Calls => _calls;

    public void ApplyChunk(string data)
    {
        using var doc = JsonDocument.Parse(data);
        if (!doc.RootElement.TryGetProperty("choices", out var choicesEl) || choicesEl.GetArrayLength() == 0)
            return;
        var ch0 = choicesEl[0];
        if (!ch0.TryGetProperty("delta", out var deltaEl) ||
            !deltaEl.TryGetProperty("tool_calls", out var tcsEl) ||
            tcsEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var tcEl in tcsEl.EnumerateArray())
        {
            int? idxCandidate = null;
            if (tcEl.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
                idxCandidate = idxEl.GetInt32();

            string? incomingId = null;
            if (tcEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                var s = idEl.GetString();
                if (!string.IsNullOrWhiteSpace(s)) incomingId = s.Trim();
            }

            string? incomingName = null;
            string? incomingArgs = null;
            if (tcEl.TryGetProperty("function", out var funcEl))
            {
                if (funcEl.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                    incomingName = nEl.GetString();
                if (funcEl.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                    incomingArgs = aEl.GetString();
            }

            bool hasName = !string.IsNullOrEmpty(incomingName);
            bool hasArgs = incomingArgs != null;

            int targetIdx;
            if (!string.IsNullOrEmpty(incomingId) && _idToIndex.TryGetValue(incomingId, out var byId))
            {
                targetIdx = byId;
            }
            else if (idxCandidate.HasValue)
            {
                targetIdx = idxCandidate.Value;
                if (!string.IsNullOrEmpty(incomingId) && !_idToIndex.ContainsKey(incomingId))
                    _idToIndex[incomingId] = targetIdx;
                // Detect id collision on same index with different id -> allocate new
                if (!string.IsNullOrEmpty(incomingId) && _calls.TryGetValue(targetIdx, out var existingCollide)
                    && !string.IsNullOrEmpty(existingCollide.Id) && existingCollide.Id != incomingId)
                {
                    targetIdx = _calls.Keys.Count > 0 ? _calls.Keys.Max() + 1 : 0;
                    _idToIndex[incomingId] = targetIdx;
                }
            }
            else if (!string.IsNullOrEmpty(incomingId))
            {
                targetIdx = _calls.Count > 0 ? _calls.Keys.Max() + 1 : 0;
                _idToIndex[incomingId] = targetIdx;
            }
            else if (hasName)
            {
                // No id/index, name present -> treat as new tool unless last entry looks like fragmented name
                // Fragmented names arrive before any arguments; if last entry has no args yet and name is short fragment, merge.
                if (_calls.Count > 0)
                {
                    var lastIdx = _calls.Keys.Max();
                    var last = _calls[lastIdx];
                    bool lastHasArgs = !string.IsNullOrEmpty(last.Function.Arguments);
                    bool lastNameIsFragment = !string.IsNullOrEmpty(last.Function.Name) && last.Function.Name.Length < 5;
                    if (!lastHasArgs && lastNameIsFragment)
                    {
                        targetIdx = lastIdx;
                    }
                    else
                    {
                        targetIdx = lastIdx + 1;
                    }
                }
                else
                {
                    targetIdx = 0;
                }
            }
            else if (hasArgs)
            {
                targetIdx = _calls.Count > 0 ? _calls.Keys.Max() : 0;
            }
            else
            {
                targetIdx = _calls.Count > 0 ? _calls.Keys.Max() : 0;
            }

            if (!_calls.TryGetValue(targetIdx, out var existing))
            {
                existing = new ToolCall { Id = "", Function = new ToolCallFunction() };
                _calls[targetIdx] = existing;
                if (!string.IsNullOrEmpty(incomingId) && !_idToIndex.ContainsKey(incomingId))
                    _idToIndex[incomingId] = targetIdx;
            }

            if (!string.IsNullOrEmpty(incomingId) && string.IsNullOrEmpty(existing.Id))
                existing.Id = incomingId;
            if (tcEl.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
                existing.Type = typeEl.GetString() ?? "function";
            if (hasName)
            {
                var name = incomingName!;
                // If existing already has a name, decide append vs keep (fragment vs new)
                if (string.IsNullOrEmpty(existing.Function.Name))
                    existing.Function.Name = name;
                else if (existing.Function.Name == name)
                {
                    // duplicate, ignore
                }
                else if (name.Length < 4 && !existing.Function.Name.EndsWith(name, StringComparison.Ordinal))
                {
                    // likely fragment continuation
                    existing.Function.Name += name;
                }
                else if (existing.Function.Name.EndsWith(name, StringComparison.Ordinal))
                {
                    // duplicate fragment, ignore
                }
                else if (existing.Function.Name.Length < 8)
                {
                    existing.Function.Name += name;
                }
                else
                {
                    // Should have been new entry, but we landed here -> treat as append to avoid losing
                    existing.Function.Name += name;
                }
            }
            if (hasArgs && incomingArgs != null)
                existing.Function.Arguments += incomingArgs;
        }
    }

    public List<ToolCall> ToList()
    {
        var list = _calls.OrderBy(k => k.Key).Select(k => k.Value).ToList();
        foreach (var tc in list)
            if (string.IsNullOrEmpty(tc.Id))
                tc.Id = "call_" + Guid.NewGuid().ToString("N")[..8];
        // Filter out entries with empty name (partial streaming artifacts)
        list.RemoveAll(tc => string.IsNullOrWhiteSpace(tc.Function.Name));
        return list;
    }
}
