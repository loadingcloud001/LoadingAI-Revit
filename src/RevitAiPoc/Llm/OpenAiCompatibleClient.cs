using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitAiPoc.Revit;

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

internal sealed class ResponsesRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("input")] public List<object> Input { get; set; } = new();
    [JsonPropertyName("tools")] public List<ResponsesTool>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public string ToolChoice { get; set; } = "auto";
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.2;
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
}

internal sealed class ResponsesTool
{
    [JsonPropertyName("type")] public string Type => "function";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("parameters")] public object? Parameters { get; set; }
}

internal sealed class ResponsesResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("object")] public string? Object { get; set; }
    [JsonPropertyName("output")] public List<ResponsesOutputItem>? Output { get; set; }
    [JsonPropertyName("error")] public ApiError? Error { get; set; }
}

internal sealed class ResponsesOutputItem
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public List<ResponsesContent>? Content { get; set; }
    [JsonPropertyName("call_id")] public string? CallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

internal sealed class ResponsesContent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
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

    private static readonly HashSet<string> ResponsesModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "muse-spark-1.2-contributor", "grok-4.5", "gpt-5.6-luna"
    };

    private static bool IsResponsesModel(string model)
        => !string.IsNullOrWhiteSpace(model) && ResponsesModels.Contains(model.Trim());

    public async Task<ChatCompletionResponse> ChatAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        Action<string>? onDelta,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no API key. Please add it in settings.");

        if (IsResponsesModel(provider.Model))
        {
            var resp = await ResponsesOnceAsync(provider, messages, tools, ct);
            if (onDelta != null)
            {
                var text = LlmJson.GetContentString(resp.Choices?.FirstOrDefault()?.Message?.Content);
                if (!string.IsNullOrEmpty(text))
                    try { onDelta(text); } catch { }
            }
            return resp;
        }

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
            LogHttpFailure("chat.once", provider, messages, tools, json, (int)resp.StatusCode, body);
            if ((int)resp.StatusCode == 401) throw new HttpRequestException($"Provider '{provider.Name}' key invalid or expired (401): {err}. Open Settings and check your API key.");
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        var result = JsonSerializer.Deserialize<ChatCompletionResponse>(body);
        if (result == null) throw new InvalidOperationException("Empty response from LLM.");
        if (result.Error != null) throw new InvalidOperationException(result.Error.Message);
        return result;
    }

    private async Task<ChatCompletionResponse> ResponsesOnceAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        CancellationToken ct)
    {
        var url = BuildResponsesUrl(provider.BaseUrl);
        var input = BuildResponsesInput(messages);
        var respTools = tools?.Select(t => new ResponsesTool
        {
            Name = t.Function.Name,
            Description = t.Function.Description,
            Parameters = t.Function.Parameters
        }).ToList();
        var req = new ResponsesRequest
        {
            Model = provider.Model,
            Input = input,
            Tools = respTools,
            ToolChoice = "auto",
            Stream = null
        };

        var json = JsonSerializer.Serialize(req, LlmJson.Options);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        using var resp = await _http.SendAsync(httpReq, timeoutCts.Token);
        var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);

        if (!resp.IsSuccessStatusCode)
        {
            string err = body;
            try
            {
                var parsed = JsonSerializer.Deserialize<ResponsesResponse>(body);
                if (!string.IsNullOrWhiteSpace(parsed?.Error?.Message))
                    err = parsed.Error.Message!;
                else
                {
                    var chatParsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body);
                    if (!string.IsNullOrWhiteSpace(chatParsed?.Error?.Message))
                        err = chatParsed.Error.Message!;
                }
            }
            catch { }
            LogHttpFailure("responses.once", provider, messages, tools, json, (int)resp.StatusCode, body);
            if ((int)resp.StatusCode == 401) throw new HttpRequestException($"Provider '{provider.Name}' key invalid or expired (401): {err}. Open Settings and check your API key.");
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        var result = JsonSerializer.Deserialize<ResponsesResponse>(body);
        if (result == null) throw new InvalidOperationException("Empty response from LLM.");
        if (result.Error != null) throw new InvalidOperationException(result.Error.Message);

        var textSb = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        if (result.Output != null)
        {
            foreach (var item in result.Output)
            {
                if (item.Type == "message" && item.Content != null)
                {
                    foreach (var c in item.Content)
                        if (c.Type == "output_text" && !string.IsNullOrEmpty(c.Text))
                            textSb.Append(c.Text);
                }
                else if (item.Type == "function_call")
                {
                    var args = item.Arguments ?? "{}";
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(args))
                        {
                            using var d = JsonDocument.Parse(args);
                            if (d.RootElement.ValueKind != JsonValueKind.Object && d.RootElement.ValueKind != JsonValueKind.Array)
                                args = "{}";
                        }
                        else args = "{}";
                    }
                    catch { args = "{}"; }
                    toolCalls.Add(new ToolCall
                    {
                        Id = string.IsNullOrWhiteSpace(item.CallId) ? "call_" + Guid.NewGuid().ToString("N")[..8] : item.CallId!,
                        Type = "function",
                        Function = new ToolCallFunction { Name = item.Name ?? "", Arguments = args }
                    });
                }
            }
        }

        var finish = toolCalls.Count > 0 ? "tool_calls" : "stop";
        var msg = new ChatMessage
        {
            Role = "assistant",
            Content = textSb.ToString(),
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
        return new ChatCompletionResponse
        {
            Choices = new List<Choice> { new Choice { Message = msg, FinishReason = finish } }
        };
    }

    private static List<object> BuildResponsesInput(List<ChatMessage> messages)
    {
        var input = new List<object>();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                var output = LlmJson.GetContentString(m.Content);
                input.Add(new Dictionary<string, object>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = m.ToolCallId ?? "",
                    ["output"] = output
                });
            }
            else if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                var text = LlmJson.GetContentString(m.Content);
                if (!string.IsNullOrWhiteSpace(text))
                    input.Add(new Dictionary<string, object> { ["role"] = "assistant", ["content"] = text });
                foreach (var tc in m.ToolCalls)
                {
                    var args = LlmJson.NormalizeToolArguments(tc.Function.Arguments);
                    input.Add(new Dictionary<string, object>
                    {
                        ["type"] = "function_call",
                        ["call_id"] = tc.Id,
                        ["name"] = tc.Function.Name,
                        ["arguments"] = args
                    });
                }
            }
            else
            {
                var text = LlmJson.GetContentString(m.Content);
                var role = m.Role == "system" ? "system" : m.Role == "assistant" ? "assistant" : "user";
                if (string.IsNullOrEmpty(text) && m.ToolCalls is { Count: > 0 })
                    continue;
                input.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = text });
            }
        }
        return input;
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
            LogHttpFailure("chat.stream", provider, messages, tools, json, (int)resp.StatusCode, body);
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

    private static void LogHttpFailure(
        string kind,
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        string requestJson,
        int status,
        string body)
    {
        try
        {
            var msgSummaries = messages.Select(m =>
            {
                var args = m.ToolCalls?.Select(tc =>
                {
                    var raw = tc.Function.Arguments ?? "";
                    var valid = false;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            using var d = JsonDocument.Parse(raw);
                            valid = d.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
                        }
                    }
                    catch { }
                    return new { tc.Id, tc.Function.Name, argsLen = raw.Length, argsPreview = raw.Length > 80 ? raw[..80] : raw, argsValidJson = valid };
                }).ToList();
                return new
                {
                    m.Role,
                    contentNull = m.Content == null,
                    contentLen = LlmJson.GetContentString(m.Content).Length,
                    toolCallId = m.ToolCallId,
                    toolName = m.Name,
                    toolCalls = args
                };
            }).ToList();
            DebugLog.Write(kind, new
            {
                provider = provider.Name,
                model = provider.Model,
                status,
                requestBytes = requestJson.Length,
                toolCount = tools?.Count ?? 0,
                messageCount = messages.Count,
                messages = msgSummaries,
                bodyPreview = body.Length > 500 ? body[..500] : body
            });
        }
        catch { }
    }

    private static string BuildUrl(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return url;
        return url + "/chat/completions";
    }

    private static string BuildResponsesUrl(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url[..^"/chat/completions".Length] + "/responses";
        if (url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return url[..^"/messages".Length] + "/responses";
        return url + "/responses";
    }

    public void Dispose() => _http.Dispose();
}
