using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ICommand = System.Windows.Input.ICommand;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Mcp;

namespace Forge.App.ViewModels.Settings;

/// <summary>One row of the editor's env/header tables; a removal button each, and every keystroke re-validates the entry.</summary>
internal sealed class McpKeyValueRowViewModel : NotifyingObject
{
    private readonly Action changed;
    private string key;
    private string value;

    public McpKeyValueRowViewModel(string key, string value, Action changed, Action<McpKeyValueRowViewModel?> remove)
    {
        this.key = key;
        this.value = value;
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
        RemoveCommand = new ItemCommand<McpKeyValueRowViewModel>(remove);
    }

    public string Key
    {
        get => key;
        set
        {
            if (Set(ref key, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string Value
    {
        get => value;
        set
        {
            if (Set(ref value, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public ICommand RemoveCommand { get; }
}

/// <summary>
/// The entry editor: every field of <see cref="McpServerEntry"/> as editable text, the env/header tables, the save target
/// and live validation. <see cref="BuildEntry"/> starts from the original entry when there is one, so an edit keeps the
/// fields Forge does not understand (<see cref="McpServerEntry.RawNodeJson"/>).
/// </summary>
internal sealed class McpServerEditorViewModel : NotifyingObject
{
    private readonly McpServerEntry? original;
    private readonly string globalPath;
    private readonly string? projectPath;
    private readonly bool isNew;
    private string nameText;
    private bool stdio;
    private bool http;
    private string commandText;
    private string argsText;
    private string workingDirectoryText;
    private string urlText;
    private string readOnlyToolsText;
    private string timeoutText;
    private bool timeoutInvalid;
    private bool saveToProject;

    public McpServerEditorViewModel(McpServerEntry entry, string globalPath, string? projectPath, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalPath);
        original = isNew ? null : entry;
        this.globalPath = globalPath;
        this.projectPath = projectPath;
        this.isNew = isNew;
        nameText = entry.Name;
        stdio = entry.IsStdio || !entry.IsHttp;
        http = entry.IsHttp;
        commandText = entry.Command ?? string.Empty;
        argsText = string.Join("\n", entry.Args);
        workingDirectoryText = entry.WorkingDirectory ?? string.Empty;
        urlText = entry.Url ?? string.Empty;
        foreach (var pair in entry.Env)
        {
            EnvRows.Add(new McpKeyValueRowViewModel(pair.Key, pair.Value, RefreshIssues, RemoveEnvRow));
        }

        foreach (var pair in entry.Headers)
        {
            HeaderRows.Add(new McpKeyValueRowViewModel(pair.Key, pair.Value, RefreshIssues, RemoveHeaderRow));
        }

        readOnlyToolsText = string.Join("\n", entry.ReadOnlyTools);
        timeoutText = entry.TimeoutSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        saveToProject = projectPath is not null &&
                        string.Equals(entry.SourcePath, projectPath, StringComparison.Ordinal);
        AddEnvCommand = new RelayCommand(AddEnvRow);
        AddHeaderCommand = new RelayCommand(AddHeaderRow);
    }

    public string Title => isNew ? McpSettingsStrings.EditorTitleAdd : McpSettingsStrings.EditorTitleEdit(nameText);

    public string Name
    {
        get => nameText;
        set
        {
            if (Set(ref nameText, value ?? string.Empty))
            {
                RefreshIssues();
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public bool IsStdio
    {
        get => stdio;
        set
        {
            if (!Set(ref stdio, value))
            {
                return;
            }

            if (value && http)
            {
                http = false;
                OnPropertyChanged(nameof(IsHttp));
            }

            RefreshIssues();
        }
    }

    public bool IsHttp
    {
        get => http;
        set
        {
            if (!Set(ref http, value))
            {
                return;
            }

            if (value && stdio)
            {
                stdio = false;
                OnPropertyChanged(nameof(IsStdio));
            }

            RefreshIssues();
        }
    }

    public string Command
    {
        get => commandText;
        set
        {
            if (Set(ref commandText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string ArgsText
    {
        get => argsText;
        set
        {
            if (Set(ref argsText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string WorkingDirectory
    {
        get => workingDirectoryText;
        set
        {
            if (Set(ref workingDirectoryText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string Url
    {
        get => urlText;
        set
        {
            if (Set(ref urlText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public ObservableCollection<McpKeyValueRowViewModel> EnvRows { get; } = [];

    public ObservableCollection<McpKeyValueRowViewModel> HeaderRows { get; } = [];

    public string ReadOnlyToolsText
    {
        get => readOnlyToolsText;
        set
        {
            if (Set(ref readOnlyToolsText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string TimeoutText
    {
        get => timeoutText;
        set
        {
            if (Set(ref timeoutText, value ?? string.Empty))
            {
                RefreshIssues();
            }
        }
    }

    public string GlobalTargetText => McpSettingsStrings.TargetGlobal(globalPath);

    public string ProjectTargetText => projectPath is null ? string.Empty : McpSettingsStrings.TargetProject(projectPath);

    public bool HasProjectTarget => projectPath is not null;

    public bool SaveToGlobal
    {
        get => !saveToProject;
        set
        {
            if (value)
            {
                SetSaveToProject(false);
            }
        }
    }

    public bool SaveToProject
    {
        get => saveToProject;
        set => SetSaveToProject(value);
    }

    /// <summary>The file a save writes; the project file only when it exists and the user picked it.</summary>
    public string TargetPath => saveToProject && projectPath is not null ? projectPath : globalPath;

    /// <summary>One localized line per problem; live-recomputed on every edit.</summary>
    public ObservableCollection<string> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    public ICommand AddEnvCommand { get; }

    public ICommand AddHeaderCommand { get; }

    /// <summary>The entry the editor currently spells; normalized the way the file writer wants it.</summary>
    public McpServerEntry BuildEntry()
    {
        var basedOn = original ?? new McpServerEntry { Name = nameText };
        var entry = basedOn with
        {
            Name = nameText,
            Command = stdio ? BlankToNull(commandText) : null,
            Url = http ? BlankToNull(urlText) : null,
            WorkingDirectory = stdio ? BlankToNull(workingDirectoryText) : null,
            Args = SplitLines(argsText),
            Env = PairsOf(EnvRows),
            Headers = PairsOf(HeaderRows),
            ReadOnlyTools = SplitLines(readOnlyToolsText),
            TimeoutSeconds = ParseTimeout(),
        };
        return entry.Normalized();
    }

    /// <summary>Re-derives the validation lines (and the title/target labels) after a language switch or an edit.</summary>
    public void RefreshIssues()
    {
        var entry = BuildEntry();
        var lines = new List<string>(4);
        foreach (var issue in entry.Validate())
        {
            lines.Add(McpSettingsStrings.IssueText(issue));
        }

        if (timeoutInvalid)
        {
            lines.Add(McpSettingsStrings.ValidationTimeoutNumber);
        }

        Issues.Clear();
        foreach (var line in lines)
        {
            Issues.Add(line);
        }

        OnPropertyChanged(nameof(HasIssues));
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(GlobalTargetText));
        OnPropertyChanged(nameof(ProjectTargetText));
        RefreshIssues();
    }

    private void AddEnvRow()
    {
        EnvRows.Add(new McpKeyValueRowViewModel(string.Empty, string.Empty, RefreshIssues, RemoveEnvRow));
        RefreshIssues();
    }

    private void AddHeaderRow()
    {
        HeaderRows.Add(new McpKeyValueRowViewModel(string.Empty, string.Empty, RefreshIssues, RemoveHeaderRow));
        RefreshIssues();
    }

    private void RemoveEnvRow(McpKeyValueRowViewModel? row)
    {
        if (row is not null)
        {
            EnvRows.Remove(row);
        }

        RefreshIssues();
    }

    private void RemoveHeaderRow(McpKeyValueRowViewModel? row)
    {
        if (row is not null)
        {
            HeaderRows.Remove(row);
        }

        RefreshIssues();
    }

    private void SetSaveToProject(bool value)
    {
        if (Set(ref saveToProject, value))
        {
            OnPropertyChanged(nameof(SaveToGlobal));
            OnPropertyChanged(nameof(TargetPath));
        }
    }

    private double? ParseTimeout()
    {
        timeoutInvalid = false;
        var text = timeoutText.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            timeoutInvalid = true;
            return null;
        }

        return seconds;
    }

    private static string? BlankToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        return lines;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> PairsOf(ObservableCollection<McpKeyValueRowViewModel> rows)
    {
        var pairs = new List<KeyValuePair<string, string>>(rows.Count);
        foreach (var row in rows)
        {
            var key = row.Key.Trim();
            if (key.Length > 0)
            {
                pairs.Add(new KeyValuePair<string, string>(key, row.Value));
            }
        }

        return pairs;
    }
}
