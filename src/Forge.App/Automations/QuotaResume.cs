using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Core.Providers;

namespace Forge.App.Automations;

/// <summary>What a quota-resume run remembers across restarts (automation state, one line per field).</summary>
internal sealed record QuotaNote(string? ProviderId, string Model, DateTimeOffset? ResetAt, DateTimeOffset At, string Title, string Message)
{
    /// <summary>The latest quota failure of one session, written by every failure (also while a run is already active).</summary>
    public const string LatestPrefix = "quota.latest:";

    /// <summary>The model a run waits for, keyed by the run's occurrence.</summary>
    public const string RunPrefix = "quota.run:";

    public string Serialize() => string.Join(
        '\n',
        ProviderId ?? string.Empty,
        Model,
        ResetAt?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        At.UtcTicks.ToString(CultureInfo.InvariantCulture),
        Flatten(Title),
        Flatten(Message));

    public static QuotaNote? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var parts = text.Split('\n');
        if (parts.Length < 6 ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at))
        {
            return null;
        }

        DateTimeOffset? reset = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;
        return new QuotaNote(
            parts[0].Length == 0 ? null : parts[0],
            parts[1],
            reset,
            new DateTimeOffset(at, TimeSpan.Zero),
            parts[4],
            parts[5]);
    }

    public ModelSettings Settings => new(Model, ProviderId: ProviderId);

    private static string Flatten(string text) => text.Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>Spaces resumes of one provider at least <see cref="Gap"/> apart, so a reset does not meet a burst of turns.</summary>
internal sealed class ResumeStagger(TimeProvider time)
{
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, DateTimeOffset> next = new(StringComparer.Ordinal);

    /// <summary>Reserves the provider's next slot and returns how long to wait for it.</summary>
    public TimeSpan Reserve(string providerKey)
    {
        var now = time.GetUtcNow();
        lock (next)
        {
            var slot = next.TryGetValue(providerKey, out var reserved) && reserved > now ? reserved : now;
            next[providerKey] = slot + Gap;
            return slot - now;
        }
    }
}

/// <summary>
/// <see cref="BuiltInAutomations.QuotaResume"/>: waits for the exhausted quota to come back (the provider's reset time plus
/// jitter, or periodic probes when it gave none), then sends "continue" to the session through the mailbox. A turn that hits
/// the quota again puts the run back to waiting, up to <see cref="MaxRounds"/> rounds.
/// </summary>
internal sealed class QuotaResumeRunner(
    ISessionMailbox mailbox,
    IAutomationRunStore state,
    IProviderQuotaProbe probe,
    QuotaProbeSchedule schedule,
    ResumeStagger stagger,
    INotifications? notifications,
    TimeProvider time,
    Func<double>? random = null) : IAutomationRunner
{
    public const int MaxRounds = 5;

    /// <summary>Upper bound of the random delay added to a provider's reset time.</summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(60);

    private readonly Func<double> random = random ?? Random.Shared.NextDouble;

    /// <summary>The reset time a run waits for: the provider's, plus up to <see cref="MaxJitter"/>; null = probe.</summary>
    public DateTimeOffset? ResumeAtFor(DateTimeOffset? resetAt) =>
        resetAt is { } at ? at + TimeSpan.FromSeconds(MaxJitter.TotalSeconds * Math.Clamp(random(), 0, 1)) : null;

    public async Task<AutomationRunOutcome> RunAsync(IAutomationRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sessionId = context.Run.TargetSessionId;
        var note = QuotaNote.Parse(await state.GetStateAsync(QuotaNote.RunPrefix + context.Run.Occurrence, ct).ConfigureAwait(false));
        if (sessionId is null || note is null)
        {
            return AutomationRunOutcome.Failed(AutomationStrings.NoTargetText);
        }

        try
        {
            for (var round = 1; ; round++)
            {
                await WaitForQuotaAsync(context.Run.ResumeAt, note, ct).ConfigureAwait(false);
                var gap = stagger.Reserve(QuotaProbeSchedule.KeyFor(note.ProviderId));
                if (gap > TimeSpan.Zero)
                {
                    await Task.Delay(gap, time, ct).ConfigureAwait(false);
                }

                await context.UpdateAsync(run => run with { Status = AutomationRunStatus.Running, ResumeAt = null }, ct)
                    .ConfigureAwait(false);
                var sentAt = time.GetUtcNow();
                var result = await mailbox.DeliverAsync(
                        new MailboxMessage(
                            sessionId,
                            AutomationStrings.QuotaContinuePrompt,
                            context.Run.Id,
                            AutomationStrings.DisplayName(BuiltInAutomations.QuotaResume, context.Run.AutomationName)),
                        new WaitingProgress(context),
                        ct)
                    .ConfigureAwait(false);
                switch (result.Status)
                {
                    case MailboxStatus.Completed:
                        notifications.Notify(
                            NotificationLevel.Success,
                            AutomationStrings.QuotaResumedTitle,
                            AutomationStrings.QuotaResumedBody(note.Title),
                            "quota-resumed:" + sessionId);
                        return AutomationRunOutcome.Completed();
                    case MailboxStatus.Withdrawn:
                    case MailboxStatus.Cancelled:
                        return AutomationRunOutcome.Cancelled(result.Reason ?? string.Empty);
                }

                // The turn failed: a fresh quota failure of this session (written after the message went out) means the
                // quota ran out again, so the run waits once more; any other failure ends it.
                var latest = QuotaNote.Parse(await state.GetStateAsync(QuotaNote.LatestPrefix + sessionId, ct).ConfigureAwait(false));
                if (latest is null || latest.At < sentAt)
                {
                    return AutomationRunOutcome.Failed(result.Reason ?? string.Empty);
                }

                if (round >= MaxRounds)
                {
                    return AutomationRunOutcome.Failed(AutomationStrings.QuotaAgain(latest.Message));
                }

                note = latest with { Title = note.Title };
                await state.SetStateAsync(QuotaNote.RunPrefix + context.Run.Occurrence, note.Serialize(), ct).ConfigureAwait(false);
                var resumeAt = ResumeAtFor(latest.ResetAt);
                await context.UpdateAsync(
                        run => run with { Status = AutomationRunStatus.WaitingQuota, ResumeAt = resumeAt, Message = AutomationStrings.QuotaAgain(latest.Message) },
                        ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                await state.SetStateAsync(QuotaNote.RunPrefix + context.Run.Occurrence, null, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task WaitForQuotaAsync(DateTimeOffset? resumeAt, QuotaNote note, CancellationToken ct)
    {
        if (resumeAt is { } at)
        {
            var wait = at - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, ct).ConfigureAwait(false);
            }

            return;
        }

        await schedule.WaitUntilAvailableAsync(QuotaProbeSchedule.KeyFor(note.ProviderId), note.Settings, probe, ct)
            .ConfigureAwait(false);
    }
}
