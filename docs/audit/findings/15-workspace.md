# Forge.Workspace 审计发现

对象:`src/Forge.Workspace/SPEC.md`(68 行,全文拆解不抽样);实现核对含 `src/Forge.IO` 委托;测试证据取 `tests/Forge.Workspace.Tests`。grep 定位+精读决定性行,追踪真实调用链;已先读 test-anti-patterns SKILL。只读审计,未跑 build/test/git。路径约定:代码未注明者相对 `src/Forge.Workspace/`,测试相对 `tests/Forge.Workspace.Tests/`。口径:PASS=符合且测试充分;PASS*=实现存在无(直接)测试;PARTIAL=实现或证据部分满足;SPEC_GAP=代码有规格无;UNVERIFIED=需真实远端/构建。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-WS-01 | 文件写入限工作区内;写前解析符号链接/junction,越界拒绝 | SPEC:15 | Git/WorkspacePaths.cs:52-88;Specs/SpecGraph.cs:202,273,332;../Forge.IO/Paths/WorkspacePath.cs:98-100 | Specs/SpecGraphTests.cs::Create_refuses_paths_that_leave_the_workspace、Create_refuses_absolute_path_outside_the_workspace;Specs/SpecToolsLinkSafetyTests.cs::Create_refuses_a_path_that_runs_through_a_directory_link_pointing_outside 等 4 项 | PARTIAL |
| F-WS-02 | 7 个规格工具经 Abstractions 暴露,4 读 3 写 | SPEC:16 | Specs/SpecTools.cs:131,172,195,251(ReadOnly),280,315,342(Write) | Specs/SpecToolsTests.cs::Exposes_seven_tools_in_fixed_order_with_expected_names、Read_tools_are_read_only_and_write_tools_are_writes | PASS |
| F-WS-03 | 6 个 git 工具按名固定副作用;策略/超时仅来自设置;经 AgentRuntimeServices.Git 注入 | SPEC:17 | Git/GitTools.cs:63-87;Git/GitFetchTool.cs:13(schema 仅 remote);Git/GitStatusTool.cs:15 等;../Forge.App/Agents/SessionRuntime.cs:25,1146 | Git/GitToolsTests.cs::Definitions_expose_all_six_tools_with_documented_names、Side_effects_split_read_only_tools_from_write_tools;Git/PrCreateSyncSmokeTests.cs::Sync_and_create_reject_unknown_or_malformed_arguments | PASS |
| F-WS-04 | GetDiffSnapshotAsync 一次调用给 Text+Files;GetDiffAsync=.Files,解析/补未跟踪仅一份 | SPEC:17 | Git/GitCli.cs:521-535,860-880 | Git/GitCliTests.cs::Diff_lists_worktree_changes_and_appends_untracked_files_on_request;Git/GitCommitListTests.cs::GetDiff_with_full_context_and_untracked_files_are_whole_file | PASS |
| F-WS-05 | EmptyTreeSha 为 public 常量,单一定义 | SPEC:17 | Git/GitCli.Review.cs:14;消费方 ../Forge.App/Git/ReviewChangeSetSource.cs:81 | Git/GitCommitListTests.cs::GetTreeDiff_with_null_from_diffs_against_the_empty_tree | PASS |
| F-WS-06 | ReviewChangeSetSource 属 App,不在本模块 | SPEC:18 | 位于 src/Forge.App/Git/ReviewChangeSetSource.cs;Forge.Workspace 内无此文件(目录清单核实) | 无(结构断言,App 侧测试不在本范围) | PASS |
| F-WS-07 | PR 工具清单与副作用;IPrPlatform 隔离,GitHubPlatform 唯一实现 | SPEC:19 | Git/PrTools.cs:44-48;Git/PrChecksTool.cs:20、PrCiLogTool.cs:17(ReadOnly);PrCommentReplyTool.cs:25、PrCommentResolveTool.cs:20、ReviewCommentResolveTool.cs:20(Write);ScreenshotTools.cs:54(Execute);Git/PrCommentTools.cs:53-65 | Git/PrToolsSmokeTests.cs::Tools_have_stable_names_fixed_side_effects_and_point_to_pr_workflow;Git/PrCommentToolsSmokeTests.cs::Tools_are_split_by_side_effect_and_carry_the_documented_names;Git/ScreenshotToolsTests.cs::The_tool_is_one_pr_screenshots_call_with_the_execute_side_effect | SPEC_GAP |
| F-WS-08 | 无工具合并 PR/标可评审;接口默认实现直接拒绝 | SPEC:19 | Git/IPrPlatform.cs:51-73(默认 Fail);调用方仅 ../Forge.App/Git/GitSyncViewModel.cs:148-150,1000-1028(UI 命令) | Git/GitHubPlatformReadyTests.cs::A_platform_that_cannot_mark_ready_refuses_instead_of_pretending;Git/GitShipMergeTests.cs::Merge_pull_request_asks_gh_for_the_confirmed_strategy | PASS |
| F-WS-09 | 评审存应用数据目录 SQLite;MarkSentAsync 幂等不改 sent_at | SPEC:20 | Review/ReviewStore.cs:55-64,246-268(UPDATE…WHERE status=$draft) | Review/ReviewStoreTests.cs::Mark_sent_again_keeps_the_original_sent_at、Comments_persist_across_store_instances_and_database_reopen、Missing_data_directory_is_created_on_first_use | PASS |
| F-WS-10 | ArgumentList 不 shell 拼接;类型化接口拒 `-` 开头并加 --end-of-options;RunAsync 不受限 | SPEC:24 | Git/GitCli.cs:948-960(Reference),1012,408,611,684,794 | Git/GitCliPinTests.cs::Dash_leading_revision_does_not_escape_into_options;Git/GitCliTests.cs::Rev_parse_rejects_option_like_revision_before_launching_git;Git/GitCliBranchManagementTests.cs::Option_like_branch_name_is_never_executed_as_an_option | PASS |
| F-WS-11 | 所有 git 子进程移除 10 个 GIT_* 定位/发现变量,再应用显式覆盖 | SPEC:24 | Git/GitCli.cs:982-993(10 变量清单),1016-1035(先 Remove 后覆盖) | Git/GitCliEnvIsolationTests.cs::Poisoned_repository_location_variables_do_not_redirect_snapshot_or_status(真实子进程,仅毒化 5/10);Git/GhCliSmokeTests.cs::Removed_variables_include_everything_GitCli_removes(仅清单断言) | PARTIAL |
| F-WS-12 | GitBranch.Name 短名/RefName 完整引用/IsRemote;不按 `/` 猜类型 | SPEC:26 | Git/GitModels.cs:35-41;Git/GitCli.cs GetBranchesAsync(for-each-ref refname:short+refname,IsRemote 按 refs/remotes/ 前缀) | Git/GitCliTests.cs::Branch_list_marks_remote_refs_and_preserves_full_names_for_slash_branches | PASS |
| F-WS-13 | 远程/分支预期失败返回 GitRemoteResult 不抛;仅取消抛 OCE | SPEC:28 | Git/GitCli.Remote.cs:94-700 全部返回结构化;GitFailureClassifier:1149-1200 | Git/GitCliPullPushTests.cs::Push_behind_the_remote_reports_non_fast_forward_and_keeps_the_remote_tip 等 15 项;Git/GitToolsTests.cs::Cancelled_token_propagates_for_a_read_tool/write_tool | PASS |
| F-WS-14 | 推送仅 --force-with-lease,lease=推送前刚读的 ls-remote tip;"Everything up-to-date" 触发二次核对;空/过期 lease 升级非快进 | SPEC:28 | Git/GitCli.Remote.cs:354-475,805-821(LeaseHoldsAsync);调用方 Git/GitSyncTool.cs:83-98、Git/PrLifecycleSupport.cs:241-289 | Git/GitCliPullPushTests.cs::Force_with_lease_on_a_stale_read_reports_non_fast_forward 等 3 项;Git/GitCliPushPinTests.cs 3 项;"Everything up-to-date" 二次核对:无 | PARTIAL |
| F-WS-15 | merge/rebase/pull/continue 失败后查 GetConflictStateAsync 升级为冲突(报告在 stdout) | SPEC:28 | Git/GitCli.Remote.cs:869-891(ToRemoteFailureAsync),625-666(--git-path 派生,worktree 感知) | Git/GitCliConflictTests.cs::Merge_conflict_reports_conflict_and_state 等;Git/GitCliPullPushTests.cs::Pull_with_rebase_reports_conflict_and_abort_restores_head | PASS |
| F-WS-16 | `(non-fast-forward)` 与 `(fetch first)` 都识别 | SPEC:28 | Git/GitCli.Remote.cs:1118,1121 | Git/GitFailureClassifierTests.cs:88(fetch first 样本)、Classify_maps_captured_stderr_to_the_documented_kind | PASS |
| F-WS-17 | GitReviewLedger 记录最后一次完整读 diff 的 HEAD,供 PR 门禁 | SPEC:28 | Git/GitTools.cs:19-51;Git/GitDiffTool.cs:84-92(仅默认 base、无 paths、非 stat_only;记 diff 前读到的 HEAD) | Git/GitReviewLedgerTests.cs 5 项(含并发);Git/GitToolsTests.cs::Full_diff_read_records_head_in_the_ledger、Paths_filtered_or_stat_only_diff_does_not_record_head(路径/stat 不记录) | PASS |
| F-WS-18 | 字节级捕获保留 CRLF/末尾无换行;超限结构化接口抛 GitOutputTruncatedException | SPEC:30 | Git/GitCli.cs:1058-1180(PumpAsync 原始字节+UTF8 decoder),912-929(RunCheckedAsync 抛 Truncated),509-512 | Git/GitCliTests.cs::Show_preserves_crlf_line_endings、Diff_keeps_a_lone_carriage_return_inside_a_line 等;Diff/UntrackedFileDiffTests.cs::Text_without_trailing_newline_marks_last_line;GitOutputTruncatedException:零测试引用 | PARTIAL |
| F-WS-19 | Diff 解析器校验 hunk 声明行数,损坏块标记而非猜测 | SPEC:30 | Diff/UnifiedDiffParser.cs:324,333(坏头丢弃),347-353,570(IsCorrupt=行数不符) | Diff/UnifiedDiffParserTests.cs::Malformed_hunk_header_is_dropped_without_throwing、Malformed_header_without_closing_marker_yields_no_hunks;行数不符→IsCorrupt:无 | PARTIAL |
| F-WS-20 | stdout 截前缀、stderr 截末尾(64KiB);两方向逐行回调照常 | SPEC:30 | Git/GitCli.cs:19,96-100(keepTail 分流),1067 | Git/GitCliStderrCaptureTests.cs::Stderr_beyond_the_capture_cap_keeps_its_end_and_every_line_is_streamed、Stdout_keeps_its_beginning_when_the_cap_is_hit_while_stderr_keeps_its_end、A_rejecting_hook_that_prints_more_than_the_capture_cap_still_shows_its_verdict | PASS |
| F-WS-21 | Normalize=GetFullPath+去尾分隔;Equals 先规范化串后解析链接;Add/AttachWorktree 路径拼写不同仍返回真实 worktree;夹具先解析沙箱 | SPEC:32 | Git/WorkspacePaths.cs:9-18,45;../Forge.IO/Paths/WorkspacePath.cs:24-29,98-100;Git/GitCli.cs:843-858(FindWorktreeAsync 用 Equals) | Git/GitCliTests.cs::Worktree_add_finds_the_worktree_when_the_path_goes_through_a_symlink;TestSupport.cs:14-16(夹具 ResolveRealPath) | PASS |
| F-WS-22 | 分支默认 forge/<slug>;显式名不加后缀不复用;目录 slug 取最后一段,冲突加 -2;已存在抛 GitException、非法名抛 ArgumentException;失败不建任何分支/目录 | SPEC:34 | Worktrees/WorktreeManager.cs:155-187,243-330,348-356 | Worktrees/WorktreeManagerBranchNameTests.cs::Existing_branch_throws_GitException_naming_it_and_leaves_no_directory、Invalid_name_throws_ArgumentException_and_creates_nothing 等 9 项 | PASS |
| F-WS-23 | 非强制删除四守卫:未提交/未合入/游离独有提交/无基线 | SPEC:34 | Worktrees/WorktreeManager.cs:377-403,410-435,462-490 | Worktrees/WorktreeLifecycleTests.cs::CheckRemovable_* 四守卫各 1 项、Remove_throws_with_exactly_the_reasons_the_check_reports | PASS |
| F-WS-24 | 会话结束后 worktree 保留,用户手动清理 | SPEC:34 | Worktrees/WorktreeManager.cs:78-81(类注释+无任何会话结束钩子);移除仅由调用方触发(../Forge.App/Lifecycle/RemovalFlow.cs:364 起) | 无(本模块套件无会话生命周期测试;结构上无自动删除路径) | PASS* |
| F-WS-25 | Layout 默认 NestedByRepository;App 用 SiblingOfRepository(<repoParent>/<repoName>.worktrees/<slug>) | SPEC:34 | Worktrees/WorktreeManager.cs:27,617-640;App 侧 ../Forge.App/Services/WorktreeService.cs(SiblingOfRepository) | Worktrees/WorktreeManagerLayoutTests.cs 4 项;Worktrees/WorktreeManagerPinTests.cs::Default_options_keep_the_nested_repository_layout | PASS |
| F-WS-26 | startPoint 重载:本地/远程引用/任意 revision 先解析成提交再 add -b;新分支不带上游;解析不出抛 GitException | SPEC:36 | Worktrees/WorktreeManager.cs:204-232,342-345(^{commit});Git/GitCli.cs:783-805 | Worktrees/WorktreeManagerStartPointTests.cs::Start_point_at_a_remote_tracking_ref_starts_there_with_no_upstream 等 9 项(含 annotated tag、不可解析起点) | PASS |
| F-WS-27 | GetHeadAsync 四态;裸仓库=NotRepository;除取消外不抛;先 --show-toplevel 再 symbolic-ref/rev-parse | SPEC:38 | Git/GitCli.Head.cs:13-43 | Git/GitHeadTests.cs 7 项(含 Bare_repository_reports_not_a_repository_because_it_has_no_checkout、Cancelled_token_throws_operation_canceled) | PASS |
| F-WS-28 | 并发创建:进程内引用计数门(OS 比较器);跨进程冲突重读重试上限 20;从不删已存在的分支/目录/worktree | SPEC:40 | Worktrees/WorktreeManager.cs:86-133(门),84(MaxAddAttempts=20),296-330(重试+末次失败详情),363-367(IsCollision) | Worktrees/WorktreeManagerGatePinTests.cs::Eight_way_concurrent_same_title…(8 路并发)、取消不泄漏门;WorktreeManagerBranchNameTests.cs::Concurrent_creation_of_the_same_name_wins_exactly_once;跨进程冲突重试:无 | PARTIAL |
| F-WS-29 | SnapshotTreeAsync:临时索引 forge-snapshot-<pid>-<guid>.index + GIT_INDEX_FILE + GIT_OPTIONAL_LOCKS=0,finally 删除;用户索引/HEAD/refs/stash 不变;同内容同哈希;树 diff 三 API | SPEC:42 | Git/GitCli.cs:543-589,591-660 | Git/GitCliSnapshotTests.cs 10 项(索引/HEAD/refs/stash 逐字节不变、临时文件清理、中途失败清理);Git/GitTreeDiffTests.cs 5 项 | PASS |
| F-WS-30 | 规格更新原子写:UTF-8 无 BOM、沿用原行尾、同目录临时+fsync+改名;ReadOnly/目录目标立即失败原文件不变;新建 CreateNew 独占 | SPEC:44 | Specs/SpecGraph.cs:17,305-306,349-361;../Forge.IO/AtomicFile.cs:121-144,305-331 | Specs/SpecGraphTests.cs::Update_of_a_read_only_spec_fails_and_keeps_the_original_bytes、Update_keeps_crlf_line_endings_of_the_file、Create_rejects_duplicate_id_without_writing 等 | PASS |
| F-WS-31 | frontmatter 往返无损(引号/逗号/转义);不支持语法保留 RawText 原样回写 | SPEC:46 | Specs/SpecFrontmatter.cs:7-18,216-218,265-275,308,564-581 | Specs/SpecFrontmatterTests.cs 28 项(逐字节往返、含逗号 flow 列表、Unicode、多行值) | PASS |
| F-WS-32 | AOT:JsonPayload 反射无关;DiffAnchor 走 WorkspaceJsonContext 源生成;五主项目 IsAotCompatible=true、分析器 0 警告 | SPEC:48 | JsonPayload.cs:16-34(Utf8JsonWriter,不支持形状即抛);WorkspaceJsonContext.cs:11-12;Git/GhJson.cs:233-240;五个 csproj 均 IsAotCompatible=true(已核) | Diff/DiffAnchorTests.cs::Anchor_record_round_trips_through_json;"分析器 0 警告"需构建,只读审计无法证明 | PARTIAL |
| F-WS-33 | GhCli 与 GitCli 同 ArgumentList/字节捕获/超时杀树纪律;GH_* 环境钉死+清仓库定位变量;GhJsonContext 源生成 | SPEC:50 | Git/GhCli.cs:296-325,151-176,500-506;Git/GhJson.cs:233-240 | Git/GhCliSmokeTests.cs::Arguments_pass_verbatim_and_the_environment_is_pinned、Timeout_kills_the_process_and_reports_timed_out、Cancellation_throws_instead_of_returning_a_result;Git/GhCliProcessTreeTests.cs 2 项(杀子树) | PASS |
| F-WS-34 | GetAvailabilityAsync 三态(gh auth status);非 Ready 时 PR 工具明确报错,本地 git 不受影响 | SPEC:50 | Git/GitHubPlatform.cs:73-81;Git/PrCommentSupport.cs:131 | Git/GhCliSmokeTests.cs::Availability_distinguishes_ready_from_not_authenticated、Missing_executable_is_reported_as_not_installed_and_never_throws;Git/PrCreateSyncSmokeTests.cs::Create_says_so_when_the_platform_cli_is_not_usable_before_touching_anything | PASS |
| F-WS-35 | pr_create 闸门(干净树含未跟踪/fetch 后 base 祖先/确切 HEAD 自评/Testing 非空话)+闸后重读+SourceCommit 钉死推送+push 前再核 HEAD+WriteUpstreamAsync 补上游 | SPEC:52 | Git/PrCreateTool.cs:96-190;Git/PrLifecycleSupport.cs:222-299(双 HEAD 核对);Git/GitCli.Remote.cs:399-475,478-503;Git/PrTestingEvidence.cs:13-50 | Git/PrCreateSyncSmokeTests.cs::Gate_1…Gate_4 各拒绝场景、Gates_are_reported_in_order_one_two_three_four、Create_refuses_a_commit_that_lands_between_the_last_gate_and_the_push、Create_rechecks_head_after_the_gates_and_stops_when_it_moved;Git/GitCliPushPinTests.cs 3 项 | PASS |
| F-WS-36 | pr_sync 拒无 PR 分支;按历史选 rebase/merge;冲突停下;成功后推送 | SPEC:52 | Git/PrSyncTool.cs;Git/PrLifecycleSupport.cs:276-289(lease=mode!=FastForward) | Git/PrCreateSyncSmokeTests.cs::Sync_rebases…pushes_with_a_fresh_lease、Sync_merges…pushes_without_force、Sync_stops_on_conflicts…、Sync_refuses_a_branch_without_a_pull_request… 等 12 项 | PASS |
| F-WS-37 | pr_edit 读-改-核对;pr_view/pr_workflow 只读 | SPEC:52 | Git/PrEditTool.cs;Git/PrBodyEditor.cs(读、算、写前重读、写、回读核对) | Git/PrToolsSmokeTests.cs::Edit_replaces_one_section_and_verifies_by_reading_back、Edit_refuses_a_full_replacement_when_the_body_changed_meanwhile_and_shows_both、回读不符报错、View 只读 | PASS |
| F-WS-38 | Verdict 单读版与 WaitAsync 同规则;no-CI 仅连续多次成功空读;单参 Verdict streak=0 空列表永远未定 | SPEC:54 | Git/PrChecksPoller.cs:179-195,236-290,324-352 | Git/PrChecksVerdictTests.cs 16 项(空快照永不 no-CI、连续空读达阈值才判);Git/PrCheckPollerSmokeTests.cs 40+ 项同规则 | PASS |
| F-WS-39 | PrCheckText 与 BucketRank 公开给 App,判决词汇/清单渲染单份 | SPEC:54 | Git/PrCheckText.cs:12,259;App 消费 ../Forge.App/Git/PrChecksFixMessage.cs、ViewModels/ChecksPanelViewModel.cs | Git/PrChecksVerdictTests.cs::The_panel_order_puts_failures_first_and_passes_last;Git/PrCheckToolSmokeTests.cs::Snapshot_lists_every_check_with_bucket_and_link_failures_first | PASS |
| F-WS-40 | MarkPullRequestReadyAsync=gh pr ready;"already ready" 记成功(幂等);不重试不改正文不请求评审 | SPEC:54 | Git/GitHubPlatform.cs:320-345,633-647 | Git/GitHubPlatformReadyTests.cs 7 项(含 Ready_is_idempotent_when_gh_says_the_pull_request_already_is_ready) | PASS |
| F-WS-41 | 评审线程 GraphQL 全量分页(reviewThreads+comments,上限 100/50 页);超限/cursor 空/不前进拒绝残缺列表 | SPEC:56 | Git/GitHubPlatform.cs:25-30,63,69,399-405,449,549-556,585 | Git/GitHubPlatformPaginationTests.cs 6 项(cursor 不前进/空 cursor/超页数上限均拒绝残缺列表) | PASS |
| F-WS-42 | pr_comment_reply 必带 verdict;apply 提交按 PR 实际 headRefOid 核对(merge-base --is-ancestor);超时后回读比对不重发 | SPEC:56 | Git/PrCommentReplyTool.cs:37-149;Git/PrCommentCommitGate.cs:22-60 | Git/PrCommentReplySmokeTests.cs 26 项(含 The_pull_request_head_is_the_evidence_not_a_local_remote_tracking_ref、A_reply_whose_answer_was_lost_is_not_posted_twice、超时重试恰发一次) | PASS |
| F-WS-43 | pr_comment_resolve 仅限已回复 apply/push_back;verdict 后任何新评论(含同登录)阻塞;clarify 不可 resolve | SPEC:56 | Git/PrCommentResolveTool.cs:40-91 | Git/PrCommentResolveSmokeTests.cs 23 项(含 A_follow_up_comment_with_the_same_login_also_blocks_resolving、A_clarify_reply_never_unlocks_resolving、resolve 前重读线程) | PASS |
| F-WS-44 | 本地 review_comments/review_comment_resolve 同“先判断再改”;草稿(未 Sent)不可 resolve | SPEC:56 | Git/ReviewCommentsTool.cs;Git/ReviewCommentResolveTool.cs:20;Git/PrCommentTools.cs:63-65 | Git/ReviewCommentSmokeTests.cs 18 项(含 A_draft_comment_that_was_never_sent_cannot_be_resolved、Clarify_cannot_resolve_a_local_comment、Push_back_without_reasons_is_refused_and_leaves_the_comment_open) | PASS |
| F-WS-45 | 截图:.forge/screenshots.json(command/output/timeoutSeconds)或一次性命令;after 会话 worktree、before origin/<base> 临时分离 worktree;按名配对单侧保留标注;失败返回退出码+输出尾不附图;finally 移除临时 worktree | SPEC:58 | Git/ScreenshotConfig.cs;Git/ScreenshotCapture.cs:47-199(finally:197-199) | Git/ScreenshotCaptureTests.cs 25 项(配对/单侧保留、失败侧退出码+输出尾、超时杀进程清理);Git/ScreenshotConfigTests.cs 16 项 | PASS |
| F-WS-46 | 图片经孤儿提交(hash-object -w/mktree/commit-tree)push 到 refs/heads/assets/<slug>;pr_edit 读改核对更新 ## Screenshots;采集脚本若被提交过,删除提交一并推送 | SPEC:58 | Git/ScreenshotPublisher.cs:8-12,105,136;Git/GitCli.Objects.cs;Git/ScreenshotCleanup.cs:41-45,172-179 | Git/ScreenshotGitObjectsTests.cs 24 项(孤儿提交/lease 推送);Git/ScreenshotPublisherTests.cs 21 项;Git/ScreenshotToolsTests.cs 13 项 | PASS |
| F-WS-47 | PrBodyRenderer 纯函数:正文保留+## Plan;有步骤分组各一节、组内按 Order 从 1 编号;散项/悬空 GroupId 进 Other steps;空分组不出节;7 位 sha+Verification;状态标记;OneLine 压平;空标题+id 用 Group 兜底;两读 ListAsync/ListGroupsAsync | SPEC:60 | Git/PrBodyRenderer.cs:37-52,132-244;Git/PrCreateTool.cs:283-296(两读) | Git/PrBodyRendererGroupTests.cs 7 项(空分组不出节、悬空 GroupId 进 Other steps、空标题兜底);Git/PrBodyRendererSmokeTests.cs 12 项 | PASS |
| F-WS-48 | CheckRemovableAsync/CheckCommitsAsync 共用守卫;“移除目录只走回收站,不用 git worktree remove,不删分支”;回滚仅 TryDeleteCreatedBranchAsync(CreatedBranch internal、停在 BaseCommit、只 -d 不 -D、失败不抛) | SPEC:62 | Worktrees/WorktreeManager.cs:410-490,555-604;但 RemoveAsync:401→Git/GitCli.cs:815-826 用 git worktree remove;回收站在 App(../Forge.App/Lifecycle/RemovalFlow.cs:707-733);回滚消费方 ../Forge.App/Services/WorktreeService.cs:94-104 | Worktrees/WorktreeLifecycleTests.cs::Rollback_* 4 项(未移动才删/移动保留/预存分支不删/换名会话拒绝);Worktrees/WorktreeManagerTests.cs::Check_commits_from_the_repository_answers_like_the_worktree_check_when_git_cannot_run_inside_it | PARTIAL |
| F-WS-49 | 生命周期原语:MergeAsync(--no-ff --no-edit)、AbortMerge、HasMerge/RebaseInProgress(git dir 派生)、ListCommits、IsAncestor、RenameBranch、GetUpstreamRemote(离线) | SPEC:62 | Git/GitCli.Lifecycle.cs:11-72;Git/GitCli.Remote.cs:598-621,625-666 | Git/GitCliLifecycleTests.cs 17 项(--no-ff、按 worktree 读合并态、斜杠分支上游);Git/GitCliConflictTests.cs 8 项 | PASS |
| F-WS-50 | 稀疏:add --no-checkout→sparse-checkout set --cone→checkout;失败先非强制移除再回滚分支;清不掉的保留并写明(不强删);GetSparseConfigAsync/ListTopLevelDirectoriesAsync;主检出不受影响 | SPEC:62 | Worktrees/WorktreeManager.cs:505-553;Git/GitCli.Lifecycle.cs:75-140 | Worktrees/WorktreeLifecycleTests.cs::Sparse_* 5 项(主检出不受影响、非法 pattern 清理、空目录拒绝);Git/GitCliLifecycleTests.cs::SparseConfig_* 3 项 | PASS |
| F-WS-51 | ReviewStore.CopySessionAsync 含标记只换 session_id;DeleteSessionAsync 彻底删除 | SPEC:62 | Review/ReviewStore.cs:327-379 | Review/ReviewStoreSessionTests.cs 12 项(含标记复制、删除只及本会话、删源不伤副本) | PASS |
| F-WS-52 | CloneAsync:clone --progress -- url dest;url 去空白后拒 `-` 开头;dest 必须绝对、不存在或空目录否则 InvalidOperationException;父目录缺失则建;不做浅克隆/子模块/--filter | SPEC:64 | Git/GitCli.Clone.cs:243-278(Args 仅 clone --progress --,无 shallow/submodule/filter) | Git/GitCliCloneTests.cs::A_url_that_starts_with_a_dash_is_refused_before_anything_is_created、An_existing_destination_that_holds_something_is_refused_and_left_untouched 等 5 项 | PASS |
| F-WS-53 | GitCloneProgress 七阶段、阶段自身百分比、Detail 截 120;TryParse 纯函数吃 remote: 前缀/空白填充/done./Cloning into/无百分比行 | SPEC:64 | Git/GitCli.Clone.cs:13-48,56-200 | Git/GitCliCloneTests.cs::One_progress_line_is_parsed_into_one_update、A_line_longer_than_the_cap_keeps_only_the_beginning_of_its_detail、The_phases_are_declared_in_the_order_git_reports_them(含 Theory 样本) | PASS |
| F-WS-54 | CarriageReturnEndsLine:裸 CR 断行、CRLF 一个断行、跨 chunk 边界吞对;默认关;StreamLines/StreamRecords 不受影响 | SPEC:64 | Git/GitCli.cs:96-100,190,227(流传 false),1075-1090(skipLeadingLf 跨 chunk) | Git/GitCliCloneTests.cs::A_carriage_return_ends_a_line_and_a_crlf_ends_one_line_not_two、A_crlf_split_across_two_reads_is_still_one_break、A_bit_of_text_ending_in_a_bare_cr_is_reported_without_it;Git/GitCliTests.cs::Stream_lines_yields_lines_and_throws_on_failure | PASS |
| F-WS-55 | 克隆失败/超时抛 GitException(stderr 尾),取消杀树抛 OCE;两路径删本次创建的目标;已存在空目录保留但清空;DeleteTreeQuietlyAsync 清只读+5×100ms 重试 | SPEC:64 | Git/GitCli.Clone.cs:296-313,451-490,497-500(CleanupAttempts=5/CleanupPause=100ms) | Git/GitCliCloneTests.cs::A_failing_clone_removes_the_destination_it_created、A_cancelled_clone_throws_and_leaves_no_destination、预置空目录保留但清空、只读清理 4 项 | PASS |
| F-WS-56 | GIT_ALLOW_PROTOCOL=https:http:ssh:git:file 压过用户配置;ext::/fd:: 由 git 拒;异常消息/Arguments/StandardError 全经 RedactCredentials;拒 `-` 消息同样脱敏 | SPEC:64 | Git/GitCli.Clone.cs:317-345 | Git/GitCliCloneTests.cs::Every_clone_runs_with_the_transport_allowlist、A_transport_outside_the_allowlist_is_refused_by_git、异常/拒绝消息脱敏 2 项 | PASS |
| F-WS-57 | 进度回调在 stderr 读取线程;回调异常吞掉;同阶段同百分比只报一次、倒退过滤 | SPEC:64 | Git/GitCli.Clone.cs:284-290(仅 stderr 回调),CloneProgressFilter(highWater 每阶段一槽,percent<=highWater 丢弃,catch-all) | Git/GitCliCloneTests.cs::A_progress_reporter_that_throws_does_not_fail_the_clone、A_clone_reaches_the_destination…(断言各阶段百分比只前进,138-151 行) | PASS |
| F-WS-58 | 未决:索引刷新默认信任长度+mtime;同长度保时间戳修改需 force(已提供强制路径) | SPEC:68 | Specs/SpecGraph.cs:413-422(length+LastWriteTimeUtc.Ticks),45(force);Specs/SpecTools.cs:116-117(refresh 参数) | Specs/SpecGraphTests.cs::Refresh_picks_up_edited_content_incrementally、Queries_see_changes_only_after_refresh;同长度+同 mtime 的 force 边界:无 | PARTIAL |

