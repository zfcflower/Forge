using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>The transcript's follow-the-reply button; keys are <c>transcript.*</c>.</summary>
internal static class TranscriptScrollStrings
{
    public const string FollowKey = "transcript.follow";

    public static string Follow => Loc.Instance[FollowKey];

    [ModuleInitializer]
    internal static void Initialize() => Register();

    internal static void Register() => Loc.Register(FollowKey, "跟随回复", "Follow replies");
}
