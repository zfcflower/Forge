# App E(TODO/计划面板/审批队列/会话生命周期/自动化)审计发现

范围:`src/Forge.App/SPEC.md` 行 298(TODO 面板)、301–309(计划面板 W7)、334–349(审批队列与 Agent 扩展)、350–378(会话生命周期·进行中)、379–389(自动化)、402(ask_user 问题卡)。只读审计;Core 侧 todo 工具/审批引擎/自动化引擎另路审。测试证据判读前已读 `.github/skills/test-anti-patterns/SKILL.md`。

结论等级:PASS=实现符合且有有效测试;PASS*=实现存在但缺(或仅弱)测试;PARTIAL/FAIL/MISSING/SPEC_GAP/UNVERIFIED 见详情。生命周期段已区分「已落地」与规格自述「尚未做」。

## 功能点矩阵

### TODO 面板(SPEC:298)
| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-E-01 | 结构顺序:置顶输入行→进行中分组→待排散项→完成区 | 298 | ViewModels/Panels.cs:1962(Rebuild) | TodoPanelTests::A_group_shows_its_steps_in_order_with_its_progress 等 | PASS |
| F-APP-E-02 | 置顶行 Enter 添加 `TodoOrigin.User` 待排散项(非分组) | 298 | Panels.cs:1948(AddAsync);Views/TodoToolView.axaml.cs:43 | TodoPanelTests::Queueing_an_item_reaches_the_store_as_a_user_added_loose_row | PASS |
| F-APP-E-03 | 分组行=图标+标题+进度;未完成组恒展开、无删除/折叠入口 | 298 | Panels.cs:1676(TodoGroupViewModel,无删除命令) | TodoPanelTests::A_finished_group_folds_into_the_done_section_and_opens_on_demand | PASS |
| F-APP-E-04 | 步骤缩进+1px 引导线居中状态图标列(30px) | 298 | Views/TodoToolView.axaml(布局) | 无(纯 XAML 几何,未见专测) | PASS* |
| F-APP-E-05 | 四态状态图标不可点+配色 | 298 | Panels.cs:1655(StatusKey);Resources/Icons.axaml | TodoStepCommitTests::The_view_shows_the_short_id…(视图) | PASS* |
| F-APP-E-06 | 分组状态由步骤推导、从不存储 | 298 | Panels.cs:1758(DeriveStatus→TodoStatusRule.Derive) | TodoPanelTests::An_empty_group_is_pending;A_group_shows_its_steps… | PASS |
| F-APP-E-07 | 只读契约:无勾选框、不改状态 | 298 | Panels.cs:1572(TodoRowViewModel 无改状态命令) | TodoStepCommitTests::The_row_has_no_way_to_tick_a_step_done;The_view_has_no_check_box… | PASS |
| F-APP-E-08 | 行尾×仅悬停/聚焦;完成区无×无"清除已完成" | 298 | Panels.cs:1631(IsReadOnly);TodoToolView.axaml | TodoStepCommitTests::The_view_has_no_check_box_and_no_delete_button_for_a_finished_row | PASS |
| F-APP-E-09 | 人工添加行右端线框 user 图标 | 298 | Panels.cs:1627(IsUserAdded) | TodoStepCommitTests::The_view_marks_a_row_the_user_queued_with_the_line_user_icon | PASS |
| F-APP-E-10 | 拖拽仅 Pending 发起、`TodoDragSource`、同层判定、MoveAsync 夹取、不跨组 | 298 | Panels.cs:1624(CanDrag);TodoToolView.axaml.cs:12/94/110(SameLayer) | TodoPanelTests::Delete_and_move_reach_the_store(VM;视图 SameLayer 无专测) | PASS* |
| F-APP-E-11 | 完成组折一行可展开、完成散项列组后 | 298 | Panels.cs:1974/1983(Rebuild 分区) | TodoPanelTests::A_finished_group_folds…;Finished_loose_items_move_to_the_done_section_read_only | PASS |
| F-APP-E-12 | 状态行仅无会话/空/错;四集合一次刷新重建 | 298 | Panels.cs:1827(HasStatus)/1862(RefreshAsync 世代) | TodoPanelTests::The_status_line_reports_a_missing_session_and_a_failing_store | PASS |

