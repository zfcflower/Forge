using System.Text;
using Forge.Abstractions;
using Forge.Core.Agents;

namespace Forge.Bench;

/// <summary>
/// The sub-agent reuse scan: what one <c>spawn_agent</c> pays to find the idle child that already did the work.
/// <c>subagent.affinity.last-exchange</c> is the口径 the scheduler used before it read the whole conversation - the new task
/// scored against the candidate's last task and last answer - and it is the baseline the widened口径 is compared with.
/// <c>subagent.affinity.full-history</c> is the口径 it uses now: the new task against every user and assistant message of the
/// history. <c>subagent.affinity.cjk</c> repeats the widened scan over Chinese prose, where every adjacent character pair is
/// a term, and carries the Latin scan as its baseline. One call scores a pool of ten candidates of one kind, which is what a
/// session that worked through a plan accumulates; the scan runs once per candidate per spawn and keeps nothing.
/// Run with <c>--filter subagent</c>.
/// </summary>
internal static class SubAgentScenarios
{
    private const int Candidates = 10;
    private const int MessagesPerCandidate = 15;
    private const int CharsPerMessage = 600;

    private const string LatinTask =
        "Continue the sub-agent reuse work in src/Forge.Core/Agents/SubAgentAffinity.cs: the scheduler hands a new task to the "
        + "idle child whose history names the same files.";