计数:58 项 = PASS 47 + PASS* 1 + PARTIAL 9 + SPEC_GAP 1;FAIL/MISSING 0;UNVERIFIED 子声明见 §9。

## 非 PASS 项详情

### F-WS-01(PARTIAL)——“评审写入限工作区”与实现/规格自身矛盾
- 规格侧:规格文件写入守卫完整且严格——`EnsurePlainPathInside`(Git/WorkspacePaths.cs:52-88)先规范化比较,再逐段拒绝根下任何 reparse point,最后把根与目标都解析成真实路径再比一次;create/update/delete 三处都调用(Specs/SpecGraph.cs:202,273,332)。链接逃逸测试真实创建 junction/symlink 并断言外部目录零写入。
- 缺口:SPEC:15 说“所有文件写入(规格、**评审**)限工作区”,但 SPEC:20 与实现都把评审存**应用数据目录** SQLite(Review/ReviewStore.cs:55-64),且 `AddAsync` 只校验非空(ReviewStore.cs:75),workspace_path/file_path 不做归属校验。两条规格声明互斥。
- 建议处理类型:规格修订(边界句改为“规格文件写入限工作区;评审库在应用数据目录,workspace_path 仅为查询键”),或按业务意图给 ReviewStore 加归属校验。需产品决策。

