using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// How much a candidate sub-agent's last exchange overlaps the task about to be handed out — the deterministic stand-in for
/// "is this the same work". Reuse only pays when it is: continuing an idle child keeps its context and its provider-side
/// prompt cache, but a task about something else gets stapled onto a conversation that then drags the model off course. So
/// the scheduler reuses only above <see cref="AgentOptions.SubAgentReuseThreshold"/>, and this is what that number counts.
/// <para>Two signals, both read from plain text (the new task, and the candidate's last user message and last answer): the
/// file names the two sides name, worth <see cref="PathWeight"/> each, and the identifiers they share, worth one. Text is
/// everything: a file the candidate only edited without naming it is invisible here, which is the deliberate first cut —
/// parsing every candidate's tool arguments costs more than it buys at spawn time. CJK prose contributes no terms; a
/// Chinese task still matches through the paths and identifiers it contains.</para>
/// </summary>
internal static class SubAgentAffinity
{
    /// <summary>What one shared file name is worth, against one shared identifier.</summary>
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
    /// The overlap between <paramref name="task"/> (the new one) and the candidate's last exchange, in
    /// <see cref="PathWeight"/>-weighted units.
    /// </summary>
    internal static int Score(string task, string lastTask, string lastReport)
    {
        var wanted = Read(task);
        if (wanted.Paths.Count == 0 && wanted.Terms.Count == 0)
        {
            return 0;
        }

        // The answer names files as often as the task does ("I changed SubAgentView.axaml"), and a file the candidate named
        // there is exactly as much evidence of the same work as one in its task.
        var known = Read(lastTask);
        var report = Read(lastReport);
        foreach (var path in report.Paths)
        {
            known.Paths.Add(path);
        }

        foreach (var term in report.Terms)
        {
            known.Terms.Add(term);
        }

        var shared = 0;
        foreach (var path in wanted.Paths)
        {
            if (known.Paths.Contains(path))
            {
                shared += PathWeight;
            }
        }

        foreach (var term in wanted.Terms)
        {
            if (known.Terms.Contains(term))
            {
                shared++;
            }
        }

        return shared;
    }

    /// <summary>The last thing the candidate was asked and the last thing it answered: the exchange a new task would continue.</summary>
    internal static (string LastTask, string LastReport) ReadLastExchange(IReadOnlyList<ChatMessage> history)
    {
        var lastTask = string.Empty;
        var lastReport = string.Empty;
        for (var index = history.Count - 1; index >= 0 && (lastTask.Length == 0 || lastReport.Length == 0); index--)
        {
            var message = history[index];
            if (message.Role == MessageRole.Assistant && lastReport.Length == 0)
            {
                lastReport = TextOf(message);
            }
            else if (message.Role == MessageRole.User && lastTask.Length == 0)
            {
                lastTask = TextOf(message);
            }
        }

        return (lastTask, lastReport);
    }

    private static string TextOf(ChatMessage message)
    {
        string? text = null;
        foreach (var block in message.Content)
        {
            if (block is TextBlock { Text.Length: > 0 } content)
            {
                text = text is null ? content.Text : text + "\n" + content.Text;
            }
        }

        return text ?? string.Empty;
    }

    /// <summary>
    /// The words of <paramref name="text"/> that can name a topic: a path reduced to its file name, and a bare identifier.
    /// The scan is hand-rolled rather than a regex because it runs for every candidate of every spawn and allocates only
    /// for the words it keeps.
    /// </summary>
    internal static (HashSet<string> Paths, HashSet<string> Terms) Read(string text)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (text.Length == 0)
        {
            return (paths, terms);
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
                Add(text.AsSpan(index - length, length), paths, terms);
                length = 0;
            }
        }

        return (paths, terms);

        static void Add(ReadOnlySpan<char> raw, HashSet<string> paths, HashSet<string> terms)
        {
            // A path is what has a separator or a dotted extension; everything else that is long enough is an identifier.
            var separator = raw.LastIndexOfAny('/', '\\');
            var trimmed = raw.Trim('/').Trim('\\').Trim('-').Trim('.');
            if (trimmed.Length < 3)
            {
                return;
            }

            if (separator >= 0 || IsDotted(trimmed))
            {
                var name = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
                if (name.Length >= 3)
                {
                    paths.Add(name.ToString().ToLowerInvariant());
                }

                return;
            }

            var term = trimmed.ToString().ToLowerInvariant();
            if (!Stopwords.Contains(term))
            {
                terms.Add(term);
            }
        }

        static bool IsDotted(ReadOnlySpan<char> word)
        {
            var dot = word.LastIndexOf('.');
            return dot > 0 && word.Length - dot > 1;
        }
    }
}