### 计划面板 W7(SPEC:301–309)
| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-E-13 | 开/关/切换会话自动关/无会话禁用 | 301 | ViewModels/MainWindowViewModel.Plan.cs:38/52/71 | PlanPanelShellTests::Open_plan_needs_an_active_session;Switching_the_session_closes_the_panel;PlanPanelViewHeadlessTests::Escape_closes_the_panel | PASS |
| F-APP-E-14 | 居中卡片 1120×760、左摘要步骤/右下一步出口历史 | 301 | Views/PlanPanelView.axaml(plan-card) | PlanPanelViewHeadlessTests::The_panel_renders_its_parts_and_the_summary_markdown | PASS |
| F-APP-E-15 | 摘要 Markdown 渲染、文本变才重建、更新时间戳、空提示 | 302 | PlanPanelViewModel.cs(摘要);Resources/MarkdownStyles.axaml | PlanPanelViewModelTests::The_summary_shows_its_text_and_stamp…;PlanPanelViewHeadlessTests::A_refresh_with_an_unchanged_summary_keeps_the_markdown_controls | PASS |
| F-APP-E-16 | 扁平虚拟化步骤、组序同 PrBodyRenderer、按 key 就地更新 | 303 | PlanPanelRows.cs;PlanPanelViewModel.cs | PlanPanelViewModelTests::Rows_follow_group_order…;A_step_finishing_patches_its_row_in_place… | PASS |
| F-APP-E-17 | 只读契约+交回 agent:不覆盖草稿/附件、不可用清草稿 | 304 | MainWindowViewModel.Plan.cs:109(HandoffToAgent) | PlanPanelShellTests::Handoff_does_not_overwrite_an_unsent_draft;Handoff_does_not_send_the_users_pending_attachments;Handoff_on_a_read_only_session… | PASS |
| F-APP-E-18 | 下一步=首个 in_progress 否则首个 pending;全做完提示 | 305 | PlanPanelViewModel.cs(NextStep) | PlanPanelViewModelTests::Next_step_is_the_in_progress_step…;Next_step_is_none_when_only_paused_or_done… | PASS |
| F-APP-E-19 | 修订历史最近 50、新→旧、预览/复制、按存储序、裁剪退出 | 306 | PlanPanelViewModel.cs:316(ListPlanRevisionsAsync,RevisionTake) | PlanPanelViewModelTests::Revisions_are_newest_first…;A_previewed_revision_the_store_no_longer_holds_exits… | PASS |
| F-APP-E-20 | 三出口:复制/导出/开 PR 同一 ComposePullRequestCommand | 307 | MainWindowViewModel.Plan.cs:96(OpenPlanPullRequest);PlanPanelViewModel.cs:258 | PlanPanelViewModelTests::Export_writes_the_rendered_plan;The_open_pull_request_exit_delegates…;PlanPanelShellTests::The_open_panels_pull_request_button_follows_the_git_rows_gate… | PASS |
| F-APP-E-21 | 后台四读+世代丢弃+补读合并+Dispose 退订+语言不重读 | 308 | PlanPanelViewModel.cs:191/280/313 | PlanPanelViewModelTests::A_store_change_during_a_read_coalesces…;A_store_change_after_dispose_no_longer_reads…;A_language_switch_re_resolves… | PASS |
| F-APP-E-22 | 左列状态行:读取/失败/交接/导出回执不被刷新冲掉 | 309 | PlanPanelViewModel.cs:244 | PlanPanelViewModelTests::A_read_that_succeeds_again_clears_the_failure_but_not_a_handoff_report | PASS |