### F-WS-07(SPEC_GAP)——PR 工具清单漏列 pr_comments / review_comments
- 代码实际注册 11 个 PR/评审工具:PrTools 5 个 + pr_checks/pr_ci_log + pr_comments(ReadOnly,Git/PrCommentsTool.cs:20)+ pr_comment_reply/pr_comment_resolve + review_comments(ReadOnly)/review_comment_resolve + pr_screenshots(Execute)。SPEC:19 的只读清单只有 pr_view/pr_workflow/pr_checks/pr_ci_log,漏 `pr_comments` 与 `review_comments`(后者在 SPEC:56 顺带提及,前者全文未出现,grep 证实)。
- 副作用划分本身与代码一致且有测试。建议处理类型:规格补全(边界清单)。

### F-WS-11(PARTIAL)——10 个环境变量仅 5 个有行为级测试
- 实现完整:Git/GitCli.cs:982-993 列全 10 变量,Start 先 Remove 再应用显式覆盖(1016-1035)。
- 证据缺口:GitCliEnvIsolationTests 用真实子进程只毒化 GIT_DIR/GIT_WORK_TREE/GIT_INDEX_FILE/GIT_OBJECT_DIRECTORY/GIT_COMMON_DIR 5 个;其余 5 个(ALTERNATE_OBJECT_DIRECTORIES/NAMESPACE/PREFIX/CEILING_DIRECTORIES/DISCOVERY_ACROSS_FILESYSTEM)无行为测试(GhCli 清单包含断言不证明移除生效)。
- 建议处理类型:补测试(毒化集扩到 10 个,同一子进程夹具,成本极低)。

