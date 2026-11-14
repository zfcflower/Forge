using Forge.Mcp;

namespace Forge.App.Agents;

/// <summary>
/// Which MCP tools one session offers the model. With no limit (<c>mcpToolSearchLimit</c> 0, the default) every ready tool is
/// offered, exactly as before the limit existed.
/// <para>With a limit, the offer is: the tools the model pinned through <c>mcp_tool_search</c>, then the best matches for the
/// current request. The rest stay reachable — the search tool is offered exactly while something is hidden, so a trimmed surface
/// can never make a server's tool unreachable.</para>
/// <para>Scoring is the cheap kind on purpose: lowercased word membership over the exposed name, the tool name, the description
/// and the server name, once per (catalog, limit, request) combination. The result is cached by catalog reference, so a turn that
/// sends no new request text pays nothing, and no state lives per tool.</para>
/// <para>Callers pass the manager's current snapshot; this class never locks, never awaits, and never allocates per tool beyond
/// the arrays it returns.</para>
/// </summary>
internal sealed class McpToolExposure
{
    /// <summary>Words taken from one request; the tail of a long request rarely names a tool and would only add noise.</summary>
    private const int MaxFocusWords = 32;

    /// <summary>Pinned names kept before the oldest are dropped; far above any limit, so eviction is a leak guard, not a policy.</summary>
    private const int MaxPinned = 200;