### 审批队列与 Agent 扩展(SPEC:334–349)
| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-E-23 | UI 线程队列、420px 可折叠叠加层、来源会话、不阻塞、计数 | 335 | Approval/ApprovalQueue.cs:40;ApprovalQueueOverlay.axaml:7/442;MainWindowViewModel.cs:2880 | ApprovalQueueTests::Enqueue_from_a_pool_thread…;Concurrent_enqueues_from_many_sessions_all_land | PASS |
| F-APP-E-24 | SessionApprovalHandler 先查规则→入队;取消/释放撤卡 | 336 | Approval/SessionApprovalHandler.cs:104/137/146 | SessionApprovalHandlerTests::Remembered_rule_allows_without_a_card;Cancellation_throws_withdraws_the_card… | PASS |
| F-APP-E-25 | ApprovalRules 键/作用域/只记允许;会话内存、项目/全局文件;损坏.bak;不可记住项 | 336 | Approval/ApprovalRules.cs:386(IsRememberable) | ApprovalRulesTests::Round_trip_persists_project_and_global…;Corrupt_file_reports_a_problem…;Extension_delete_can_never_be_remembered | PASS |
| F-APP-E-26 | 策略 extension_list/create/update/disable=Allow、delete=Ask | 337 | Core SessionPolicy(App 经 Router 引用);SessionToolCallRouter.cs:72 | SessionToolCallRouterTests::Deny_policy_is_refused_without_a_card…(策略本体属 Core) | PASS |
| F-APP-E-27 | MCP 卡服务器段+仅键名+即时撤销 | 338 | Approval/McpApprovalServer.cs;Composition/McpServices.cs:183;ToolApprovalCardViewModel.cs | ToolApprovalCardMcpTests::The_env_and_header_texts_are_exactly_the_key_names;Remembered_rules_are_counted…revoked_together | PASS |
| F-APP-E-28 | ExtensionAuthoring 暂存→诊断直返→卡→批准 commit/拒 discard;重启 pending;热加载 | 339 | Approval/ExtensionAuthoringService.cs:137/200/232/320 | ExtensionAuthoringServiceTests::Diagnostics_are_invalid_and_never_reach_the_user;Approve_commits_exactly_the_shown_hashes…;Restore_pending_requeues… | PASS |
| F-APP-E-29 | extensions 设置分区:启停/删二次确认/待审批/按作用域撤销 | 340 | Approval/ExtensionsSettingsSectionViewModel.cs | ExtensionsSettingsSectionTests(存在) | PASS |
| F-APP-E-30 | SessionToolCallRouter 按 run 注册会话路由;extension_* 永不放行;注册/注销 | 341 | Approval/SessionToolCallRouter.cs:31/63/72 | SessionToolCallRouterTests::Unknown_or_unregistered_session_fails_closed;A_stale_unregister_does_not_remove_a_newer_scope | PASS |
| F-APP-E-31 | 子 Agent 别名归根 scope、卡显示根会话、释放清别名 | 342 | Agents/SessionRuntime.cs(别名);SessionToolCallRouter.cs | SubAgentAliasAndConcurrencyTests::A_child_alias_resolves_and_approves_through_the_root_scope…;Aliases_of_nested_children_are_removed_on_finish_and_on_dispose… | PASS |
| F-APP-E-32 | 插件命令 ToolName `plugin-command:`、不可记住、逐次确认 | 343 | ApprovalRules.cs:386;ToolApprovalCardViewModel.cs:137/175 | SubAgentAliasAndConcurrencyTests::A_plugin_command_decision_is_never_remembered;A_plugin_command_named_todo_still_gets_a_card… | PASS |
| F-APP-E-33 | 同会话多工具卡并行、按卡实例决策/取消、不加跨组件闸 | 344 | ApprovalQueue.cs(每卡独立 TCS);SessionApprovalHandler.cs | SubAgentAliasAndConcurrencyTests::Two_concurrent_cards_of_one_session_are_decided_and_cancelled_independently | PASS |
| F-APP-E-34 | 插件卡撤回=Withdrawn≠Deny、留稿;工具卡撤回=Deny | 345 | ExtensionApprovalCardViewModel.cs:195;ApprovalCardViewModel.cs:96;ExtensionAuthoringService.cs:625 | ExtensionWithdrawalAndRulesTests::Disposing_the_extension_card_is_a_withdrawal_not_a_denial;Session_release_withdraws_the_card_without_discarding_the_draft | PASS |
| F-APP-E-35 | 提交前后清规则;rules:null 损坏;加载不 Faulted;RequestAsync 降级询问 | 346 | ExtensionAuthoringService.cs:505/540;SessionApprovalHandler.cs:113 | ExtensionWithdrawalAndRulesTests::Remembered_rules_are_cleared_before_the_commit…;Rules_null_is_reported_as_a_load_problem | PASS |
| F-APP-E-36 | 工作流卡显示声明工具(名/描述/schema) | 347 | ExtensionApprovalCardViewModel.cs;ExtensionAuthoringService.cs:TryMap | ExtensionWithdrawalAndRulesTests::The_workflow_tool_is_shown_on_the_card | PASS |

