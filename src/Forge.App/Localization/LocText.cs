namespace Forge.App.Localization;

/// <summary>
/// A text that is resolved in the current UI language when it is shown, not when it is created: a <see cref="Loc"/> key
/// plus its format arguments. Anything stored now and displayed later (startup diagnostics, provider status, session
/// notices, turn footnotes) is kept as a <see cref="LocText"/> so a language switch re-renders it.
/// <para>Arguments are formatted by <see cref="Loc.Format"/> with the invariant culture. An argument that is itself a
/// <see cref="LocText"/> is resolved at the same time (through <see cref="ToString"/>), so nested texts follow the
/// language too. Pass numbers pre-formatted with <see cref="TextFormat.Number(long)"/> so grouping stays uniform.</para>
/// <para><see cref="Literal"/> wraps text that must not be translated (an exception message from the OS, a path).</para>
/// </summary>
internal readonly struct LocText : IEquatable<LocText>
{
    private readonly string? key;
    private readonly string? literal;
    private readonly object?[]? args;

    public LocText(string key)
        : this(key, null)
    {
    }

    public LocText(string key, params object?[]? args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        this.key = key;
        this.args = args is { Length: > 0 } ? args : null;
        literal = null;
    }

    private LocText(string literal, bool _)
    {
        key = null;
        args = null;
        this.literal = literal;
    }

    /// <summary>The <see cref="Loc"/> key; <see langword="null"/> for a literal or an empty value.</summary>
    public string? Key => key;

    public ReadOnlySpan<object?> Args => args;

    /// <summary>True for <c>default(LocText)</c>: no key and no literal.</summary>
    public bool IsEmpty => key is null && literal is null;

    /// <summary>Untranslated text shown as is.</summary>
    public static LocText Literal(string text) => new(text ?? string.Empty, false);

    /// <summary>
    /// The key of a count-dependent text: <c>{keyBase}.one</c> for exactly one, <c>{keyBase}.other</c> otherwise. Both keys
    /// are registered by the owning table (the Chinese texts are usually the same).
    /// </summary>
    public static string PluralKey(string keyBase, long count) => keyBase + (count == 1 ? ".one" : ".other");

    /// <summary>A count-dependent text; see <see cref="PluralKey"/>.</summary>
    public static LocText Plural(string keyBase, long count, params object?[]? args) => new(PluralKey(keyBase, count), args);

    /// <summary>The text in the current language; empty for <see cref="IsEmpty"/>.</summary>
    public string Resolve()
    {
        if (key is null)
        {
            return literal ?? string.Empty;
        }

        return args is null ? Loc.Instance[key] : Loc.Instance.Format(key, args);
    }

    public override string ToString() => Resolve();

    public bool Equals(LocText other)
    {
        if (!string.Equals(key, other.key, StringComparison.Ordinal) ||
            !string.Equals(literal, other.literal, StringComparison.Ordinal))
        {
            return false;
        }

        var left = Args;
        var right = other.Args;
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is LocText other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(key, literal, args?.Length ?? 0);

    public static bool operator ==(LocText left, LocText right) => left.Equals(right);

    public static bool operator !=(LocText left, LocText right) => !left.Equals(right);
}
