namespace Forge.App.Composition;

/// <summary>
/// Every directory the app owns, resolved once at startup. The data root holds <c>forge.db</c>, <c>settings.json</c>,
/// <c>reviews/</c> and <c>extension-host/</c>; the extension-host data directory is the same one
/// <c>HostOptions.DefaultDataDirectory</c> computes, so the approval database the UI reads is the one the host enforces.
/// </summary>
internal sealed record AppPaths
{
    public const string WorkspaceVariable = "FORGE_WORKSPACE";

    public const string GeneralVariable = "FORGE_GENERAL_WORKSPACE";

    /// <summary>Overrides the whole data root, so a test run never touches the user's real database.</summary>
    public const string DataVariable = "FORGE_DATA_DIR";

    private AppPaths(
        string userDataDirectory,
        string hostDataDirectory,
        string pluginsDirectory,
        string settingsPath,
        string workspaceDirectory,
        string generalWorkspaceDirectory,
        string reviewsDirectory)
    {
        UserDataDirectory = userDataDirectory;
        HostDataDirectory = hostDataDirectory;
        PluginsDirectory = pluginsDirectory;
        SettingsPath = settingsPath;
        WorkspaceDirectory = workspaceDirectory;
        GeneralWorkspaceDirectory = generalWorkspaceDirectory;
        ReviewsDirectory = reviewsDirectory;
    }

    public string UserDataDirectory { get; }

    public string HostDataDirectory { get; }

    public string PluginsDirectory { get; }

    public string SettingsPath { get; }

    public string WorkspaceDirectory { get; }

    /// <summary>Root of the general (project-unrelated) workspace; sessions work in <c>scratch/&lt;date-slug&gt;</c> under it.</summary>
    public string GeneralWorkspaceDirectory { get; }

    /// <summary>Review comments of every workspace, kept out of the repositories themselves.</summary>
    public string ReviewsDirectory { get; }

    /// <summary>The one workbench database: sessions, messages, projects, todos, UI state and turn checkpoints.</summary>
    public string DatabasePath => Path.Combine(UserDataDirectory, "forge.db");

    /// <summary>Approval database shared with the extension host.</summary>
    public string ApprovalsDatabasePath => Path.Combine(HostDataDirectory, "approvals.db");

    public static AppPaths Create()
    {
        var data = Environment.GetEnvironmentVariable(DataVariable);
        string root;
        if (!string.IsNullOrWhiteSpace(data))
        {
            root = Path.GetFullPath(data);
        }
        else
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = string.IsNullOrEmpty(local)
                ? Path.Combine(Path.GetTempPath(), "Forge")
                : Path.Combine(local, "Forge");
        }

        var hostData = Path.Combine(root, "extension-host");

        var workspace = Environment.GetEnvironmentVariable(WorkspaceVariable);
        var workspaceDirectory = string.IsNullOrWhiteSpace(workspace)
            ? Environment.CurrentDirectory
            : workspace;
        if (!Path.IsPathFullyQualified(workspaceDirectory))
        {
            workspaceDirectory = Path.GetFullPath(workspaceDirectory);
        }

        var general = Environment.GetEnvironmentVariable(GeneralVariable);
        var generalDirectory = string.IsNullOrWhiteSpace(general)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Forge", "General")
            : general;
        if (!Path.IsPathFullyQualified(generalDirectory))
        {
            generalDirectory = Path.GetFullPath(generalDirectory);
        }

        return new AppPaths(
            root,
            hostData,
            Path.Combine(hostData, "plugins"),
            Path.Combine(root, "settings.json"),
            Path.GetFullPath(workspaceDirectory),
            Path.GetFullPath(generalDirectory),
            Path.Combine(root, "reviews"));
    }
}
