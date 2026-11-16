namespace Forge.HostClient;

/// <summary>
/// One <c>plugin.problem</c> the Extension Host reported: something went wrong with a plugin that the user should hear
/// about even though the call that hit it already returned, or no call was waiting at all.
/// <para>
/// <see cref="Code"/> is the stable English token the host sends (<c>workflow-terminated</c>, <c>worker-disabled</c>), so a
/// caller maps the codes it knows to its own language and falls back to <see cref="Message"/> for the rest. <see
/// cref="Count"/> is whatever number the code quantifies - runs terminated, failures recorded - and zero when it
/// quantifies nothing.
/// </para>
/// </summary>
/// <param name="PluginId">The plugin the problem belongs to; never empty.</param>
/// <param name="Code">The stable English token, lower-case with dashes.</param>
/// <param name="Message">The host's own English sentence: the log line and the fallback body.</param>
/// <param name="Count">The number the code quantifies, or zero.</param>
public sealed record HostPluginProblem(string PluginId, string Code, string Message, int Count);
