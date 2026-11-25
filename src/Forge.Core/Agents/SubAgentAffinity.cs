using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// How much a candidate sub-agent's context overlaps the task about to be handed out — the deterministic stand-in for
/// "is this the same work". Reuse only pays when it is: continuing an idle child keeps its context and its provider-side
/// prompt cache, but a task about something else gets stapled onto a conversation that then drags the model off course. So
/// the scheduler reuses only above <see cref="AgentOptions.SubAgentReuseThreshold"/>, and this is what that number counts.
/// <para>Two signals, both read from plain text: the file names a side names, worth <see cref="PathWeight"/> each, and the
/// terms it uses, worth one. The candidate's side is every user and assistant text block of its history - the child brings
/// its whole conversation to the next task, so a file it named three turns ago is as much evidence as one it named last.
/// Tool calls and tool results are not read: tool output is the bulk of a history, and parsing every candidate's tool
/// arguments costs more than it buys at spawn time, so a file the candidate only edited without naming it in prose is
/// invisible here.</para>
/// <para>A CJK stretch contributes its two-character terms, because a Chinese sentence arrives without spaces and taken as
/// one word it could only ever match itself; a single CJK character is too generic to count. Latin words contribute
/// themselves from three characters up, minus <see cref="Stopwords"/>.</para>
/// </summary>
internal static class SubAgentAffinity
{
    /// <summary>What one shared file name is worth, against one shared term.</summary>
    internal const int PathWeight = 3;

