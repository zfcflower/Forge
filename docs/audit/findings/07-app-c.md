# App C(Git/PR/出货/检查/步骤提交/行评审)审计发现

审计范围:src/Forge.App/SPEC.md 行 56–63(git 分区)、73、77、82、85、88、91、98、109–124、126、140、142、157、249–262;反向核对 src/Forge.App/{Git,Changes,Review,Editing} 与 src/Forge.Workspace 被消费处。只读审计,未改任何源码/规格/测试,未跑 build/test/git。测试证据均读自 tests/Forge.App.Tests(先读 .github/skills/test-anti-patterns/SKILL.md 作为判据)。

结论汇总:60 个功能点;PASS 51,PASS* 7(实现存在、该不变量无直接测试),PARTIAL 2,FAIL/MISSING/SPEC_GAP/UNVERIFIED 0。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-C-01 | 远程读数是文本非按钮;五态(没有远程/未发布/{n}待推送/与origin同步/落后{n}) | App SPEC:73–75 | src/Forge.App/Git/GitSyncViewModel.cs:414–443;Localization/Strings.Git.cs:438–446 | Git/GitSyncViewModelTests.cs::The_remote_readout_tracks_published_pending_and_synced | PASS |
| F-APP-C-02 | PR 对话框正文预填 PrBodyRenderer.Render(与 pr_create 同源,不调模型) | App SPEC:77–78 | ViewModels/MainWindowViewModel.cs:2456–2478;Git/GitSyncViewModel.cs:761–777;Workspace/Git/PrCreateTool.cs:295 | Git/GitSyncViewModelTests.cs::The_pull_request_dialog_opens_with_the_description_the_plan_can_already_write | PASS |
| F-APP-C-03 | gh 缺失/未登录时 compare 回退页,仅 github.com 远端,base...branch?expand=1 | App SPEC:79–80 | Git/GitSyncViewModel.cs:479–540(WebUrlOf/CompareUrl)、627–634 | Git/GitSyncViewModelTests.cs::A_remote_url_becomes_a_web_url_only_when_the_page_shape_is_known | PARTIAL |
| F-APP-C-04 | 合并队列:gh 成功但 PR 仍 OPEN → 报"已加入合并队列",不谎报已合并 | App SPEC:82–83 | Git/GitSyncViewModel.cs:1070–1083(重读后判 Open) | Git/GitSyncShipFinaleTests.cs::A_merge_queue_keeps_its_message_instead_of_the_follow_up | PASS |
| F-APP-C-05 | 推送目标=默认分支且有内容先确认;功能分支/空推送不问 | App SPEC:85–86 | Git/GitSyncViewModel.cs:1740–1780 | Git/GitSyncViewModelTests.cs::Pushing_to_the_shared_branch_asks_first_and_declining_pushes_nothing;Pushing_a_feature_branch_does_not_ask | PASS |
| F-APP-C-06 | 刻意不做(1):本地合并不按 merge-base 置灰;点击时如实报"已在 main 里" | App SPEC:88 | Git/GitSyncViewModel.cs:576–578(CanMergeLocally 无祖先判定)、916–923(IsAncestorAsync→ShipAlreadyMerged) | 无(置灰缺失为结构性;ShipAlreadyMerged 应答无测试) | PASS* |
| F-APP-C-07 | 刻意不做(2):不加"移除工作区(保留分支)"第二入口 | App SPEC:89–90 | Git/GitSyncViewModel.cs:611–683(ShipActions 无此项);RemovalFlow 拥有 | 无 | PASS* |
| F-APP-C-08 | 出货动作失败只写结果行(错误底+红字+stderr详情),不发系统通知 | App SPEC:91–92 | Git/GitSyncViewModel.cs:832–858(RunShipWriteAsync 无 notifications)、1815–1846 | Git/GitSyncShipFinaleTests.cs::A_failed_merge_reports_the_platform_message_and_never_runs_the_follow_up;A_refused_mark_ready_reports_the_platform_message_and_leaves_the_draft_pill_alone | PASS |
| F-APP-C-09 | 提交目标菜单按状态列七项;删远端分支仅 merged/closed 且必确认;ready 仅草稿 | App SPEC:92–96 | Git/GitSyncViewModel.cs:611–683、399、576–596、1182–1216 | Git/GitSyncViewModelTests.cs::The_ship_row_offers_to_open_a_pull_request_once_the_branch_is_published;An_open_pull_request_becomes_the_button_state_and_blocks_a_second_one;The_merge_pull_request_row_follows_the_platform_merge_state;Git/GitSyncShipFinaleTests.cs::Only_a_draft_pull_request_can_be_marked_ready;Deleting_the_remote_branch_releases_the_row | PASS |
| F-APP-C-10 | 合并对话框策略默认 squash、删远端分支开关默认关;破坏性动作一次确认 | App SPEC:93–94、97 | ViewModels/MergePullRequestDialogViewModel.cs:9–60;Git/GitSyncViewModel.cs:884–895、1194–1201 | Git/MergePullRequestDialogTests.cs::The_dialog_defaults_to_squash_and_leaves_the_remote_branch_alone;Picking_a_strategy_is_exclusive_and_reaches_the_choice;The_delete_switch_only_affects_the_choice_when_the_user_turns_it_on | PASS |
| F-APP-C-11 | UI 从不自动合并、从不强推;三动词不带 force/lease | App SPEC:97、88 | Git/GitSyncViewModel.cs:1760–1763(GitPushOptions 仅 SetUpstream);无自动合并路径 | Git/GitSyncViewModelTests.cs::Push_moves_ahead_commits_and_fetch_pull_track_the_remote;Pull_on_a_diverged_branch_with_ff_only_reports_an_error_and_keeps_head | PASS |
| F-APP-C-12 | 合并后收尾:根 fetch --prune;根在 PR 自己的 base 且干净才仅快进拉动;收尾失败不改写合并事实;根路径弹窗前捕获 | App SPEC:98–104 | Git/GitSyncViewModel.cs:1000–1088、1090–1127;Workspace/Git/GitCli.Remote.cs:243(fetch --prune) | Git/GitSyncShipFinaleTests.cs::A_merge_follow_up_skips_a_root_checkout_that_is_not_on_the_pull_requests_base_branch;A_merge_follow_up_fast_forwards_a_clean_root_checkout_that_is_on_the_base_branch;A_merge_follow_up_only_fast_forwards_even_when_the_pull_setting_rebases;A_dirty_root_checkout_is_fetched_but_the_base_branch_is_not_pulled;A_failed_merge_reports_the_platform_message_and_never_runs_the_follow_up | PASS |
| F-APP-C-13 | 写纪律 RunShipWriteAsync 覆盖全部五个写:writing/busy、二次点击 no-op、刷新排队、EndWork 补跑 | App SPEC:100–104 | Git/GitSyncViewModel.cs:832–858、1394–1431、1611–1633 | Git/GitSyncShipFinaleTests.cs::The_merge_write_is_busy_and_disables_itself_until_the_platform_answers;Opening_a_pull_request_releases_the_row;Deleting_the_remote_branch_releases_the_row;Mark_ready_releases_the_row_and_only_re_reads_after_a_success;Git/GitSyncViewModelTests.cs::Commands_are_disabled_while_busy_and_only_one_operation_runs;Refresh_requests_raised_during_a_read_collapse_into_one_more_read;Git/GitSyncWorktreeRaceTests.cs(4 方法) | PASS |
| F-APP-C-14 | 只有成功才重读(ready 被拒保留平台原话) | App SPEC:104–105 | Git/GitSyncViewModel.cs:1173–1176 | Git/GitSyncShipFinaleTests.cs::Mark_ready_releases_the_row_and_only_re_reads_after_a_success;A_refused_mark_ready_reports_the_platform_message_and_leaves_the_draft_pill_alone | PASS |
| F-APP-C-15 | 检查面板:标题/判决行(Verdict 同等待规则)/桶计数/BucketRank 排序/行字段 | App SPEC:109–114 | ViewModels/ChecksPanelViewModel.cs:167–182、489–499;Workspace/Git/PrChecksPoller.cs:190 | Git/ChecksPanelViewModelTests.cs::A_reading_lists_the_checks_failure_first_and_maps_the_verdict;The_summary_counts_passed_failed_running_and_skipped;Git/ChecksPanelViewTests.cs::The_panel_loads_and_renders_the_verdict_the_counts_and_every_check | PASS |
| F-APP-C-16 | 读失败说明原因并保留上一次检查 | App SPEC:111–112 | ViewModels/ChecksPanelViewModel.cs:437–451(Apply 失败仅 Report) | Git/ChecksPanelViewModelTests.cs::A_failed_read_keeps_the_last_rows_and_verdict_and_shows_the_platform_message | PASS |
| F-APP-C-17 | 轮询复用 ObserveAsync;判决即停;刷新重开;MaxWait 停;连续失败/gh 缺失停 | App SPEC:115–117 | ViewModels/ChecksPanelViewModel.cs:335–417;Workspace/Git/PrChecksPoller.cs:155(MaxConsecutiveFailures=3) | Git/ChecksPanelViewModelTests.cs::A_running_check_keeps_the_poll_going_until_the_next_reading_settles;A_reading_that_already_settles_is_read_once;Refresh_starts_a_new_read_after_a_settled_one;Consecutive_failed_reads_stop_the_poll;A_missing_gh_stops_the_poll_after_one_read;Empty_reads_conclude_no_ci_after_the_configured_confirmations;A_failed_read_breaks_the_run_of_empty_reads(MaxWait 分支无测试,见缺测清单) | PASS |
| F-APP-C-18 | 关闭/切换会话/重开面板取消轮询与行日志/fix/ready 平台调用;资源释放 | App SPEC:117–118 | ViewModels/ChecksPanelViewModel.cs:289–313;ViewModels/MainWindowViewModel.Checks.cs:52、69–77;MainWindowViewModel.cs:439、3134 | Git/ChecksPanelViewModelTests.cs::Dispose_cancels_the_poll_and_the_platform_is_not_read_again;Closing_the_panel_cancels_a_row_log_that_is_being_read;Close_invokes_the_injected_close_and_dispose_is_idempotent | PASS |
| F-APP-C-19 | 字段只在 UI 线程写(post 注入);每轮带世代号,旧读迟到一律丢弃 | App SPEC:118–119 | ViewModels/ChecksPanelViewModel.cs:60–64、329、369、419–427 | 测试用同步 post;无"刷新后旧读迟到"竞态直接测试 | PASS* |
| F-APP-C-20 | 失败日志尾行数来自 GitChecksFailureLogTailLines;展开跨刷新保留 | App SPEC:113–114 | ViewModels/ChecksPanelViewModel.cs:505–524、459–475(Adopt 携行) | Git/ChecksPanelViewModelTests.cs::ToggleLog_reads_the_log_once_and_reopens_it_from_the_cache;A_loaded_log_stays_open_and_is_not_read_again_by_the_next_poll;A_log_is_marked_cut_when_the_platform_cut_it;Git/PrChecksFixMessageTests.cs::The_log_keeps_exactly_the_last_lines_the_setting_asks_for | PASS |
| F-APP-C-21 | fix 消息=digest(Snapshot)+日志尾(每份≤4000、合计≤20000、超出点名省略)+英文指令;读不到原因≤500 | App SPEC:120–124 | Git/PrChecksFixMessage.cs:29–113 | Git/PrChecksFixMessageTests.cs(10 方法,含 Logs_beyond_the_total_cap_are_counted_and_left_out、A_long_unreadable_reason_is_cut_to_one_bounded_line) | PASS |
| F-APP-C-22 | 让 agent 修:空闲跑一轮(带「PR 检查」来源行);忙落输入框;交付失败落输入框;接在草稿后空一行;不中断不排队二次运行 | App SPEC:121–124 | ViewModels/MainWindowViewModel.Checks.cs:84–131;ViewModels/SessionWorkspace.cs:1060–1079、1091;Localization/Strings.Checks.cs:215 | Git/ChecksHandOffTests.cs::A_queued_hand_off_fills_an_empty_composer_with_the_message;A_queued_hand_off_keeps_the_users_draft_and_appends_the_message;Git/ChecksPanelViewModelTests.cs::Fix_reports_what_the_hand_off_did;Closing_the_panel_while_the_fix_reads_logs_hands_nothing_to_the_agent;Fix_reads_one_log_per_failing_check_and_hands_the_digest_to_the_agent | PASS |
| F-APP-C-23 | 检查「详情」链接只打开 http/https | App SPEC:124 | ViewModels/MainWindowViewModel.Checks.cs:139–163 | Git/ChecksHandOffTests.cs::Only_web_links_are_opened;A_link_that_is_not_web_is_refused_without_starting_anything | PASS |
| F-APP-C-24 | 面板自身不发通知 | App SPEC:124 | ViewModels/ChecksPanelViewModel.cs(无 INotifications 依赖,纯结构) | 无 | PASS* |
| F-APP-C-25 | PR 状态与默认分支随刷新读:attach/每回合结束/每次操作后/手动 | App SPEC:126 | Git/GitSyncViewModel.cs:1272–1321、1599–1602、1471–1487 | Git/GitSyncViewModelTests.cs::Attach_to_a_tracked_repo_shows_branch_upstream_aheadbehind_remotes_and_branches;NotifyTurnCompleted_refreshes_only_the_attached_session | PASS |
| F-APP-C-26 | 没有上游的分支一次 gh 调用都不发 | App SPEC:126–127 | Git/GitSyncViewModel.cs:1508–1512(Upstream null→PrRead.None) | 无调用计数断言(未发布文案有测试) | PASS* |
| F-APP-C-27 | gh 缺失/未登录/读失败=独立态 Unavailable,不画成"没有 PR" | App SPEC:127 | Git/GitSyncViewModel.cs:1516–1539、2011–2012;Git/GitShipViewModel.cs:25–42 | Git/GitSyncViewModelTests.cs::Gh_not_ready_is_its_own_state_and_never_looks_like_no_pull_request | PASS |
| F-APP-C-28 | request_review git 侧在 App:ReviewChangeSetSource 经 Reviews 注入;无实现无工具;组合根接线 | App SPEC:140 | Git/ReviewChangeSetSource.cs;Composition/AppComposition.cs:596–597;Core/Tools/RequestReviewTool.cs | Git/ReviewChangeSetSourceTests.cs(22 方法);Agents/SessionRuntimeSubAgentLimitsTests.cs:342–365 | PASS |
| F-APP-C-29 | 带 step_id=该步提交对第一父提交;根提交对 EmptyTreeSha(public) | App SPEC:140 | Git/ReviewChangeSetSource.cs:66–92;Workspace/Git/GitCli.Review.cs:14;Core/Tools/RequestReviewTool.cs:105–130 | Git/ReviewChangeSetSourceTests.cs::A_commit_review_diffs_the_commit_against_its_first_parent_and_lists_only_that_commit;A_root_commit_is_reviewed_against_the_empty_tree_with_a_null_base | PASS |
| F-APP-C-30 | 分支模式基准先解析成提交再取 merge-base;无共同历史→Failed;Base 是 sha;默认分支=当前分支只审未提交 | App SPEC:140 | Git/ReviewChangeSetSource.cs:96–135 | Git/ReviewChangeSetSourceTests.cs::A_base_that_moved_ahead_is_compared_from_the_merge_base_not_from_the_base_tip;The_default_base_is_resolved_to_a_commit_so_the_package_can_shorten_it;A_default_branch_that_is_the_current_branch_reviews_the_uncommitted_tree(无共同历史分支无测试) | PASS |
| F-APP-C-31 | 一次 GetDiffSnapshotAsync;IncludeUntracked 进清单不进 diff;8000 字节探测;提交清单上限 1000 可注入、取上限+1、paths 收窄;预期失败 Failed 文本、取消上抛 | App SPEC:140 | Git/ReviewChangeSetSource.cs:137–176、186–210 | Git/ReviewChangeSetSourceTests.cs::A_branch_review_against_a_base_lists_the_commit_the_tracked_edit_and_the_untracked_file;An_untracked_file_is_binary_exactly_when_git_would_say_so_from_its_first_8000_bytes;The_commit_list_is_marked_truncated_only_when_the_branch_has_more_commits_than_the_cap;Paths_narrow_a_branch_review_to_the_named_file;A_diff_over_gits_capture_limit_fails_with_text_that_says_how_to_narrow_it;A_cancelled_token_surfaces_the_cancellation_instead_of_a_failed_result | PASS |
| F-APP-C-32 | 工具归 agents 目录;子 Agent 开关关闭时与 spawn_agent 一起被撤下(SpawningTools) | App SPEC:140 | Agents/SessionRuntime.cs:609–628 | Agents/SessionRuntimeSubAgentLimitsTests.cs:342–365(request_review 随 spawning 开关出现/消失) | PASS |
| F-APP-C-33 | done→提交 worktree 全部未提交改动(add -A);gitStepCommits 默认开、每次现读 | App SPEC:142 | Git/StepCommitter.cs:117–180、318–380;Preferences/AppPreferences.cs:58 | Git/StepCommitterTests.cs(40 方法);Git/StepCommitterSessionRuntimeTests.cs::Marking_a_step_done_commits_it_with_the_subject_and_summary_and_records_the_sha;With_the_preference_off_the_step_is_done_but_not_committed;Git/StepCommitterSettingsTests.cs::Step_commits_are_on_by_default | PASS |
| F-APP-C-34 | 子会话(ParentSessionId 非空)一律跳过;无 worktree 常规会话跳过 | App SPEC:142 | Git/StepCommitter.cs:96–110、131–145 | Git/StepCommitterTests.cs::WorktreeOf_reports_the_session_worktree_and_marks_sub_agent_sessions;:818(SubAgentSkipMessage 断言);A_session_without_a_worktree_is_skipped_and_nothing_is_committed | PASS |
| F-APP-C-35 | 提交说明优先 commit_subject 否则标题;正文 summary;verification 不进说明 | App SPEC:142 | Git/StepCommitter.cs:285–316(BuildMessage/FirstLine) | Git/StepCommitterTests.cs::The_commit_message_is_subject_then_summary_and_never_the_verification;The_result_message_falls_back_to_the_title_when_the_item_has_no_commit_subject | PASS |
| F-APP-C-36 | 失败不回滚 done;工具结果与 TODO 行显示原因+重试提交;失败后 mixed reset 复位 index | App SPEC:142 | Git/StepCommitter.cs:388–404、437–452、466–478;ViewModels/PlanPanelRows.cs(重试行) | Git/StepCommitterTests.cs::A_failing_pre_commit_hook_fails_the_step_with_the_hook_output_and_a_retry_commits_the_same_work;A_failed_commit_unstages_what_it_staged_so_the_retry_starts_from_the_same_changes;Git/StepCommitterSessionRuntimeTests.cs::A_rejecting_hook_leaves_the_step_done_with_the_reason_and_saying_done_again_retries;TodoStepCommitTests.cs::A_done_row_with_a_failure_shows_it_and_offers_the_retry;Retry_reads_the_item_again_and_commits_only_that_item;The_row_has_no_way_to_tick_a_step_done | PASS |
| F-APP-C-37 | 晚取消:提交已存在重读 HEAD 记 sha 报已提交;add 阶段取消照常抛 | App SPEC:142 | Git/StepCommitter.cs:355–380、415–430 | Git/StepCommitterConcurrencyTests.cs::A_cancel_that_arrives_while_a_post_commit_hook_runs_still_records_the_commit_that_exists;Git/StepCommitterTests.cs::A_cancelled_token_throws_before_the_preference_the_resolver_or_git_is_touched | PASS |
| F-APP-C-38 | 同 worktree 按路径门串行,不同 worktree 并行 | App SPEC:142 | Git/StepCommitter.cs:65、166–176 | Git/StepCommitterConcurrencyTests.cs::Two_commits_into_one_worktree_never_overlap_and_the_second_commits_what_arrived_meanwhile;Commits_into_different_worktrees_run_in_parallel;A_commit_blocked_in_one_worktree_does_not_hold_up_another_worktree;One_worktree_reached_through_another_spelling_of_its_path_shares_the_gate | PASS |
| F-APP-C-39 | 计划摘要+步骤 sha/summary/verification 由 PrBodyRenderer 渲染 PR 正文 | App SPEC:142 | Workspace/Git/PrBodyRenderer.cs;PrCreateTool.cs:295;ViewModels/MainWindowViewModel.cs:2470 | Git/GitSyncViewModelTests.cs::The_pull_request_dialog_opens_with_the_description_the_plan_can_already_write(渲染器本体测试在 Workspace 侧) | PASS |
| F-APP-C-40 | 每主轮次开始/结束(含失败/取消)SnapshotTreeAsync 写 turn_checkpoints;非 git 跳过 | App SPEC:157 | Agents/TurnCheckpointService.cs:48–103;ViewModels/SessionWorkspace.cs:1383–1444(finally 内 Complete) | ViewModels/SessionTurnFailureTests.cs::A_failing_checkpoint_begin_leaves_the_session_idle_and_completes_the_turn_once;A_failing_checkpoint_complete_leaves_the_session_idle_and_completes_the_turn_once;A_failing_checkpoint_keeps_the_answer_that_streamed | PARTIAL |
| F-APP-C-41 | 轮次号从已存检查点续接;已完成轮次不覆盖 | App SPEC:157 | Agents/TurnCheckpointService.cs:64–75;Core/Storage/SqliteTurnCheckpointStore.cs:64–92(WHERE tree_after IS NULL) | 无直接测试(续接/不覆盖规则) | PASS* |
| F-APP-C-42 | 文本文件即 AvaloniaEdit 可编辑;默认不换行/换行切换;选择复制撤销重做保存;缓冲随模型不随视图 | App SPEC:249 | ViewModels/FileDocumentViewModel.Editing.cs;Views/FileDocumentView.axaml.cs | Editor/TextEditorLifecycleTests.cs::Undo_survives_detaching_and_reattaching_the_view;Typing_on_one_line_keeps_the_tokens_of_untouched_lines_and_ctrl_c_copies_the_exact_text;Editor/EditorFeatureTests.cs | PASS |
| F-APP-C-43 | >8MiB 明确拒绝;>1Mi 字符跳过 TextMate 仍可编辑保存 | App SPEC:251 | ViewModels/FileDocumentViewModel.Editing.cs:18、326–329;Editing/ForgeEditorBridge.cs:22(1<<20)、211–232 | ViewModels/FileLoadRobustnessTests.cs::A_file_over_16_MiB_is_rejected_with_a_visible_error_and_no_document(实际用 MaxFileBytes+1,名不符实);Editor/HighlightCapTests.cs(5 方法,含边界恰好等于上限) | PASS |
| F-APP-C-44 | 镜像故障可见、保留原生未保存文本、阻止错误保存;保存中后续编辑保持脏 | App SPEC:249 | Editing/ForgeEditorMirror.cs:126–131;ViewModels/FileDocumentViewModel.Editing.cs:59–60(IsDirty 含 faulted、CanSave 排除) | Editor/TextEditorLifecycleTests.cs::A_background_write_faults_the_editor_keeps_the_local_document_and_blocks_saving | PASS |
| F-APP-C-45 | 绝对路径去重复用标签;未保存关闭/替换需确认;加载取消 | App SPEC:249 | ViewModels/MainWindowViewModel.cs:2600–2614(FindFileTab)、2630+(ConfirmDiscardAsync) | ViewModels/FileLoadRobustnessTests.cs::Loading_over_unsaved_edits_is_refused_and_keeps_the_content;A_cancelled_load_throws_leaves_no_document_and_does_not_poison_the_next_load;Concurrent_loads_keep_only_the_newest_file | PASS |
| F-APP-C-46 | 选中/点行号出评论图标;浮层锚定所选末行;行范围+保存草稿/立即发送/取消 | App SPEC:253 | Review/ReviewOverlay.cs;Review/ReviewIconPlacement.cs | Review/ReviewOverlayUiTests.cs::Clicking_a_line_number_shows_the_icon_beside_that_line_and_types_nothing;A_keyboard_selection_over_two_lines_places_the_icon_at_the_last_line_without_changing_text;Review/ReviewIconPlacementTests.cs(5 方法) | PASS |
| F-APP-C-47 | 草稿复用 ReviewStore;单条发送只含该评论;批量合成一次消息;队列接受成功后才标记已发送 | App SPEC:253 | Review/FileReviewController.cs:113–238;Workspace/Review/ReviewDispatcher.cs | Review/FileReviewControllerTests.cs::Single_send_saves_the_file_first_queues_then_marks_and_batch_send_queues_once;A_session_that_cannot_accept_the_message_leaves_the_draft_unsent_and_unmarked;A_message_withdrawn_after_it_was_marked_sent_restores_the_draft | PASS |
| F-APP-C-48 | 发送目标固定文件所属会话;忙时沿既有队列排队 | App SPEC:255 | ViewModels/MainWindowViewModel.cs:2581–2586(按 sessionId 路由);SessionWorkspace.cs:1060–1079(TryQueueReview) | Review/FileReviewControllerTests.cs::Different_owner_scopes_keep_drafts_isolated | PASS |
| F-APP-C-49 | 发送前必须保存成功且内容仍对应锚点;失败保留草稿并提示 | App SPEC:255 | ViewModels/FileDocumentViewModel.Review.cs:21–41(ReviewSaveAsync);Review/FileReviewController.cs:160–172 | Review/FileReviewControllerTests.cs::A_failed_file_save_keeps_the_draft_stored_but_never_queues_or_marks_it | PASS |
| F-APP-C-50 | 编辑后锚点跟随文本;无法重定位明确失效,不用错误行号 | App SPEC:255 | Review/ReviewTextAnchors.cs:9–60(Exact/Moved/Lost,唯一哈希窗口才 Moved);FileReviewController.cs:83(IsLost) | Review/FileReviewControllerTests.cs::Saving_persists_the_exact_file_range_follows_later_edits_and_rejects_an_empty_body;A_draft_whose_lines_were_deleted_cannot_be_saved_or_sent | PASS |
| F-APP-C-51 | 浮层不推开代码、不插占位文本;自身输入/复制不被编辑器截获 | App SPEC:253 | Review/ReviewOverlay.cs(覆盖层) | Review/ReviewOverlayUiTests.cs::The_composer_keeps_its_own_typing_and_copy_and_escape_discards_and_returns_focus_to_the_editor;Markers_follow_scrolling_and_the_composer_stays_inside_a_narrow_window | PASS |
| F-APP-C-52 | diff 复用同一评审浮层:ReviewOverlay 绑 IReviewSurface;File/Diff 两控制器 | App SPEC:257 | Review/IReviewSurface.cs、ReviewSurfaceCore.cs、DiffReviewController.cs、FileReviewController.cs;Changes/DiffEditorView.axaml.cs:629 | Changes/DiffReviewControllerTests.cs(18 方法) | PASS |
| F-APP-C-53 | 选区按行映射(文件,新侧行号);跨文件/只删除/占位行不出图标;拆分只右(新)侧评 | App SPEC:257 | Changes/DiffDocumentBuilder.cs:157–201(TryMapRange);Changes/DiffEditorView.axaml.cs:143(ReviewEditor=split?Secondary:Primary) | Changes/DiffReviewControllerTests.cs::Only_lines_that_carry_a_new_side_line_of_one_file_can_be_commented_on;In_split_mode_comments_use_the_new_side_document;Changes/DiffEditorViewUiTests.cs::Selecting_a_new_side_line_offers_a_comment_and_a_removed_line_does_not;Switching_to_split_moves_commenting_to_the_new_side_editor | PASS |
| F-APP-C-54 | 锚点用真实 FileDiff;发送不保存文件;与评审 panel 同 ReviewStore | App SPEC:257 | Review/DiffReviewController.cs:300–360(SendBatchAsync 无保存调用);MainWindowViewModel.cs:2581(共用 reviews) | Changes/DiffReviewControllerTests.cs::Sending_one_comment_saves_it_queues_the_message_with_path_and_quote_and_marks_it_sent;Saving_stores_the_new_side_range_the_file_path_and_the_base_commit | PASS |
| F-APP-C-55 | 草稿按新文件坐标存;折叠时显示在折叠条;文件折叠时显示在文件头 | App SPEC:257 | Review/DiffReviewController.cs:400–475(DraftInfo/DisplayRow) | Changes/DiffReviewControllerTests.cs::Stored_drafts_of_this_session_and_the_shown_files_reload_in_new_file_coordinates;A_draft_in_a_folded_or_collapsed_region_stays_and_shows_on_the_row_that_hides_it;Expanding_a_fold_moves_the_draft_from_the_fold_row_to_its_own_line | PASS |
| F-APP-C-56 | git 分区:拉取策略 ff-only/rebase/merge、网络超时 10–600(默认120);setter 即校验器;每次操作现读 | App SPEC:60 | Preferences/AppPreferences.cs:50–57、263–272;Git/GitPreferences.cs:13–35;Git/GitSyncViewModel.cs:1782–1783 | Git/GitPreferencesTests.cs(15 方法,含 The_timeout_setter_clamps_to_10_to_600、PanelSettings_reads_live_values_on_every_call);Git/GitSettingsSectionGitTests.cs::The_three_pull_strategy_options_have_localized_labels | PASS |
| F-APP-C-57 | 检查六项:轮询30(10–300)、最长等待30(1–180)、无检查确认3(1–10)、日志尾200(20–2000)、gh 路径(空=PATH、trim) | App SPEC:60 | Preferences/AppPreferences.cs:59–120;Git/GitSettingsSectionViewModel.cs:104–213;Git/GitToolPreferences.cs:27–34 | Git/GitSettingsSectionGitTests.cs::The_poll_interval_setter_clamps_to_10_to_300_seconds;The_max_wait_setter_clamps_to_1_to_180_minutes;The_gh_path_is_trimmed_and_an_empty_one_means_gh_from_PASH;The_wait_options_are_read_from_the_live_preferences;A_value_out_of_range_typed_into_the_section_is_clamped_in_the_preference_and_read_back | PASS |
| F-APP-C-58 | 评审包上限四项(48000/50/200/2000 及夹取)→ReviewLimits 每次评审现读 | App SPEC:60 | Git/GitToolPreferences.cs:37–45;Git/GitSettingsSectionViewModel.cs:150–196;Composition/AppComposition.cs:597 | Git/GitSettingsSectionGitTests.cs::The_review_limits_default_to_the_values_the_package_always_used;The_review_limits_clamp_in_the_section_and_in_the_preferences;A_change_in_the_section_reaches_the_next_review_limits_without_a_restart | PASS |
| F-APP-C-59 | gh 路径经 ConfiguredGh/ConfiguredPrPlatform 每次调用现读;路径不变复用同一 GhCli | App SPEC:60 | Git/ConfiguredGh.cs:45–59;Composition/AppComposition.cs:530–531 | Git/ConfiguredPrPlatformTests.cs(6 方法,转发与真实调用;路径切换复用逻辑无直接测试) | PASS* |
| F-APP-C-60 | GitNotifications 只作迁移兼容只读旧键;实际开关在 notifications 分区 | App SPEC:60 | Preferences/AppPreferences.cs:632、960–962(写回只写 Notifications) | Git/GitSettingsSectionGitTests.cs::A_file_carrying_only_the_legacy_gitNotifications_key_loads_and_rewrites_as_notifications | PASS |

