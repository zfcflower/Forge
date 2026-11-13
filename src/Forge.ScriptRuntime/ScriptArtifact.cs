using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Forge.Scripting;
using MessagePack;

namespace Forge.ScriptRuntime;

/// <summary>
/// The byte format and identity of a compiled <c>run_code</c> script. The compiler (<c>Forge.Scripting</c>) emits one
/// loadable PE image with an embedded PDB and a <see cref="ScriptArtifactDescriptor"/>; the executor
/// (<c>Forge.ScriptExecutor</c>) re-checks the descriptor against the bytes and the running runtime before loading the
/// image from memory. The descriptor travels beside the image in the <c>executor.load</c> message and is not part of
/// the image, so byte integrity (<see cref="ScriptArtifactDescriptor.PeSha256"/>) and cache identity
/// (<see cref="ScriptArtifactDescriptor.CacheKey"/>) never collapse into one concept.
/// </summary>
public static class ScriptArtifact
{
    /// <summary>The artifact contract this module implements; the compiler writes it into every descriptor, and the
    /// validator refuses a descriptor that names a different one, so an executor never loads bytes it was not built to
    /// run.</summary>
    public const string ContractVersion = "forge-script-artifact/v1";

    /// <summary>Bump when the descriptor fields or the validation rules change incompatibly.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Upper bound on the PE image (assembly + embedded PDB). The image travels as MessagePack <c>bin</c>, so the whole
    /// <c>executor.load</c> frame stays under <c>Forge.Ipc.IpcProtocol.MaxFrameBytes</c> (16 MiB); the arithmetic is
    /// asserted by <see cref="IsFrameWithinBounds"/>.
    /// </summary>
    public const int MaxAssemblyBytes = 8 * 1024 * 1024;

    /// <summary>Upper bound on the serialized descriptor text; it is metadata, never image bytes.</summary>
    public const int MaxDescriptorChars = 64 * 1024;

    /// <summary>The entry point type name the compiler emits, in the global namespace of the script assembly.</summary>
    public const string DefaultEntryPointType = "ForgeScriptEntry";

    /// <summary>The entry point method name the compiler emits.</summary>
    public const string DefaultEntryPointMethod = "Run";

    /// <summary>
    /// What the largest possible <c>executor.load</c> frame costs: the image plus the descriptor bound (UTF-8, at most
    /// three bytes per char) plus room for the frame header and MessagePack framing.
    /// </summary>
    public const int MaxLoadFrameBytes = MaxAssemblyBytes + (MaxDescriptorChars * 3) + 4096;

    /// <summary>True when even the largest legal load frame fits the Ipc limit it is sent under.</summary>
    public static bool IsFrameWithinBounds(int maxFrameBytes) => MaxLoadFrameBytes < maxFrameBytes;

    /// <summary>The MVID of the assembly that owns <see cref="ScriptGlobals"/>: a script compiled against another one
    /// cannot run here.</summary>
    public static Guid GlobalsModuleVersionId { get; } =
        typeof(ScriptGlobals).Assembly.ManifestModule.ModuleVersionId;

    /// <summary>The same identity as the descriptor carries it, in the compact form the compiler writes.</summary>
    public static string GlobalsMvid { get; } = GlobalsModuleVersionId.ToString("N");

    /// <summary>The runtime the compiler that produced the artifact ran on.</summary>
    public static string RuntimeVersion { get; } = Environment.Version.ToString();

    /// <summary>True when <paramref name="runtimeVersion"/> names a runtime compatible with this one (same major and
    /// minor).</summary>
    public static bool IsCompatibleRuntimeVersion(string? runtimeVersion)
    {
        if (!Version.TryParse(runtimeVersion, out var other))
        {
            return false;
        }

        var current = Environment.Version;
        return other.Major == current.Major && other.Minor == current.Minor;
    }

    /// <summary>Hex SHA-256 of one image; the value the descriptor carries as <c>PeSha256</c>.</summary>
    public static string Sha256(ReadOnlySpan<byte> image) => Convert.ToHexStringLower(SHA256.HashData(image));

    /// <summary>Refuses an image larger than <see cref="MaxAssemblyBytes"/>; both ends call it before sending or
    /// loading.</summary>
    public static void EnsureImageWithinBounds(int length)
    {
        if (length > MaxAssemblyBytes)
        {
            throw new ScriptArtifactException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The script image is {length} bytes; the limit is {MaxAssemblyBytes}."));
        }
    }

    /// <summary>
    /// Validates the descriptor on its own: required text fields, the artifact contract, the size bound and the
    /// code-identity fields that keep the entry point inside the loaded image.
    /// </summary>
    public static void ValidateDescriptor(ScriptArtifactDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor, "The executor.load request carries no descriptor.");

        Require(descriptor.AssemblyName, nameof(ScriptArtifactDescriptor.AssemblyName));
        Require(descriptor.SourceHash, nameof(ScriptArtifactDescriptor.SourceHash));
        Require(descriptor.CacheKey, nameof(ScriptArtifactDescriptor.CacheKey));
        Require(descriptor.ContractVersion, nameof(ScriptArtifactDescriptor.ContractVersion));
        Require(descriptor.RuntimeVersion, nameof(ScriptArtifactDescriptor.RuntimeVersion));
        Require(descriptor.EntryPointType, nameof(ScriptArtifactDescriptor.EntryPointType));
        Require(descriptor.EntryPointMethod, nameof(ScriptArtifactDescriptor.EntryPointMethod));
        Require(descriptor.GlobalsMvid, nameof(ScriptArtifactDescriptor.GlobalsMvid));
        Require(descriptor.PeSha256, nameof(ScriptArtifactDescriptor.PeSha256));

