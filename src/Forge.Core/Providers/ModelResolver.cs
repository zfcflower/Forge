using System.Text;

namespace Forge.Core.Providers;

/// <summary>One connected provider as the resolver sees it: its id, the name the user sees, and the model ids it lists.</summary>
public sealed record ProviderModels(string ProviderId, string DisplayName, IReadOnlyList<string> Models);

/// <summary>A model one provider lists, in that provider's own spelling: what a caller must store to run on it.</summary>
public sealed record ResolvedModel(string ProviderId, string Model);

/// <summary>How <see cref="ModelResolver.Resolve"/> ended: one model, several candidates, no model, or no provider.</summary>
public enum ModelResolutionKind
{
    /// <summary>Exactly one model matched: <see cref="ModelResolution.Match"/> carries it.</summary>
    Unique,

    /// <summary>More than one model matched: <see cref="ModelResolution.Candidates"/> carries all of them.</summary>
    Ambiguous,

    /// <summary>No model matched; nothing is guessed.</summary>
    UnknownModel,

    /// <summary>The provider filter named no connected provider, so no model was looked up.</summary>
    UnknownProvider,
}

/// <summary>
/// The outcome of matching user text against the connected models. It never guesses: an ambiguous text comes back as
/// every candidate, which a caller renders so the model (or the user) can pick one.
/// </summary>
public sealed record ModelResolution
{
    private ModelResolution(ModelResolutionKind kind, ResolvedModel? match, IReadOnlyList<ResolvedModel> candidates)
    {
        Kind = kind;
        Match = match;
        Candidates = candidates;
    }

    public ModelResolutionKind Kind { get; }

    /// <summary>The one model, when <see cref="Kind"/> is <see cref="ModelResolutionKind.Unique"/>; otherwise null.</summary>
    public ResolvedModel? Match { get; }

    /// <summary>Every match, in provider order then the provider's own list order; empty unless ambiguous.</summary>
    public IReadOnlyList<ResolvedModel> Candidates { get; }

    internal static ModelResolution Unique(ResolvedModel match) => new(ModelResolutionKind.Unique, match, []);

    internal static ModelResolution Ambiguous(IReadOnlyList<ResolvedModel> candidates) =>
        new(ModelResolutionKind.Ambiguous, null, candidates);

    internal static ModelResolution UnknownModel { get; } =
        new(ModelResolutionKind.UnknownModel, null, []);

    internal static ModelResolution UnknownProvider { get; } =
        new(ModelResolutionKind.UnknownProvider, null, []);
}

/// <summary>
/// Matches the model a user names against the models the connected providers actually list, so a per-spawn choice is
/// stored exactly as the provider spells it. Matching runs in passes, and a pass that yields more than one model stops
/// the search and reports all of them: choosing one would be a guess, and a wrong id is a config that only fails later.
/// <list type="number">
/// <item>the id spelled exactly as one provider lists it, case-sensitive;</item>
/// <item><see cref="ModelIdRules.Normalize"/> equality — case, an OpenRouter-style <c>vendor/</c> prefix, a trailing
/// <c>:free</c> and a trailing <c>-latest</c> are ignored;</item>
/// <item>a snapshot of a listed base (<c>claude-sonnet-4-5-20250929</c> → <c>claude-sonnet-4-5</c>) or a listed snapshot
/// of a base the user named (<c>claude-sonnet-4-5</c> → <c>claude-sonnet-4-5-20250929</c>); only a real snapshot suffix
/// counts, so a version bump names a different model;</item>
/// <item>a listed id that contains the text, or a listed id the text contains (<c>opus</c> → <c>claude-opus-4-5</c>).</item>
/// </list>
/// The provider filter (<see cref="MatchProviders"/>) narrows the list first, so a model two providers serve can be
/// named unambiguously. The data is the connected providers' own model lists — a per-spawn choice can only name a model
/// that is really on offer.
/// </summary>
public static class ModelResolver
{
    /// <summary>
    /// Resolves <paramref name="text"/> against <paramref name="providers"/>, optionally narrowed to the providers
    /// <paramref name="provider"/> names (id or display name; see <see cref="MatchProviders"/>).
    /// </summary>
    public static ModelResolution Resolve(string? text, IReadOnlyList<ProviderModels> providers, string? provider = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var scope = MatchProviders(providers, provider);
        if (scope.Count == 0 && !string.IsNullOrWhiteSpace(provider))
        {
            return ModelResolution.UnknownProvider;
        }

        var needle = text?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            return ModelResolution.UnknownModel;
        }

        var seen = new HashSet<(string ProviderId, string Model)>();
        var exact = new List<ResolvedModel>();
        foreach (var candidate in scope)
        {
            foreach (var model in candidate.Models)
            {
                if (string.Equals(model, needle, StringComparison.Ordinal) &&
                    seen.Add((candidate.ProviderId, model)))
                {
                    exact.Add(new ResolvedModel(candidate.ProviderId, model));
                }
            }
        }

        if (exact.Count > 0)
        {
            return exact.Count == 1 ? ModelResolution.Unique(exact[0]) : ModelResolution.Ambiguous(exact);
        }

        var normalized = ModelIdRules.Normalize(needle);
        if (normalized.Length == 0)
        {
            return ModelResolution.UnknownModel;
        }

        var same = Collect(scope, seen, model => string.Equals(ModelIdRules.Normalize(model), normalized, StringComparison.Ordinal));
        if (same.Count > 0)
        {
            return Settle(same);
        }

