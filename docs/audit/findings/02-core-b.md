# Core B(存储/会话生命周期/压缩/内容块)审计发现

审计对象:src/Forge.Core/SPEC.md 指定章节(行号以该文件为准)。只读审计;代码引用为 相对路径:行号(省略 src/Forge.Core/ 前缀,Abstractions 除外);测试证据为 tests/Forge.Core.Tests 下 文件::方法(文件名全局唯一,省略目录;长名以…截断)。已先读 .github/skills/test-anti-patterns/SKILL.md;抽样测试未见 Critical 反模式(断言多为精确值/线路字节比较,含子进程并发与竞态测试)。

结论计数:共 123 项;PASS 113,PASS* 1,PARTIAL 5,FAIL 3,UNVERIFIED 1,MISSING 0。三个 FAIL 均为规格过时(实现已前进),非实现缺陷。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-CORE-B-01 | sessions 元数据列;目录/存储双接口同一实现 | SPEC.md:37 | Storage/StorageSchema.cs:34-75;SqliteSessionStore.cs:19 | SqliteSessionCatalogTests::Create_and_get_round_trip_every_catalog_field | PASS |
| F-CORE-B-02 | SessionCatalogs.From 门控(SupportsCatalog;内层非目录→null) | SPEC.md:37 | Agents/SessionCatalogs.cs:8-13;LoggingSessionStore.cs:22-27 | SubAgentCatalogCapabilityPinTests;LoggingSessionStorePinTests | PASS |
| F-CORE-B-03 | 子会话带 Parent/Kind/Task,继承父 worktree/项目 | SPEC.md:37 | Agents/SubAgentScheduler.cs:363-369 | SubAgentSessionPinTests | PASS |
| F-CORE-B-04 | 每回合持久化后 TouchAsync,失败不影响循环 | SPEC.md:37 | Agents/AgentLoop.cs:232,1036-1053 | SqliteSessionActivityTests::Touch_sets_the_last_active_time… | PASS |
| F-CORE-B-05 | ResumeRootAsync 摘要视图+尾部;丢孤立首工具结果/末尾无结果工具回合 | SPEC.md:37 | Agents/AgentHost.cs:330-344;SessionHistory.cs:104-117 | InheritanceTests::Inherited_level_uses_the_resume_trimming… | PASS |
| F-CORE-B-06 | AgentScope AsyncLocal;名额池/id 计数实例内;多主会话并发 | SPEC.md:37 | Agents/AgentEvents.cs:31-33;AgentSlots.cs | AgentSlotPoolTests | PASS |
| F-CORE-B-07 | SQLite 会话/消息/摘要/FTS5;JSONL 事件日志;Artifact 文件 | SPEC.md:57 | Storage/JsonlEventLog.cs;FileArtifactStore.cs | JsonlEventLogTests::Recover_reimports_log_entries_lost_by_a_crash | PASS |
| F-CORE-B-08 | 单写线程批量;返回 seq 已落盘;提交后取消不误判 | SPEC.md:57 | Storage/SqliteSessionStore.cs:452-545,749-792 | SqliteSessionStoreRegressionTests::Random_cancellation_leaves_no_sequence_gap_and_never_hides_a_commit | PASS |
| F-CORE-B-09 | 摘要只追加;被压缩原文可检索取回 | SPEC.md:57 | Storage/SqliteSessionStore.cs:866-886 | SqliteSessionStoreTests::Search_finds_text_of_compacted_away_messages | PASS |
| F-CORE-B-10 | 每次压缩一条 layer-2 摘要;范围=上一摘要起点→最后被摘要消息 | SPEC.md:57 | Agents/Compaction.cs:296-301 | CompactionTests::Every_compaction_appends_a_wider_session_summary… | PASS |
| F-CORE-B-11 | 视图=最宽会话摘要+未覆盖阶段行+尾部 | SPEC.md:57 | Storage/SummaryLayers.cs:48-92 | SummaryLayersTests::Build_prefers_the_widest_session_summary… | PASS |
| F-CORE-B-12 | 旧 layer-1 可读;下次压缩并入上一摘要;无需迁移 | SPEC.md:57 | Agents/Compaction.cs:380-396 | CheckpointCompactionTests::Old_style_stage_rows_resume_and_are_updated_into_one_session_summary | PASS |
| F-CORE-B-13 | PlanCut 只落下标≥1 的 user/assistant,不落工具结果 | SPEC.md:57 | Storage/SummaryLayers.cs:104-167 | SummaryLayersRegressionTests::The_cut_never_lands_on_a_tool_result… | PASS |
| F-CORE-B-14 | v0→v1:基表+会话列+工作台表;回填 Active/最后活动=创建,严格解析失败整步回滚 | SPEC.md:59 | Storage/StorageSchema.cs:309-355 | StorageSchemaMigrationTests::Migration_with_bad_timestamp_rolls_back… | PASS |
| F-CORE-B-15 | v1→v2:todos 五列+plan_summaries | SPEC.md:59 | Storage/StorageSchema.cs:127-139 | StorageSchemaV2MigrationTests::V1_database_with_todos_upgrades_to_v2… | PASS |
| F-CORE-B-16 | v2→v3:规范化+守卫,只处理已存在表/列 | SPEC.md:59,63 | Storage/StorageSchema.cs:362-384 | StorageSchemaV3MigrationTests(8 项) | PASS |
| F-CORE-B-17 | v3→v4:todo_groups+group_id/origin;旧行 NULL/agent 不回写;守卫重装 | SPEC.md:59 | Storage/StorageSchema.cs:141-156,394-400 | StorageSchemaV2MigrationTests::V0_database_reaches_the_current_schema… | PASS |
| F-CORE-B-18 | v4→v5:occupancy_key C# 回填+去重归档+notice+部分唯一索引;可重跑 | SPEC.md:59,67 | Storage/StorageSchema.cs:411-486 | StorageSchemaV5To9MigrationTests::Running_the_v5_to_v9_steps_again_changes_nothing | PASS |
| F-CORE-B-19 | v5→v6:deleted_at,无回填 | SPEC.md:67 | Storage/StorageSchema.cs:488-495 | StorageSchemaV5To9MigrationTests::A_v4_database_climbs_to_v9… | PASS |
| F-CORE-B-20 | v6→v7:projects.closed_at,无回填 | SPEC.md:67 | Storage/StorageSchema.cs:498-505 | 同上 climb;SqliteProjectRegistryCloseTests | PASS |
| F-CORE-B-21 | v7→v8:fork_of_branch/fork_base_sha,无回填 | SPEC.md:67 | Storage/StorageSchema.cs:508-518 | 同上 climb | PASS |
| F-CORE-B-22 | v8→v9:继承五列;lineage 回填自身/origin 回填 cold | SPEC.md:67 | Storage/StorageSchema.cs:521-547 | 同上 climb;SessionLifecycleStoreTests::Create_round_trips_lineage_inheritance_and_origin | PASS |
| F-CORE-B-23 | v9→v10:automation_runs/automation_state+会话两列+守卫 | SPEC.md:59,133 | Storage/StorageSchema.cs:549-563,625-660 | StorageSchemaV10MigrationTests(5 项,含回滚/重跑) | PASS |
| F-CORE-B-24 | v10→v11:plan_summary_revisions+id DESC 索引;无回填;守卫 replaced_at | SPEC.md:59,61 | Storage/StorageSchema.cs:566-575,662-672 | SqlitePlanRevisionTests::A_v10_database_migrates_to_v11_without_backfilling_history | PASS |
| F-CORE-B-25 | 每步 BEGIN IMMEDIATE+同事务写版本+事务内重读(多进程) | SPEC.md:59 | Storage/StorageSchema.cs:253-307 | StorageSchemaConcurrencyPinTests::Two_processes_opening_the_same_fresh_database… | PASS |
| F-CORE-B-26 | 更高版本立即 InvalidDataException,不降级不写 | SPEC.md:59 | Storage/StorageSchema.cs:215-222 | StorageSchemaV2MigrationTests::A_newer_schema_is_refused_by_every_new_operation_without_writing | PASS |
| F-CORE-B-27 | v5 注入失败回滚 v4(两 Active、键随 DDL 消失),重跑爬到 v9 | SPEC.md:59 | Storage/StorageSchema.cs:411-486 | StorageSchemaV5To9MigrationTests::A_v5_failure_after_the_archives_rolls_the_whole_step_back_to_v4 | PASS |
| F-CORE-B-28 | 排序用 last_active_ticks 非 ISO 文本 | SPEC.md:59 | Storage/StorageSchema.cs:79-83;SqliteSessionStore.cs:154-201 | SqliteSessionCatalogTests::Query_orders_by_last_activity_descending_after_touch | PASS |
| F-CORE-B-29 | FTS 与阶梯解耦;首建+回填同事务;坏消息跳过 | SPEC.md:59 | Storage/StorageSchema.cs:880-990 | StorageSchemaMigrationTests;SqliteSessionStoreTests::Search_matches_all_terms_and_reports_fts_support | PARTIAL |
| F-CORE-B-30 | SetPlanSummary 同事务记旧文本;同文本不记 | SPEC.md:61 | Storage/SqliteTodoStore.cs:355-406 | SqlitePlanRevisionTests::Concurrent_replacements_record_every_replaced_text_once | PASS |
| F-CORE-B-31 | 每会话留最新 50 条修订 | SPEC.md:61 | Storage/SqliteTodoStore.cs:26,385-392 | SqlitePlanRevisionTests::History_is_trimmed_to_the_newest_PlanRevisionLimit_rows | PASS |
| F-CORE-B-32 | ListPlanRevisions id 倒序;limit 夹 1..50 | SPEC.md:61 | Storage/SqliteTodoStore.cs:326-349 | SqlitePlanRevisionTests::Limit_is_clamped_between_one_and_PlanRevisionLimit | PASS |
| F-CORE-B-33 | 修订随会话复制/清理 | SPEC.md:61 | Storage/SqliteSessionStore.Lifecycle.cs:690-695,782-790 | SqlitePlanRevisionTests::Handover_copies_the_revision_history…;Purge_removes_the_sessions_revisions… | PASS |
| F-CORE-B-34 | 面板只读历史;恢复不走 UI 写路径 | SPEC.md:61 | Core 无恢复写 API(边界一致) | 无(App 侧,超出范围) | UNVERIFIED |
| F-CORE-B-35 | 时间戳只存 "O" 形状;读取严格 | SPEC.md:63 | Storage/StorageDb.cs:113-127;但 SqliteSessionStore.cs:255,300 宽松 | StorageSchemaV3MigrationTests::A_timestamp_without_an_offset_is_left_untouched…(仅严格路径) | PARTIAL |
| F-CORE-B-36 | v3 规范化:带 offset 可解析→改写(时刻/offset 不变);否则保留不猜 | SPEC.md:63 | Storage/StorageDb.cs:139-176;StorageSchema.cs:727-780 | StorageSchemaV3MigrationTests::The_repair_keeps_the_instant_and_the_offset… | PASS |
| F-CORE-B-37 | BEFORE INSERT/UPDATE 触发器 GLOB+ABORT;NULL 允许 | SPEC.md:63 | Storage/StorageSchema.cs:820-858 | StorageSchemaV3MigrationTests::Every_timestamp_column_gets_an_insert_and_an_update_guard | PASS |
| F-CORE-B-38 | 守卫只建已存在表;v4 起全量重装幂等;现 12 表 19 列 | SPEC.md:63 | Storage/StorageSchema.cs:161-175,790-818 | StorageSchemaV5To9MigrationTests::New_timestamp_columns_are_guarded_like_the_old_ones | PASS |
| F-CORE-B-39 | 新时间戳列须登记 TimestampColumns | SPEC.md:63 | Storage/StorageSchema.cs:161-175 | v10/v11 守卫测试证明登记→守卫链路 | PASS |
| F-CORE-B-40 | approvals.db 不在守卫清单 | SPEC.md:63 | TimestampColumns 无 approvals;Storage/ 无审批库代码 | 无(结构性边界) | PASS |
| F-CORE-B-41 | 工作台同库;长连接+信号量+每写 IMMEDIATE;读池化短连接 | SPEC.md:65 | Storage/SqliteWriter.cs:17-67;StorageDb.cs:27-34 | SqliteTodoStoreTests;SqliteUiStateStoreTests | PASS |
| F-CORE-B-42 | Changed 提交后写锁外;异常吞;一次写入一次 | SPEC.md:65 | Storage/SqliteTodoStore.cs:918-937 | SqliteTodoStoreTests::Throwing_handler_does_not_fail_the_write_and_others_still_run | PASS |
| F-CORE-B-43 | root_key 唯一(Windows 大写);General 部分索引单例;再注册改指向 | SPEC.md:65 | Storage/StorageSchema.cs:92-94;SqliteProjectRegistry.cs:66-135,228 | SqliteProjectRegistryTests(7 项) | PASS |
| F-CORE-B-44 | TODO 层内致密;插/删/Move 只重排本层 | SPEC.md:65 | Storage/SqliteTodoStore.cs:848-916 | SqliteTodoStoreGroupTests::Inserts_removes_and_moves_renumber_only_their_own_layer | PASS |
| F-CORE-B-45 | 分组顺序会话内致密只增;无删除入口 | SPEC.md:65 | Storage/SqliteTodoStore.cs:436-518 | SqliteTodoStoreGroupTests::ListGroups_returns_groups_in_order… | PASS |
| F-CORE-B-46 | 同会话单步 in_progress,同事务其余退 Pending | SPEC.md:65 | Storage/SqliteTodoStore.cs:221-235 | SqliteTodoStoreGroupTests::Only_one_step_is_in_progress_across_groups_and_the_queue… | PASS |
| F-CORE-B-47 | 检查点 (session,turn) PK;已完成不被 Begin 改写 | SPEC.md:65 | Storage/SqliteTurnCheckpointStore.cs:64-67,91-92 | SqliteTurnCheckpointStoreTests::Begin_on_a_completed_turn_leaves_the_recorded_diff_untouched | PASS |
| F-CORE-B-48 | synchronous=NORMAL | SPEC.md:65 | Storage/StorageDb.cs:73-77 | 无 | PASS* |
| F-CORE-B-49 | 三状态 Active/Archived/Deleted | SPEC.md:67 | Storage/SqliteSessionStore.cs:356-370 | SqliteSessionCatalogTests::Row_with_unknown_status_text_fails_with_InvalidDataException | PASS |
| F-CORE-B-50 | OccupancyKey:GetFullPath+去尾分隔+Windows 大写;存储层每写计算 | SPEC.md:67 | Storage/OccupancyKey.cs:13-27;Lifecycle.cs:232 | StorageSchemaV5To9MigrationTests::The_occupancy_key_normalizes_exactly_like_WorkspacePath | PASS |
| F-CORE-B-51 | ux_sessions_occupancy(Active 根)部分唯一索引;冲突→WorktreeOccupiedException(holder) | SPEC.md:67 | Storage/StorageSchema.cs:186-188;Lifecycle.cs:322-358 | SessionLifecycleStoreTests::Second_active_root_session_on_the_same_key_is_refused_with_the_holder_id | PASS |
| F-CORE-B-52 | Active 只走条件插入/Activate/Handover;SetStatus 原始写仅索引把关 | SPEC.md:67 | Storage/SqliteSessionStore.Lifecycle.cs:367-471,518-543 | SessionLifecycleStoreTests::Unique_index_backstop_turns_a_raw_activation_into_WorktreeOccupied… | PASS |
| F-CORE-B-53 | v5 去重留 ticks 最大、平局 id 大;归档写 dedupe-archived | SPEC.md:67 | Storage/StorageSchema.cs:447-480 | StorageSchemaV5To9MigrationTests::Duplicate_active_roots_are_archived_except_the_most_recent… | PASS |
| F-CORE-B-54 | v7 软关闭;AddAsync 同路径复活原 id | SPEC.md:67 | Storage/SqliteProjectRegistry.cs:83,102,152-165 | SqliteProjectRegistryCloseTests::Closed_project_leaves_the_list_and_re_adding_it_reopens_the_same_row… | PASS |
| F-CORE-B-55 | 每批一个 IMMEDIATE;每操作 SAVEPOINT forge_op 不连坐 | SPEC.md:69 | Storage/SqliteSessionStore.cs:38,482-569;UsesSavepoint 仅部分 op | SessionLifecycleStoreTests::Conflicting_create_batched_with_an_append_fails_alone | PARTIAL |
| F-CORE-B-56 | Handover 单事务:归档≠1 抛 SessionNotActive;条件插入继承 fork/lineage+InitialMessages+开关复制 todos/groups/plans/修订;或恢复只改状态;失败全回滚 | SPEC.md:69 | Storage/SqliteSessionStore.Lifecycle.cs:554-704 | SessionLifecycleStoreTests::Handover_rolls_back_entirely_when_a_sub_step_fails 等 6 项 | PASS |
| F-CORE-B-57 | SoftDelete 级联子会话 | SPEC.md:69 | Storage/SqliteSessionStore.Lifecycle.cs:26-32,473-482 | SessionLifecycleStoreTests::Soft_delete_cascades_to_descendants… | PASS |
| F-CORE-B-58 | Purge 只删 Deleted+子的 session_id 键行(含 FTS);被 inherits 指向→拒绝 | SPEC.md:69 | Storage/SqliteSessionStore.Lifecycle.cs:714-809 | SessionLifecycleStoreTests::Purge_is_refused_while_a_session_outside_the_purged_set_inherits_from_it | PASS |
| F-CORE-B-59 | 常规查询排除 Deleted;ListHistory 包含 | SPEC.md:69 | Storage/SqliteSessionStore.cs:145-201;Lifecycle.cs:100-105 | SessionLifecycleStoreTests::Soft_delete_cascades…(常规查询隐藏) | PASS |
| F-CORE-B-60 | Heal:目录缺失且项目根仍在的 Active/Archived 根→Deleted 级联+heal-deleted 只列根;写内复查 | SPEC.md:69 | Storage/SqliteSessionStore.Lifecycle.cs:112-158,811-847 | SessionLifecycleStoreTests::Heal_soft_deletes_roots_with_missing_directories_only_when_the_project_root_exists | PASS |
| F-CORE-B-61 | ReadNotices/AcknowledgeNotices | SPEC.md:69 | Storage/SqliteSessionStore.Lifecycle.cs:179-215,892-905 | SessionLifecycleStoreTests::Notices_are_read_oldest_first_until_acknowledged | PASS |
| F-CORE-B-62 | 继承=父指针:递归装父截至 up_to_seq 视图+尾部,同裁剪,标 Inherited | SPEC.md:71 | Agents/SessionHistory.cs:21-121 | InheritanceTests::Multi_level_inheritance_keeps_every_level_byte_identical | PASS |
| F-CORE-B-63 | 首请求前缀与父最后请求逐字节相同 | SPEC.md:71 | Agents/SessionHistory.cs;AgentLoop.BuildRequest | InheritanceTests::First_child_request_repeats_the_parents_last_request_on_the_wire(wire 原文比较) | PASS |
| F-CORE-B-64 | 压缩保留继承前缀;只摘要自有;预算扣继承;不清父指针 | SPEC.md:71 | Agents/Compaction.cs:259-266,323-365 | CheckpointCompactionTests::The_inherited_prefix_is_kept_whole_and_never_summarized | PASS |
| F-CORE-B-65 | HandoverSummary 专用提示词;InheritanceNote.Build/BuildFork 要素齐全 | SPEC.md:71 | Agents/SessionHandover.cs:50,162,188-205 | HandoverSummaryTests::Inheritance_note_names_head_handover_commit_and_carried_changes 等 14 项 | PASS |
| F-CORE-B-66 | 每次合成算指纹;仅变化写回 | SPEC.md:71 | Agents/AgentLoop.cs:484-518;Lifecycle.cs:849-861 | SessionLifecycleStoreTests::Prefix_fingerprint_is_written_only_when_it_changes | PASS |
| F-CORE-B-67 | RoutingKey=lineage_root;SendPromptCacheKey 默认关;DeepSeek 不发;hit_tokens 计读取 cached_tokens 优先 | SPEC.md:71 | Agents/AgentLoop.cs:392,446-460;Providers/OpenAiCompatibleProvider.cs:120,164,729-741 | PromptCacheLoopTests;PromptCacheWireTests | PASS |
| F-CORE-B-68 | 首请求缓存用量+origin/提供商/模型诊断 | SPEC.md:71 | Agents/AgentLoop.cs:228,520-545 | PromptCacheLoopTests(收集 FirstRequestCacheReport,行 200) | PASS |
| F-CORE-B-69 | MessageCacheTtl→CacheHint.MessageTtl;Anthropic 忽略 | SPEC.md:71 | 属性已不存在:AgentOptions.CacheTtl;Abstractions/Providers.cs:22(Ttl);Providers/AnthropicProvider.cs:593-596 已遵循 | PromptCacheLoopTests;ClaudeWireTests | FAIL |
| F-CORE-B-70 | R=min(maxOut,⌊0.2W⌋)→max(R,min(4096,W/2));U=W−R | SPEC.md:73 | Agents/CompactionPolicy.cs:79-91 | CompactionPolicyTests::Budget_reserves_output_and_leaves_the_rest_usable(Theory) | PASS |
| F-CORE-B-71 | 默认 0.85/0.5/0.1;Validate 拒非法 | SPEC.md:73 | Agents/CompactionPolicy.cs:34-52;AgentHost.cs:111 | CompactionPolicyTests::Invalid_ratios_are_refused_by_the_host | PASS |
| F-CORE-B-72 | maxOut 模型值优先否则解析器;异常按未知 | SPEC.md:73 | Agents/CompactionPolicy.cs:61-72 | CompactionPolicyTests::BudgetFor_prefers_the_models_own_output_limit… | PASS |
| F-CORE-B-73 | C=上次真实 InputTokens+新增估算(key+版本一致);否则全量+前缀+工具(每定义+8,按版本缓存) | SPEC.md:73 | Agents/AgentLoop.cs:745-780,1095-1108 | CompactionPolicyTests::Real_prompt_usage_triggers_compaction_even_when_the_estimate_is_small | PASS |
| F-CORE-B-74 | 迟滞:无效后等自有+0.1U;两次→Stalled+事件,直到手动/换模型 | SPEC.md:73 | Agents/AgentLoop.cs:596-625,795-805;CompactionPolicy.cs:23 | CompactionPolicyTests::Two_fruitless_attempts_stop_automatic_compaction_until_a_manual_one | PASS |
| F-CORE-B-75 | 关/Noop/Stalled/W 未知不自动;子 Agent 自己窗口 | SPEC.md:73 | Agents/AgentLoop.cs:577-586 | CompactionPolicyTests::A_sub_agent_is_measured_against_its_own_models_window | PASS |
| F-CORE-B-76 | keepRecent=max(1,min(20000,⌊0.2U⌋));自动/溢出夹≤目标一半 | SPEC.md:73 | Agents/CompactionPolicy.cs:103;AgentLoop.cs:665 | CheckpointCompactionTests::Automatic_compaction_keeps_at_least_keep_recent… | PASS |
| F-CORE-B-77 | goal=max(keepRecent,目标−继承−摘要);最长合法后缀;放不下留最新切点后;整条保留 | SPEC.md:73 | Agents/Compaction.cs:261-264;SummaryLayers.cs:104-167 | SummaryLayersRegressionTests(11 项) | PASS |
| F-CORE-B-78 | SetCompactionPolicy 先校验后 volatile 替换;根+子共用;不重置迟滞 | SPEC.md:73 | Agents/AgentHost.cs:195-208 | LiveCompactionPolicyTests::A_policy_set_while_a_turn_runs_applies_to_that_turns_next_request | PASS |
| F-CORE-B-79 | ContextMeasured:每请求落盘后+每压缩后;W 未知 null;手动仅 sink 且确有压缩 | SPEC.md:73 | Agents/AgentLoop.cs:231,611,655,717-727 | LiveCompactionPolicyTests::Every_model_request_reports_the_context_against_the_usable_budget 等 7 项 | PASS |
| F-CORE-B-80 | IsContextOverflow:Data/413/短语;可重试与 429 不算 | SPEC.md:75 | Providers/ProviderErrors.cs:47-80 | CompactionPolicyTests::Rate_limits_retryable_and_unrelated_errors_are_not_overflow | PASS |
| F-CORE-B-81 | 仅流无内容:强制压缩一次+重发一次;二次溢出/没腾出→原错误;W 未知生效;关/Noop 不生效 | SPEC.md:75 | Agents/AgentLoop.cs:203-218,629-658 | CompactionPolicyTests::Overflow_compacts_once_and_resends_the_request_once 等 4 项 | PASS |
| F-CORE-B-82 | 手动目标 1、无视触发线/Enabled、重置迟滞;keepRecent(未知 20000) | SPEC.md:77 | Agents/AgentLoop.cs:686-712 | CompactionPolicyTests::Manual_compaction_ignores_the_threshold_and_the_switch… | PASS |
| F-CORE-B-83 | instructions→"\n\nAdditional focus: <text>" 追加每请求 | SPEC.md:77 | Agents/Compaction.cs:272-274,727-738 | CheckpointCompactionTests(行 241,272-275 断言后缀) | PASS |
| F-CORE-B-84 | 被拒抛 CompactionFailedException;无存储不生效;调用方保证无回合 | SPEC.md:77 | Agents/Compaction.cs:907-971;AgentLoop.cs:686-712 | CheckpointCompactionTests::A_rejected_manual_summary_surfaces_as_a_failed_compaction | PASS |
| F-CORE-B-85 | summarizer 槽位默认 SummarizerPrompt;结构化检查点 | SPEC.md:81 | Agents/Compaction.cs:64-98 | CompactionTests::An_agent_host_built_with_an_overridden_compaction_slot_summarizes_with_it | PASS |
| F-CORE-B-86 | 序列化标签;thinking 不写;工具结果 2000 截断标记;图/文件占位 | SPEC.md:81 | Agents/Compaction.cs:181,747-783,885-897 | CheckpointCompactionTests::The_first_compaction_sends_the_structured_prompt… | PASS |
| F-CORE-B-87 | previous-summary(会话前阶段后、剥文件标签)+UPDATE | SPEC.md:81 | Agents/Compaction.cs:380-396,591-605,727-738 | CheckpointCompactionTests::A_second_compaction_updates_the_previous_summary… | PASS |
| F-CORE-B-88 | 批预算=(W−min(上限,W/2))·0.6·4−固定项,夹[4000,400000];不可拆单元贪心;超预算中间截断 | SPEC.md:81 | Agents/Compaction.cs:185-199,616-696 | CompactionBatchingTests::A_single_message_larger_than_a_request_is_cut_in_the_middle_with_a_marker 等 4 项 | PASS |
| F-CORE-B-89 | 首批初始/UPDATE,后续 UPDATE+上批输出;末批即摘要;无静默丢弃 | SPEC.md:81 | Agents/Compaction.cs:553-589 | CompactionBatchingTests::A_range_over_the_budget_is_summarized_in_batches… | PASS |
| F-CORE-B-90 | 文件追踪 read/edit/write path;合并;既读既改列 modified;排序追加 | SPEC.md:81 | Agents/Compaction.cs:204-210,444-526 | CheckpointCompactionTests::A_second_compaction…accumulates_the_file_lists | PASS |
| F-CORE-B-91 | 拆分回合 TURN-PREFIX 并行+合并;前缀分批续写;旧段降级/(no new messages) | SPEC.md:81-87 | Agents/Compaction.cs:117-171,281-295,534-547 | CheckpointCompactionTests::One_long_tool_loop_is_split_inside_the_turn…;CompactionBatchingTests::A_new_split_without_new_history_folds… | PASS |
| F-CORE-B-92 | 任一批失败整次失败(联动取消);全成功才写唯一摘要 | SPEC.md:81 | Agents/Compaction.cs:279-301,584-588 | CompactionBatchingTests::A_failing_history_chain_cancels_the_turn_prefix_chain | PASS |
| F-CORE-B-93 | 摘要请求无工具/Thinking 关/SessionId/上限 min(模型,8192|4096);五种拒绝;取消传播;自动/溢出拒绝计无效 | SPEC.md:81 | Agents/Compaction.cs:177-178,608-609,907-971;AgentLoop.cs:668-676 | CheckpointCompactionTests::An_unusable_summary_is_rejected_and_nothing_changes(Theory)等 3 项 | PASS |
| F-CORE-B-94 | Blocks 只 Text/Image,ctor/with 校验;便捷构造;Content 拉平;逐块相等 | SPEC.md:89 | Abstractions/Tools.cs:12-35;Messages.cs:15-38;ToolResultBlocks.cs:16-140 | ToolResultBlocksTests(21 项) | PASS |
| F-CORE-B-95 | 恒写拉平 content;非单文本块才写 blocks;旧行兼容不升 schema | SPEC.md:89 | Storage/MessageJson.cs:235-242,317-333 | MessageJsonTests;SqliteSessionStoreTests::Append_round_trips_every_block_type_and_usage | PASS |
| F-CORE-B-96 | 溢出只搬文本、图保留;工具结果图 1600 | SPEC.md:89 | Agents/AgentLoop.cs:992-1024;TokenCounter.cs:66-84 | ToolResultImageLoopTests::Over_threshold_text_is_stored_in_full_while_the_image_survives | PASS |
| F-CORE-B-97 | “适配器只发拉平 Content,发图是后续工作 W4” | SPEC.md:89 | 已实现发图:Providers/OpenAiCompatibleProvider.cs:420-470;AnthropicProvider.cs:540+;OpenAiResponsesProvider.cs:231-265 | ToolImageTurnTests;ToolResultImageLoopTests | FAIL |
| F-CORE-B-98 | Progress=尾部快照;每调用独立 context→事件夹 Started/Finished;不落库 | SPEC.md:91 | Agents/AgentLoop.cs:941 | ToolProgressLoopTests::Progress_is_not_written_to_the_history_or_the_store 等 6 项 | PASS |
| F-CORE-B-99 | 每调用 CTS 链回合令牌;Started 前登记;finally TryRemove(KVP)+Dispose;整树共用 | SPEC.md:93 | Agents/ToolCallCancellations.cs:17-39;AgentLoop.cs:79,934-963 | ToolCallCancelTests::A_stop_racing_the_calls_own_finish_never_throws_and_leaks_no_token_source | PASS |
| F-CORE-B-100 | CancelToolCall:CancelAsync;结束/未知/已请求→false;ODE 吞→false | SPEC.md:93 | Agents/ToolCallCancellations.cs:45-65;AgentHost.cs:224-225 | ToolCallCancelTests::A_stop_for_a_call_that_is_not_running_is_refused | PASS |
| F-CORE-B-101 | 单调用 OCE→CancelledByUserResult 写回,模型继续;回合取消照旧;忽略令牌者留真实结果 | SPEC.md:93 | Agents/AgentLoop.cs:75,944-957 | ToolCallCancelTests::A_call_that_ignores_its_token_keeps_its_real_result 等 3 项 | PASS |
| F-CORE-B-102 | 令牌传导:bash 进程树 Kill;spawn 子 run 链调用令牌;转存用回合令牌 | SPEC.md:93 | AgentLoop.cs:966(spill 用 ct,已验证);ProcessRunner 未精读 | ToolCallCancelTests::Stopping_a_spawn_agent_call_stops_the_child…(子链已证);bash Kill 无证据 | PARTIAL |
| F-CORE-B-103 | ImageBlock 用户消息;三适配器编码;MessageJson 原样持久化 | SPEC.md:95 | Providers/AnthropicProvider.cs;OpenAiCompatibleProvider.cs:486-487;OpenAiResponsesProvider.cs:205-206;MessageJson.cs:264-267,304 | ImageWireTests(9 项);ImageBlockStorageTests(6 项) | PASS |
| F-CORE-B-104 | 每图固定 1600 | SPEC.md:95 | Agents/TokenCounter.cs:22,55 | ToolResultImageAccountingTests | PASS |
| F-CORE-B-105 | 压缩只发 [image:…]/[file:…] 占位 | SPEC.md:95 | Agents/Compaction.cs:798-825;Abstractions/ContentPlaceholders.cs | CheckpointCompactionTests::Images_and_files_reach_the_summarizer_only_as_placeholders | PASS |
| F-CORE-B-106 | Text=仅 TextBlock 逐行;HasImages 不建列表;单层嵌套 | SPEC.md:97 | Abstractions/ToolResultBlocks.cs:82-120 | ToolImageTurnTests::A_tool_result_carries_its_image_and_reads_two_ways | PASS |
| F-CORE-B-107 | IncludeToolImages 默认 true;每请求现问 Func+ModelInputs(未知可收图);false 只写 Content 图仍存 | SPEC.md:99 | Agents/AgentLoop.cs:394,402-410;AgentOptions.cs:39,47 | ToolImageTurnTests::The_switch_is_asked_per_request_so_a_flip_applies_without_rebuilding_the_host 等 8 项 | PASS |
| F-CORE-B-108 | Anthropic 有图数组(Text 前)否则字符串;纯文本字节不变 | SPEC.md:101 | Providers/AnthropicProvider.cs:490-494,540+ | ToolImageTurnTests::A_text_only_tool_result_reads_the_same_both_ways | PASS |
| F-CORE-B-109 | Responses output 收 input_text/input_image 数组 | SPEC.md:102 | Providers/OpenAiResponsesProvider.cs:231-265 | ImageWireTests;ToolResultImageLoopTests | PASS |
| F-CORE-B-110 | Chat Completions tool 只文本;整组后一条携图 user(说明行+块序),不插组内 | SPEC.md:103 | Providers/OpenAiCompatibleProvider.cs:18,202-218,420-470 | ToolImageTurnTests;ToolResultImageLoopTests | PASS |
| F-CORE-B-111 | 工具图溢出只搬文本;1600;压缩占位 | SPEC.md:105 | Agents/AgentLoop.cs:1005-1024;TokenCounter.cs:66-84 | ToolResultImageLoopTests::Several_text_blocks_over_the_threshold…;ToolResultImageCompactionTests | PASS |
| F-CORE-B-112 | FileBlock+静态 WireInputs 表;CanCarry 按媒体类型 | SPEC.md:107 | Providers/ProviderAttachments.cs:46-68;OpenAiCompatibleProvider.cs:31 | FileWireTests::Can_carry_is_decided_per_media_type | PASS |
| F-CORE-B-113 | 编码 document/file/input_audio/input_file;不支持组合构造期抛不可重试异常不发请求 | SPEC.md:107 | Providers/AnthropicProvider.cs:517-527;OpenAiCompatibleProvider.cs:361-381;OpenAiResponsesProvider.cs:185-205 | FileWireTests::Anthropic_refuses_audio_and_video_before_sending(断言 Calls==0)等 3 项 | PASS |
| F-CORE-B-114 | “绝不静默丢弃” | SPEC.md:107 | assistant 消息中 FileBlock 被 Anthropic 静默丢弃(测试钉住) | FileWireTests::Anthropic_drops_a_file_block_outside_a_user_message | PARTIAL |
| F-CORE-B-115 | "type":"file" 持久化;搜索/全文/压缩只占位 | SPEC.md:107 | Storage/MessageJson.cs:269-275,305-308,111-135 | FileBlockStorageTests::Full_text_and_search_text_carry_a_placeholder_never_the_base64 | PASS |
| F-CORE-B-116 | 文件每已开始 KB 250 token | SPEC.md:107 | Agents/TokenCounter.cs:29-39 | FileBlockStorageTests::The_estimate_counts_a_file_by_its_decoded_size | PASS |
| F-CORE-B-117 | TextAttachment:File: 头+自适应围栏 TextBlock | SPEC.md:107 | Storage/TextAttachment.cs:19-34 | FileBlockStorageTests::The_fence_is_longer_than_any_backtick_run_in_the_content | PASS |
| F-CORE-B-118 | 未决:阈值/keepRecent 默认未验证 | SPEC.md:145 | Agents/AgentOptions.cs:30-32(32000/2000) | 记录与实现一致 | PASS |
| F-CORE-B-119 | 未决:文件追踪只认 read/edit/write path | SPEC.md:146 | Agents/Compaction.cs:204-210 | 记录与实现一致 | PASS |
| F-CORE-B-120 | 未决:4 字符/token;0.6 余量;合并依赖模型遵从 | SPEC.md:147 | Agents/Compaction.cs:194-196,534-547 | 记录与实现一致 | PASS |
| F-CORE-B-121 | 未决:Anthropic "1h" 未接线 | SPEC.md:148 | 已接线:AgentLoop.cs:416-440;AnthropicProvider.cs:593-596 | PromptCacheLoopTests;ClaudeWireTests | FAIL |
| F-CORE-B-122 | 未决:跨前缀 ThinkingBlock 可能被拒;检索不沿继承链 | SPEC.md:149 | Agents/RetrievalTools.cs:135,211(仅本会话) | 记录与实现一致 | PASS |
| F-CORE-B-123 | 未决:继承前缀超线,迟滞限两次,根因未除 | SPEC.md:150 | Agents/CompactionPolicy.cs:23;AgentLoop.cs:596-625 | CompactionPolicyTests::An_inherited_prefix_over_the_trigger_line_is_not_recompacted_every_turn | PASS |

