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

    [Fact]
    public async Task ReadLine_ThrowsTimeoutWhenNothingArrives()
    {
        using var reader = new StreamReader(new StalledStream());
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            OpenAiCompatibleClient.ReadLineWithIdleTimeoutAsync(reader, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadLine_StopsPromptlyOnCancel()
    {
        using var reader = new StreamReader(new StalledStream());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiCompatibleClient.ReadLineWithIdleTimeoutAsync(reader, TimeSpan.FromMinutes(5), cts.Token));
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
