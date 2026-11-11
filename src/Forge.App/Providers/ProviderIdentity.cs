using System.Text;

namespace Forge.App.Providers;

/// <summary>
/// How a provider id is made from its display name. The id is the key of the secret reference, the model cache and the
/// settings entry, so it must be stable for a name and unique among the connected providers: two accounts of the same
/// vendor are allowed, they just get <c>-2</c>, <c>-3</c>… suffixes.
/// </summary>
internal static class ProviderIdentity
{
    /// <summary>A readable, lowercase slug of a name; <c>provider</c> when nothing usable remains.</summary>
    public static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > 0 ? slug : "provider";
    }

    /// <summary>The slug itself when it is free, otherwise the first <c>slug-N</c> (N ≥ 2) that is.</summary>
    public static string Unique(string slug, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        if (!used.Contains(slug))
        {
            return slug;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = slug + "-" + suffix;
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