    /// <summary>Words that carry no topic: they appear in every task and would make unrelated ones look alike.</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "this", "that", "from", "into", "task", "file", "files", "code", "run", "use",
        "all", "not", "you", "your", "are", "was", "were", "will", "can", "get", "set", "new", "old", "add", "fix",
        "one", "two", "also", "them", "then", "than", "but", "its", "our", "their", "has", "have", "how", "why",
        "what", "when", "where", "which", "who", "put", "see", "own", "per", "via", "out", "any", "each", "more",
        "most", "some", "such", "only", "just", "same", "other", "over", "after", "before", "make", "made", "keep",
    };

    /// <summary>
    /// The overlap between <paramref name="task"/> (the new one) and the candidate's <paramref name="evidence"/>, in
    /// <see cref="PathWeight"/>-weighted units.
    /// </summary>
    internal static int Score(string task, AffinityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var wanted = Read(task);
        if (wanted.IsEmpty)
        {
            return 0;
        }

        var shared = 0;
        foreach (var path in wanted.Paths)
        {
            if (evidence.Paths.Contains(path))
            {
                shared += PathWeight;
            }
        }

        foreach (var term in wanted.Terms)
        {
            if (evidence.Terms.Contains(term))
            {
                shared++;
            }
        }

        return shared;
    }

    /// <summary>
    /// Everything a candidate brings to a new task: the text of every user and assistant message of its history. Tool
    /// messages are skipped, so the bulk of a long run never reaches the scan.
    /// </summary>
    internal static AffinityEvidence ReadHistory(IReadOnlyList<AgentMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var evidence = new AffinityEvidence();

        // The caller hands in a snapshot (AgentInstance.SnapshotHistory): a delivery may append to an idle child's history at
        // the same moment this reads it.
        for (var index = 0; index < history.Count; index++)
        {
            var message = history[index].Message;
            if (message.Role is not (MessageRole.User or MessageRole.Assistant))
            {
                continue;
            }

            foreach (var block in message.Content)
            {
                if (block is TextBlock { Text.Length: > 0 } text)
                {
                    evidence.Add(text.Text);
                }
            }
        }

        return evidence;
    }

    /// <summary>The evidence one piece of text carries on its own.</summary>
    internal static AffinityEvidence Read(string text)
    {
        var evidence = new AffinityEvidence();
        evidence.Add(text);
        return evidence;
    }

    /// <summary>
    /// The words of <paramref name="text"/> that can name a topic: a path reduced to its file name, a bare identifier, and
    /// a CJK stretch cut into two-character terms. The scan is hand-rolled rather than a regex because it runs for every
    /// candidate of every spawn and allocates only for the words it keeps.
    /// </summary>
    internal static void Read(ReadOnlySpan<char> text, AffinityEvidence evidence)
    {
        if (text.IsEmpty)
        {
            return;
        }

        var length = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            var current = index < text.Length ? text[index] : ' ';
            if (char.IsLetterOrDigit(current) || current is '_' or '.' or '/' or '\\' or '-')
            {
                length++;
                continue;
            }

            if (length > 0)
            {
                Add(text.Slice(index - length, length), evidence);
                length = 0;
            }
        }
    }

    private static void Add(ReadOnlySpan<char> raw, AffinityEvidence evidence)
    {
        // A path is what has a separator or a dotted extension; everything else is words. The separator is taken on the
        // trimmed run, not the raw one: a run that ends in one ("src/", "a//") would otherwise index past its end.
        var trimmed = raw.Trim('/').Trim('\\').Trim('-').Trim('.');
        if (trimmed.IsEmpty)
        {
            return;
        }

        var separator = trimmed.LastIndexOfAny('/', '\\');
        if (separator >= 0 || IsDotted(trimmed))
        {
            var name = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
            if (name.Length >= 3)
            {
                evidence.Paths.Add(name.ToString().ToLowerInvariant());
            }

            return;
        }

        if (!ContainsCjk(trimmed))
        {
            AddWord(trimmed, evidence);
            return;
        }

        // CJK and Latin inside one run ("worker复用"): each stretch contributes the way it would on its own.
        var start = 0;
        var inCjk = IsCjk(trimmed[0]);
        for (var index = 1; index <= trimmed.Length; index++)
        {
            var cjk = index < trimmed.Length && IsCjk(trimmed[index]);
            if (index < trimmed.Length && cjk == inCjk)
            {
                continue;
            }

            if (inCjk)
            {
                AddCjk(trimmed[start..index], evidence);
            }
            else
            {
                AddWord(trimmed[start..index], evidence);
            }

            start = index;
            inCjk = cjk;
        }
    }

    private static void AddWord(ReadOnlySpan<char> word, AffinityEvidence evidence)
    {
        if (word.Length < 3)
        {
            return;
        }

        var term = word.ToString().ToLowerInvariant();
        if (!Stopwords.Contains(term))
        {
            evidence.Terms.Add(term);
        }
    }

    /// <summary>One term per adjacent pair: the topic a Chinese sentence is about, without a dictionary to segment it.</summary>
    private static void AddCjk(ReadOnlySpan<char> run, AffinityEvidence evidence)
    {
        for (var index = 0; index + 1 < run.Length; index++)
        {
            evidence.Terms.Add(run.Slice(index, 2).ToString());
        }
    }

    private static bool ContainsCjk(ReadOnlySpan<char> word)
    {
        foreach (var value in word)
        {
            if (IsCjk(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>CJK ideographs (with their extensions), kana and hangul: the scripts that arrive without word separators.</summary>
    private static bool IsCjk(char value) =>
        value is (>= '\u2E80' and <= '\u9FFF') or (>= '\uAC00' and <= '\uD7AF') or (>= '\uF900' and <= '\uFAFF');

    /// <summary>
    /// Whether <paramref name="line"/> is a review's closing verdict: the word a verdict line starts with, after the list and
    /// markdown markers a report may put in front of it.
    /// </summary>
    internal static bool IsVerdictLine(ReadOnlySpan<char> line)
    {
        var trimmed = line.TrimStart();
        // Whitespace rides along with the markers: a report says "- **verdict: approve**" as often as it says "verdict: approve".
        while (!trimmed.IsEmpty && (trimmed[0] is '-' or '*' or '#' or '>' or '_' or '`' or '"' or '\'' or '|' or ' ' or '\t'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.StartsWith("verdict", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotted(ReadOnlySpan<char> word)
    {
        var dot = word.LastIndexOf('.');
        return dot > 0 && word.Length - dot > 1;
    }
}

/// <summary>What one side of the comparison names: the file names it mentions and the terms it uses.</summary>
internal sealed class AffinityEvidence
{
    internal HashSet<string> Paths { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> Terms { get; } = new(StringComparer.Ordinal);

    internal bool IsEmpty => Paths.Count == 0 && Terms.Count == 0;

    /// <summary>
    /// Adds one piece of text to this side of the comparison, line by line: a verdict line is skipped, because it says how a
    /// review ended rather than what the review was about, and a later review must be continued for the change it reads - never
    /// for the word "approve" in an earlier report.
    /// </summary>
    internal void Add(string text)
    {
        ReadOnlySpan<char> remaining = text;
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf('\n');
            var line = newline < 0 ? remaining : remaining[..newline];
            if (!SubAgentAffinity.IsVerdictLine(line))
            {
                SubAgentAffinity.Read(line, this);
            }

            if (newline < 0)
            {
                return;
            }

            remaining = remaining[(newline + 1)..];
        }
    }
}