### 会话生命周期(SPEC:350–378,进行中)
| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-E-37 | 单实例锁:开库前 TryAcquire、占用即退出、不 IPC | 351 | App.axaml.cs:43/101 | 未见专测(启动测试未含锁;ShutdownAsync 释放未追踪) | PASS* |
| F-APP-E-38 | 只读查看:非 Active 只读、无 runtime、继续禁用规则(目录/git) | 352 | MainWindowViewModel.cs:1060(AttachReadOnlyCommands);SessionWorkspace.cs | SessionLifecycleShellTests::Opening_an_archived_session_keeps_its_status…;RemovalShellTests::Continue_is_judged_from_the_worktree_list… | PASS |
| F-APP-E-39 | 回合前校验:Active/冲突/操作中、草稿回队、读库失败放行 | 353 | MainWindowViewModel.cs:1023(CheckTurnAsync);SessionWorkspace.cs:1494 | SessionLifecycleShellTests::A_turn_on_a_session_archived_elsewhere…;A_turn_in_a_workspace_with_a_merge_conflict…;ForkMergeShellTests::A_turn_in_a_workspace_that_is_operating… | PASS |
| F-APP-E-40 | 占用冲突对话框:点名持有者、带意图接手/查看/取消 | 354 | MainWindowViewModel.cs:1197(ShowOccupiedAsync)/120(SessionCreateOccupiedException) | SessionLifecycleShellTests::Continuing_a_session_in_an_occupied_workspace_shows_the_occupied_dialog…;Viewing_the_holder… | PASS |
| F-APP-E-41 | 接手:空闲→守卫→交接提交→上下文→单事务换手→评审复制;失败写 sha | 355 | Lifecycle/HandoverFlow.cs:189/237/242 | HandoverFlowTests::A_switch_that_fails_after_the_commit_names_the_commit;A_failed_summary_keeps_the_commit…;Summary_mode…copies_todos | PASS |
| F-APP-E-42 | 【规格自述尚未做】摘要沿继承链取父;对话框选模型/档位及相关提示 | 356 | 无(未落地) | 无 | MISSING(规格已标未落地,非缺陷) |
| F-APP-E-43 | 分叉:实时 HEAD、先提交、上下文先于 worktree、lineage 承根不写 parent、不复制 todos/计划/评审、失败收回 | 357 | Lifecycle/ForkFlow.cs:238/321/392/68 | ForkFlowTests::A_fork_of_a_fork_keeps_the_original_lineage_root;A_session_row_that_cannot_be_written_takes_the_new_worktree_back;ForkMergeShellTests::Commit_first_holds_the_source… | PASS |
| F-APP-E-44 | 合并回/同步:定位目标、降级推送、前后空闲、重读分支、--no-ff、冲突/中止、移除分叉 | 357 | Lifecycle/MergeFlow.cs | MergeFlowTests::A_target_switched_to_another_branch_after_the_preview_merges_nothing;A_conflict_leaves_the_target…abort_restores_it;ForkMergeShellTests::A_merge_back_holds_the_target…offers_the_removal | PASS |
| F-APP-E-45 | 回合前另读"工作区操作中"拒绝 | 358 | MainWindowViewModel.cs:1050(IsOperating) | ForkMergeShellTests::A_turn_in_a_workspace_that_is_operating_is_rejected_and_the_draft_stays | PASS |
| F-APP-E-46 | 冲突快照:两树+活动标签、占用键去重、4 并发、Epoch 丢弃、读失败保留 | 359 | MainWindowViewModel.cs:535/601;Composition/WorkspaceStates.cs:77 | (快照并发/Epoch 逻辑见代码;未见针对 4 并发/Epoch 的独立命名测试) | PASS* |
| F-APP-E-47 | 启动:HealMissingDirectories→合成 storage_notices 确认;未知留库;无对话框不确认 | 360 | MainWindowViewModel.cs:1916/1953 | SessionLifecycleShellTests::Startup_heals_a_session_whose_folder_is_gone_and_shows_the_notice_once;A_shell_without_dialogs_keeps_the_startup_notice… | PASS |
| F-APP-E-48 | 树移除标记:Deleted/目录缺失/已回收/git prunable/未登记/未知 | 362 | ViewModels/ProjectTree.cs;TreeMenu | RemovalShellTests::A_workspace_git_marks_prunable_is_marked_stale…;A_failed_or_empty_worktree_list_leaves_the_workspace_unverified… | PASS |
| F-APP-E-49 | 移除会话:计划/守卫/回收站→prune→软删;先离窗清理终端/标签/runtime | 363 | Lifecycle/RemovalFlow.cs;MainWindowViewModel.Removal.cs | RemovalFlowTests::A_prunable_worktree_is_guarded_by_what_git_says…;RemovalShellTests::The_removed_session_leaves_the_window_before_its_directory_goes_to_the_trash | PASS |
| F-APP-E-50 | 移除工作区:列全部根会话、归档 X 并移除、项目根/常规拒 | 364 | Lifecycle/RemovalFlow.cs | RemovalFlowTests::The_project_root_is_never_a_removable_workspace;RemovalShellTests::Archive_and_remove_keeps_the_archive_and_says_so… | PASS |
| F-APP-E-51 | 恢复会话/工作区:重建目录、占用进对话框、稀疏提示 | 365 | Lifecycle/RemovalFlow.cs;MainWindowViewModel.Removal.cs | RemovalShellTests::A_restore_rebuilds_the_worktree_from_its_branch_and_says_the_sparse_settings_are_not_restored;A_restore_that_rebuilt…could_not_activate… | PASS |
| F-APP-E-52 | 彻底删除:PurgeAsync+工件/评审清理+purge-pending 日志重试 | 366 | Lifecycle/PurgeCleanup.cs;RemovalFlow.cs | PurgeCleanupTests::A_cleanup_that_fails_is_reported_kept_and_cleaned_up_by_the_next_start;RemovalShellTests::A_purge_deletes_the_records_and_the_artifact_folder | PASS |
| F-APP-E-53 | 关闭项目:忙拒→缓冲守卫→CloseAsync→成功才关标签 | 367 | MainWindowViewModel.Removal.cs | RemovalShellTests::Close_project_is_refused_while_a_session_busy…;Close_project_is_refused_while_a_workspace…merge_conflict | PASS |
| F-APP-E-54 | 树菜单纯函数、开菜单只读内存、刷新读 git 缓存 15s | 368 | ViewModels/TreeMenu.cs;ProjectTree.Menu.cs | TreeMenuTests;TreeMenuRowTests;RemovalShellTests::The_worktree_list_is_read_once_per_lifetime…never_by_a_menu | PASS |
| F-APP-E-55 | 改名分支/链接已有工作区/稀疏检出 | 369–371 | Lifecycle;MainWindowViewModel.Menu.cs | MenuShellTests;ForkTreeTests(存在) | PASS |
| F-APP-E-56 | WorkspaceStates 进程级线程安全、占用键两组标志 | 372 | Composition/WorkspaceStates.cs:44/77/134 | (经生命周期 shell 测试间接覆盖) | PASS |
| F-APP-E-57 | 提供商缓存配置只读 settings.json(5m/1h、非法格式错)、无 UI | 373 | SessionInstrumentsViewModel.cs:306(CacheTtl 显示) | 未见解析规则专测(仅观察到显示侧) | UNVERIFIED |