    /// <summary>Words too common to say anything about which tool a request needs.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "you", "are", "not", "from", "have", "use", "was", "were", "can",
        "will", "into", "then", "than", "out", "all", "any", "how", "what", "when", "why", "our", "your", "its",
    };

    private readonly Dictionary<string, long> pinned = new(StringComparer.Ordinal);
    private readonly Lock sync = new();
    private long sequence;

    // One-entry cache of the last selection: the catalog snapshot is replaced whenever a server's tools change, and the request
    // text is the same for every request inside one turn.
    private IReadOnlyList<McpToolAdapter>? cachedCatalog;
    private McpToolAdapter[] cachedSelected = [];
    private string? cachedFocus;
    private int cachedLimit;
    private long cachedVersion = -1;
    private bool cachedTruncated;

    /// <summary>How many tools are pinned right now; the settings page and tests read it.</summary>
    public int PinnedCount
    {
        get
        {
            lock (sync)
            {
                return pinned.Count;
            }
        }
    }

    /// <summary>
    /// The tools of <paramref name="catalog"/> this session should offer for <paramref name="focus"/>, in catalog order (a stable
    /// order keeps the prompt cache warm while the set itself does not change).
    /// <paramref name="truncated"/> is true when the limit hid at least one tool, which is when <c>mcp_tool_search</c> is offered.
    /// </summary>
    public IReadOnlyList<McpToolAdapter> Select(
        IReadOnlyList<McpToolAdapter> catalog,
        int limit,
        string? focus,
        out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (sync)
        {
            return SelectLocked(catalog, limit, focus, out truncated);
        }
    }

    private IReadOnlyList<McpToolAdapter> SelectLocked(
        IReadOnlyList<McpToolAdapter> catalog,
        int limit,
        string? focus,
        out bool truncated)
    {
        truncated = false;
        if (catalog.Count == 0)
        {
            return [];
        }

        if (limit <= 0 || catalog.Count <= limit)
        {
            // Everything fits: no scoring, no cache, no search tool. This is the default configuration.
            return catalog;
        }

        var version = sequence;
        if (ReferenceEquals(cachedCatalog, catalog) &&
            cachedLimit == limit &&
            cachedVersion == version &&
            string.Equals(cachedFocus, focus, StringComparison.Ordinal))
        {
            truncated = cachedTruncated;
            return cachedSelected;
        }

        var words = Words(focus);
        var chosen = new List<McpToolAdapter>(limit);
        var chosenNames = new HashSet<string>(limit, StringComparer.Ordinal);

        // The pinned tools, newest pin first: when there are more pins than the limit, the ones the conversation reached for last
        // are the ones it is still working with. One pass over the catalog, then a sort of the few that are pinned.
        if (pinned.Count > 0)
        {
            var pinnedHere = new List<McpToolAdapter>(Math.Min(pinned.Count, catalog.Count));
            foreach (var adapter in catalog)
            {
                if (pinned.ContainsKey(adapter.ExposedName))
                {
                    pinnedHere.Add(adapter);
                }
            }

            if (pinnedHere.Count > 1)
            {
                pinnedHere.Sort((left, right) =>
                    pinned[right.ExposedName].CompareTo(pinned[left.ExposedName]));
            }

            foreach (var adapter in pinnedHere)
            {
                if (chosen.Count >= limit)
                {
                    break;
                }

                if (chosenNames.Add(adapter.ExposedName))
                {
                    chosen.Add(adapter);
                }
            }
        }

        if (chosen.Count < limit && words.Count > 0)
        {
            var scored = new List<(McpToolAdapter Adapter, int Score)>(catalog.Count);
            foreach (var adapter in catalog)
            {
                if (chosenNames.Contains(adapter.ExposedName))
                {
                    continue;
                }

                var score = Score(adapter, words);
                if (score > 0)
                {
                    scored.Add((adapter, score));
                }
            }

            if (scored.Count > 0)
            {
                scored.Sort(static (left, right) =>
                {
                    var byScore = right.Score.CompareTo(left.Score);
                    return byScore != 0
                        ? byScore
                        : string.CompareOrdinal(left.Adapter.ExposedName, right.Adapter.ExposedName);
                });

                for (var i = 0; i < scored.Count && chosen.Count < limit; i++)
                {
                    if (chosenNames.Add(scored[i].Adapter.ExposedName))
                    {
                        chosen.Add(scored[i].Adapter);
                    }
                }
            }
        }

        // A request that matched nothing still has to offer something: the first tools in catalog order, so the surface is
        // predictable instead of empty.
        foreach (var adapter in catalog)
        {
            if (chosen.Count >= limit)
            {
                break;
            }

            if (chosenNames.Add(adapter.ExposedName))
            {
                chosen.Add(adapter);
            }
        }

        // Catalog order, not score order: an unchanged set then keeps its place in the prompt.
        chosen.Sort(static (left, right) => string.CompareOrdinal(left.ExposedName, right.ExposedName));
        var selected = chosen.ToArray();
        truncated = selected.Length < catalog.Count;
        cachedCatalog = catalog;
        cachedSelected = selected;
        cachedFocus = focus;
        cachedLimit = limit;
        cachedVersion = version;
        cachedTruncated = truncated;
        return selected;
    }

    /// <summary>
    /// The tools matching a search, pinned so the next request offers them. Pinning is what makes the answer true: the model is
    /// told the tools are callable, so they must be.
    /// </summary>
    public IReadOnlyList<McpToolAdapter> Search(IReadOnlyList<McpToolAdapter> catalog, string? query, int limit)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (sync)
        {
            return SearchLocked(catalog, query, limit);
        }
    }

    private IReadOnlyList<McpToolAdapter> SearchLocked(IReadOnlyList<McpToolAdapter> catalog, string? query, int limit)
    {
        var words = Words(query);
        if (words.Count == 0 || limit <= 0)
        {
            return [];
        }

        var scored = new List<(McpToolAdapter Adapter, int Score)>(Math.Min(catalog.Count, limit * 4));
        foreach (var adapter in catalog)
        {
            var score = Score(adapter, words);
            if (score > 0)
            {
                scored.Add((adapter, score));
            }
        }

        if (scored.Count == 0)
        {
            return [];
        }

        scored.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0
                ? byScore
                : string.CompareOrdinal(left.Adapter.ExposedName, right.Adapter.ExposedName);
        });

        var take = Math.Min(limit, scored.Count);
        var matches = new McpToolAdapter[take];
        for (var i = 0; i < take; i++)
        {
            matches[i] = scored[i].Adapter;
            Pin(matches[i].ExposedName);
        }

        cachedCatalog = null; // the pin set moved; the next Select must recompute
        return matches;
    }

    /// <summary>Drops every pin; the session keeps its limit and falls back to matching the request.</summary>
    public void ClearPins()
    {
        lock (sync)
        {
            pinned.Clear();
            sequence++;
            cachedCatalog = null;
        }
    }

    private void Pin(string exposedName)
    {
        sequence++;
        pinned[exposedName] = sequence;
        if (pinned.Count <= MaxPinned)
        {
            return;
        }

        var oldestName = (string?)null;
        var oldest = long.MaxValue;
        foreach (var pair in pinned)
        {
            if (pair.Value < oldest)
            {
                oldest = pair.Value;
                oldestName = pair.Key;
            }
        }

        if (oldestName is not null)
        {
            pinned.Remove(oldestName);
        }
    }

    private static int Score(McpToolAdapter adapter, IReadOnlyCollection<string> words)
    {
        var name = adapter.ExposedName;
        var toolName = adapter.ToolName;
        var server = adapter.ServerName;
        var description = adapter.Definition.Description ?? string.Empty;
        var score = 0;
        foreach (var word in words)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                // The exposed name is <server>__<tool>: a word in it is the strongest signal there is.
                score += 4;
                continue;
            }

            if (toolName.Contains(word, StringComparison.OrdinalIgnoreCase) || server.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
                continue;
            }

            if (description.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    /// <summary>The words a request contributes: lowercased, alphanumeric, at least three characters, no stop words.</summary>
    private static HashSet<string> Words(string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus))
        {
            return [];
        }

        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = -1;
        for (var index = 0; index <= focus.Length; index++)
        {
            var isWord = index < focus.Length && char.IsLetterOrDigit(focus[index]);
            if (isWord)
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start >= 0)
            {
                AddWord(words, focus.AsSpan(start, index - start));
                start = -1;
                if (words.Count >= MaxFocusWords)
                {
                    break;
                }
            }
        }

        return words;
    }

    private static void AddWord(HashSet<string> words, ReadOnlySpan<char> word)
    {
        if (word.Length < 3)
        {
            return;
        }

        var text = word.ToString().ToLowerInvariant();
        if (!StopWords.Contains(text))
        {
            words.Add(text);
        }
    }
}
