using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Workspace;

namespace Forge.App.Automations;

/// <summary>Outcome of pushing a fix.</summary>
internal sealed record CiPushResult(bool Success, string? Error);

/// <summary>The two git operations the CI chain needs; a seam so tests need no repository.</summary>
internal interface ICiGit
{
    /// <summary>The commit HEAD points at, or null when it cannot be read.</summary>
    Task<string?> HeadAsync(string worktree, CancellationToken ct);

    /// <summary>Pushes local <paramref name="branch"/> to <c>origin</c>; never forces.</summary>
    Task<CiPushResult> PushAsync(string worktree, string branch, CancellationToken ct);
}

internal sealed class GitCliCiGit : ICiGit
{
    private readonly GitCli git;

    public GitCliCiGit(GitCli git)
    {
        this.git = git;
    }

    public Task<string?> HeadAsync(string worktree, CancellationToken ct) => git.RevParseAsync(worktree, "HEAD", ct);

    public async Task<CiPushResult> PushAsync(string worktree, string branch, CancellationToken ct)
    {
        var result = await git.PushAsync(worktree, new GitPushOptions { Remote = "origin", Branch = branch }, ct: ct).ConfigureAwait(false);
        return result.Success ? new CiPushResult(true, null) : new CiPushResult(false, result.Message);
    }
}

/// <summary>What a CI chain run was started for; serialized as <c>key=value</c> lines in <see cref="AutomationRun.Detail"/>.</summary>
internal sealed record CiDetail(int PrNumber, string HeadBranch, string BaseBranch, string HeadOid, string Signature)
{
    public string Serialize() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"pr={PrNumber}\nhead={Clean(HeadBranch)}\nbase={Clean(BaseBranch)}\noid={Clean(HeadOid)}\nsig={Clean(Signature)}");

    public static CiDetail? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var pr = 0;
        string head = string.Empty, baseBranch = string.Empty, oid = string.Empty, sig = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var value = line[(eq + 1)..];
            switch (line[..eq])
            {
                case "pr":
                    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out pr);
                    break;
                case "head":
                    head = value;
                    break;
                case "base":
                    baseBranch = value;
                    break;
                case "oid":
                    oid = value;
                    break;
                case "sig":
                    sig = value;
                    break;
            }
        }

        return pr > 0 ? new CiDetail(pr, head, baseBranch, oid, sig) : null;
    }

    private static string Clean(string value) => value.Replace('\n', ' ').Replace('\r', ' ');
}

internal static class CiState
{
    public static string SeenKey(string sessionId) => "ci.seen:" + sessionId;

    public static string BudgetNotifiedKey(string targetKey) => "ci.budgetNotified:" + targetKey;

    public static string LastFixKey(string targetKey) => "ci.lastFix:" + targetKey;

    /// <summary>The failed checks as one sorted, comma-joined string; empty when none.</summary>
    public static string Signature(IReadOnlyList<PrCheck> failed)
    {
        var names = new string[failed.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = failed[i].Name;
        }

        Array.Sort(names, StringComparer.Ordinal);
        return string.Join(", ", names);
    }

    /// <summary>Checks whose bucket is fail or cancel.</summary>
    public static List<PrCheck> Failed(PrChecksSnapshot snapshot)
    {
        var failed = new List<PrCheck>();
        foreach (var check in snapshot.Checks)
        {
            if (check.Bucket is PrCheckBucket.Fail or PrCheckBucket.Cancel)
            {
                failed.Add(check);
            }
        }

        return failed;
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 6);
}

/// <summary>
/// Polls the open pull requests of active branch sessions and starts <see cref="BuiltInAutomations.CiChain"/> when a new
/// set of checks failed. A watermark per session (<c>head|signature</c>) keeps one failure from starting a second run.
/// </summary>
internal sealed class CiFailureWatcher
{
    internal const int MaxAttempts = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IAutomationEngine engine;
    private readonly ISessionCatalog catalog;
    private readonly IPrPlatform pr;
    private readonly IAutomationRunStore state;
    private readonly TimeProvider time;
    private readonly Func<bool> enabled;
    private readonly INotifications? notifications;

