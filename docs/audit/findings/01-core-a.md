# Core A(Agent 循环/子 Agent/工具面)审计发现

审计范围:`src/Forge.Core/SPEC.md` 的「循环」(L26)、「SubAgent」(L28)、「子 Agent 控制」(L30–35)、「todo 工具」(L39)、「扩展编写工具」(L41)、「评审工具」(L43–49)、「工具热加载」(L51)、「扩展工具仅限根 Agent」(L53)、「审批路径比较按 OS」(L55)、「ask_user」(L111)、「带附件的插话」(L109)及相关「未决」(L141–145、154),外加「边界」审批条(L17)。
方法:只读;先读 `.github/skills/test-anti-patterns/SKILL.md`,逐条追踪真实调用链,测试证据核对到方法体断言。未运行 git/build/test;唯一写入=本文件。测试路径省略前缀 `tests/Forge.Core.Tests/`。App 侧接线不审(仅标注依赖)。

分级:PASS=符合且有测试;PASS*=实现存在但无专项测试;PARTIAL=部分符合;FAIL/MISSING 等本次未用(规格缺口单列)。

**总计 69 个功能点:PASS 62,PASS* 6,PARTIAL 1,FAIL 0,MISSING 0。**

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-CORE-A-01 | 循环骨架:流式一轮→审批→执行→回写 | SPEC L26 | src/Forge.Core/Agents/AgentLoop.cs:121–262(RunAsync) | Agents/AgentLoopTests.cs::SimpleTurn_streams_text_and_persists_the_exchange;ToolCalls_run_and_are_written_back_as_one_tool_message | PASS |
| F-CORE-A-02 | 连续只读并发、副作用依序;按单次调用真实副作用分批 | L26 | AgentLoop.cs:819–887(ExecuteToolsAsync)、889–905(RunBatchAsync)、IsReadOnly(经 ToolRegistry.SideEffectOf) | AgentLoopTests.cs::ReadOnlyCalls_overlap_while_write_calls_stay_sequential(PeakReaders=2/PeakWriters=1+时间线断言) | PASS |
| F-CORE-A-03 | 结果按调用顺序写成一条 Tool 消息 | L26 | AgentLoop.cs:246–248、1055–1065(Complete) | AgentLoopTests.cs::ToolCalls_run_and_are_written_back_as_one_tool_message;ReadOnlyCalls…(顺序断言 w1,a,b,w2) | PASS |
| F-CORE-A-04 | 取消时每个待决调用补"已取消"结果,历史合法 | L26 | AgentLoop.cs:878–886(catch+Complete,fallback "Cancelled.",CancellationToken.None 落盘) | AgentLoopTests.cs::Cancellation_fills_every_pending_call_with_a_cancelled_result(3 条全断言 "Cancelled."+IsError+落盘) | PASS |
| F-CORE-A-05 | 超长工具输出转存 Artifact,上下文留预览+id | L26;未决 L145 | AgentLoop.cs:917–947(SpillAsync,仅文本、跳过错误结果与 read_artifact);Agents/AgentOptions.cs:31–34(默认 32000/2000) | AgentLoopTests.cs::LargeToolOutput_moves_into_the_artifact_store(预览/字节数/id/原文往返断言) | PASS |
| F-CORE-A-06 | 审批:策略求值+按需调处理器;无处理器按拒绝 | L17 | AgentLoop.cs:968–988(IsApprovedAsync) | AgentLoopTests.cs::AskWithoutHandler_is_denied;DeniedCalls_become_error_results_and_never_invoke_the_tool | PASS |
| F-CORE-A-07 | 事件通道无界(决定,不加背压) | 未决 L141 | Agents/AgentHost.cs:393–397(Channel.CreateUnbounded) | 无专项(结构性决定) | PASS* |
| F-CORE-A-08 | MaxTurns 计入后续指令续跑 | 未决 L142 | AgentLoop.cs:157(for turn<MaxTurns)、285–317(follow-up continue 消耗回合) | AgentLoopTests.cs::MaxTurns_is_enforced(仅一般上限;无 follow-up 计数专项) | PASS* |
| F-CORE-A-09 | spawn_agent 按 `<kind>-<n>` 登记实例 | L28 | Agents/SubAgentScheduler.cs:332–341(NextId)、376–386(Identifier 清洗) | Agents/SubAgentTests.cs::Spawn_returns_the_child_text_with_its_id;SubAgentRunRegistryPinTests.cs::Hundred_sequential_spawns… | PASS |
| F-CORE-A-10 | 同类型共享前缀;按 id 续跑(同类型且未在运行) | L28 | SubAgentScheduler.cs:150–185(kind 校验+TryAcquire 运行门);Agents/AgentInstance.cs:158–159 | SubAgentTests.cs::ResumeId_continues_the_same_instance_and_kind_mismatch_is_rejected;Resuming_the_agent_that_is_spawning_is_rejected | PASS |
| F-CORE-A-11 | 活跃计数覆盖整棵树;父等子结果交还名额 | L28 | AgentLoop.cs:267–283(AcquireAsync,根也占名额);Agents/AgentSlots.cs:358–377(Detach);Agents/SpawnAgentTool.cs:87 | SubAgentTests.cs::TreeWideLimit_counts_every_running_agent_and_the_spawn_tree_still_completes;EightSiblingSpawns_stay_within_the_parallel_limit;AgentSlotPoolTests.cs::A_detached_slot_hands_its_permit_to_a_queued_waiter_and_takes_it_back | PASS |
| F-CORE-A-12 | 深度上限 3 | L28 | AgentOptions.cs:23(MaxSubAgentDepth=3);SubAgentScheduler.cs:128–134;ToolRegistry.cs:51–95(canSpawn) | SubAgentTests.cs::DepthLimit_hides_the_spawn_tool_from_the_deepest_agent;SubAgentControlSurfacePinTests.cs::Parent_sees_control_tools_beside_spawn_and_deepest_agent_sees_none | PASS |
| F-CORE-A-13 | 树内并发默认 4、可调;扩容唤醒排队、缩容不打断 | L28 | AgentOptions.cs:19(=4);AgentLoop.cs:100–112;AgentSlots.cs:66–99(SetLimit);AgentHost.cs:276 | Agents/AgentLoopSubAgentCapConcurrencyTests.cs(5 法,含并发设值);AgentSlotPoolTests.cs::Raising_the_limit_wakes_queued_waiters_immediately;Lowering_the_limit_keeps_handed_out_permits_and_blocks_new_grants;SubAgentTests.cs::SpawnCalls_run_in_parallel_up_to_the_configured_bound | PASS |
| F-CORE-A-14 | spawn 对未超深度 Agent 隐式可用、定义关不掉;禁用走同一集合 | L28 | ToolRegistry.cs:75–92(显式白名单也补 subAgentTools);ToolSurface.Publish 过滤禁用名 | SubAgentControlSurfacePinTests.cs::Parent_sees_control_tools_beside_spawn…;Agents/ToolSwitchTests.cs::A_disabled_external_and_built_in_tool_are_not_offered_to_the_model(显式禁 spawn_agent) | PASS |
| F-CORE-A-15 | 每 run 独立 CTS 链父令牌;工具与 UI 共用 ISubAgentControl | L30 | Agents/SubAgentRun.cs:34(CreateLinkedTokenSource);SubAgentScheduler 实现 ISubAgentControl;AgentHost.cs:214 | Agents/SubAgentControlSurfacePinTests.cs(全 6 法);SubAgentSteerFollowUpStopPinTests.cs::Stop_cancels_only_that_child… | PASS |
| F-CORE-A-16 | 转向在批次边界生效:已发出跑完,未开始补 skipped 错误,指令注入下一条 user | L31 | AgentLoop.cs:826–839(SkippedResult)、246–250(steering 落盘);SubAgentRun.cs:168–186 | SubAgentSteerFollowUpStopPinTests.cs::Steer_at_batch_boundary_skips_remaining_calls_with_error_results;Steer_during_read_only_parallel_batch_lets_in_flight_calls_finish | PASS |
| F-CORE-A-17 | 边界前多条转向合并一条消息一个事件;批后无剩余按丢弃 0 交付 | L31 | SubAgentRun.cs:168–186("\n\n" 合并);AgentLoop.cs:843–847(trailing)、296–302 | SubAgentSteerFollowUpStopPinTests.cs::Two_steers_before_one_boundary_produce_one_message_one_event_in_order;Steer_after_the_last_call_is_delivered_with_zero_dropped | PASS |
| F-CORE-A-18 | 后续指令在本应结束时注入续跑;排队期转向/后续合入初始任务 | L32 | AgentLoop.cs:285–317(ContinueWithControlAsync)、343–365(WithInstructions);SubAgentRun.cs:60–77(Admit)、224–233 | SubAgentSteerFollowUpStopPinTests.cs::FollowUp_continues_instead_of_finishing_and_reports_pending;Multiple_followUps_are_delivered_in_order_with_pending_counts;SubAgentControlSurfacePinTests.cs::Queued_steer_and_followUp_are_appended_to_the_task | PASS |
| F-CORE-A-19 | 停止只取消该子:待决工具补 "Cancelled."、待决审批释放;父收非错误 stopped 文本 | L33 | SubAgentRun.cs:138–164(Stop);SubAgentScheduler.cs:203–209、276–283(StoppedText);AgentLoop.cs:878–886、968–988(ct 传入审批等待) | SubAgentSteerFollowUpStopPinTests.cs::Stop_cancels_only_that_child_and_returns_partial_output_without_error;Stop_while_a_tool_awaits_approval_releases_the_wait(toolInvoked=0);Stop_of_a_queued_child_finishes_stopped_without_running_or_invoking_the_provider | PASS |
| F-CORE-A-20 | 生命周期 Started→仅真等名额才 Queued→Running→每 run 恰一次 Finished(finally,含异常/父取消=Stopped "parent cancelled");用量增量、耗时含排队 | L34 | SubAgentScheduler.cs:191–238(finally+FinishState)、240–257;SubAgentRun.cs:211–222(Finish 幂等);AgentLoop.cs:267–283 | Agents/SubAgentLifecyclePinTests.cs(全 6 法:恰一次/Queued 条件/增量/含排队);SubAgentSteerFollowUpStopPinTests.cs::Parent_cancellation_finishes_each_child_stopped_with_parent_cancelled;SubAgentSessionPinTests.cs::Stress_random_steer_stop_followUp_interleavings_finish_every_child_exactly_once | PASS |
| F-CORE-A-21 | runs 只存运行中,结束移除并释放 CTS/父注册;finished 集合续答 AlreadyFinished;instances 保留 | L35 | SubAgentScheduler.cs:288–310(Attach/Detach)、109–118(Missing);SubAgentRun.cs:224 Dispose | Agents/SubAgentRunRegistryPinTests.cs::Hundred_sequential_spawns_leave_every_finished_id_AlreadyFinished_and_unknown_NotFound;Resume_of_a_finished_id_is_controllable_while_running_then_AlreadyFinished;SubAgentControlSurfacePinTests.cs::Finished_children_report_AlreadyFinished | PASS |
| F-CORE-A-22 | todo 按参数定副作用:list/plan_summary_get 只读,其余写 | L39 | Agents/TodoTool.cs:50(Resolve)、123–146(IsReadOnly) | Agents/TodoToolPinTests.cs::List_is_ReadOnly_while_mutations_are_Write;Approval_policy_sees_the_per_call_effect;TodoToolStepCommitEdgeTests.cs::Side_effect_is_resolved_per_call_from_the_operation | PASS |
| F-CORE-A-23 | update/remove/promote 先确认条目属于本会话 | L39 | TodoTool.cs:255–281(promote)、283–347(update)、394–408(remove),均经 FindAsync(sessionId) | TodoToolPinTests.cs::Update_remove_reject_other_sessions_items;TodoToolStepCommitEdgeTests.cs::An_item_of_another_session_is_rejected_without_touching_it_or_calling_the_committer | PASS |
| F-CORE-A-24 | add 带 group 按标题找/建;不带进待排散项队列 | L39 | TodoTool.cs:227–253(AddAsync→FindOrCreateGroupAsync);Storage/SqliteTodoStore.cs(FindOrCreateGroupAsync) | Agents/TodoToolGroupTests.cs::Add_with_a_group_creates_the_group_and_reuses_it_for_the_same_title;Add_without_a_group_lands_in_the_loose_queue;Storage/SqliteTodoStoreGroupTests.cs::ListGroups_returns_groups_in_order_and_FindOrCreate_reuses_trimmed_titles_case_sensitively | PASS |
| F-CORE-A-25 | promote:散项升为以它命名的新分组,继承 id/标题、移出队列 | L39 | TodoTool.cs:255–281;SqliteTodoStore.cs:475–519(组继承 id/标题、删行、队列致密化) | TodoToolGroupTests.cs::Promote_turns_a_loose_item_into_an_empty_group…;Promote_of_a_group_step_is_rejected;SqliteTodoStoreGroupTests.cs::Promote_inherits_the_item_id_and_title_removes_the_loose_row_and_compacts_the_queue | PASS |
| F-CORE-A-26 | after_id 只在同一层内插入 | L39 | SqliteTodoStore.cs:104–120(跨层/异会话抛 ArgumentException) | SqliteTodoStoreGroupTests.cs::Add_with_a_cross_layer_after_id_is_rejected_but_same_layer_inserts_behind;SqliteTodoStoreTests.cs::Add_with_unknown_or_foreign_after_id_throws_ArgumentException | PASS |
| F-CORE-A-27 | 同会话仅一步 in_progress,存储层把其余退回 pending | L39 | SqliteTodoStore.cs:221–235(写事务内降级) | TodoToolGroupTests.cs::Setting_in_progress_returns_the_previous_step_to_pending_in_the_store;SqliteTodoStoreGroupTests.cs::Only_one_step_is_in_progress_across_groups_and_the_queue_with_a_single_notification | PASS |
| F-CORE-A-28 | paused 永不触发步骤提交 | L39 | TodoTool.cs:338–344(仅 parsed==Done 触发) | TodoToolGroupTests.cs::Update_to_paused_succeeds_and_never_runs_the_step_committer;TodoToolStepCommitEdgeTests.cs::Updates_that_do_not_say_done_never_commit_even_on_an_item_with_a_failed_commit | PASS |
| F-CORE-A-29 | done 经 IStepCommitter 提交;短 sha/失败原因入结果;失败不回滚 done | L39 | TodoTool.cs:338–393(AppendStepCommitAsync+TryRecordFailureAsync);AgentHost.cs:135;Abstractions/Workbench.cs:216–227(契约) | Agents/TodoToolStepCommitTests.cs(8 法);TodoToolStepCommitEdgeTests.cs(约 40 法,含重试/取消/抛异常记录);AgentHostStepCommitterTests.cs(10 法,含宿主级取消历史配对) | PASS |
| F-CORE-A-30 | list 按分组输出(标题+done/total、步骤 id/状态/note),散项列末尾 | L39 | TodoTool.cs:148–225(ListAsync) | TodoToolGroupTests.cs::List_groups_steps_by_group_with_done_counts_and_the_loose_queue_last;List_puts_items_whose_group_is_missing_into_the_loose_section;TodoToolStepCommitEdgeTests.cs::List_renders_status_note_commit_state_and_step_fields_in_the_documented_layout | PASS |
| F-CORE-A-31 | plan_summary 读写会话级计划摘要(plan_summaries) | L39 | TodoTool.cs:410–430;SqliteTodoStore(Get/SetPlanSummaryAsync) | TodoToolStepCommitTests.cs::Plan_summary_operations_round_trip_and_resolve_side_effects;TodoToolStepCommitEdgeTests.cs::Plan_summary_is_stored_verbatim_per_session…;Storage/SqlitePlanRevisionTests.cs | PASS |
| F-CORE-A-32 | todo 不进内置白名单,目前只有根 Agent 可用 | 未决 L143 | Prompts/BuiltInAgents.cs:20、35、46、60(四个白名单均无 todo) | 无专项(未找到断言子 Agent 看不到 todo 的测试) | PASS* |
| F-CORE-A-33 | Core 只声明窄接口 IExtensionAuthoring;无提交/批准能力 | L41 | Tools/IExtensionAuthoring.cs:75–88(List/Submit/Disable/Delete) | Tools/ExtensionToolsSurfaceTests.cs;Tools/FakeExtensionAuthoring.cs 支撑的各套件(提交只能经接口) | PASS |
| F-CORE-A-34 | 五个工具;extension_list 只读 | L41 | IExtensionAuthoring.cs:110–121(Create);Tools/ExtensionListTool.cs:33(ReadOnly) | ExtensionToolsSurfaceTests.cs::Create_returns_exactly_five_tools_with_the_documented_names;SideEffects_are_readonly_for_list_and_write_for_the_rest | PASS |
| F-CORE-A-35 | create/update 同一个类、schema 字节一致 | L41 | Tools/ExtensionSubmitTool.cs:14–70(单一 SchemaJson 常量,两实例)、107–114 | ExtensionToolsSurfaceTests.cs::Schemas_are_valid_json_and_byte_stable_across_reads;Tools/ExtensionDraftContentTests.cs::IsUpdate_matches_the_tool | PASS |
| F-CORE-A-36 | 参数全走 JsonDocument 本地校验,不合规不调 authoring | L41 | Tools/ExtensionDraftReader.cs:24–96(Read);ExtensionSubmitTool.cs:122–127;Tools/ExtensionLifecycleTools.cs(两工具同路径) | Tools/ExtensionSubmitValidationTests.cs::Invalid_arguments_are_errors_and_authoring_is_never_called;ExtensionLifecycleToolTests.cs::Disable/Delete_rejects_bad_arguments_without_calling_authoring;ExtensionDraftContentTests.cs | PASS |
| F-CORE-A-37 | 提交阻塞到用户决定;结果=注册工具名/拒绝原因/编译诊断 | L41 | ExtensionSubmitTool.cs:129–199(await SubmitAsync+Report) | Tools/ExtensionSubmitOutcomeTests.cs::Approved_names_the_tools_and_points_at_the_next_model_request;Rejected_is_not_an_error…;Invalid_is_an_error_and_passes_the_diagnostics_through | PASS |
| F-CORE-A-38 | 四值 outcome;Withdrawn=卡消失、草稿保留、非拒绝非错误 | L41 | IExtensionAuthoring.cs:38–56(enum);ExtensionSubmitTool.cs:165–173(Withdrawn 分支) | ExtensionSubmitOutcomeTests.cs::Withdrawn_is_not_an_error_and_reports_the_draft_kept_pending;Withdrawn_without_a_message_still_reports_the_base_text;Unknown_outcome_is_an_error | PASS |
| F-CORE-A-39 | OperationCanceledException 透传(五工具) | L41 | ExtensionSubmitTool.cs:129–133、ExtensionListTool.cs:47–51、ExtensionLifecycleTools.cs(catch 过滤均排除 OCE) | Tools/ExtensionToolFailureTests.cs::List/Submit/Disable/Delete_cancellation_propagates_instead_of_becoming_a_result(4 法) | PASS |
| F-CORE-A-40 | 不进内置 Agent 白名单,内置可缓存工具集不变 | L41 | BuiltInAgents.cs:20、35、46、60(无 extension_*);工具由 App 作外部注入 | 无直接断言内置白名单内容的测试(剔除机制由 F-CORE-A-61 套件覆盖) | PASS* |
| F-CORE-A-41 | 无 IReviewChangeSetSource 即无工具;契约类型齐备;预期失败=文本,仅取消抛 | L43 | AgentHost.cs:149–152;Tools/ReviewPackage.cs(全契约记录);Tools/RequestReviewTool.cs:141–147 | Tools/RequestReviewToolTests.cs::The_host_registers_request_review_only_when_a_change_set_source_is_given;A_change_set_source_failure_is_an_error_result_carrying_the_reason;Cancelling_the_change_set_read_propagates_instead_of_becoming_a_result | PASS |
| F-CORE-A-42 | step_id→该步骤自己的提交;意图取步骤标题/摘要/验证 | L44 | RequestReviewTool.cs:113–139(request.Commit=step.CommitId、Base=null;ReviewBrief 带步骤字段) | RequestReviewToolTests.cs::A_step_review_asks_for_the_steps_own_commit_and_passes_its_fields_to_the_reviewer;但无提交步骤回退分支评审见详情 | PARTIAL |
| F-CORE-A-43 | 无 step_id→base(默认仓库默认分支)+未提交/未跟踪;intent 必填 | L44 | RequestReviewTool.cs:92–97、128–134 | RequestReviewToolTests.cs::Neither_a_step_id_nor_an_intent_is_an_error;A_branch_review_passes_the_base_and_the_paths_to_the_change_set_source | PASS |
| F-CORE-A-44 | step_id+base 同给=错误;已知参数非字符串类型=错误不回落 | L44 | RequestReviewTool.cs:99–104、269–299(TryReadArguments) | RequestReviewToolTests.cs::A_step_id_combined_with_a_base_is_an_error;A_numeric_step_id_is_an_error_instead_of_a_branch_review;Paths_that_are_not_an_array_of_strings_are_an_error;An_unknown_property_is_an_error_naming_it | PASS |
| F-CORE-A-45 | 包一次渲染:意图/步骤/分支/计数/清单/diff/焦点+收尾指针一句;报告规则只在 Reviewer 提示词 | L45 | Tools/ReviewPackageText.cs:33–141(Render)、30(ClosingLine);BuiltInAgents.cs:51–58(Reviewer 提示词含 verdict 规则) | Tools/ReviewPackageTextTests.cs(28 法:A_step_brief_renders…;A_brief_without_a_step_says…;A_non_blank_focus…;The_untracked_note_is_in_every_package) | PASS |
| F-CORE-A-46 | 限额 diff 48000/提交 50 行/文件 200 行独立/字段 2000;ReviewLimits 每次评审现读 | L45 | Tools/ReviewPackageLimits.cs:11–14(默认);RequestReviewTool.cs:148(limits?.Invoke());ReviewPackageText.cs:37(Sanitized) | ReviewPackageTextTests.cs::Limits_default_to_the_values_the_package_always_used;The_commit_and_file_limits_apply_independently_and_report_what_they_left_out;RequestReviewToolTests.cs::The_limits_the_host_hands_in_are_read_for_each_review_and_cap_the_package | PASS |
| F-CORE-A-47 | Reviewer 原文 8000 上限;DiffCut 标志→结果头部提示;截断写明 git_diff 读其余 | L45–46 | Tools/RequestReviewVerdict.cs:21、84–107(Clamp);RequestReviewTool.cs:192–195;ReviewPackageText.cs:113–130 | RequestReviewToolTests.cs::An_overlong_review_is_cut_in_the_result_but_its_verdict_is_read_from_the_whole_text;A_cut_diff_is_flagged_in_the_result_and_the_reviewer_is_given_the_base_that_reproduces_it;RequestReviewVerdictTests.cs::A_review_over_the_cap_is_cut_on_a_line_boundary_and_says_both_lengths | PASS |
| F-CORE-A-48 | 包固定写明未跟踪文件规则;narrowed to 自述;CommitsTruncated→at least N | L45 | ReviewPackageText.cs:27–28、92–99(narrowed)、86–88(at least)、169–176 | ReviewPackageTextTests.cs::The_untracked_note_is_in_every_package;A_narrowed_change_set_says_which_paths_were_read;An_unnarrowed_change_set_says_nothing_about_paths;A_commit_list_the_source_cut_is_reported_as_a_floor_not_as_a_total | PASS |
| F-CORE-A-49 | 结论只认最后一条 verdict 行;大小写/强调/列表/标点容忍;缺失=Unstructured 绝不推断通过 | L46 | RequestReviewVerdict.cs:26–60(Parse 自尾向前)、139–193(TrimMarkup/Matches) | Tools/RequestReviewVerdictTests.cs(9 法:The_last_verdict_line_of_a_text_is_the_one_that_counts;Text_without_a_known_verdict_line_is_unstructured;An_unstructured_verdict_is_never_described_as_an_approval;A_verdict_line_far_above_the_end_is_still_found) | PASS |
| F-CORE-A-50 | 结果固定形状:结论行/reviewed 行/截断提示/下一步/---/原文≤8000/[agent_id] | L46 | RequestReviewTool.cs:181–210 | RequestReviewToolTests.cs::The_result_opens_with_the_verdict_and_carries_the_review_separately;A_request_changes_review_is_reported_as_such_with_two_files | PASS |
| F-CORE-A-51 | 空变更集(无提交/文件/diff)不启动 Reviewer,非错误结果 | L46 | RequestReviewTool.cs:143–145、243–256(DescribeEmpty) | RequestReviewToolTests.cs::An_empty_change_set_says_there_is_nothing_to_review_and_starts_no_reviewer(断言 Instances 空+请求数);An_empty_step_review_of_a_commit_that_changed_nothing_says_so_and_starts_no_reviewer | PASS |
| F-CORE-A-52 | 被停止的评审:错误结果、不解析结论、附部分原文+agent_id;父取消一起取消并上抛 | L47 | RequestReviewTool.cs:162–174(Stopped 先于 Parse)、149–155(ct 链) | RequestReviewToolTests.cs::A_reviewer_stopped_mid_review_is_an_error_and_never_its_partial_verdict(部分文本含过早 verdict 仍断言错误);Cancelling_the_parent_during_a_review_cancels_the_reviewer_and_returns_no_verdict | PASS |
| F-CORE-A-53 | git_diff 退路带 base(JSON 转义);SingleCommit 补句;根提交指向 read;路径列表 2000 截断;无换行 diff 不劈代理对 | L48 | ReviewPackageText.cs:230–272(AppendGitDiffHint)、322–330(DiffCutLength)、280–312(AppendClamped) | ReviewPackageTextTests.cs::A_cut_branch_diff_names_the_git_diff_base…;A_cut_single_commit_diff_warns…;A_cut_root_commit_diff_points_at_the_files…;A_huge_path_list_is_held_to_the_field_cap;A_diff_without_line_breaks_is_never_cut_inside_a_surrogate_pair | PASS |
| F-CORE-A-54 | ReadOnly+等子 Detach 交还名额;Reviewer 白名单只读含 git_diff 无写工具;结论不落库 | L49;未决 L144 | RequestReviewTool.cs:50(ReadOnly)、149–155(Detach);BuiltInAgents.cs:60;工具内无任何结论持久化调用 | RequestReviewToolTests.cs::Definition_is_the_read_only_request_review_tool;A_review_runs_with_one_agent_slot_because_the_parent_hands_its_permit_back(单名额死锁反证);The_reviewer_sees_git_diff_and_no_write_tool_and_cannot_request_another_review;Two_reviews_in_one_turn_run_side_by_side_and_each_returns_its_own_verdict | PASS |
| F-CORE-A-55 | 自动评审未接线(未决:需设置项+步骤提交后触发,单独决定) | 未决 L144 | TodoTool.cs 全文无 review 引用;Core 内 request_review 仅 AgentHost.cs:151 注册处 | 无(缺失态经代码检索核实) | PASS* |
| F-CORE-A-56 | 发布不可变快照;Volatile 读、发布加锁、版本单调;内置优先、外部同名静默丢弃 | L51 | Agents/ToolRegistry.cs:126–243(ToolSurface;179–197 ReplaceExternal、221–243 Publish) | Agents/HotReloadTests.cs::Concurrent_replacements_keep_versions_monotonic_and_snapshots_paired;External_tool_with_a_builtin_name_never_shadows_the_builtin;Replacements_during_a_live_run_leave_every_request_paired | PASS |
| F-CORE-A-57 | 每次模型请求前/查工具前取快照;版本未变复用同一 Definitions 实例;变了按白名单/深度重建 | L51 | AgentLoop.cs:1077–1120(ToolSelection)、169(请求前)、909(派发查工具) | HotReloadTests.cs::Steady_requests_share_one_tool_array_and_a_replacement_resorts_it(Assert.Same/NotSame);Tool_published_mid_turn_is_visible_and_callable_in_the_next_request;Child_rebuilds_from_its_own_allow_list_while_root_sees_the_new_tool | PASS |
| F-CORE-A-58 | 禁用集合过滤内置+外部;SetEquals 不发布;替换后仍生效、后注册同名被挡 | L51 | ToolRegistry.cs:204–219(SetDisabled)、221–243(Publish);AgentHost.cs:267 | ToolSwitchTests.cs::Re_applying_the_same_set_publishes_nothing_and_a_change_publishes_once;The_disabled_set_survives_an_external_replacement_and_covers_tools_registered_later;Disabled_tools_from_the_options_apply_before_the_first_request;Concurrent_switches_and_replacements_leave_a_consistent_set | PASS |
| F-CORE-A-59 | 被禁工具不出现在请求;回合中途关掉后再调用=错误结果 | L51 | ToolRegistry.cs:221–243;AgentLoop.cs:909–912(Unknown tool 错误) | ToolSwitchTests.cs::A_disabled_external_and_built_in_tool_are_not_offered_to_the_model;A_call_to_a_tool_disabled_mid_turn_is_refused_in_the_next_request;HotReloadTests.cs::Calling_a_removed_tool_is_an_error_and_history_stays_paired | PASS |
| F-CORE-A-60 | AllTools 列全部内置+外部(含禁用);AgentLoopServices.Tools 类型为 ToolSurface | L51 | AgentHost.cs:285;AgentLoop.cs:17 | ToolSwitchTests.cs::All_tools_lists_disabled_built_ins_and_external_tools;HotReloadTests.cs::Host_tools_returns_the_current_snapshot | PASS |
| F-CORE-A-61 | extension_* 仅 Depth==0 可见可调:Select 对子 Agent 一律剔除(null/显式);派发同一选择;工具自身 depth>0 拒绝 | L53 | ToolRegistry.cs:51–95(IsExtensionTool);AgentLoop.cs:909;IExtensionAuthoring.cs:101–105(RefuseSubAgent,五工具均调) | Agents/ExtensionToolRootOnlyTests.cs(全 8 法:Root_with_null_tool_list_sees_all_five;SubAgent_with_explicit_list_naming_them_still_loses_them;SubAgent_without_tool_list_neither_sees_nor_can_call_extension_disable;Tools_refuse_at_depth_above_zero_without_calling_authoring) | PASS |
| F-CORE-A-62 | 审批路径比较按 OS:Windows OrdinalIgnoreCase/其余 Ordinal;不探测 FS;fail-closed 到 Ask;测试按 OS 断言 | L55 | Agents/ApprovalPolicy.cs:44–58(IsUnder,Path.GetFullPath 纯字符串规范化) | Agents/ApprovalPolicyTests.cs::Directory_comparison_follows_the_platform_case_sensitivity(按 OperatingSystem.IsWindows() 断言);Trusted_directories_allow_side_effects_inside_them_only | PASS |
| F-CORE-A-63 | ask_user 仅有 QuestionHandler 时注册;挂起等答案;取消→取消结果;只读默认放行 | L111 | AgentHost.cs:140;Agents/AskUserTool.cs:47(ReadOnly)、55–84(InvokeAsync) | Agents/AskUserToolTests.cs::The_tool_is_registered_only_when_a_handler_is_set;Cancelling_a_pending_question_returns_cancelled_instead_of_hanging;An_already_cancelled_token_does_not_ask;Cancelled_outcome_reports_cancelled;The_default_policy_does_not_ask_for_ask_user | PASS |
| F-CORE-A-64 | minSelect/maxSelect:整数 1≤min≤max≤选项数(+自由输入 1);单选带参报错提示 multiSelect | L111 | AskUserTool.cs:205–245(校验)、247–266(OptionalInt) | Agents/AskUserSelectionBoundsTests.cs(全 9 法:Free_text_counts_as_one_extra_pick_for_the_upper_bound;Invalid_bounds_are_an_error_and_never_reach_the_handler;Bounds_on_a_single_select_question_name_multiSelect_in_the_error) | PASS |
| F-CORE-A-65 | 单题跳过输出 `<header>: (skipped)`;全跳过→Declined(UI 不提交) | L111 | AskUserTool.cs:268–325(Format:Skipped/Declined 分支) | AskUserSelectionBoundsTests.cs::A_skipped_answer_is_reported_as_skipped_next_to_real_answers;All_skipped_answers_still_format;AskUserToolTests.cs::Declined_tells_the_model_to_proceed_on_its_own_judgment(UI 侧行为见 App SPEC L402,不审) | PASS |
| F-CORE-A-66 | 并行 ask_user 由 App 串行展示,Core 不感知 | 未决 L154 | AskUserTool.cs:66–70(直接 await handler,无会话级串行);只读→可并发批 | 无专项 | PASS* |
| F-CORE-A-67 | Interject(blocks) 与 Interject(string) 并存;TakeInterjections→Interjection(Content/Text/Attachments) | L109 | AgentInstance.cs:103–133;Agents/Interjection.cs:14–60 | Agents/RootInterjectionTests.cs::Interject_rejects_blank_text;Interject_with_blocks_rejects_empty_content_and_other_block_kinds;Undelivered_interjections_come_back_with_their_blocks;An_inlined_text_file_is_an_attachment_not_typed_text | PASS |
| F-CORE-A-68 | 送达合并为一条 user 消息:键入文本 "\n\n" 连接成首块,随后全部附件按序 | L109 | Interjection.cs:99–131(Merge);AgentLoop.cs:321–341(批后送达) | RootInterjectionTests.cs::Several_interjections_become_one_user_message_in_order;An_interjection_lands_after_the_running_tool_batch_without_dropping_calls;An_interjection_with_an_image_reaches_the_next_request_after_the_tool_results | PASS |
| F-CORE-A-69 | 每条插话一个 AgentInterjected(Text=键入文本、Attachments=同批块实例) | L109 | AgentLoop.cs:330–334;Interjection.cs:36–60(AttachmentsOf 保留原实例) | RootInterjectionTests.cs:144–150(Assert.Same(image/pdf, notices[i].Attachments));61–89(逐条 Text 断言、纯附件 Text 为空) | PASS |