### 自动化(SPEC:379–389)
| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-E-58 | 组合根 Create、恢复标签后 Attach、宿主不抢焦点/列会话/报前台 | 379 | Automations/AutomationComposition.cs;AutomationService.cs:186;MainWindowViewModel.Automations.cs:40 | (宿主 OpenAsync 不抢焦点逻辑见代码;AutomationBannerTests 间接) | PASS* |
| F-APP-E-59 | 邮箱投递:忙等待 WaitingIdle、接手/归档/移除/关闭撤回、不打断 | 380 | Automations/SessionMailbox.cs:30/72;SessionWorkspace.cs:1091 | SessionMailboxTests::Session_taken_over_while_waiting_is_withdrawn…;Busy_session_is_waited_for…;Session_closed_by_the_user_is_withdrawn_even_when_idle | PASS |
| F-APP-E-60 | 会话环境 Scratch/FixedFork/Scout/Remind;ReadOnlyTools;回合专用 Agent 用后恢复 | 381 | Automations/PromptRunner.cs;SessionWorkspace.cs:1108(ApplyRootDefinition/恢复) | PromptRunnerTests::The_read_only_agent_travels_with_the_message;AgentFor_read_only_keeps_only_the_looking_tools…;Scout_launches_a_scout_child… | PASS |
| F-APP-E-61 | QuotaResume:记 note、抖动 0–60s、间隔 10s、≤5 轮、用户取消删 note/关闭保留、重启续跑 | 383 | Automations/QuotaResume.cs:113/120/195;AutomationService.cs:280 | QuotaResumeTests::A_user_cancel_outside_a_shutdown_removes_the_note;A_cancel_during_shutdown_keeps_the_note;Quota_failing_again_after_the_maximum_rounds_fails… | PASS |
| F-APP-E-62 | CiChain:5 分钟、Scout 分析、只推 head 永不 base/强推、fork 不推、预算 3 | 384 | Automations/CiChain.cs:57/235 | CiChainTests;GitCliCiGitTests(推送守卫) | PASS |
| F-APP-E-63 | SessionWatch:15 分钟停滞/工具 60 分钟/审批 10 分钟/后台完成,只通知 | 385 | Automations/SessionWatch.cs | SessionWatchTests::A_tool_call_silent_for_an_hour_is_reported_as_a_long_command;Check_reports_an_approval_that_waited_ten_minutes… | PASS |
| F-APP-E-64 | ModelCatalogRefresh:每日 05:00、单飞、ETag、启动补跑只抓快照 | 386 | Automations/ModelCatalogRefreshRunner.cs | Providers/ModelCatalogRefreshTests;ModelCatalogStorageTests | PASS |
| F-APP-E-65 | Housekeeping:prune/空暂存/修剪历史/闲置归档;OnSessionsArchived UI 收尾 | 387 | Automations/Housekeeping.cs;AutomationService.cs:88;MainWindowViewModel.Automations.cs:90 | HousekeepingTests::Idle_automation_sessions_are_archived_unless_open…;A_failing_window_cleanup_does_not_fail_the_run | PASS |
| F-APP-E-66 | 界面:横幅倒计时/取消、树闪电标记、设置启停/历史、本地化校验错误 | 388 | MainWindowViewModel.Automations.cs:180(TickAutomationBanners);Automations/Settings | AutomationBannerTests::A_quota_banner_counts_down_against_the_clock;AutomationDefinitionStoreTests | PASS |