    public CiFailureWatcher(
        IAutomationEngine engine,
        ISessionCatalog catalog,
        IPrPlatform pr,
        IAutomationRunStore state,
        TimeProvider time,
        Func<bool> enabled,
        INotifications? notifications = null)
    {
        this.engine = engine;
        this.catalog = catalog;
        this.pr = pr;
        this.state = state;
        this.time = time;
        this.enabled = enabled;
        this.notifications = notifications;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine("[ci] poll failed: " + exception.Message);
            }

            try
            {
                await Task.Delay(PollInterval, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        if (!enabled())
        {
            return;
        }

        var sessions = await catalog
            .QueryAsync(new SessionQuery { Status = SessionStatus.Active, ParentSessionId = string.Empty }, ct)
            .ConfigureAwait(false);
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (session.ParentSessionId is not null
                || session.Status != SessionStatus.Active
                || string.IsNullOrEmpty(session.WorktreePath))
            {
                continue;
            }

            try
            {
                await PollSessionAsync(session, session.WorktreePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine($"[ci] session {session.Id} poll failed: {exception.Message}");
            }
        }
    }

    private async Task PollSessionAsync(SessionInfo session, string worktree, CancellationToken ct)
    {
        var view = await pr.ViewPullRequestAsync(worktree, null, ct).ConfigureAwait(false);
        if (!view.Success || view.Value is not { State: PrState.Open } info)
        {
            return;
        }

        var checks = await pr.GetChecksAsync(worktree, info.Number, ct).ConfigureAwait(false);
        if (!checks.Success || checks.Value is not { } snapshot || snapshot.NoneReported)
        {
            return;
        }

        var targetKey = $"ci:{session.Id}#{info.Number}";
        var seenKey = CiState.SeenKey(session.Id);
        var failed = CiState.Failed(snapshot);
        var pending = snapshot.Count(PrCheckBucket.Pending);
        if (failed.Count == 0)
        {
            if (pending == 0)
            {
                await state.SetStateAsync(seenKey, null, ct).ConfigureAwait(false);
                await state.SetStateAsync(CiState.BudgetNotifiedKey(targetKey), null, ct).ConfigureAwait(false);
                await engine.ResetTargetBudgetAsync(targetKey, ct).ConfigureAwait(false);
            }

            return;
        }

        if (pending > 0)
        {
            return; // wait until every check has finished, so one fix covers the whole picture
        }

        var signature = CiState.Signature(failed);
        var watermark = info.HeadRefOid + "|" + signature;
        if (string.Equals(await state.GetStateAsync(seenKey, ct).ConfigureAwait(false), watermark, StringComparison.Ordinal))
        {
            return;
        }

        var detail = new CiDetail(info.Number, info.HeadRefName, info.BaseRefName, info.HeadRefOid, signature);
        var request = new AutomationRunRequest(BuiltInAutomations.CiChain, AutomationTriggerSource.Event)
        {
            Occurrence = $"ci:{session.Id}:{info.HeadRefOid}:{CiState.Hash(signature)}",
            TargetSessionId = session.Id,
            TargetKey = targetKey,
            MaxTargetAttempts = MaxAttempts,
            Detail = detail.Serialize(),
        };
        var result = await engine.StartAsync(request, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case AutomationStartStatus.Started:
            case AutomationStartStatus.Duplicate:
                await state.SetStateAsync(seenKey, watermark, ct).ConfigureAwait(false);
                break;
            case AutomationStartStatus.BudgetExhausted:
                await state.SetStateAsync(seenKey, watermark, ct).ConfigureAwait(false);
                var flag = CiState.BudgetNotifiedKey(targetKey);
                if (await state.GetStateAsync(flag, ct).ConfigureAwait(false) is null)
                {
                    await state.SetStateAsync(flag, "1", ct).ConfigureAwait(false);
                    notifications.Notify(
                        NotificationLevel.Warning,
                        AutomationStrings.CiStoppedTitle,
                        LocText.Literal(AutomationStrings.CiStoppedBudget(MaxAttempts.ToString(CultureInfo.InvariantCulture))),
                        key: flag);
                }

                break;
            default:
                // Busy, too deep, ...: leave the watermark so the next poll tries again.
                break;
        }
    }
}

/// <summary>
/// One CI chain run: Scout reads the failed job logs, the main session fixes and commits, and the fix is pushed to the
/// PR branch (never the base branch, never when auto-push is off for the project).
/// </summary>
internal sealed class CiChainRunner : IAutomationRunner
{
    internal const int MaxLogLines = 200;
    internal const int MaxJobChars = 12_000;
    internal const int MaxTotalChars = 40_000;

