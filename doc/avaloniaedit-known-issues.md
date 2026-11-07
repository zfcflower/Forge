# AvaloniaEdit integration: remaining issues

Recorded 2026-11-08. At the user's request, remaining issues are deferred rather than fixed in this change.

## Validation and baseline

- Final main-worktree `dotnet test Forge.slnx`: **6148 passed, 48 failed, 0 skipped**.
- Pre-change baseline: 46 failures. Of the final 48 failures, 45 match baseline test names and 3 are newly observed. One baseline failure did not recur. This is not a green full-suite result.
- Native editor mixed scope passed 132/132 twice in the isolated copy. These targeted runs did not reveal the two full-suite UI failures below.
- Main application Release Native AOT publish succeeded. The published executable created a responsive native window with isolated data/workspace directories.
- Published GUI startup is not a full manual editor, hover, Ctrl-click or review-overlay acceptance test. Core editor, popup, F12, diagnostics, review drafts and queue operations have headless coverage.
- Full Release solution build failed with `CS0122` on `TerminalOps` in the unrelated HostClient tests. Representative locations: `tests/Forge.HostClient.Tests/HostClientTestSupport.cs:243`, `Terminal/TerminalStartProtocolTests.cs:25`, `Terminal/TerminalConnectionTests.cs:81`. Application publish itself succeeds. The Release-only failure was not established as a baseline failure.

## Newly observed failures requiring investigation

| Test | Location | Observed failure / follow-up |
| --- | --- | --- |
| `TextEditorLifecycleTests.A_large_file_materializes_only_the_visible_lines` | `tests/Forge.App.Tests/Editor/TextEditorLifecycleTests.cs:121` | Scrolled lines near 150000 did not materialize within the bounded 15-second wait in the full solution run. Targeted runs passed. Investigate layout, scroll readiness and shared headless application state; do not simply weaken virtualization assertions. |
| `FileEditorInteractionTests.Declining_one_of_two_dirty_files_closes_nothing_and_marks_neither_clean` | `tests/Forge.App.Tests/ViewModels/FileEditorInteractionTests.cs:177`; `tests/Forge.App.Tests/Workbench/WorkbenchControlsTests.cs:1821` | Different-thread access during `ShellFixture.DisposeAsync`. Stack: `HeadlessUnitTestSession.EnsureIsolatedApplication` → `AppBuilder.SetupUnsafe` → `AvaloniaHeadlessPlatform.Initialize` → `ServerCompositor` → `DefaultRenderLoop.Add`. This is application/render-loop initialization interference, not the previously fixed static-keybinding symptom. |
| `ProcessTreeTests.Cancellation_kills_the_grandchild_the_child_started` | `tests/Forge.IO.Tests/Process/ProcessTreeTests.cs:63` | Child did not record the grandchild PID. Outside editor paths; newly observed under full-suite load, root cause unconfirmed. |

AvaloniaEdit 12.0.0 also shares static `KeyBinding` instances in `EditingCommandHandler` and `CaretNavigationCommandHandler`. Those AvaloniaObjects belong to the dispatcher that first created them. Native-editor tests therefore use a shared headless session. Other tests still create independent sessions; mixed ownership can interfere with global Avalonia state. Production has a single UI thread. A wider test-infrastructure migration was deliberately not performed.

## Final baseline-matching failures (45)

Locations are the first source location in the final TRX stack, not necessarily the defect's root cause. Methods below are grouped by class; all Workspace Git paths are under `tests/Forge.Workspace.Tests/Git/`.

### Workspace Git (38)

`GitRemoteTestSupport.cs:77` is the common failure location for these cases:

