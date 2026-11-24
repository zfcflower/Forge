using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The zero-cost rung of context compaction: when the context crosses the trigger line, old tool results become receipts
/// before the summarizer is asked. Everything here drives the public surface — the event stream and the requests a
/// scripted provider saw — and asserts invariants rather than exact trigger moments: a pass that was enough runs no
/// summarizer request, the error, short and recent results are never touched, a receipt is never cropped again, a
/// stalled instance still gets its receipts, and the overflow fallback stays on the summarizer path.
/// <para>Budget arithmetic the scenarios lean on: the output reserve is at least <c>min(4096, W/2)</c>, so a 10000-token
/// window has U 5904 (trigger 5018, emergency 5608, midpoint 3985, keepRecent 1180) and a 7500-token window U 3750
/// (trigger 3187, midpoint 2531, keepRecent 750).</para>
/// </summary>
public sealed class ToolResultReductionTests
{
    private sealed record Script(string? CallName = null, string? ArgumentsJson = null, Exception? Error = null);

    /// <summary>Plays one script entry per request and keeps every request's messages as the loop sent them.</summary>
    private sealed class ScriptedProvider(params Script[] scripts) : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public List<List<ChatMessage>> Captured { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Captured.Add([.. request.Messages]);
            var script = scripts[calls];
            calls++;
            if (script.Error is { } error)
            {
                throw error;
            }

