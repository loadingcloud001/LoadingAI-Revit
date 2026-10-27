namespace LoadingAI.Llm;

public static class ChatHistoryTrimmer
{
    public const int KeepMessages = 40;
    public const int MaxToolResultChars = 2000;
    /// <summary>Tool results the model has not answered yet (after the last user message) keep more.</summary>
    public const int MaxCurrentTurnToolResultChars = 8000;

    public static void Trim(List<ChatMessage> history)
    {
        if (history.Count == 0) return;

        var lastUser = history.FindLastIndex(m => m.Role == "user" && !m.IsSnapshot);
        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            if (msg.Role != "tool") continue;
            var cap = lastUser >= 0 && i > lastUser ? MaxCurrentTurnToolResultChars : MaxToolResultChars;
            var text = LlmJson.GetContentString(msg.Content);
            if (text.Length > cap)
                msg.Content = text.Substring(0, cap) + "\n[omitted]";
        }

        // The newest image messages stay; older ones lose their images but keep their text.
        const int KeepImageMessages = 2;
        var keepImages = new HashSet<int>();
        for (int i = history.Count - 1; i >= 0 && keepImages.Count < KeepImageMessages; i--)
            if (history[i].Role == "user" && LlmJson.IsMultimodal(history[i].Content))
                keepImages.Add(i);
        for (int i = 0; i < history.Count; i++)
        {
            if (keepImages.Contains(i)) continue;
            if (LlmJson.IsMultimodal(history[i].Content))
                history[i].Content = LlmJson.StripImages(history[i].Content);
        }

        if (history.Count <= KeepMessages + 1) return;

        var hasSystem = history[0].Role == "system";
        int keepFrom = history.Count - KeepMessages;
        while (keepFrom < history.Count && history[keepFrom].Role == "tool")
            keepFrom++;

        // A long turn must not lose the instruction it is executing.
        var pinned = lastUser >= (hasSystem ? 1 : 0) && lastUser < keepFrom ? history[lastUser] : null;
        var system = hasSystem ? history[0] : null;
        var tail = history.Skip(keepFrom).ToList();
        history.Clear();
        if (system != null) history.Add(system);
        if (pinned != null) history.Add(pinned);
        history.AddRange(tail);
    }
}
