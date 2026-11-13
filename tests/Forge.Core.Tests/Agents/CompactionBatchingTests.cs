using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// A range too large for one summarizer request is summarized in batches (the later ones updating the running summary),
/// split-turn blocks never stack, and the context estimate counts the tool definitions a request carries.
/// </summary>
public sealed class CompactionBatchingTests
{
    private const string Workspace = "D:/work/batching";

    // A 2000-token window leaves too little room for the per-request formula, so every request carries the minimum
    // conversation budget of 4000 characters: two of the ~1.8 KB units below fit, a third does not.
    private const int SmallWindow = 2_000;

    private static readonly CancellationToken Ct = CancellationToken.None;

    // ---------------------------------------------------------------- batching

    [Fact]
    public async Task A_range_over_the_budget_is_summarized_in_batches_that_update_the_running_summary()
    {
        var fixture = new Fixture((_, index) => Turn.Say("SUM-" + (index + 1)), SmallWindow);
        var session = await fixture.SeedAsync(
            ChatMessage.User("MSG-1 " + new string('a', 1_800)),
            Read("c1", "src/A.cs"),
            Result("c1", "MSG-2 " + new string('r', 1_794)),
            ChatMessage.User("MSG-3 " + new string('c', 1_800)),
            ChatMessage.Assistant("MSG-4 " + new string('d', 1_800)),
            ChatMessage.User("MSG-5 " + new string('e', 1_800)),
            Write("c2", "src/B.cs"),
            Result("c2", "MSG-6 " + new string('w', 1_794)),
            ChatMessage.User("tail"));
        var root = await fixture.ResumeAsync(session);

        var result = await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 10), Ct);

        Assert.NotNull(result);
        var prompts = fixture.SummarizerRequests.Select(Prompt).ToList();
        Assert.Equal(3, prompts.Count);

        Assert.DoesNotContain("<previous-summary>", prompts[0], StringComparison.Ordinal);
        Assert.EndsWith(SummaryCompactor.InitialInstruction, prompts[0], StringComparison.Ordinal);
        Assert.Contains("<previous-summary>\nSUM-1\n</previous-summary>", prompts[1], StringComparison.Ordinal);
        Assert.EndsWith(SummaryCompactor.UpdateInstruction, prompts[1], StringComparison.Ordinal);
        Assert.Contains("<previous-summary>\nSUM-2\n</previous-summary>", prompts[2], StringComparison.Ordinal);
        Assert.EndsWith(SummaryCompactor.UpdateInstruction, prompts[2], StringComparison.Ordinal);

        // Every summarized message reaches exactly one request, in order, the newest ones included.
        string[][] expected = [["MSG-1", "MSG-2"], ["MSG-3", "MSG-4"], ["MSG-5", "MSG-6"]];
        for (var batch = 0; batch < expected.Length; batch++)
        {
            for (var marker = 1; marker <= 6; marker++)
            {
                var name = "MSG-" + marker;
                Assert.Equal(expected[batch].Contains(name), prompts[batch].Contains(name, StringComparison.Ordinal));
            }
        }

        Assert.All(prompts, prompt => Assert.DoesNotContain("tail", prompt, StringComparison.Ordinal));

        // The call and its result travel together.
        Assert.Contains("read(path=\"src/A.cs\")\n\n[Tool result]: MSG-2", prompts[0], StringComparison.Ordinal);
        Assert.Contains("write(path=\"src/B.cs\", content=\"body\")\n\n[Tool result]: MSG-6", prompts[2], StringComparison.Ordinal);

        // One layer-2 row: the last running summary plus the files of every batch.
        var row = Assert.Single(await fixture.Store.GetSummariesAsync(session, Ct));
        Assert.Equal((SummaryLayers.SessionLayer, 1L, 8L), (row.Layer, row.FromSeq, row.ToSeq));
        Assert.Equal("SUM-3\n\n<read-files>\nsrc/A.cs\n</read-files>\n\n<modified-files>\nsrc/B.cs\n</modified-files>", row.Text);
        Assert.Equal(["tail"], root.History.Skip(1).Select(Turn.Text));
    }

    [Fact]
    public async Task Without_a_known_window_the_same_range_fits_one_request()
    {
        var fixture = new Fixture((_, index) => Turn.Say("SUM-" + (index + 1)), window: null);
        var session = await fixture.SeedAsync(
            ChatMessage.User("MSG-1 " + new string('a', 1_800)),
            ChatMessage.Assistant("MSG-2 " + new string('b', 1_800)),
            ChatMessage.User("MSG-3 " + new string('c', 1_800)),
            ChatMessage.Assistant("MSG-4 " + new string('d', 1_800)),
            ChatMessage.User("tail"));
        var root = await fixture.ResumeAsync(session);

        await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 10), Ct);

        var prompt = Prompt(Assert.Single(fixture.SummarizerRequests));
        Assert.Contains("MSG-1", prompt, StringComparison.Ordinal);
        Assert.Contains("MSG-4", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_single_message_larger_than_a_request_is_cut_in_the_middle_with_a_marker()
    {
        var fixture = new Fixture((_, index) => Turn.Say("SUM-" + (index + 1)), SmallWindow);
        var session = await fixture.SeedAsync(
            ChatMessage.User("HEAD-MARK " + new string('x', 20_000) + " TAIL-MARK"),
            ChatMessage.Assistant("MSG-2 short answer"),
            ChatMessage.User("tail"));
        var root = await fixture.ResumeAsync(session);

        await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 10), Ct);

        var prompts = fixture.SummarizerRequests.Select(Prompt).ToList();
        Assert.Equal(2, prompts.Count);
        var conversation = Conversation(prompts[0]);
        Assert.StartsWith("[User]: HEAD-MARK", conversation, StringComparison.Ordinal);
        Assert.EndsWith(" TAIL-MARK", conversation, StringComparison.Ordinal);
        Assert.Contains(" chars omitted …]", conversation, StringComparison.Ordinal);
        Assert.True(conversation.Length <= SummaryCompactor.MinRequestConversationChars, $"conversation of {conversation.Length} chars");
        Assert.Contains("[Assistant]: MSG-2 short answer", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_split_turn_prefix_over_the_budget_is_batched_with_the_turn_prefix_update_instruction()
    {
        var fixture = new Fixture((_, index) => Turn.Say("PREFIX-" + (index + 1)), SmallWindow);
        var messages = new List<ChatMessage> { ChatMessage.User("build the feature") };
        for (var index = 1; index <= 6; index++)
        {
            messages.Add(Read("c" + index, $"f{index}.cs"));
            messages.Add(Result("c" + index, $"R{index} " + new string('r', 1_797)));
        }

        messages.Add(ChatMessage.Assistant("final words"));
        var session = await fixture.SeedAsync([.. messages]);
        var root = await fixture.ResumeAsync(session);

        // keepRecent 500 keeps the sixth call, its result and the final words: the cut lands inside the only turn.
        await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 500), Ct);

        var prompts = fixture.SummarizerRequests.Select(Prompt).ToList();
        Assert.Equal(3, prompts.Count);
        Assert.EndsWith(SummaryCompactor.TurnPrefixInstruction, prompts[0], StringComparison.Ordinal);
        Assert.StartsWith("<conversation>\n[User]: build the feature", prompts[0], StringComparison.Ordinal);
        for (var index = 1; index < prompts.Count; index++)
        {
            Assert.EndsWith(SummaryCompactor.TurnPrefixUpdateInstruction, prompts[index], StringComparison.Ordinal);
            Assert.Contains($"<previous-summary>\nPREFIX-{index}\n</previous-summary>", prompts[index], StringComparison.Ordinal);
        }

        Assert.Contains("R5 ", prompts[^1], StringComparison.Ordinal);
        Assert.All(prompts, prompt => Assert.DoesNotContain("R6 ", prompt, StringComparison.Ordinal));
        var row = Assert.Single(await fixture.Store.GetSummariesAsync(session, Ct));
        Assert.StartsWith(SummaryCompactor.SplitTurnHeading + "\n\nPREFIX-3\n\n<read-files>\nf1.cs\n", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_middle_batch_persists_nothing_and_sends_no_further_batch()
    {
        var fixture = new Fixture(
            (_, index) => index == 1 ? throw new ProviderException("upstream 500", retryable: false, statusCode: 500) : Turn.Say("SUM-" + (index + 1)),
            SmallWindow);
        var session = await fixture.SeedAsync(ThreeBatches());
        var root = await fixture.ResumeAsync(session);
        var before = Wire(root.History);

        await Assert.ThrowsAsync<CompactionFailedException>(() => fixture.Compactor.CompactAsync(root, Manual(keepRecent: 10), Ct));

        Assert.Equal(2, fixture.SummarizerRequests.Count);
        Assert.Empty(await fixture.Store.GetSummariesAsync(session, Ct));
        Assert.Equal(before, Wire(root.History));
    }

    [Fact]
    public async Task Cancellation_between_batches_stops_at_once_and_persists_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture(
            (_, index) =>
            {
                cancellation.Cancel();
                return Turn.Say("SUM-" + (index + 1));
            },
            SmallWindow);
        var session = await fixture.SeedAsync(ThreeBatches());
        var root = await fixture.ResumeAsync(session);
        var before = Wire(root.History);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Compactor.CompactAsync(root, Manual(keepRecent: 10), cancellation.Token));

        Assert.IsNotType<CompactionFailedException>(error);
        Assert.Single(fixture.SummarizerRequests);
        Assert.Empty(await fixture.Store.GetSummariesAsync(session, Ct));
        Assert.Equal(before, Wire(root.History));
    }

    [Fact]
    public async Task A_failing_history_chain_cancels_the_turn_prefix_chain()
    {
        // The history needs one request and fails; the prefix chain would need three and must not run to its end.
        var prefixCalls = 0;
        var fixture = new Fixture(
            (request, _) =>
            {
                if (!Prompt(request).Contains("## Original Request", StringComparison.Ordinal))
                {
                    throw new ProviderException("history failed", retryable: false, statusCode: 500);
                }

                Interlocked.Increment(ref prefixCalls);
                return Turn.Say("PREFIX");
            },
            SmallWindow);
        var messages = new List<ChatMessage> { ChatMessage.User("old question"), ChatMessage.Assistant("old answer"), ChatMessage.User("build") };
        for (var index = 1; index <= 6; index++)
        {
            messages.Add(Read("c" + index, $"f{index}.cs"));
            messages.Add(Result("c" + index, new string('r', 1_800)));
        }

        messages.Add(ChatMessage.Assistant("final words"));
        var session = await fixture.SeedAsync([.. messages]);
        var root = await fixture.ResumeAsync(session);

        await Assert.ThrowsAsync<CompactionFailedException>(() => fixture.Compactor.CompactAsync(root, Manual(keepRecent: 500), Ct));

        Assert.True(prefixCalls < 3, $"the prefix chain sent {prefixCalls} requests after the history failed");
        Assert.Empty(await fixture.Store.GetSummariesAsync(session, Ct));
    }

    // ---------------------------------------------------------------- stacked split-turn blocks

    [Fact]
    public async Task An_old_split_turn_block_is_folded_by_the_update_and_only_the_new_one_is_kept()
    {
        // The summarizer ignores the fold instruction and repeats the old block: the code demotes it anyway.
        var fixture = new Fixture(
            (request, _) => Turn.Say(Prompt(request).Contains(SummaryCompactor.TurnPrefixInstruction, StringComparison.Ordinal)
                ? "NEW-PREFIX"
                : "HISTORY-UPDATED\n\n---\n\n" + SummaryCompactor.SplitTurnHeading + "\n\nSTALE"),
            window: null);
        var session = await fixture.SeedAsync(
            ChatMessage.User("first"),
            ChatMessage.Assistant("first answer"),
            ChatMessage.User("old question"),
            ChatMessage.Assistant("old answer"),
            ChatMessage.User("long task"),
            Read("c1", "a.cs"),
            Result("c1", new string('r', 2_000)),
            Read("c2", "b.cs"),
            Result("c2", new string('r', 200)),
            ChatMessage.Assistant("finished"));
        await fixture.Store.PutSummaryAsync(
            new SummaryRecord(session, SummaryLayers.SessionLayer, 1, 2, "OLD-HISTORY\n\n---\n\n" + SummaryCompactor.SplitTurnHeading + "\n\nOLD-PREFIX", DateTimeOffset.UtcNow),
            Ct);
        var root = await fixture.ResumeAsync(session);

        await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 100), Ct);

        var history = fixture.SummarizerRequests.Select(Prompt).Single(prompt => prompt.Contains("[User]: old question", StringComparison.Ordinal));
        Assert.Contains("OLD-PREFIX\n</previous-summary>", history, StringComparison.Ordinal);
        Assert.EndsWith(SummaryCompactor.UpdateInstruction + "\n" + SummaryCompactor.FoldTurnContextInstruction, history, StringComparison.Ordinal);

        var latest = (await fixture.Store.GetSummariesAsync(session, Ct)).MaxBy(row => row.ToSeq)!;
        Assert.Equal(
            "HISTORY-UPDATED\n\n" + SummaryCompactor.FoldedTurnHeading + "\n\nSTALE\n\n---\n\n" + SummaryCompactor.SplitTurnHeading
            + "\n\nNEW-PREFIX\n\n<read-files>\na.cs\n</read-files>",
            latest.Text);
    }

    [Fact]
    public async Task A_new_split_without_new_history_folds_the_old_block_instead_of_stacking_it()
    {
        var fixture = new Fixture(
            (request, _) => Turn.Say(Prompt(request).Contains(SummaryCompactor.TurnPrefixInstruction, StringComparison.Ordinal) ? "NEW-PREFIX" : "FOLDED"),
            window: null);
        var session = await fixture.SeedAsync(
            ChatMessage.User("first"),
            ChatMessage.Assistant("first answer"),
            ChatMessage.User("long task"),
            Read("c1", "a.cs"),
            Result("c1", new string('r', 2_000)),
            Read("c2", "b.cs"),
            Result("c2", new string('r', 200)),
            ChatMessage.Assistant("finished"));
        await fixture.Store.PutSummaryAsync(
            new SummaryRecord(session, SummaryLayers.SessionLayer, 1, 2, "OLD-HISTORY\n\n---\n\n" + SummaryCompactor.SplitTurnHeading + "\n\nOLD-PREFIX", DateTimeOffset.UtcNow),
            Ct);
        var root = await fixture.ResumeAsync(session);

        await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 100), Ct);

        Assert.Equal(2, fixture.SummarizerRequests.Count);
        var fold = fixture.SummarizerRequests.Select(Prompt).Single(prompt => !prompt.Contains(SummaryCompactor.TurnPrefixInstruction, StringComparison.Ordinal));
        Assert.StartsWith("<conversation>\n" + SummaryCompactor.NoNewMessages + "\n</conversation>", fold, StringComparison.Ordinal);
        Assert.Contains("OLD-PREFIX\n</previous-summary>", fold, StringComparison.Ordinal);
        Assert.EndsWith(SummaryCompactor.FoldTurnContextInstruction, fold, StringComparison.Ordinal);

        var latest = (await fixture.Store.GetSummariesAsync(session, Ct)).MaxBy(row => row.ToSeq)!;
        Assert.Equal("FOLDED\n\n---\n\n" + SummaryCompactor.SplitTurnHeading + "\n\nNEW-PREFIX\n\n<read-files>\na.cs\n</read-files>", latest.Text);
    }

    // ---------------------------------------------------------------- tool definitions in the estimate

    [Fact]
    public async Task Tool_definitions_count_toward_the_estimated_context()
    {
        // U = 20 000 − 4096 = 15 904, trigger 13 518. The messages are tiny; only a 14 000-token tool description crosses it.
        var withBigTool = await AutoCompactionCallsAsync(new string('d', 56_000));
        var withSmallTool = await AutoCompactionCallsAsync("small");

        Assert.Equal(CompactionReason.Auto, Assert.Single(withBigTool).Request.Reason);
        Assert.Empty(withSmallTool);
    }

    [Fact]
    public async Task Tool_definitions_are_counted_once_per_published_tool_set()
    {
        var description = "UNIQUE-DESCRIPTION " + new string('d', 100);
        var counter = new CountingTokenCounter(description);
        var tool = new DescribedTool("probe", description);
        var main = 0;
        var provider = new FakeProvider((_, _) => Interlocked.Increment(ref main) % 2 == 1
            ? Turn.Call(("c" + main, "probe", "{}"))
            : Turn.Say("done", TokenUsage.Empty));
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [tool],
            Layers = new PromptLayers("test system", null, null),
            Options = new AgentOptions { Compaction = Policy(1_000_000) },
            Policy = new DefaultApprovalPolicy(),
            Compactor = new RecordingCompactor(),
            TokenCounter = counter,
        });
        var root = host.CreateRoot("s", Workspace);

        for (var turn = 0; turn < 3; turn++)
        {
            Assert.Empty((await AgentTestHost.CollectAsync(host.RunAsync(root, "turn " + turn))).OfType<AgentFailed>());
        }

        Assert.Equal(6, provider.Requests.Count); // two requests per run, three runs
        Assert.Equal(1, counter.Hits);

        host.ReplaceExternalTools([tool]);
        await AgentTestHost.CollectAsync(host.RunAsync(root, "after replacement"));

        Assert.Equal(2, counter.Hits);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<IReadOnlyList<(string AgentId, string Model, int Target, CompactionRequest Request)>> AutoCompactionCallsAsync(string description)
    {
        var compactor = new RecordingCompactor();
        var provider = new FakeProvider((_, _) => Turn.Say("ok", TokenUsage.Empty));
        var host = AgentTestHost.Create(
            provider,
            [new DescribedTool("probe", description)],
            options: new AgentOptions { Compaction = Policy(20_000) },
            compactor: compactor);
        var root = host.CreateRoot("s", Workspace);

        await AgentTestHost.CollectAsync(host.RunAsync(root, "hello"));
        return compactor.Calls;
    }

    private static CompactionPolicy Policy(int window) => new() { ContextWindow = _ => window };

    private static ChatMessage[] ThreeBatches() =>
    [
        ChatMessage.User("MSG-1 " + new string('a', 1_800)),
        ChatMessage.Assistant("MSG-2 " + new string('b', 1_800)),
        ChatMessage.User("MSG-3 " + new string('c', 1_800)),
        ChatMessage.Assistant("MSG-4 " + new string('d', 1_800)),
        ChatMessage.User("MSG-5 " + new string('e', 1_800)),
        ChatMessage.Assistant("MSG-6 " + new string('f', 1_800)),
        ChatMessage.User("tail"),
    ];

    private static CompactionRequest Manual(int keepRecent) => new(1, keepRecent, CompactionReason.Manual);

    private static ChatMessage Read(string id, string path) =>
        new(MessageRole.Assistant, [new TextBlock("calling"), new ToolCallBlock(id, "read", "{\"path\":\"" + path + "\"}")]);

    private static ChatMessage Write(string id, string path) =>
        new(MessageRole.Assistant, [new ToolCallBlock(id, "write", "{\"path\":\"" + path + "\",\"content\":\"body\"}")]);

    private static ChatMessage Result(string id, string content) =>
        new(MessageRole.Tool, [new ToolResultBlock(id, content)]);

    private static string Prompt(ChatRequest request) => Turn.Text(request.Messages[^1]);

    private static string Conversation(string prompt)
    {
        const string Open = "<conversation>\n";
        var end = prompt.IndexOf("\n</conversation>", StringComparison.Ordinal);
        return prompt[Open.Length..end];
    }

    private static string[] Wire(IReadOnlyList<ChatMessage> history) =>
        [.. history.Select(message => MessageJson.Serialize(message))];

    private sealed class DescribedTool(string name, string description) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, description, """{"type":"object","properties":{}}""");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            new(new ToolResult("pong"));
    }

    /// <summary>Approximate counting that records how often one particular text is counted.</summary>
    private sealed class CountingTokenCounter(string watched) : ITokenCounter
    {
        private readonly ApproximateTokenCounter inner = new();
        private int hits;

        public int Hits => Volatile.Read(ref hits);

        public int Count(IReadOnlyList<ChatMessage> messages) => inner.Count(messages);

        public int Count(string text)
        {
            if (string.Equals(text, watched, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref hits);
            }

            return inner.Count(text);
        }
    }

    private sealed class Fixture
    {
        private readonly AgentHost host;

        public Fixture(Func<ChatRequest, int, IReadOnlyList<ChatStreamEvent>> summarize, int? window)
        {
            Provider = new FakeProvider((request, index) => request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? summarize(request, index) : Turn.Say("ok"));
            Compactor = new SummaryCompactor(
                Store,
                _ => Provider,
                new ApproximateTokenCounter(),
                policy: window is { } known ? Policy(known) : null);
            host = AgentTestHost.Create(Provider, store: Store, compactor: Compactor);
        }

        public InMemorySessionStore Store { get; } = new();

        public FakeProvider Provider { get; }

        public SummaryCompactor Compactor { get; }

        public IReadOnlyList<ChatRequest> SummarizerRequests =>
            [.. Provider.Requests.Where(request => request.SystemPrompt == SummaryCompactor.SummarizerPrompt)];

        public async Task<string> SeedAsync(params ChatMessage[] messages)
        {
            var session = await Store.CreateSessionAsync(Workspace, "batching", Ct);
            foreach (var message in messages)
            {
                await Store.AppendAsync(session.Id, message, null, Ct);
            }

            return session.Id;
        }

        public Task<AgentInstance> ResumeAsync(string session) => host.ResumeRootAsync(session, Workspace, Ct);
    }
}