## 非 PASS 项详情

### F-CORE-A-42(PARTIAL)步骤评审在"步骤无提交"时回退为分支评审,超出规格断言
- 证据:SPEC L44 断言「带 `step_id` 时变更集**就是**该步骤自己的那次提交」。实现 `src/Forge.Core/Tools/RequestReviewTool.cs:128–134`:请求 `Base = step is null ? arguments.Base : null`、`Commit = step?.CommitId`;当步骤 `CommitId` 为 null(步骤提交关闭/无可提交内容)时,两者皆 null,变更集源按「默认分支+未提交树」构建。`WithCommitNote`(同文件 239–244)在意图里追加说明,包内仍渲染步骤记录。
- 测试:RequestReviewToolTests.cs::A_step_without_its_own_commit_reviews_the_branch_and_says_so_in_the_package 明确断言该回退(请求 Commit/Base 均 null、包含 "no commit of its own")——行为是有意且被测的。
- 缺口:规格没有这条例外。这正是规格在参数校验处严防的失败形态(「静默忽略其中一个会让调用方以为审了别的东西」)的温和版:回退虽自述,但工具结果头部仍是 `reviewed: <HEAD sha>`,调用方可能误以为审了步骤提交。`commit^..commit`/根提交对空树的实际比较在 App 的 `ReviewChangeSetSource`(不审)。
- 建议处理类型:**规格修订**(在 L44 补「步骤无提交时回退为分支+未提交树评审并在意图中自述」例外);或改为显式错误结果。二选一,由规格所有者决定。

