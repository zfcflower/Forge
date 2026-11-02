using Forge.Abstractions;

namespace Forge.Core.Prompts;

/// <summary>Reads <see cref="AgentDefinition"/> files (frontmatter + prompt body) from disk or text.</summary>
public static class AgentFileLoader
{
    public const string FileExtension = ".md";

    /// <summary>Parses agent Markdown text; <paramref name="fallbackName"/> is used when frontmatter omits a name.</summary>
    public static AgentDefinition Parse(string text, string fallbackName) =>
        AgentMarkdownParser.Parse(text, fallbackName);

    public static AgentDefinition LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseFile(path, File.ReadAllText(path));
    }

    public static async Task<AgentDefinition> LoadFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return ParseFile(path, text);
    }

    /// <summary>Loads every top-level <c>*.md</c> file of <paramref name="path"/>, ordered by file name.</summary>
    public static IReadOnlyList<AgentDefinition> LoadDirectory(string path)
    {
        var files = EnumerateAgentFiles(path);
        var definitions = new AgentDefinition[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            definitions[i] = ParseFile(files[i], File.ReadAllText(files[i]));
        }

        return definitions;
    }

    /// <summary>Loads every top-level <c>*.md</c> file of <paramref name="path"/>, ordered by file name.</summary>
    public static async Task<IReadOnlyList<AgentDefinition>> LoadDirectoryAsync(string path, CancellationToken ct = default)
    {
        var files = EnumerateAgentFiles(path);
        var reads = new Task<string>[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            reads[i] = File.ReadAllTextAsync(files[i], ct);
        }

        await Task.WhenAll(reads).ConfigureAwait(false);

        var definitions = new AgentDefinition[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            definitions[i] = ParseFile(files[i], await reads[i].ConfigureAwait(false));
        }

        return definitions;
    }

    private static AgentDefinition ParseFile(string path, string text) =>
        AgentMarkdownParser.Parse(text, Path.GetFileNameWithoutExtension(path));

    private static string[] EnumerateAgentFiles(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Agent directory not found: {path}");
        }

        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(path))
        {
            if (Path.GetExtension(file).Equals(FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(file);
            }
        }

        found.Sort(static (left, right) => string.CompareOrdinal(Path.GetFileName(left), Path.GetFileName(right)));
        return [.. found];
    }
}
