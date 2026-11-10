using Forge.Abstractions;
using Forge.App.Approval;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Tests.Approval;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// The seams that turn a business event into one notification: an approval card that starts waiting announces itself
/// through <see cref="INotifications"/>, and the fire-and-forget helper never lets a notification failure reach the work
/// that wanted to notify. The notification centre itself is covered by <see cref="NotificationCenterTests"/>.
/// </summary>
public sealed class NotificationWiringTests
{
    [Fact]
    public async Task An_approval_card_that_starts_waiting_is_announced_once()
    {
        var dir = ApprovalFixtures.TempDir("forge-notify-approval-");
        try
        {
            await using var rules = new ApprovalRules(Path.Combine(dir, ApprovalRules.FileName), debounceMilliseconds: 0);
            var queue = new ApprovalQueue(InlineDispatcher.Instance);
            var notifications = new RecordingNotifications();
            using var handler = new SessionApprovalHandler(
                queue,
                rules,
                ApprovalFixtures.Session("s1", "p1"),
                notifications);

            var cardTask = ApprovalFixtures.NextToolCardAsync(queue);
            var pending = handler.RequestAsync(ApprovalFixtures.ToolRequest(), CancellationToken.None);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

            var call = Assert.Single(notifications.Calls);
            Assert.Equal(NotificationLevel.Warning, call.Level);
            Assert.Equal(NotificationsStrings.ApprovalWaitingTitleKey, call.TitleKey);
            Assert.Equal(NotificationsStrings.ApprovalWaitingBody, call.BodyKey);
            Assert.StartsWith("approval:", call.Key, StringComparison.Ordinal);

            // The card is still the thing that answers; the announcement does not decide anything.
            card.DenyCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Deny, await pending.AsTask().WaitAsync(ApprovalFixtures.StepTimeout));
            Assert.Single(notifications.Calls);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public void The_fire_and_forget_helper_swallows_a_failing_notification()
    {
        var notifications = new RecordingNotifications { Failure = new InvalidOperationException("notify-boom") };

        notifications.Notify(
            NotificationLevel.Error,
            LocText.Literal("title"),
            LocText.Literal("body"));

        // Reaching this line is the assertion: a notification that throws must not surface in the caller.
        Assert.True(true);
    }

    [Fact]
    public void The_fire_and_forget_helper_accepts_a_missing_notification_entry() => ((INotifications?)null).Notify(
        NotificationLevel.Info,
        LocText.Literal("title"),
        LocText.Literal("body"));

    /// <summary>Records what a call site announced; a shell built without the wiring simply has no such object.</summary>
    private sealed class RecordingNotifications : INotifications
    {
        private readonly List<(NotificationLevel Level, string? TitleKey, string? BodyKey, string? Key)> calls = [];

        public IReadOnlyList<(NotificationLevel Level, string? TitleKey, string? BodyKey, string? Key)> Calls => calls;

        /// <summary>When set, <see cref="NotifyAsync"/> throws it instead of recording.</summary>
        public Exception? Failure { get; init; }

        public Task NotifyAsync(
            NotificationLevel level,
            LocText title,
            LocText body,
            string? key = null,
            CancellationToken ct = default)
        {
            if (Failure is { } failure)
            {
                return Task.FromException(failure);
            }

            calls.Add((level, title.Key, body.Key, key));
            return Task.CompletedTask;
        }
    }
}
