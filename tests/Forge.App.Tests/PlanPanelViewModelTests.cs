using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Pins the plan panel's read-only projection of the store (forge-app spec, "计划面板(W7)"): the row structure and
/// ordering (groups by order, steps by order, the loose section last, a missing group falling back to it), the
/// next-step rule, in-place row patching with per-field notifications, the summary and its revision preview, the
/// hand-off message and outcomes, the export, and the refresh/dispose semantics - coalescing one follow-up read
/// while a read runs, no reads after dispose, and a read that finishes after dispose landing nowhere.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PlanPanelViewModelTests
{
    private const string SessionId = "s1";

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The row properties that carry a stored field; a patch must not raise these when the field did not change.</summary>
    private static readonly string[] ValueFields =
    [
        nameof(PlanRowViewModel.Title),
        nameof(PlanRowViewModel.Status),
        nameof(PlanRowViewModel.IsDone),
        nameof(PlanRowViewModel.IsInProgress),
        nameof(PlanRowViewModel.IsPaused),
        nameof(PlanRowViewModel.IsPending),
        nameof(PlanRowViewModel.Note),
        nameof(PlanRowViewModel.HasNote),
        nameof(PlanRowViewModel.Summary),
        nameof(PlanRowViewModel.SummaryText),
        nameof(PlanRowViewModel.HasSummary),
        nameof(PlanRowViewModel.Verification),
        nameof(PlanRowViewModel.VerificationText),
        nameof(PlanRowViewModel.HasVerification),
        nameof(PlanRowViewModel.CommitId),
        nameof(PlanRowViewModel.HasCommitId),
        nameof(PlanRowViewModel.CommitError),
        nameof(PlanRowViewModel.HasCommitError),
        nameof(PlanRowViewModel.IsNext),
        nameof(PlanRowViewModel.Done),
        nameof(PlanRowViewModel.Total),
        nameof(PlanRowViewModel.ProgressText),
        nameof(PlanRowViewModel.HasProgress),
    ];

    // ---------------------------------------------------------------- A1 rows and ordering

    [Fact]
    public async Task Rows_follow_group_order_then_step_order_with_the_loose_section_last()
    {
        var store = new FakePlanStore();
        store.SeedGroup(new TodoGroup("g-early", SessionId, "先组", 0, At));
        store.SeedGroup(new TodoGroup("g-early-b", SessionId, "先组乙", 0, At)); // same order: id breaks the tie
        store.SeedGroup(new TodoGroup("g-late", SessionId, "后组", 1, At));
        store.SeedGroup(new TodoGroup("g-empty", SessionId, "空组", 2, At)); // no steps: no header row
        store.Seed(new TodoItem("a", SessionId, "先组一步", TodoStatus.Pending, null, 0, At, GroupId: "g-early"));
        store.Seed(new TodoItem("b", SessionId, "先组二步", TodoStatus.Pending, null, 1, At, GroupId: "g-early"));
        store.Seed(new TodoItem("x", SessionId, "乙组一步", TodoStatus.Pending, null, 0, At, GroupId: "g-early-b"));
        store.Seed(new TodoItem("c", SessionId, "后组一步", TodoStatus.Done, null, 0, At, GroupId: "g-late"));
        store.Seed(new TodoItem("d", SessionId, "后组二步", TodoStatus.InProgress, null, 1, At, GroupId: "g-late"));
        store.Seed(new TodoItem("f", SessionId, "孤儿步骤", TodoStatus.Pending, null, 0, At, GroupId: "g-missing"));
        store.Seed(new TodoItem("e", SessionId, "散项", TodoStatus.Pending, null, 1, At));

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 11);
            try
            {
                Assert.Equal(
                    ["group:g-early", "step:a", "step:b", "group:g-early-b", "step:x", "group:g-late", "step:c", "step:d", "group:loose", "step:f", "step:e"],
                    panel.Rows.Select(static row => row.Key).ToList());

                var early = panel.Rows[0];
                Assert.True(early.IsGroupRow);
                Assert.False(early.IsLooseSection);
                Assert.Equal("先组", early.Title);
                Assert.Equal(0, early.Done);
                Assert.Equal(2, early.Total);
                Assert.Equal("0/2", early.ProgressText);
                Assert.Equal(TodoStatus.Pending, early.Status);

                var late = panel.Rows[5];
                Assert.Equal("后组", late.Title);
                Assert.Equal(1, late.Done);
                Assert.Equal(2, late.Total);
                Assert.Equal("1/2", late.ProgressText);
                Assert.Equal(TodoStatus.InProgress, late.Status);

                // The step of a group that no longer exists fell back to the loose section instead of vanishing.
                var loose = panel.Rows[8];
                Assert.True(loose.IsGroupRow);
                Assert.True(loose.IsLooseSection);
                Assert.Equal(Loc.Instance[PlanStrings.OtherSteps], loose.Title);
                Assert.Equal(0, loose.Done);
                Assert.Equal(2, loose.Total);

                Assert.Equal("先组", panel.Rows[1].GroupTitle);
                Assert.Null(panel.Rows[9].GroupTitle);
                Assert.False(panel.Rows[9].HasGroupTitle);
                Assert.True(panel.Rows[9].IsStepRow);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- A2 next step

    [Fact]
    public async Task Next_step_is_the_in_progress_step_even_after_a_pending_one()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("p", SessionId, "先等待", TodoStatus.Pending, null, 0, At));
        store.Seed(new TodoItem("i", SessionId, "进行中", TodoStatus.InProgress, null, 1, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 3);
            try
            {
                Assert.Equal("step:i", panel.NextStep?.Key);
                Assert.True(panel.HasNextStep);
                Assert.True(panel.Rows.Single(static row => row.Key == "step:i").IsNext);
                Assert.False(panel.Rows.Single(static row => row.Key == "step:p").IsNext);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task Next_step_falls_back_to_the_first_pending_step_and_skips_paused()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("z", SessionId, "暂停的", TodoStatus.Paused, null, 0, At));
        store.Seed(new TodoItem("p", SessionId, "等待一", TodoStatus.Pending, null, 1, At));
        store.Seed(new TodoItem("q", SessionId, "等待二", TodoStatus.Pending, null, 2, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 4);
            try
            {
                Assert.Equal("step:p", panel.NextStep?.Key);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task Next_step_is_none_when_only_paused_or_done_steps_remain()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("z", SessionId, "暂停的", TodoStatus.Paused, null, 0, At));
        store.Seed(new TodoItem("d", SessionId, "完成的", TodoStatus.Done, null, 1, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 3);
            try
            {
                Assert.Null(panel.NextStep);
                Assert.False(panel.HasNextStep);

                // Once the paused step is done too, still nothing to work on.
                store.Seed(store.Items().Single(static item => item.Id == "z") with { Status = TodoStatus.Done });
                await panel.RefreshAsync(CancellationToken.None);
                Assert.Null(panel.NextStep);
                Assert.False(panel.HasNextStep);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- A3 in-place patching

    [Fact]
    public async Task A_step_finishing_patches_its_row_in_place_notifying_only_the_changed_fields()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "摘要", At);
        store.SeedGroup(new TodoGroup("g1", SessionId, "分组甲", 0, At));
        store.Seed(new TodoItem("t1", SessionId, "第一步", TodoStatus.Pending, null, 0, At, GroupId: "g1"));
        store.Seed(new TodoItem("t2", SessionId, "第二步", TodoStatus.Pending, null, 1, At, GroupId: "g1"));
        store.Seed(new TodoItem("t3", SessionId, "第三步", TodoStatus.Done, null, 2, At, GroupId: "g1"));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 4);
            try
            {
                var before = panel.Rows.ToDictionary(static row => row.Key, static row => row);
                var notes = panel.Rows.ToDictionary(static row => row.Key, static _ => new List<string>());
                foreach (var row in panel.Rows)
                {
                    var mine = notes[row.Key];
                    row.PropertyChanged += (_, e) => mine.Add(e.PropertyName ?? string.Empty);
                }

                store.Seed(
                    store.Items().Single(static item => item.Id == "t2") with
                    {
                        Status = TodoStatus.Done,
                        Summary = "改了布局",
                        Verification = "测试通过",
                        CommitId = "abcdef1234567",
                    });
                await panel.RefreshAsync(CancellationToken.None);

                // Same key, same instance: the row was patched, not rebuilt.
                foreach (var (key, row) in before)
                {
                    Assert.Same(row, panel.Rows.Single(candidate => candidate.Key == key));
                }

                // The step that finished reports its new fields; nothing it kept is re-raised.
                var changed = notes["step:t2"];
                foreach (var name in new[]
                         {
                             nameof(PlanRowViewModel.Status),
                             nameof(PlanRowViewModel.IsDone),
                             nameof(PlanRowViewModel.IsInProgress),
                             nameof(PlanRowViewModel.IsPaused),
                             nameof(PlanRowViewModel.IsPending),
                             nameof(PlanRowViewModel.StatusName),
                             nameof(PlanRowViewModel.CanHandoff),
                             nameof(PlanRowViewModel.CommitId),
                             nameof(PlanRowViewModel.HasCommitId),
                             nameof(PlanRowViewModel.Summary),
                             nameof(PlanRowViewModel.SummaryText),
                             nameof(PlanRowViewModel.HasSummary),
                             nameof(PlanRowViewModel.Verification),
                             nameof(PlanRowViewModel.VerificationText),
                             nameof(PlanRowViewModel.HasVerification),
                         })
                {
                    Assert.Contains(name, changed);
                }

                Assert.DoesNotContain(nameof(PlanRowViewModel.Title), changed);
                Assert.DoesNotContain(nameof(PlanRowViewModel.Note), changed);
                Assert.DoesNotContain(nameof(PlanRowViewModel.IsNext), changed);
                Assert.DoesNotContain(nameof(PlanRowViewModel.CommitError), changed);
                Assert.Equal("abcdef1", panel.Rows.Single(static row => row.Key == "step:t2").CommitShort);

                // The untouched step row and the finished third step raised no field notification at all.
                AssertNoFieldNotifications(notes, "step:t1");
                AssertNoFieldNotifications(notes, "step:t3");

                // The group row only re-reports its step count; its title and derived status did not change.
                var group = notes["group:g1"];
                Assert.Equal(
                    [nameof(PlanRowViewModel.Done), nameof(PlanRowViewModel.Total), nameof(PlanRowViewModel.ProgressText), nameof(PlanRowViewModel.HasProgress)],
                    group.Where(ValueFields.Contains).ToList());
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task An_in_progress_step_finishing_moves_the_next_mark_to_the_following_step()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("t1", SessionId, "进行中", TodoStatus.InProgress, null, 0, At));
        store.Seed(new TodoItem("t2", SessionId, "等待中", TodoStatus.Pending, null, 1, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 3);
            try
            {
                var before = panel.Rows.ToDictionary(static row => row.Key, static row => row);
                var notes = panel.Rows.ToDictionary(static row => row.Key, static _ => new List<string>());
                foreach (var row in panel.Rows)
                {
                    var mine = notes[row.Key];
                    row.PropertyChanged += (_, e) => mine.Add(e.PropertyName ?? string.Empty);
                }

                store.Seed(store.Items().Single(static item => item.Id == "t1") with { Status = TodoStatus.Done, CommitId = "cafebabe" });
                await panel.RefreshAsync(CancellationToken.None);

                Assert.Same(before["step:t1"], panel.Rows.Single(static row => row.Key == "step:t1"));
                Assert.Same(before["step:t2"], panel.Rows.Single(static row => row.Key == "step:t2"));
                Assert.Equal("step:t2", panel.NextStep?.Key);
                Assert.Contains(nameof(PlanRowViewModel.IsNext), notes["step:t1"]);
                Assert.Contains(nameof(PlanRowViewModel.IsNext), notes["step:t2"]);
                Assert.False(panel.Rows.Single(static row => row.Key == "step:t1").IsNext);
                Assert.True(panel.Rows.Single(static row => row.Key == "step:t2").IsNext);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_group_that_lost_all_its_steps_disappears_keeping_the_other_rows()
    {
        var store = new FakePlanStore();
        store.SeedGroup(new TodoGroup("g1", SessionId, "留下", 0, At));
        store.SeedGroup(new TodoGroup("g2", SessionId, "整组消失", 1, At));
        store.Seed(new TodoItem("t1", SessionId, "留下的步骤", TodoStatus.Pending, null, 0, At, GroupId: "g1"));
        store.Seed(new TodoItem("t2", SessionId, "要删的步骤", TodoStatus.Pending, null, 0, At, GroupId: "g2"));
        store.Seed(new TodoItem("t3", SessionId, "散项", TodoStatus.Pending, null, 0, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 6);
            try
            {
                var before = panel.Rows.ToDictionary(static row => row.Key, static row => row);
                store.Remove("t2");
                store.RemoveGroup("g2");
                await panel.RefreshAsync(CancellationToken.None);

                Assert.Equal(["group:g1", "step:t1", "group:loose", "step:t3"], panel.Rows.Select(static row => row.Key).ToList());
                foreach (var row in panel.Rows)
                {
                    Assert.Same(before[row.Key], row);
                }
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_row_that_moved_keeps_its_instance_at_the_new_position()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("x", SessionId, "甲", TodoStatus.Pending, null, 0, At));
        store.Seed(new TodoItem("y", SessionId, "乙", TodoStatus.Pending, null, 1, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 3);
            try
            {
                Assert.Equal(["group:loose", "step:x", "step:y"], panel.Rows.Select(static row => row.Key).ToList());
                var x = panel.Rows[1];
                var y = panel.Rows[2];

                store.Seed(store.Items().Single(static item => item.Id == "x") with { Order = 1 });
                store.Seed(store.Items().Single(static item => item.Id == "y") with { Order = 0 });
                await panel.RefreshAsync(CancellationToken.None);

                Assert.Equal(["group:loose", "step:y", "step:x"], panel.Rows.Select(static row => row.Key).ToList());
                Assert.Same(y, panel.Rows[1]);
                Assert.Same(x, panel.Rows[2]);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- A4 summary

    [Fact]
    public async Task The_summary_shows_its_text_and_stamp_and_reports_its_absence()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "# 标题\n\n正文内容", At);

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 0);
            try
            {
                Assert.True(await TestPump.UntilAsync(() => panel.HasSummary), "the summary never loaded");
                Assert.Equal("# 标题\n\n正文内容", panel.SummaryText);
                Assert.True(panel.HasSummary);
                Assert.Equal(TextFormat.ShortStamp(At), panel.SummaryStamp);
                Assert.True(panel.HasSummaryStamp);
                Assert.NotEmpty(panel.SummaryStamp);

                store.Summary = null;
                await panel.RefreshAsync(CancellationToken.None);

                Assert.Equal(string.Empty, panel.SummaryText);
                Assert.False(panel.HasSummary);
                Assert.Equal(string.Empty, panel.SummaryStamp);
                Assert.False(panel.HasSummaryStamp);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- A5 revisions

    [Fact]
    public async Task Revisions_are_newest_first_with_the_count_preview_and_back()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "当前摘要", At);
        // The store hands the history out newest first (the ITodoStore contract); the panel keeps that order.
        store.Revisions.Add(new PlanRevision(SessionId, "较新版本内容", At.AddMinutes(-2)));
        store.Revisions.Add(new PlanRevision(SessionId, "旧版本内容", At.AddMinutes(-10)));

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 0);
            try
            {
                Assert.True(await TestPump.UntilAsync(() => panel.Revisions.Count == 2), "the revisions never loaded");
                Assert.Equal(At.AddMinutes(-2), panel.Revisions[0].ReplacedAt);
                Assert.Equal(At.AddMinutes(-10), panel.Revisions[1].ReplacedAt);
                Assert.Equal(PlanStrings.RevisionCount(2).Resolve(), panel.RevisionsCountText);
                Assert.True(panel.HasRevisions);

                var newer = panel.Revisions[0];
                newer.ViewCommand.Execute(null);
                Assert.Equal("较新版本内容", panel.SummaryText);
                Assert.True(panel.IsViewingRevision);
                Assert.Equal(PlanStrings.RevisionBanner(newer.TimeText).Resolve(), panel.RevisionBanner);
                Assert.Contains(newer.TimeText, panel.RevisionBanner);
                Assert.Equal(string.Empty, panel.SummaryStamp);

                panel.BackToSummaryCommand.Execute(null);
                Assert.False(panel.IsViewingRevision);
                Assert.Equal("当前摘要", panel.SummaryText);
                Assert.Equal(string.Empty, panel.RevisionBanner);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_previewed_revision_the_store_no_longer_holds_exits_the_preview()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "当前摘要", At);
        store.Revisions.Add(new PlanRevision(SessionId, "将被打扫掉的版本", At.AddMinutes(-3)));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 0);
            try
            {
                Assert.True(await TestPump.UntilAsync(() => panel.Revisions.Count == 1), "the revision never loaded");
                panel.Revisions[0].ViewCommand.Execute(null);
                Assert.True(panel.IsViewingRevision);

                store.Revisions.Clear();
                await panel.RefreshAsync(CancellationToken.None);

                Assert.False(panel.IsViewingRevision);
                Assert.Equal("当前摘要", panel.SummaryText);
                Assert.Empty(panel.Revisions);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- A6 hand-off

    [Fact]
    public async Task Handing_a_step_off_sends_a_prompt_naming_the_step_and_reports_it()
    {
        var store = new FakePlanStore();
        store.SeedGroup(new TodoGroup("g1", SessionId, "分组甲", 0, At));
        store.Seed(new TodoItem("t1", SessionId, "步骤一", TodoStatus.Pending, "注意项", 0, At, GroupId: "g1"));

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 2);
            try
            {
                var step = panel.Rows[1];
                Assert.True(step.CanHandoff);
                Assert.True(step.HandoffCommand!.CanExecute(null));
                step.HandoffCommand.Execute(null);

                var prompt = Assert.Single(host.HandoffPrompts);
                Assert.Equal(PlanStrings.HandoffPrompt("步骤一", "t1", "分组甲", "注意项"), prompt);
                Assert.Contains("t1", prompt);
                Assert.Contains("步骤一", prompt);

                Assert.Equal(PlanStrings.HandoffSent("步骤一").Resolve(), panel.StatusText);
                Assert.True(panel.HasStatus);
                Assert.False(panel.StatusIsFailure);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Theory]
    [InlineData((int)PlanHandoff.Queued, false)]
    [InlineData((int)PlanHandoff.BlockedByDraft, true)]
    [InlineData((int)PlanHandoff.Unavailable, true)]
    public async Task A_hand_off_reports_its_outcome_in_the_status_line(int outcomeValue, bool expectedFailure)
    {
        var outcome = (PlanHandoff)outcomeValue;
        var store = new FakePlanStore();
        store.Seed(new TodoItem("t1", SessionId, "步骤一", TodoStatus.Pending, null, 0, At));

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost
            {
                PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false),
                HandoffOutcome = outcome,
            };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 2);
            try
            {
                var step = panel.Rows[1];
                step.HandoffCommand!.Execute(null);

                var expected = outcome switch
                {
                    PlanHandoff.Queued => PlanStrings.HandoffQueued("步骤一").Resolve(),
                    PlanHandoff.BlockedByDraft => Loc.Instance[PlanStrings.HandoffBlocked],
                    _ => Loc.Instance[PlanStrings.HandoffUnavailable],
                };
                Assert.Equal(expected, panel.StatusText);
                Assert.Equal(expectedFailure, panel.StatusIsFailure);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task Handing_off_a_done_step_does_nothing()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("t1", SessionId, "已完成的步骤", TodoStatus.Done, null, 0, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 2);
            try
            {
                var step = panel.Rows[1];
                Assert.False(step.CanHandoff);
                Assert.False(step.HandoffCommand!.CanExecute(null));
                var statusBefore = panel.StatusText;

                step.HandoffCommand.Execute(null);

                Assert.Empty(host.HandoffPrompts);
                Assert.Equal(statusBefore, panel.StatusText);
                Assert.False(panel.StatusIsFailure);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_read_only_session_reports_unavailable_without_calling_the_host()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("t1", SessionId, "步骤一", TodoStatus.Pending, null, 0, At));

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost
            {
                PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: true),
                HandoffOutcome = PlanHandoff.Sent,
            };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 2);
            try
            {
                panel.Rows[1].HandoffCommand!.Execute(null);

                Assert.Empty(host.HandoffPrompts);
                Assert.Equal(Loc.Instance[PlanStrings.HandoffReadOnly], panel.StatusText);
                Assert.True(panel.StatusIsFailure);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- A7 export

    [Fact]
    public async Task Export_writes_the_rendered_plan()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "# 摘要\n\n这是计划正文。", At);
        store.SeedGroup(new TodoGroup("g1", SessionId, "分组甲", 0, At));
        store.Seed(new TodoItem("t1", SessionId, "步骤一", TodoStatus.Done, null, 0, At, GroupId: "g1", CommitId: "abcdef1234567", Summary: "改了", Verification: "验了"));
        store.Seed(new TodoItem("t2", SessionId, "散项", TodoStatus.Pending, null, 0, At));

        var root = TestTempFiles.NewDirectory("forge-plan-export-");
        try
        {
            var path = Path.Combine(root, "plan.md");
            await SharedUi.RunAsync(async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
                var panel = await LoadedPanelAsync(store, host, expectedRows: 4);
                try
                {
                    var expected = PrBodyRenderer.Render(store.Summary, store.Items(), store.Groups());
                    Assert.Equal(expected, panel.CopyText);

                    await panel.ExportAsync(path, CancellationToken.None);

                    Assert.Equal(expected, await File.ReadAllTextAsync(path));
                    Assert.Equal(PlanStrings.ExportDone(path).Resolve(), panel.StatusText);
                    Assert.True(panel.HasStatus);
                    Assert.False(panel.StatusIsFailure);
                }
                finally
                {
                    panel.Dispose();
                }
            });
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task A_failed_export_reports_the_failure_without_throwing()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "计划正文", At);

        var root = TestTempFiles.NewDirectory("forge-plan-export-");
        try
        {
            await SharedUi.RunAsync(async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
                var panel = await LoadedPanelAsync(store, host, expectedRows: 0);
                try
                {
                    Assert.True(await TestPump.UntilAsync(() => panel.HasSummary), "the summary never loaded");
                    await panel.ExportAsync(Path.Combine(root, "no-such-directory", "plan.md"), CancellationToken.None);

                    Assert.StartsWith(PlanStrings.ExportFailed(string.Empty).Resolve().TrimEnd(), panel.StatusText, StringComparison.Ordinal);
                    Assert.True(panel.HasStatus);
                    Assert.True(panel.StatusIsFailure);
                }
                finally
                {
                    panel.Dispose();
                }
            });
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task An_unreadable_plan_reports_the_failure_without_throwing()
    {
        var store = new FakePlanStore();
        store.SummaryFailure = new IOException("the plan store is gone");

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            try
            {
                Assert.True(await TestPump.UntilAsync(() => panel.StatusIsFailure), "the read failure never landed in the status line");
                Assert.StartsWith(PlanStrings.Failed(string.Empty).Resolve().TrimEnd(), panel.StatusText, StringComparison.Ordinal);
                Assert.True(panel.HasStatus);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_read_that_succeeds_again_clears_the_failure_but_not_a_handoff_report()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("t1", SessionId, "一步", TodoStatus.Pending, null, 0, At));
        store.SummaryFailure = new IOException("the plan store is gone");

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            try
            {
                Assert.True(await TestPump.UntilAsync(() => panel.StatusIsFailure), "the read failure never landed in the status line");

                // The store recovers: the next read puts the plan on screen and retires the stale failure.
                store.SummaryFailure = null;
                await panel.RefreshAsync(CancellationToken.None);
                Assert.Equal(2, panel.Rows.Count);
                Assert.False(panel.StatusIsFailure);
                Assert.False(panel.HasStatus);
                Assert.Equal(string.Empty, panel.StatusText);

                // A hand-off report is not a read report: a later successful read leaves it on screen.
                panel.Handoff(panel.Rows[1]);
                var report = panel.StatusText;
                Assert.NotEqual(string.Empty, report);
                await panel.RefreshAsync(CancellationToken.None);
                Assert.Equal(report, panel.StatusText);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task Revisions_keep_the_store_order_and_an_unchanged_history_keeps_its_rows()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "当前摘要", At);

        // Newest recorded first, but the stamps run backwards (the clock stepped back between the two writes).
        store.Revisions.Add(new PlanRevision(SessionId, "后记的版本", At.AddMinutes(-10)));
        store.Revisions.Add(new PlanRevision(SessionId, "先记的版本", At.AddMinutes(-2)));

        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.English);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "s", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 0);
            try
            {
                Assert.True(await TestPump.UntilAsync(() => panel.Revisions.Count == 2), "the revisions never loaded");
                Assert.Equal(["后记的版本", "先记的版本"], panel.Revisions.Select(revision => revision.Text).ToArray());
                var first = panel.Revisions[0];
                var second = panel.Revisions[1];
                var resets = 0;
                panel.Revisions.CollectionChanged += (_, _) => resets++;

                await panel.RefreshAsync(CancellationToken.None);

                Assert.Equal(0, resets);
                Assert.Same(first, panel.Revisions[0]);
                Assert.Same(second, panel.Revisions[1]);
                Assert.Equal("2 versions", panel.RevisionsCountText);

                store.Revisions.RemoveAt(1);
                await panel.RefreshAsync(CancellationToken.None);
                Assert.Equal("1 version", panel.RevisionsCountText);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- A8 refresh and dispose

    [Fact]
    public async Task A_refresh_requested_during_a_read_schedules_exactly_one_follow_up_read()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "v1", At);
        store.Seed(new TodoItem("t1", SessionId, "步骤", TodoStatus.Pending, null, 0, At));
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GateNextSummaryRead(firstGate);
        store.GateNextSummaryRead(secondGate);

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            try
            {
                panel.Open();
                Assert.True(await TestPump.UntilAsync(() => store.GetPlanSummaryCalls == 1), "the first read never started");
                Assert.Equal("", panel.SummaryText);

                // Two requests while the read runs: both coalesce into the one queued follow-up, no second read beside it.
                await panel.RefreshAsync(CancellationToken.None);
                await panel.RefreshAsync(CancellationToken.None);
                Assert.Equal(1, store.GetPlanSummaryCalls);

                firstGate.SetResult();
                Assert.True(await TestPump.UntilAsync(() => store.GetPlanSummaryCalls == 2), "the queued follow-up read never started");
                Assert.Equal("v1", panel.SummaryText);

                // The follow-up re-reads the store; its answer is the one that lands.
                store.Summary = new PlanSummary(SessionId, "v2", At.AddMinutes(1));
                secondGate.SetResult();
                Assert.True(await TestPump.UntilAsync(() => panel.SummaryText == "v2"), "the follow-up answer never landed");
                Assert.Equal(2, store.GetPlanSummaryCalls);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_store_change_during_a_read_coalesces_into_one_follow_up_read()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "v1", At);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GateNextSummaryRead(gate);

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            try
            {
                panel.Open();
                Assert.True(await TestPump.UntilAsync(() => store.GetPlanSummaryCalls == 1), "the first read never started");

                store.Summary = new PlanSummary(SessionId, "v2", At.AddMinutes(1));
                store.Emit(SessionId);
                // The posted refresh only queues a follow-up; it starts no second read beside the running one.
                Assert.Equal(1, await PumpKeepingCountAsync(store, 1));

                gate.SetResult();
                Assert.True(await TestPump.UntilAsync(() => panel.SummaryText == "v2" && store.GetPlanSummaryCalls == 2),
                    "the follow-up read never applied the new summary");
                Assert.Equal(2, store.GetPlanSummaryCalls);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    [Fact]
    public async Task A_store_change_after_dispose_no_longer_reads_and_a_foreign_session_never_did()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "v1", At);

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            Assert.True(await TestPump.UntilAsync(() => store.GetPlanSummaryCalls == 1), "the first read never finished");
            var calls = store.GetPlanSummaryCalls;

            // A change of another session is ignored even while the panel is alive.
            store.Emit("another-session");
            Assert.Equal(calls, await PumpKeepingCountAsync(store, calls));

            panel.Dispose();
            store.Emit(SessionId);
            Assert.Equal(calls, await PumpKeepingCountAsync(store, calls));
        });
    }

    [Fact]
    public async Task A_read_that_finishes_after_dispose_lands_nowhere()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "迟到的摘要", At);
        store.Seed(new TodoItem("t1", SessionId, "迟到的步骤", TodoStatus.Pending, null, 0, At));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.GateNextSummaryRead(gate);

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            Assert.True(await TestPump.UntilAsync(() => store.GetPlanSummaryCalls == 1), "the held read never started");

            panel.Dispose();
            gate.SetResult();
            Assert.True(await TestPump.UntilAsync(() => store.SummaryReadsCompleted == 1), "the held read never finished");

            Assert.Equal(string.Empty, panel.SummaryText);
            Assert.False(panel.HasSummary);
            Assert.Empty(panel.Rows);
        });
    }

    // ---------------------------------------------------------------- A9 no session

    [Fact]
    public async Task Without_a_session_the_panel_is_empty_and_reads_nothing()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "不该被读", At);

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = null };
            var panel = new PlanPanelViewModel(store, host);
            panel.Open();
            try
            {
                await panel.RefreshAsync(CancellationToken.None);

                Assert.False(panel.HasSession);
                Assert.Equal(string.Empty, panel.SessionTitle);
                Assert.Empty(panel.Rows);
                Assert.Empty(panel.Revisions);
                Assert.Equal(0, store.GetPlanSummaryCalls);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    /// <summary>
    /// The first load already marks the next step: the panel's first read inserts every row and must apply the
    /// next-step mark to the inserted rows too, so the right rail names the first in-progress step (else the first
    /// pending one) the moment the panel opens.
    /// </summary>
    [Fact]
    public async Task The_first_load_already_marks_the_next_step()
    {
        var store = new FakePlanStore();
        store.Seed(new TodoItem("i", SessionId, "进行中", TodoStatus.InProgress, null, 0, At));

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 2);
            try
            {
                Assert.Equal("step:i", panel.NextStep?.Key);
                Assert.True(panel.HasNextStep);
                Assert.True(panel.Rows.Single(static row => row.Key == "step:i").IsNext);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- B1 language switch

    [Fact]
    public async Task A_language_switch_re_resolves_the_texts_without_re_reading_the_store()
    {
        var store = new FakePlanStore();
        store.Summary = new PlanSummary(SessionId, "摘要正文", At);
        store.SeedGroup(new TodoGroup("g1", SessionId, "分组甲", 0, At));
        store.Seed(new TodoItem("t1", SessionId, "步骤一", TodoStatus.Done, "备注", 0, At, GroupId: "g1", Summary: "改了布局", Verification: "测试通过"));
        store.Seed(new TodoItem("t2", SessionId, "步骤二", TodoStatus.Pending, null, 1, At, GroupId: "g1"));
        store.Revisions.Add(new PlanRevision(SessionId, "旧版本内容", At.AddMinutes(-5)));

        await SharedUi.RunAsync(async () =>
        {
            using var chinese = new LanguageScope(UiLanguage.Chinese);
            var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 3);
            try
            {
                Assert.True(await TestPump.UntilAsync(() => panel.Revisions.Count == 1), "the revision never loaded");
                var step = panel.Rows.Single(static row => row.Key == "step:t1");
                var revisionTime = panel.Revisions[0].TimeText;

                // The derived texts resolve in Chinese while the interface language is Chinese.
                Assert.Equal(PlanStrings.Progress(1, 2).Resolve(), panel.ProgressText);
                Assert.Equal(PlanStrings.StepSummary("改了布局").Resolve(), step.SummaryText);
                Assert.Equal(PlanStrings.Verification("测试通过").Resolve(), step.VerificationText);
                Assert.Equal(Loc.Instance[TodoRowViewModel.StatusKey(TodoStatus.Done)], step.StatusName);
                Assert.Equal(PlanStrings.SummaryUpdated(TextFormat.ShortStamp(At)).Resolve(), panel.SummaryUpdatedText);
                var chineseProgress = panel.ProgressText;
                var chineseSummary = step.SummaryText;
                var chineseVerification = step.VerificationText;
                var chineseStatus = step.StatusName;
                var chineseUpdated = panel.SummaryUpdatedText;

                // The banner replaces the updated stamp while a revision is previewed.
                panel.Revisions[0].ViewCommand.Execute(null);
                Assert.Equal(PlanStrings.RevisionBanner(revisionTime).Resolve(), panel.RevisionBanner);
                Assert.Equal(string.Empty, panel.SummaryUpdatedText);
                var chineseBanner = panel.RevisionBanner;
                var rowsBefore = panel.Rows.ToList();
                var readsBefore = store.GetPlanSummaryCalls;
                string englishProgress, englishSummary, englishVerification, englishStatus, englishBanner, englishUpdated;

                using (new LanguageScope(UiLanguage.English))
                {
                    Assert.Equal(UiLanguage.English, Loc.Instance.Language);

                    // The switch re-resolves every derived text in English...
                    englishProgress = panel.ProgressText;
                    englishSummary = step.SummaryText;
                    englishVerification = step.VerificationText;
                    englishStatus = step.StatusName;
                    englishBanner = panel.RevisionBanner;
                    Assert.Equal(PlanStrings.Progress(1, 2).Resolve(), englishProgress);
                    Assert.Equal(PlanStrings.StepSummary("改了布局").Resolve(), englishSummary);
                    Assert.Equal(PlanStrings.Verification("测试通过").Resolve(), englishVerification);
                    Assert.Equal(Loc.Instance[TodoRowViewModel.StatusKey(TodoStatus.Done)], englishStatus);
                    Assert.Equal(PlanStrings.RevisionBanner(revisionTime).Resolve(), englishBanner);
                    Assert.Equal(string.Empty, panel.SummaryUpdatedText);

                    // Back on the current summary, the updated stamp resolves in English too.
                    panel.BackToSummaryCommand.Execute(null);
                    englishUpdated = panel.SummaryUpdatedText;
                    Assert.Equal(PlanStrings.SummaryUpdated(TextFormat.ShortStamp(At)).Resolve(), englishUpdated);

                    // ...without reading the store again or rebuilding a single row.
                    Assert.Equal(readsBefore, store.GetPlanSummaryCalls);
                    for (var index = 0; index < rowsBefore.Count; index++)
                    {
                        Assert.Same(rowsBefore[index], panel.Rows[index]);
                    }
                }

                // The texts really changed language, not just resolved the same way twice.
                Assert.NotEqual(chineseProgress, englishProgress);
                Assert.NotEqual(chineseSummary, englishSummary);
                Assert.NotEqual(chineseVerification, englishVerification);
                Assert.NotEqual(chineseStatus, englishStatus);
                Assert.NotEqual(chineseBanner, englishBanner);
                Assert.NotEqual(chineseUpdated, englishUpdated);
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- B2 open pull request

    [Fact]
    public async Task The_open_pull_request_exit_delegates_to_the_host_and_tracks_its_availability()
    {
        var store = new FakePlanStore();

        await SharedUi.RunAsync(async () =>
        {
            var host = new FakePlanHost
            {
                PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false),
                CanOpenPlanPullRequest = true,
            };
            var panel = await LoadedPanelAsync(store, host, expectedRows: 0);
            try
            {
                Assert.True(panel.OpenPullRequestCommand.CanExecute(null));

                panel.OpenPullRequestCommand.Execute(null);
                Assert.Equal(1, host.OpenPullRequestCalls);

                // The git row's availability changed while the panel was open: the next refresh picks it up.
                host.CanOpenPlanPullRequest = false;
                await panel.RefreshAsync(CancellationToken.None);
                Assert.False(panel.OpenPullRequestCommand.CanExecute(null));
                panel.OpenPullRequestCommand.Execute(null);
                Assert.Equal(1, host.OpenPullRequestCalls); // a disabled command delegates nothing

                host.CanOpenPlanPullRequest = true;
                await panel.RefreshAsync(CancellationToken.None);
                Assert.True(panel.OpenPullRequestCommand.CanExecute(null));
            }
            finally
            {
                panel.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- B3 revision rows

    [Theory]
    [InlineData("\n\n  第一行内容\n第二行", "第一行内容")]
    [InlineData(" \n\t\n三个空格后的行\n", "三个空格后的行")]
    [InlineData("第一行已经是内容", "第一行已经是内容")]
    public void The_revision_preview_is_the_first_non_empty_line(string text, string expected)
    {
        var panel = NewPanelOwner();
        try
        {
            var row = new PlanRevisionViewModel(panel, new PlanRevision(SessionId, text, At));

            Assert.Equal(expected, row.PreviewText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Theory]
    [InlineData("\n \n\t\n")]
    [InlineData("   ")]
    public void An_all_whitespace_revision_previews_as_empty(string text)
    {
        var panel = NewPanelOwner();
        try
        {
            var row = new PlanRevisionViewModel(panel, new PlanRevision(SessionId, text, At));

            Assert.Equal(string.Empty, row.PreviewText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Theory]
    [InlineData(160, false)]
    [InlineData(161, true)]
    public void A_long_revision_preview_is_cut_at_160_characters_with_an_ellipsis(int length, bool withEllipsis)
    {
        var text = new string('x', length);
        var panel = NewPanelOwner();
        try
        {
            var row = new PlanRevisionViewModel(panel, new PlanRevision(SessionId, text, At));

            Assert.Equal(withEllipsis ? new string('x', 160) + "…" : text, row.PreviewText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public void A_revision_row_copies_the_full_text_and_stamps_its_replacement_time()
    {
        var text = "\n第一段\n\n第二段\n";
        var replacedAt = At.AddMinutes(-7);
        var panel = NewPanelOwner();
        try
        {
            var row = new PlanRevisionViewModel(panel, new PlanRevision(SessionId, text, replacedAt));

            // The preview is the one-liner; the copy carries the whole text the version held.
            Assert.Equal("第一段", row.PreviewText);
            Assert.Equal(text, row.CopyText);
            Assert.Equal(replacedAt, row.ReplacedAt);
            Assert.Equal(TextFormat.ShortStamp(replacedAt), row.TimeText);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ---------------------------------------------------------------- B4 suggested export name

    [Theory]
    [InlineData("wave-7 计划", "wave-7-计划-plan.md")]
    [InlineData("/release: 计划:", "release--计划-plan.md")]
    [InlineData("__计划__", "计划-plan.md")]
    [InlineData("", "plan-plan.md")]
    [InlineData("::/  ", "plan-plan.md")]
    public async Task The_suggested_export_name_sanitizes_the_session_title(string title, string expected)
    {
        await SharedUi.RunAsync(async () =>
        {
            Assert.Equal(expected, await SuggestedNameAsync(title));
        });
    }

    [Fact]
    public async Task A_title_longer_than_60_characters_is_cut_before_the_extension_is_appended()
    {
        await SharedUi.RunAsync(async () =>
        {
            Assert.Equal(new string('a', 60) + "-plan.md", await SuggestedNameAsync(new string('a', 70)));

            // A separator that lands on the 60th kept character is cut with it: the length limit applies
            // to what the name keeps, and the trailing trim runs afterwards.
            Assert.Equal(new string('a', 59) + "-plan.md", await SuggestedNameAsync(new string('a', 59) + " /x"));
        });
    }

    // ---------------------------------------------------------------- B5 the revision window

    [Fact]
    public void The_panel_asks_the_store_for_exactly_the_revisions_the_store_keeps()
    {
        // Both constants gate the same list. If they drift apart, the store trims a version the panel still
        // expects to read back, and the guard that exits the preview of a trimmed revision stops working.
        Assert.Equal(SqliteTodoStore.PlanRevisionLimit, PlanPanelViewModel.RevisionTake);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Creates the panel, opens it and waits until the first load put the expected rows on screen. Tests whose
    /// panel has no rows wait on the property they read (summary, revisions) instead.</summary>
    private static async Task<PlanPanelViewModel> LoadedPanelAsync(FakePlanStore store, FakePlanHost host, int expectedRows)
    {
        var panel = new PlanPanelViewModel(store, host);
        panel.Open();
        if (expectedRows > 0)
        {
            Assert.True(
                await TestPump.UntilAsync(() => panel.Rows.Count == expectedRows),
                $"the panel never loaded its {expectedRows} rows");
        }

        return panel;
    }

    /// <summary>Asserts the row raised no notification for any stored field (only derived convenience flags may re-fire).</summary>
    private static void AssertNoFieldNotifications(IReadOnlyDictionary<string, List<string>> notes, string key)
    {
        var raised = notes[key];
        foreach (var field in ValueFields)
        {
            Assert.DoesNotContain(field, raised);
        }
    }

    /// <summary>A panel owner for the revision-row unit tests: the row needs a live owner, never a loaded plan.</summary>
    private static PlanPanelViewModel NewPanelOwner()
    {
        var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, "会话一", IsReadOnly: false) };
        return new PlanPanelViewModel(new FakePlanStore(), host);
    }

    /// <summary>The panel's export suggestion for one session title, over a loaded panel that is disposed on return.</summary>
    private static async Task<string> SuggestedNameAsync(string title)
    {
        var host = new FakePlanHost { PlanSession = new PlanPanelSession(SessionId, title, IsReadOnly: false) };
        var panel = new PlanPanelViewModel(new FakePlanStore(), host);
        try
        {
            panel.Open();
            await panel.RefreshAsync(CancellationToken.None);
            return panel.SuggestedExportName;
        }
        finally
        {
            panel.Dispose();
        }
    }

    /// <summary>
    /// Pumps the dispatcher for a bounded moment, watching that a summary read never starts; returns the call count at
    /// the end. Used to prove a change event no longer reaches the panel, not to sleep unbounded.
    /// </summary>
    private static async Task<int> PumpKeepingCountAsync(FakePlanStore store, int calls)
    {
        for (var i = 0; i < 40; i++)
        {
            if (store.GetPlanSummaryCalls != calls)
            {
                break;
            }

            await Task.Delay(5);
        }

        return store.GetPlanSummaryCalls;
    }
}
