using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitAiPoc.Llm;

public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public object? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class ToolCall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ToolCallFunction Function { get; set; } = new();
}

public sealed class ToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
}

public sealed class ToolDefinition
{
    [JsonPropertyName("type")] public string Type => "function";
    [JsonPropertyName("function")] public ToolFunction Function { get; set; } = new();
}

public sealed class ToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("parameters")] public object Parameters { get; set; } = new { };
}

internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("tools")] public List<ToolDefinition>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public string ToolChoice { get; set; } = "auto";
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.2;
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
}

internal sealed class ChatCompletionChunk
{
    [JsonPropertyName("choices")] public List<ChunkChoice>? Choices { get; set; }
}
internal sealed class ChunkChoice
{
    [JsonPropertyName("delta")] public ChatMessage? Delta { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    [JsonPropertyName("error")] public ApiError? Error { get; set; }
}

public sealed class Choice
{
    [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class ApiError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}

public sealed class OpenAiCompatibleClient : IDisposable
{
    private readonly HttpClient _http;

    public OpenAiCompatibleClient()
    {
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ChatCompletionResponse> ChatAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        CancellationToken ct = default)
        => await ChatAsync(provider, messages, tools, null, ct);

    public async Task<ChatCompletionResponse> ChatAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        Action<string>? onDelta,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no API key. Please add it in settings.");

        if (onDelta != null)
        {
            var streamed = false;
            try
            {
                return await ChatStreamAsync(provider, messages, tools, chunk =>
                {
                    streamed = true;
                    onDelta(chunk);
                }, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch when (!streamed)
            {
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        return await ChatOnceAsync(provider, messages, tools, timeoutCts.Token);
    }

    private async Task<ChatCompletionResponse> ChatOnceAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        CancellationToken ct)
    {
        var url = BuildUrl(provider.BaseUrl);
        var req = new ChatCompletionRequest
        {
            Model = provider.Model,
            Messages = messages,
            Tools = tools,
            ToolChoice = "auto",
            Stream = null
        };

        var json = JsonSerializer.Serialize(req, LlmJson.Options);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        using var resp = await _http.SendAsync(httpReq, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            string err = body;
            try
            {
                var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body);
                if (!string.IsNullOrWhiteSpace(parsed?.Error?.Message))
                    err = parsed.Error.Message!;
            }
            catch { }
            if ((int)resp.StatusCode == 401) throw new HttpRequestException($"Provider '{provider.Name}' key invalid or expired (401): {err}. Open Settings and check your API key.");
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        var result = JsonSerializer.Deserialize<ChatCompletionResponse>(body);
        if (result == null) throw new InvalidOperationException("Empty response from LLM.");
        if (result.Error != null) throw new InvalidOperationException(result.Error.Message);
        return result;
    }

    private async Task<ChatCompletionResponse> ChatStreamAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        Action<string> onDelta,
        CancellationToken ct)
    {
        var url = BuildUrl(provider.BaseUrl);
        var req = new ChatCompletionRequest
        {
            Model = provider.Model,
            Messages = messages,
            Tools = tools,
            ToolChoice = "auto",
            Stream = true
        };

        var json = JsonSerializer.Serialize(req, LlmJson.Options);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            string err = body;
            try
            {
                var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body);
                if (!string.IsNullOrWhiteSpace(parsed?.Error?.Message)) err = parsed.Error.Message!;
            }
            catch { }
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        var contentSb = new StringBuilder();
        var toolCallsAccum = new StreamToolCallAccumulator();
        string? finishReason = null;
        string? role = "assistant";

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith(":")) continue;
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (string.IsNullOrWhiteSpace(data)) continue;

            ChatCompletionChunk? chunk = null;
            try { chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(data); } catch { continue; }
            var choice = chunk?.Choices?.FirstOrDefault();
            if (choice == null) continue;
            if (!string.IsNullOrEmpty(choice.FinishReason)) finishReason = choice.FinishReason;
            var delta = choice.Delta;
            if (delta == null) continue;
            if (!string.IsNullOrEmpty(delta.Role)) role = delta.Role;

            var deltaContent = LlmJson.GetContentString(delta.Content);
            if (!string.IsNullOrEmpty(deltaContent))
            {
                contentSb.Append(deltaContent);
                try { onDelta(deltaContent); } catch { }
            }

            try { toolCallsAccum.ApplyChunk(data); } catch { }
        }

        var finalMsg = new ChatMessage { Role = role ?? "assistant", Content = contentSb.ToString() };
        if (toolCallsAccum.Calls.Count > 0)
            finalMsg.ToolCalls = toolCallsAccum.ToList();

        return new ChatCompletionResponse
        {
            Choices = new List<Choice> { new Choice { Message = finalMsg, FinishReason = finishReason ?? (finalMsg.ToolCalls?.Count > 0 ? "tool_calls" : "stop") } }
        };
    }

    private static string BuildUrl(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    public void Dispose() => _http.Dispose();
}
