using System.Collections.ObjectModel;
using System.Windows.Input;
using ICommand = System.Windows.Input.ICommand;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Mcp;

namespace Forge.App.ViewModels.Settings;

/// <summary>One server read from the import source; the checkbox decides, the badge says whether the name is taken.</summary>
internal sealed class McpImportCandidateViewModel : NotifyingObject
{
    private bool selected;

    public McpImportCandidateViewModel(McpServerEntry entry, bool alreadyExists, string? sourcePath)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        AlreadyExists = alreadyExists;
        SourceText = McpImportSource.LabelOf(sourcePath);
        selected = !alreadyExists;
    }

    public McpServerEntry Entry { get; }

    /// <summary>Which application's file this server came from ("Claude Code", "Cursor", …); empty when unknown.</summary>
    public string SourceText { get; }

    public bool HasSource => SourceText.Length > 0;

    public string Name => Entry.Name;

    public string TransportText => Entry.IsHttp ? McpSettingsStrings.TransportHttp : McpSettingsStrings.TransportStdio;

    public string Detail => Entry.Command ?? Entry.Url ?? string.Empty;

    public bool HasDetail => Detail.Length > 0;

    public bool AlreadyExists { get; }

    public bool IsSelected
    {
        get => selected;
        set => Set(ref selected, value);
    }
}

/// <summary>
/// One config another application keeps its MCP servers in. The names are the applications' own ("Claude Code", "Cursor"),
/// so they are not translated; only the sentences around them are.
/// </summary>
internal sealed record McpImportSource(string Label, string FilePath)
{
    /// <summary>The sources scanned by the import panel: the user's own files, in the order a duplicate name is decided.</summary>
    public static McpImportSource[] Known { get; } = Build();

    /// <summary>Which application a file belongs to; empty for a path that is not one of <see cref="Known"/>.</summary>
    public static string LabelOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        foreach (var source in Known)
        {
            if (string.Equals(source.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return source.Label;
            }
        }

        return string.Empty;
    }

    private static McpImportSource[] Build()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var sources = new List<McpImportSource>(3);
        if (!string.IsNullOrEmpty(home))
        {
            sources.Add(new McpImportSource("Claude Code", Path.Combine(home, ".claude.json")));
        }

        if (!string.IsNullOrEmpty(appData))
        {
            sources.Add(new McpImportSource(
                "Claude Desktop",
                Path.Combine(appData, "Claude", "claude_desktop_config.json")));
        }

        if (!string.IsNullOrEmpty(home))
        {
            sources.Add(new McpImportSource("Cursor", Path.Combine(home, ".cursor", "mcp.json")));
        }

        return [.. sources];
    }
}