### F-WS-14(PARTIAL)——“Everything up-to-date”二次核对:无测试,且语义窄于规格
- 实现:Git/GitCli.Remote.cs:436-446 在该短语时调 `LeaseHoldsAsync`(805-821):空 lease→false;否则比 expected 与**本地** refs/heads/<branch> 的 HEAD。
- 缺口 1(证据):全套件无一处构造 “Everything up-to-date” 场景(grep 0 命中);空 lease 升级路径也未被直接测试。
- 缺口 2(语义):规格说“空或**过期**的 lease 升级为非快进”。若读 tip 时 remote==本地 HEAD,推送期间远端**前进**(仍含本地 HEAD),git 报 up-to-date,expected==本地 HEAD 使核对通过→按规格应报非快进,实际报成功。主场景(本地落后)能正确升级,但“过期”的一个真实分支被近似实现漏掉;未重读 ls-remote 是根因。
- 建议处理类型:代码缺陷(窄竞态,无数据损失,但违反规格诚实性声明)+补测试。修复:二次核对重读 ls-remote,或在规格中把语义收窄为“lease≠本地 HEAD 时升级”。

### F-WS-18(PARTIAL)——GitOutputTruncatedException 契约零测试
- 字节保真证据充分(CRLF、行内裸 CR、末尾无换行)。但“超限抛 GitOutputTruncatedException”契约无任何测试引用该类型(grep 0 命中);RunAsync 的 IsTruncated 有测试,RunCheckedAsync/GetDiffTextAsync 抛异常路径(Git/GitCli.cs:919-929,509-512)与 GitDiffTool 转工具错误路径(Git/GitDiffTool.cs:74-81)未证明。
- 风险:回归把截断数据当完整结果返回时,现有测试不报警;下游评审包/自评闸门依赖此契约。建议处理类型:补测试(构造超 8MiB 输出或用 MaxCapturedCharacters 调小)。