## 非 PASS 项详情

### F-CORE-B-29(PARTIAL)FTS 降级在“表已存在但模块缺失”时打断写入
- 证据:StorageSchema.cs:884-895 EnsureSearchTableAsync 在 messages_fts 已存在时仅返回 CanReadFtsAsync(本构建无 FTS5 模块则 false),不创建 messages_text 回退表;SqliteSessionStore.cs:443-447 却把 _searchInsertSql 设为 INSERT INTO messages_text,SearchLikeAsync(:671-698)也查该表。
- 闭环缺环:带 FTS5 构建建库→无 FTS5 构建打开:每条含可搜索文本的 append 抛 “no such table”;AppendOp 无保存点→整批失败;约束错误不在瞬时重试集→RunWriterAsync 置 _writerFault,存储永久不可用(:466-478,571-585)。规格承诺的“可用性取决于编译选项”解耦在写路径未闭环。
- 缓解:Microsoft.Data.Sqlite 原生包自带 FTS5,当前近乎理论路径;无测试覆盖。
- 建议:实现缺陷(低概率高影响)。CanReadFtsAsync=false 时同事务补建并回填 messages_text,或写路径降级跳过搜索行;补混合场景测试。

### F-CORE-B-35(PARTIAL)messages.at / summaries.at 读取不严格
- 证据:StorageDb.cs:104-105 ParseTimestamp 宽容(DateTimeOffset.Parse);SqliteSessionStore.cs:255(ReadAsync)、:300(GetSummariesAsync)用它读 messages.at/summaries.at。其余读路径均用严格的 ParseCanonicalTimestamp(StorageDb.cs:113-127)。
- 规格(SPEC.md:63)承诺“读取严格”“无 offset 的值…由严格读取指名报错”。v3 后残留的无 offset 值在这两条路径被静默按本地时区解析——正是规格要避免的失败形态。
- 测试缺口:V3 的“指名报错”测试只覆盖严格路径;无 messages/summaries 坏时间戳测试。
- 建议:实现缺陷。两处改 ParseCanonicalTimestamp(带列名/行键)并补测试;守卫已挡新坏写入,影响限于历史残留。

