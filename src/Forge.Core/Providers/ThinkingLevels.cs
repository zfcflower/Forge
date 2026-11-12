using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Which reasoning-effort steps a model accepts. The steps are a per-model fact: the reasoning dial shows only these, and a
/// stored effort that a model does not accept is clamped to the nearest accepted step. <see cref="ThinkingEffort.Off"/> is
/// always accepted.
/// <para>Lookup mirrors <see cref="ModelIdRules.Normalize"/> because providers spell the same model differently: the id is
/// trimmed, lower-cased and stripped of an OpenRouter-style <c>vendor/</c> prefix, and an exact entry wins. The one other
/// accepted spelling is a snapshot of a known id: the longest known id the model id starts with, whose remainder is exactly
/// <c>-yyyyMMdd</c>, that optionally followed by <c>-v&lt;digits&gt;</c>, or <c>-latest</c> — which is how Anthropic's
/// <c>claude-sonnet-4-5-20250929</c> resolves to <c>claude-sonnet-4-5</c>. Every other id — including <c>null</c>, empty, a
/// version bump such as <c>gpt-5.7</c> and anything else unknown — reports <see cref="ThinkingEffort.Off"/> alone rather
/// than guessing, so an unknown model never offers a step it may reject.</para>
/// </summary>
public static class ThinkingLevels
{
    /// <summary>Off alone: a model with no reasoning, or one the snapshot below does not know.</summary>
    private static readonly IReadOnlyList<ThinkingEffort> OffOnly = [ThinkingEffort.Off];

    /// <summary>Off and High, which is all a model with a single thinking tier offers.</summary>
    private static readonly IReadOnlyList<ThinkingEffort> OffHigh = [ThinkingEffort.Off, ThinkingEffort.High];

    private static readonly IReadOnlyList<ThinkingEffort> OffHighMax =
        [ThinkingEffort.Off, ThinkingEffort.High, ThinkingEffort.Max];

    private static readonly IReadOnlyList<ThinkingEffort> OffMinimalHigh =
        [ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.High];

    /// <summary>Pi's default for a reasoning model that carries no <c>thinkingLevelMap</c>: the four middle steps.</summary>
    private static readonly IReadOnlyList<ThinkingEffort> Standard =
        [ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High];

    private static readonly IReadOnlyList<ThinkingEffort> StandardXHigh =
    [
        ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High,
        ThinkingEffort.XHigh,
    ];

    private static readonly IReadOnlyList<ThinkingEffort> StandardMax =
    [
        ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High,
        ThinkingEffort.Max,
    ];

    private static readonly IReadOnlyList<ThinkingEffort> Everything =
    [
        ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High,
        ThinkingEffort.XHigh, ThinkingEffort.Max,
    ];

    private static readonly IReadOnlyList<ThinkingEffort> NoMinimal =
        [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High];

    private static readonly IReadOnlyList<ThinkingEffort> NoMinimalMax =
        [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High, ThinkingEffort.Max];

    private static readonly IReadOnlyList<ThinkingEffort> NoMinimalXHigh =
        [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High, ThinkingEffort.XHigh];

    private static readonly IReadOnlyList<ThinkingEffort> NoMinimalEverything =
    [
        ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High, ThinkingEffort.XHigh,
        ThinkingEffort.Max,
    ];

    private static readonly IReadOnlyList<ThinkingEffort> OffLowHighMax =
        [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High, ThinkingEffort.Max];

    private static readonly IReadOnlyList<ThinkingEffort> OffLowMediumXHigh =
        [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.XHigh];

    private static readonly IReadOnlyList<ThinkingEffort> OffMediumHighXHigh =
        [ThinkingEffort.Off, ThinkingEffort.Medium, ThinkingEffort.High, ThinkingEffort.XHigh];

    /// <summary>
    /// Snapshot of the Pi model catalogue's <c>thinkingLevelMap</c> tables, taken from the bundled runtime
    /// (<c>…/ai.thinkrail.app/stable/app/Resources/app/runtime/server-runtime.ts</c>) rather than from provider docs: Pi
    /// derives the steps it offers from the same map, so a model that lists a step here is a model whose provider accepts
    /// it. A map value of <c>null</c> drops that step and a string keeps it; an omitted
    /// <c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c> keeps it and an omitted <c>xhigh</c>/<c>max</c> drops it, and a
    /// reasoning model with no map at all offers
    /// <see cref="Standard"/>. <see cref="ThinkingEffort.Off"/> is listed whatever the map says, because the dial always
    /// needs a way to stop thinking. Where providers disagree about one id the first-party provider wins (anthropic,
    /// openai, google, xai, zai, moonshotai, deepseek), otherwise the more canonical provider. The ids are stored already
    /// normalized, so the lookup can compare them ordinally.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyList<ThinkingEffort>> Models = BuildModels();

