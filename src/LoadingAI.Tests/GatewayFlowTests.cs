using System.Net;
using System.Net.Sockets;
using System.Text;
using LoadingAI.Llm;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Drives the real <see cref="OpenAiCompatibleClient"/> against a stub gateway over
/// a socket, so the wire contract the gateway implements is exercised for real:
/// device header + ticket, one quota unit per send, no upstream key on the client.
/// </summary>
[Collection("builtin-provider")]
public sealed class GatewayFlowTests : IDisposable
{
    readonly string _prevBaseUrl = DefaultLlm.GatewayBaseUrl;

    public GatewayFlowTests() => BuiltinQuota.ClearTicket();

    public void Dispose()
    {
        BuiltinQuota.ClearTicket();
        DefaultLlm.GatewayBaseUrl = _prevBaseUrl;
    }

    static ProviderConfig Builtin() => DefaultLlm.BindForCall(DefaultLlm.ForSettings());

    static Task<ChatCompletionResponse> Send(OpenAiCompatibleClient client)
        => client.ChatAsync(
            Builtin(),
            new List<ChatMessage> { new() { Role = "user", Content = "hello" } },
            null);

    [Fact]
    public async Task Consume_SpendsOneRequestAndKeepsTheTicket()
    {
        using var gw = new StubGateway(_ => StubGateway.Json(
            """{"limit":100,"used":1,"remaining":99,"resetUtc":"2026-01-02T00:00:00.000Z","ticket":"tk-abc"}"""));
        DefaultLlm.GatewayBaseUrl = gw.BaseUrl;
        using var client = new OpenAiCompatibleClient();

        var status = await client.ConsumeBuiltinQuotaAsync();

        Assert.True(status.Ok, status.Message);
        Assert.Equal(100, status.Limit);
        Assert.Equal(99, status.Remaining);
        Assert.False(status.Exhausted);
        Assert.Equal("tk-abc", BuiltinQuota.Ticket);

        var req = Assert.Single(gw.Requests);
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1/quota", req.Path);
        Assert.Equal(BuiltinQuota.DeviceId, req.Header(BuiltinQuota.DeviceHeader));
        Assert.False(req.HasHeader("Authorization"));
    }

    [Fact]
    public async Task Peek_ReadsTheAllowanceWithoutSpending()
    {
        using var gw = new StubGateway(_ => StubGateway.Json(
            """{"limit":100,"used":40,"remaining":60,"resetUtc":"2026-01-02T00:00:00.000Z"}"""));
        DefaultLlm.GatewayBaseUrl = gw.BaseUrl;
        using var client = new OpenAiCompatibleClient();

        var status = await client.PeekBuiltinQuotaAsync();

        Assert.True(status.Ok, status.Message);
        Assert.Equal(60, status.Remaining);
        Assert.Null(BuiltinQuota.Ticket);
        Assert.Equal("GET", Assert.Single(gw.Requests).Method);
    }

    [Fact]
    public async Task Chat_AfterConsume_CarriesTheTicketAndNoBearerKey()
    {
        using var gw = new StubGateway(req => req.Path == "/v1/quota"
            ? StubGateway.Json("""{"limit":100,"used":1,"remaining":99,"resetUtc":"2026-01-02T00:00:00.000Z","ticket":"tk-xyz"}""")
            : StubGateway.Json("""{"choices":[{"message":{"role":"assistant","content":"stub answer"},"finishReason":"stop"}]}"""));
        DefaultLlm.GatewayBaseUrl = gw.BaseUrl;
        using var client = new OpenAiCompatibleClient();

        await client.ConsumeBuiltinQuotaAsync();
        var reply = await Send(client);

        Assert.Equal("stub answer", LlmJson.GetContentString(reply.Choices![0].Message!.Content));

        var chat = gw.Requests[1];
        Assert.Equal("/v1/chat/completions", chat.Path);
        Assert.Equal("tk-xyz", chat.Header(BuiltinQuota.TicketHeader));
        Assert.Equal(BuiltinQuota.DeviceId, chat.Header(BuiltinQuota.DeviceHeader));
        // The whole point of the gateway: the add-in never sends a credential upstream.
        Assert.False(chat.HasHeader("Authorization"));
        Assert.Contains("\"model\":\"MiniMax-M3\"", chat.Body);
    }

    [Fact]
    public async Task Chat_WithoutATicket_IsRefusedBeforeAnyNetworkCall()
    {
        using var gw = new StubGateway(_ => StubGateway.Json("{}"));
        DefaultLlm.GatewayBaseUrl = gw.BaseUrl;
        using var client = new OpenAiCompatibleClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(client));