    private const string ChineseTask = "继续修标签里数字居中的问题，顺便把运行计数和复用窗口一起看一遍";

    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        var latin = new Pool(Histories(chinese: false), LatinTask);
        var chinese = new Pool(Histories(chinese: true), ChineseTask);
        yield return Scan(
            "subagent.affinity.last-exchange",
            latin,
            wholeHistory: false,
            baseline: null,
            baselineName: null);
        yield return Scan(
            "subagent.affinity.full-history",
            latin,
            wholeHistory: true,
            baseline: () => Score(latin, wholeHistory: false),
            baselineName: "last exchange only");
        yield return Scan(
            "subagent.affinity.cjk",
            chinese,
            wholeHistory: true,
            baseline: () => Score(latin, wholeHistory: true),
            baselineName: "the same Latin pool");
    }

    private static BenchScenario Scan(
        string name,
        Pool pool,
        bool wholeHistory,
        Func<OpResult>? baseline,
        string? baselineName) =>
        new(
            name,
            "subagent",
            Heavy: false,
            PrepareAsync: _ => Task.FromResult(new BenchCase
            {
                Warmup = 20,
                Iterations = 200,
                Note = $"{pool.Histories.Length} candidates x {MessagesPerCandidate} messages of ~{CharsPerMessage} chars; "
                       + (wholeHistory ? "the whole conversation is read per candidate" : "the last task and answer only"),
                Baseline = baseline is null ? null : _ => Task.FromResult(baseline()),
                BaselineName = baselineName,
                Run = _ => Task.FromResult(Score(pool, wholeHistory)),
                Check = comparison => comparison.Implementation.P50Ms <= 5.0
                    ? new Verdict(
                        "one spawn's candidate scan stays under 5 ms",
                        $"{comparison.Implementation.P50Ms:0.000} ms",
                        true)
                    : new Verdict(
                        "one spawn's candidate scan stays under 5 ms",
                        $"{comparison.Implementation.P50Ms:0.000} ms",
                        false),
            }));

    private static OpResult Score(Pool pool, bool wholeHistory)
    {
        var total = 0;
        var chars = 0;
        foreach (var history in pool.Histories)
        {
            // Score re-reads the new task for every candidate, exactly as the scheduler's Reusable() does.
            total += SubAgentAffinity.Score(pool.Task, wholeHistory ? SubAgentAffinity.ReadHistory(history) : LastExchange(history));
            foreach (var message in history)
            {
                foreach (var block in message.Message.Content)
                {
                    if (block is TextBlock text)
                    {
                        chars += text.Text.Length;
                    }
                }
            }
        }

        return new OpResult(
            Bytes: chars,
            Items: pool.Histories.Length,
            Signature: total,
            Observation: $"score={total} chars={chars}");
    }

    /// <summary>The口径 the scheduler used before the widening: the candidate's last task and last answer alone.</summary>
    private static AffinityEvidence LastExchange(List<AgentMessage> history)
    {
        var evidence = new AffinityEvidence();
        string? lastTask = null;
        string? lastReport = null;
        for (var index = history.Count - 1; index >= 0 && (lastTask is null || lastReport is null); index--)
        {
            var message = history[index].Message;
            if (message.Role == MessageRole.Assistant && lastReport is null)
            {
                lastReport = TextOf(message);
            }
            else if (message.Role == MessageRole.User && lastTask is null)
            {
                lastTask = TextOf(message);
            }
        }

        evidence.Add(lastTask ?? string.Empty);
        evidence.Add(lastReport ?? string.Empty);
        return evidence;
    }

    private static string TextOf(ChatMessage message)
    {
        var builder = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block is TextBlock { Text.Length: > 0 } text)
            {
                builder.Append(text.Text).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Realistic worker prose: file names, identifiers and a report, padded to <see cref="CharsPerMessage"/> with clauses that
    /// vary per message - a history that repeats one sentence would deduplicate into a handful of terms and measure nothing.
    /// </summary>
    private static List<AgentMessage>[] Histories(bool chinese)
    {
        var histories = new List<AgentMessage>[Candidates];
        for (var candidate = 0; candidate < Candidates; candidate++)
        {
            var history = new List<AgentMessage>(MessagesPerCandidate);
            for (var message = 0; message < MessagesPerCandidate; message++)
            {
                var role = message % 2 == 0 ? MessageRole.User : MessageRole.Assistant;
                var text = chinese
                    ? Text(chinese: true, candidate, message)
                    : Text(chinese: false, candidate, message);
                history.Add(new AgentMessage(new ChatMessage(role, [new TextBlock(text)]), message + 1));
            }

            histories[candidate] = history;
        }

        return histories;
    }

    private static string Text(bool chinese, int candidate, int message)
    {
        var builder = new StringBuilder(CharsPerMessage + 64);
        if (chinese)
        {
            builder.Append("第").Append(message + 1).Append("轮：看了 SubAgentScheduler")
                .Append(candidate).Append('_').Append(message)
                .Append(".cs 里复用窗口的判断，把结束时间记在实例旁边，顺手清理了旧的分支。");
            while (builder.Length < CharsPerMessage)
            {
                builder.Append("第").Append(builder.Length).Append("处又发现一遍同样的边界问题，")
                    .Append(candidate % 3 == 0 ? "把池子接过来" : "把窗口挪到实例上")
                    .Append("，用 dotnet build 和复用测试验证过，")
                    .Append(candidate % 2 == 0 ? "剩下的问题是要把证据面放宽" : "剩下的问题是要让中文也能命中")
                    .Append("；");
            }

            return builder.ToString();
        }

        builder.Append("Turn ").Append(message + 1).Append(": read src/Forge.Core/Agents/SubAgentScheduler.cs and tightened the ")
            .Append("reuse window check; the pool keeps finishedAt beside the instances. ");
        while (builder.Length < CharsPerMessage)
        {
            builder.Append("Sweep ").Append(builder.Length).Append(" rewrote the ")
                .Append(candidate % 3 == 0 ? "candidate ranking" : "window comparison")
                .Append(" and verified it with dotnet build plus the reuse tests, leaving ")
                .Append(candidate % 2 == 0 ? "the widened evidence" : "the Chinese tokenizer")
                .Append(" for the next pass. ");
        }

        return builder.ToString();
    }

    private sealed record Pool(List<AgentMessage>[] Histories, string Task);
}
