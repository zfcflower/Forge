using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Storage;

namespace Forge.Bench;

/// <summary>
/// The turn ledger's own hot paths (forge-core "Turn Ledger"): the steady-state request cost a host pays for keeping a
/// ledger at all, the evidence collection that reads a batch's calls and results without touching a file, the begin+settle
/// write pair behind one row, and the composer's cost.
/// <para><c>agentloop.dynamic</c> carries the steady-state budget: a host WITH a ledger may not allocate more than 1 KiB per
/// request over the same host without one - same scripted provider, same layers, same tools - averaged over the requests
/// after the first. <c>turnledger.evidence</c> and <c>prompt.compose</c> report only. <c>turnledger.write</c> reports only on
/// purpose: SQLite IO jitter must not turn a build red; the contract it names is the store's own ≤ 5 ms per turn.</para>
/// </summary>
internal static class TurnLedgerScenarios
{
    /// <summary>How many requests one steady-state op runs; the first of a run is excluded by the warmup, not the clock.</summary>
    private const int DynamicRequests = 10;

    /// <summary>The ledger's steady-state budget, per request, over the same host without one.</summary>
    private const long DynamicAllocationLimit = 1024;

    /// <summary>Calls in one evidence batch: the shape a tool-heavy turn really sends, paths arrays included.</summary>
    private const int EvidenceCalls = 40;

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        yield return Dynamic(fixtures);
        yield return Evidence();
        yield return Write(fixtures);
        yield return Compose();
    }

    /// <summary>
    /// The steady-state per-request cost of a host WITH a ledger against the same host WITHOUT one: the same scripted
    /// provider, the same layers and tools, answers that always follow the template (no nudge is ever pending). One op
    /// is <see cref="DynamicRequests"/> turns; the harness's warmup absorbs each run's first request (the one whose
    /// one-time costs - the first-request cache report, the resolved cache state - are not steady state), and the hard
    /// check is the paired difference: the ledger host may not add more than 1 KiB per request.
    /// </summary>
    private static BenchScenario Dynamic(Fixtures fixtures)
    {
        return new BenchScenario(
            "agentloop.dynamic",
            "agentloop",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var provider = new ScriptedProvider();
                var withLedger = Host(provider, new BenchLedgerStore());
                var withoutLedger = Host(provider, null);
                var ledgerRoot = withLedger.CreateRoot("bench-ledger-dynamic", fixtures.Workspace.Root);
                var plainRoot = withoutLedger.CreateRoot("bench-ledger-plain", fixtures.Workspace.Root);

                Func<AgentInstance, CancellationToken, Task<OpResult>> run = async (root, token) =>
                {
                    for (var request = 0; request < DynamicRequests; request++)
                    {
                        await foreach (var _ in (root == ledgerRoot ? withLedger : withoutLedger).RunAsync(root, "continue the work", token))
                        {
                        }
                    }

                    return new OpResult(Items: DynamicRequests, Signature: provider.Calls);
                };

                return Task.FromResult(new BenchCase
                {
                    Run = token => run(ledgerRoot, token),
                    Baseline = token => run(plainRoot, token),
                    BaselineName = "the same host without a ledger",
                    ProcessWideAllocation = true,
                    Iterations = 10,
                    Note = $"one sample = {DynamicRequests} turns of a scripted, tool-less session; the average is per request",
                    Check = comparison =>
                    {
                        var perRequest = (comparison.Implementation.AllocatedBytesPerOp - comparison.Baseline!.AllocatedBytesPerOp)
                                         / DynamicRequests;
                        return new Verdict(
                            "a ledger host allocates at most 1 KiB per steady-state request over the same host without one",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{perRequest:N0} B/request "
                                + $"({comparison.Implementation.AllocatedBytesPerOp:N0} vs {comparison.Baseline.AllocatedBytesPerOp:N0} B per {DynamicRequests} requests, "
                                + $"{Harness.Milliseconds(comparison.Implementation.P50Ms)} ms vs {Harness.Milliseconds(comparison.Baseline.P50Ms)} ms per {DynamicRequests})"),
                            perRequest <= DynamicAllocationLimit);
                    },
                });
            });
    }

    private static AgentHost Host(IChatProvider provider, ITurnLedgerStore? ledger) =>
        new(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("bench system layer", "bench global agent", null),
            TurnLedger = ledger,
        });

    /// <summary>Answers every request with one valid completion template; never a nudge follows.</summary>
    private sealed class ScriptedProvider : IChatProvider
    {
        private static readonly ChatMessage Answer = new(
            MessageRole.Assistant,
            [new TextBlock(
                """
                The scripted answer: one turn of steady-state work, no tools, no nudge pending.

                **Status**:

                - unresolved: —
                - next: —
                """)]);

        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Interlocked.Increment(ref calls);
            foreach (var block in Answer.Content)
            {
                if (block is TextBlock text)
                {
                    yield return new TextDelta(text.Text);
                }
            }

            yield return new MessageCompleted(Answer, TokenUsage.Empty, StopReason.EndTurn);
        }
    }

    /// <summary>The store a ledger host in a bench gets: numbers from a counter, writes into the void.</summary>
    private sealed class BenchLedgerStore : ITurnLedgerStore
    {
        private long nextTurn;

        public Task<long> BeginAsync(TurnLedgerBegin begin, CancellationToken ct) =>
            Task.FromResult(begin.TurnSequence ?? Interlocked.Increment(ref nextTurn));

        public Task CompleteAsync(TurnLedgerCompletion completion, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<TurnLedgerEntry>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TurnLedgerEntry>>([]);

        public Task<TurnLedgerEntry?> GetAsync(string sessionId, long turnSequence, CancellationToken ct) =>
            Task.FromResult<TurnLedgerEntry?>(null);

        public Task<TurnLedgerEntry?> FindByAnswerSeqAsync(string sessionId, long answerSeq, CancellationToken ct) =>
            Task.FromResult<TurnLedgerEntry?>(null);

        public Task MarkFoldedAsync(string sessionId, long throughTurn, CancellationToken ct) => Task.CompletedTask;


        public Task<IReadOnlyList<TurnLedgerHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TurnLedgerHit>>([]);
    }

    /// <summary>
    /// One evidence batch of <see cref="EvidenceCalls"/> calls with its results: <c>read</c> with a path, <c>read</c>
    /// with a paths array, the write tools with their paths, and a <c>spec_get</c> - plus the single-file read result
    /// that begins with a spec's frontmatter. Nothing opens a file: every path names a file that was never created.
    /// Report only.
    /// </summary>
    private static BenchScenario Evidence()
    {
        return new BenchScenario(
            "turnledger.evidence",
            "turnledger",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var (calls, results) = EvidenceBatch();
                return Task.FromResult(new BenchCase
                {
                    Run = async token =>
                    {
                        await Task.CompletedTask;
                        var evidence = new TurnLedgerEvidence();
                        evidence.Collect(calls, results);
                        return new OpResult(
                            Items: calls.Length,
                            Signature: evidence.ReadFiles.Count + evidence.ChangedFiles.Count + evidence.Specs.Count,
                            Observation: $"read={evidence.ReadFiles.Count} wrote={evidence.ChangedFiles.Count} "
                                + $"specs={evidence.Specs.Count} calls={calls.Length}");
                    },
                    Iterations = 200,
                    Note = $"{EvidenceCalls} calls per batch: read (path), read (paths[3]), edit/write/delete, copy/move (to), spec_get, "
                           + "one read result with frontmatter; no file is ever opened",
                });
            });
    }

    private static (ToolCallBlock[] Calls, ContentBlock[] Results) EvidenceBatch()
    {
        var calls = new ToolCallBlock[EvidenceCalls];
        var results = new ContentBlock[EvidenceCalls];
        // The production read answer's own shape: a header line, then every line prefixed with its right-aligned
        // number and the arrow - the scan the evidence collection runs against.
        var frontmatter =
            "src/spec.md: 5 lines\n" +
            "1→---\n" +
            "2→id: bench-evidence-spec\n" +
            "3→---\n" +
            "4→\n" +
            "5→The spec's own body text, already in memory as the tool's result.";
        for (var index = 0; index < EvidenceCalls; index++)
        {
            var id = index.ToString(CultureInfo.InvariantCulture);
            string arguments;
            string result;
            switch (index % 8)
            {
                case 0:
                    arguments = $$"""{"path":"src/module{{id}}/file.cs"}""";
                    result = "src: 12 lines";
                    break;
                case 1:
                    arguments = $$"""{"paths":["src/a{{id}}.md","src/b{{id}}.md","src/c{{id}}.md"]}""";
                    result = "src/a.md: 3 lines\n1→one\n2→two\n3→three\nsrc/b.md: 2 lines";
                    break;
                case 2:
                    arguments = $$"""{"path":"src/spec{{id}}.md"}""";
                    // The single-file read whose result begins with a frontmatter block carrying id.
                    result = frontmatter;
                    break;
                case 3:
                    arguments = $$"""{"id":"forge-core-{{id}}"}""";
                    result = "one spec's entry";
                    break;
                case 4:
                    arguments = $$"""{"path":"src/edit{{id}}.cs","content":"x"}""";
                    result = "written";
                    break;
                case 5:
                    arguments = $$"""{"path":"src/write{{id}}.cs","content":"x"}""";
                    result = "written";
                    break;
                case 6:
                    arguments = $$"""{"paths":["src/delete-a{{id}}.cs","src/delete-b{{id}}.cs"]}""";
                    result = "deleted 2 files";
                    break;
                default:
                    arguments = $$"""{"from":"src/from{{id}}.cs","to":"src/to{{id}}.cs"}""";
                    result = "moved";
                    break;
            }

            calls[index] = new ToolCallBlock($"c{id}", ToolName(index), arguments);
            results[index] = new ToolResultBlock($"c{id}", result, IsError: false);
        }

        return (calls, results);
    }

    private static string ToolName(int index)
    {
        var shape = index % 8;
        if (shape is 0 or 1 or 2)
        {
            return "read";
        }

        return shape switch
        {
            3 => "spec_get",
            4 => "edit",
            5 => "write",
            6 => "delete",
            _ => "move",
        };
    }

    /// <summary>
    /// One row per op - the store's own next number, the pending write, the settle with its FTS rewrite - on a temp
    /// database under the bench workspace. Report only: SQLite IO jitter must not turn a build red. The number to read
    /// it against is the contract the store owns: ≤ 5 ms per turn for begin + settle together.
    /// </summary>
    private static BenchScenario Write(Fixtures fixtures)
    {
        return new BenchScenario(
            "turnledger.write",
            "turnledger",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var directory = fixtures.Workspace.DirectoryAt("turnledger/store");
                var store = new SqliteTurnLedgerStore(directory);
                const string session = "bench-write";
                return Task.FromResult(new BenchCase
                {
                    Run = async operationToken =>
                    {
                        // The number and its reservation are the begin's own insert, not a read beside it: the caller
                        // hands nothing over and takes back the row it was filed under.
                        var number = await store.BeginAsync(Begin(session, null), operationToken).ConfigureAwait(false);
                        await store.CompleteAsync(Completion(session, number), operationToken).ConfigureAwait(false);
                        return new OpResult(Items: 1, Signature: number, Observation: $"row {number}");
                    },
                    ProcessWideAllocation = true,
                    Iterations = 20,
                    Note = "one op = begin (allocates and reserves the number in the insert) + settle + FTS on a temp database; contract budget: ≤ 5 ms per turn",
                });
            });
    }

    private static TurnLedgerBegin Begin(string session, long? turn) =>
        new(
            session,
            turn,
            "the task this turn was given",
            "anthropic",
            "claude-sonnet",
            "Root",
            0,
            4,
            DateTimeOffset.UnixEpoch);

    private static TurnLedgerCompletion Completion(string session, long turn) =>
        new(
            session,
            turn,
            TurnLedgerStatus.Settled,
            "the answer as shown",
            "the parsed conclusion",
            new TurnEvidence(["src/one.cs", "src/two.cs"], ["src/three.cs"], ["forge-core"], 4, 12),
            12,
            DateTimeOffset.UnixEpoch);

    /// <summary>
    /// The composer over all three layers, role included: what one request's stable prefix costs to build. Report only.
    /// </summary>
    private static BenchScenario Compose()
    {
        var layers = new PromptLayers("bench system layer", "bench global agent", "bench project agent");
        return new BenchScenario(
            "prompt.compose",
            "prompt",
            Heavy: false,
            PrepareAsync: _ => Task.FromResult(new BenchCase
            {
                Run = _ =>
                {
                    var composed = new PromptComposer().Compose(new PromptInput(layers, "the agent's own role"));
                    return Task.FromResult(new OpResult(Bytes: composed.StablePrefix.Length, Items: 1, Signature: 0));
                },
                Baseline = _ =>
                {
                    var composed = new PromptComposer().Compose(new PromptInput(new PromptLayers("bench system layer", null, null), null));
                    return Task.FromResult(new OpResult(Bytes: composed.StablePrefix.Length, Items: 1, Signature: 0));
                },
                BaselineName = "the system layer alone",
                Iterations = 200,
                Note = "one compose over all three layers against the system layer alone; report only",
            }));
    }
}