        Assert.Contains("ticket", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gw.Requests);
    }

    [Fact]
    public async Task Consume_On429_ReportsTheDailyLimitAndClearsTheTicket()
    {
        var limited = false;
        using var gw = new StubGateway(_ => limited
            ? new StubResponse(429, """
              {"error":{"message":"LoadingAI daily limit reached (100 requests/day). Try again after 2026-01-02T00:00:00.000Z, or add your own API key in Settings.","code":"daily_limit","type":"rate_limit_error"},"limit":100,"used":100,"remaining":0,"resetUtc":"2026-01-02T00:00:00.000Z"}
              """)
            : StubGateway.Json("""{"limit":100,"used":99,"remaining":1,"resetUtc":"2026-01-02T00:00:00.000Z","ticket":"tk-last"}"""));
        DefaultLlm.GatewayBaseUrl = gw.BaseUrl;
        using var client = new OpenAiCompatibleClient();

        await client.ConsumeBuiltinQuotaAsync();
        Assert.Equal("tk-last", BuiltinQuota.Ticket);

        limited = true;
        var status = await client.ConsumeBuiltinQuotaAsync();

        Assert.False(status.Ok);
        Assert.True(status.Exhausted);
        Assert.Equal(100, status.Limit);
        Assert.Equal(0, status.Remaining);
        Assert.Contains("100 requests/day", BuiltinQuota.LimitMessage(status));
        Assert.Null(BuiltinQuota.Ticket);
    }

    [Fact]
    public async Task Consume_WhenTheGatewayIsDown_ReportsUnreachable()
    {
        var port = StubGateway.FreePort();
        DefaultLlm.GatewayBaseUrl = $"http://127.0.0.1:{port}/v1";
        using var client = new OpenAiCompatibleClient();

        var status = await client.ConsumeBuiltinQuotaAsync();

        Assert.False(status.Ok);
        Assert.False(status.Exhausted);
        Assert.Contains("unreachable", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(status.Message, status.Describe());
        Assert.DoesNotContain("minimax", status.Describe(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"{DefaultLlm.DisplayName} unavailable.", new QuotaStatus { Ok = false }.Describe());
    }

    [Fact]
    public void ShippedGatewayUrl_IsAnAnonymousHttpsEndpoint()
    {
        Assert.Equal("https://gateway.loadingtechnology.app/v1", DefaultLlm.PublicGatewayBaseUrl);
        Assert.DoesNotContain("minimax", DefaultLlm.PublicGatewayBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    sealed record StubResponse(int Status, string Body);

    sealed class StubGateway : IDisposable
    {
        readonly TcpListener _listener;
        readonly Func<StubRequest, StubResponse> _handler;
        readonly List<StubRequest> _requests = new();

        public StubGateway(Func<StubRequest, StubResponse> handler)
        {
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

        public IReadOnlyList<StubRequest> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public static StubResponse Json(string body) => new(200, body);

        async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient socket;
                try { socket = await _listener.AcceptTcpClientAsync(); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    using var client = socket;
                    using var stream = client.GetStream();
                    var request = await ReadRequest(stream);
                    if (request == null) return;
                    lock (_requests) _requests.Add(request);
                    var response = _handler(request);
                    await WriteResponse(stream, response);
                });
            }
        }

        static async Task<StubRequest?> ReadRequest(NetworkStream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, 0, 1) == 0) return null;
                head.Append((char)one[0]);
                if (head.Length > 65536) return null;
            }

            var lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            var method = parts[0];
            var path = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length - 1; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            var body = "";
            if (headers.TryGetValue("Content-Length", out var lenText) && int.TryParse(lenText, out var len) && len > 0)
            {
                var buffer = new byte[len];
                var read = 0;
                while (read < len)
                {
                    var n = await stream.ReadAsync(buffer, read, len - read);
                    if (n == 0) break;
                    read += n;
                }
                body = Encoding.UTF8.GetString(buffer, 0, read);
            }

            return new StubRequest(method, path, headers, body);
        }

        static async Task WriteResponse(NetworkStream stream, StubResponse response)
        {
            var body = Encoding.UTF8.GetBytes(response.Body);
            var head = $"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Error")}\r\n" +
                       "Content-Type: application/json; charset=utf-8\r\n" +
                       $"Content-Length: {body.Length}\r\n" +
                       "Connection: close\r\n\r\n";
            var headBytes = Encoding.UTF8.GetBytes(head);
            await stream.WriteAsync(headBytes, 0, headBytes.Length);
            await stream.WriteAsync(body, 0, body.Length);
            await stream.FlushAsync();
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }

    sealed record StubRequest(string Method, string Path, Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

        public bool HasHeader(string name) => Headers.ContainsKey(name);
    }
}
