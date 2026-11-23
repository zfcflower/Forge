namespace Forge.Abstractions;

/// <summary>
/// Who named a session's title (<c>sessions.title_source</c>). NULL is not among these: it is the creation default, which is
/// exactly what "still unnamed" means, so every named title carries one of these and only the first two can still be
/// replaced (see <see cref="ISessionCatalog.TryTitleAsync"/>).
/// </summary>
public static class SessionTitleSource
{
    /// <summary>A placeholder the host derived from the session's first user message.</summary>
    public const string Derived = "derived";

    /// <summary>A name the model wrote through <c>set_title</c>.</summary>
    public const string Agent = "agent";

    /// <summary>A name a person typed in the rename dialog, or one a host deliberately pinned.</summary>
    public const string User = "user";
}