## 非 PASS 项详情

### F-APP-C-03(PARTIAL)compare 回退:宿主判定过松 + 仅未发布分支可用 + 误导禁用提示
证据:
- src/Forge.App/Git/GitSyncViewModel.cs:524 — https 远端用 `trimmed.Contains("github.com/")` 判定 GitHub。`https://notgithub.com/o/r`、`https://evil.com/github.com/x` 均命中,会被当作 GitHub 生成 compare URL;而 scp 形式(:537)却严格 `host == "github.com"`。规格(App SPEC:79–80)要求"只对 github.com 的远端生成——猜一个只会开 404"。同一函数两条路径标准不一致。
- GitSyncViewModel.cs:483–487 — CompareUrl 要求 `Upstream` 为空(仅未发布分支)。规格触发条件是"gh 缺失/未登录时";已发布分支 + gh 不可用(Unavailable)时 HasPullRequestState=false,:629 的 compare 行虽显示但 CompareUrl 为空 → 禁用,用户失去无 gh 建 PR 的回退。
- GitSyncViewModel.cs:631–633 — 该情形禁用提示落到 `ShipNoRemote`("没有配置远程"),而实际有远程,提示与事实不符。
- 测试 Git/GitSyncViewModelTests.cs:760–770 的 DataRow 只覆盖 github/gitlab/本地路径,没有近似宿主(notgithub.com)用例,缺陷未被测试暴露。
缺口:真实调用链 OpenUrl(CompareUrl) 会把非 GitHub 宿主当 GitHub 打开;已发布分支的 gh 缺失回退缺失。
建议处理类型:代码修复(https 分支改为解析 Uri.Host 严格等于 github.com,与 scp 分支对齐;compare 可用性改为"无 PR 状态且远端是 GitHub",不限未发布;禁用提示按真实原因)+ 规格澄清(compare 的可用条件)。