### F-CORE-A-07(PASS*)事件通道无界
- 证据:AgentHost.cs:393–397 `Channel.CreateUnbounded<AgentEvent>`(SingleReader/多写者),与未决 L141 的决定一致。无测试断言"不加背压/不丢事件"(例如慢消费者下事件不丢)。建议处理类型:补测试(低优先,结构性决定)。

### F-CORE-A-08(PASS*)MaxTurns 计入 follow-up 续跑
- 证据:AgentLoop.cs:157 的 `for (turn < MaxTurns)` 包住 follow-up 的 `continue`(285–317),续跑必然消耗回合;未决 L142 声明一致。现有 MaxTurns_is_enforced 只测工具循环上限,没有「follow-up 续跑耗尽 MaxTurns 抛 AgentTurnLimitException」的专项。建议处理类型:补测试(边界:最后一次回合注入 follow-up)。

### F-CORE-A-32(PASS*)todo 仅根 Agent 可用
- 证据:BuiltInAgents.cs 四个内置白名单(L20/35/46/60)均无 `todo`;todo 是宿主内置工具(AgentHost.cs:135),子 Agent 的显式白名单不含它即被 Select 剔除。未找到断言「子 Agent 看不到/调不到 todo」的测试。另注意规格措辞过宽(见规格质量 #4):用户自定义 kind 若 `ToolNames=null`,子 Agent 仍可见 todo。建议处理类型:补测试+规格措辞收窄。

### F-CORE-A-40(PASS*)扩展工具不进内置白名单
- 证据:BuiltInAgents.cs 白名单无 extension_*;五工具由 `ExtensionTools.Create` 供 App 作外部注册(IExtensionAuthoring.cs:110–121)。剔除机制有强测试(ExtensionToolRootOnlyTests),但「内置白名单内容不含五名、可缓存工具集不变」无直接断言。建议处理类型:补一条白名单内容 pin 测试(低优先)。

### F-CORE-A-55(PASS*)自动评审未接线(未决项核实)
- 证据:Core 内 `request_review` 仅出现在 AgentHost.cs:151 注册与工具自身文件;TodoTool 的步提交路径(349–393)无任何评审触发;结论无持久化调用(工具内唯一的存储读取是 todos.ListAsync)。与未决 L144「未接线、结论不落库、pr_create 不看它」一致。`GitReviewLedger` 在 App(src/Forge.App/Composition/AppComposition.cs 引用),Reviewer 子会话独立(SubAgentScheduler.CreateSessionAsync)保证评审的 git_diff 记在子会话——App 侧不审。建议处理类型:维持未决;接线时另立任务。

### F-CORE-A-66(PASS*)并行 ask_user Core 不感知
- 证据:AskUserTool.cs:66–70 直接 await handler,无会话级互斥;只读副作用使多个 ask_user 可进同一并发批。串行化在 App(App SPEC L402 已述)。Core 侧无「两个并行 ask_user 都到达 handler」的专项测试。建议处理类型:补测试(低优先,契约是"不感知")。

## 已实现但缺测试清单

| ID | 行为 | 建议 |
|---|---|---|
| F-CORE-A-07 | 事件通道无界、慢消费者不丢事件 | 慢读端压测断言事件完整 |
| F-CORE-A-08 | follow-up 续跑计入 MaxTurns | 末回合 follow-up→AgentTurnLimitException |
| F-CORE-A-32 | 内置子 Agent 看不到/调不到 todo | Select+派发双层断言 |
| F-CORE-A-40 | 内置白名单不含 extension_*(可缓存集不变) | 白名单内容 pin |
| F-CORE-A-55 | 步提交后无自动评审(缺失态) | 接线时再补;现状无需 |
| F-CORE-A-66 | 并行 ask_user 均直达 handler(Core 不串行) | 双调用并发到达断言 |

## 程序→规格:未记录行为

以下规则/默认值/副作用在代码中存在(多数有测试),Core SPEC 未写;除注明外建议补规格一句:

1. **Reviewer 自评审守卫**:`context.AgentName == "Reviewer"` 时 request_review 直接报错(RequestReviewTool.cs:69–75;测试 A_reviewer_holding_the_tool_cannot_ask_for_another_review)。规格只说白名单不含该工具,未写这层防用户覆盖 kind 的守卫。
2. **步骤无提交→分支评审回退**(RequestReviewTool.cs:128–134、239–244)——与规格断言冲突,见 PARTIAL 详情。
3. **Reviewer 空文本→错误结果**「The reviewer returned no text; nothing was reviewed.」(RequestReviewTool.cs:176–179)。规格未定义该失败形态。
4. **未知属性拒绝**:request_review 参数含未知名即报错(RequestReviewTool.cs:277–281);规格只写了已知参数的类型错误。
5. **verdict 同义词集**:approved/requests changes/request change/changes requested/changes required(RequestReviewVerdict.cs:120–135);规格只写容忍标记/标点,未列接受词表。
6. **extension_create/update 的 `tool` 参数**(workflow 必填:name/description/input_schema)与文件规则(plugin 恰好一个 .cs;workflow 必含 workflow.json;禁 manifest.json;路径/段校验)(ExtensionSubmitTool.cs schema、ExtensionDraftReader.cs:54–96、200–280)。规格 L41 参数列表缺 `tool`(亦见规格质量 #2)。
7. **todo 状态别名**:todo/open、in-progress/inprogress/doing/running、pause/waiting、completed/closed(TodoTool.cs:560–590;测试 Status_aliases_decide_whether_the_step_commits)。
8. **todo 坏 JSON 的副作用分类**:无法解析的参数按 ReadOnly 分类(调用必失败,无副作用)(TodoTool.cs:148–151;测试 Malformed_json_resolves_read_only_because_invoking_it_is_a_no_op_error)。
9. **ask_user 模型面限额**:1–4 题、2–6 选项、header≤12 字符、id/label 唯一、allowFreeText 默认 true(AskUserTool.cs:18–22、Parse/ParseQuestion)。Core SPEC L111 与 App SPEC L402 均未写全这些数字。
10. **审批处理器串行闸**:approvalGate(1,1)使并发调用的审批请求逐个进行(AgentLoop.cs:82、978–987;测试 ApprovalHandler_is_serialized_across_concurrent_calls)。规格未写。
11. **插话送达时机与回收契约**:只在工具批后送达;无批结束的 run 留待宿主 TakeInterjections 回收(AgentInstance.cs:100–106 注释、AgentLoop.cs:254;App SessionRuntime.cs:316 消费)。规格 L109 只写合并形状(亦见规格质量 #3)。
12. **spawn 结果尾注 `[agent_id: …]`**(SpawnAgentTool.cs:80–83)——评审节写了该形状,SubAgent 节未写。
13. **子 Agent id 清洗与默认停止原因**:kind 中非字母数字字符替换为 '-'(SubAgentScheduler.cs:376–386);Stop 无 reason 默认 "stopped by the user"(SubAgentRun.cs:13)。
14. **ReviewPackageLimits.Sanitized**:DiffChars/FieldChars 下限 1、行数下限 0(ReviewPackageLimits.cs:35–41;测试 Nonsense_limits_from_a_host_do_not_break_the_render)。
15. **DefaultApprovalPolicy 旋钮**:ToolRules 显式规则优先、AutoAllowReadOnly 默认 true(ApprovalPolicy.cs:14–18;有测试)。Core SPEC 只写路径比较;策略组装在 App SPEC,建议 Core 补一句默认规则序。
16. **steer/followup/stop 三工具副作用为 ReadOnly**(SubAgentControlTools.cs)——可进只读并发批;规格未声明其副作用类别。
17. **ReplaceExternal 恒发布新版本**(即使外部集合逐名相同)(ToolRegistry.cs:179–197)——与 SetDisabled 的 SetEquals 免发布不同,同集合替换也会破一次缓存;规格未区分。

## 规格质量问题

1. **L44 与实现矛盾(最重)**:「带 step_id 时变更集就是该步骤自己的那次提交」过于绝对,实现存在"步骤无提交→分支+未提交树"回退(有测试、有自述)。需补例外条款或改实现为显式错误。详见 F-CORE-A-42。
2. **L41 参数契约不完整**:extension_create/update 参数列表缺 `tool`(workflow 必填),也未写两类 kind 的文件规则与本地校验范围(路径段/控制字符/manifest 禁止)。这些是模型面契约,只存在于代码 schema。
3. **L109 缺跨层契约**:插话只定义合并形状与事件,未定义送达时机(工具批后)与「run 结束未送达→宿主 TakeInterjections 回收」的交接契约;App(SessionRuntime.cs:316)已依赖后者。契约漂移即静默丢插话,应写入规格。
4. **L143 措辞过宽**:「目前只有根 Agent 可用」仅对内置 kind 成立;用户自定义子 Agent `ToolNames=null` 时 Select 不剔除 todo(ToolRegistry.cs:51–95 只剔 extension_* 与 subAgentTools)。应改为「内置 Agent 白名单未含 todo」。
5. **L28 歧义**:「要停止派生就关掉它(工具开关或 agents 分区的子 Agent 开关,两者都落到同一个禁用集合),已在跑的子树仍可用 steer/follow_up/stop 管理」未说明 agents 分区开关禁用的是仅 spawn_agent 还是四个工具;若连控制工具一起禁,父 Agent 工具面即不可管理,只剩 UI(ISubAgentControl)。Core 机制(单一禁用集合)已验证,语义需在规格或 App SPEC 明确。
6. **L26 措辞与实现的微小偏差**:「取消时每个待决调用都补一条'已取消'结果」——并行批中一个调用触发取消时,**已完成**的兄弟调用结果也被覆盖为 "Cancelled."(RunBatchAsync 仅在 WhenAll 全部成功后赋值,AgentLoop.cs:889–905+878–886)。历史合法性不变,但"待决"一词未覆盖此情形(见业务闭环 #2)。
7. **范围外相邻观察(一句)**:L89 加粗句「适配器目前只发拉平后的 Content…是后续工作」与 L97–105(三适配器图片线路已实现、IncludeToolImages 已在 BuildRequest 生效)矛盾,属过时叙述;归 Core B/提供商审计处理。

## 业务闭环缺口

1. **插话回收依赖 App 自觉(低风险,契约缺口)**:Core 把未送达插话留在实例上,闭环的最后一环在 App(SessionRuntime.cs:316 已实现)。规格未记录该契约(规格质量 #3),未来换宿主实现可能静默丢用户插话。处理类型:规格补契约。
2. **取消批次的已完成结果被丢弃(低风险,无测试)**:只读并发批中若一个调用因回合取消抛 OCE,WhenAll 失败使该批**所有**结果(含已成功返回的)被 "Cancelled." 覆盖;非取消异常路径同理覆盖为 "The tool call did not complete."。只读副作用无害、历史合法,但模型会看到失真的"已取消"。现有测试只覆盖全待决场景。处理类型:补边界测试;可选改进为保留已完成结果。
3. **评审闭环完整(核实通过)**:空评审不启动 Reviewer(断言 Instances 空)、被停止不解析结论(部分文本含过早 `verdict: approve` 仍判错误)、父取消上抛、单名额 Detach 防死锁、双评审并发各得其结论——均有强断言测试,未发现缺环。自动评审未接线是显式未决,不计缺陷。
4. **todo 提交闭环完整(核实通过)**:done→提交→失败记录→再说 done 重试→成功即停;committer 抛异常也记录;取消传播不落失败;提交失败不回滚 done;跨会话条目拒动。边界测试约 40 条,未发现缺环。
5. **子 Agent 停止/转向/并发闭环完整(核实通过)**:停止释放待决审批(toolInvoked=0)、排队停止不触模型、父取消记 Stopped "parent cancelled"、随机 steer/stop/followUp 交错压力测试保证每 child 恰一次 Finished、名额池取消/缩容/双 Detach 均有测试。未发现缺环。