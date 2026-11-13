using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Services;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Automations;

/// <summary>Where a user automation's prompt runs; the product creates sessions through the catalog and the window.</summary>
internal interface IAutomationSessions
{
    /// <summary>Creates a scratch session in the General project; null when there is no General project.</summary>
    Task<SessionInfo?> CreateScratchAsync(string title, AutomationDefinition definition, string runId, CancellationToken ct);

    /// <summary>
    /// The automation's own branch session in <paramref name="projectId"/>: reused while it is active, created (on a new
    /// <c>forge/</c> branch from the project's HEAD) otherwise. Null when the project is not a repository.
    /// </summary>
    Task<SessionInfo?> GetOrCreateForkAsync(string projectId, AutomationDefinition definition, string runId, CancellationToken ct);

    Task<SessionInfo?> GetAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// The runner of user definitions (the engine's default runner): a reminder only notifies; a prompt runs in a fresh General
/// scratch session, in the automation's own branch session, or as a read-only Scout under an existing session. The session
/// it targets is only ever written to through the mailbox, so a busy session is waited for and never interrupted.
/// </summary>
internal sealed class PromptRunner(
    IAutomationSessions sessions,
    ISessionMailbox mailbox,
    IChildAgentLauncher launcher,
    IAutomationHost host,
    IUiThread ui,
    INotifications? notifications,
    Func<ModelSettings> defaultModel,
    TimeProvider time) : IAutomationRunner
{
    /// <summary>The tools a read-only automation keeps: it can look, never change.</summary>
    internal static readonly IReadOnlyList<string> ReadOnlyTools = ["read", "grep", "find", "ls"];

    public async Task<AutomationRunOutcome> RunAsync(IAutomationRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var definition = context.Definition;
        var name = AutomationStrings.DisplayName(definition.Id, definition.Name);
        if (definition.Action == AutomationAction.Remind)
        {
            notifications.Notify(
                NotificationLevel.Info,
                AutomationStrings.RemindTitle(name),
                LocText.Literal(definition.Prompt ?? string.Empty),
                "automation-remind:" + definition.Id);
            return AutomationRunOutcome.Completed();
        }

        return definition.Placement switch
        {
            AutomationPlacement.Scout => await RunScoutAsync(context, name, ct).ConfigureAwait(false),
            AutomationPlacement.FixedFork => await RunInSessionAsync(
                    context,
                    name,
                    definition.ProjectId is { } project
                        ? await sessions.GetOrCreateForkAsync(project, definition, context.Run.Id, ct).ConfigureAwait(false)
                        : null,
                    ct)
                .ConfigureAwait(false),
            _ => await RunInSessionAsync(
                    context,
                    name,
                    await sessions.CreateScratchAsync(ScratchTitle(name), definition, context.Run.Id, ct).ConfigureAwait(false),
                    ct)
                .ConfigureAwait(false),
        };
    }

    /// <summary>The agent an automation-owned session runs with: read-only tools and/or the definition's model.</summary>
    internal static AgentDefinition? AgentFor(AutomationDefinition definition, ModelSettings fallback)
    {
        if (!definition.ReadOnlyTools && definition.Model is null)
        {
            return null;
        }

        return new AgentDefinition(
            "Root",
            "Main coding agent.",
            string.Empty,
            definition.Model ?? fallback,
            definition.ReadOnlyTools ? ReadOnlyTools : null);
    }

    private string ScratchTitle(string name) =>
        name + " " + time.GetLocalNow().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private async Task<AutomationRunOutcome> RunInSessionAsync(
        IAutomationRunContext context,
        string name,
        SessionInfo? session,
        CancellationToken ct)
    {
        if (session is null)
        {
            return AutomationRunOutcome.Failed(
                context.Definition.Placement == AutomationPlacement.Scratch
                    ? AutomationStrings.NoProjectText
                    : AutomationStrings.NoTargetText);
        }

        await context.UpdateAsync(run => run with { SessionId = session.Id }, ct).ConfigureAwait(false);
        var opened = await ui.InvokeAsync(async () =>
        {
            await host.RefreshTreesAsync().ConfigureAwait(true);
            return (host.FindOpen(session.Id) ?? await host.OpenAsync(session.Id, ct).ConfigureAwait(true)) is not null;
        }).ConfigureAwait(false);
        if (!opened)
        {
            return Finish(context, name, session.Title, new MailboxResult(MailboxStatus.Withdrawn, AutomationStrings.SessionUnavailableText));
        }

        // The agent (read-only tools, model) travels with the message: it applies to this one turn only, so a reused
        // branch session goes back to its own agent for the user's turns.
        var progress = new WaitingProgress(context);
        var result = await mailbox.DeliverAsync(
                new MailboxMessage(session.Id, context.Definition.Prompt ?? string.Empty, context.Run.Id, name)
                {
                    Agent = AgentFor(context.Definition, defaultModel()),
                },
                progress,
                ct)
            .ConfigureAwait(false);
        await progress.DrainAsync().ConfigureAwait(false);
        return Finish(context, name, session.Title, result);
    }

    private async Task<AutomationRunOutcome> RunScoutAsync(IAutomationRunContext context, string name, CancellationToken ct)
    {
        var target = context.Definition.TargetSessionId ?? context.Run.TargetSessionId;
        var session = target is null ? null : await sessions.GetAsync(target, ct).ConfigureAwait(false);
        if (session is null || session.Status != SessionStatus.Active || session.ParentSessionId is not null)
        {
            return AutomationRunOutcome.Failed(AutomationStrings.NoTargetText);
        }

        await context.UpdateAsync(run => run with { TargetSessionId = session.Id }, ct).ConfigureAwait(false);
        var result = await launcher.LaunchAsync(
                new ChildAgentRequest(session.Id, "Scout", context.Definition.Prompt ?? string.Empty),
                ct)
            .ConfigureAwait(false);
        await context.UpdateAsync(
                run => run with { SessionId = string.IsNullOrEmpty(result.ChildSessionId) ? null : result.ChildSessionId, Usage = result.Usage },
                ct)
            .ConfigureAwait(false);
        if (result.IsError)
        {
            notifications.Notify(
                NotificationLevel.Error,
                AutomationStrings.RunFailedTitle,
                AutomationStrings.RunFailedBody(name, result.Text),
                "automation-failed:" + context.Run.Id);
            return AutomationRunOutcome.Failed(result.Text);
        }

        notifications.Notify(
            NotificationLevel.Success,
            AutomationStrings.RunCompletedTitle,
            AutomationStrings.RunCompletedBody(name, session.Title),
            "automation-done:" + context.Run.Id);
        return AutomationRunOutcome.Completed();
    }

    private AutomationRunOutcome Finish(IAutomationRunContext context, string name, string sessionTitle, MailboxResult result)
    {
        switch (result.Status)
        {
            case MailboxStatus.Completed:
                notifications.Notify(
                    NotificationLevel.Success,
                    AutomationStrings.RunCompletedTitle,
                    AutomationStrings.RunCompletedBody(name, sessionTitle),
                    "automation-done:" + context.Run.Id);
                return AutomationRunOutcome.Completed();
            case MailboxStatus.Withdrawn:
                notifications.Notify(
                    NotificationLevel.Warning,
                    AutomationStrings.RunFailedTitle,
                    AutomationStrings.RunWithdrawnBody(name, result.Reason ?? string.Empty),
                    "automation-failed:" + context.Run.Id);
                return AutomationRunOutcome.Cancelled(result.Reason ?? string.Empty);
            case MailboxStatus.Cancelled:
                return AutomationRunOutcome.Cancelled(result.Reason ?? string.Empty);
            default:
                notifications.Notify(
                    NotificationLevel.Error,
                    AutomationStrings.RunFailedTitle,
                    AutomationStrings.RunFailedBody(name, result.Reason ?? string.Empty),
                    "automation-failed:" + context.Run.Id);
                return AutomationRunOutcome.Failed(result.Reason ?? string.Empty);
        }
    }
}

/// <summary>
/// Mirrors the mailbox's "waiting for the session to be idle" onto the run's status. Reports are written in order, and a
/// runner awaits <see cref="DrainAsync"/> before its own next update, so a late report never overwrites it.
/// </summary>
internal sealed class WaitingProgress(IAutomationRunContext context) : IProgress<bool>
{
    private readonly object gate = new();
    private Task last = Task.CompletedTask;