### ask_user 问题卡(SPEC:402)
| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-E-67 | 问题处理器→审批浮层问题卡(选项+自由文本)、作答/取消继续 | 402 | Approval/SessionQuestionHandler.cs:34;UserQuestionCardViewModel.cs | UserQuestionTests::Handler_returns_the_submitted_answers_and_removes_the_card;Cancelling_the_wait_withdraws_the_card_and_reports_cancelled | PASS |
| F-APP-E-68 | 多选 Min/Max 约束、自由文本算一次、跳过、全跳过禁提交 | 402 | UserQuestionCardViewModel.cs:70/176/213/262 | UserQuestionBoundsSkipQueueTests::Max_refuses_further_picks…;All_skipped_cannot_be_submitted…;Submit_with_a_skipped_question_completes_with_the_skipped_flag | PASS |
| F-APP-E-69 | 会话级串行 FIFO、暂存取消即 Cancelled 不显示、"还有 N 个"、跨会话不阻塞 | 402 | Approval/ApprovalQueue.cs:52/225/245 | UserQuestionBoundsSkipQueueTests::Parallel_requests_show_one_card_and_the_next_after_an_answer;Cancelling_a_queued_request_completes_cancelled_without_showing_it;Different_sessions_do_not_block_each_other | PASS |

## 非 PASS 项详情

### F-APP-E-42 接手未落地项(MISSING,规格已自述「尚未做」,非缺陷)
- 证据:SPEC:356 明列「摘要只用旧会话自有消息,不沿继承链取父会话;对话框不选模型/推理档位,也没有缓存剩余时间、接近压缩阈值、前缀指纹不符的提示,换模型时禁用完整继承一项随模型选择一并缺席」。`HandoverFlow.cs` 的 `BuildNewSessionAsync` 仅消费旧会话自有消息,无模型选择参数。
- 现状核对:与规格「进行中」标注一致,属预期未落地。建议处理类型:保持 MISSING 跟踪,待后续波次;不需当前改动。

### F-APP-E-37 单实例锁(PASS*)
- 证据:`App.axaml.cs:43` 开库前 `SingleInstanceLock.TryAcquire(dataRoot)`;`:101 AlreadyRunningWindow` 仅显示「已在运行」窗口,无 IPC/转交/前置。符合 SPEC:351。
- 缺口:未见针对「拿不到锁即退出」「锁持有到 ShutdownAsync 结束」的专门测试;`MainWindowStartupTests`/`HeadlessSmokeTests` 未含锁断言。释放时机未追踪。建议:补一条占用锁启动即退出的测试。

