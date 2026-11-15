using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// The stream-level half of the credential contract (App SPEC 29): <see cref="CredentialedChatProvider"/> resolves the
/// credential on first use, answers an authentication refusal that arrives <em>before</em> the stream produced anything
/// with one freshly resolved attempt, and surfaces the same refusal once the first event is out — replaying a half-read
/// turn would print what the user already saw a second time. The credential layer's own rules (serial refreshes,
/// conditional clearing) are covered by <c>ProviderCredentialsTests</c> and the per-provider authentication suites.
/// </summary>
public sealed class CredentialedChatProviderTests : IDisposable
{
    private const string AccessToken = "sk-ant-oat01-test";

    private readonly string root = TestTempFiles.NewDirectory("forge-credentialed-provider-");

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_refusal_before_the_first_event_is_answered_by_one_freshly_resolved_attempt(int status)
    {
        var builds = new List<string>();
        var provider = Provider(builds, Refuse.OnFirstMove(status));

        // The credential is resolved on first use, never at construction: building a provider costs no read and no call.
        Assert.Empty(builds);

        var events = await DrainAsync(provider);

        // Exactly one retry, against a provider built from a newly resolved credential — the refused one is dropped, so
        // reusing it would have refused again instead of answering.
        Assert.Equal([AccessToken, AccessToken], builds);
        Assert.Equal(
            ["the answer"],
            [.. events.OfType<TextDelta>().Select(static delta => delta.Text)]);
        Assert.Single(events.OfType<MessageCompleted>());
    }

    [Fact]
    public async Task A_refusal_after_the_first_event_surfaces_instead_of_replaying_the_turn()
    {
        var builds = new List<string>();
        var provider = Provider(builds, Refuse.AfterFirstEvent(401));

        var seen = new List<string>();
        var failure = await Record.ExceptionAsync(async () =>
        {
            await foreach (var item in provider.StreamAsync(Request(), CancellationToken.None))
            {
                if (item is TextDelta delta)
                {
                    seen.Add(delta.Text);
                }
            }
        });

        Assert.Equal(401, Assert.IsType<ProviderException>(failure).StatusCode);
        Assert.Single(builds);
        // What already reached the user stays reached exactly once: no second attempt, so no duplicated answer.
        Assert.Equal(["half an answer"], seen);
    }

    [Fact]
    public async Task A_failure_that_is_not_about_authentication_is_never_retried()
    {
        var builds = new List<string>();
        var provider = Provider(builds, Refuse.OnFirstMove(500));

        var failure = await Record.ExceptionAsync(() => DrainAsync(provider));

        Assert.Equal(500, Assert.IsType<ProviderException>(failure).StatusCode);
        Assert.Single(builds);
    }

    public void Dispose() => TestTempFiles.DeleteQuietly(root);

    /// <summary>
    /// A provider over a Claude login file whose token is not due for a refresh, so resolving the credential reads the
    /// file and never dials out: the <see cref="ThrowingHandler"/> turns any refresh attempt into a test failure.
    /// </summary>
    private CredentialedChatProvider Provider(List<string> builds, Refuse firstAttempt)
    {
        var credentialsPath = Path.Combine(root, "credentials.json");
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        File.WriteAllText(credentialsPath, $$"""{ "claudeAiOauth": { "accessToken": "{{AccessToken}}", "expiresAt": {{expiresAt}} } }""");
        var credentials = new ProviderCredentials(
            new HttpClient(new ThrowingHandler()) { Timeout = Timeout.InfiniteTimeSpan },
            new ProviderSecretStore(root),
            credentialsPath);

        return new CredentialedChatProvider(
            "claude",
            ProviderCredentialKind.ClaudeSubscription,
            "https://api.anthropic.com",
            credentials,
            (token, _, _, _, _) =>
            {
                builds.Add(token);
                return new ScriptedProvider(builds.Count == 1 ? firstAttempt : Refuse.Never);
            });
    }

    private static ChatRequest Request() =>
        new(string.Empty, [], [ChatMessage.User("hello")], new ModelSettings("claude-sonnet-4-5"));

    private static async Task<List<ChatStreamEvent>> DrainAsync(IChatProvider provider)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var item in provider.StreamAsync(Request(), CancellationToken.None))
        {
            events.Add(item);
        }

        return events;
    }

    /// <summary>Where a scripted attempt refuses, and with what status; <see cref="Never"/> streams a whole answer.</summary>
    private sealed record Refuse(int Status, int EventsBeforeRefusal)
    {
        public static Refuse Never { get; } = new(0, 0);

        public static Refuse OnFirstMove(int status) => new(status, 0);

        public static Refuse AfterFirstEvent(int status) => new(status, 1);

        public bool Refuses => Status != 0;
    }

    /// <summary>One attempt: it refuses where it was told to, otherwise it streams one complete answer.</summary>
    private sealed class ScriptedProvider(Refuse refuse) : IChatProvider
    {
        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            for (var sent = 0; sent < refuse.EventsBeforeRefusal; sent++)
            {
                yield return new TextDelta("half an answer");
            }

            if (refuse.Refuses)
            {
                throw new ProviderException("refused", retryable: true, statusCode: refuse.Status);
            }

            yield return new TextDelta("the answer");
            yield return new MessageCompleted(
                ChatMessage.Assistant("the answer"),
                new TokenUsage(3, 2),
                StopReason.EndTurn);
        }
    }
}
