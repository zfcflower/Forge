# ExtensionHost/PluginWorker/Plugin.Sdk 审计发现

只读静态审计(未跑 build/test/git)。规格:EH=src/Forge.ExtensionHost/SPEC.md,PW=src/Forge.PluginWorker/SPEC.md,SDK=src/Forge.Plugin.Sdk/SPEC.md(L=行号)。
缩写:EH/PW/SDK/IPC=对应 src 项目目录;IT=tests/Forge.Integration.Tests,HC=tests/Forge.HostClient.Tests,ST=tests/Forge.Scripting.Tests;测试证据=类::方法。
结论:PASS=符合且有测试;PASS*=符合但无直接测试;PARTIAL=部分符合;FAIL=违背规格/缺失;UNVERIFIED=静态无法验证(macOS)。

## 功能点矩阵

### Forge.ExtensionHost

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-EH-01 | 不引用 Core/Abstractions;只编译不运行插件/脚本 | EH L15 | EH/Forge.ExtensionHost.csproj;ExtensionHost.cs:53-65 | IT/ConcurrencyAndIsolationTests::Broken_plugin_is_refused_and_leaves_the_healthy_plugin_untouched | PASS |
| F-EH-02 | CLI;Worker/Executor 定位链(path→env→同目录→兄弟输出) | EH L16 | EH/HostOptions.cs:68-148,175-246 | 各 RawHost 测试显式传 --worker;回落链无直接测试 | PASS* |
| F-EH-03 | 显式 executor 不回落;不存在→executor_missing fail-closed | EH L16 | EH/HostOptions.cs:131-140;ScriptExecutorSupervisor.cs:88-92,322-329 | IT/SingleUseExecutorHostTests::MissingExecutor_FailsClosed_WithExecutorMissing | PASS |
| F-EH-04 | --dotnet 仅启动 .dll 子进程,不传给子进程 | EH L16 | EH/WorkerProcess.cs:117-131 | 无 argv 断言;IT/HostCleanupFailureTests 间接 | PASS* |
| F-EH-05 | 审批以磁盘字节为准,每次执行重读重算 | EH L19 | EH/ExtensionHost.cs:1295-1340;PluginCatalog.cs:125-178 | IT/TamperingTests::Replaced_artifact_is_blocked_until_reapproved_and_never_runs_unapproved | PASS |
| F-EH-06 | approvals.db WAL;损坏 fail-closed 退出;Dispose 关库无 -wal/-shm | EH L19 | EH/Program.cs:60-70;ExtensionHost.cs:1820-1828 | Forge.Approval.Tests/ApprovalFailureTests(损坏/-wal/-shm,跨套件) | PASS |
| F-EH-07 | 镜像=入口+递归全部文件;v2 域/长度定帧/排序/折叠 | EH L20 | EH/DependencyHashSchema.cs:38-130;PluginCatalog.cs:575-598 | IT/MigrationHashCollisionTests::Delimiter_and_domain_spoofs_change_the_dependency_hash 等4项 | PASS |
| F-EH-08 | 派发前批准比对 plugin_not_approved;worker.load 字节由 Worker 校验 | EH L20 | EH/ExtensionHost.cs:386-401;WorkerSlot.cs:633-700;PW/PluginIntegrity.cs:28-105 | IT/VerifiedDependencyTests::Dependency_replaced_after_verification_never_runs_even_when_first_loaded_mid_call | PASS |
| F-EH-09 | 批准落盘前闸门内重算,不符 approval_mismatch 不写 | EH L21 | EH/ExtensionHost.cs:786-905 | IT/RejectedApprovalTests::Rejected_stale_approval_leaves_the_persisted_record_untouched | PASS |
| F-EH-10 | 失效 Approved→Draft;SetApproved 后再读退回(闸门内+哈希条件) | EH L21 | EH/ExtensionHost.cs:815-843,1012-1057 | IT/ExtensionHardeningTests::Approving_a_CSharp_plugin_whose_tools_are_owned_by_another_plugin_is_refused_and_leaves_no_approval | PASS |
| F-EH-11 | 每插件闸门串行审批/执行;离临界区前登记在途 | EH L22 | EH/PluginState.cs:71-150;ExtensionHost.cs:405-410 | IT/ApprovalRaceTests::Execute_racing_revoke_fails_the_inflight_call_and_blocks_every_later_call | PASS |
| F-EH-12 | revoke:作废+注销+杀树+等在途归零才返回 | EH L22 | EH/ExtensionHost.cs:1059-1098,1831;PluginState.cs:189-204 | IT/ExtensionWorkflowPluginTests::Revoke_during_execution_ends_cleanly_with_no_new_runs | PARTIAL |
| F-EH-13 | 闸门按 rootRunId;独占排队;同根嵌套准入否则 plugin_busy;防饿死 | EH L25 | EH/PluginState.cs:71-150;ExtensionHost.cs:715-734 | IT/NestedRootQueueTests::Cross_root_nested_call_is_refused_while_same_chain_name_is_shared 等4项 | PASS |
| F-EH-14 | PluginState 永不丢弃;闸门内按 id 重解析;锁序刷新→插件 | EH L26 | EH/ExtensionHost.cs:1100-1199,1935-1951 | IT/EntryLifecycleTests::Execute_paused_before_its_gate_never_runs_a_plugin_that_refresh_removed 等3项 | PASS |
| F-EH-15 | Debug-only 钩子(pipe 匹配+armed+有界;Release 无类型不读 env) | EH L27 | EH/HostTestHook.cs(#if DEBUG);ExtensionHost.cs:1922-1926 | IT/ReleaseHookAbsenceTests::Release_host_contains_no_test_hook_types;E2eFixture.cs:407-417 Debug 断言 | PASS |
| F-EH-16 | 异常事件→杀树+重启重载+重注册,只失败在途 | EH L28 | EH/WorkerSlot.cs:436-597 | IT/CrashRecoveryTests::Worker_crash_fails_the_inflight_call_and_the_next_call_runs_on_a_restarted_worker | PASS |
| F-EH-17 | host.tool_call 中继:Worker 只发 {runId,name,args},Host 补 pluginId | EH L28 | EH/CallBinding.cs:330-440;IPC/HostMessages.cs:41-54 | IT/RunBindingTests::Cross_plugin_runId_rejected_before_resolver_and_approval 等8项 | PASS |
| F-EH-18 | EnsureLoaded 按镜像字节(非版本号)复用/换进程 | EH L28 | EH/WorkerSlot.cs:87-153,599-619 | IT/ExtensionCommitLifecycleTests::Same_version_update_serves_the_new_code | PASS |
| F-EH-19 | 失败预算4→worker_disabled;仅 refresh/成功完成清零;refresh 重注册 | EH L29 | EH/PluginState.cs:32,152-157;WorkerSlot.cs:621-631;ExtensionHost.cs:1202-1213 | IT/CrashRecoveryTests::Refresh_re_enables_a_worker_disabled_by_repeated_crashes_and_republishes_its_tools | PASS |
| F-EH-20 | 取消宽限窗2s;不合作→cancel+杀树+重启,计取消杀 | EH L30 | EH/WorkerSlot.cs:42,500-536 | IT/CancellationTests::Cancelled_non_cooperative_tool_kills_the_worker_and_the_next_call_recovers | PASS |
| F-EH-21 | Windows 私有 Job:仅 KILL_ON_JOB_CLOSE;先分配后 load;TerminateJobObject | EH L31 | EH/WorkerProcessOwnership.cs:139-215;WorkerProcess.cs:196-215 | IT/ProcessOwnershipTests::Killing_the_host_kills_the_child_the_worker_started 等6项 | PASS |
| F-EH-22 | fail-closed:Job 创建/限额/分配失败→杀+worker_start_failed;getpgid 校验;Debug 钩子 | EH L32 | EH/WorkerProcessOwnership.cs:139-243;WorkerProcess.cs:216-237 | IT/ProcessOwnershipTests::A_worker_that_cannot_be_contained_is_killed_and_the_start_fails | PASS |
| F-EH-23 | POSIX spawn+SETPGROUP+pipe2/fcntl+env 快照;收割 waitid(WNOWAIT)→kill(-pgid)→waitpid;组闭后不再发信号;1→100ms 退避 | EH L33 | EH/WorkerProcessOwnership.cs:262-435,555-590 | IT/ProcessOwnershipTests::A_worker_that_exits_after_starting_a_child_leaves_no_child_behind(Linux) | PASS |
| F-EH-24 | macOS 归属分支(pipe+fcntl/WNOWAIT 0x20/si_pid 偏移12) | EH L33 | EH/WorkerProcessOwnership.cs:84-97,444-465 | 无(macOS 未运行,规格自述未验证) | UNVERIFIED |
| F-EH-25 | 管道名按 IpcPipe.MaxNameLength 预算截断长 id | EH L33 | EH/WorkerProcess.cs:400-416 | Forge.Ipc.Tests/NamedPipeTransportTests::Max_name_length_is_the_longest_name_a_real_pipe_accepts(跨套件);截断无测试 | PASS* |
| F-EH-26 | POSIX 组非硬边界;Host 强杀无 KILL_ON_JOB_CLOSE 等价物(自述) | EH L34 | EH/WorkerProcessOwnership.cs(无等价机制) | IT/ProcessOwnershipTests::Killing_the_host… POSIX 静默跳过,与限制一致 | PASS |
| F-EH-27 | P/Invoke 隔离:唯一原生类;Worker/Executor/ScriptRuntime/主程序无 DllImport/unsafe | EH L35 | 全 src grep:仅 WorkerProcessOwnership*.cs;余命中为字符串 | 静态验证 | PASS |
| F-EH-28 | 清理顺序 stop(2s)→Job/组杀→Kill 兜底→拆管道;可验证不吞 | EH L36 | EH/WorkerProcess.cs:283-330,440-490 | IT/StopLifecycleTests::Stop_ends_host_and_worker_processes_and_releases_the_pipe | PASS |
| F-EH-29 | 内容寻址编译缓存;命中重算摘要;预编译按字节哈希 | EH L37 | EH/PluginCatalog.cs:341-409,490-560 | IT/EntryLifecycleTests::Execute_paused_before_its_gate_never_runs_the_version_a_prebuilt_switch_replaced;ST/CompilationCacheTests | PASS |
| F-EH-30 | 坏插件不拖垮启动;id/version 路径段校验 | EH L38 | EH/PluginCatalog.cs:84-115,240-258;PluginManifest.cs:453-490 | IT/ConcurrencyAndIsolationTests::Broken_plugin_present_at_startup_does_not_block_approval_of_the_healthy_one | PASS |
| F-EH-31 | context 形状:runId/绝对目录/depth/链≤8/chain_cycle/rootRunId 补全 | EH L39 | EH/CallBinding.cs:63-204 | IT/HostBindingValidationTests::Nested_call_without_rootRunId_is_invalid_context;HC/HostClientRootContextTests 4项 | PASS |
| F-EH-32 | 旧式无 context 的 plugin.execute 兼容路径(随机 runId) | EH L39 | EH/CallBinding.cs:29-35 null→invalid_context;IPC/HostMessages.cs:13-14 注释拒绝;无随机 runId 构造 | HC/RunBindingTests::Blank_runId_and_legacy_shape_rejected_before_resolver_and_approval | FAIL |
| F-EH-33 | active-run 登记/注销+生命周期取消;unbound_tool_call | EH L39 | EH/CallBinding.cs:214-328;WorkerSlot.cs:330-334,793-806 | IT/RunBindingTests::Replayed_runId_rejected_after_the_run_ended | PASS |
| F-EH-34 | $run_code 保留身份;code.execute 仅空链 | EH L40 | EH/CallBinding.cs:138-157,380-392 | IT/HostBindingValidationTests::Reserved_identity_is_refused;IT/RunCodeScriptTests::Nested_run_code_from_script_refused | PASS |
| F-EH-35 | 跨根→立即 plugin_busy;同根兄弟排队;三路径共用准入 | EH L41 | EH/PluginState.cs:164-187;ExtensionHost.cs:498-553 | IT/RunCodeScriptTests::Simultaneous_roots_A_to_B_and_B_to_A_do_not_deadlock_and_report_busy | PASS |
| F-EH-36 | 根内 wait-for 图:真实排队边/链尾源/原子检查登记/引用计数/环拒/各路径释放 | EH L41 | EH/RootWaitGraph.cs;WorkerSlot.cs:361-434 | IT/SameRootResourceCycleTests::Same_root_cross_branch_wait_cycle_refuses_the_closing_edge_and_the_other_branch_drains 等4项 | PASS |
| F-EH-37 | 绝对 deadline:peer 无限;夹取;剩余派发;派发前耗尽→plugin_queue_timeout 不杀不计 | EH L42 | EH/CallBudget.cs;ExtensionHost.cs:355-357,672-713;WorkerSlot.cs:306-359 | IT/PluginQueueBudgetTests::Top_level_call_that_waits_past_its_budget_for_a_worker_slot_expires_with_plugin_queue_timeout 等6项 | PASS |
| F-EH-38 | 一次性执行器:唯一编译器;全新进程;load 内存交付;run 剩余;返回前杀树等待;不复用;槽上限 | EH L43 | EH/ScriptExecutorSupervisor.cs:104-305 | IT/SingleUseExecutorHostTests::TwoScriptCallsThroughHost_UseFreshExecutorProcesses_AndLeaveNoneBehind;IT/ScriptPoolTests 11项 | PASS |
| F-EH-39 | 运行等待双挂 deadline+令牌;宽限受 deadline 约束;措辞区分宽限/预算/load | EH L43-44 | EH/ScriptExecutorSupervisor.cs:232-262,420-475 | IT/ScriptPoolTests::Short_caller_budget_stops_long_script;IT/SingleUseExecutorHostTests::HangingScriptBeyondBudget_IsKilled_AndExecutorDisappears | PASS |
| F-EH-40 | 执行器启动/关闭登记:锁内计数+创建即登记+栅栏;锁外销毁;残留先杀后拆 | EH L44 | EH/ScriptExecutorSupervisor.cs:310-390,497-555 | IT/HostShutdownFenceTests::Dispose_waits_for_a_starting_script_worker_and_starts_no_new_one 等3项 | PASS |
| F-EH-41 | 执行器单飞处置;报告只记一次;live 表 finally 同锁移除;问题追加文本绝不成功 | EH L43 | EH/ScriptExecutorSupervisor.cs:280-305,745-808 | IT/SingleUseExecutorProtocolTests::KillTree_LeavesNoExecutorProcess | PASS |
| F-EH-42 | load 拒→executor_load_failed;run 拒→带原因失败;IpcRemote/ScriptArtifact 有归宿 | EH L43 | EH/ScriptExecutorSupervisor.cs:263-276,395-418 | IT/SingleUseExecutorRejectionTests 9项;SingleUseExecutorProtocolTests::Run_WithoutSuccessfulLoad_IsRejected | PASS |
| F-EH-43 | 贡献快照:双注册;flush 后记去重;代号围栏;注销墓碑;发送自带期限 | EH L45 | EH/ExtensionHost.cs:1381-1592;Publication.cs | IT/NonUiContributionTests::Approve_via_status_publishes_tools_and_all_contributions 等21项;HC/ContributionSnapshotTests 15项 | PASS |
| F-EH-44 | 半帧→连接不可用→受监督退役(不自 await peer) | EH L45 | EH/ExtensionHost.cs:1633-1685 | 无(未找到强制半帧测试) | PASS* |
| F-EH-45 | 崩溃恢复重发先确认仍在发现表且仍被允许 | EH L45 | EH/ExtensionHost.cs:1594-1609;WorkerSlot.cs:570-575 | IT/CrashRecoveryTests::Worker_crash…(同 F-EH-16);IT/ApprovalRaceTests | PASS |
| F-EH-46 | status 不执行代码;记录每次重读(data_version);manifest 变更→诊断 | EH L46 | EH/ExtensionHost.cs:597-662;Forge.Approval/ApprovalStore.cs:249-256 | IT/PublicationInvalidationTests::Manifest_edit_without_refresh_is_not_reported_as_an_approved_clean_status | PASS |
| F-EH-47 | 权限进 perm 域;RequirePermission 子集;执行/批准前重读 manifest fail-closed | EH L47 | EH/PluginManifest.cs:362-427;ExtensionHost.cs:907-1010;PW/PluginHost.cs:338-363 | IT/ManifestSnapshotConsistencyTests::Manifest_edit_without_refresh_fails_closed_then_fingerprint_changes_after_refresh | PASS |
| F-EH-48 | 旧 plugin.json 仅 LegacyPluginPackage 诊断 | EH L48 | EH/PluginCatalog.cs:262-272,642-653 | IT/NonUiContributionTests::Legacy_plugin_json_reports_incompatible_state_and_never_loads | PASS |
| F-EH-49 | 关闭栅栏:不可逆+单飞+快照→杀→关管;锁内复查;Dispose 等管道与审批库 | EH L49 | EH/ExtensionHost.cs:80-177,1729-1828;HostStopSignal.cs | IT/HostShutdownFenceTests::Concurrent_dispose_calls_join_the_same_script_worker_teardown | PASS |
| F-EH-50 | 被打断在途不作回答;栅栏后 host_stopping;host.stop 即升栅栏 | EH L49 | EH/ExtensionHost.cs:179-247,310-318 | IT/PluginQueueBudgetTests::Host_stop_drains_a_queued_sibling_without_hanging;IT/HostCleanupFailureTests(queued 无结果) | PASS |
| F-EH-51 | 启动中 Worker 创建即登记+栅栏中止+锁外销毁;退役任务有界等待30s | EH L49 | EH/WorkerSlot.cs:226-304,703-736;ExtensionHost.cs:1687-1727,1788-1818 | IT/HostCleanupFailureTests::Host_stop_exits_and_kills_a_plugin_worker_stalled_in_its_startup_load(2模式) | PASS |
| F-EH-52 | 清理失败→stderr+报告;RunAsync 非零;Dispose 抛 host_shutdown_failed | EH L49 | EH/ExtensionHost.cs:128-130,170-177;Program.cs:76-92 | 无(全 tests grep host_shutdown_failed 零命中) | PASS* |
| F-EH-53 | Workflows 已接线;问题仅 stderr | EH L50 | EH/ExtensionHost.WorkflowPlugins.cs;ExtensionHost.cs:1186-1190 | IT/ExtensionWorkflowPluginTests 12项 | PASS |
| F-EH-54 | .pending/.trash 永不加载;点开头 id/version 拒 | EH L52 | EH/PluginCatalog.cs:88-92,660-662;PluginManifest.cs:466-469 | IT/ExtensionPendingVisibilityTests::Dot_directories_are_invisible_even_with_junk_inside 等6项 | PASS |
| F-EH-55 | stage 校验:路径/白名单/256KiB/1MiB/禁自写 manifest/一.cs/workflow+tool/extension_blocked;违规不写盘 | EH L53 | EH/ExtensionStaging.cs:52-145,285-376;Authoring.cs:20-48,581-596 | IT/ExtensionStageValidationTests 18项(均断言磁盘不变) | PASS |
| F-EH-56 | stage 写 tmp→manifest→原子替换;同一发现路径不审批不注册;旧文本+线上版本 | EH L53 | EH/ExtensionStaging.cs:571-600,671-692;Authoring.cs:26-46,600-640 | IT/ExtensionPendingVisibilityTests::Draft_registers_nothing_and_spawns_no_worker_process | PASS |
| F-EH-57 | pending 按当前字节重算;坏草稿自身诊断;重启重建 | EH L54 | EH/ExtensionHost.Authoring.cs:52-124 | IT/ExtensionPendingVisibilityTests::Pending_draft_survives_host_restart_and_commits_afterwards | PASS |
| F-EH-58 | commit:双闸门;哈希比对不动;停旧+批准+发布;失败整体回滚;trash;跨版本 Revoke 旧记录 | EH L55 | EH/ExtensionHost.Authoring.cs:157-405 | IT/ExtensionCommitLifecycleTests::Failed_commit_keeps_the_old_version_runnable 等9项 | PASS |
| F-EH-59 | disable/enable/delete 占闸门;停法同 revoke;enable 不覆盖不运行;delete 封锁+删目录;各错误码 | EH L56 | EH/ExtensionHost.Authoring.cs:407-528 | IT/ExtensionBlocklistTests::Enable_with_changed_disk_content_stays_not_running 等9项 | PASS |
| F-EH-60 | 加固①:workflow tool 三条目进 tool 域;stage/pending/批准同路径 | EH L57① | EH/DependencyHashSchema.cs:92-98;PluginCatalog.cs:133-145,411-471 | IT/ExtensionHardeningTests::Changing_only_the_tool_description_after_approval_invalidates_execution | PASS |
| F-EH-61 | 加固②:工具名冲突 stage 诊断/commit 动盘前拒/C# 发布前拒+approve 收回/refresh InvalidPackage | EH L57② | EH/Authoring.cs:40-45,243-248,643-675;ExtensionHost.cs:1259-1268 | IT/ExtensionHardeningTests::Tool_names_of_a_CSharp_plugin_owned_by_another_plugin_are_refused_at_commit_before_registration 等4项 | PASS |
| F-EH-62 | 加固③:保留设备名/尾点/尾空格拒 | EH L57③ | EH/PluginManifest.cs:478-513 | IT/ExtensionHardeningTests::Reserved_device_names_and_trailing_dots_are_refused_and_write_nothing | PASS |
| F-EH-63 | 加固④:VerifyLayout 白名单/无链接/无空目录/workflow 无 bin/manifest 字节一致 | EH L57④ | EH/ExtensionStaging.cs:155-257;Authoring.cs:88-101,203-209 | IT/ExtensionHardeningTests::Extra_files_or_directories_planted_in_pending_are_refused_at_commit_and_shown_as_diagnostics | PASS |
| F-EH-64 | L0 包:kind/workflow/tool 规则;不编译;sourceHash 归一;文件域仅 workflow.json;forge.workflows/1 | EH L58 | EH/PluginCatalog.cs:411-483 | IT/ExtensionWorkflowPluginTests::Edited_workflow_json_is_fail_closed_for_execute_and_approve | PASS |
| F-EH-65 | 工作流执行:闸门+审批+在途同插件;Host 内引擎;剩余预算;同 run 绑定;结果文本;不计预算 | EH L58 | EH/ExtensionHost.WorkflowPlugins.cs:27-110,152-232 | IT/ExtensionWorkflowPluginTests::Failing_step_reports_step_and_reason_and_skips_dependents;::Host_tool_call_carries_run_plugin_name_and_args | PASS |
| F-EH-66 | terminal.* 单源 TerminalMessages;不进 SDK 不抬版本 | EH L62 | IPC/TerminalMessages.cs | HC/Terminal 6文件57个 Fact/Theory;IT/Terminal/TerminalWire.cs | PASS |
| F-EH-67 | 校验:目录/列1-1000/行1-500/32上限;错误码;未知 id 语义 | EH L63 | EH/TerminalManager.cs:79-215 | IT/Terminal/TerminalLimitTests::Thirty_two_terminals_start_the_thirty_third_is_limited_and_a_slot_is_reusable;TerminalIdSemanticsTests | PASS |
| F-EH-68 | 1MiB 窗口满停读不丢;64KiB 帧;Sequence 连续;ack 恢复 | EH L64 | EH/TerminalSession.cs:45-48,218-280 | IT/Terminal/TerminalFlowControlTests::Unacked_window_stops_delivery_and_ack_resumes(硬断言) | PASS |
| F-EH-69 | 输入队列1024块;满/关闭整块丢弃不阻塞 | EH L64 | EH/TerminalSession.cs:66-74,148 | 无(Host 侧丢弃未测;HC/TerminalInputTests 只测客户端分片) | PASS* |
| F-EH-70 | 退出收口:自然30s/stop 5s 排空;exit 最后且先于 stop 应答;128+信号;杀树失败计报告 | EH L65-68 | EH/TerminalSession.cs:296-420;Pty.cs:979-984 | IT/Terminal/TerminalNaturalExitDrainTests 3项;TerminalStopTests::Stop_kills_the_tree_reports_exit_before_answering_and_is_idempotent | PARTIAL |
| F-EH-71 | Windows PTY 序列:Pipe×2→ConPTY→suspended CreateProcess→Job→Resume;USESTDHANDLES;Resize;前台>1 | EH L69 | EH/WorkerProcessOwnership.Pty.cs:244-475 | IT/Terminal/TerminalForegroundTests::Foreground_reports_idle_busy_and_unknown;TerminalStartTests | PASS |
| F-EH-72 | POSIX PTY:openpt/grantpt/unlockpt/TIOCSWINSZ/SETSID/会话收割(/proc stat)/tcgetpgrp | EH L70 | EH/WorkerProcessOwnership.Pty.cs:593-760,781-1050 | IT/Terminal/TerminalStopTests::Stop_kills_a_job_control_background_job_with_its_own_process_group(Linux) | PASS |
| F-EH-73 | macOS PTY 分支(O_CLOEXEC/SETSID 0x0400/TIOCSWINSZ Darwin/仅组信号) | EH L70 | EH/WorkerProcessOwnership.Pty.cs:77-87,761-762,831-834 | 无(macOS 未运行,规格自述未验证) | UNVERIFIED |
| F-EH-74 | PTY 原生代码同一 partial 类 | EH L71 | EH/WorkerProcessOwnership.Pty.cs:28 | 静态验证 | PASS |
| F-EH-75 | 默认 shell 链+跳过别名;$SHELL 须存在否则 /bin/sh -l;env 三变量+覆盖;显式照用 | EH L72-73 | EH/TerminalManager.cs:300-460 | IT/Terminal/TerminalDefaultShellTests::Default_shell_skips_a_store_alias_on_the_path_and_falls_back_to_a_real_shell | PASS |
| F-EH-76 | 验证约定:同根扇出全量/跨根双门闩/短预算隔离 | EH L77 | IT/SameRootResourceCycleTests(fanout-five);NestedRootQueueTests.cs:184-185;ShortScriptBudgetCollection.cs | 同左 | PASS |
| F-EH-77 | 验证约定:cycle 快拒/扇出排队/queue_timeout 持有者无损/**清理失败非零或抛错** | EH L79 | IT/SameRootResourceCycleTests;PluginQueueBudgetTests | 前三项有;清理失败→非零/抛错**零测试** | FAIL |
| F-EH-78 | 验证约定:authoring 清单(含 $input null 不调工具) | EH L79 | IT/Extension*Tests 各套件;Forge.Workflows.Tests/InputBindingTests.cs:222(null,跨套件) | 同左 | PASS |
| F-EH-79 | 已知缺陷核对:同根等待环契约已实现 | EH L83 | EH/RootWaitGraph.cs;WorkerSlot.cs:361-434 | IT/SameRootResourceCycleTests::Cancelled_root_with_queued_siblings_leaves_no_wait_edge_for_an_equivalent_root | PASS |
| F-EH-80 | 已知缺陷核对:构造性隔离;RequiresTermination≠健康;拒迟到绑定 | EH L85 | EH/ScriptExecutorSupervisor.cs:280-305;CallBinding.cs:280-328 | IT/SingleUseExecutorIsolationTests::GhostTask_DiesWithTheProcessTree_AndNeverWritesItsMarker | PASS |
| F-EH-81 | 已知缺陷核对:沙箱/限额/预启动池确未做 | EH L85 | EH 内无相应代码(静态确认) | 不适用(自述未做) | PASS |
| F-EH-82 | 已知缺陷核对:POSIX 执行器同归属;禁子进程(树=单进程) | EH L87 | EH/ScriptExecutorSupervisor.cs:331-352 | IT/SingleUseExecutorProtocolTests::PluginAndWorkerOps_AreRefused | PASS |

### Forge.PluginWorker

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-PW-01 | 依赖边界;不做权限检查,回调走同一管道 | PW L16 | PW csproj;HostCallFacade.cs | IT/HostToolCallTests::Denied_approval_yields_an_error_result_and_the_host_tool_does_not_run | PASS |
| F-PW-02 | 协议六 op;worker.run_code 已移除 | PW L17 | PW/WorkerSession.cs:51-64(无 run_code→unsupported_operation) | 无直接测试;IT/NonUiContributionTests 覆盖各 op | PASS* |
| F-PW-03 | host.tool_call 只发 {runId,name,args} | PW L18 | PW/HostCallFacade.cs:28-29;IPC/HostToolCalls.cs | HC/HostToolCallRelayTests::The_recorded_originating_context_wins_and_forged_host_fields_are_ignored | PASS |
| F-PW-04 | context 只校验不推算根;空链补根;嵌套缺根拒 | PW L19 | PW/WorkerCallContext.cs:20-66 | 无(三套件无直接测试) | PASS* |
| F-PW-05 | 无 --script-only;Scripting 仅为加载上下文 | PW L20 | PW/WorkerOptions.cs;PluginIntegrity.cs:2 | 静态验证 | PASS |
| F-PW-06 | 重载先建后换,失败不影响在跑插件 | PW L24 | PW/PluginHost.cs:34-62,448-456 | 无(Host 以杀+重启代替热重载) | PASS* |
| F-PW-07 | 加载前校验:读一次/哈希比对/未声明拒/越界拒/缺字段 load_failed | PW L25 | PW/PluginIntegrity.cs:28-105,152-216 | IT/NativeBinaryTests::A_declared_native_file_with_a_non_dll_name_stops_the_load;IT/TamperingTests | PASS |
| F-PW-08 | 已验证字节装载;本地库双关口;重复程序集名拒 | PW L26 | PW/PluginIntegrity.cs:96-145;Forge.Scripting/PluginLoadContext | ST/VerifiedNativeLibraryTests::Verified_mode_refuses_a_native_library_while_the_file_is_present_replaced_and_deleted 等4项;ST/VerifiedDependencyImageTests 6项 | PASS |
| F-PW-09 | 每执行一个不可变 HostCallFacade | PW L27 | PW/WorkerSession.cs:148-154,199,241 | IT/ConcurrencyAndIsolationTests::Sixteen_parallel_calls_across_two_plugins_correlate_to_their_own_results | PASS |
| F-PW-10 | 注册记录器全校验(非空/重复/枚举/run_code/Agent/Workflow/元数据命令拒/面板仅声明) | PW L28 | PW/LoadedPlugin.cs:107-419 | IT/NonUiContributionTests::Plain_command_declaration_without_body_rejected 等8项 | PASS |
| F-PW-11 | RequirePermission manifest 子集 | PW L29 | PW/PluginHost.cs:338-363 | IT/NonUiContributionTests::RequirePermission_beyond_manifest_rejects_load | PASS |
| F-PW-12 | load 返回快照;unload 全丢弃不可再调用 | PW L30 | PW/LoadedPlugin.cs:65-101;PluginHost.cs:65-79 | 无 worker 级测试;ST/PluginLoadContextTests::Load_after_unload_is_rejected(上下文级) | PASS* |
| F-PW-13 | 协作式超时预算;预算用完返回错误结果 | PW L31 | PW/PluginHost.cs:252-275;WorkerSession.cs:225-273 | 无;**prompt 路径违约**(见详情) | PARTIAL |
| F-PW-14 | CLI:--pipe/--max-concurrency/--dotnet 忽略/未知退出2 | PW L32 | PW/WorkerOptions.cs:38-110;Program.cs:11-16 | 无 | PASS* |
| F-PW-15 | 并发默认8;工具异常→错误结果;取消不回复 | PW L33 | PW/WorkerSession.cs:24,166-176;WorkerOptions.cs:15 | IT/ToolFailureTests::Throwing_tool_returns_error_result_and_the_worker_keeps_serving | PASS |
| F-PW-16 | 入口类型唯一/Info.Id 匹配/零多匹配 load_failed | PW L34 | PW/PluginHost.cs:369-446 | 无(多入口匹配未测) | PASS* |
| F-PW-17 | 先订阅再连管道 | PW L35 | PW/WorkerServer.cs:26-28 | 静态+IT/CrashRecoveryTests(连接即 load) | PASS |
| F-PW-18 | 无 DllImport/Marshal/unsafe;不自管进程组 | PW L36 | 全 PW grep 零命中 | 静态验证 | PASS |
| F-PW-19 | 退出码 0/1/2 | PW L37 | PW/Program.cs:6-27;WorkerSession.cs:34-44,333-337 | IT/StopLifecycleTests(Host 侧);worker 自身无直接断言 | PASS* |

### Forge.Plugin.Sdk

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-SDK-01 | 版本 3.0.0;不依赖 Core/Abstractions | SDK L9,L24 | SDK csproj(Version 3.0.0) | 静态验证 | PASS |
| F-SDK-02 | BCL+MessagePack;源生成;AOT;无反射序列化 | SDK L15,L22 | SDK csproj;Forge.Ipc/IpcMessages.cs:82(DefaultAot) | IT/SingleUseExecutorProtocolTests::ExecutorDependencyManifest_DeclaresNoRoslynOrScriptingDependencies(间接) | PASS |
| F-SDK-03 | 类型表面齐全 | SDK L16 | SDK/ExtensionExecution.cs;Declarations.cs;Tools.cs;Plugin.cs | IT/Wire.cs 及各协议测试 | PASS |
| F-SDK-04 | op 名+形状(含 authoring/生命周期);executor.* 不在 SDK | SDK L17 | SDK/ExtensionProtocol.cs;Forge.ScriptRuntime/ScriptExecutorProtocol.cs | HC/ExtensionAuthoringPayloadTests 9项 | PASS |
| F-SDK-05 | 不含 Roslyn/Ipc 实现/P-Invoke/Avalonia | SDK L18 | SDK csproj+grep 零命中 | 静态验证 | PASS |
| F-SDK-06 | 整数 Key 只追加;枚举整数+接收端校验 | SDK L22 | SDK 各 DTO Key 连续;PW/LoadedPlugin.cs Enum.IsDefined | HC/ContributionSnapshotTests::Unknown_agent_thinking_is_dropped_with_a_diagnostic_not_coerced(Key 历史无 git 佐证,静态未见改号) | PASS |
| F-SDK-07 | Authoring DTO;UiSchema.DefinitionJson;StagedExtension.Tool 入指纹 | SDK L23 | SDK/ExtensionAuthoring.cs;Declarations.cs:21 | HC/ExtensionAuthoringPayloadTests::Stage_sends_files_dictionary_permissions_dependencies_and_tool | PASS |
| F-SDK-08 | 契约 3.0.0 使旧审批失效;旧包不支持 | SDK L24 | EH/PluginCatalog.cs:305;ExtensionHost.cs:1316-1330 | IT/NonUiContributionTests::Legacy_plugin_json_reports_incompatible_state_and_never_loads | PASS |

**统计:109 项 = PASS 89,PASS* 13,PARTIAL 3(F-EH-12/70、F-PW-13),FAIL 2(F-EH-32/77),UNVERIFIED 2(F-EH-24/73)。**

## 非 PASS 项详情

### F-EH-32(FAIL)旧式 plugin.execute 兼容路径不存在
规格 EH L39 称保留“旧式只有顶层字段的 plugin.execute,Host 用随机 runId 现场构造 context”。实现:`CallBinding.Require`(EH/CallBinding.cs:29-35)对 null context 一律 `invalid_context`,`ExecuteAsync`(ExtensionHost.cs:350)直接调用;`PluginExecuteRequest` 已无旧顶层字段(IPC/HostMessages.cs:15-21),注释 L13-14 明确拒绝无 Context 请求;全 EH 无随机 runId 构造(grep 证实)。HC/RunBindingTests::Blank_runId_and_legacy_shape_rejected… 表明客户端也拒绝旧形状。判断:兼容路径已端到端移除,规格过时;实现方向 fail-closed 更严格,但与规格文本直接矛盾。**处理:规格修订**(删兼容句,改“缺 context 一律 invalid_context”),无需改代码。

### F-EH-12(PARTIAL)revoke“等在途归零”是有界静默等待;工作流在途不被终止
规格 EH L22:revoke 杀树“以此终止在途调用”、等在途归零才返回。实现:`WaitForIdleAsync(RevokeDrainWindow=10s)`(ExtensionHost.cs:1093,1831;PluginState.cs:189-204)用 `Task.WhenAny(idle, Delay(bound))`,**超时静默返回**,无诊断无错误。worker 插件因杀树+`Runs.EndAll()` 通常迅速归零;但**工作流包无 Worker 可杀**,`WorkflowCall` 生命周期只链调用方/预算(WorkflowPlugins.cs:88-104),revoke 不取消它——长预算工作流可在 revoke 返回后继续逐步调用 host.tool_call(主程序逐步审批仍在,但“终止在途”不成立)。测试 Revoke_during_execution… 主动释放门闩,只覆盖快速完成情形。闭环缺环:revoke 返回≠在途清零;10s 上限未入规格。**处理:代码+规格**——revoke/disable 取消该插件在途 workflow run 生命周期,或规格明示有界 drain;超时记 stderr。

### F-EH-70(PARTIAL)终端杀树失败上报条件过窄
规格 EH L68:“杀树失败计入 HostCleanupReport”。实现仅当 `killProblem is not null && exitCode < 0` 才记录(TerminalSession.cs:371-376)。POSIX 上 shell 退出码可观测(≥0)但 `SweepSession` 报告会话成员残留/kill 失败(Pty.cs:819-871)时**不计报告**,Host 可能退出码 0 而留有存活后代;与 Worker 侧“以观测为准”不一致。无“shell 已退、后代残留”的上报测试。**处理:代码修复**(killProblem 非空即记录)+补测试。

### F-EH-77(FAIL)验证约定要求的“清理失败→非零/抛错”零测试
规格 EH L79 明令:“关闭清理失败须体现为非零退出码或 DisposeAsync 抛错,而不是成功”。实现存在(ExtensionHost.cs:128-130,170-177;Program.cs:80-92),但全 tests grep `host_shutdown_failed` 零命中,无测试强制 Kill 失败/进程残留/退役窗口耗尽;HostCleanupFailureTests 只验证成功清理(stall 启动被杀、退出码 0)。核心“可验证清理”承诺的失败分支无回归保护。**处理:补测试**(不可杀假进程/注入 Kill 失败/缩短退役窗口,断言非零退出与 host_shutdown_failed)。

### F-PW-13(PARTIAL)worker.prompt 运行时失败/预算耗尽违背结果契约
规格 PW L17:prompt 返回 PromptOutput,IpcError 仅限参数畸形/不支持/加载失败。实现:`HandlePromptAsync`(WorkerSession.cs:225-273)只捕获 OCE 与 PluginExecutionException(→`prompt_failed`),**无通用 Exception 捕获**;`EvaluatePromptAsync`(PluginHost.cs:205-250)不捕获 BudgetExceededException。贡献者抛异常或预算耗尽→逃逸到 IpcPeer→`handler_error`(IpcPeerImpl.cs:423-426)。tool/command 均有通用捕获返回错误结果(WorkerSession.cs:166-176,213-217),prompt 独缺。连带:Host `WorkerSlot.DispatchAsync` 不捕获 IpcRemoteException(WorkerSlot.cs:477-489),错误直达主程序为 handler_error,**不 RecoverAsync、不计失败预算**——违背 EH L45“提示词超时/崩溃走同一异常事件与失败预算”,反复超时的提示词贡献者永不停用 worker。三套件无相关测试(grep prompt_failed/handler_error/BudgetExceeded 零命中)。**处理:代码修复**(补通用捕获→PromptOutput 错误;Host 对 IpcRemoteException 计异常事件)+补测试。

### F-EH-24 / F-EH-73(UNVERIFIED)macOS 分支
规格 EH L33/L70 自述完全未验证。代码分岔存在且标 CI-to-validate:WNOWAIT 0x20、si_pid 偏移 12(WorkerProcessOwnership.cs:84-97)、pipe+fcntl(L444-465)、O_CLOEXEC 0x0100_0000、SETSID 0x0400、TIOCSWINSZ Darwin(Pty.cs:77-87)。静态无法确认常量/偏移正确性,按规则标 UNVERIFIED,不得声称 macOS 已支持。**处理:macOS CI 跑 ProcessOwnershipTests 与 Terminal 套件。**

## 已实现但缺测试清单(PASS*)

- F-EH-02 发现链回落(env→同目录→兄弟输出);显式 --worker 不存在→worker_missing。
- F-EH-04 子进程 argv 不含 --dotnet;.dll 命令行=`<runtime> <dll> --pipe`。
- F-EH-25 长插件 id 的 BuildPipeName UTF-8 截断。
- F-EH-44 半帧写入→connectionUnsafe→受监督退役、不再发帧。
- F-EH-52 清理失败→非零退出/host_shutdown_failed(同 F-EH-77,最高优先)。
- F-EH-69 Host 输入队列满/关闭整块丢弃、不阻塞分发。
- F-PW-02 worker.run_code→unsupported_operation;F-PW-04 WorkerCallContext 校验/补根;F-PW-06 双 load 先建后换;F-PW-12 unload 后不可调用;F-PW-14 CLI/退出码2;F-PW-16 多入口 Info.Id 匹配;F-PW-19 worker 退出码直接断言。

已测功能内的未测子用例:F-EH-39 load 阶段预算到期专门措辞;F-EH-41 清理报告只记一次;F-EH-61 refresh 重放工具名冲突→InvalidPackage;F-EH-63 VerifyLayout 符号链接拒绝;F-EH-43 装载后取消的发布重发。

## 程序→规格:未记录行为

1. Host 忽略未知 CLI 开关(HostOptions.cs:113-115),与 Worker“未知退出2”不对称。
2. Host 默认值未入规格:tool-timeout 120s、连接 30s、--concurrency 默认4(≤64)、script-workers 1-16(HostOptions.cs:20-27,283-313);仅 Program.cs usage 有。
3. Host 从不向 Worker 传 --max-concurrency(WorkerSlot.cs:706-710 arguments 空):Worker 默认8不生效,有效并发=Host 信号量(默认4)。
4. Host 从不发送 worker.unload(EH 无引用),规格 EH L42 却为其规定预算;生命周期一律杀进程。
5. Worker 额外 IpcError 类别 prompt_failed/unload_failed/handler_error(WorkerSession.cs:262,296;IpcPeerImpl.cs:425)超出 PW L17 清单。
6. ManifestChangedOnDisk 还比 kind/workflowFile/tool(ExtensionHost.cs:914-919),规格 L47 只列三项(代码更严,fail-closed 方向)。
7. 脚本预算 ≤0→默认60s(ScriptExecutorSupervisor.cs:36,625-629);命令/提示词缺省→--tool-timeout 回落(CallBudget.cs:24-30);规格“夹到1-600000”未定义缺省。
8. Host 不校验 terminal.input 块大小(TerminalManager.cs:222-233):≤64KiB 仅客户端保证,“队列约64MiB”对故障客户端不成立。
9. 终端未记录常量:发送期限15s、关闭终端总窗口20s、PTY 退出等待5s、会话清扫2s(TerminalSession.cs:51-60;TerminalManager.cs:34;Pty.cs:35-39)。
10. revoke drain 上限10s 且静默(ExtensionHost.cs:1831;PluginState.cs:203)——见 F-EH-12。
11. 显式 shell 须存在、裸名走 PATH 解析(TerminalManager.cs:349-368),规格说“原样使用”。
12. 发现期额外规则:id≠目录名→IdMismatch 但仍发现;重复 id 忽略后到目录(PluginCatalog.cs:276-295);权限 kind 名大小写不敏感(PluginManifest.cs:399)。
13. Worker 把 IOException/OCE 断连视为干净关闭→退出0(WorkerSession.cs:333-337),PW L37“1 连接失败”边界未细化。
14. duplicate_run_id 错误码(CallBinding.cs:300-305)规格未列(IT 已断言)。
15. Host 响应 SIGTERM/CancelKeyPress 优雅取消(Program.cs:44-49,96-105),规格未述。

## 规格质量问题

1. **【严重】EH L39 过时**:旧式 plugin.execute 兼容路径与实现/协议注释/客户端测试直接矛盾(F-EH-32)。
2. **【严重】EH L22 与 L58 矛盾/缺口**:revoke 承诺“终止在途、等归零”,工作流包无 Worker 可杀,规格未定义其在途终止方式;实现 10s 静默有界等待(F-EH-12)。
3. **【严重】PW L17 契约不完整/被违背**:IpcError 三类清单与实现不符,prompt 运行时失败不落 PromptOutput;EH L45“提示词走同一异常事件与失败预算”未兑现(F-PW-13)。
4. **历史叙述混入设计规格**:EH L88-90 work78 分段测量(134–137ms/129–131ms)与 L33 内嵌 WSL2 实测流水账(“0 warning/0 error、6/6”、StopLifecycleTests 修复史)是一次性测量/历史,不是可复核契约,应移入变更记录;L33 单条 1200+ 字混杂四主题,建议拆条。
5. **模糊**:EH L42“夹到1–600000”未定义 ≤0/缺省(实现60s);EH L28“超时一律计异常事件”未区分 Host deadline 杀树(计数)与 Worker 协作预算错误结果(实现按成功派发清零,WorkerSlot.cs:345);EH L64“每块≤64KiB”未指明强制端。
6. **跨规格重复**:一次性执行器细节(EH L43-44)与 forge-architecture/forge-script-runtime 重复;POSIX 归属在 EH L33-34 与 PW L36 近逐句重复;EH L87 与 L33 重复。建议单一所有者+引用。
7. **边界清单遗漏/状态**:EH L15 未列 Forge.Ipc.Protocol(csproj 实际引用);EH/PW frontmatter status=draft 与已落地实现不符(SDK 为 active)。
8. **EH L42 为从不发送的 worker.unload 规定预算**,应删除或注明“协议保留、Host 不使用”。

## 业务闭环缺口

1. **撤销/禁用 vs 在途工作流**(F-EH-12):revoke 返回后长预算工作流仍可逐步调用主程序工具,“撤销即止”不成立;主程序逐步审批是最后防线。建议 revoke/disable/commit-stop 取消该插件 workflowRuns 在途生命周期。
2. **清理失败可观测性无回归保护**(F-EH-52/77):“无法验证即报告、非零/抛错”是进程归属体系的最终承诺,失败分支零测试,规格验证约定明令此项,最高优先补测。
3. **提示词失败不进失败预算**(F-PW-13):不合作/反复超时的提示词贡献者不触发杀树与 worker_disabled,与工具/命令治理不对称;主程序收到未分类 handler_error。
4. **终端后代残留可能不上报**(F-EH-70):POSIX 清扫失败但 shell 退出码可观测时 Host 以“干净”结束,与 Worker“以观测为准”规则不一致。
5. **POSIX Host 强杀残留后代**(EH L34 自述):Worker 凭管道断开自退出,后代不清理;测试在 POSIX 静默跳过。属已声明平台限制,实现一致,不另计缺陷,运维文档应承接。
6. **正面确认(闭环成立)**:审批失效→重启恢复(跨版本 commit 旧记录 Revoke、delete 封锁跨重启、SyncRecord 保持 Disabled/Uninstalled、外部撤销 data_version 即时可见:ExtensionCommitLifecycleTests::Restart_after_cross_version_update_does_not_restore_the_old_approval、ExtensionBlocklistTests::Delete_blocks_restaging_and_survives_restart);Worker 崩溃→在途失败→重启→工具重注册(CrashRecoveryTests);进程树清理竞态与 pid 复用(收割先扫组后释放、组闭后不再发信号:ProcessOwnershipTests);终端窗口满停读不丢(TerminalFlowControlTests 硬断言);Host 强杀 Windows Job 清空(ProcessOwnershipTests);取消不合作→整体终止(CancellationTests、SingleUseExecutorIsolationTests::GhostTask…)。
