namespace Forge.IO;

internal static class TrashNames
{
    /// <summary>True for a directory, false for a file; throws when neither exists.</summary>
    public static bool Classify(string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            return true;
        }

        if (File.Exists(fullPath) || new FileInfo(fullPath).LinkTarget is not null)
        {
            return false;
        }

        throw new FileNotFoundException($"Cannot move '{fullPath}' to the trash: it does not exist.", fullPath);
    }

    public static bool Occupied(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>First attempt keeps the name; later ones become <c>name.2</c>, <c>name.3</c>, ...</summary>
    public static string WithSuffix(string name, int attempt) => attempt == 1 ? name : name + "." + attempt;
}
