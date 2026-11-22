using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Forge.Core.Storage;

namespace Forge.Bench;

/// <summary>
/// The compaction paths a turn pays for, at the reference model the product ships against: a 1M-token window (R = 200 000
/// with outputs unknown, so U = 800 000, trigger line 680 000, target 400 000).
/// <para><c>compaction.budget</c> and <c>compaction.wire</c> are the per-request arithmetic, which must stay allocation-free;
/// <c>compaction.estimate.*</c> is the token estimate behind the context reading — the whole history on the cold path (after
/// every compaction replaced it) and the appended tail on the hot one; <c>compaction.plan-cut</c> is the cut search over a
/// large raw tail, run once per compaction; <c>compaction.file-list.*</c> renders the two bounded lists (a list that fits, one
/// that is truncated with the dropped-count notice, and a re-seed from a stored rendering); <c>compaction.floor</c> is the
/// irreducible-floor precheck added before the summarizer, which runs only when the context is already over the line.</para>
/// <para>What these rows do not cover: the summarizer itself (a provider round trip, not our code) and
/// <c>ToolResultReduction</c>, which is internal to Forge.Core. Run with <c>--filter compaction</c>.</para>
/// </summary>
internal static class CompactionScenarios
{
    /// <summary>Messages of the reference history: 400 × 2 500 tokens is 1M tokens, a session that already compacted twice.</summary>
    private const int HistoryMessages = 400;

