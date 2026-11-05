namespace Forge.ExtensionHost;

/// <summary>
/// What the main program currently knows about one plugin's published snapshot, plus the serialization that keeps that
/// plugin's notifications in order.
/// <para>
/// The host publishes with <c>tools.register</c> and <c>contributions.register</c>, and the main program treats each as a
/// whole replacement. Two invariants make that safe. <see cref="Gate"/> serializes the notifications of one plugin, so a
/// later snapshot can never be overtaken by an earlier one whose send completes late. And a payload is recorded in
/// <see cref="Tools"/>/<see cref="Contributions"/> only after its frame was written and flushed to the pipe, so a failed or
/// cancelled notification is not remembered as sent - the next load, refresh or execution sends it again instead of the
/// metadata being suppressed for the rest of the host's life. This is transport completion, not an application-level
/// acknowledgement: the host never learns that the main program processed a frame.
/// </para>
/// <para>
/// <see cref="Generation"/> is the generation of the load the main program's copy came from (see
/// <see cref="PluginState.NextLoadGeneration"/>). A publication from an older load than the delivered one is dropped, so a
/// late notification cannot overwrite newer state; every load allocates a new number, and an unregistration takes the
/// recorded number up to the load the plugin is running now, which fences off exactly the publications computed before it.
/// </para>
/// </summary>
internal sealed class Publication
{
    /// <summary>Serializes this plugin's registration and unregistration notifications.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Last tool payload whose frame was written and flushed, or <c>null</c> when the main program has none. It is
    /// also the pending-unregister tombstone: an unregister clears it only after its own frame was flushed.</summary>
    public string? Tools { get; set; }

    /// <summary>Last contribution payload whose frame was written and flushed, or <c>null</c> when the main program has none.
    /// It is also the pending-unregister tombstone: an unregister clears it only after its own frame was flushed.</summary>
    public string? Contributions { get; set; }

    /// <summary>Generation of the snapshot the main program has, or <see cref="long.MinValue"/> before the first one.</summary>
    public long Generation { get; set; } = long.MinValue;
}
