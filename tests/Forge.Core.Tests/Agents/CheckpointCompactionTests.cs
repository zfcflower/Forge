using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// The checkpoint compactor: structured prompts, the serialized conversation, iterative updates, file lists, the
/// keep-recent cut, split turns, the inherited prefix, rejected summaries and resume over old and new summary rows.
/// </summary>
public sealed class CheckpointCompactionTests
{
    private const string Workspace = "D:/work/checkpoint";
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ---------------------------------------------------------------- prompt and serialization

    [Fact]
    public async Task The_first_compaction_sends_the_structured_prompt_and_a_serialized_conversation()
    {
        var fixture = new Fixture(_ => Turn.Say("S1"));
        var session = await fixture.SeedAsync(
            ChatMessage.User("please fix the parser"),
            new ChatMessage(MessageRole.Assistant,
            [
                new ThinkingBlock("SECRET-REASONING"),
                new TextBlock("Looking"),
                new ToolCallBlock("c1", "read", """{"path":"src/Parser.cs"}"""),
            ]),
            Result("c1", new string('x', 5_000)),
            Edit("c2", "src/Parser.cs"),
            Result("c2", "ok"),
            ChatMessage.Assistant("done"),
            ChatMessage.User("next: tests"),
            ChatMessage.Assistant("sure"));
        var root = await fixture.ResumeAsync(session);

        var result = await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 12), Ct);

        Assert.NotNull(result);
        var request = Assert.Single(fixture.SummarizerRequests);
        Assert.Equal(SummaryCompactor.SummarizerPrompt, request.SystemPrompt);
        foreach (var heading in new[] { "## Goal", "## Constraints & Preferences", "## Progress", "### Done", "### In Progress", "### Blocked", "## Key Decisions", "## Next Steps", "## Critical Context" })
        {
            Assert.Contains(heading, request.SystemPrompt, StringComparison.Ordinal);
        }

        Assert.Empty(request.Tools);
        Assert.Equal(ThinkingEffort.Off, request.Model.Thinking);
        Assert.Equal(SummaryCompactor.SummaryMaxOutputTokens, request.Model.MaxOutputTokens);
        var prompt = Turn.Text(Assert.Single(request.Messages));
        Assert.StartsWith("<conversation>\n[User]: please fix the parser\n\n[Assistant]: Looking\n\n[Assistant tool calls]: read(path=\"src/Parser.cs\")\n\n[Tool result]: ", prompt, StringComparison.Ordinal);
        Assert.Contains(new string('x', 2_000) + "\n[… 3000 chars truncated]", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 2_001), prompt, StringComparison.Ordinal);
        Assert.Contains("[Assistant tool calls]: edit(path=\"src/Parser.cs\", old_string=\"a\", new_string=\"b\")", prompt, StringComparison.Ordinal);
        Assert.Contains("[Assistant]: done\n</conversation>", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-REASONING", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("next: tests", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<previous-summary>", prompt, StringComparison.Ordinal);
        Assert.EndsWith(SummaryCompactor.InitialInstruction, prompt, StringComparison.Ordinal);

        // Read and then edited: the file is listed as modified only.
        var summary = Assert.Single(await fixture.Store.GetSummariesAsync(session, Ct));
        Assert.Equal("S1\n\n<modified-files>\nsrc/Parser.cs\n</modified-files>", summary.Text);
        Assert.Equal((SummaryLayers.SessionLayer, 1L, 6L), (summary.Layer, summary.FromSeq, summary.ToSeq));
        Assert.Equal(3, root.History.Count);
        Assert.Contains(summary.Text, Turn.Text(root.History[0]), StringComparison.Ordinal);
        Assert.Equal(["next: tests", "sure"], root.History.Skip(1).Select(Turn.Text));
    }

    [Fact]
    public async Task Images_and_files_reach_the_summarizer_only_as_placeholders()
    {
        var fixture = new Fixture(_ => Turn.Say("S"));
        var data = Convert.ToBase64String(new byte[3_000]);
        var session = await fixture.SeedAsync(
            new ChatMessage(MessageRole.User, [new TextBlock("see"), new ImageBlock("image/png", data), new FileBlock("application/pdf", data, "spec.pdf")]),
            ChatMessage.Assistant(new string('a', 400)),
            ChatMessage.User("tail"));
        var root = await fixture.ResumeAsync(session);

        await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 10), Ct);

        var prompt = Turn.Text(Assert.Single(Assert.Single(fixture.SummarizerRequests).Messages));
        Assert.Contains("[User]: see\n[image: image/png, 3 KB]\n[file: spec.pdf, 3 KB]", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(data, prompt, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- iterative update, file lists, resume

    [Fact]
    public async Task A_second_compaction_updates_the_previous_summary_and_accumulates_the_file_lists()
    {
        var fixture = new Fixture(request => Turn.Say(Prompt(request).Contains("<previous-summary>", StringComparison.Ordinal) ? "S2" : "S1"));
        var session = await fixture.SeedAsync(
            ChatMessage.User("fix the parser"),
            Read("c1", "src/Parser.cs"),
            Result("c1", new string('p', 400)),
            Edit("c2", "src/Lexer.cs"),
            Result("c2", "ok"),
            ChatMessage.User("next: tests"),
            ChatMessage.Assistant("sure"));
        var first = await fixture.ResumeAsync(session);
        await fixture.Compactor.CompactAsync(first, Manual(keepRecent: 12), Ct);

        await fixture.AppendAsync(
            session,
            Read("c3", "docs/a.md"),
            Result("c3", new string('d', 400)),
            Write("c4", "src/New.cs"),
            Result("c4", "ok"),
            Read("c5", "src/Lexer.cs"),
            Result("c5", "lexer"),
            ChatMessage.User("and finally"),
            ChatMessage.Assistant("fine"));
        var second = await fixture.ResumeAsync(session);
        Assert.Contains("S1", Turn.Text(second.History[0]), StringComparison.Ordinal);
        await fixture.Compactor.CompactAsync(second, Manual(keepRecent: 12), Ct);

        Assert.Equal(2, fixture.SummarizerRequests.Count);
        var update = Prompt(fixture.SummarizerRequests[1]);
        Assert.Contains("<previous-summary>\nS1\n</previous-summary>", update, StringComparison.Ordinal);
        Assert.Contains("[User]: next: tests", update, StringComparison.Ordinal);
        Assert.Contains("[Assistant tool calls]: write(path=\"src/New.cs\", content=\"body\")", update, StringComparison.Ordinal);
        Assert.DoesNotContain("fix the parser", update, StringComparison.Ordinal);
        Assert.EndsWith(SummaryCompactor.UpdateInstruction, update, StringComparison.Ordinal);

        var rows = await fixture.Store.GetSummariesAsync(session, Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal((SummaryLayers.SessionLayer, 1L), (row.Layer, row.FromSeq)));
        var latest = rows.MaxBy(row => row.ToSeq)!;
        Assert.Equal(13, latest.ToSeq);
        Assert.Equal(
            "S2\n\n<read-files>\ndocs/a.md\nsrc/Parser.cs\n</read-files>\n\n<modified-files>\nsrc/Lexer.cs\nsrc/New.cs\n</modified-files>",
            latest.Text);

        // The live context after compaction and a fresh resume from the store are the same.
        Assert.Equal(Wire(second.History), Wire((await fixture.ResumeAsync(session)).History));
        Assert.Equal(["and finally", "fine"], second.History.Skip(1).Select(Turn.Text));
    }

    [Fact]
    public async Task Old_style_stage_rows_resume_and_are_updated_into_one_session_summary()
    {
        var fixture = new Fixture(_ => Turn.Say("NEW"));
        var session = await fixture.SeedAsync(
            ChatMessage.User("u1"),
            ChatMessage.Assistant("a2"),
            ChatMessage.User("u3"),
            ChatMessage.Assistant("a4"),
            ChatMessage.User("u5 " + new string('x', 400)),
            ChatMessage.Assistant("a6 " + new string('y', 400)),
            ChatMessage.User("u7"),
            ChatMessage.Assistant("a8"));
        await fixture.Store.PutSummaryAsync(new SummaryRecord(session, SummaryLayers.SessionLayer, 1, 2, "OLD-SESSION", DateTimeOffset.UtcNow.AddMinutes(-2)), Ct);
        await fixture.Store.PutSummaryAsync(new SummaryRecord(session, SummaryLayers.StageLayer, 3, 4, "OLD-STAGE", DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);

        var resumed = await fixture.ResumeAsync(session);
        var before = Turn.Text(resumed.History[0]);
        Assert.Contains("OLD-SESSION", before, StringComparison.Ordinal);
        Assert.Contains("OLD-STAGE", before, StringComparison.Ordinal);
        Assert.Equal(5, resumed.History.Count);

        await fixture.Compactor.CompactAsync(resumed, Manual(keepRecent: 12), Ct);

        var prompt = Prompt(Assert.Single(fixture.SummarizerRequests));
        Assert.Contains("<previous-summary>\nOLD-SESSION\n\nOLD-STAGE\n</previous-summary>", prompt, StringComparison.Ordinal);
        Assert.StartsWith("<conversation>\n[User]: u5", prompt, StringComparison.Ordinal);
        var latest = (await fixture.Store.GetSummariesAsync(session, Ct)).MaxBy(row => row.ToSeq)!;
        Assert.Equal((SummaryLayers.SessionLayer, 1L, 6L, "NEW"), (latest.Layer, latest.FromSeq, latest.ToSeq, latest.Text));
        var after = await fixture.ResumeAsync(session);
        Assert.Equal(Wire(resumed.History), Wire(after.History));
        Assert.DoesNotContain("OLD-STAGE", Turn.Text(after.History[0]), StringComparison.Ordinal);
        Assert.Equal(["u7", "a8"], after.History.Skip(1).Select(Turn.Text));
    }

    // ---------------------------------------------------------------- keep recent and split turns

    [Fact]
    public async Task Automatic_compaction_keeps_at_least_keep_recent_even_when_the_target_is_smaller()
    {
        var fixture = new Fixture(_ => Turn.Say("S"));
        var session = await fixture.SeedAsync(
            ChatMessage.User(new string('a', 400)),
            ChatMessage.Assistant(new string('b', 400)),
            ChatMessage.User(new string('c', 400)),
            ChatMessage.Assistant(new string('d', 400)));
        var root = await fixture.ResumeAsync(session);

        // Each message is ~104 tokens: a target of 50 alone would keep nothing, keepRecent = 210 keeps the last two.
        await fixture.Compactor.CompactAsync(root, new CompactionRequest(50, 210, CompactionReason.Auto), Ct);

        Assert.Equal([new string('c', 400), new string('d', 400)], root.History.Skip(1).Select(Turn.Text));
    }

    [Fact]
    public async Task A_target_with_room_keeps_more_than_keep_recent()
    {
        var fixture = new Fixture(_ => Turn.Say("S"));
        var session = await fixture.SeedAsync(
            ChatMessage.User(new string('a', 400)),
            ChatMessage.Assistant(new string('b', 400)),
            ChatMessage.User(new string('c', 400)),
            ChatMessage.Assistant(new string('d', 400)));
        var root = await fixture.ResumeAsync(session);

        await fixture.Compactor.CompactAsync(root, new CompactionRequest(330, 10, CompactionReason.Auto), Ct);

        Assert.Equal(4, root.History.Count);
        Assert.Equal(new string('b', 400), Turn.Text(root.History[1]));
    }

    [Fact]
    public async Task One_long_tool_loop_is_split_inside_the_turn_with_a_turn_prefix_summary()
    {
        var fixture = new Fixture(request => Turn.Say(Prompt(request).Contains(SummaryCompactor.TurnPrefixInstruction, StringComparison.Ordinal) ? "PREFIX" : "HISTORY"));
        var messages = new List<ChatMessage> { ChatMessage.User("build the feature") };
        for (var index = 1; index <= 6; index++)
        {
            messages.Add(Read("c" + index, $"f{index}.cs"));
            messages.Add(Result("c" + index, new string('r', 2_000)));
        }

        messages.Add(ChatMessage.Assistant("final words"));
        var session = await fixture.SeedAsync([.. messages]);
        var root = await fixture.ResumeAsync(session);

        var result = await fixture.Compactor.CompactAsync(
            root,
            new CompactionRequest(1, 1_100, CompactionReason.Manual, "the API surface"),
            Ct);

        Assert.NotNull(result);
        Assert.True(result.TokensAfter < result.TokensBefore);
        var request = Assert.Single(fixture.SummarizerRequests); // no history before the turn and no previous summary
        var prompt = Prompt(request);
        Assert.Equal(SummaryCompactor.SummarizerPrompt, request.SystemPrompt);
        Assert.Equal(SummaryCompactor.TurnPrefixMaxOutputTokens, request.Model.MaxOutputTokens);
        Assert.StartsWith("<conversation>\n[User]: build the feature", prompt, StringComparison.Ordinal);
        Assert.EndsWith(SummaryCompactor.TurnPrefixInstruction + "\n\nAdditional focus: the API surface", prompt, StringComparison.Ordinal);

        var summary = Assert.Single(await fixture.Store.GetSummariesAsync(session, Ct));
        Assert.StartsWith(SummaryCompactor.SplitTurnHeading + "\n\nPREFIX\n\n<read-files>\nf1.cs\n", summary.Text, StringComparison.Ordinal);
        AssertToolPairsIntact(root.History.Skip(1).ToList());
        Assert.Equal(MessageRole.Assistant, root.History[1].Role);
        Assert.Equal("final words", Turn.Text(root.History[^1]));
        Assert.Equal(Wire(root.History), Wire((await fixture.ResumeAsync(session)).History));
    }

    [Fact]
    public async Task A_split_turn_after_earlier_history_merges_both_summaries()
    {
        var fixture = new Fixture(request => Turn.Say(Prompt(request).Contains(SummaryCompactor.TurnPrefixInstruction, StringComparison.Ordinal) ? "PREFIX" : "HISTORY"));
        var session = await fixture.SeedAsync(
            ChatMessage.User("old question"),
            ChatMessage.Assistant("old answer"),
            ChatMessage.User("long task"),
            Read("c1", "a.cs"),
            Result("c1", new string('r', 2_000)),
            Read("c2", "b.cs"),
            Result("c2", new string('r', 200)),
            ChatMessage.Assistant("finished"));
        var root = await fixture.ResumeAsync(session);

        await fixture.Compactor.CompactAsync(root, Manual(keepRecent: 100, focus: "naming"), Ct);

        Assert.Equal(2, fixture.SummarizerRequests.Count);
        var history = fixture.SummarizerRequests.Select(Prompt).Single(prompt => prompt.Contains(SummaryCompactor.InitialInstruction, StringComparison.Ordinal));
        Assert.Contains("[User]: old question", history, StringComparison.Ordinal);
        Assert.DoesNotContain("long task", history, StringComparison.Ordinal);
        Assert.EndsWith("\n\nAdditional focus: naming", history, StringComparison.Ordinal);
        var prefix = fixture.SummarizerRequests.Select(Prompt).Single(prompt => prompt.Contains(SummaryCompactor.TurnPrefixInstruction, StringComparison.Ordinal));
        Assert.StartsWith("<conversation>\n[User]: long task", prefix, StringComparison.Ordinal);
        Assert.EndsWith("\n\nAdditional focus: naming", prefix, StringComparison.Ordinal);

        var summary = Assert.Single(await fixture.Store.GetSummariesAsync(session, Ct));
        Assert.Equal(
            "HISTORY\n\n---\n\n" + SummaryCompactor.SplitTurnHeading + "\n\nPREFIX\n\n<read-files>\na.cs\n</read-files>",
            summary.Text);
        Assert.Equal(5L, summary.ToSeq);
        Assert.Equal(["calling", string.Empty, "finished"], root.History.Skip(1).Select(Turn.Text));
        AssertToolPairsIntact(root.History.Skip(1).ToList());
    }

    [Fact]
    public async Task A_long_single_turn_compacts_inside_the_loop_and_finishes_without_stalling()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "long-turn", Ct);
        var big = new string('z', 2_000);
        var tool = new FakeTool("read", ToolSideEffect.ReadOnly, (_, _, _) => new ValueTask<ToolResult>(new ToolResult(big)));
        var main = 0;
        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt
                ? Turn.Say("CHECKPOINT")
                : Interlocked.Increment(ref main) <= 12
                    ? [new MessageCompleted(new ChatMessage(MessageRole.Assistant, [new TextBlock("calling"), new ToolCallBlock("c" + main, "read", "{\"path\":\"f" + main + ".cs\"}")]), TokenUsage.Empty, StopReason.ToolUse)]
                    : Turn.Say("all done", TokenUsage.Empty));
        // U = 2000: the turn alone grows to ~6000 tokens, so it has to be cut inside itself several times. No usage is
        // reported, so the loop works from estimates only.
        var host = AgentTestHost.Create(provider, [tool], options: CompactionWindows.Legacy(2_000, maxTurns: 40), store: store);
        var root = host.CreateRoot(session.Id, directory.Path);

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "do the long thing"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Empty(events.OfType<AgentCompactionStalled>());
        Assert.True(events.OfType<AgentCompacted>().Count() >= 2);
        Assert.Equal("all done", Turn.Text(root.History[^1]));
        var summaries = await store.GetSummariesAsync(session.Id, Ct);
        Assert.Contains(summaries, summary => summary.Text.Contains(SummaryCompactor.SplitTurnHeading, StringComparison.Ordinal));
        AssertToolPairsIntact(root.History.Skip(1).ToList());
        foreach (var request in provider.Requests.Where(request => request.SystemPrompt != SummaryCompactor.SummarizerPrompt))
        {
            AssertToolPairsIntact(request.Messages.SkipWhile(message => message.Role == MessageRole.User).ToList());
        }
    }

    // ---------------------------------------------------------------- inherited prefix

    [Fact]
    public async Task The_inherited_prefix_is_kept_whole_and_never_summarized()
    {
        var store = new LineageTestStore();
        var parent = await store.CreateSessionAsync(Workspace, "parent", Ct);
        await store.AppendAsync(parent.Id, ChatMessage.User("PARENT-QUESTION"), null, Ct);
        await store.AppendAsync(parent.Id, ChatMessage.Assistant("PARENT-ANSWER " + new string('p', 800)), null, Ct);
        var child = await store.InheritAsync(parent);
        await store.AppendAsync(child.Id, ChatMessage.User("own one"), null, Ct);
        await store.AppendAsync(child.Id, ChatMessage.Assistant(new string('o', 400)), null, Ct);
        await store.AppendAsync(child.Id, ChatMessage.User("own two"), null, Ct);
        await store.AppendAsync(child.Id, ChatMessage.Assistant("short"), null, Ct);
        var provider = new FakeProvider((_, _) => Turn.Say("CHILD-SUMMARY"));
        var compactor = new SummaryCompactor(store, _ => provider, new ApproximateTokenCounter());
        var host = AgentTestHost.Create(provider, store: store, compactor: compactor);
        var root = await host.ResumeRootAsync(child.Id, Workspace, Ct);
        var inherited = Wire(root.History)[..2];

        await compactor.CompactAsync(root, new CompactionRequest(1, 15, CompactionReason.Manual), Ct);

        Assert.Equal(inherited, Wire(root.History)[..2]);
        Assert.Contains("CHILD-SUMMARY", Turn.Text(root.History[2]), StringComparison.Ordinal);
        Assert.Equal(["own two", "short"], root.History.Skip(3).Select(Turn.Text));
        var prompt = Prompt(Assert.Single(provider.Requests));
        Assert.DoesNotContain("PARENT", prompt, StringComparison.Ordinal);
        Assert.Contains("[User]: own one", prompt, StringComparison.Ordinal);
        Assert.Empty(await store.GetSummariesAsync(parent.Id, Ct));
        Assert.Equal(Wire(root.History), Wire((await host.ResumeRootAsync(child.Id, Workspace, Ct)).History));
    }

    // ---------------------------------------------------------------- rejected summaries

    public static TheoryData<string> Rejections => ["max-tokens", "tool-call", "error", "empty", "no-completion"];

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task An_unusable_summary_is_rejected_and_nothing_changes(string kind)
    {
        var fixture = new Fixture(_ => kind switch
        {
            "max-tokens" => [new TextDelta("partial"), new MessageCompleted(ChatMessage.Assistant("partial"), TokenUsage.Empty, StopReason.MaxTokens)],
            "tool-call" => Turn.Call(("t1", "read", "{}")),
            "error" => throw new ProviderException("upstream 500", retryable: false, statusCode: 500),
            "empty" => Turn.Say("   "),
            _ => [new TextDelta("dangling")],
        });
        var session = await fixture.SeedAsync(ChatMessage.User(new string('a', 400)), ChatMessage.Assistant(new string('b', 400)), ChatMessage.User("tail"));
        var root = await fixture.ResumeAsync(session);
        var before = Wire(root.History);

        await Assert.ThrowsAsync<CompactionFailedException>(() => fixture.Compactor.CompactAsync(root, Manual(keepRecent: 10), Ct));

        Assert.Empty(await fixture.Store.GetSummariesAsync(session, Ct));
        Assert.Equal(before, Wire(root.History));
    }

    [Fact]
    public async Task Cancellation_is_not_turned_into_a_failed_compaction()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var session = await fixture.SeedAsync(ChatMessage.User(new string('a', 400)), ChatMessage.Assistant(new string('b', 400)), ChatMessage.User("tail"));
        var root = await fixture.ResumeAsync(session);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Compactor.CompactAsync(root, Manual(keepRecent: 10), cancellation.Token));

        Assert.IsNotType<CompactionFailedException>(error);
    }

    [Fact]
    public async Task A_rejected_automatic_summary_counts_as_fruitless_and_does_not_fail_the_turn()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "rejected", Ct);
        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt
                ? [new MessageCompleted(ChatMessage.Assistant("cut"), TokenUsage.Empty, StopReason.MaxTokens)]
                : Turn.Say(new string('z', 400), TokenUsage.Empty));
        var options = new AgentOptions { Compaction = new CompactionPolicy { ContextWindow = _ => 2_000 } };
        var host = AgentTestHost.Create(provider, options: options, store: store);
        var root = host.CreateRoot(session.Id, Workspace);

        var events = new List<AgentEvent>();
        events.AddRange(await AgentTestHost.CollectAsync(host.RunAsync(root, new string('x', 3_600))));
        events.AddRange(await AgentTestHost.CollectAsync(host.RunAsync(root, "second")));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Empty(events.OfType<AgentCompacted>());
        Assert.Single(events.OfType<AgentCompactionStalled>());
        Assert.Empty(await store.GetSummariesAsync(session.Id, Ct));
    }

    [Fact]
    public async Task A_rejected_manual_summary_surfaces_as_a_failed_compaction()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "manual-rejected", Ct);
        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt
                ? [new MessageCompleted(ChatMessage.Assistant("cut"), TokenUsage.Empty, StopReason.MaxTokens)]
                : Turn.Say(new string('z', 400), TokenUsage.Empty));
        var host = AgentTestHost.Create(provider, store: store);
        var root = host.CreateRoot(session.Id, Workspace);

        // The window is unknown, so manual compaction keeps 20 000 tokens: make the history larger than that first.
        for (var turn = 0; turn < 3; turn++)
        {
            await AgentTestHost.CollectAsync(host.RunAsync(root, new string('q', 40_000)));
        }

        await Assert.ThrowsAsync<CompactionFailedException>(() => host.CompactAsync(root, null, "focus", Ct));
        Assert.Empty(await store.GetSummariesAsync(session.Id, Ct));
    }

    // ---------------------------------------------------------------- helpers

    private static CompactionRequest Manual(int keepRecent, string? focus = null) =>
        new(1, keepRecent, CompactionReason.Manual, focus);

    private static ChatMessage Read(string id, string path) =>
        new(MessageRole.Assistant, [new TextBlock("calling"), new ToolCallBlock(id, "read", "{\"path\":\"" + path + "\"}")]);

    private static ChatMessage Edit(string id, string path) =>
        new(MessageRole.Assistant, [new ToolCallBlock(id, "edit", "{\"path\":\"" + path + "\",\"old_string\":\"a\",\"new_string\":\"b\"}")]);

    private static ChatMessage Write(string id, string path) =>
        new(MessageRole.Assistant, [new ToolCallBlock(id, "write", "{\"path\":\"" + path + "\",\"content\":\"body\"}")]);

    private static ChatMessage Result(string id, string content) =>
        new(MessageRole.Tool, [new ToolResultBlock(id, content)]);

    private static string Prompt(ChatRequest request) => Turn.Text(request.Messages[^1]);

    private static string[] Wire(IReadOnlyList<ChatMessage> history) =>
        [.. history.Select(message => MessageJson.Serialize(message))];

    /// <summary>Every tool result answers a call of the assistant message right before its run of results, and every call is answered.</summary>
    private static void AssertToolPairsIntact(IReadOnlyList<ChatMessage> messages)
    {
        HashSet<string>? open = null;
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.Tool)
            {
                Assert.NotNull(open);
                foreach (var block in message.Content.OfType<ToolResultBlock>())
                {
                    Assert.True(open.Remove(block.CallId), $"tool result {block.CallId} has no call before it");
                }

                continue;
            }

            Assert.True(open is null || open.Count == 0, "a tool call was separated from its result");
            open = message.Role == MessageRole.Assistant
                ? [.. message.Content.OfType<ToolCallBlock>().Select(call => call.Id)]
                : null;
        }
    }

    private sealed class Fixture
    {
        private readonly AgentHost host;

        public Fixture(Func<ChatRequest, IReadOnlyList<ChatStreamEvent>> summarize)
        {
            Provider = new FakeProvider((request, _) => request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? summarize(request) : Turn.Say("ok"));
            Compactor = new SummaryCompactor(Store, _ => Provider, new ApproximateTokenCounter());
            host = AgentTestHost.Create(Provider, store: Store, compactor: Compactor);
        }

        public InMemorySessionStore Store { get; } = new();

        public FakeProvider Provider { get; }

        public SummaryCompactor Compactor { get; }

        public IReadOnlyList<ChatRequest> SummarizerRequests =>
            [.. Provider.Requests.Where(request => request.SystemPrompt == SummaryCompactor.SummarizerPrompt)];

        public async Task<string> SeedAsync(params ChatMessage[] messages)
        {
            var session = await Store.CreateSessionAsync(Workspace, "checkpoint", Ct);
            await AppendAsync(session.Id, messages);
            return session.Id;
        }

        public async Task AppendAsync(string session, params ChatMessage[] messages)
        {
            foreach (var message in messages)
            {
                await Store.AppendAsync(session, message, null, Ct);
            }
        }

        public Task<AgentInstance> ResumeAsync(string session) => host.ResumeRootAsync(session, Workspace, Ct);
    }
}
