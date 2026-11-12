using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

public class HandoverSummaryTests
{
    private static readonly IReadOnlyList<ChatMessage> History =
    [
        ChatMessage.User("Add retry to the uploader."),
        ChatMessage.Assistant("Added RetryPolicy in Uploader.cs."),
        new ChatMessage(MessageRole.Tool, [new ToolResultBlock("t1", "build ok")]),
    ];

    [Fact]
    public async Task Uses_its_own_prompt_and_returns_the_persistable_message_text()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("  ## Goal\nRetry uploads.  "));

        var text = await HandoverSummary.GenerateAsync(
            provider,
            new ModelSettings("m", ThinkingEffort.High, 2000),
            History,
            " M src/Uploader.cs ",
            CancellationToken.None);

        Assert.Equal(HandoverSummary.MessageHeading + "\n\n## Goal\nRetry uploads.", text);
        var request = Assert.Single(provider.Requests);
        Assert.Equal(HandoverSummary.Prompt, request.SystemPrompt);
        Assert.NotEqual(SummaryCompactor.SummarizerPrompt, request.SystemPrompt);
        foreach (var section in new[] { "## Goal", "## Done", "## Uncommitted changes", "## Open items", "## Next steps" })
        {
            Assert.Contains(section, HandoverSummary.Prompt, StringComparison.Ordinal);
        }

        Assert.Empty(request.Tools);
        Assert.Equal(new ModelSettings("m", ThinkingEffort.Off, 2000), request.Model);
        var input = Turn.LastUserText(request);
        Assert.Contains("user: Add retry to the uploader.", input, StringComparison.Ordinal);
        Assert.Contains("assistant: Added RetryPolicy in Uploader.cs.", input, StringComparison.Ordinal);
        Assert.Contains("build ok", input, StringComparison.Ordinal);
        Assert.EndsWith("# Working tree\n\nM src/Uploader.cs", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_given_prompt_replaces_the_built_in_one_and_leaves_the_rest_of_the_request_alone()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("summary"));

        var text = await HandoverSummary.GenerateAsync(
            provider,
            new ModelSettings("m", ThinkingEffort.High, 2000),
            History,
            " M src/Uploader.cs ",
            CancellationToken.None,
            prompt: "CUSTOM-HANDOVER-PROMPT");

        var request = Assert.Single(provider.Requests);
        Assert.Equal("CUSTOM-HANDOVER-PROMPT", request.SystemPrompt);
        Assert.Equal(HandoverSummary.MessageHeading + "\n\nsummary", text);
        Assert.Empty(request.Tools);
        Assert.Equal(new ModelSettings("m", ThinkingEffort.Off, 2000), request.Model);
        Assert.EndsWith("# Working tree\n\nM src/Uploader.cs", Turn.LastUserText(request), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public async Task A_null_or_blank_prompt_falls_back_to_the_built_in_one(string? prompt)
    {
        var provider = new FakeProvider((_, _) => Turn.Say("summary"));

        await HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, CancellationToken.None, prompt);

        Assert.Equal(HandoverSummary.Prompt, Assert.Single(provider.Requests).SystemPrompt);
    }

    [Fact]
    public async Task The_handover_slot_of_a_template_set_can_be_passed_as_the_prompt()
    {
        using var fixture = new Forge.Core.Tests.Prompts.Templates.TemplateFixture();
        fixture.Global(PromptTemplateKind.Slot, Forge.Core.Prompts.Templates.PromptSlotIds.HandoverSummary, "Write a haiku.\n");
        var slot = fixture.Resolve().Slot(Forge.Core.Prompts.Templates.PromptSlotIds.HandoverSummary);
        var provider = new FakeProvider((_, _) => Turn.Say("summary"));

        await HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, CancellationToken.None, slot);

        Assert.Equal("Write a haiku.", Assert.Single(provider.Requests).SystemPrompt);
    }

    [Fact]
    public async Task Reports_no_uncommitted_changes_when_none_are_given()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("summary"));
        await HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, CancellationToken.None);
        Assert.EndsWith("No uncommitted changes.", Turn.LastUserText(provider.Requests[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_the_completed_message_when_no_deltas_were_streamed()
    {
        var provider = new FakeProvider((_, _) =>
            [new MessageCompleted(ChatMessage.Assistant("from completed"), new TokenUsage(1, 1), StopReason.EndTurn)]);
        var text = await HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, CancellationToken.None);
        Assert.EndsWith("from completed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_history_keeps_the_most_recent_messages()
    {
        var history = Enumerable.Range(0, 200)
            .Select(index => ChatMessage.User($"m{index:D3} " + new string('x', 5_000)))
            .ToArray();
        var provider = new FakeProvider((_, _) => Turn.Say("summary"));
        await HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), history, null, CancellationToken.None);

        var input = Turn.LastUserText(provider.Requests[0]);
        Assert.Contains("[earlier messages omitted for length]", input, StringComparison.Ordinal);
        Assert.Contains("m199", input, StringComparison.Ordinal);
        Assert.DoesNotContain("m000", input, StringComparison.Ordinal);
        Assert.True(input.Length < 420_000);
    }

    [Fact]
    public async Task Empty_answer_is_a_handover_summary_failure()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("   "));
        var error = await Assert.ThrowsAsync<HandoverSummaryException>(() =>
            HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, CancellationToken.None));
        Assert.Contains("no text", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_failure_is_wrapped_with_its_cause()
    {
        var cause = new ProviderException("HTTP 529 overloaded", retryable: true, statusCode: 529);
        var provider = new FakeProvider((_, _, _) => Task.FromException<IReadOnlyList<ChatStreamEvent>>(cause));
        var error = await Assert.ThrowsAsync<HandoverSummaryException>(() =>
            HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, CancellationToken.None));
        Assert.Same(cause, error.InnerException);
        Assert.Contains("HTTP 529 overloaded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_before_the_call_never_reaches_the_provider()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("summary"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, cancellation.Token));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Cancellation_while_streaming_surfaces_as_cancellation_not_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new HangingProvider(cancellation);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, cancellation.Token));
        Assert.IsNotType<HandoverSummaryException>(error);
        Assert.True(provider.Started);
    }

    [Fact]
    public async Task Provider_cancellation_without_our_token_is_a_failure()
    {
        var provider = new FakeProvider((_, _, _) =>
            Task.FromException<IReadOnlyList<ChatStreamEvent>>(new TaskCanceledException("HTTP timeout")));
        var error = await Assert.ThrowsAsync<HandoverSummaryException>(() =>
            HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), History, null, CancellationToken.None));
        Assert.IsType<TaskCanceledException>(error.InnerException);
    }

    [Fact]
    public async Task Rejects_missing_arguments()
    {
        var provider = new FakeProvider((_, _) => Turn.Say("x"));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HandoverSummary.GenerateAsync(null!, new ModelSettings("m"), History, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HandoverSummary.GenerateAsync(provider, null!, History, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HandoverSummary.GenerateAsync(provider, new ModelSettings("m"), null!, null, CancellationToken.None));
    }

    [Fact]
    public void Inheritance_note_names_head_handover_commit_and_carried_changes()
    {
        var text = InheritanceNote.Build(" abc123 ", "def456", uncommittedChangesCarried: true);

        Assert.StartsWith("[Session handover]", text, StringComparison.Ordinal);
        Assert.Contains("- Current HEAD: abc123\n", text, StringComparison.Ordinal);
        Assert.Contains("- Handover commit: def456\n", text, StringComparison.Ordinal);
        Assert.Contains("- Uncommitted changes: carried over into this working tree\n", text, StringComparison.Ordinal);
        Assert.EndsWith("Files read earlier in this history may have changed since. Re-read a file before editing it.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Inheritance_note_without_handover_commit_or_carried_changes()
    {
        var message = InheritanceNote.Message("abc123", null, uncommittedChangesCarried: false);

        Assert.Equal(MessageRole.User, message.Role);
        var text = Turn.Text(message);
        Assert.Contains("- Handover commit: none\n", text, StringComparison.Ordinal);
        Assert.Contains("- Uncommitted changes: not carried over\n", text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => InheritanceNote.Build(" ", null, false));
    }

    /// <summary>Emits one delta, cancels the caller's token, then waits on it like a stalled stream.</summary>
    private sealed class HangingProvider(CancellationTokenSource cancellation) : IChatProvider
    {
        public bool Started { get; private set; }

        public string Id => "hanging";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Started = true;
            yield return new TextDelta("partial");
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            yield return new TextDelta("never");
        }
    }
}