- `ScreenshotGitObjectsTests.PushCommit_pushes_nothing_but_the_one_ref`
- `GitCliPushPinTests.A_pinned_first_push_still_sets_the_upstream_of_the_branch`
- `GitCliPushPinTests.A_pinned_push_publishes_exactly_the_given_commit_even_when_head_moved_on`
- `GitCliPullPushTests.Push_behind_the_remote_reports_non_fast_forward_and_keeps_the_remote_tip`
- `GitCliPullPushTests.LsRemote_returns_the_exact_tip_for_an_existing_branch`
- `GitCliPullPushTests.First_push_with_set_upstream_configures_the_upstream`
- `GitCliPullPushTests.Force_with_lease_on_a_stale_read_reports_non_fast_forward`
- `GitCliPullPushTests.Force_with_lease_claiming_absence_is_rejected_when_the_branch_exists`
- `GitCliPullPushTests.Force_with_lease_after_fresh_read_succeeds`
- `PrCreateSyncSmokeTests.Sync_refuses_to_force_over_commits_somebody_else_pushed`
- `ScreenshotPublisherTests.Publish_fails_at_the_remote_stage_when_head_is_detached`

| Class / method | File and line |
| --- | --- |
| `ScreenshotPublisherTests.Publish_pushes_a_parentless_commit_that_holds_exactly_the_images` | `ScreenshotPublisherTests.cs:89` |
| `ScreenshotPublisherTests.Publish_again_replaces_the_section_and_the_assets_branch_with_the_new_images` | `ScreenshotPublisherTests.cs:211` |
| `ScreenshotPublisherTests.Publish_overwrites_an_assets_branch_that_someone_else_rewrote_because_the_lease_is_read_fresh` | `ScreenshotPublisherTests.cs:242` |
| `ScreenshotPublisherTests.Publish_without_a_pull_request_still_pushes_the_images_and_hands_back_the_markdown` | `ScreenshotPublisherTests.cs:461` |
| `ScreenshotPublisherTests.Publish_reports_a_description_the_platform_did_not_keep_as_a_body_failure_after_the_push` | `ScreenshotPublisherTests.cs:484` |
| `ScreenshotGitObjectsTests.PushCommit_creates_a_ref_on_the_remote_without_creating_one_locally` | `ScreenshotGitObjectsTests.cs:258` |
| `ScreenshotGitObjectsTests.PushCommit_replaces_the_ref_when_the_lease_matches_even_though_the_history_is_unrelated` | `ScreenshotGitObjectsTests.cs:279` |
| `ScreenshotGitObjectsTests.PushCommit_refuses_to_overwrite_a_ref_that_moved_since_it_was_read` | `ScreenshotGitObjectsTests.cs:301` |
| `ScreenshotGitObjectsTests.PushCommit_with_an_empty_lease_refuses_when_the_ref_already_exists` | `ScreenshotGitObjectsTests.cs:320` |
| `ScreenshotToolsTests.It_captures_before_and_after_and_writes_the_section_into_the_description` | `ScreenshotToolsTests.cs:50` |
| `ScreenshotToolsTests.It_publishes_existing_files_instead_of_capturing` | `ScreenshotToolsTests.cs:223` |
| `ScreenshotToolsTests.It_removes_the_temporary_paths_once_the_description_holds_the_images` | `ScreenshotToolsTests.cs:278` |
| `ScreenshotToolsTests.A_description_the_platform_did_not_keep_is_reported_with_the_markdown_to_paste` | `ScreenshotToolsTests.cs:349` |
| `PrCreateSyncSmokeTests.Create_pushes_the_branch_with_upstream_and_a_lease_and_opens_the_pull_request` | `PrCreateSyncSmokeTests.cs:201` |
| `PrCreateSyncSmokeTests.Create_is_not_blocked_by_a_closed_pull_request_of_the_same_branch_name` | `PrCreateSyncSmokeTests.cs:675` |
| `PrCreateSyncSmokeTests.Create_never_overwrites_a_diverged_remote_branch` | `PrCreateSyncSmokeTests.cs:739` |
| `PrCreateSyncSmokeTests.Create_fast_forwards_an_older_remote_branch_with_a_lease_on_the_observed_tip` | `PrCreateSyncSmokeTests.cs:762` |
| `PrCreateSyncSmokeTests.Create_sets_the_upstream_of_a_branch_that_is_already_on_the_remote_at_head` | `PrCreateSyncSmokeTests.cs:783` |
| `PrCreateSyncSmokeTests.Create_keeps_the_pushed_branch_when_the_platform_fails_and_a_retry_succeeds` | `PrCreateSyncSmokeTests.cs:824` |
| `PrCreateSyncSmokeTests.Sync_rebases_a_branch_without_merge_commits_and_pushes_with_a_fresh_lease` | `PrCreateSyncSmokeTests.cs:901` |
| `PrCreateSyncSmokeTests.Sync_merges_when_the_branch_already_has_merge_commits_and_pushes_without_force` | `PrCreateSyncSmokeTests.cs:929` |
| `PrCreateSyncSmokeTests.Sync_does_nothing_to_history_when_the_base_is_already_contained` | `PrCreateSyncSmokeTests.cs:952` |
| `PrCreateSyncSmokeTests.Sync_pushes_a_branch_the_agent_already_rebased_by_hand` | `PrCreateSyncSmokeTests.cs:978` |
| `PrCreateSyncSmokeTests.Sync_takes_the_base_from_the_open_pull_request` | `PrCreateSyncSmokeTests.cs:1000` |
| `PrCreateSyncSmokeTests.Sync_falls_back_to_the_default_branch_and_says_so_when_the_platform_is_unavailable` | `PrCreateSyncSmokeTests.cs:1021` |
| `PrCreateSyncSmokeTests.Sync_keeps_the_local_update_and_says_so_when_the_push_is_rejected` | `PrCreateSyncSmokeTests.cs:1147` |
| `PrCreateSyncSmokeTests.Sync_refuses_a_branch_without_a_pull_request_and_points_at_pr_create` | `PrCreateSyncSmokeTests.cs:1168` |

