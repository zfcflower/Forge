using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Prompts;
using Forge.App.ViewModels;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// End to end through the whole stack the shell actually builds: a real <see cref="OpenAiCompatibleProvider"/> over a fake
/// transport (so the 429 goes through the real status/body/header parsing), the shell's retry middleware with its real waits,
/// the real agent loop, and the real session view model with its timeline and footnote.
/// <para>
/// The bodies are the shape the incident left in <c>turn_metrics</c> — the OpenCode gateway's
/// <c>{"error":{"type":"server_error",…}}</c> overload refusal. Every case lets the server's own <c>Retry-After: 1</c> decide
/// the wait, so the middleware runs with nothing injected and the run still costs seconds rather than the ladder's 32.
/// </para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class OverloadEndToEndTests
{
    private const string OverloadBody =
        """{"error":{"type":"server_error","message":"The inference service is overloaded. Please try again later."}}""";

    /// <summary>The sentence the adapter composes from that body, as it reached the user's transcript in the incident.</summary>
    private const string Refusal =
        "opencode returned 429 TooManyRequests: The inference service is overloaded. Please try again later. (type: server_error)";

    [Fact]
    public Task A_busy_gateway_is_retried_once_and_the_turn_still_answers() => SharedUi.RunAsync(async () =>
    {
        var gateway = new Gateway(attempt => attempt == 1 ? Busy() : Answer());
        using var session = await OverloadSession.StartAsync(Adapter(gateway), mechanicalWaits: false);

        await session.RunTurnAsync("ship it", expectRuns: 1);

        var card = Assert.IsType<TurnCardViewModel>(session.Tab.Transcript.Current);
        Assert.Equal(2, gateway.Attempts);
        Assert.Equal(TurnEndStatus.Completed, card.EndStatus);

        // The server's Retry-After (1s) is what the middleware waited and what the line reports: proof the header was parsed
        // and honoured rather than the ladder's first step being used.
        Assert.Equal(
            [RateLimitStrings.Retrying("opencode", 2, 5, TimeSpan.FromSeconds(1), RetryReason.Overloaded).Resolve()],
            session.Notes());
    });

    [Fact]
    public Task A_gateway_that_stays_busy_ends_the_turn_with_the_attempts_on_its_footnote() => SharedUi.RunAsync(async () =>
    {
        var gateway = new Gateway(_ => Busy());
        using var session = await OverloadSession.StartAsync(Adapter(gateway), mechanicalWaits: false);

        await session.RunTurnAsync("ship it", expectRuns: 1);

        var card = Assert.IsType<TurnCardViewModel>(session.Tab.Transcript.Current);
        Assert.Equal(5, gateway.Attempts);
        Assert.Equal(TurnEndStatus.Failed, card.EndStatus);
        Assert.Equal(4, session.Notes().Count);

        // Four waits of one second each, all of them the server's own number.
        Assert.Equal(SessionStrings.TurnFailedRetried(Refusal, 4, TimeSpan.FromSeconds(4)).Resolve(), card.Footnote);
    });

    [Fact]
    public Task The_automatic_round_sends_the_continue_instruction_through_the_real_gateway() => SharedUi.RunAsync(async () =>
    {
        // Five refusals spend the ladder on the first turn; the gateway answers the automatic round's request.
        var gateway = new Gateway(attempt => attempt <= 5 ? Busy() : Answer());
        using var session = await OverloadSession.StartAsync(Adapter(gateway), mechanicalWaits: false);

        await session.RunTurnAsync("ship it", expectRuns: 2, autoResume: true);

        Assert.Equal(2, session.Turns);
        var resumed = Assert.IsType<TurnCardViewModel>(session.Tab.Transcript.Current);
        Assert.Equal(TurnEndStatus.Completed, resumed.EndStatus);
        // The continue instruction rides the automatic round and only that round: the first turn's five attempts never carried
        // it, so nothing is being replayed — the session is being asked to go on from where it stopped.
        Assert.Contains(AppPromptTexts.OverloadContinue, gateway.Bodies[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(
            gateway.Bodies.Take(gateway.Bodies.Count - 1),
            static body => body.Contains(AppPromptTexts.OverloadContinue, StringComparison.Ordinal));
    });

    [Fact]
    public Task A_stream_that_dies_after_its_first_delta_is_never_retried() => SharedUi.RunAsync(async () =>
    {
        // The retry layer owns the window before the first event and nothing after it: replaying this would print the partial
        // answer twice and re-run whatever the model already asked for.
        var gateway = new Gateway(_ => FakeSse.Ok(new AbortingStream(FakeSse.Frame("""{"choices":[{"delta":{"content":"half"}}]}"""))));
        using var session = await OverloadSession.StartAsync(Adapter(gateway), mechanicalWaits: false);

        await session.RunTurnAsync("ship it", expectRuns: 1);

        Assert.Equal(1, gateway.Attempts);
        var card = Assert.IsType<TurnCardViewModel>(session.Tab.Transcript.Current);
        Assert.Equal(TurnEndStatus.Failed, card.EndStatus);
        Assert.Contains("response stream failed", card.Footnote, StringComparison.Ordinal);
        Assert.DoesNotContain("已重试", card.Footnote!, StringComparison.Ordinal);
    });

    private static OpenAiCompatibleProvider Adapter(HttpMessageHandler gateway) =>
        new(new ProviderConfig("opencode", "openai-compatible", "https://gateway.example/v1", "k"), new HttpClient(gateway));

    private static HttpResponseMessage Busy() => new(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent(OverloadBody, Encoding.UTF8, "application/json"),
        Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)) },
    };

    private static HttpResponseMessage Answer() => FakeSse.Ok(FakeSse.Body(
    [
        FakeSse.Frame("""{"choices":[{"delta":{"content":"done"}}]}"""),
        FakeSse.Frame("""{"choices":[{"delta":{},"finish_reason":"stop"}]}"""),
        FakeSse.Done,
    ]));

    /// <summary>A gateway that answers from a script and keeps every request body the adapter wrote.</summary>
    private sealed class Gateway(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            return answer(Attempts);
        }
    }

    /// <summary>A response body that serves one frame and then fails the way a dropped connection does.</summary>
    private sealed class AbortingStream(string frame) : Stream
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes(frame);
        private int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (offset >= bytes.Length)
            {
                throw new IOException("The response ended prematurely.");
            }

            var take = Math.Min(buffer.Length, bytes.Length - offset);
            bytes.AsMemory(offset, take).CopyTo(buffer);
            offset += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
