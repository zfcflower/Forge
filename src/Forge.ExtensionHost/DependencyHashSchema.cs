using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Forge.Approval;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>
/// The extension host's own schema for the hash an approval covers beside the source: manifest dependencies, the
/// declared permissions and the digests of every file of the loadable image.
/// <para>
/// The host used to fold these three kinds of tuple into <see cref="Forge.Approval.Hashing.DependencyHash"/>, which
/// writes each entry as <c>name@version</c> on its own line. That encoding is only injective for names and versions that
/// contain neither <c>@</c> nor a newline, and manifest values are arbitrary text: a dependency named
/// <c>permission:Database</c> with the "version" <c>y</c> produced exactly the same text as the internal permission entry
/// <c>Database</c>/<c>y</c>, so adding a dependency could stand in for granting a permission and an old approval stayed
/// valid across a permission change. Prefixing the internal entries does not fix that class of collision, only one
/// instance of it.
/// </para>
/// <para>
/// This schema is therefore unambiguous by construction: every tuple is written as a <em>domain</em>, a key and a value,
/// and every one of those three strings is frame-encoded with its UTF-8 byte length before its bytes, so no content -
/// <c>@</c>, newlines, Unicode, empty strings, unpaired surrogates - can ever move a boundary or impersonate a domain.
/// The domain label is written by the host from a closed set, never parsed out of user data, so manifest dependencies
/// cannot inhabit the permission or file domain. Entries are sorted ordinally and exact duplicates collapse, so the
/// result is deterministic. Anything that changes an entry changes the hash, and a changed hash fails closed: an existing
/// approval stops matching and the user has to approve the new content.
/// </para>
/// </summary>
internal static class DependencyHashSchema
{
    /// <summary>Version marker of this schema. A change to the encoding must change this marker (and so every hash).</summary>
    public const string SchemaMarker = "forge.dependency-fingerprint/v2";

    /// <summary>Domain of a manifest dependency: name plus version.</summary>
    private const string DependencyDomain = "dep";

    /// <summary>Domain of one file of the loadable image: relative path plus SHA-256.</summary>
    private const string FileDomain = "file";

    /// <summary>Domain of one declared permission: kind name plus scope.</summary>
    private const string PermissionDomain = "perm";

    private readonly record struct Entry(string Domain, string Key, string Value);

    /// <summary>Hashes the three tuple categories into one lowercase-hex fingerprint.</summary>
    public static string Compute(
        IReadOnlyList<Dependency> dependencies,
        IReadOnlyList<Permission> permissions,
        IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(files);

        var entries = new List<Entry>(dependencies.Count + permissions.Count + files.Count);
        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index] ??
                throw new ArgumentNullException(nameof(dependencies), "A manifest dependency may not be null.");
            entries.Add(new Entry(DependencyDomain, dependency.Name, dependency.Version));
        }

        foreach (var file in files)
        {
            entries.Add(new Entry(FileDomain, file.Key, file.Value));
        }

        for (var index = 0; index < permissions.Count; index++)
        {
            var permission = permissions[index] ??
                throw new ArgumentNullException(nameof(permissions), "A declared permission may not be null.");
            entries.Add(new Entry(PermissionDomain, permission.Kind.ToString(), NormalizeScope(permission.Scope)));
        }

        entries.Sort(CompareEntries);

        var buffer = new ArrayBufferWriter<byte>(64 + (entries.Count * 48));
        WriteField(buffer, SchemaMarker);

        // The distinct count keeps a list that ends early from being a prefix of a longer one, on top of the framing.
        var distinct = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            if (index == 0 || CompareEntries(entries[index - 1], entries[index]) != 0)
            {
                distinct++;
            }
        }

        WriteField(buffer, distinct.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (index > 0 && CompareEntries(entries[index - 1], entry) == 0)
            {
                continue;
            }

            WriteField(buffer, entry.Domain);
            WriteField(buffer, entry.Key);
            WriteField(buffer, entry.Value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>
    /// Canonical scope of a permission. A permission without a scope and a permission with an empty scope describe the
    /// same grant (the registration recorder keys requirements on <c>scope ?? ""</c> and refuses an empty scope, and the
    /// reader refuses it too), so both fold to the same entry instead of hashing as two different grants.
    /// </summary>
    private static string NormalizeScope(string? scope) => scope ?? string.Empty;

    private static int CompareEntries(Entry left, Entry right)
    {
        var byDomain = string.CompareOrdinal(left.Domain, right.Domain);
        if (byDomain != 0)
        {
            return byDomain;
        }

        var byKey = string.CompareOrdinal(left.Key, right.Key);
        return byKey != 0 ? byKey : string.CompareOrdinal(left.Value, right.Value);
    }

    /// <summary>
    /// Appends one frame: the UTF-8 byte length in decimal, a newline, then that many UTF-8 bytes. The length prefix is
    /// what makes the encoding injective; the newline after it only keeps the stream readable.
    /// </summary>
    private static void WriteField(ArrayBufferWriter<byte> buffer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var header = buffer.GetSpan(12);
        if (!Utf8Formatter.TryFormat(byteCount, header, out var written))
        {
            throw new InvalidOperationException("The dependency fingerprint frame length could not be formatted.");
        }

        header[written] = (byte)'\n';
        buffer.Advance(written + 1);
        if (byteCount == 0)
        {
            return;
        }

        var payload = buffer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, payload);
        buffer.Advance(byteCount);
    }
}