    private readonly IPrPlatform pr;
    private readonly ICiGit git;
    private readonly ISessionStore sessions;
    private readonly ISessionMailbox mailbox;
    private readonly IChildAgentLauncher launcher;
    private readonly INotifications? notifications;
    private readonly Func<AutomationConfig> config;
    private readonly IAutomationRunStore state;

    public CiChainRunner(
        IPrPlatform pr,
        ICiGit git,
        ISessionStore sessions,
        ISessionMailbox mailbox,
        IChildAgentLauncher launcher,
        INotifications? notifications,
        Func<AutomationConfig> config,
        IAutomationRunStore state)
    {
        this.pr = pr;
        this.git = git;
        this.sessions = sessions;
        this.mailbox = mailbox;
        this.launcher = launcher;
        this.notifications = notifications;
        this.config = config;
        this.state = state;
    }

    public async Task<AutomationRunOutcome> RunAsync(IAutomationRunContext context, CancellationToken ct)
    {
        var run = context.Run;
        var detail = CiDetail.Parse(run.Detail);
        if (detail is null || run.TargetSessionId is null)
        {
            return AutomationRunOutcome.Skipped(AutomationStrings.NoTargetText);
        }

        var session = await sessions.GetSessionAsync(run.TargetSessionId, ct).ConfigureAwait(false);
        if (session is not { Status: SessionStatus.Active } || string.IsNullOrEmpty(session.WorktreePath))
        {
            return AutomationRunOutcome.Skipped(AutomationStrings.NoTargetText);
        }

        var worktree = session.WorktreePath;
        var checks = await pr.GetChecksAsync(worktree, detail.PrNumber, ct).ConfigureAwait(false);
        var failed = checks.Success && checks.Value is { } snapshot ? CiState.Failed(snapshot) : [];
        if (failed.Count == 0)
        {
            return AutomationRunOutcome.Skipped(AutomationStrings.CiChecksGone);
        }

        var signature = CiState.Signature(failed);
        var prText = detail.PrNumber.ToString(CultureInfo.InvariantCulture);
        var failures = await BuildFailureReportAsync(worktree, failed, ct).ConfigureAwait(false);

        notifications.Notify(
            NotificationLevel.Warning,
            AutomationStrings.CiFailedTitle,
            AutomationStrings.CiAnalysingBody(prText, session.Title, failed.Count.ToString(CultureInfo.InvariantCulture)),
            key: "ci.failed:" + run.Id);

        var scout = await launcher
            .LaunchAsync(new ChildAgentRequest(session.Id, "Scout", AutomationStrings.CiScoutTask(prText, detail.HeadBranch, failures)), ct)
            .ConfigureAwait(false);
        if (scout.IsError)
        {
            return AutomationRunOutcome.Failed(scout.Text);
        }

        var before = await git.HeadAsync(worktree, ct).ConfigureAwait(false);
        var message = new MailboxMessage(
            session.Id,
            AutomationStrings.CiFixPrompt(prText, scout.Text),
            run.Id,
            AutomationStrings.DisplayName(BuiltInAutomations.CiChain, run.AutomationName));
        var delivered = await mailbox
            .DeliverAsync(message, new StatusProgress(context), ct)
            .ConfigureAwait(false);
        switch (delivered.Status)
        {
            case MailboxStatus.Withdrawn:
            case MailboxStatus.Cancelled:
                return AutomationRunOutcome.Cancelled(delivered.Reason ?? string.Empty);
            case MailboxStatus.TurnFailed:
                return AutomationRunOutcome.Failed(delivered.Reason ?? string.Empty);
        }

        var after = await git.HeadAsync(worktree, ct).ConfigureAwait(false);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            notifications.Notify(
                NotificationLevel.Info,
                AutomationStrings.CiFailedTitle,
                AutomationStrings.CiNoFixBody(session.Title),
                key: "ci.nofix:" + run.Id);
            return AutomationRunOutcome.Completed();
        }