    /// <summary>
    /// The exact ids above, longest first so the most specific one wins when a snapshot extends it. This is not a general
    /// prefix list: a match only counts when the remainder is a snapshot suffix (see <see cref="IsSnapshotSuffix"/>), so
    /// <c>gpt-5.7</c> still keeps <see cref="OffOnly"/> even though <c>gpt-5</c> is known.
    /// </summary>
    private static readonly (string Id, IReadOnlyList<ThinkingEffort> Steps)[] SnapshotBases = BuildSnapshotBases();

    /// <summary>The accepted steps for one model id, in ascending order; always starts with <see cref="ThinkingEffort.Off"/>.</summary>
    public static IReadOnlyList<ThinkingEffort> For(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return OffOnly;
        }

        var normalized = Normalize(modelId);
        if (normalized.Length == 0)
        {
            return OffOnly;
        }

        if (Models.TryGetValue(normalized, out var exact))
        {
            return exact;
        }

        foreach (var known in SnapshotBases)
        {
            if (normalized.StartsWith(known.Id, StringComparison.Ordinal)
                && IsSnapshotSuffix(normalized.AsSpan(known.Id.Length)))
            {
                return known.Steps;
            }
        }

        // A model the snapshot does not know gets Off alone: guessing the steps from the vendor family would offer a step
        // the model may reject, which is exactly what the dial must never do.
        return OffOnly;
    }

    /// <summary>
    /// True for the only suffixes a known id may carry: a dated snapshot (<c>-20250929</c>), a dated snapshot plus a
    /// release (<c>-20250929-v1</c>), or <c>-latest</c>. Anything else — a version bump such as <c>.7</c>, extra words or a
    /// shorter date — names a different model, not a snapshot of a known one, so it must not inherit the known id's steps.
    /// </summary>
    private static bool IsSnapshotSuffix(ReadOnlySpan<char> remainder)
    {
        if (remainder.Equals("-latest", StringComparison.Ordinal))
        {
            return true;
        }

        // "-yyyyMMdd": exactly eight digits, because a shorter or longer run is a release number, not a date.
        if (remainder.Length < 9 || remainder[0] != '-')
        {
            return false;
        }

        var index = 1;
        while (index < remainder.Length && char.IsAsciiDigit(remainder[index]))
        {
            index++;
        }

        if (index != 9)
        {
            return false;
        }

        if (index == remainder.Length)
        {
            return true;
        }

        // Optional "-v<digits>": a dated snapshot re-released by the vendor.
        if (remainder[index] != '-' || index + 2 >= remainder.Length || remainder[index + 1] != 'v')
        {
            return false;
        }

        index += 2;
        var versionStart = index;
        while (index < remainder.Length && char.IsAsciiDigit(remainder[index]))
        {
            index++;
        }

        return index > versionStart && index == remainder.Length;
    }

    /// <summary>
    /// Trims, lower-cases and drops an OpenRouter-style <c>vendor/</c> prefix, so <c>moonshotai-cn/kimi-k3</c> and
    /// <c>kimi-k3</c> are the same model.
    /// </summary>
    private static string Normalize(string modelId)
    {
        var trimmed = modelId.AsSpan().Trim();
        var slash = trimmed.LastIndexOf('/');
        if (slash >= 0 && slash < trimmed.Length - 1)
        {
            trimmed = trimmed[(slash + 1)..];
        }

        return trimmed.ToString().ToLowerInvariant();
    }

    private static Dictionary<string, IReadOnlyList<ThinkingEffort>> BuildModels()
    {
        var table = new Dictionary<string, IReadOnlyList<ThinkingEffort>>(StringComparer.Ordinal);

        // Standard
        Add(table, Standard, "claude-haiku-4-5", "claude-haiku-4.5", "claude-opus-4-5", "claude-opus-4.1",
            "claude-opus-4.5", "claude-sonnet-4-5", "claude-sonnet-4.5", "deepseek-chat-v3.1", "deepseek-r1",
            "deepseek-r1-0528", "deepseek-v3.1", "deepseek-v3.1-terminus", "deepseek-v3.2", "deepseek-v3.2-exp",
            "deepseek-v4-flash", "deepseek.v3.2", "gemini-2.5-computer-use-preview-10-2025", "gemini-2.5-flash",
            "gemini-2.5-flash-lite", "gemini-2.5-pro", "gemini-2.5-pro-preview", "gemini-3-flash",
            "gemini-3-flash-preview", "gemini-3-pro-image", "gemini-3.1-flash-lite", "gemini-3.1-flash-lite-preview",
            "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-3.6-flash", "gemini-flash-latest",
            "gemini-flash-lite-latest", "gemini-robotics-er-2-preview", "glm-4.5", "glm-4.5-air", "glm-4.5v",
            "glm-4.6", "glm-4.6v", "glm-4.6v-flash", "glm-4.7", "glm-4.7-flash", "glm-4.7-flashx", "glm-5",
            "glm-5-turbo", "glm-5.1", "glm-5v-turbo", "gpt-5", "gpt-5-mini", "gpt-5-nano", "gpt-oss-safeguard-120b",
            "gpt-oss-safeguard-20b", "grok-4.20", "grok-4.20-multi-agent", "grok-4.20-multi-agent-beta",
            "grok-4.20-reasoning", "grok-4.20-reasoning-beta", "grok-build-0.1", "grok-voice-3.0", "kimi-k2.5",
            "kimi-k2.6", "kimi-k2.7-code", "llama-3.1-nemotron-ultra-253b-v1", "qwen-3-14b", "qwen-3-30b",
            "qwen-3-32b", "qwen3-14b", "qwen3-235b-a22b", "qwen3-30b-a3b", "qwen3-30b-a3b-fp8", "qwen3-32b",
            "qwen3.5-122b-a10b", "qwen3.5-27b", "qwen3.5-35b-a3b", "qwen3.5-397b-a17b", "qwen3.5-9b", "qwen3.5-flash",
            "qwen3.5-flash-02-23", "qwen3.5-plus", "qwen3.5-plus-02-15", "qwen3.6-27b", "qwen3.6-35b-a3b",
            "qwen3.6-flash", "qwen3.6-plus", "qwen3.7-flash", "qwen3.7-max", "qwen3.7-plus", "qwen3.8-flash",
            "qwen3.8-omni-flash");

        // OffOnly
        Add(table, OffOnly, "deepseek-chat", "deepseek-chat-v3-0324", "deepseek-v3", "deepseek-v3-0324",
            "gemma-3-12b-it", "gemma-3-27b-it", "gemma-3-4b-it", "gpt-3.5-turbo-0613", "gpt-3.5-turbo-16k", "gpt-4",
            "gpt-4-turbo", "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-4o", "gpt-4o-mini", "gpt-5-chat-latest",
            "gpt-5.2-chat", "gpt-audio", "gpt-audio-mini", "gpt-chat-latest", "grok-4.20-non-reasoning",
            "grok-4.20-non-reasoning-beta", "kimi-k2", "kimi-k2-0905", "kimi-k2-instruct", "kimi-k2-instruct-0905",
            "llama-3.1-70b", "llama-3.1-70b-instruct", "llama-3.1-8b", "llama-3.1-8b-instant", "llama-3.1-8b-instruct",
            "llama-3.1-nemotron-70b-instruct", "llama-3.2-11b-vision-instruct", "llama-3.2-90b-vision-instruct",
            "llama-3.3-70b", "llama-3.3-70b-instruct", "llama-3.3-70b-versatile", "llama-4-maverick", "llama-4-scout",
            "llama-4-scout-17b-16e-instruct", "qwen-2.5-72b-instruct", "qwen-2.5-7b-instruct", "qwen-3-235b",
            "qwen-plus", "qwen.qwen3-coder-next", "qwen.qwen3-next-80b-a3b", "qwen.qwen3-vl-235b-a22b",
            "qwen2.5-coder-32b-instruct", "qwen3-235b-a22b-2507", "qwen3-235b-a22b-instruct-2507",
            "qwen3-30b-a3b-instruct-2507", "qwen3-coder", "qwen3-coder-30b-a3b", "qwen3-coder-30b-a3b-instruct",
            "qwen3-coder-480b-a35b-instruct", "qwen3-coder-flash", "qwen3-coder-next", "qwen3-next-80b-a3b-instruct",
            "qwen3-vl-235b-a22b-instruct", "qwen3-vl-30b-a3b-instruct", "qwen3-vl-8b-instruct", "qwen3-vl-instruct");

        // NoMinimalEverything
        Add(table, NoMinimalEverything, "claude-fable-latest", "claude-haiku-5-5", "claude-haiku-latest",
            "claude-opus-4.7", "claude-opus-5-5", "claude-opus-5.5", "claude-opus-latest", "claude-sonnet-5-5",
            "claude-sonnet-latest", "gpt-5.6-cyber", "gpt-5.6-luna", "gpt-5.6-luna-pro", "gpt-5.6-sol",
            "gpt-5.6-sol-pro", "gpt-5.6-terra", "gpt-5.6-terra-pro", "gpt-6-astra", "gpt-6-astra-pro", "gpt-6-luna",
            "gpt-6-luna-pro", "gpt-6-sol", "gpt-6-sol-pro", "gpt-6.1-sol", "gpt-6.1-sol-pro", "gpt-6.1-terra",
            "gpt-6.1-terra-pro", "gpt-astra-latest", "gpt-daybreak-blue-latest", "gpt-daybreak-red-latest",
            "gpt-luna-latest", "gpt-sol-latest", "gpt-terra-latest");

        // OffLowHighMax
        Add(table, OffLowHighMax, "deepseek-flash", "deepseek-flash-latest", "deepseek-pro-latest",
            "deepseek-v4-1-flash-1017", "deepseek-v4-flash-latest", "deepseek-v4-flash-vision-exp", "deepseek-v4.1-flash",
            "deepseek-v4.1-flash-1017", "deepseek-v4p1-flash", "deepseek-v4p1-flash-1017", "glm-5.3", "glm-5.3-flash",
            "glm-5.3-flashx", "glm-5p3", "glm-5p3-flash", "glm-flash-latest", "glm-latest", "k3", "k3-256k", "k3.1-pro",
            "kimi-for-coding", "kimi-k3", "kimi-k3.1-pro");

        // NoMinimal
        Add(table, NoMinimal, "gemini-3.1-pro", "gemini-3.1-pro-preview", "gemini-3.1-pro-preview-customtools",
            "gemini-3.7-flash", "gemini-3.8-flash", "gemini-4-argon", "gemini-pro-latest", "gpt-5-codex", "gpt-5.1",
            "gpt-5.1-codex", "gpt-5.1-codex-mini", "gpt-oss-120b", "gpt-oss-20b", "o1", "o1-pro", "o3", "o3-mini",
            "o3-pro", "o4-mini", "qwen-3.8-27b", "qwen3-8b");

        // NoMinimalXHigh
        Add(table, NoMinimalXHigh, "gpt-5.1-codex-max", "gpt-5.2", "gpt-5.2-codex", "gpt-5.3-codex",
            "gpt-5.3-codex-spark", "gpt-5.4", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.5", "gpt-mini-latest", "grok-4.3",
            "grok-4.5", "grok-4.6", "grok-4.7", "grok-latest");

        // Everything
        Add(table, Everything, "claude-fable-5", "claude-fable-5-1", "claude-fable-5.1", "claude-haiku-5.5",
            "claude-opus-4-7", "claude-opus-4-8", "claude-opus-4.8", "claude-opus-5", "claude-sonnet-5",
            "claude-sonnet-5.5");

        // OffLowMediumXHigh
        Add(table, OffLowMediumXHigh, "qwen-max-latest", "qwen3.8-2.4t-a95b", "qwen3.8-27b", "qwen3.8-max",
            "qwen3p8-2p4t-a95b", "qwen3p8-max");

        // OffHighMax
        Add(table, OffHighMax, "deepseek-v4-flash-0731", "deepseek-v4-pro", "deepseek-v4-pro-0813", "glm-5-2",
            "glm-5.2");

        // OffMediumHighXHigh
        Add(table, OffMediumHighXHigh, "gpt-5.2-pro", "gpt-5.4-pro", "gpt-5.5-pro");

        // OffMinimalHigh
        Add(table, OffMinimalHigh, "gemini-nano-banana-2.1", "gemma-4-26b-a4b-it", "gemma-4-31b-it");

        // StandardMax
        Add(table, StandardMax, "claude-opus-4-6", "claude-sonnet-4-6", "claude-sonnet-4.6");

        // NoMinimalMax
        Add(table, NoMinimalMax, "claude-opus-4.6", "kimi-latest");

        // OffHigh
        Add(table, OffHigh, "gpt-5-pro");

        // StandardXHigh
        Add(table, StandardXHigh, "qwen3.8-max-0902");

        return table;
    }

    private static void Add(
        Dictionary<string, IReadOnlyList<ThinkingEffort>> table,
        IReadOnlyList<ThinkingEffort> steps,
        params string[] modelIds)
    {
        foreach (var modelId in modelIds)
        {
            table[modelId] = steps;
        }
    }

    private static (string Id, IReadOnlyList<ThinkingEffort> Steps)[] BuildSnapshotBases()
    {
        // Only ids the table actually lists appear here, never a bare vendor name, so "gpt-whatever-unknown" stays Off.
        var bases = new (string Id, IReadOnlyList<ThinkingEffort> Steps)[Models.Count];
        var index = 0;
        foreach (var entry in Models)
        {
            bases[index++] = (entry.Key, entry.Value);
        }

        // Longest first: the lookup walks this order, so the most specific base id always wins.
        Array.Sort(bases, static (left, right) => right.Id.Length.CompareTo(left.Id.Length));
        return bases;
    }
}