### F-APP-C-40(PARTIAL)轮次检查点:CompleteAsync 失败路径重复发通知
证据:src/Forge.App/ViewModels/SessionWorkspace.cs:1434–1443 — finally 内 CompleteAsync 的 catch 块里 `notifications.Notify(Warning, TurnFailedTitle, …, "checkpoint:"+SessionId)` 连续出现两次(第二块缩进 also 错位,疑似合并/编辑事故)。通知按 key 去重累加(App SPEC:209),用户会看到同一检查点失败 ×2。
闭环:检查点 begin/complete 失败本身处理正确(会话不卡 running、转写有 notice、轮次照常结束,SessionTurnFailureTests 覆盖),仅通知重复。
建议处理类型:代码修复(删除重复 Notify);补一条断言通知次数的测试。

### PASS* 项(实现存在、该不变量无直接测试)
- F-APP-C-06:ShipAlreadyMerged("已经在 main 里了")的点击应答无测试;CanMergeLocally 不做祖先判定为结构性事实。
- F-APP-C-07:菜单无"移除工作区"第二入口,无测试钉住(回归风险:后人加菜单项)。
- F-APP-C-19:世代号守卫(GitSyncViewModel 的 readSource 对应物 ChecksPanelViewModel.cs:419–427)存在,但无"刷新后旧读迟到 post 被丢弃"的竞态测试;现有测试用同步 post,天然绕开竞态。
- F-APP-C-24:面板不发通知为结构性事实(无 INotifications),无测试。
- F-APP-C-26:ReadPullRequestAsync:1508–1512 的"无上游零 gh 调用"无调用计数断言;StubPullRequestPlatform 可数但未用于此断言。
- F-APP-C-41:NextTurnNumberAsync 续接与 SQL `WHERE tree_after IS NULL` 不覆盖规则无直接测试。
- F-APP-C-59:ConfiguredGh.Resolve 的"路径不变复用、变了重建"无直接测试(ConfiguredPrPlatformTests 只测转发)。