### F-CORE-B-55(PARTIAL)保存点只包“可能业务冲突”的操作
- 证据:UsesSavepoint 默认 false(SqliteSessionStore.cs:759);仅根 Create/SetStatus(Active)/Activate/Handover/SoftDelete/Purge/Heal/RenameBranch 覆写 true(Lifecycle.cs:497,520,547,563,708,718,813,865)。Append/PutSummary/Touch/UpdateSession/SetFingerprint/AcknowledgeNotices/子 Create 不在保存点内,其非瞬时失败(Serialize 抛错、FTS 插入失败、触发器 ABORT)经 catch-all(:529-538)使同批全部失败——规格字面“每个操作包在 SAVEPOINT”未满足。
- 已验证闭环:占用冲突/交接拒绝/清理拒绝只回滚自身保存点,同批 append 照常提交(Conflicting_create_batched_with_an_append_fails_alone;RunContainedAsync:547-569);瞬时 BUSY/LOCKED 整批重试且 BeginAttempt 清旧失败。
- 建议:把所有 op 包进保存点(注意 seq 回滚语义),或修订规格为“可能冲突的操作包保存点”。实现注释(I1)已表达窄化意图,规格未跟上。

### F-CORE-B-69 / 97 / 121(FAIL ×3)规格过时,实现已前进
- 69:SPEC.md:71 的 AgentOptions.MessageCacheTtl、CacheHint.MessageTtl 全 src 无命中;现为 AgentOptions.CacheTtl→CacheHint.Ttl(Abstractions/Providers.cs:22)。“Anthropic 目前忽略它”为假:AnthropicProvider.cs:593-596 按 Ttl 写/不写 ttl 字段;AgentLoop.cs:416-440 实现 auto 5m→1h 升档(与 SPEC.md:123 一致)。
- 97:SPEC.md:89 “只发拉平 Content…发图是后续工作 W4…当前没有任何工具产出图片”与同文件 97-105 及实现矛盾:三适配器已按 IncludeToolImages 发图,ToolImageTurnTests/ToolResultImageLoopTests 钉住。
- 121:未决 SPEC.md:148 “1h 未接线…待定”已被推翻;断点冲突已由 123 行“两 system 块 1h、工具不带断点”方案解决。
- 建议:规格修订(非代码缺陷)。删/改 71 行末句与 89 行粗体段,移除 148 行未决项。