### F-APP-E-04 / 05 / 10 TODO 视图几何与拖拽守卫(PASS*)
- F-APP-E-04/05:30px 引导线、四态图标配色为纯 XAML,未见几何/图标断言测试(`IconGeometryTests` 针对图标素材而非 TODO 行布局)。
- F-APP-E-10:VM 侧 `MoveAsync` 有 `TodoPanelTests::Delete_and_move_reach_the_store`;但视图 `SameLayer`(TodoToolView.axaml.cs:110)的同层守卫无 headless 拖拽专测。
- 闭环观察(低危):`SameLayer` 仅比较 `GroupId` 相等与 id 不同,未排除「完成区」行作为落点。因仅 Pending 行可发起拖拽、且完成散项与待排散项同属 `group_id=NULL` 层,理论上可把待排项拖到完成散项位置,交由存储夹取。规格「待排区之间」措辞未明确是否排除完成区(见规格质量#4)。核心契约(不跨组、仅 Pending、存储夹取)成立,故不判 FAIL。建议:补视图级同层/跨组拒绝的 headless 用例,并在规格澄清完成区是否为合法落点。

### F-APP-E-46 冲突快照并发/Epoch(PASS*)
- 证据:`MainWindowViewModel.cs:535` 去重+`MaxDegreeOfParallelism=4`(:601)+`states.Epoch`/`TrySetConflictedFromRead`(:577)+操作中跳过+读失败保留,逐条符合 SPEC:359。
- 缺口:逻辑正确但未见以「4 并发上限」「Epoch 竞态丢弃」为名的独立测试;多经 shell 集成间接覆盖。建议:补 Epoch 竞态(读期间操作开始/写标志)的定向测试。

### F-APP-E-58 自动化宿主接线(PASS*)
- 证据:`MainWindowViewModel.Automations.cs:40 OpenAsync` 打开后恢复 `previous` 活动会话(不抢焦点)、拒非 Active/子/未知;`AutomationService.Attach`(:186)在恢复后启动。符合 SPEC:379。
- 缺口:「不抢焦点」这一行为未见以焦点断言的专测(`AutomationBannerTests` 只测横幅)。建议:补 OpenAsync 保持原活动会话的测试。

### F-APP-E-57 提供商缓存配置(UNVERIFIED)
- 证据:仅观察到 `SessionInstrumentsViewModel.cs:306` 的 CacheTtl 显示侧;`provider.sendPromptCacheKey`/`promptCacheTtl=5m/1h`、非法值格式错误的解析规则未追踪到存储实现,未见专测。
- 建议:定位解析存储并补非法值回落/格式错误测试;本路未深入,标 UNVERIFIED 而非 PASS。

## 已实现但缺测试清单(PASS*)
- F-APP-E-04/05:TODO 行 30px 引导线几何、四态图标配色(XAML,无断言)。
- F-APP-E-10:视图 `SameLayer` 同层/跨组拖拽守卫(VM MoveAsync 已测,视图未测)。
- F-APP-E-37:单实例锁占用即退出、持有至 ShutdownAsync。
- F-APP-E-46:冲突快照 4 并发上限、Epoch 竞态丢弃的定向测试。
- F-APP-E-58:自动化 OpenAsync 不抢焦点(保持原活动会话)。
- (F-APP-E-57 为 UNVERIFIED,另列。)

## 程序→规格:未记录行为
- `TodoListViewModel.RetryCommitCommand`(Panels.cs:1600/1900):TODO 行「重试提交」。**已在 SPEC:141(StepCommitter)记录**,但 TODO 面板段(298)的动作枚举「新建/删除/调序」未含它——属规格内部不一致,而非完全未记录(见规格质量#2)。
- `CountPendingApprovals`(MainWindowViewModel.cs:2878):无会话的插件审批卡「不计入任何会话计数」。SPEC:335 只说「每张卡标注来源会话」,未明确重启恢复的无会话插件卡不计入会话计数。建议在审批段补一句。
- `ApprovalQueue` 对重复 Id 卡「拒绝二次入队、保留先者」(ApprovalQueue.cs:47)与 `Superseded` 决策(新稿顶替旧卡、旧卡不得 commit/discard):SPEC:339 提到新稿顶替,但「Superseded  neither commit nor discard」这一独立决策态未在 App 规格显式命名(代码 ApprovalCardViewModel.cs:16 有载)。建议补记。
- 自动化 `SessionMailbox` 轮询间隔默认 1s(SessionMailbox.cs:24):规格未给默认轮询值(仅「等它空闲」)。属实现默认值,建议记录或注明非契约。

## 规格质量问题
1. **跨规格重复(ask_user 约束)**:`Forge.Core/SPEC.md:111` 与 `Forge.App/SPEC.md:402` 都定义了多选 min/max、「自由文本算一次选择」、全跳过→「忽略」的同一语义。两处独立陈述易漂移。Core:153 已正确把「按会话串行」委托给 App(好样例);建议 min/max/skip 语义也单点归属(Core 定契约、App 引用),避免双写。
2. **App 内部不一致(TODO 动作枚举)**:SPEC:298 称面板「只读展示 + 新建/删除/调序」,但 SPEC:141 与代码/测试均提供「重试提交」动作。298 的动作清单不完整,读者据 298 会漏掉重试。建议在 298 补「重试失败步骤提交」或交叉引用 141。
3. **审批术语三处交叠、缺互链**:App SPEC:334–349 的「审批」同时覆盖工具记住规则(`approval-rules.json`)与插件卡;`Forge.Approval/SPEC.md`(status=draft)是插件源码哈希引擎(`approvals.db`);Core SPEC:17/55 是策略求值。App:336「插件新版本获批清除新旧工具名记住项」与 Forge.Approval「跨版本 commit 后对旧版本 Revoke」是耦合行为却分述两处、无互链,且「审批/approval」同名易混。建议 App 审批段显式区分「工具记住规则」与「插件哈希审批(见 forge-approval)」并加 references。
4. **模糊(拖拽落点)**:SPEC:298「落点先判同层(待排区之间/同一分组之间)」未明确「完成区散项」是否为合法落点,导致实现 `SameLayer` 仅按 GroupId 判层、未排除完成区(见 F-APP-E-10 低危观察)。建议澄清。
5. **历史叙述**:SPEC:298「旧的顶部'3/8 完成'与底部'清除已完成'已删除」、362「原硬删除入口已去掉」、终端段「已知遗留/延后项」等为沿革叙述,混在规范性声明中。建议移入变更注记,保持契约段现在时。

## 业务闭环缺口
- **审批卡/问题卡会话级串行/取消/撤卡(Withdrawn)**:闭环完整。问题卡每会话 FIFO 单显、暂存取消即 Cancelled 不显示、释放/关闭撤卡(ApprovalQueue.cs:52/85/120);插件卡撤回=Withdrawn 留稿、工具卡=Deny(ExtensionApprovalCardViewModel.cs:195 vs ApprovalCardViewModel.cs:96),均有定向测试。无缺环。
- **扩展提交阻塞到用户决定**:闭环完整。`SubmitAsync` 在 `card.Decision.WaitAsync(ct)` 阻塞,诊断直返不入队,批准按卡上哈希 commit、拒 discard、撤回留稿(ExtensionAuthoringService.cs:200/232)。`inFlight` 防同插件并发。无缺环。
- **占用冲突与接手/分叉/移除资源清理**:闭环完整。占用→专用对话框(ShowOccupiedAsync);接手/分叉/移除均经 `OnSessionArchived`(MainWindowViewModel.cs:2081)结束终端+关标签+释放 runtime;移除先离窗再进回收站防句柄锁目录。分叉失败收回 worktree/会话行(ForkFlow.cs:392/68)。无缺环。
- **自动化邮箱投递与撤回**:闭环完整。忙等待、接手/归档/移除/关闭撤回(SessionMailbox.cs:72)、被拒回合报 Withdrawn 不落输入框(SessionWorkspace.cs:1499)。无缺环。
- **运行状态持久化与中断恢复**:闭环完整。`SqliteAutomationRunStore` 落 forge.db;QuotaResume note 仅关闭时保留供重启续跑、用户取消删除(QuotaResume.cs:195);Attach 时 `Engine.StartupAsync` 恢复。无缺环。
- **TODO 只读契约与拖拽同层**:契约成立(无改状态命令、仅 Pending 拖拽、GroupId 同层、存储夹取)。**低危缺环**:视图同层守卫未排除完成区落点且无 headless 专测(F-APP-E-10)。
- **计划面板交接不覆盖用户输入**:闭环完整。草稿/附件→BlockedByDraft,不可用清草稿(MainWindowViewModel.Plan.cs:109),定向测试齐全。无缺环。

> 总评:本路六个主题实现与规格高度一致,闭环重点均有真实调用链与定向测试支撑,未发现 FAIL。非 PASS 集中于「规格自述未落地」(F-APP-E-42)、少量视图/接线缺测(PASS*)与一项未追踪(F-APP-E-57)。规格侧主要是跨规格重复与术语交叠,建议单点归属+互链。