    public void Report(bool value)
    {
        var status = value ? AutomationRunStatus.WaitingIdle : AutomationRunStatus.Running;
        lock (gate)
        {
            last = last.ContinueWith(_ => UpdateAsync(status), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Completes once every report so far is written.</summary>
    public Task DrainAsync()
    {
        lock (gate)
        {
            return last;
        }
    }

    private async Task UpdateAsync(AutomationRunStatus status)
    {
        try
        {
            await context.UpdateAsync(run => run with { Status = status }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Automation.ZLogWarning($"status update failed: {exception.Message}");
        }
    }
}

/// <summary>The product <see cref="IAutomationSessions"/>: the catalog, the project registry and the worktree service.</summary>
internal sealed class CatalogAutomationSessions(
    IProjectRegistry projects,
    ISessionCatalog catalog,
    ISessionStore store,
    WorktreeService worktrees,
    IAutomationRunStore state) : IAutomationSessions
{
    internal const string ForkStatePrefix = "fork.session:";

    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken ct) => store.GetSessionAsync(sessionId, ct);

    public async Task<SessionInfo?> CreateScratchAsync(string title, AutomationDefinition definition, string runId, CancellationToken ct)
    {
        var all = await projects.ListAsync(ct).ConfigureAwait(false);
        var general = all.FirstOrDefault(static p => p.Kind == ProjectKind.General);
        if (general is null)
        {
            return null;
        }

        var scratch = WorktreeService.CreateScratchFolder(general.RootPath, title);
        return await catalog.CreateAsync(
                new SessionCreate(scratch, title)
                {
                    ProjectId = general.Id,
                    AutomationId = definition.Id,
                    AutomationRunId = runId,
                },
                ct)
            .ConfigureAwait(false);
    }

    public async Task<SessionInfo?> GetOrCreateForkAsync(string projectId, AutomationDefinition definition, string runId, CancellationToken ct)
    {
        var key = ForkStatePrefix + definition.Id;
        if (await state.GetStateAsync(key, ct).ConfigureAwait(false) is { Length: > 0 } existingId &&
            await store.GetSessionAsync(existingId, ct).ConfigureAwait(false) is { Status: SessionStatus.Active, ParentSessionId: null } existing)
        {
            return existing;
        }

        var all = await projects.ListAsync(ct).ConfigureAwait(false);
        var project = all.FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.Ordinal));
        if (project is null || project.Kind != ProjectKind.Repository)
        {
            return null;
        }

        var worktree = await worktrees.CreateAsync(project.RootPath, definition.Name, ct).ConfigureAwait(false);
        SessionInfo created;
        try
        {
            created = await catalog.CreateAsync(
                    new SessionCreate(worktree.Path, definition.Name)
                    {
                        ProjectId = project.Id,
                        WorktreePath = worktree.Path,
                        Branch = worktree.Branch,
                        AutomationId = definition.Id,
                        AutomationRunId = runId,
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await worktrees.TryRemoveCreatedAsync(worktree, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await state.SetStateAsync(key, created.Id, ct).ConfigureAwait(false);
        return created;
    }
}