### F-WS-19(PARTIAL)——hunk 行数不符→IsCorrupt 标记无测试
- 实现存在:Diff/UnifiedDiffParser.cs:570 `IsCorrupt = oldLines != OldCount || newLines != NewCount`,347-353 报告 issue。测试只覆盖坏 hunk **头**(丢弃),未覆盖“头合法但内容行数与声明不符”的损坏块标记。建议处理类型:补测试(手工构造行数不符的 hunk,断言 IsCorrupt 与 issue 文本)。

### F-WS-28(PARTIAL)——跨进程冲突重试(上限 20)无测试
- 进程内门与并发唯一性证据强(8 路并发、取消不泄漏门、同名恰一次)。但跨进程 `already exists`/`cannot lock ref`/路径已存在→重读取下一后缀、上限 20、耗尽抛带末次详情的 GitException(Worktrees/WorktreeManager.cs:296-330)无任何测试走到 IsCollision 重试分支(现有并发测试同进程同门,不会触发 git 级冲突)。
- 建议处理类型:补测试(预建同名分支/目录模拟碰撞或双进程夹具;断言重试成功与耗尽错误含末次 git 详情)。

### F-WS-32(PARTIAL)——“分析器 0 警告”不可只读证明
- 结构性声明全部核实:JsonPayload 用 Utf8JsonWriter、不支持形状即抛;WorkspaceJsonContext/GhJsonContext 源生成;五主项目 csproj 均 IsAotCompatible=true。
- “分析器 0 警告”需构建,只读审计无法证明→UNVERIFIED。建议:纳入 CI 门禁,规格引用 CI 结果而非散文断言。