### App Tree/Git (7)

Paths below are under `tests/Forge.App.Tests/`.

| Test | Location |
| --- | --- |
| `GitPanelFailureKindTests.Every_failure_kind_reaches_the_panel_with_its_hint_and_error_flag(kind: AuthenticationFailed)` | `Git/GitPanelFailureKindTests.cs:58` |
| `GitPanelScreenshotTests.The_declared_command_is_shown_and_the_click_publishes_the_screenshots` | `Git/GitTestSupport.cs:110` |
| `GitScreenshotApprovalTests.Approving_the_pr_screenshots_card_runs_the_capture_and_publishes_the_images` | `Git/GitScreenshotApprovalTests.cs:77` |
| `GitPanelWorktreeRaceTests.A_write_in_flight_neither_blocks_nor_stains_the_session_switched_to_and_its_result_waits_for_the_way_back` | `Git/GitPanelWorktreeRaceTests.cs:110` |
| `GitPanelWorktreeRaceTests.Attaching_an_empty_context_during_a_write_is_idle_and_the_result_survives_the_clear` | `Git/GitPanelWorktreeRaceTests.cs:277` |
| `GitPanelWorktreeRaceTests.Switching_to_a_worktree_with_its_own_write_running_queues_the_refresh_until_that_write_ends` | `Git/GitPanelWorktreeRaceTests.cs:185` |
| `TreeVisualTests.Agent_rows_put_chevron_after_title_and_colour_and_animate_by_state` | `TreeVisualTests.cs:451` |

## Remaining limitations / coverage

- Queue acceptance and `MarkSent` are not a durable cross-system transaction. Mark/restore are serialized per store, and chip withdrawal restores drafts, but crash/application-exit windows remain. No exactly-once delivery guarantee.
- Deterministic delayed-store coverage for retry-mark versus withdrawal is still missing. Code review verified both semaphore orderings; ordinary withdrawal-before/after-mark and real queue removal/delivery tests pass.
- `ReportHighlightFailure` is not directly exercised by a controlled asset-failure test.
- Hover and Ctrl-click have not received the same real-window gesture coverage as Ctrl+Space and F12. Published startup smoke does not close this gap.
- Highlighting is limited to 1 Mi characters; text remains intact/editable beyond the cap. A single long line below the cap has no additional Forge line-length cap.
- Completion is initially triggered by Ctrl+Space or `.`, snippets/insert-replace edits/item defaults are not advertised, hover is plain text, and only the first definition is followed.
- `Forge.Editor` is intentionally retained for LSP, atomic encoding-aware save and Diff services. Native/Forge mirrored document and undo structures remain duplicated pending service migration.
- Persistent review anchors do not record a base commit. Unique relocation searches files of at most 20000 lines; larger files only accept the original-position exact match.