        var snapshot = Collect(
            scope,
            seen,
            model =>
            {
                var listed = ModelIdRules.Normalize(model);
                return listed.Length > 0 &&
                       (IsSnapshotOf(normalized, listed) || IsSnapshotOf(listed, normalized));
            });
        if (snapshot.Count > 0)
        {
            return Settle(snapshot);
        }

        var contains = Collect(
            scope,
            seen,
            model =>
            {
                var listed = ModelIdRules.Normalize(model);
                return listed.Length > 0 &&
                       (listed.Contains(normalized, StringComparison.Ordinal) ||
                        normalized.Contains(listed, StringComparison.Ordinal));
            });
        return contains.Count > 0 ? Settle(contains) : ModelResolution.UnknownModel;
    }

    /// <summary>
    /// The listed models closest to a text that matched nothing: shared alphanumeric tokens (<c>luna-6.1</c> →
    /// <c>gpt-6-luna</c>), ranked by how many they share and then by how close the spelling is. A suggestion is never a
    /// match — the caller offers it so the next attempt can use the real id instead of guessing four more times.
    /// </summary>
    public static IReadOnlyList<ResolvedModel> Suggest(string? text, IReadOnlyList<ProviderModels> providers, int max = 5)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var needle = Tokens(text);
        if (needle.Count == 0 || max <= 0)
        {
            return [];
        }

        var typed = text!.Trim().Length;
        var ranked = new List<(int Shared, int Distance, ResolvedModel Model)>();
        foreach (var provider in providers)
        {
            foreach (var model in provider.Models)
            {
                var listed = Tokens(model);
                var shared = 0;
                foreach (var token in needle)
                {
                    if (listed.Contains(token))
                    {
                        shared++;
                    }
                }

                if (shared > 0)
                {
                    ranked.Add((shared, Math.Abs(model.Length - typed), new ResolvedModel(provider.ProviderId, model)));
                }
            }
        }

        return ranked
            .OrderByDescending(static entry => entry.Shared)
            .ThenBy(static entry => entry.Distance)
            .ThenBy(static entry => entry.Model.Model, StringComparer.Ordinal)
            .Take(max)
            .Select(static entry => entry.Model)
            .ToArray();
    }

    /// <summary>The words of a model id: split on every non-alphanumeric and between letters and digits; single
    /// characters are dropped because every id shares them.</summary>
    private static HashSet<string> Tokens(string? id)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(id))
        {
            return tokens;
        }

        var builder = new StringBuilder(16);
        var digits = false;
        foreach (var character in id)
        {
            if (!char.IsLetterOrDigit(character))
            {
                Flush(tokens, builder);
                continue;
            }

            if (builder.Length > 0 && digits != char.IsDigit(character))
            {
                Flush(tokens, builder);
            }

            digits = char.IsDigit(character);
            builder.Append(character);
        }

        Flush(tokens, builder);
        return tokens;

        static void Flush(HashSet<string> into, StringBuilder from)
        {
            if (from.Length > 1)
            {
                into.Add(from.ToString());
            }

            from.Clear();
        }
    }

    /// <summary>
    /// The providers a filter names: an exact id first, then an exact display name, then a partial match on either.
    /// Null or blank returns every provider; an unmatched filter returns none, so the caller can say so instead of
    /// silently searching every provider.
    /// </summary>
    public static IReadOnlyList<ProviderModels> MatchProviders(IReadOnlyList<ProviderModels> providers, string? filter)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var needle = filter?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            return providers;
        }

        var byId = new List<ProviderModels>(1);
        var byName = new List<ProviderModels>(1);
        var partial = new List<ProviderModels>(1);
        foreach (var provider in providers)
        {
            if (string.Equals(provider.ProviderId, needle, StringComparison.OrdinalIgnoreCase))
            {
                byId.Add(provider);
            }
            else if (string.Equals(provider.DisplayName.Trim(), needle, StringComparison.OrdinalIgnoreCase))
            {
                byName.Add(provider);
            }
            else if (provider.ProviderId.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                     provider.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                partial.Add(provider);
            }
        }

        if (byId.Count > 0)
        {
            return byId;
        }

        return byName.Count > 0 ? byName : partial;
    }

    /// <summary>True when <paramref name="longer"/> is <paramref name="baseId"/> plus a snapshot suffix.</summary>
    private static bool IsSnapshotOf(string longer, string baseId) =>
        longer.Length > baseId.Length &&
        longer.StartsWith(baseId, StringComparison.Ordinal) &&
        ModelIdRules.IsSnapshotSuffix(longer.AsSpan(baseId.Length));

    private static List<ResolvedModel> Collect(
        IReadOnlyList<ProviderModels> scope,
        HashSet<(string ProviderId, string Model)> seen,
        Func<string, bool> matches)
    {
        var found = new List<ResolvedModel>();
        foreach (var provider in scope)
        {
            foreach (var model in provider.Models)
            {
                if (matches(model) && seen.Add((provider.ProviderId, model)))
                {
                    found.Add(new ResolvedModel(provider.ProviderId, model));
                }
            }
        }

        return found;
    }

    private static ModelResolution Settle(IReadOnlyList<ResolvedModel> candidates) =>
        candidates.Count == 1 ? ModelResolution.Unique(candidates[0]) : ModelResolution.Ambiguous(candidates);
}
