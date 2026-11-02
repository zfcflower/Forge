namespace Forge.Core.Storage;

/// <summary>Guards identifiers that become path segments or SQL parameters.</summary>
internal static class StoragePaths
{
    public static string SafeSegment(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value.All(static ch => ch == '.') || value[^1] is '.' or ' ')
            throw new ArgumentException($"'{value}' cannot be used as a storage identifier.", paramName);
        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))
                throw new ArgumentException($"'{value}' contains characters that are not allowed in a storage identifier.", paramName);
        }

        return value;
    }
}
