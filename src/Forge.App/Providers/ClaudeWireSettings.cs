using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// Reads the <c>claudeWire</c> section of <c>settings.json</c>: whether a Claude subscription request should be
/// indistinguishable from the official client's, and under which version, build and entrypoint.
/// </summary>
/// <remarks>
/// <para>
/// This is a hand-edited section with no settings UI on purpose: claiming the official client's identity is a wire-level
/// decision with account consequences, not a preference. It is read on every catalogue load, so an edit takes effect the
/// next time the provider catalogue is rebuilt, and a damaged value falls back to its documented default instead of
/// disabling the file — the same tolerance rule the other sections follow.
/// </para>
/// <para>
/// The claim is <b>on by default</b>: a subscription request is meant to be indistinguishable from the official client's, and
/// the section exists so that can be inspected and switched off. The switch is one thing only, <c>"enabled": true|false</c>;
/// everything else in the section only matters while the claim is on.
/// </para>
/// </remarks>
internal static partial class ClaudeWireSettings
{
    /// <summary>The section this reads, and the one a provider save appends or removes.</summary>
    internal const string SectionName = "claudeWire";
    /// <summary>The version claimed when the local CLI cannot be asked; also what a section without <c>version</c> means.</summary>
    internal const string DefaultVersion = "2.1.317";

    /// <summary>
    /// The entrypoint the claim defaults to, chosen by measurement rather than by plausibility: with everything else identical,
    /// <c>sdk-cli</c> moved the five-hour window at about half the rate of <c>cli</c>, <c>claude-vscode</c> and
    /// <c>claude-desktop-3p</c> (31–33k tokens per 0.01 against 62–68k, reproduced in both arm orders on 2026-11-13). It is the
    /// label the public report names as expensive, which is why the numbers are recorded in the provider spec next to this value.
    /// </summary>
    internal const string DefaultEntrypoint = "sdk-cli";

    /// <summary>One sentence a reader of <c>settings.json</c> can act on; the full table lives in the provider spec.</summary>
    internal const string Documentation =
        "enabled: true|false, no quotes; omitted or not a boolean reads as true. " +
        "version: three numbers (2.1.317) - also goes into the user agent. " +
        "build: optional 1-8 lowercase alphanumerics; omit it to use the local CLI's own value at runtime. " +
        "entrypoint: 1-32 chars of a-z 0-9 _ - starting with a letter (sdk-cli, claude-vscode, claude-desktop-3p). " +
        "note is ignored.";

    /// <summary>The claim to make, or null when the section switches it off.</summary>
    internal static ClaudeWire? Resolve(ClaudeWireSectionDocument? section)
    {
        if (section is not null && !IsOn(section))
        {
            return null;
        }

        section ??= new ClaudeWireSectionDocument();

        var version = ValidVersion(section.Version) ? section.Version!.Trim() : DefaultVersion;
        // No default build: the real one is a value the local CLI produces at runtime, so an unpinned section claims "ask the CLI"
        // rather than a constant that would be wrong on every machine whose CLI differs.
        var build = ValidBuild(section.Build) ? section.Build!.Trim() : string.Empty;
        var entrypoint = ValidEntrypoint(section.Entrypoint) ? section.Entrypoint!.Trim() : DefaultEntrypoint;
        return new ClaudeWire(version, build, entrypoint, AnthropicWire.Betas);
    }

    /// <summary>
    /// The switch: only <c>false</c> turns the claim off. A missing switch, and any value that is not a boolean, read as the
    /// default (on) — one spelling is the rule, and an unreadable one is "nothing was said", not "the opposite was said".
    /// </summary>
    private static bool IsOn(ClaudeWireSectionDocument section) =>
        section.Enabled is not { ValueKind: JsonValueKind.False };

    private static bool ValidVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && VersionRegex().IsMatch(value.Trim());

    private static bool ValidBuild(string? value) =>
        !string.IsNullOrWhiteSpace(value) && BuildRegex().IsMatch(value.Trim());

    /// <summary>
    /// The official client's own rule for a request-name override, loosened only in that reserved names stay typable: an
    /// entrypoint the client itself would refuse is still a value a user may deliberately choose, and this app does not
    /// pretend to gate it — the spec says what each name claims.
    /// </summary>
    private static bool ValidEntrypoint(string? value) =>
        !string.IsNullOrWhiteSpace(value) && EntrypointRegex().IsMatch(value.Trim());

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[a-z0-9]{1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex EntrypointRegex();
}