### F-WS-48(PARTIAL)——“只走回收站、不用 git worktree remove”与代码矛盾
- 事实链:用户发起的移除确实走回收站(../Forge.App/Lifecycle/RemovalFlow.cs:707-733,ITrashService→prune,分支从不删)。但 `WorktreeManager.RemoveAsync`(Worktrees/WorktreeManager.cs:401)调 `git worktree remove`(Git/GitCli.cs:815-826),被两处真实使用:稀疏创建失败清理(WorktreeManager.cs:537)与 App 创建失败回滚(../Forge.App/Services/WorktreeService.cs:94)。SPEC:62 同行内既说“不用 git worktree remove”,又说稀疏失败“先非强制移除 worktree”(那正是 git worktree remove)——自相矛盾。
- 回滚规则本身实现与测试都完整(CreatedBranch 仅 add -b 成功后写、BaseCommit 未移动才删、只 -d、失败只 Trace)。
- 建议处理类型:规格修订(把“只走回收站”限定为“用户发起的会话/工作区移除(App RemovalFlow)”;写明创建失败回滚用非强制 git worktree remove)。代码行为安全,无需改动。

### F-WS-58(PARTIAL)——force 刷新的关键边界无测试
- 实现与未决项一致:ScanAsync 信任 length+LastWriteTimeUtc.Ticks(SpecGraph.cs:413-422),force 经 RefreshAsync(true)/refresh 参数可达。但“保留时间戳的同长度修改”这一未决项存在理由的场景无测试(现有 refresh 测试都改了长度或 mtime)。建议:补测试(同长度内容+回填 mtime,断言默认看不见、refresh:true 看得见)。