### F-CORE-B-102(PARTIAL)取消传导两段已证、一段未证
- 已证:SpillAsync 用回合令牌(AgentLoop.cs:966,1005-1010);spawn_agent 子 run 链调用令牌(ToolCallCancelTests::Stopping_a_spawn_agent_call_stops_the_child_and_the_parent_goes_on)。
- 未证:bash/ProcessRunner Kill(entireProcessTree:true) 属 Tools 范围,未精读,本范围测试无直接断言。
- 建议:补证后转 PASS;不视为已知缺陷。

### F-CORE-B-114(PARTIAL)assistant 消息中的 FileBlock 被 Anthropic 静默丢弃
- 证据:FileWireTests::Anthropic_drops_a_file_block_outside_a_user_message 断言请求体不含 PDF 数据且不抛异常——钉住了与规格全称句“绝不静默丢弃”(SPEC.md:107)相反的行为。媒体类型维度的不支持组合确实都抛不可重试异常(三个 refuses 测试断言 Calls==0),角色维度丢弃未被规格覆盖。
- 风险:历史中若出现 assistant 携 FileBlock(继承/手工构造),内容无声消失。
- 建议:规格限定“user 消息中的媒体类型组合”,或实现对非 user 角色 FileBlock 同样抛错(倾向后者)。