## 已实现但缺测试清单

| 不变量 | 代码位置 | 建议测试 |
|---|---|---|
| 未发布分支零 gh 调用 | GitSyncViewModel.cs:1508–1512 | 断言 platform.ViewCalls/AvailabilityCalls==0 |
| 检查面板世代号丢弃迟到旧读 | ChecksPanelViewModel.cs:329、419–427 | 受控 post 队列,刷新后放行旧 post,断言字段不被覆盖 |
| MaxWait 超时停轮询并提示 | ChecksPanelViewModel.cs:377–390 | 注入 time,越预算后断言停+WaitTimeout 文案 |
| 基准与 HEAD 无共同历史→Failed | ReviewChangeSetSource.cs:124–128 | 两个不相关历史的仓库 |
| 本地合并点击报"已在 main 里" | GitSyncViewModel.cs:916–923 | 分支已被 base 包含时点合并 |
| 轮次号续接/已完成不覆盖 | TurnCheckpointService.cs:64–75;SqliteTurnCheckpointStore.cs:64–92 | 重开会话续号;二次 Complete 不改写 |
| ConfiguredGh 路径切换复用/重建 | ConfiguredGh.cs:45–59 | 切换 path lambda,断言 GhCli 实例同一/新建 |
| WebUrlOf 近似宿主拒绝 | GitSyncViewModel.cs:524 | notgithub.com、evil.com/github.com 用例(当前会失败,先修代码) |
| 检查点失败通知只发一次 | SessionWorkspace.cs:1434–1443 | 计数断言(当前会失败,先修代码) |
| 面板无通知/无第二移除入口 | 结构性 | 反射/菜单快照钉住 |