### §9 UNVERIFIED 集成面(真实 gh / GitHub)
以下逻辑均有 fake-gh(真实进程+脚本假二进制)或 IPrPlatform 桩测试,但**无真实 gh/GitHub 集成测试**:auth status 三态、GraphQL 分页 schema/cursor(F-WS-41)、"already ready" 文案(F-WS-40)、gh 错误分类文本(捕获样本)。gh 升级/schema 漂移时现有测试不报警。git 侧不同:推送/拉取/克隆/worktree 用**真实 git+本地裸仓库远端**(GitRemoteTestSupport.cs,隔离全局/系统配置),属真实 git 边界证据;仅 https/ssh 网络认证层未集成验证。建议:可选夜间真实 gh 冒烟,或在规格标注平台文案契约依赖 gh 版本。

## 已实现但缺测试清单

| 行为 | 代码位置 | 说明 |
|---|---|---|
| “Everything up-to-date”触发 lease 二次核对(含空 lease 升级) | Git/GitCli.Remote.cs:436-446,805-821 | 全套件无此场景(见 F-WS-14) |
| GitOutputTruncatedException 抛出与工具层转译 | Git/GitCli.cs:919-929,509-512;Git/GitDiffTool.cs:74-81 | 类型零引用(见 F-WS-18) |
| hunk 声明行数不符→IsCorrupt+issue | Diff/UnifiedDiffParser.cs:347-353,570 | 只测了坏头(见 F-WS-19) |
| 跨进程碰撞重试/20 次耗尽错误 | Worktrees/WorktreeManager.cs:296-330 | 进程内并发已测(见 F-WS-28) |
| 5 个 GIT_* 变量的移除生效 | Git/GitCli.cs:987-992 | 仅清单包含断言(见 F-WS-11) |
| 同长度+同 mtime 的 force 刷新边界 | Specs/SpecGraph.cs:422 | 见 F-WS-58 |
| 会话结束不自动移除 worktree | Worktrees/WorktreeManager.cs(无钩子) | 结构性 PASS*(F-WS-24) |
| AtomicFile 目录目标“立即失败” | ../Forge.IO/AtomicFile.cs:305-331 | 本套件未测;契约归 Forge.IO SPEC(16-io 审计范围) |

## 程序→规格:未记录行为

