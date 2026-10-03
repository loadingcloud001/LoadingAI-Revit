using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoadingAI.Revit;

namespace LoadingAI.Llm;

public sealed class ChatContentPart
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("image_url")] public ChatImageUrl? ImageUrl { get; set; }
}
public sealed class ChatImageUrl
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public sealed class ChatContentConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType == JsonTokenType.StartArray) return JsonSerializer.Deserialize<List<ChatContentPart>>(ref reader, options);
        if (reader.TokenType == JsonTokenType.Null) return null;
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }
    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is string s) { writer.WriteStringValue(s); return; }
        if (value is List<ChatContentPart> parts) { JsonSerializer.Serialize(writer, parts, options); return; }
        if (value == null) { writer.WriteNullValue(); return; }
        JsonSerializer.Serialize(writer, value, options);
    }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")]
    [JsonConverter(typeof(ChatContentConverter))]
    public object? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }

    public static ChatMessage FromText(string role, string text) => new() { Role = role, Content = text };
    public static ChatMessage FromMultimodal(string role, string text, IReadOnlyList<AttachmentDto> images)
    {
        if (images.Count == 0) return FromText(role, text);
        var parts = new List<ChatContentPart>();
        if (!string.IsNullOrWhiteSpace(text)) parts.Add(new ChatContentPart { Type = "text", Text = text });
        foreach (var a in images) parts.Add(new ChatContentPart { Type = "image_url", ImageUrl = new ChatImageUrl { Url = a.DataUrl, Detail = "auto" } });
        return new ChatMessage { Role = role, Content = parts };
    }
}
public sealed record AttachmentDto(string Id, string Name, string MediaType, string DataUrl);

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
    [JsonPropertyName("tool_choice")] public string? ToolChoice { get; set; }
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.2;
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
}