        if (!string.Equals(descriptor.ContractVersion, ContractVersion, StringComparison.Ordinal))
        {
            throw new ScriptArtifactException(
                $"The script was compiled for artifact contract '{descriptor.ContractVersion}'; this runtime implements '{ContractVersion}'.");
        }

        if (descriptor.PeLength is <= 0 or > MaxAssemblyBytes)
        {
            throw new ScriptArtifactException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The script image is declared as {descriptor.PeLength} bytes; the limit is {MaxAssemblyBytes}."));
        }

        if (descriptor.EntryPointType.Length > 256 || descriptor.EntryPointMethod.Length > 256)
        {
            throw new ScriptArtifactException("The script entry point name is longer than the descriptor allows.");
        }

        RequireTypeName(descriptor.EntryPointType);
        RequireIdentifier(descriptor.EntryPointMethod, nameof(ScriptArtifactDescriptor.EntryPointMethod));

        if (!Guid.TryParse(descriptor.GlobalsMvid, out var globalsMvid))
        {
            throw new ScriptArtifactException(
                $"The script descriptor's '{nameof(ScriptArtifactDescriptor.GlobalsMvid)}' is not a module version id.");
        }

        if (globalsMvid != GlobalsModuleVersionId)
        {
            throw new ScriptArtifactException(
                $"The script was compiled against globals surface {globalsMvid:D}; this executor carries {GlobalsModuleVersionId:D}.");
        }

        if (!IsCompatibleRuntimeVersion(descriptor.RuntimeVersion))
        {
            throw new ScriptArtifactException(
                $"The script was compiled for runtime {descriptor.RuntimeVersion}; this executor runs {RuntimeVersion}.");
        }
    }

    /// <summary>
    /// Validates a descriptor against the bytes that are about to be loaded: length, SHA-256, globals surface, runtime
    /// and entry point. Nothing is loaded and no script code runs when any of these mismatches.
    /// </summary>
    public static void Validate(ScriptArtifactDescriptor? descriptor, ReadOnlySpan<byte> image)
    {
        ValidateDescriptor(descriptor);
        if (image.Length != descriptor!.PeLength)
        {
            throw new ScriptArtifactException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The script image is {image.Length} bytes but the descriptor declares {descriptor.PeLength}."));
        }

        var actual = Sha256(image);
        if (!string.Equals(actual, descriptor.PeSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScriptArtifactException(
                $"The script image hash '{actual}' does not match the descriptor's '{descriptor.PeSha256}'.");
        }
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ScriptArtifactException($"The script descriptor carries no '{field}'.");
        }

        if (value.Length > MaxDescriptorChars)
        {
            throw new ScriptArtifactException($"The script descriptor's '{field}' is longer than {MaxDescriptorChars} chars.");
        }
    }

    /// <summary>The entry point type lives inside the loaded image, so a namespaced name is fine; nothing else is: the
    /// lookup can never leave the assembly it was given.</summary>
    private static void RequireTypeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ScriptArtifactException(
                $"The script descriptor carries no '{nameof(ScriptArtifactDescriptor.EntryPointType)}'.");
        }

        var segmentStart = true;
        foreach (var c in value)
        {
            if (c == '.')
            {
                segmentStart = true;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '+' && c != '`')
            {
                throw new ScriptArtifactException(
                    $"The script descriptor's '{nameof(ScriptArtifactDescriptor.EntryPointType)}' is not a type name.");
            }

            if (segmentStart && !(char.IsAsciiLetter(c) || c == '_'))
            {
                throw new ScriptArtifactException(
                    $"The script descriptor's '{nameof(ScriptArtifactDescriptor.EntryPointType)}' is not a type name.");
            }

            segmentStart = false;
        }

        if (segmentStart)
        {
            throw new ScriptArtifactException(
                $"The script descriptor's '{nameof(ScriptArtifactDescriptor.EntryPointType)}' is not a type name.");
        }
    }

    private static void RequireIdentifier(string? value, string field)
    {
        if (string.IsNullOrEmpty(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            throw new ScriptArtifactException($"The script descriptor's '{field}' is not a valid identifier.");
        }

        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                continue;
            }

            throw new ScriptArtifactException($"The script descriptor's '{field}' is not a valid identifier.");
        }
    }
}

/// <summary>
/// Identity and validation metadata for one compiled script image. Every field is produced by the trusted compiler and
/// re-checked by the executor before the bytes are loaded, so a truncated, corrupted, or wrong-runtime image is refused
/// without ever being loaded.
/// </summary>
[MessagePackObject]
public sealed partial record ScriptArtifactDescriptor(
    [property: Key(0)] string AssemblyName,
    [property: Key(1)] string SourceHash,
    [property: Key(2)] string CacheKey,
    [property: Key(3)] string ContractVersion,
    [property: Key(4)] string RuntimeVersion,
    [property: Key(5)] string EntryPointType,
    [property: Key(6)] string EntryPointMethod,
    [property: Key(7)] string GlobalsMvid,
    [property: Key(8)] string PeSha256,
    [property: Key(9)] int PeLength);

/// <summary>The executor refused an artifact: a size bound, a length/hash mismatch, a wrong runtime or a wrong globals
/// surface. No image was loaded and no script code ran.</summary>
public sealed class ScriptArtifactException(string message) : Exception(message);
