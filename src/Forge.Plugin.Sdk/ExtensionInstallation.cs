using MessagePack;

namespace Forge.Plugin.Sdk;

/// <summary>Wire values for the plugin origin dimension (wave W10a), mirroring <c>Forge.Approval.PluginOrigin</c>.
/// The SDK cannot reference the approval engine, so the strings live here and the host maps them.</summary>
public static class ExtensionOrigins
{
    public const string Installed = "installed";
    public const string Bundled = "bundled";
    public const string BuiltIn = "builtin";
}

/// <summary>
/// The path agreement of <c>extensions.install</c> (wave W10a). The main program extracts a downloaded package into
/// <c>&lt;host data&gt;/<see cref="TempDirectoryName"/>/&lt;fresh directory&gt;</c> and sends that directory as
/// <see cref="ExtensionInstallRequest.SourceDir"/>; the host accepts an install only from inside that root, so the two
/// sides read the name from here rather than each spelling it out - a drift would make every install
/// <c>invalid_request</c>.
/// </summary>
public static class ExtensionInstallPaths
{
    public const string TempDirectoryName = "install";
}

/// <summary>
/// <c>extensions.install</c> arguments (wave W10a). The main program has already downloaded the package, verified its
/// SHA-256 against the repository index and extracted it into <see cref="SourceDir"/> — a directory whose root holds a
/// <c>manifest.json</c> in the existing plugin layout. The host re-validates the manifest (id/version safety, kind),
/// refuses a <see cref="SourceDir"/> outside the agreed installation temp root, swaps the directory in with the same
/// trash rollback as <c>extensions.commit</c>, and records the approval entry with <see cref="Origin"/>. The new version
/// starts <c>Draft</c>: installation never approves, the user still approves the computed content hashes.
/// </summary>
[MessagePackObject]
public sealed partial record ExtensionInstallRequest(
    [property: Key(0)] string SourceDir,
    [property: Key(1)] string Origin);

/// <summary>
/// Result of a successful install: the plugin id/version actually installed, its status afterwards (normally
/// <c>Draft</c>) and <see cref="Diagnostics"/>, which carries non-blocking diagnostics of a successful answer only. A
/// failed install is reported as a host error code and leaves the previous live version untouched; it is never
/// expressed as a result with diagnostics.
/// </summary>
[MessagePackObject]
public sealed partial record ExtensionInstallResult(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Version,
    [property: Key(2)] PluginStatusInfo Status,
    [property: Key(3)] IReadOnlyList<string> Diagnostics);