### F-CORE-B-34(UNVERIFIED)面板只读、恢复不走 UI 写路径
- Core 侧一致:无恢复写 API,ListPlanRevisionsAsync 只读。面板交互在 Forge.App,超出范围,未验证。

## 已实现但缺测试清单

| 项 | 代码 | 说明 |
|---|---|---|
| F-CORE-B-48 synchronous=NORMAL | Storage/StorageDb.cs:73-77 | 无测试断言该 pragma。低危(体验数据语义)。 |

其余均有直接/间接测试。另:29(FTS 混合场景)、35(消息/摘要坏时间戳)、55(非保存点 op 连坐)属“有测试但缺关键分支”,见详情。

## 程序→规格:未记录行为

1. AppendOp 每条消息刷新 last_active_at/ticks(SqliteSessionStore.cs:842-864,为空闲归档扫描)。规格只提每回合 TouchAsync;有测试(SqliteSessionActivityTests::Appending_a_message_updates_the_last_active_time)。
2. ActivateRowAsync 复活根时把 Deleted 后代改回 Archived(Lifecycle.cs:459-466)。
3. RenameBranchAsync(Lifecycle.cs:168-177,863-890):按 (project,occupancy_key) 改 branch、按 (project,oldBranch) 改 fork_of_branch。Core SPEC 未载此 API(有测试)。
4. 继承时间过滤与深度守卫:父摘要按 At ≤ child.CreatedAt 过滤(SessionHistory.cs:62-66,95-97),链上限 64 且查环(:18,52-58)。规格 71 只说“截至 inherits_up_to_seq”;有测试。
5. Todo Done→非 Done 同写清空 commit_id/commit_error(SqliteTodoStore.cs:186-196)。
6. SpillAsync 跳过 IsError 结果与 read_artifact(AgentLoop.cs:992-999);有测试(A_huge_error_result_is_never_spilled)。
7. PurgeOp 不清 automation_runs.session_id/target_session_id 引用(Lifecycle.cs:782-790 表清单无该表)。字面合规(该表以 id 为键)但留悬空引用;规格未置可否。
8. ui_state.version 由读者决定可用性(SqliteUiStateStore.cs:8)。
9. StoragePaths.SafeSegment 标识符白名单(ASCII 字母数字 -_. ,拒尾点/空格,StoragePaths.cs:6-17)。
10. v5 迁移对 GetFullPath 拒绝的路径回退原始文本作键(StorageSchema.cs:600-610)。
11. 摘要批预算额外扣 PromptFrameChars=128(Compaction.cs:199,626),规格扣除清单未含。
12. ToolCallCancellations.Begin 重复键 TryAdd:新 CTS 仍建但不可单独停止(ToolCallCancellations.cs:26-31);规格只描述 End 的重复 id 语义。
13. LoggingSessionStore 先 SQLite 提交后 JSONL 镜像,失败抬 LogFailed 不影响提交(LoggingSessionStore.cs:41-61)。含义:JSONL 永不领先 SQLite,“崩溃恢复”仅对库文件丢失后重建有效(ReplayAsync 按 store 最高 seq 补尾)。
14. HandoverAsync 拒自我交接(Lifecycle.cs:68-71);Heal 只处理有 project 的会话(JOIN projects,:131-137),无项目会话永不 heal。
15. IsContextOverflow 沿 InnerException 链查找(ProviderErrors.cs:49)。