1. **Worktree 默认值**:MaxSlugLength=48、MaxSlugAttempts=1000(目录/分支可用性循环,与 20 次 git 碰撞重试是两层)、RootDirectory 默认 `<repo>/../.forge-worktrees`(Worktrees/WorktreeManager.cs:27-36,83)。规格只写 forge/<slug> 与布局。
2. **GitCli 默认值**:DefaultTimeout=2 分钟、MaxCapturedCharacters=8MiB(Git/GitCli.cs:44-46);GitToolSettings 默认 ff-only/2 分钟(Git/GitTools.cs:9-11)。规格只写了 stderr 64KiB。
3. **git 环境钉死**:LC_ALL/LANG/LANGUAGE=C、GIT_TERMINAL_PROMPT=0、GIT_PAGER=cat、TERM=dumb(Git/GitCli.cs:1020-1025)。分类器依赖 LC_ALL=C 英文输出(GitCli.Remote.cs:1097 注释),是分类正确性前提,规格未记。
4. **GhCli 额外环境与截断方向**:GH_SPINNER_DISABLED、GH_NO_EXTENSION_UPDATE_NOTIFIER、CLICOLOR=0、GCM_INTERACTIVE=never(Git/GhCli.cs:315-324);**stderr 保留头部**(keepTail:false,GhCli.cs:155),与 GitCli 的 stderr 保尾相反——SPEC:50“同一套字节捕获纪律”未区分(见规格质量 §4)。
5. **PrBranchPublisher reflog 守卫**:远端分支含有本地 HEAD 与 reflog(SeenTipLimit=50)从未见过的提交时拒绝覆盖(Git/PrLifecycleSupport.cs:206-213,258-263)。SPEC:52 未提此闸门(PrTools.Sync 的代码注释提了)。
6. **空 lease 语义**:ExpectedRemoteCommit="" 表示“远端分支必须尚不存在”(Git/GitCli.Remote.cs:421-423),规格未记。
7. **CommitAsync 契约**:消息走 stdin(-F -)绕过命令行长度限制;提交已存在后取消仍返回提交(Git/GitCli.cs:457-503)。规格未提。
8. **默认分支解析顺序**:origin/HEAD→main→master→trunk(Git/GitDiffTool.cs:44 经 GetDefaultBranchAsync;测试 Default_branch_falls_back_to_main_then_master_then_null)。规格未记。
9. **SpecGraph 索引限制**:超大文件不索引、扩展名必须 .md(Specs/SpecGraph.cs:826-829;测试 Oversized_spec_files_are_not_indexed、Create_refuses_non_markdown_extension)。规格未记阈值。
10. **PrBodyRenderer 替换已有 ## Plan**:summary 已含 ## Plan 时替换而非追加(Git/PrBodyRenderer.cs:51 ReplaceSection;测试 Render_replaces_a_plan_section_the_summary_already_has)。SPEC:60 只说“其后追一个”。
11. **截图配置硬限制**:timeout 默认 300s、范围 10–3600s、output 必须在仓库内、命令长度上限、每侧≤40 张、单张≤10MiB、PNG 签名校验(Git/ScreenshotConfig.cs;对应测试齐全)。SPEC:58 只列三字段名。
12. **PrChecksPoller 默认值**:NoChecksConfirmations=3、最长等待 30 分钟(Git/PrChecksPoller.cs:60;测试断言默认值)。SPEC:54 只说“连续多次”。
13. **FindWorktreeAsync 占位回退**:列表中找不到时返回空分支占位对象(Git/GitCli.cs:856)——规格强调“不返回占位”,但未说明找不到时的行为。
14. **GetHeadAsync 目录缺失**:目录不存在直接 NotRepository,不起 git(Git/GitCli.Head.cs:19-22)。规格未列此态。

## 规格质量问题

1. **SPEC:62 自相矛盾(最严重)**:同一行既声明“移除目录只走回收站,不用 git worktree remove”,又声明稀疏失败“先非强制移除 worktree”(实现即 git worktree remove,WorktreeManager.cs:401→GitCli.cs:815)。回收站流程实际属 App(RemovalFlow),写进 Workspace 规格造成所有权与事实双重失真。详见 F-WS-48。
2. **SPEC:15 与 SPEC:20 矛盾**:“所有文件写入(规格、评审)限工作区”vs“评审存应用数据目录 SQLite”。且 SPEC:19 工具清单漏 pr_comments/review_comments(F-WS-07)。边界节需重写。
3. **SPEC:52 闸门计数与推送措辞失真**:“五道闸门”中第 5 道自认“与 3 同源”,代码/工具描述/测试一律按 4 道(PrCreateTool.cs:21 "Four gates";测试 Gates_are_reported_in_order_one_two_three_four)——冗余编号徒增对齐成本;同句“pr_sync……成功后带 lease 推送”对 merge 路径不成立(实际 plain push,PrLifecycleSupport.cs:276 lease=mode!=FastForward;测试 Sync_merges…pushes_without_force 证实)。
4. **SPEC:50 模糊**:“与 GitCli 同一套……字节捕获……纪律”未区分 stderr 截断方向——GhCli stderr 保头(GhCli.cs:155),GitCli stderr 保尾(GitCli.cs:100)。若“纪律”含方向则矛盾,若不含则应写明差异是有意的。
5. **跨规格重复(一致但双写,易漂移)**:Forge.App/SPEC.md:138(起点/不带上游)↔SPEC:36;App:363(符号链接比较)↔SPEC:32;App:375(稀疏序列)↔SPEC:62;App:140(GetDiffSnapshotAsync/EmptyTreeSha)↔SPEC:17。architecture.md:27,48 仅模块表,无实质重复。建议机制细节归 Workspace,App 只留引用与 UI 决策。
6. **过时/不可验证断言**:SPEC:48“分析器 0 警告”是构建期事实,散文无法保鲜(F-WS-32);SPEC:42 的性能数字(约 70ms/80–120ms)无基准测试守护,属易过时注记。

## 业务闭环缺口

1. **推送 lease 闭环缺最后一环验证**:二次核对既无测试又有语义窄化(F-WS-14),是永不裸 force/永不静默发布的兜底,优先补。
2. **创建失败回滚闭环完整但韧性分支未证明**:稀疏失败→非强制移除→TryDeleteCreatedBranch→残留写明,链条与测试齐全;跨进程碰撞重试无测试(F-WS-28)。
3. **diff 诚实性闭环两处断点**:截断异常(F-WS-18)与损坏 hunk 标记(F-WS-19)是“不把半数据当完整”的机器可执行契约,均零测试;评审包与自评闸门消费它们,回归不可见。
4. **评审草稿→发送→解决闭环完整**:MarkSent 幂等、草稿不可 resolve、verdict 门禁、新评论阻塞、超时防重发,全链有测试,无缺口。
5. **规格写入守卫闭环完整**:三写路径均过 EnsurePlainPathInside,链接逃逸有真实 junction/symlink 测试;但宿主无法创建链接时静默跳过(见附注),CI 降权则证据消失。
6. **PR 平台错误分类**:分类器有捕获文本测试,真实 gh/GitHub 漂移无集成守护(§9)。

### 测试质量附注(按 test-anti-patterns 口径)
- **静默跳过(系统性,High)**:14 个文件约 183 处 `if (!GitProbe.IsAvailable) return;`,SpecToolsLinkSafetyTests 无法创建链接时打印后 return——宿主缺 git/链接权限时测试**绿而不跑**。建议显式 skip 上报,否则 CI 环境退化不可见。
- **墙钟延时(Medium)**:ReviewStoreTests.cs:137,217、GitCliCommitCancellationTests.cs:59(400ms)、GhCliProcessTreeTests.cs:26,38、ScreenshotCaptureTests.cs:76,105——时序敏感;轮询器测试已用 time provider,值得推广。
- **实现耦合(Low)**:GhCliSmokeTests.cs:73 反射读私有字段比对变量清单,目的正当但重构即碎。
- 正面:真实 git+本地裸远端、配置隔离;ThrowingPrPlatform 让意外调用响亮失败;断言具体、取消/并发覆盖密集,未发现空断言/自指断言/吞异常。