测试质量备注(按 test-anti-patterns):所引测试多为真实 git 沙箱或受控 fake,断言精确值(Loc key、sha、计数),未见恒真断言/吞异常。两处命名问题:FileLoadRobustnessTests.cs:38 名为 "A_file_over_16_MiB…" 实际测 MaxFileBytes+1=8MiB+1(名字误导);GitSettingsSectionGitTests.cs:179 "…gh_from_PASH" 拼写错误(PATH)。

## 程序→规格:未记录行为

1. compare 仅未发布分支可用(GitSyncViewModel.cs:483–487)——规格只说 gh 缺失/未登录触发(见 F-APP-C-03)。
2. TryMapRange 额外拒绝跨 hunk 选区(DiffDocumentBuilder.cs:181–187)——规格只列跨文件/删除/占位;同文件跨 hunk 也不出图标,更严。
3. StepCommitter 兜底主题 "Complete step"(StepCommitter.cs:54)、失败文本尾 800 字符(:50)、路径门字典永不回收(:65,大量 worktree 下单调增长)、路径键全平台 OrdinalIgnoreCase(:65,Linux 大小写敏感路径会并门,偏保守)。
4. ReviewChangeSetSource 拒绝 leading '-' 的 base(ReviewChangeSetSource.cs:103–107)——防选项注入,规格未写。
5. GitSyncViewModel 有界状态表上限 16、LRU 驱逐(GitSyncViewModel.cs:37、1668+)——规格只说"有界",未给数。
6. NeedsSharedBranchConfirmation 对首次推送(Ahead==0 且无上游)也确认(GitSyncViewModel.cs:1776–1780);confirm 委托为 null 时跳过确认直接推(:1777,生产已接线,测试宿主可无)。
7. CanDeleteRemoteBranch 额外排除默认分支本身(GitSyncViewModel.cs:591–596);删除的是上游解析出的远端分支名而非本地名(:1224–1235)。
8. StripRemotePrefix 剥任意 remote 前缀(不限 origin)(GitSyncViewModel.cs:1497–1506)。
9. 合并队列消息覆盖收尾结果行(GitSyncViewModel.cs:1070–1083,测试钉住此顺序)——队列与收尾同时发生时只显示队列。
10. ReviewTextAnchors.MaxSearchLines=20000 重定位搜索窗口;Moved 仅当哈希窗口全文唯一(ReviewTextAnchors.cs:19、14–17)——规格只说"跟随/失效",未给窗口与唯一性规则。
11. ChangesViewModel 文件级 revert:confirm 委托为 null 时直接回滚不确认(ChangesViewModel.Review.cs:463;测试 Revert_file_without_a_configured_confirmation_reverts_directly 钉住);生产 dialogs 缺失时返回 false 是安全的,但两种"无确认"语义不一致。
12. GhPath setter 故意不发 PropertyChanged(GitSettingsSectionViewModel.cs:200–213,防逐字编辑吞空格)——规格未写。
13. 检查面板 MarkReady 成功后 RestartAsync 重开一轮、MaxWait 预算重置(ChecksPanelViewModel.cs:583–585)。
14. 收尾失败文案不含规格所述"清理工作区交给项目树的移除入口"提示(Strings.Git.cs:477–480 只有"已合并,但收尾失败:{1}")。
15. fetch/pull/push 失败发 Error 通知、key "git-op:<dir>"(GitSyncViewModel.cs:1588–1597)——App SPEC:209 有记录,但与 :91 矛盾(见规格质量 1)。