## 规格质量问题

1. **过时+自相矛盾(最严重)**:SPEC.md:71 末句(MessageCacheTtl/MessageTtl/“Anthropic 忽略”)与 :148 未决(“1h 未接线”)均被实现推翻,且与 :123 的 ResolveCacheTtl/CacheHint.Ttl 叙述直接冲突——同一文件对同一机制三种互斥状态。建议删 71 末句、删 148 条目。
2. **同文件两段互斥**:SPEC.md:89 粗体段(“只发拉平 Content、发图是后续工作 W4、没有工具产图”)与 :97-105(已实现的发图协议与开关)矛盾;Blocks 契约也在 89 与 97 重复描述,一份过时一份现行。建议 89 只留持久化规则。
3. **全称量词过宽**:SPEC.md:57 “所有写入走单写线程批量提交”——仅 SqliteSessionStore 有写队列,工作台各 store 每写一个 IMMEDIATE(:65 自述,SqliteWriter.cs:45-67);:69 “每个操作包在 SAVEPOINT”——实现只包可能冲突的操作(见 55)。建议 57 限定为会话消息/摘要写入,69 收窄或补齐。
4. 交叉引用不准:SPEC.md:59 “5–10 见下两段”——下两段(67、69)只覆盖 v5–v9,v10 在 133 行自动化段。
5. 模糊:SPEC.md:69 “所有以 session_id 为键的行”未定义 automation_runs 这类含 session_id 外键但以 id 为键的表(见未记录 7);:107 “绝不静默丢弃”未限定角色维度(见 114)。
6. 历史叙述残留:SPEC.md:59 以一次性 v5 验收叙事开头且称“爬到 v9”,而 Version=11(StorageSchema.cs:15);建议移入测试指引,阶梯段直列 0–11。
7. 未决 145-147、149-150 核对仍准确(矩阵 118-123);148 应移除(见问题 1)。