            if (script.CallName is { } name)
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new ToolCallBlock($"c{Captured.Count}", name, script.ArgumentsJson ?? "{}")]),
                    TokenUsage.Empty,
                    StopReason.ToolUse);
            }
            else
            {
                yield return new MessageCompleted(ChatMessage.Assistant("done"), TokenUsage.Empty, StopReason.EndTurn);
            }
        }
    }

    /// <summary>A tool whose reply is fixed by the test, named and shaped as the test needs.</summary>
    private sealed class ReplyTool(string name, string description, string schema, Func<ToolResult> reply)
        : ToolBase(name, description, schema, ToolSideEffect.ReadOnly)
    {
        protected override ValueTask<ToolResult> InvokeCoreAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken ct) => new(reply());
    }

    private sealed class RecordingCompactor : IContextCompactor
    {
        public List<CompactionRequest> Requests { get; } = [];

        public Task<CompactionResult?> CompactAsync(AgentInstance instance, CompactionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult<CompactionResult?>(null);
        }
    }

    private const string ReadSchema =
        """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer"},"limit":{"type":"integer"}},"additionalProperties":false}""";

    private const string ReadArguments = """{"path":"src/App.cs","offset":1,"limit":420}""";

    private static ToolResult Text(int chars) => new(new string('x', chars));

    private static ToolResult ErrorText(int chars) => new(new string('e', chars), IsError: true);

    private static ToolResult TextAndImages(int chars, int images) => new(
        [
            new TextBlock(new string('x', chars)),
            .. Enumerable.Range(0, images).Select(static _ => new ImageBlock("image/png", "data")),
        ]);

    /// <summary>The product's read tool shape, replying with a fixed block of text.</summary>
    private static ReplyTool ReadTool(int chars) => new("read", "Reads a file.", ReadSchema, () => Text(chars));

    /// <summary>A read tool whose description is a large share of the usable budget, so a pass that aims at the message
    /// count alone leaves the context over the line.</summary>
    private static ReplyTool HeavyReadTool(int descriptionChars, int resultChars) => new(
        "read",
        new string('d', descriptionChars),
        ReadSchema,
        () => Text(resultChars));

    private static (AgentHost Host, ScriptedProvider Provider, RecordingCompactor Compactor, AgentInstance Root) Subject(
        int window,
        Script[] scripts,
        params ITool[] tools)
    {
        var compactor = new RecordingCompactor();
        var provider = new ScriptedProvider(scripts);
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = tools,
            Layers = new PromptLayers("sys", null, null),
            Compactor = compactor,
            Options = new AgentOptions
            {
                Compaction = new CompactionPolicy
                {
                    ContextWindow = _ => window,
                    MaxOutputTokens = _ => null,
                },
            },
        });
        return (host, provider, compactor, host.CreateRoot("session-reduction", Directory.GetCurrentDirectory()));
    }

    /// <summary>One turn per script pair: a request answered with a tool call, and the request its result answers.</summary>
    private static Script[] Turns(int turns, string callName = "read", string? argumentsJson = null) =>
        Enumerable.Range(0, turns)
            .SelectMany(_ => new[] { new Script(callName, argumentsJson), new Script() })
            .ToArray();

    /// <summary><paramref name="turns"/> scripted pairs plus one single-request turn, so the pass has a request to serve.</summary>
    private static Script[] TurnsAndAnExtraRequest(int turns, string callName = "read", string? argumentsJson = null) =>
        [.. Turns(turns, callName, argumentsJson), new Script()];

    private static async Task<List<AgentEvent>> RunTurnAsync(AgentHost host, AgentInstance root, string text)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in host.RunAsync(root, text))
        {
            events.Add(evt);
        }

        return events;
    }

    private static string? ReceiptText(IReadOnlyList<ChatMessage> messages, string callId) =>
        messages.SelectMany(static message => message.Content)
            .OfType<ToolResultBlock>()
            .FirstOrDefault(block => block.CallId == callId)?
            .Blocks.OfType<TextBlock>()
            .FirstOrDefault()?
            .Text;

    /// <summary>The first request that carried a receipt — the pass's own output, and the reference point for everything it kept.</summary>
    private static List<ChatMessage> FirstCroppedRequest(IReadOnlyList<List<ChatMessage>> captured) =>
        captured.First(messages => messages
            .SelectMany(static message => message.Content)
            .OfType<ToolResultBlock>()
            .Any(block => block.Blocks.OfType<TextBlock>().FirstOrDefault()?.Text.Contains("[omitted: ", StringComparison.Ordinal) == true));

    [Fact]
    public async Task Receipts_that_are_enough_run_no_summarizer_and_keep_the_recent_window_raw()
    {
        // Window 10000 (U 5904, trigger 5018, keepRecent 1180): read turns of ~1000 tokens each cross the trigger well
        // before the turns run out. Every pass must land under the line without a summarizer request, the oldest
        // result must go out as a receipt naming its target and the find-back reference, and raw results must survive.
        var (host, provider, compactor, root) = Subject(10_000, Turns(6, argumentsJson: ReadArguments), ReadTool(4_000));
        var events = new List<AgentEvent>();
        for (var turn = 0; turn < 6; turn++)
        {
            events.AddRange(await RunTurnAsync(host, root, "go"));
        }

        var passes = events.OfType<AgentContextReduced>().ToList();
        Assert.NotEmpty(passes);
        Assert.All(passes, pass => Assert.True(pass.TokensAfter < pass.TokensBefore, "a pass released nothing"));
        Assert.Empty(compactor.Requests);
        var trigger = events.OfType<AgentContextMeasured>().Last().TriggerTokens!.Value;
        Assert.All(passes, pass => Assert.True(pass.TokensAfter < trigger, $"a pass left {pass.TokensAfter} at or above the line {trigger}"));

        var cropped = FirstCroppedRequest(provider.Captured);
        var oldest = ReceiptText(cropped, "c1");
        Assert.NotNull(oldest);
        Assert.Contains("[omitted: read_history ", oldest);
        Assert.Contains("src/App.cs:1-420", oldest);
        Assert.True(oldest!.Length < ContextReceipt.MinCroppableChars, $"receipt was {oldest.Length} chars");
        Assert.Contains(
            cropped.SelectMany(static message => message.Content).OfType<ToolResultBlock>(),
            block => block.Blocks.OfType<TextBlock>().Single().Text.Length == 4_000);
    }

    [Fact]
    public async Task Error_short_and_recent_results_stay_verbatim()
    {
        // An error answer, a short answer, then ordinary oversized answers. Across every request the loop sent, the
        // error and the short result stay byte for byte what the tool produced, and the recent window keeps its raw
        // results; only the old, ordinary, oversized results ever become receipts.
        var replies = new Queue<ToolResult>([
            ErrorText(4_000),
            Text(400),
            Text(4_000),
            Text(4_000),
            Text(4_000),
            Text(4_000),
            Text(4_000),
            Text(4_000),
        ]);
        var read = new ReplyTool("read", "Reads a file.", ReadSchema, () => replies.Dequeue());
        var (host, provider, compactor, root) = Subject(12_000, TurnsAndAnExtraRequest(7), read);
        var events = new List<AgentEvent>();
        for (var turn = 0; turn < 7; turn++)
        {
            events.AddRange(await RunTurnAsync(host, root, "go"));
        }

        events.AddRange(await RunTurnAsync(host, root, "go"));

        Assert.NotEmpty(events.OfType<AgentContextReduced>());

        foreach (var sent in provider.Captured)
        {
            var error = sent.SelectMany(static message => message.Content).OfType<ToolResultBlock>()
                .Where(block => block.CallId == "c1").ToList();
            if (error.Count == 0)
            {
                continue;
            }

            var single = Assert.Single(error);
            Assert.True(single.IsError);
            Assert.Equal(4_000, single.Blocks.OfType<TextBlock>().Single().Text.Length);

            var shortResult = sent.SelectMany(static message => message.Content).OfType<ToolResultBlock>()
                .Where(block => block.CallId == "c3").ToList();
            if (shortResult.Count > 0)
            {
                Assert.False(Assert.Single(shortResult).IsError);
                Assert.Equal(400, Assert.Single(shortResult).Blocks.OfType<TextBlock>().Single().Text.Length);
            }
        }
    }

    [Fact]
    public async Task No_candidates_falls_through_to_the_summarizer()
    {
        // One oversized result fills the whole keepRecent window by itself, so the guard protects even it: nothing is
        // croppable and the pass must fall through to the summarizer without claiming a reduction.
        var (host, provider, compactor, root) = Subject(
            10_000,
            [.. Turns(1), new Script()],
            ReadTool(30_000));
        await RunTurnAsync(host, root, "go");

        var events = await RunTurnAsync(host, root, "again");

        Assert.Empty(events.OfType<AgentContextReduced>());
        var request = Assert.Single(compactor.Requests);
        Assert.Equal(CompactionReason.Auto, request.Reason);
    }

    [Fact]
    public async Task A_second_pass_never_crops_a_receipt_again()
    {
        // Nine read turns cross the trigger more than once. The receipt the first pass wrote for the oldest result must
        // go out byte for byte the same after every later pass, because a receipt is shorter than the croppable
        // minimum and so is never a candidate again.
        var (host, provider, compactor, root) = Subject(10_000, Turns(9), ReadTool(4_000));
        var events = new List<AgentEvent>();
        for (var turn = 0; turn < 9; turn++)
        {
            events.AddRange(await RunTurnAsync(host, root, "go"));
        }

        var passes = events.OfType<AgentContextReduced>().ToList();
        Assert.True(passes.Count >= 2, $"only {passes.Count} passes ran in nine turns over the line");
        Assert.All(passes, pass => Assert.True(pass.TokensAfter < pass.TokensBefore, "a pass released nothing"));

        var receipts = provider.Captured
            .Select(messages => ReceiptText(messages, "c1"))
            .Where(text => text is not null && text.Contains("[omitted: read_history ", StringComparison.Ordinal))
            .Select(text => text!)
            .Distinct()
            .ToList();
        Assert.Single(receipts);
    }

    [Fact]
    public async Task A_stalled_instance_still_crops_and_a_partial_pass_does_not_reset_it()
    {
        // Three user turns of pasted text over the line give the fake compactor its two fruitless attempts and stall the
        // instance. The read turns after that must still be cropped (stalling stops the summarizer, not the receipts),
        // every pass releases only part of the excess — the pasted text cannot be cropped — and the summarizer count
        // stays where stalling left it.
        var bigRead = ReadTool(4_000);
        var smallRead = new ReplyTool("peek", "Peeks at a file.", ReadSchema, () => Text(400));
        var scripts = new List<Script>();
        for (var turn = 0; turn < 7; turn++)
        {
            scripts.Add(new Script());
        }

        scripts.AddRange(Turns(3, callName: "read"));
        scripts.AddRange(Turns(3, callName: "peek"));
        var (host, provider, compactor, root) = Subject(10_000, [.. scripts], bigRead, smallRead);

        var events = new List<AgentEvent>();
        for (var turn = 0; turn < 7; turn++)
        {
            events.AddRange(await RunTurnAsync(host, root, new string('u', 4_400)));
        }
        for (var turn = 0; turn < 6; turn++)
        {
            events.AddRange(await RunTurnAsync(host, root, "go"));
        }

        var stalledAt = events.FindIndex(evt => evt is AgentCompactionStalled);
        Assert.True(stalledAt >= 0, "the two fruitless attempts did not stall the instance");
        var reducedAt = events.FindIndex(evt => evt is AgentContextReduced);
        Assert.True(reducedAt > stalledAt, "the stalled instance was not cropped");

        // Every pass stays above the trigger line (5018 for U 5904): the pasted text is outside the rung's reach.
        var passes = events.OfType<AgentContextReduced>().ToList();
        Assert.NotEmpty(passes);
        Assert.All(
            passes,
            reduced => Assert.True(reduced.TokensAfter > 5_018, $"a pass claimed {reduced.TokensAfter} was under the line"));
        Assert.Equal(2, compactor.Requests.Count);
    }

    [Fact]
    public async Task A_small_window_crops_to_under_the_line_without_the_summarizer()
    {
        // Window 7500 (U 3750, trigger 3187, midpoint 2531, keepRecent 750): the tool definitions are a large share of
        // the usable budget. A pass that aims at the message count alone crops too little to get under the line and the
        // summarizer runs; this guards the budget's single arithmetic — the release target comes from the measured C,
        // definitions and all.
        var (host, provider, compactor, root) = Subject(7_500, TurnsAndAnExtraRequest(4), HeavyReadTool(2_500, 2_000));
        var events = new List<AgentEvent>();
        for (var turn = 0; turn < 4; turn++)
        {
            events.AddRange(await RunTurnAsync(host, root, "go"));
        }

        events.AddRange(await RunTurnAsync(host, root, "go"));

        var passes = events.OfType<AgentContextReduced>().ToList();
        Assert.NotEmpty(passes);
        var trigger = events.OfType<AgentContextMeasured>().Last().TriggerTokens!.Value;
        Assert.All(
            passes,
            pass => Assert.True(pass.TokensAfter < trigger, $"a pass left {pass.TokensAfter} at or above the line {trigger}"));
        Assert.Empty(compactor.Requests);
    }

    [Fact]
    public async Task Mixed_blocks_and_other_tools_count_by_their_kind()
    {
        // A turn whose result is text plus two images is the biggest single release in the history: the pass that
        // reaches it must replace the text with one receipt line, each image with its own line, count the release by
        // its kind, and leave the ordinary reads it did not need untouched.
        var look = new ReplyTool(
            "look",
            "Looks at things.",
            ReadSchema,
            () => TextAndImages(600, 2));
        var scripts = new List<Script> { new("look"), new() };
        scripts.AddRange(Turns(4, argumentsJson: ReadArguments));
        var (host, provider, compactor, root) = Subject(10_000, [.. scripts], look, ReadTool(4_000));
        var events = new List<AgentEvent>();
        for (var turn = 0; turn < 5; turn++)
        {
            events.AddRange(await RunTurnAsync(host, root, "go"));
        }

        var passes = events.OfType<AgentContextReduced>().ToList();
        Assert.NotEmpty(passes);
        Assert.Equal(3_200, passes.Sum(pass => pass.ImageTokens));
        Assert.Equal(150, passes.Sum(pass => pass.OtherTokens));

        // While the mixed result still sits inside the keepRecent window the rung has nothing to reach and the
        // summarizer answers once; after it leaves, the receipts alone carry every later crossing.
        var summarizer = Assert.Single(compactor.Requests);
        Assert.Equal(CompactionReason.Auto, summarizer.Reason);

        var cropped = FirstCroppedRequest(provider.Captured);
        var mixed = cropped.SelectMany(static message => message.Content)
            .OfType<ToolResultBlock>()
            .Single(block => block.CallId == "c1");
        var text = mixed.Blocks.OfType<TextBlock>().ToList();
        Assert.Equal(3, text.Count);
        Assert.Contains("[omitted: read_history ", text[0].Text);
        Assert.Contains("look", text[0].Text);
        Assert.All(text.Skip(1), block => Assert.Equal("[omitted: image]", block.Text));

        // A read the greedy pass did not need stays whole.
        Assert.Contains(
            cropped.SelectMany(static message => message.Content).OfType<ToolResultBlock>(),
            block => block.CallId != "c1" && block.Blocks.OfType<TextBlock>().Single().Text.Length == 4_000);
    }

    [Fact]
    public async Task The_overflow_fallback_stays_on_the_summarizer_path()
    {
        // A provider rejection for a too-long prompt must go through the forced compaction alone: the reduction rung is
        // for the line before the request, not for the recovery after it, and no receipt may appear on that path.
        var overflow = new ProviderException("prompt is too long: 1000000 tokens > 10000 maximum", retryable: false);
        overflow.Data[ProviderErrors.ContextOverflowKey] = true;
        var (host, provider, compactor, root) = Subject(10_000, [new Script(Error: overflow), new Script()], ReadTool(4_000));

        var events = new List<AgentEvent>();
        await foreach (var evt in host.RunAsync(root, "go", TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        var failed = Assert.Single(events.OfType<AgentFailed>());
        Assert.Contains("prompt is too long", failed.Error);
        Assert.Empty(events.OfType<AgentContextReduced>());
        var request = Assert.Single(compactor.Requests);
        Assert.Equal(CompactionReason.Overflow, request.Reason);
    }




}
