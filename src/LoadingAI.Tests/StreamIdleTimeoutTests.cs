using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class StreamIdleTimeoutTests
{
    /// <summary>A body that never yields a byte, like a provider that stalls mid-stream.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Stalls every streaming request; answers plain JSON for the non-stream fallback.</summary>
    private sealed class StallThenJsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var streaming = request.Headers.Accept.Any(a => a.MediaType == "text/event-stream");
            HttpContent content = streaming
                ? new StreamContent(new StalledStream())
                : new StringContent("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""", Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Yields each chunk after its delay, then ends: a provider that is slow but alive.</summary>
    private sealed class DelayedStream : Stream
    {
        private readonly Queue<(int DelayMs, byte[] Bytes)> _chunks;
        public DelayedStream(params (int DelayMs, string Text)[] chunks)
            => _chunks = new Queue<(int DelayMs, byte[] Bytes)>(chunks.Select(c => (c.DelayMs, Encoding.UTF8.GetBytes(c.Text))));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_chunks.Count == 0) return 0;
            var (delayMs, bytes) = _chunks.Dequeue();
            await Task.Delay(delayMs, ct);
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            return bytes.Length;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Streams one delayed SSE reply; any non-stream retry would answer "fallback" and be counted.</summary>
    private sealed class SlowFirstLineHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var streaming = request.Headers.Accept.Any(a => a.MediaType == "text/event-stream");
            HttpContent content = streaming
                ? new StreamContent(new DelayedStream((600,
                    "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n")))
                : new StringContent("""{"choices":[{"message":{"role":"assistant","content":"fallback"},"finish_reason":"stop"}]}""", Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task ReadLine_ThrowsTimeoutWhenNothingArrives()
    {
        using var reader = new StreamReader(new StalledStream());
        var sw = Stopwatch.StartNew();
        var call = OpenAiCompatibleClient.ReadLineWithIdleTimeoutAsync(reader, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(call, finished);
        await Assert.ThrowsAsync<TimeoutException>(() => call);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadLine_StopsPromptlyOnCancel()
    {
        using var reader = new StreamReader(new StalledStream());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var call = OpenAiCompatibleClient.ReadLineWithIdleTimeoutAsync(reader, TimeSpan.FromMinutes(5), cts.Token);

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(call, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task ReadLine_SlowButAliveStreamIsReadFully()
    {
        using var reader = new StreamReader(new DelayedStream((0, "first\n"), (200, "second\n")));
        var lines = new List<string?>();
        var read = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
                lines.Add(await OpenAiCompatibleClient.ReadLineWithIdleTimeoutAsync(reader, TimeSpan.FromSeconds(2), CancellationToken.None));
        });

        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(read, finished);
        await read;
        Assert.Equal(new string?[] { "first", "second", null }, lines);
    }

    [Fact]
    public async Task SlowFirstLine_UsesFirstLineAllowanceInsteadOfIdleTimeout()
    {
        var handler = new SlowFirstLineHandler();
        using var client = new OpenAiCompatibleClient(handler)
        {
            StreamIdleTimeout = TimeSpan.FromMilliseconds(200),
            StreamFirstLineTimeout = TimeSpan.FromSeconds(2)
        };
        var provider = new ProviderConfig { Id = "fake", Name = "Fake", BaseUrl = "http://fake.test/v1", Model = "m", ApiKey = "k", ApiKind = "openai" };
        var deltas = new List<string>();
        var call = client.ChatAsync(provider, new List<ChatMessage> { ChatMessage.FromText("user", "hi") }, null, d => deltas.Add(d));

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(call, finished);
        Assert.Equal("hello", LlmJson.GetContentString((await call).Choices![0].Message!.Content));
        Assert.Equal("hello", string.Concat(deltas));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task StalledStream_FallsBackInsteadOfHanging()
    {
        using var client = new OpenAiCompatibleClient(new StallThenJsonHandler()) { StreamIdleTimeout = TimeSpan.FromMilliseconds(300) };
        var provider = new ProviderConfig { Id = "fake", Name = "Fake", BaseUrl = "http://fake.test/v1", Model = "m", ApiKey = "k", ApiKind = "openai" };
        var call = client.ChatAsync(provider, new List<ChatMessage> { ChatMessage.FromText("user", "hi") }, null, _ => { });

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(call, finished);
        Assert.Equal("ok", LlmJson.GetContentString((await call).Choices![0].Message!.Content));
    }
}
