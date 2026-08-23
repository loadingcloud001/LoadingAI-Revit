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

// Streaming delta structures (subset of OpenAI spec)
internal sealed class ChatCompletionChunk
{
    [JsonPropertyName("choices")] public List<ChunkChoice>? Choices { get; set; }
}
internal sealed class ChunkChoice
{
    [JsonPropertyName("delta")] public ChatMessage? Delta { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}
internal sealed class DeltaToolCall
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("function")] public DeltaFunction? Function { get; set; }
}
internal sealed class DeltaFunction
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
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
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
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

        // Try streaming first if caller wants deltas
        if (onDelta != null)
        {
            try { return await ChatStreamAsync(provider, messages, tools, onDelta, ct); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // fallback to non-stream on any stream failure (provider doesn't support stream, etc.)
            }
        }

        var baseUrl = provider.BaseUrl.Trim().TrimEnd('/');
        var url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/chat/completions";

        var req = new ChatCompletionRequest
        {
            Model = provider.Model,
            Messages = messages,
            Tools = tools,
            ToolChoice = "auto",
            Stream = null
        };

        var json = JsonSerializer.Serialize(req, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

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
            // Friendly 401
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
        var baseUrl = provider.BaseUrl.Trim().TrimEnd('/');
        var url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/chat/completions";

        var req = new ChatCompletionRequest
        {
            Model = provider.Model,
            Messages = messages,
            Tools = tools,
            ToolChoice = "auto",
            Stream = true
        };

        var json = JsonSerializer.Serialize(req, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

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

        // Accumulate streaming deltas
        var contentSb = new StringBuilder();
        var toolCallsAccum = new Dictionary<int, ToolCall>();
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
            if (line.StartsWith(":")) continue; // comment
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

            // Content delta
            var deltaContent = GetContentString(delta.Content);
            if (!string.IsNullOrEmpty(deltaContent))
            {
                contentSb.Append(deltaContent);
                try { onDelta(deltaContent); } catch { }
            }

            // Tool call deltas — need to parse raw JSON for tool_calls array since ChatMessage.ToolCalls is typed as List<ToolCall>
            // We re-parse delta's raw JSON for tool_calls incremental fragments
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choicesEl) && choicesEl.GetArrayLength() > 0)
                {
                    var ch0 = choicesEl[0];
                    if (ch0.TryGetProperty("delta", out var deltaEl) && deltaEl.TryGetProperty("tool_calls", out var tcsEl) && tcsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tcEl in tcsEl.EnumerateArray())
                        {
                            int idx;
                            if (tcEl.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number) idx = idxEl.GetInt32();
                            else
                            {
                                // provider omitted index (e.g. some OpenAI compatibles) — allocate new index if this chunk starts a new tool (has name), else append to last
                                bool hasName = tcEl.TryGetProperty("function", out var _fe) && _fe.TryGetProperty("name", out var _ne) && _ne.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(_ne.GetString());
                                if (hasName) idx = toolCallsAccum.Count;
                                else idx = toolCallsAccum.Count > 0 ? toolCallsAccum.Keys.Max() : 0;
                            }
                            if (!toolCallsAccum.TryGetValue(idx, out var existing))
                            {
                                existing = new ToolCall { Id = "", Function = new ToolCallFunction() };
                                toolCallsAccum[idx] = existing;
                            }
                            if (tcEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) existing.Id = (existing.Id ?? "") + idEl.GetString();
                            if (tcEl.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String) existing.Type = typeEl.GetString() ?? "function";
                            if (tcEl.TryGetProperty("function", out var funcEl))
                            {
                                if (funcEl.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String) existing.Function.Name += nEl.GetString();
                                if (funcEl.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String) existing.Function.Arguments += aEl.GetString();
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Build final message from accumulators
        var finalMsg = new ChatMessage { Role = role ?? "assistant", Content = contentSb.ToString() };
        if (toolCallsAccum.Count > 0)
        {
            finalMsg.ToolCalls = toolCallsAccum.OrderBy(k => k.Key).Select(k => k.Value).ToList();
            // Ensure ids present
            foreach (var tc in finalMsg.ToolCalls) if (string.IsNullOrEmpty(tc.Id)) tc.Id = "call_" + Guid.NewGuid().ToString("N")[..8];
        }

        return new ChatCompletionResponse
        {
            Choices = new List<Choice> { new Choice { Message = finalMsg, FinishReason = finishReason ?? (finalMsg.ToolCalls?.Count > 0 ? "tool_calls" : "stop") } }
        };
    }

    private static string GetContentString(object? c)
    {
        if (c == null) return "";
        if (c is string s) return s;
        if (c is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString() ?? "";
            if (je.ValueKind == JsonValueKind.Null) return "";
            return je.GetRawText();
        }
        return c.ToString() ?? "";
    }

    public void Dispose() => _http.Dispose();
}