        var targetKey = run.TargetKey ?? $"ci:{session.Id}#{detail.PrNumber}";
        var lastFixKey = CiState.LastFixKey(targetKey);
        if (string.Equals(await state.GetStateAsync(lastFixKey, ct).ConfigureAwait(false), signature, StringComparison.Ordinal))
        {
            Stopped(run.Id, AutomationStrings.CiStoppedSameFailure);
            return AutomationRunOutcome.Failed(AutomationStrings.CiStoppedSameFailure);
        }

        var branch = detail.HeadBranch;
        if (string.IsNullOrWhiteSpace(branch)
            || string.Equals(branch, detail.BaseBranch, StringComparison.Ordinal))
        {
            var text = AutomationStrings.CiStoppedBaseBranch(detail.BaseBranch);
            Stopped(run.Id, text);
            return AutomationRunOutcome.Failed(text);
        }

        if (session.ProjectId is { } project && Contains(config().CiAutoPushOffProjects, project))
        {
            notifications.Notify(
                NotificationLevel.Info,
                AutomationStrings.CiFailedTitle,
                AutomationStrings.CiNotPushedBody(session.Title),
                key: "ci.notpushed:" + run.Id);
            return AutomationRunOutcome.Completed();
        }

        var push = await git.PushAsync(worktree, branch, ct).ConfigureAwait(false);
        if (!push.Success)
        {
            var text = AutomationStrings.CiStoppedPushFailed(push.Error ?? string.Empty);
            Stopped(run.Id, text);
            return AutomationRunOutcome.Failed(text);
        }

        await state.SetStateAsync(lastFixKey, signature, ct).ConfigureAwait(false);
        notifications.Notify(
            NotificationLevel.Success,
            AutomationStrings.CiPushedTitle,
            AutomationStrings.CiPushedBody(branch),
            key: "ci.pushed:" + run.Id);
        return AutomationRunOutcome.Completed();
    }

    /// <summary>Keeps the last <paramref name="maxChars"/> characters, then the last <paramref name="maxLines"/> lines.</summary>
    internal static string Tail(string text, int maxLines, int maxChars)
    {
        var s = text.Length > maxChars ? text[^maxChars..] : text;
        var pos = s.Length;
        if (pos > 0 && s[pos - 1] == '\n')
        {
            pos--;
        }

        for (var n = 0; n < maxLines; n++)
        {
            pos = pos > 0 ? s.LastIndexOf('\n', pos - 1) : -1;
            if (pos < 0)
            {
                return s;
            }
        }

        return s[(pos + 1)..];
    }

    private void Stopped(string runId, string text) =>
        notifications.Notify(
            NotificationLevel.Warning,
            AutomationStrings.CiStoppedTitle,
            LocText.Literal(text),
            key: "ci.stopped:" + runId);

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        foreach (var item in list)
        {
            if (string.Equals(item, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> BuildFailureReportAsync(string worktree, List<PrCheck> failed, CancellationToken ct)
    {
        var report = new StringBuilder();
        foreach (var check in failed)
        {
            ct.ThrowIfCancellationRequested();
            report.Append("### ").Append(check.Name);
            if (check.Description.Length > 0)
            {
                report.Append(" - ").Append(check.Description);
            }

            report.Append('\n');
            if (check.JobId is { } jobId && report.Length < MaxTotalChars)
            {
                string body;
                var log = await pr.GetFailedJobLogAsync(worktree, check.RunId, jobId, ct).ConfigureAwait(false);
                if (log.Success && log.Value is { } logText)
                {
                    body = Tail(logText.Text, MaxLogLines, MaxJobChars);
                }
                else
                {
                    body = AutomationStrings.CiLogUnavailable(log.Status.Message);
                }

                var room = MaxTotalChars - report.Length;
                if (body.Length > room)
                {
                    body = body[^room..];
                }

                report.Append(body).Append('\n');
            }

            report.Append('\n');
        }

        return report.ToString();
    }

    private sealed class StatusProgress : IProgress<bool>
    {
        private readonly IAutomationRunContext context;

        public StatusProgress(IAutomationRunContext context)
        {
            this.context = context;
        }

        public void Report(bool waiting)
        {
            var status = waiting ? AutomationRunStatus.WaitingIdle : AutomationRunStatus.Running;
            _ = Update(status);
        }

        private async Task Update(AutomationRunStatus status)
        {
            try
            {
                await context.UpdateAsync(run => run with { Status = status }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine("[ci] status update failed: " + exception.Message);
            }
        }
    }
}
