using System.Globalization;
using System.Text;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// The parsed fields of a Git LFS pointer file. Every field is optional: a pointer that lacks the <c>size</c> line (or
/// one that was truncated by the peek) still previews with what it has.
/// </summary>
internal readonly record struct LfsPointer(string? Oid, long? Size, string? Version)
{
    public bool HasOid => !string.IsNullOrEmpty(Oid);

    public bool HasSize => Size is not null;

    public bool HasVersion => !string.IsNullOrEmpty(Version);
}

/// <summary>Reads the three pointer lines (<c>version</c>, <c>oid sha256:…</c>, <c>size</c>) from the first bytes.</summary>
internal static class LfsPointerParser
{
    public static LfsPointer Parse(ReadOnlySpan<byte> firstBytes)
    {
        string? oid = null;
        long? size = null;
        string? version = null;
        var text = Encoding.UTF8.GetString(firstBytes);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("version ", StringComparison.Ordinal))
            {
                version = line["version ".Length..].TrimEnd();
            }
            else if (line.StartsWith("oid sha256:", StringComparison.Ordinal))
            {
                oid = line["oid ".Length..].TrimEnd();
            }
            else if (line.StartsWith("size ", StringComparison.Ordinal) &&
                     long.TryParse(line.AsSpan("size ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                size = parsed;
            }
        }

        return new LfsPointer(oid, size, version);
    }
}