## 业务闭环缺口

1. **写队列致命故障不可恢复**:RunWriterAsync 致命异常后置 _writerFault 并 Complete 通道,此后所有写抛 InvalidOperationException(SqliteSessionStore.cs:466-478,575-579),只能重启。与 29、55 叠加时单条坏消息可永久打死会话存储;规格未描述该降级。建议:故障分类或至少把 AppendOp 包进保存点。
2. **跨进程同会话追加 seq 竞态**:_lastSeq 缓存进程内(SqliteSessionStore.cs:260-271,794-826);两进程对同一会话 Append 分配相同 seq→PK 冲突;AppendOp 无保存点且约束错误(19)不在重试集(仅 5/6)→整批失败。占用索引只防同工作区双活动根,不防双开同一会话。无测试。建议:seq 事务内 MAX(seq)+1 不信任跨进程缓存,或文档化单进程写约束。
3. **Purge 库外残留**:artifact 文件由 App 在 purge 后删(AppComposition.cs:760),两步间崩溃→孤儿文件,无对账;automation_runs 引用悬空(未记录 7)。建议规格明确库外边界与补偿。
4. **时间戳残留值静默漂移**(35):守卫+v3 后仍可能残留无 offset 值,消息/摘要读路径按本地时区静默解析,恢复/压缩时间线漂移且无错误——“库自己把关”差最后一环。
5. **FTS 降级写路径断裂**(29):读搜索亦查不存在表。
6. 已闭环确认:迁移多进程并发(事务内重读+子进程测试)、v5 失败重跑、Handover 任一步失败全回滚、Purge 继承拒绝、Heal 写内复查、取消后 seq 不丢不重(随机取消回归)、压缩失败联动取消零写入、单调用取消登记/注销竞态——实现与测试双重证据。