    /// <summary>Characters per message on the Latin path: four characters per token, so 10 000 characters are 2 500 tokens.</summary>
    private const int MessageChars = 10_000;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        _ = fixtures;
        yield return Budget();
        yield return Wire();
        yield return Estimate("compaction.estimate.history", HistoryMessages, warmup: 20, iterations: 200, "the whole history: the cold path after a compaction, and the floor's inherited half");
        yield return Estimate("compaction.estimate.appended", 20, warmup: 500, iterations: 20_000, "the appended tail: one request's steady-state context reading");
        yield return PlanCut();
        yield return FileList("compaction.file-list.fits", paths: 60, warmup: 50, iterations: 2_000, "below both budgets");
        yield return FileList("compaction.file-list.over-budget", paths: 1_200, warmup: 20, iterations: 500, "truncated, with the dropped-count notice");
        yield return Reseed();
        yield return Floor();
    }

    /// <summary>The reserve arithmetic: three inputs, no allocation, called before every model request.</summary>
    private static BenchScenario Budget()
    {
        // The four shapes are precomputed: Task.FromResult would otherwise allocate 96 B per call and the row would
        // report the harness's own cost instead of the arithmetic's.
        var answers = new[]
        {
            CompactionPolicy.Budget(1_000_000, null)!.Value,
            CompactionPolicy.Budget(1_000_000, 64_000)!.Value,
            CompactionPolicy.Budget(1_000_000, 64_000, wireMaxOutput: 53_248)!.Value,
            CompactionPolicy.Budget(1_000_000, 64_000, keepRecentTokens: 100_000)!.Value,
        };
        var results = new Task<OpResult>[answers.Length];
        for (var index = 0; index < answers.Length; index++)
        {
            results[index] = Task.FromResult(new OpResult(Items: 1, Signature: answers[index].Usable));
        }

        var step = 0L;
        return new BenchScenario(
            "compaction.budget",
            "compaction",
            Heavy: false,
            token => Task.FromResult(new BenchCase
            {
                Warmup = 10_000,
                Iterations = 1_000_000,
                Note = "one Budget() per model request; 1M window, four reserve shapes cycled",
                Run = token =>
                {
                    var shape = (int)(step++ % 4);
                    _ = shape switch
                    {
                        0 => CompactionPolicy.Budget(1_000_000, null),
                        1 => CompactionPolicy.Budget(1_000_000, 64_000),
                        2 => CompactionPolicy.Budget(1_000_000, 64_000, wireMaxOutput: 53_248),
                        _ => CompactionPolicy.Budget(1_000_000, 64_000, keepRecentTokens: 100_000),
                    };
                    return results[shape];
                },
                Check = comparison => comparison.Implementation.AllocatedBytesPerOp == 0
                    ? new Verdict("Budget() allocates nothing", "0 B/op", true)
                    : new Verdict("Budget() allocates nothing", $"{comparison.Implementation.AllocatedBytesPerOp} B/op", false),
            }));
    }

    /// <summary>The single figure the adapter writes as max_tokens, which the reserve then uses.</summary>
    private static BenchScenario Wire()
    {
        var heavy = new ModelSettings("claude", ThinkingEffort.High, null);
        var done = Task.FromResult(new OpResult(Items: 1, Signature: OutputCeiling.Wire(heavy)));
        return new BenchScenario(
            "compaction.wire",
            "compaction",
            Heavy: false,
            token => Task.FromResult(new BenchCase
            {
                Warmup = 10_000,
                Iterations = 1_000_000,
                Note = "one OutputCeiling.Wire() per model request (policy resolver and Anthropic body); 24 576-token thinking budget",
                Run = token =>
                {
                    _ = OutputCeiling.Wire(heavy);
                    return done;
                },
                Check = comparison => comparison.Implementation.AllocatedBytesPerOp == 0
                    ? new Verdict("Wire() allocates nothing", "0 B/op", true)
                    : new Verdict("Wire() allocates nothing", $"{comparison.Implementation.AllocatedBytesPerOp} B/op", false),
            }));
    }

    /// <summary>One token estimate over <paramref name="messages"/> messages of <see cref="MessageChars"/> characters each.</summary>
    private static BenchScenario Estimate(string name, int messages, int warmup, int iterations, string note)
    {
        var counter = new ApproximateTokenCounter();
        var history = Messages(messages);
        return new BenchScenario(
            name,
            "compaction",
            Heavy: false,
            token => Task.FromResult(new BenchCase
            {
                Warmup = warmup,
                Iterations = iterations,
                Note = $"{messages} messages × {MessageChars} chars = {messages * (MessageChars / 4)} tokens; {note}",
                Run = token => Task.FromResult(new OpResult(
                    Bytes: (long)messages * MessageChars,
                    Items: messages,
                    Signature: counter.Count(history))),
            }));
    }

    /// <summary>The cut search: a suffix sum plus a scan for the longest legal suffix, over the raw tail of a compacted session.</summary>
    private static BenchScenario PlanCut()
    {
        var raw = Raw(20_000);
        return new BenchScenario(
            "compaction.plan-cut",
            "compaction",
            Heavy: false,
            token => Task.FromResult(new BenchCase
            {
                Warmup = 20,
                Iterations = 200,
                Note = "20 000 raw messages, goal = 100 000 tokens: the whole tail on every compaction",
                Run = token =>
                {
                    var cut = SummaryLayers.PlanCut(raw, 100_000, 80_000, static message => message.Content.Count * 50);
                    return Task.FromResult(new OpResult(Items: raw.Count, Signature: cut?.KeepFrom ?? -1));
                },
                Check = comparison => comparison.Implementation.Signature > 0
                    ? new Verdict("PlanCut finds a cut", $"keepFrom={comparison.Implementation.Signature}", true)
                    : new Verdict("PlanCut finds a cut", "no cut (the tail fits)", false),
            }));
    }

    /// <summary>Rendering both bounded lists, which happens once per compaction and once more for the stored summary.</summary>
    private static BenchScenario FileList(string name, int paths, int warmup, int iterations, string note)
    {
        var files = Files(paths);
        return new BenchScenario(
            name,
            "compaction",
            Heavy: false,
            token => Task.FromResult(new BenchCase
            {
                Warmup = warmup,
                Iterations = iterations,
                Note = $"{paths} read + {paths} modified paths; {note}",
                Run = token => Task.FromResult(new OpResult(Items: paths * 2, Signature: files.Render().Length)),
                Check = comparison => comparison.Implementation.Signature <= CompactionFiles.ReadListChars + CompactionFiles.ModifiedListChars
                    ? new Verdict(
                        "both blocks stay inside their budgets",
                        $"{comparison.Implementation.Signature} chars (≤ 12 000)",
                        true)
                    : new Verdict("both blocks stay inside their budgets", $"{comparison.Implementation.Signature} chars", false),
            }));
    }

    /// <summary>
    /// Rebuilding the lists from a stored rendering — what a later compaction in the same session starts from. The rendering
    /// carries the dropped-count notice and shortened paths, which must not come back as entries.
    /// </summary>
    private static BenchScenario Reseed()
    {
        var stored = Files(1_200).Render();
        var rendered = Reseed(stored);
        var names = new List<string>(200);
        var seeded = new CompactionFiles();
        const string open = "<read-files>";
        var start = stored.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = stored.IndexOf("</read-files>", start, StringComparison.Ordinal);
        foreach (var line in stored[start..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            seeded.AddEntry(line);
        }

        var expected = seeded.Read.Count;
        return new BenchScenario(
            "compaction.file-list.reseed",
            "compaction",
            Heavy: false,
            token => Task.FromResult(new BenchCase
            {
                Warmup = 20,
                Iterations = 500,
                Note = $"{expected} paths parsed back from a truncated 1 200-path rendering (the notice and the shortened paths drop out)",
                Run = token =>
                {
                    var files = new CompactionFiles();
                    var begin = stored.IndexOf(open, StringComparison.Ordinal) + open.Length;
                    var finish = stored.IndexOf("</read-files>", begin, StringComparison.Ordinal);
                    foreach (var line in stored[begin..finish].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        files.AddEntry(line);
                    }

                    return Task.FromResult(new OpResult(Items: files.Read.Count, Signature: files.Render().Length));
                },
                Check = comparison => comparison.Implementation.Signature <= CompactionFiles.ReadListChars + CompactionFiles.ModifiedListChars
                                                        && comparison.Implementation.ItemsPerOp == expected
                    ? new Verdict(
                        "a re-seed keeps what the cap kept and stays inside the budgets",
                        $"{comparison.Implementation.ItemsPerOp} paths, {comparison.Implementation.Signature} chars",
                        true)
                    : new Verdict(
                        "a re-seed keeps what the cap kept and stays inside the budgets",
                        $"{comparison.Implementation.ItemsPerOp}/{expected} paths, {comparison.Implementation.Signature} chars",
                        false),
            }));
    }

    /// <summary>
    /// The irreducible floor: two token counts (the inherited prefix and this instance's own messages) plus the stable
    /// prefix. Runs once per request that is already over the line, so this is the cost added before deciding not to ask the
    /// summarizer.
    /// </summary>
    private static BenchScenario Floor()
    {
        var counter = new ApproximateTokenCounter();
        var inherited = Messages(300);
        var own = Messages(100);
        const string Prefix = "You are Forge, a coding agent. ";
        return new BenchScenario(
            "compaction.floor",
            "compaction",
            Heavy: false,
            token => Task.FromResult(new BenchCase
            {
                Warmup = 20,
                Iterations = 200,
                Note = "300 inherited + 100 own messages of 10 000 chars: the two counts a stalled candidate adds per over-the-line request",
                Run = token =>
                {
                    // The shape AgentLoop.IrreducibleTokens uses: walk the inherited prefix once, count it, count the own
                    // messages, add the stable prefix and the tool definitions.
                    var inheritedMessages = new List<ChatMessage>(inherited.Count);
                    foreach (var message in inherited)
                    {
                        inheritedMessages.Add(message);
                    }

                    var floor = (long)counter.Count(inheritedMessages)
                                + counter.Count(Prefix)
                                + 20_000
                                + Math.Min(80_000, counter.Count(own));
                    return Task.FromResult(new OpResult(Items: inherited.Count + own.Count, Signature: (int)Math.Min(int.MaxValue, floor)));
                },
                Check = comparison => comparison.Implementation.Signature > 0
                    ? new Verdict("the floor is computed", $"floor={comparison.Implementation.Signature} tokens", true)
                    : new Verdict("the floor is computed", "floor=0", false),
            }));
    }

    /// <summary>Messages of roughly <see cref="MessageChars"/> characters each, alternating user and assistant.</summary>
    private static List<ChatMessage> Messages(int count)
    {
        var text = new string('x', MessageChars);
        var messages = new List<ChatMessage>(count);
        for (var index = 0; index < count; index++)
        {
            messages.Add(index % 2 == 0 ? ChatMessage.User(text) : ChatMessage.Assistant(text));
        }

        return messages;
    }

    /// <summary>Stored messages for <see cref="SummaryLayers.PlanCut"/>: user, assistant, then the tool result that answers it.</summary>
    private static List<StoredMessage> Raw(int count)
    {
        var messages = new List<StoredMessage>(count);
        for (var index = 0; index < count; index++)
        {
            var role = (index % 3) switch
            {
                0 => MessageRole.User,
                1 => MessageRole.Assistant,
                _ => MessageRole.Tool,
            };
            var message = role switch
            {
                MessageRole.User => ChatMessage.User("go"),
                MessageRole.Assistant => new ChatMessage(MessageRole.Assistant, [new ToolCallBlock($"c{index}", "read", "{}")]),
                _ => new ChatMessage(MessageRole.Tool, [new ToolResultBlock($"c{index - 1}", new string('x', 400))]),
            };
            messages.Add(new StoredMessage(index + 1, message, null, DateTimeOffset.UnixEpoch));
        }

        return messages;
    }

    /// <summary>Paths across both lists, long enough that 1 200 of them overflow the two budgets.</summary>
    private static CompactionFiles Files(int paths)
    {
        var files = new CompactionFiles();
        for (var index = 0; index < paths; index++)
        {
            files.AddRead($"src/forge/very/deeply/nested/folder/{index:D4}/File{index:D4}.cs", new LineRange(1, 420));
            files.AddModified($"src/forge/very/deeply/nested/folder/{index:D4}/Changed{index:D4}.cs");
        }

        return files;
    }

    /// <summary>A collection rebuilt from a stored rendering, the way the next compaction in the session starts.</summary>
    private static string Reseed(string stored)
    {
        var files = new CompactionFiles();
        const string open = "<read-files>";
        var start = stored.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = stored.IndexOf("</read-files>", start, StringComparison.Ordinal);
        foreach (var line in stored[start..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            files.AddEntry(line);
        }

        return files.Render();
    }
}