## 规格质量问题

1. **矛盾(最严重)**:App SPEC:91 "某个出货动作失败……不发系统通知:通知仍只属于'等待 PR 检查结束'那一种后台进程" 与 App SPEC:209 "Git 侧——步骤提交被拒(Error)、fetch/pull/push 失败(Error,在 GitSyncViewModel 的操作结果处)" 直接冲突。实现遵循 209(RunOperationAsync 发通知、RunShipWriteAsync 不发)。:91 的"通知仍只属于"是过时叙述,应改为"出货(提交目标菜单)动作失败不发通知;同步三动词失败发通知(见通知节)"。
2. **结构断裂**:App SPEC:88 把"两处刻意不做"与"结果行/数字信息非开关/CanPull/CanPush/rebase 横幅"两个主题用 "。;" 粘在同一行(:88 中段 ";最近一次操作的结果或读失败占那一行……")。数字与动词规则属于 :70 的同步行主题,当前排版使"刻意不做"条目吞并了不相关内容,审计与阅读都易误归属。
3. **历史叙述混入规范**:App SPEC:91–93 "这条纪律同时修掉了原先三处'写完之后行一直 busy'……" 是 changelog;:259 "实现状态:原生 TextPresenter 原型不采纳……构建通过不替代……验证" 是过程记录;:255 "以下交互边界已向用户提问,当前未获可用答复,暂采用默认项" 是问答过程。规范应只留当前契约。
4. **跨规格重复(漂移风险)**:8000 字节未跟踪探测同时写在 App SPEC:60、140 与 Core SPEC:45;评审包四项默认值(48000/50/200/2000)同时写在 App SPEC:60 与 Core SPEC:45;EmptyTreeSha public 同时写在 App SPEC:140 与 Workspace SPEC:17;no-CI 判决规则同时写在 App SPEC:109–110 与 Workspace SPEC:54。当前四处一致,但任一改动需同步多份;建议单一所有者+引用。
5. **模糊/措辞漂移**:App SPEC:98 "提示仍是'清理工作区交给项目树的移除入口'"——实现无此文案,无法判断是字面 UI 提示还是职责说明;App SPEC:73 "没有远程" vs 实现"没有配置远程"(Strings.Git.cs:413);:73/:445 "与 origin 同步" 硬编码 origin,与 :70 tooltip "不写具体 remote,猜一个就是把猜测当事实" 的谨慎标准内部不一致(远端可能不叫 origin)。
6. **未决问题留在规格**:App SPEC:127 "(具体提示与自动切换策略未确认)"、:130 "(模式暂存于内存,未确认是否持久化)"——开放问题应移到缺口清单而非契约正文。
7. **可导航性**:App SPEC:60 单行约 2.5KB,混排设置注册表规则、git 分区 11 项偏好、四个只读分区;建议拆子节(本次审计按行定位成本高)。

