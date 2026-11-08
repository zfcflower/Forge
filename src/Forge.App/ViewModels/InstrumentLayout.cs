namespace Forge.App.ViewModels;

/// <summary>
/// Which parts of the composer's instrument strip still fit at a given width.
/// <para>A pure function of the width, so the rule is testable without a visual tree: the view measures once per size change
/// and applies the flag, and never decides priority itself. Only the git markers (ahead/behind, CI) give way; the resources
/// button, the sub-agent toggle, the workspace button and the token readout never do — they are the strip's whole purpose.</para>
/// </summary>
internal readonly record struct InstrumentLayout(bool ShowGit)
{
    /// <summary>Below this the ahead/behind and CI markers go.</summary>
    public const double GitMinWidth = 640;

    public static InstrumentLayout For(double width) => new(ShowGit: width >= GitMinWidth);
}
