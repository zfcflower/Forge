using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Forge.Abstractions;
using Forge.App.Automations.Settings;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.Core.Automations;
using Forge.Workspace;

namespace Forge.App.Automations;

/// <summary>
/// The window-side callbacks the automations page needs but the composition root cannot supply (the main window does not
/// exist yet when the section is registered); the main window view model fills them in.
/// </summary>
internal sealed class AutomationUiBridge
{
    public Action<string>? OpenSession { get; set; }

    public Func<string, Task<bool>>? Confirm { get; set; }
}

/// <summary>Builds the automation service, its built-in CI chain and the settings section (called from the composition root).</summary>
internal static class AutomationComposition
{
    public const string DefinitionsFileName = "automations.json";

    public static AutomationService Create(
        string settingsDirectory,
        string dataDirectory,
        IProjectRegistry projects,
        ISessionCatalog catalog,
        ISessionStore store,
        Services.WorktreeService worktrees,
        Func<ModelSettings, IChatProvider> providers,
        Func<ModelSettings> defaultModel,
        IPrPlatform pullRequests,
        GitCli git,
        INotifications? notifications,
        Providers.ModelCatalogRefreshService modelCatalogRefresh,
        Prompts.PromptTemplateService? promptTemplates = null,
        Func<IdleArchiveSettings>? idleArchive = null)
    {
        // Without the composition's service (a test) the user's ~/.forge and the workspace's .forge are still honoured.
        promptTemplates ??= new Prompts.PromptTemplateService(Agents.AgentRuntimeServices.UserDirectory());
        var service = new AutomationService(
            Path.Combine(settingsDirectory, DefinitionsFileName),
            dataDirectory,
            projects,
            catalog,
            store,
            worktrees,
            providers,
            defaultModel,
            notifications,
            promptTemplates: promptTemplates,
            idleArchive: idleArchive);
        service.RegisterRunner(
            BuiltInAutomations.CiChain,
            new CiChainRunner(
                pullRequests,
                new GitCliCiGit(git),
                store,
                service.Mailbox,
                service.Launcher,
                notifications,
                () => service.Engine.Config,
                service.Runs,
                promptTemplates));
        service.RegisterRunner(
            BuiltInAutomations.ModelCatalogRefresh,
            new ModelCatalogRefreshRunner(modelCatalogRefresh));
        // The startup catch-up: only when today's catalog is missing, so a normal launch records nothing. It runs after
        // attach because that is where the engine's own loop starts.
        service.AddLoopOnAttach(async ct =>
        {
            // The snapshot is read on the pool at startup: judging "today's is missing" before that read lands would fetch a
            // catalog the disk already holds (and leave a run record for it).
            try
            {
                await modelCatalogRefresh.Loaded.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A read that is still not done after thirty seconds is not a reason to skip the check: the fetch itself
                // would have taken longer.
            }

            if (modelCatalogRefresh.IsFromToday || !service.IsEnabled(BuiltInAutomations.ModelCatalogRefresh))
            {
                return;
            }

            await service.Engine
                .StartAsync(new AutomationRunRequest(BuiltInAutomations.ModelCatalogRefresh, AutomationTriggerSource.Event), ct)
                .ConfigureAwait(false);
        });
        var watcher = new CiFailureWatcher(
            service.Engine,
            catalog,
            pullRequests,
            service.Runs,
            TimeProvider.System,
            () => service.IsEnabled(BuiltInAutomations.CiChain),
            notifications);
        service.AddLoopOnAttach(watcher.RunAsync);
        return service;
    }

    /// <summary>The settings page over the service; <paramref name="bridge"/> is filled by the main window.</summary>
    public static SettingsSection Section(
        AutomationService service,
        IProjectRegistry projects,
        ISessionCatalog catalog,
        AutomationUiBridge bridge) =>
        new(
            SettingsSectionIds.Automations,
            AutomationSettingsStrings.SectionTitleKey,
            SettingsSectionIds.AutomationsOrder,
            new AutomationsSettingsViewModel(
                service.Engine,
                service.Runs,
                Preview,
                ct => ProjectChoicesAsync(projects, ct),
                ct => SessionChoicesAsync(projects, catalog, ct),
                id => bridge.OpenSession?.Invoke(id),
                editor => ActiveWindow() is { } owner ? AutomationEditorWindow.ShowAsync(owner, editor) : Task.FromResult(false),
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                name => bridge.Confirm?.Invoke(name) ?? Task.FromResult(true)),
            SettingsGroups.Agent,
            "IconBolt");

    /// <summary>The next <paramref name="count"/> fire times of one trigger in local time, or why it is invalid.</summary>
    internal static AutomationPreview Preview(AutomationTrigger trigger, int count)
    {
        if (!AutomationSchedule.TryCreate(trigger, out var schedule, out var error) || schedule is null)
        {
            return new AutomationPreview([], error);
        }

        return new AutomationPreview(schedule.NextOccurrences(DateTimeOffset.Now, TimeZoneInfo.Local, count), null);
    }

    private static async Task<IReadOnlyList<AutomationProjectChoice>> ProjectChoicesAsync(IProjectRegistry projects, CancellationToken ct)
    {
        var all = await Task.Run(() => projects.ListAsync(ct), ct).ConfigureAwait(true);
        var list = new List<AutomationProjectChoice>(all.Count);
        foreach (var project in all)
        {
            list.Add(new AutomationProjectChoice(project.Id, project.Name, project.Kind == ProjectKind.Repository));
        }

        return list;
    }

    private static async Task<IReadOnlyList<AutomationSessionChoice>> SessionChoicesAsync(
        IProjectRegistry projects,
        ISessionCatalog catalog,
        CancellationToken ct)
    {
        var (all, sessions) = await Task.Run(
                async () => (
                    await projects.ListAsync(ct).ConfigureAwait(false),
                    await catalog.QueryAsync(new SessionQuery { Status = SessionStatus.Active, ParentSessionId = string.Empty }, ct).ConfigureAwait(false)),
                ct)
            .ConfigureAwait(true);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in all)
        {
            names[project.Id] = project.Name;
        }

        var list = new List<AutomationSessionChoice>(sessions.Count);
        foreach (var session in sessions)
        {
            list.Add(new AutomationSessionChoice(
                session.Id,
                session.Title,
                session.ProjectId is { } id ? names.GetValueOrDefault(id) : null));
        }

        return list;
    }

    /// <summary>The focused window (the settings window when the page is used), else the main window.</summary>
    private static Window? ActiveWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        foreach (var window in desktop.Windows)
        {
            if (window.IsActive)
            {
                return window;
            }
        }

        return desktop.MainWindow;
    }
}
