using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.HostClient;

namespace Forge.App.Approval;

/// <summary>What the extensions settings section needs to know about the host connection; a test can fake it.</summary>
internal interface IExtensionHostState
{
    bool HasHost { get; }

    /// <summary>Why the host is missing entirely (the executable was not found), or null.</summary>
    string? HostProblem { get; }

    HostStatus Status { get; }

    /// <summary>The status bar's sentence for the current status, when the supervisor has one.</summary>
    string? StatusDetail { get; }

    /// <summary>Raised when the host reported a status change; any thread.</summary>
    event Action? Changed;
}

/// <summary>
/// The shell's approval wiring: the remembered rules, the queue, the per-session handlers, the agent-facing extension
/// authoring service and the extensions settings section. The composition root creates exactly one instance (before the
/// window, so the queue overlay can resolve it from its own constructor) and tears it down last.
/// <para>Threading: construction happens on the UI thread; the queue marshals its own work; the host's status events arrive
/// on whatever thread produced them and only start background work here. A restored pending draft is re-queued when the
/// host reports <see cref="HostStatus.Running"/>, so approvals submitted before a restart become cards again.</para>
/// </summary>
internal sealed class ApprovalServices : IAsyncDisposable, IExtensionHostState
{
    private readonly ExtensionHostSupervisor? host;
    private readonly LocText hostProblem;
    private readonly Lock gate = new();
    private bool disposed;
    private int restoring;

    private ApprovalServices(AppPaths paths, ExtensionHostSupervisor? host, LocText hostProblem)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Rules = new ApprovalRules(Path.Combine(paths.UserDataDirectory, ApprovalRules.FileName));
        Queue = new ApprovalQueue();
        Sessions = new ApprovalSessionDirectory();
        this.host = host;
        this.hostProblem = hostProblem;

        if (host is not null)
        {
            Extensions = new ExtensionAuthoringService(
                host.Client,
                host.Client,
                ToolNamesOf,
                Queue,
                Rules,
                Sessions);
            host.StatusChanged += OnHostStatusChanged;
        }

        QueueViewModel = new ApprovalQueueViewModel(Queue);
        SettingsSection = new ExtensionsSettingsSectionViewModel(
            Extensions,
            this,
            Rules,
            OpenPendingCardAsync,
            Queue.Dispatcher);

        // The rules are read once in the background at startup; a handler that needs them awaits the same task.
        _ = Rules.LoadAsync(CancellationToken.None);
        if (host is not null && host.Status == HostStatus.Running)
        {
            RestorePendingOnce();
        }

        Current = this;
    }

    /// <summary>
    /// The instance the window overlay resolves from its own constructor. The queue overlay is a view of the shell, and the
    /// root view model is not a feature's to extend, so this one static reference is the seam between them. It is set once
    /// per composition root and cleared on dispose.
    /// </summary>
    public static ApprovalServices? Current { get; private set; }

    public ApprovalRules Rules { get; }

    public ApprovalQueue Queue { get; }

    public ApprovalSessionDirectory Sessions { get; }

    /// <summary>Null when no extension host was found; the settings section then says so instead of showing an empty list.</summary>
    public ExtensionAuthoringService? Extensions { get; }

    public ApprovalQueueViewModel QueueViewModel { get; }

    /// <summary>The <c>extensions</c> settings section body.</summary>
    public ExtensionsSettingsSectionViewModel SettingsSection { get; }

    public bool HasHost => host is not null;

    public string? HostProblem => hostProblem.IsEmpty ? null : hostProblem.Resolve();

    public HostStatus Status => host?.Status ?? HostStatus.Stopped;

    public string? StatusDetail => host?.StatusDetail?.Resolve();

    /// <summary>Raised on the host's thread when the connection status changed.</summary>
    public event Action? Changed;

    public static ApprovalServices Create(AppPaths paths, ExtensionHostSupervisor? host, LocText hostProblem) =>
        new(paths, host, hostProblem);

    /// <summary>Registers the session title and returns the handler the session's host injects.</summary>
    public SessionApprovalHandler CreateHandler(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Sessions.Register(session.Id, session.Title);
        return new SessionApprovalHandler(Queue, Rules, session);
    }

    /// <summary>
    /// Opens the approval card of one staged draft: an already queued card is revealed, otherwise the draft is re-queued as
    /// a "submitted before the restart" card. False when there is nothing to show (no such draft, or no host).
    /// </summary>
    public async Task<bool> OpenPendingCardAsync(string pluginId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return false;
        }

        if (await Queue.ContainsAsync("extension:" + pluginId).ConfigureAwait(false))
        {
            await Queue.Dispatcher.InvokeAsync(QueueViewModel.Expand).ConfigureAwait(false);
            return true;
        }

        if (Extensions is null)
        {
            return false;
        }

        try
        {
            var staged = await Extensions.FindPendingAsync(pluginId, ct).ConfigureAwait(false);
            if (staged is null)
            {
                return false;
            }

            if (!await Extensions.EnqueueRestoredCardAsync(staged, ct).ConfigureAwait(false))
            {
                return false;
            }

            await Queue.Dispatcher.InvokeAsync(QueueViewModel.Expand).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.WriteLine("[approval] the pending draft could not be opened: " + exception.Message);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        if (host is not null)
        {
            host.StatusChanged -= OnHostStatusChanged;
        }

        SettingsSection.Dispose();
        QueueViewModel.Dispose();

        // Every waiter sees a denial instead of hanging on a card nobody can click any more. Tearing the graph down may
        // happen where no dispatcher loop runs (a test host, the very last shutdown step), so the withdrawal is queued
        // rather than awaited when this is not the UI thread.
        if (Queue.Dispatcher.CheckAccess())
        {
            await Queue.WithdrawAllAsync().ConfigureAwait(false);
        }
        else
        {
            Queue.Dispatcher.Post(() => _ = Queue.WithdrawAllAsync());
        }

        await Rules.DisposeAsync().ConfigureAwait(false);
    }

    private void OnHostStatusChanged(HostStatus status, LocText? detail)
    {
        RaiseChanged();
        if (status == HostStatus.Running)
        {
            RestorePendingOnce();
        }
    }

    private void RestorePendingOnce()
    {
        if (Interlocked.Exchange(ref restoring, 1) == 1)
        {
            return;
        }

        _ = Task.Run(RestorePendingAsync);
    }

    private async Task RestorePendingAsync()
    {
        try
        {
            if (Extensions is null)
            {
                return;
            }

            await Extensions.RestorePendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[approval] the staged extensions could not be restored: " + exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref restoring, 0);
        }
    }

    /// <summary>The live tool names of a plugin, so an approval can clear the rules remembered for the code it replaces.</summary>
    private IReadOnlyList<string> ToolNamesOf(string pluginId)
    {
        if (host is null || !host.Client.ToolsByPlugin.TryGetValue(pluginId, out var tools))
        {
            return [];
        }

        var names = new string[tools.Count];
        for (var index = 0; index < tools.Count; index++)
        {
            names[index] = tools[index].Definition.Name;
        }

        return names;
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[approval] a host-state subscriber failed: " + exception.Message);
        }
    }
}
