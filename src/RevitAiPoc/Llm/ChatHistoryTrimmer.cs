namespace RevitAiPoc.Llm;

public static class ChatHistoryTrimmer
{
    public const int KeepMessages = 40;
    public const int MaxToolResultChars = 2000;

    public static void Trim(List<ChatMessage> history)
    {
        if (history.Count == 0) return;

        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            if (msg.Role != "tool") continue;
            var text = LlmJson.GetContentString(msg.Content);
            if (text.Length > MaxToolResultChars)
                msg.Content = text[..MaxToolResultChars] + "\n[omitted]";
        }

        if (history.Count <= KeepMessages + 1) return;

        int keepFrom = history.Count - KeepMessages;
        if (history[0].Role == "system")
        {
            var system = history[0];
            while (keepFrom < history.Count && history[keepFrom].Role == "tool")
                keepFrom++;
            var tail = history.Skip(keepFrom).ToList();
            history.Clear();
            history.Add(system);
            history.AddRange(tail);
        }
        else
        {
            var tail = history.Skip(history.Count - KeepMessages).ToList();
            history.Clear();
            history.AddRange(tail);
        }
    }
}