internal sealed class ResponsesRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("input")] public List<object> Input { get; set; } = new();
    [JsonPropertyName("tools")] public List<ResponsesTool>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public string? ToolChoice { get; set; }
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
        : this(new HttpClientHandler())
    {
    }

    /// <summary>Test seam: drive the real client against a scripted handler (InternalsVisibleTo LoadingAI.Tests).</summary>
    internal OpenAiCompatibleClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Longest silence tolerated between two stream lines before the call fails.</summary>
    internal TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Longer allowance until the first line arrives, since a reasoning model can think silently for a while.</summary>
    internal TimeSpan StreamFirstLineTimeout { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// netstandard2.0 ReadLineAsync takes no token, so a stalled provider would hang Stop forever.
    /// Races the read against the idle timeout and the caller's token.
    /// </summary>
    internal static async Task<string?> ReadLineWithIdleTimeoutAsync(StreamReader reader, TimeSpan idle, CancellationToken ct)
    {
        var read = reader.ReadLineAsync();
        // Buffered SSE lines complete synchronously; skip the timer for them.
        if (read.IsCompleted) return await AwaitReadAsync(read, ct).ConfigureAwait(false);

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(idle, delayCts.Token);
        var done = await Task.WhenAny(read, delay).ConfigureAwait(false);
        // A line that landed at the same instant as the timer is not a timeout.
        if (done == read || read.IsCompleted)
        {
            delayCts.Cancel();
            return await AwaitReadAsync(read, ct).ConfigureAwait(false);
        }
        // The abandoned read faults once the response is disposed; observe it so it is not unobserved.
        _ = read.ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"Provider stream sent nothing for {idle.TotalSeconds:0}s.");
    }

    private static async Task<string?> AwaitReadAsync(Task<string> read, CancellationToken ct)
    {
        try { return await read.ConfigureAwait(false); }
        // Stop disposes the response, which can fault the read before the token is observed.
        catch when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
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

    private static string ResolveApiKind(ProviderConfig p)
    {
        if (!string.IsNullOrWhiteSpace(p.ApiKind))
            return p.ApiKind.Trim().ToLowerInvariant();
        if (IsResponsesModel(p.Model)) return "responses";
        if (p.BaseUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase)) return "anthropic";
        return "openai";
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        var url = BuildModelsUrl(provider.BaseUrl);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req, provider);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await _http.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            string err = body;
            try
            {
                var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body);
                if (!string.IsNullOrWhiteSpace(parsed?.Error?.Message)) err = parsed.Error.Message!;
            }
            catch { }
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in data.EnumerateArray())
                {
                    if (el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        list.Add(id.GetString()!);
                }
                return list;
            }
        }
        catch { }
        return Array.Empty<string>();
    }

    public async Task<(bool ok, string message, IReadOnlyList<string>? models)> TestAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        // 1. try list models (validates baseUrl + key)
        List<string>? models = null;
        try
        {
            var list = await ListModelsAsync(provider, ct);
            models = list.ToList();
            // check requested model exists if list non-empty
            if (models.Count > 0 && !string.IsNullOrWhiteSpace(provider.Model) && !models.Contains(provider.Model))
            {
                // not fatal, just warn but continue to chat test
            }
        }
        catch (Exception ex)
        {
            // For local ollama without auth, list may still succeed; if fails, report but try chat anyway for ollama
            var kind = ResolveApiKind(provider);
            if (kind == "openai" && provider.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                // ignore list failure for local, continue
            }
            else if (!provider.BaseUrl.Contains("localhost"))
            {
                return (false, $"List models failed: {ex.Message}", null);
            }
        }

        // 2. minimal chat probe
        var kind2 = ResolveApiKind(provider);
        if (kind2 == "anthropic")
            return (false, "Anthropic's direct /messages API is not supported by this add-in. Set Base URL to https://openrouter.ai/api/v1, ApiKind to openai, and Model to anthropic/claude-sonnet-4 (Settings -> Edit provider).", models);
        try
        {
            var probe = new List<ChatMessage> { new() { Role = "user", Content = "hi" } };
            // use non-stream trivial call with tiny max
            if (kind2 == "responses")
                await ResponsesOnceAsync(provider, probe, null, ct);
            else
                await ChatOnceAsync(provider, probe, null, ct);
            return (true, models != null ? $"OK — {models.Count} models available" : "OK", models);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, models);
        }
    }

    public async Task<ChatCompletionResponse> ChatAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        Action<string>? onDelta,
        CancellationToken ct = default)
        => await ChatAsync(provider, messages, tools, onDelta, null, ct);

    public async Task<ChatCompletionResponse> ChatAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        Action<string>? onDelta,
        Action<string>? onReasoning,
        CancellationToken ct = default)
    {
        if (!DefaultLlm.IsBuiltin(provider.Id)
            && string.IsNullOrWhiteSpace(provider.ApiKey)
            && !provider.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no API key. Please add it in settings.");

        if (DefaultLlm.IsBuiltin(provider.Id) && !DefaultLlm.DirectUpstreamEnabled && BuiltinQuota.Ticket == null)
            throw new InvalidOperationException($"{DefaultLlm.DisplayName} has no active request ticket. Send your message again to start a new request.");

        var kind = ResolveApiKind(provider);
        if (kind == "anthropic")
            throw new InvalidOperationException("Anthropic's direct /messages API is not supported by this add-in. Set Base URL to https://openrouter.ai/api/v1, ApiKind to openai, and Model to anthropic/claude-sonnet-4 (Settings -> Edit provider).");

        if (kind == "responses" || IsResponsesModel(provider.Model))
        {
            var resp = await ResponsesOnceAsync(provider, messages, tools, ct);
            EmitStrippedOnce(resp, onDelta, onReasoning);
            return resp;
        }

        if (onDelta != null || onReasoning != null)
        {
            var streamed = false;
            try
            {
                return await ChatStreamAsync(provider, messages, tools, chunk =>
                {
                    streamed = true;
                    try { onDelta?.Invoke(chunk); } catch { }
                }, onReasoning, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch when (!streamed)
            {
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        var once = await ChatOnceAsync(provider, messages, tools, timeoutCts.Token);
        EmitStrippedOnce(once, onDelta, onReasoning);
        return once;
    }

    /// <summary>
    /// Non-stream replies still dump the full content; strip think tags so they never reach chat
    /// and route inner think to the Think panel when a host post exists.
    /// </summary>
    private static void EmitStrippedOnce(
        ChatCompletionResponse resp,
        Action<string>? onDelta,
        Action<string>? onReasoning)
    {
        var msg = resp.Choices?.FirstOrDefault()?.Message;
        if (msg == null) return;
        var raw = LlmJson.GetContentString(msg.Content);
        if (string.IsNullOrEmpty(raw)) return;
        var stripped = ThinkTagStripper.Strip(raw);
        msg.Content = stripped.Visible;
        if (!string.IsNullOrEmpty(stripped.Visible) && onDelta != null)
            try { onDelta(stripped.Visible); } catch { }
        if (!string.IsNullOrEmpty(stripped.Reasoning) && onReasoning != null)
            try { onReasoning(stripped.Reasoning); } catch { }
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
            Tools = tools is { Count: > 0 } ? tools : null,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null,
            Stream = null
        };

        var json = JsonSerializer.Serialize(req, LlmJson.Options);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuth(httpReq, provider);

        using var resp = await _http.SendAsync(httpReq, ct);
        var body = await resp.Content.ReadAsStringAsync();

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
            if ((int)resp.StatusCode == 401 && !DefaultLlm.IsBuiltin(provider.Id)) throw new HttpRequestException($"Provider '{provider.Name}' key invalid or expired (401): {err}. Open Settings and check your API key.");
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
        var respTools = tools is { Count: > 0 }
            ? tools.Select(t => new ResponsesTool
            {
                Name = t.Function.Name,
                Description = t.Function.Description,
                Parameters = t.Function.Parameters
            }).ToList()
            : null;
        var req = new ResponsesRequest
        {
            Model = provider.Model,
            Input = input,
            Tools = respTools,
            ToolChoice = respTools != null ? "auto" : null,
            Stream = null
        };

        var json = JsonSerializer.Serialize(req, LlmJson.Options);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuth(httpReq, provider);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        using var resp = await _http.SendAsync(httpReq, timeoutCts.Token);
        var body = await resp.Content.ReadAsStringAsync();

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
                        Id = string.IsNullOrWhiteSpace(item.CallId) ? "call_" + Guid.NewGuid().ToString("N").Substring(0, 8) : item.CallId!,
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
                var role = m.Role == "system" ? "system" : m.Role == "assistant" ? "assistant" : "user";
                if (m.Content is List<ChatContentPart> parts)
                {
                    var content = parts.Select(p => p.Type == "text"
                        ? (object)new Dictionary<string, object> { ["type"] = "input_text", ["text"] = p.Text ?? "" }
                        : new Dictionary<string, object> { ["type"] = "input_image", ["image_url"] = p.ImageUrl?.Url ?? "", ["detail"] = p.ImageUrl?.Detail ?? "auto" }).ToList();
                    input.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = content });
                }
                else if (m.Content is JsonElement je && je.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<object>();
                    foreach (var el in je.EnumerateArray())
                    {
                        if (el.TryGetProperty("type", out var t) && t.GetString() == "text" && el.TryGetProperty("text", out var tx))
                            list.Add(new Dictionary<string, object> { ["type"] = "input_text", ["text"] = tx.GetString() ?? "" });
                        else if (el.TryGetProperty("type", out var t2) && t2.GetString() == "image_url" && el.TryGetProperty("image_url", out var iu) && iu.TryGetProperty("url", out var url))
                            list.Add(new Dictionary<string, object> { ["type"] = "input_image", ["image_url"] = url.GetString() ?? "" });
                    }
                    input.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = list });
                }
                else
                {
                    var text = LlmJson.GetContentString(m.Content);
                    if (string.IsNullOrEmpty(text) && m.ToolCalls is { Count: > 0 }) continue;
                    input.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = text });
                }
            }
        }
        return input;
    }

    private async Task<ChatCompletionResponse> ChatStreamAsync(
        ProviderConfig provider,
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        Action<string> onDelta,
        Action<string>? onReasoning,
        CancellationToken ct)
    {
        var url = BuildUrl(provider.BaseUrl);
        var req = new ChatCompletionRequest
        {
            Model = provider.Model,
            Messages = messages,
            Tools = tools is { Count: > 0 } ? tools : null,
            ToolChoice = tools is { Count: > 0 } ? "auto" : null,
            Stream = true
        };

        var json = JsonSerializer.Serialize(req, LlmJson.Options);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuth(httpReq, provider);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        // Stop must abort a body read that is already in flight. Hop off the thread calling Cancel (the UI thread),
        // since disposing a response with a pending read can block on some runtimes.
        using var abortOnCancel = ct.Register(() => Task.Run(() => { try { resp.Dispose(); } catch { } }));
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
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
        var thinkStrip = new ThinkTagStripper();
        string? finishReason = null;
        string? role = "assistant";

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Trade-off: once any line has arrived (even a keep-alive comment), the shorter idle limit applies.
        var firstLine = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await ReadLineWithIdleTimeoutAsync(reader, firstLine ? StreamFirstLineTimeout : StreamIdleTimeout, ct);
            if (line == null) break;
            firstLine = false;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith(":")) continue;
            if (!line.StartsWith("data:")) continue;
            var data = line.Substring("data:".Length).Trim();
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
                var stripped = thinkStrip.Push(deltaContent);
                if (!string.IsNullOrEmpty(stripped.Visible))
                {
                    contentSb.Append(stripped.Visible);
                    try { onDelta(stripped.Visible); } catch { }
                }
                if (!string.IsNullOrEmpty(stripped.Reasoning) && onReasoning != null)
                    try { onReasoning(stripped.Reasoning); } catch { }
            }

            try { toolCallsAccum.ApplyChunk(data); } catch { }
        }

        var flushed = thinkStrip.Flush();
        if (!string.IsNullOrEmpty(flushed.Visible))
        {
            contentSb.Append(flushed.Visible);
            try { onDelta(flushed.Visible); } catch { }
        }
        if (!string.IsNullOrEmpty(flushed.Reasoning) && onReasoning != null)
            try { onReasoning(flushed.Reasoning); } catch { }

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
                    return new { tc.Id, tc.Function.Name, argsLen = raw.Length, argsPreview = raw.Length > 80 ? raw.Substring(0, 80) : raw, argsValidJson = valid };
                }).ToList();
                var isMulti = LlmJson.IsMultimodal(m.Content);
                return new
                {
                    m.Role,
                    contentNull = m.Content == null,
                    isMultimodal = isMulti,
                    contentLen = isMulti ? -1 : LlmJson.GetContentString(m.Content).Length,
                    contentPreview = isMulti ? "[multimodal]" : (LlmJson.GetContentString(m.Content).Length > 120 ? LlmJson.GetContentString(m.Content).Substring(0, 120) : LlmJson.GetContentString(m.Content)),
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
                bodyPreview = body.Length > 500 ? body.Substring(0, 500) : body
            });
        }
        catch { }
    }

    /// <summary>
    /// Credentials for one request. The built-in provider authenticates with the
    /// anonymous device id plus the ticket from its last send; every other
    /// provider uses its own bearer key.
    /// </summary>
    private static void ApplyAuth(HttpRequestMessage req, ProviderConfig provider)
    {
        if (DefaultLlm.IsBuiltin(provider.Id))
        {
            req.Headers.TryAddWithoutValidation(BuiltinQuota.ClientHeader, BuiltinQuota.ClientToken);
            req.Headers.TryAddWithoutValidation(BuiltinQuota.DeviceHeader, BuiltinQuota.DeviceId);
            var ticket = BuiltinQuota.Ticket;
            if (!string.IsNullOrWhiteSpace(ticket))
                req.Headers.TryAddWithoutValidation(BuiltinQuota.TicketHeader, ticket);
            return;
        }

        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
    }

    /// <summary>Spends one free request for this device and keeps the returned ticket.</summary>
    public Task<QuotaStatus> ConsumeBuiltinQuotaAsync(CancellationToken ct = default)
        => SendQuotaAsync(HttpMethod.Post, ct);

    /// <summary>Reads the remaining free allowance without spending it.</summary>
    public Task<QuotaStatus> PeekBuiltinQuotaAsync(CancellationToken ct = default)
        => SendQuotaAsync(HttpMethod.Get, ct);

    private async Task<QuotaStatus> SendQuotaAsync(HttpMethod method, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, BuildQuotaUrl(DefaultLlm.GatewayBaseUrl));
            req.Headers.TryAddWithoutValidation(BuiltinQuota.ClientHeader, BuiltinQuota.ClientToken);
            req.Headers.TryAddWithoutValidation(BuiltinQuota.DeviceHeader, BuiltinQuota.DeviceId);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync();
            var status = ParseQuota(body) ?? new QuotaStatus();

            if (method == HttpMethod.Post)
            {
                if (resp.IsSuccessStatusCode) BuiltinQuota.SetTicket(status.Ticket);
                else BuiltinQuota.ClearTicket();
            }

            if (resp.IsSuccessStatusCode)
            {
                status.Ok = true;
                return status;
            }

            status.Ok = false;
            status.Exhausted = string.Equals(status.Error?.Code, "daily_limit", StringComparison.OrdinalIgnoreCase)
                || (int)resp.StatusCode == 429;
            status.Message = status.Exhausted
                ? BuiltinQuota.LimitMessage(status)
                : !string.IsNullOrWhiteSpace(status.Error?.Message)
                    ? status.Error!.Message!
                    : $"{DefaultLlm.DisplayName} gateway error (HTTP {(int)resp.StatusCode}). Try again, or add your own API key in Settings.";
            return status;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return QuotaUnreachable($"{DefaultLlm.DisplayName} gateway timed out. Check your connection and try again.");
        }
        catch (Exception ex)
        {
            return QuotaUnreachable($"{DefaultLlm.DisplayName} gateway unreachable: {ex.Message}");
        }
    }

    private static QuotaStatus QuotaUnreachable(string message)
        => new() { Ok = false, Message = message };

    private static QuotaStatus? ParseQuota(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<QuotaStatus>(body); }
        catch { return null; }
    }

    private static string BuildQuotaUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) return "/v1/quota";
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            url = url.Substring(0, url.Length - "/chat/completions".Length);
        else if (url.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            url = url.Substring(0, url.Length - "/responses".Length);
        else if (url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            url = url.Substring(0, url.Length - "/messages".Length);
        return url + "/quota";
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
            return url.Substring(0, url.Length - "/chat/completions".Length) + "/responses";
        if (url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return url.Substring(0, url.Length - "/messages".Length) + "/responses";
        return url + "/responses";
    }

    private static string BuildModelsUrl(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            url = url.Substring(0, url.Length - "/chat/completions".Length);
        else if (url.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            url = url.Substring(0, url.Length - "/responses".Length);
        else if (url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            url = url.Substring(0, url.Length - "/messages".Length);
        return url + "/models";
    }

    public void Dispose() => _http.Dispose();
}