## 业务闭环缺口

已验证闭合的环:
- **写纪律**:五个出货写 + fetch/pull/push/abort/continue 全部经 RunShipWriteAsync/RunOperationAsync,busy 置位、二次点击 no-op、读随上下文取消、写跑完为止、EndWork 补跑排队刷新;切换会话不污染新工作区(GitSyncWorktreeRaceTests 4 方法)。无缺环。
- **确认闸门**:共享分支推送、分歧合并提交、删远端分支、文件级 revert 生产路径均有确认;合并对话框/删除确认在首个 await 前捕获工作区与根路径。
- **合并收尾失败不改写合并事实**:FinaleResult 恒带"已合并"前缀,异常也只追加收尾失败说明;测试覆盖。
- **轮询世代与取消**:关闭/切换/重开均 Dispose(取消 watch+lifetime,不 dispose source 防 ObjectDisposedException);fix 读日志期间关闭则不交付(测试覆盖)。
- **步骤提交失败不回滚 done**:失败持久化原因、index 复位、重试入口、晚取消回收 sha,全链有测试。
- **评审锚点与草稿坐标**:保存→锚点校验→队列接受→标记→撤回恢复,文件侧与 diff 侧均闭合;新侧坐标、折叠条/文件头显示有测试。

真实缺口(按影响排序):
1. **无 gh 的已发布分支建 PR 回退缺失**(F-APP-C-03):gh 缺失/未登录且分支已发布时,compare 行禁用且提示"没有配置远程",UI 无路径创建 PR,只能手拼 URL。规格承诺的回退在此状态不可达。
2. **近似 GitHub 宿主误判**(F-APP-C-03):notgithub.com 之类远端会生成并打开错误 compare 页,违反"只对 github.com 生成"的红字理由(猜 URL 开 404)。
3. **检查点失败通知 ×2**(F-APP-C-40):用户可见的重复告警,稀释真通知(与 :209 "弹出来只会稀释真通知"的设计理由相悖)。
4. **证据缺口而非代码缺口**:世代号竞态、MaxWait 停轮询、零 gh 调用、轮次续接、ConfiguredGh 复用、无共同历史 Failed 六条不变量实现存在但无直接测试(见缺测清单),回归时无保护。
5. **低危宿主降级**:confirm 委托为 null 时推送共享分支跳过确认、文件 revert 直接执行(生产已接线,仅影响自建宿主/测试);两处"无确认"语义不一致,建议统一为"无确认能力则拒绝破坏性动作"。