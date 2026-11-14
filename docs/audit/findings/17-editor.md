# Forge.Editor 审计发现

对象:src/Forge.Editor/SPEC.md(46 行,draft)全部声明,不抽样;内化 TextMate 只核署名与边界。测试:tests/Forge.Editor.Tests(xUnit,407 方法全量清点);已先读 test-anti-patterns SKILL。只读审计,未改源码/规格/测试,未跑 git/build/test。

结论速览:**67 功能点 = 50 PASS + 10 PASS*(实现存在、无专项测试)+ 3 PARTIAL + 2 FAIL + 1 UNVERIFIED + 1 SPEC_GAP**。两 FAIL 均为规格明文契约未实现:refreshSupport 未声明(F-ED-40)、锚点移动搜索无索引(F-ED-53)。

## 功能点矩阵

测试证据格式 `文件::方法`(省略 tests/Forge.Editor.Tests/ 前缀);「无」= 无覆盖。

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-ED-01 | 不依赖 UI 工具包;快照/事件供上层消费 | SPEC.md:11,15 | Forge.Editor.csproj(仅 2 个 ProjectReference);全项目无 Avalonia using(grep 验证) | 无(结构性约束,检查验证) | PASS* |
| F-ED-02 | 只依赖 BCL 与 Forge.IO(仅保存原子替换) | SPEC.md:11 | Forge.Editor.csproj:10 多一个 `Forge.Abstractions` 引用;src 内无任何 `Forge.Abstractions` using(grep 零命中,零使用) | 无 | PARTIAL |
| F-ED-03 | TextMate 内化自 Tint,MIT 保留署名 | SPEC.md:11 | THIRD_PARTY_NOTICES.md(Tint 条目:commit df7ba6f、逐文件清单、修改说明);文件头署名如 TextMate/Internal/Tokenizer.cs:1-2 | 无(文档性约束) | PASS* |
| F-ED-04 | 两层着色:词法立即 + 语义叠加;不用 tree-sitter | SPEC.md:16 | Highlighting/HighlightMerger.cs(语义覆盖词法、切分跨界 token);全项目无 tree-sitter 引用 | Highlighting/HighlightMergerTests.cs::Semantic_token_wins_on_its_range_and_lexical_keeps_the_rest 等 8 方法 | PASS |
| F-ED-05 | AvaloniaEdit 不进入本项目 | SPEC.md:17 | Forge.Editor.csproj 无 AvaloniaEdit/Avalonia 引用 | 无 | PASS* |
| F-ED-06 | Rope + 不可变快照 | SPEC.md:23 | Text/Rope.cs(持久化节点替换)、Text/TextSnapshot.cs:7(sealed,只读 rope+惰性行表) | Text/RopeModelTests.cs::Slice_copy_and_indexer_agree_with_string_operations、Out_of_range_edits_throw_without_changing_the_rope;Text/TextDocumentTests.cs::Snapshot_taken_before_an_edit_is_unchanged_by_it、Concurrent_readers_see_whole_versions_while_a_writer_edits | PASS |
| F-ED-07 | 批量编辑原子提交为单版本 | SPEC.md:23 | Text/TextDocument.cs:268-289 ApplyLocked(校验→逐条替换→一次 Publish,版本+1) | Text/TextDocumentTests.cs::Batch_changes_apply_as_one_version_and_one_event、Batch_changes_refer_to_the_state_before_the_batch、Overlapping_or_out_of_range_batches_are_rejected_and_leave_the_text_alone、Empty_batch_returns_the_current_snapshot_without_a_version_bump | PASS |
| F-ED-08 | 撤销/重做分组 | SPEC.md:23 | Text/TextDocument.cs:138 BeginUndoGroup、315-341 EndUndoGroupLocked(嵌套折叠、净零变更不入栈)、Text/UndoHistory.cs | Text/TextDocumentTests.cs::Nested_undo_groups_collapse_into_one_step、Undo_is_refused_while_a_group_is_open、Undo_history_is_bounded_by_max_entries 等 10 方法 | PASS |
| F-ED-09 | 锚点随编辑迁移 | SPEC.md:23 | Text/TextDocument.cs Publish→AdjustAnchors、Text/Anchor.cs(bias/范围锚点) | Text/TextDocumentTests.cs::Anchors_follow_edits_before_them_and_collapse_when_their_text_is_deleted、Anchor_offsets_in_a_batch_follow_every_change、Range_anchor_biases_decide_whether_boundary_insertions_are_included 等 9 方法 | PASS |
| F-ED-10 | 事件提交后按版本顺序派发,重入安全 | SPEC.md:23 | Text/TextDocument.cs:419-450 DispatchEvents(锁内入队、_dispatching 标志防重入、循环排空保证版本序) | Text/TextDocumentTests.cs::Batch_changes_apply_as_one_version_and_one_event、Changed_event_reports_the_minimal_equivalent_change(顺序收集断言);重入场景(处理器内再编辑)无专项测试,见缺测试清单 | PASS |
| F-ED-11 | 保存记录实际写入的快照版本;保存期间新编辑保持 IsDirty | SPEC.md:23 | Text/TextDocument.cs:194-209 SaveAsync(先取 snapshot,写后 `_savedVersion=snapshot.Version`)、:57 IsDirty=版本比较;_saveGate 串行化保存 | Text/TextDocumentTests.cs::Dirty_flag_follows_saves_and_edits;Text/TextFileCodecTests.cs::A_failed_save_leaves_the_original_bytes_and_no_temporary_file、Cancelled_save_leaves_the_original_file_and_no_temporary_files(失败/取消后 IsDirty=true);保存进行中新编辑的竞态无专项测试 | PASS |
| F-ED-12 | WriteAsync 流式分块编码,不内存拼整份文件 | SPEC.md:25 | Text/TextFileCodec.cs:159-210(rope 逐块 Encoder.Convert+ArrayPool);**:168-174 规范化路径 GetText+Normalize+GetBytes 整份物化(三份全文驻留)** | Text/TextFileCodecTests.cs::Large_document_survives_a_save_and_reload、Surrogate_pair_split_across_rope_leaves_is_encoded_correctly(仅非规范化路径);规范化路径内存行为无测试 | PARTIAL |
| F-ED-13 | 临时文件 `<文件名>.<guid:N>.tmp` 于目标目录,CreateNew+FileShare.None | SPEC.md:25 | Text/TextFileCodec.cs:134-143 | Text/TextFileCodecTests.cs::Save_replaces_an_existing_file_and_removes_its_temporary、A_failed_save_leaves_the_original_bytes_and_no_temporary_file(断言目录仅剩原文件) | PASS |
| F-ED-14 | FlushAsync 后再 Flush(flushToDisk:true) | SPEC.md:25 | Text/TextFileCodec.cs:146-147 | 无专项断言(所有保存测试间接执行该路径,顺序本身不可观测) | PASS* |
| F-ED-15 | 用 AtomicFile.ReplaceFileAsync 发布,不自己 File.Move | SPEC.md:25 | Text/TextFileCodec.cs:150;全文件无 File.Move(grep 验证);Forge.IO/AtomicFile.cs:74 | Text/TextFileCodecTests.cs::Saving_while_another_handle_holds_the_file_succeeds_once_it_is_released(证明重试契约被委托) | PASS |
| F-ED-16 | 失败/取消删临时文件,原文件保持原字节 | SPEC.md:25 | Text/TextFileCodec.cs:152-156 catch→TryDelete→throw | Text/TextFileCodecTests.cs::A_failed_save_leaves_the_original_bytes_and_no_temporary_file、Cancelled_save_leaves_the_original_file_and_no_temporary_files(逐字节断言+目录清单断言) | PASS |
| F-ED-17 | 复用 Forge.IO 有界重试:占用重试;ReadOnly/目录立即失败 | SPEC.md:25 | Text/TextFileCodec.cs:150 委托 AtomicFile.ReplaceFileAsync(Forge.IO/AtomicFile.cs:63-74 同一重试/只读拒绝契约) | Text/TextFileCodecTests.cs::Saving_while_another_handle_holds_the_file_succeeds_once_it_is_released(释放后才完成)、A_failed_save_leaves_the_original_bytes_and_no_temporary_file(ReadOnly 目标 <1s 返回、保留只读属性);目录目标场景本套无测试(契约属 Forge.IO) | PASS |
| F-ED-18 | JSON-RPC over stdio,Content-Length 分帧,收发头缓冲分离 | SPEC.md:27 | Lsp/JsonRpcConnection.cs:62 读侧固定 `_readHeader[8KB]`;:228 写侧按帧租 ArrayPool,互不共享 | Lsp/JsonRpcFramingTests.cs::Content_length_counts_utf8_bytes_not_characters、Header_split_across_writes_while_a_request_is_sent_is_still_parsed、Oversized_header_section_closes_the_connection_with_a_protocol_error 等 11 方法 | PASS |
| F-ED-19 | 服务器请求 id 按 JSON 类型原样回传 | SPEC.md:27 | Lsp/JsonRpcConnection.cs:327 `id.GetRawText()`、:369-370 `WriteRawValue(idJson)`(数字/字符串均原样) | 无字符串 id 测试:Support/FakeLanguageServer.cs:96 `RequestAsync(int id,…)` 只发数字 id;LspClientTests 全部服务器请求用数字 id(4、91 等) | PASS* |
| F-ED-20 | .cmd/.bat 启动用 `cmd /d /s /c` 引号规则 | SPEC.md:27 | Lsp/LanguageServerProcess.cs:413-431(.cmd/.bat→cmd.exe)、BuildBatchCommandLine 实为 **`/e:ON /v:OFF /d /c`(无 /s)**;AppendBatchArgument 转义;路径含 `"^%`/尾随 `\`、参数换行→抛异常 | 无(tests 内无任何批处理引号/启动测试;LanguageServerDiscoveryTests.cs:150 的 .cmd 仅测路径解析) | PARTIAL |
| F-ED-21 | 进程工厂可注入以便测试 | SPEC.md:27 | Lsp/LanguageSession.cs:37 ProcessFactory、Lsp/LanguageServerProcess.cs:148 DefaultFactory | Lsp/LanguageSessionTests.cs 全套经 Support/FakeLanguageServerProcess.cs(继承 LanguageServerProcess 的进程内假服务器)驱动,如 Crash_of_a_running_server_restarts_it_and_reopens_documents | PASS |
| F-ED-22 | 同步泵按文档维护单一有序状态 | SPEC.md:27 | Lsp/LanguageSession.cs:55 单读 Channel、:781-804 OnDocumentChanged 合并计数、:843-905 SendPendingAsync(pending.Gate 每文档串行、单 delta 或全量二选一) | Lsp/LanguageSessionTests.cs::Sync_pump_coalesces_a_burst_of_changes_into_one_notification | PASS |
| F-ED-23 | 增量变更携带不可变截止快照 | SPEC.md:27 | Lsp/LanguageSession.cs:799 `pending.Latest=change.New`(TextSnapshot 不可变)、:866-870 仅当 `candidate.Old.Version==ServerVersion` 才发单 delta,否则发截止快照全量;失败置 ForceFull(:893-897) | Lsp/LspClientTests.cs::Incremental_change_reports_the_range_in_utf16_units、Incremental_change_reports_the_range_in_utf8_bytes_when_negotiated、Multi_line_incremental_change_spans_both_lines、Full_sync_servers_receive_the_whole_text_without_a_range | PASS |
| F-ED-24 | 功能请求前建立同步屏障 | SPEC.md:27 | Lsp/LanguageSession.cs:492-511 InvokeAsync:先 EnsureClientAsync 再 `await SyncDocumentAsync` 后才执行功能请求 | 无屏障专项断言(功能测试经 LspClient 直连,不经会话屏障);实现检查确认 | PASS* |
| F-ED-25 | 启动中服务器退出(两种观察顺序)→Unavailable+「failed to initialize」 | SPEC.md:27 | Lsp/LanguageSession.cs:975-986 OnProcessExited(handshake=0→Unavailable)、:618-624 ConnectAsync 失败→Unavailable;启动期未订阅新 client.Closed,无 Stopped 覆盖 | Lsp/LanguageSessionTests.cs:121-143 [InlineData(ExitBeforeClose)]/[InlineData(CloseBeforeExit)] 双顺序:断言 Unavailable、日志含 failed to initialize、状态序列不含 Stopped | PASS |
| F-ED-26 | Stopped 仅表示已 Running 崩溃、等待重启;重启退避 | SPEC.md:27 | Lsp/LanguageSession.cs:955-968 OnClientClosed(仅握手后订阅)、:989 handshake=1→Stopped;懒式重启(下次 EnsureClientAsync);LanguageServerProcess.cs:288-307 退避+MaxAttempts | Lsp/LanguageSessionTests.cs::Crash_of_a_running_server_restarts_it_and_reopens_documents、Restart_with_no_attempts_allowed_fails_immediately_with_the_stderr_tail、Restart_backoff_gives_up_on_a_missing_executable_after_the_delay | PASS |
| F-ED-27 | 两种观察顺序用可注入进程内假服务器固定,不依赖真实进程时序 | SPEC.md:27 | Support/FakeLanguageServerProcess.cs:7-16 FakeInitialisationExit 枚举、:247-255 在 initialize 时应答前按枚举固定 Exit/Close 顺序 | Lsp/LanguageSessionTests.cs:121-143(同 F-ED-25,Theory 双顺序) | PASS |
| F-ED-28 | 会话/客户端关闭生命周期(shutdown→exit、幂等释放) | SPEC.md:15,27(启动与通信职责) | Lsp/LspClient.cs ShutdownAsync/DisposeAsync;Lsp/LanguageSession.cs:390-400 DisposeAsync(置 Disposed、停泵、停进程) | Lsp/LspClientTests.cs::Shutdown_sends_shutdown_then_exit_and_is_a_no_op_once_closed、Disposing_the_client_is_idempotent_and_marks_it_closed;Lsp/LanguageSessionTests.cs::Session_disposal_moves_to_disposed_and_is_idempotent | PASS |
| F-ED-29 | 发现只查命令+参数表,不下载、不执行探测 | SPEC.md:29 | Languages/LanguageServerDiscovery.cs 仅 File.Exists/Directory.Exists(:167,206,247,304,383,410),无 Process.Start(grep 验证) | Languages/LanguageServerDiscoveryTests.cs::A_command_on_the_path_is_used_with_its_declared_arguments | PASS |
| F-ED-30 | 第一轮 PATH 已知命令表(规格列举的全部命令) | SPEC.md:29 | Languages/LanguageServerDiscovery.cs:447-467 PathCandidates:roslyn-language-server/csharp-ls/OmniSharp、typescript-language-server、vscode-{json,css,html}、pyright-langserver/pylsp、gopls、rust-analyzer、clangd、bash-language-server、yaml-language-server、lua-language-server,与规格一致 | Languages/LanguageServerDiscoveryTests.cs::A_command_on_the_path_is_used_with_its_declared_arguments、The_first_path_candidate_wins_for_a_language | PASS |
| F-ED-31 | 第二轮 VS Code/Cursor 扩展:ms-dotnettools.csharp-* 的 Roslyn dll+.roslyn 同名片、omnisharp | SPEC.md:29 | Languages/LanguageServerDiscovery.cs:470-478(dotnet 启动 Microsoft.CodeAnalysis.LanguageServer.dll,回退 .roslyn 目录,再回退 omnisharp/OmniSharp) | Languages/LanguageServerDiscoveryTests.cs::A_server_inside_a_vs_code_extension_is_launched_through_dotnet、Without_the_launcher_the_extension_is_not_used、The_newest_extension_version_is_preferred | PASS |
| F-ED-32 | 第三轮 VS Code 安装目录自带 JSON/CSS/HTML 服务(node *ServerMain.js) | SPEC.md:29 | Languages/LanguageServerDiscovery.cs:481-487(jsonServerMain.js/cssServerMain.js/htmlServerMain.js,命令 node) | Languages/LanguageServerDiscoveryTests.cs::A_server_shipped_with_the_editor_installation_runs_on_node、A_versioned_editor_installation_is_found_one_level_deep | PASS |
| F-ED-33 | 版本目录取最新;每扩展最多 32 个;缺失根目录跳过 | SPEC.md:29 | Languages/LanguageServerDiscovery.cs:86 上限 32、:325-338 版本排序+截断、:206,247 存在性守卫 | Languages/LanguageServerDiscoveryTests.cs::The_newest_extension_version_is_preferred、A_missing_root_is_skipped_without_throwing;32 上限本身无测试 | PASS |
| F-ED-34 | 结果仅作建议值+ServerOrigin;WithDiscoveredServers 不覆盖用户配置(含显式无服务器/disabled);未配置语言新增并继承扩展名表 | SPEC.md:29 | Languages/LanguageRegistry.cs:49-88(`_configured.Contains→skip`;新语言带 server.Extensions;已有定义保留扩展名表)、Languages/LanguageDefinition.cs:26 ServerOrigin | Languages/LanguageServerDiscoveryTests.cs::An_explicit_configuration_wins_over_discovery、A_language_configured_without_a_server_is_left_alone、A_language_disabled_in_the_configuration_is_not_resurrected_by_discovery、A_language_that_was_never_configured_gets_the_discovered_server_and_its_extensions 等 6 方法 | PASS |
| F-ED-35 | 发现只在启动时跑一次,失败只记一条 trace,不影响编辑 | SPEC.md:29 | 调用点在 App:src/Forge.App/Composition/EditorServices.cs:68 `LanguageServerDiscovery.Discover()`;Editor 侧无缓存/一次性强制 | 无(App 接线,别路审) | UNVERIFIED |
| F-ED-36 | 声明 publishDiagnostics(versionSupport)+textDocument/diagnostic;按 diagnosticProvider 决定拉取 | SPEC.md:31 | Lsp/LspClient.cs:645-664(两能力均声明)、:344-347 无能力不发请求;LspCapabilities.cs:95 | Lsp/LspClientTests.cs::Diagnostics_are_pulled_when_the_server_declares_a_diagnostic_provider、A_server_without_a_diagnostic_provider_is_not_asked | PASS |
| F-ED-37 | 拉取结果解析 kind:full 的 items、kind:unchanged、旧式裸数组 | SPEC.md:31 | Lsp/LspClient.cs:1217-1240 ParsePulledDiagnostics | Lsp/LspClientTests.cs::An_unchanged_pull_answer_and_a_bare_array_are_understood、Diagnostics_are_pulled_when_the_server_declares_a_diagnostic_provider | PASS |
| F-ED-38 | 拉取集合经 LanguageSession.PublishDiagnostics 写回,与推送同一事件(渲染/排序/重放单路径) | SPEC.md:31 | Lsp/LanguageSession.cs:339-347 PublishDiagnostics(Update+唯一 DiagnosticsChanged);推送经 :935 OnClientDiagnostics 汇入同一 store;闭环调用在 App:src/Forge.App/ViewModels/FileDocumentViewModel.Lsp.cs:641,650(拉取后 PublishDiagnostics) | Lsp/WorkspaceDiagnosticsTests.cs::Changed_fires_when_a_session_publishes_diagnostics;Lsp/LspClientTests.cs::Published_diagnostics_are_stored_as_immutable_snapshots;会话级「拉取→写回」组合无 Editor 内测试(依赖 App 接线) | PASS |
| F-ED-39 | 声明 window.workDoneProgress(否则 Roslyn 不加载项目) | SPEC.md:31 | Lsp/LspClient.cs:703-706 workDoneProgress=true(带注释说明 Roslyn 行为) | 无能力载荷断言;Lsp/LspClientTests.cs::Server_progress_brackets_one_busy_window 只测进度处理,不测声明 | PASS* |
| F-ED-40 | 声明 workspace.diagnostics.refreshSupport(否则服务器不告知重拉) | SPEC.md:31 | **未声明**:Lsp/LspClient.cs 能力写入器 workspace 段(:555-574)只有 applyEdit/workspaceEdit/configuration/workspaceFolders/didChangeConfiguration/didChangeWatchedFiles/semanticTokens;全文件无 `WritePropertyName("diagnostics")`(grep 验证)。唯一 refreshSupport 是 :572 `workspace.semanticTokens.refreshSupport=false` | 无(LspClientTests.cs:1190 的 refresh 测试由假服务器无条件发送,不校验客户端能力,掩盖此缺口) | FAIL |
| F-ED-41 | workspace/diagnostic/refresh→RefreshRequested→LanguageSession.DiagnosticsRefreshRequested | SPEC.md:31 | Lsp/LspClient.cs:751-752(refresh 类请求→RefreshRequested+确认)、Lsp/LanguageSession.cs:943-947 | Lsp/LspClientTests.cs::The_server_asking_for_a_diagnostics_refresh_is_surfaced(:1190-1194)、Workspace_refresh_requests_raise_the_refresh_event_and_are_acknowledged | PASS |
| F-ED-42 | SemanticTokenMap:UTF-16 直取;UTF-8 两端各自解码;越行剪到行尾 | SPEC.md:33 | Lsp/SemanticTokenMap.cs:68-83(Utf16 分支 Clamp;Utf8 分支 OffsetOfCharacter(start) 与 OffsetOfCharacter(start+len) 分别换算再 Clamp) | Lsp/SemanticTokenMapTests.cs::Utf16_columns_are_taken_as_character_offsets、Utf8_byte_columns_are_decoded_to_character_offsets、Utf8_byte_columns_after_a_wide_character_decode_to_the_right_range、A_token_running_past_its_line_is_clipped_to_the_line、A_token_on_a_line_the_snapshot_does_not_have_is_dropped | PASS |
| F-ED-43 | 图例按位置一次性映射 TokenCategory[],每 token 一次数组下标 | SPEC.md:33 | Lsp/SemanticTokenMap.cs:34-35 MapLegend 一次、:42 下标判定;Highlighting/SemanticTokenMapper.cs | Lsp/SemanticTokenMapTests.cs::Token_types_are_mapped_through_the_legend_positionally、A_token_whose_type_has_no_category_is_dropped;Lsp/SemanticTokensDecoderTests.cs::Legend_mapping_is_positional_and_unknown_entries_are_none | PASS |
| F-ED-44 | SemanticLineIndex 不可变按行数组;Build 收平行行号数组(乱序排序降级);内容未变行复用上一版数组;暴露 BuiltLines/ReusedLines/DroppedTokens | SPEC.md:33 | Highlighting/SemanticLineIndex.cs:53-59 计数器、:71-173 Build(:100 乱序排序、:140-159 逐行复用) | Highlighting/SemanticLineIndexTests.cs::An_unchanged_line_keeps_the_previous_array_and_a_changed_line_does_not、An_unordered_line_number_array_is_sorted_and_still_reuses_unchanged_lines、A_line_that_lost_all_of_its_tokens_is_not_counted_as_reused_while_an_unchanged_line_is 等 14 方法 | PASS |
| F-ED-45 | 索引带快照版本号;版本不符是否绘制由展示层决定 | SPEC.md:33 | Highlighting/SemanticLineIndex.cs:45 Version;Editor 不强制版本一致(无丢弃逻辑,符合「由展示层决定」) | Highlighting/SemanticLineIndexTests.cs::Empty_input_keeps_the_version_and_the_line_count_without_tokens、The_empty_index_has_no_tokens_and_no_document_version;Lsp/SemanticTokenMapTests.cs::The_index_carries_the_snapshot_version_and_reuses_the_previous_one、An_empty_result_gives_an_empty_index_of_the_snapshot_version | PASS |
| F-ED-46 | ApplyEdits 两趟单次分配;乱序先排序;重叠起点夹进上一条区间 | SPEC.md:33 | Lsp/LspCapabilities.cs:234-290(两趟单分配;SortedEdits 乱序才排序;Clamp 重叠起点) | Lsp/SemanticTokensApplyEditsOverlapTests.cs 全 10 方法(含 Random_overlapping_and_unordered_edits_never_throw_and_match_a_clamped_splice_model、The_previous_data_is_never_modified_by_overlapping_edits);Lsp/SemanticTokensDecoderTests.cs::Edits_given_out_of_order_are_applied_against_original_positions 等 | PASS |
| F-ED-47 | workspace/semanticTokens/refresh→SemanticTokensRefreshRequested(表示分类变了) | SPEC.md:33 | Lsp/LspClient.cs:751、Lsp/LanguageSession.cs:949-952(与诊断事件并列) | Lsp/LspClientTests.cs:849-851(RefreshRequested 收到 workspace/semanticTokens/refresh);注意客户端声明 semanticTokens.refreshSupport=false(:572),见规格质量 Q2 | PASS |
| F-ED-48 | WorkspaceDiagnostics 订阅池+各 store,只冒泡一次变更;读取时展平/排序;无后台计时器 | SPEC.md:35 | Lsp/WorkspaceDiagnostics.cs:52-62(订阅池事件+Reconcile)、:71-120 读时展平排序,无 Timer | Lsp/WorkspaceDiagnosticsTests.cs::Changed_fires_when_a_session_publishes_diagnostics、Changed_fires_when_a_session_starts_and_when_it_closes、Every_session_contributes_rows_sorted_by_severity_then_path_then_line、A_pool_without_sessions_reports_nothing | PASS |
| F-ED-49 | 行携带文件路径、所属工作区、会话位置编码(晚打开文件仍可换算偏移;跨工作区沿自己的会话打开) | SPEC.md:35 | Lsp/WorkspaceDiagnostics.cs:9-16 WorkspaceDiagnostic(Path,Workspace,Encoding,…)、:93-95 取 session.WorkspacePath/PositionEncoding | Lsp/WorkspaceDiagnosticsTests.cs::A_row_carries_the_file_path_workspace_severity_range_message_code_and_source、A_row_keeps_the_workspace_of_its_own_session、A_published_document_that_is_not_a_file_is_left_out;「沿自己会话打开」的 App 行为别路审 | PASS |
| F-ED-50 | 订阅与池对齐:静默退役会话下次读取摘掉;Snapshot 跳过 Disposed 会话 | SPEC.md:35 | Lsp/WorkspaceDiagnostics.cs:78 Reconcile、:84-89 跳过 Disposed;LanguageSessionPool.cs:65 过滤 Retired | Lsp/WorkspaceDiagnosticsTests.cs::Reconcile_drops_a_session_the_pool_forgot_without_an_event_and_later_publishes_do_not_reach_the_aggregator(:232 直接 DisposeAsync 后验证)、Changed_fires_when_the_idle_sweep_retires_a_session_and_its_rows_go_away(:178 断言 Disposed) | PASS |
| F-ED-51 | 行 Diff 默认比较行终止符;IgnoreLineEndings 可关闭 | SPEC.md:37 | Diff/LineDiff.cs:27 选项、:72-74 默认走 GetLineTextsWithBreak,开启则 GetLineTexts | Diff/LineDiffTests.cs::Snapshots_compare_line_terminators_by_default_and_ignore_them_on_request、A_trailing_newline_is_compared_as_part_of_the_line;Diff/DiffModelTests.cs::Options_are_passed_through_to_the_line_diff | PASS |
| F-ED-52 | 评论锚点按内容哈希重定位:精确/移动/模糊/丢失 | SPEC.md:37 | Diff/CommentAnchor.cs:160-244 Reanchor(Exact→窗口±1 扫描找 Moved→相似度≥阈值为 Fuzzy→Lost 并夹取行号)、FNV 行哈希+组合哈希 | Diff/CommentAnchorTests.cs::Moved_block_is_found_at_its_new_lines、Edited_line_inside_the_block_is_a_fuzzy_match_and_the_anchor_adopts_the_new_text、Unrelated_text_is_lost_and_the_original_lines_are_clamped_into_the_document、Max_distance_excludes_far_copies_of_the_block 等 16 方法 | PASS |
| F-ED-53 | 移动搜索建每 hunk 索引,避免平方级扫描 | SPEC.md:37 | **未实现索引**:Diff/CommentAnchor.cs:179-213 为 `for size∈[w-1,w+1] × for start∈[0,lineCount]` 暴力扫描,每个候选位置重算 LineHashesOf(O(行数×窗口));:252 的 Dictionary 仅是模糊相似度的参考哈希多重集,不是位置索引;Diff 目录无任何 hunk 索引(grep 验证);ReanchorOptions.MaxDistance 默认 0=无限制(:141) | 无性能/索引测试;功能正确性由 F-ED-52 测试覆盖 | FAIL |
| F-ED-54 | 服务器能折叠时用 textDocument/foldingRange,返回与本地启发式同形的 Folding.FoldingRange | SPEC.md:39 | Lsp/LspClient.cs:373-389(无能力→空,不发请求)、Lsp/LanguageSession.cs:360-362;两者同用 Folding/FoldingRange 类型 | Lsp/LspClientTests.cs::Folding_ranges_come_from_the_server_only_when_it_declares_the_capability | PASS |
| F-ED-55 | StructuralFolding:括号族(行/块注释、字串、C# 逐字串/raw string)、缩进族、#region(四种注释标记) | SPEC.md:39 | Folding/StructuralFolding.cs(FoldingSyntax.Braces/Indent/Region;:330-345 region 匹配、:447 注释标点剥离) | Folding/StructuralFoldingTests.cs::Braces_inside_strings_and_comments_are_ignored、A_verbatim_string_keeps_its_braces_and_a_raw_string_keeps_three_quotes、Regions_fold_in_every_dialect_and_nest 等 19 方法 | PASS |
| F-ED-56 | 单次线性扫描、ArrayPool 滑窗读 rope、不按行分配字符串 | SPEC.md:39 | Folding/StructuralFolding.cs:66 `ArrayPool<char>.Shared.Rent(WindowSize)`(32K 窗)、:154 归还;Scanner 直接在窗 span 上扫描 | Folding/StructuralFoldingTests.cs::A_document_beyond_one_window_still_folds_across_the_chunk_boundary、Crlf_line_endings_count_as_one_break;零分配无断言测试 | PASS |
| F-ED-57 | >20000 行返回空;区间上限 5000;深度上限 256;按起始行去重取最长 | SPEC.md:39 | Folding/StructuralFolding.cs:29-35 MaxLines=20_000/MaxRanges=5_000/MaxDepth=256、:49-51 行数守卫、:278,343,359 深度守卫、去重取最长(类注释+实现) | Folding/StructuralFoldingTests.cs::A_document_above_the_line_cap_yields_nothing、The_range_cap_bounds_a_pathological_document、One_range_per_start_line_keeps_the_longest、Ranges_are_sorted_by_start_line;深度 256 上限无专项测试 | PASS |
| F-ED-58 | 协议非法折叠区间丢弃:倒序/负数/字符串行号/endCharacter=0 指到下一块首行 | SPEC.md:39 | Lsp/LspClient.cs:1154-1192 ParseFoldingRanges(ValueKind!=Number 丢弃、start<0/end<0/end<=start 丢弃、endCharacter==0→endLine--、同起点保留最长) | Lsp/LspClientTests.cs::Folding_ranges_are_normalised_and_bad_entries_dropped | PASS |
| F-ED-59 | 扩展名→启发式族映射放 Forge.App;XML 交库自带 XmlFoldingStrategy | SPEC.md:39 | Editor 侧 StructuralFolding.Compute 只收 FoldingSyntax 参数,无扩展名表;App 侧存在 src/Forge.App/Editing/FoldingSyntaxTable.cs、EditorFoldingController.cs:265 XmlFoldingStrategy(存在性确认,细节别路审) | Editor 侧无(映射不在本项目);Folding/StructuralFoldingTests.cs 覆盖各语法族行为 | PASS |
| F-ED-60 | Oniguruma→.NET 正则翻译;翻译不了报诊断且不匹配;未内化 GregsEx | SPEC.md:41 | TextMate/Internal/Regexes/OnigurumaTranslator.cs(TryTranslate)、CompiledRegex.cs:49-64(失败→Never+Error,CanMatch=false)、Grammars/RegexSource.cs:27(生成器不带 GregsEx);THIRD_PARTY_NOTICES.md 记录改写 | TextMate/GrammarRegistryTests.cs::Invalid_regular_expressions_match_nothing_and_are_reported_by_the_tokenizer | PASS |
| F-ED-61 | include 展开带环检测与深度上限 | SPEC.md:41 | TextMate/Internal/Rules/Rule.cs:290-301(`_path.Contains(ruleId)`→环报告跳过;`_path.Count>=MaxDepth(256)`→深度报告)、CompiledGrammar.cs:147,276-289 编译深度同限 | 无环/深度专项测试(GrammarRegistryTests 的 recursive 仅指目录递归加载) | PASS* |
| F-ED-62 | 状态相等性覆盖影响后续分词的全部字段(含 BeginRuleCapturedEol),增量=全量 | SPEC.md:41 | TextMate/Internal/StateStack.cs:164-179 Equals+StructuralEquals、:186-206 GetHashCode 逐节点含 RuleId/EndRule/BeginRuleCapturedEol+作用域栈;行位置不参与比较(注释说明重置语义) | Highlighting/HighlighterTests.cs::Incremental_update_after_a_single_line_replace_equals_a_full_pass、Inserting_and_removing_lines_match_a_full_pass_over_random_edits、Removing_the_comment_closer_keeps_the_rest_of_the_file_in_comment_state | PASS |
| F-ED-63 | 用户语法来自 ~/.forge/grammars 与 <workspace>/.forge/grammars | SPEC.md:41 | TextMate/GrammarRegistry.cs:20 常量、:145-160 AddUserGrammarDirectories(先用户目录后工作区,工作区可覆盖) | 无(tests 内无 AddUserGrammarDirectories/.forge/grammars 调用;LanguageRegistryTests 的 .forge 是 languages.json,另一回事) | PASS* |
| F-ED-64 | 字符串/快照高亮源均视 LF/CRLF/CR 为换行,行内容不含换行;快照整份物化一次后按行返回 span,读行零分配 | SPEC.md:41 | Highlighting/IHighlightSource.cs:29-52 SplitLines(IndexOfAny('\r','\n')+CRLF 合并)、Highlighting/TextSnapshotHighlightSource.cs:17,24-29(GetText 一次+AsSpan 切片) | Highlighting/HighlighterTests.cs::Lf_and_crlf_split_lines_without_their_terminators、Lone_cr_splits_lines_like_a_snapshot_does;Highlighting/TextSnapshotHighlightSourceTests.cs::Reading_lines_allocates_nothing_after_warmup、Snapshot_source_matches_string_source_token_for_token、Snapshot_source_matches_string_source_for_a_line_longer_than_one_rope_leaf | PASS |
| F-ED-65 | 分词器行内容+\n 写入复用 char[];EnumerateMatches(span) 零分配找最早匹配;仅需 ≥1 组捕获时才物化字符串给 Regex.Match | SPEC.md:41 | TextMate/Internal/Tokenizer.cs:131-136(行+\n 入复用缓冲)、:390 EnumerateMatches、:552-600 NeedsCaptures 才物化、:605 惰性;RuleScanner.cs:234 span 搜索 | TextMate/TokenizeLineAllocationTests.cs::Capture_free_lines_tokenize_without_a_per_line_string、End_of_line_anchors_tokenize_identically_to_the_string_path | PASS |
| F-ED-66 | 单次搜索 500ms 预算;超预算先重试一次;第二次仍超时→不匹配+记诊断(最坏两个预算) | SPEC.md:41 | CompiledRegex.cs:18 500ms、:57,78 均带超时;RuleScanner.cs:136-153,230-260 首次超时 continue、二次记诊断+NoMatch | 无超时/重试专项测试(GrammarRegistryTests::Line_time_budget_stops_a_long_line_early… 测的是另一机制 LineTimeBudget 整行预算) | PASS* |
| F-ED-67 | 编码检测与保存格式(BOM/无 BOM 宽编码/Latin1 回退/二进制拒绝/行尾保持与规范化) | SPEC.md 未记录(仅 :17「原子编码保存」一词) | Text/TextFileCodec.cs:45-92 DetectEncoding(UTF-32 优先/零字节奇偶/Latin1)、二进制判定、:95-120 DetectLineEnding、:13 规范化默认 false | Text/TextFileCodecTests.cs 27 方法:Utf32_little_endian_bom_is_detected_before_utf16、Bomless_utf16_is_recognised_by_zero_byte_parity、Latin1_fallback_round_trips_non_utf8_bytes、Png_header_is_rejected_as_binary 等 | SPEC_GAP |

## 非 PASS 项详情

### F-ED-40(FAIL)workspace.diagnostics.refreshSupport 未声明
- 证据:SPEC.md:31 明文要求声明 `workspace.diagnostics.refreshSupport`。LspClient.cs 能力写入器 workspace 段(:555-574)无 `diagnostics` 属性;全文件 grep `refreshSupport` 仅 :572 一处,属 `workspace.semanticTokens` 且值 `false`。
- 缺口:合规服务器(Roslyn)见客户端不支持 refresh 便不发 `workspace/diagnostic/refresh`,拉取更新闭环(F-ED-41/38)失去触发源。LspClientTests.cs:1190 的假服务器无条件发送 refresh,不校验能力协商,绿灯掩盖缺口。
- 建议处理类型:代码修复(补 `diagnostics:{refreshSupport:true}`)+ 断言 initialize 载荷的能力测试。

### F-ED-53(FAIL)评论锚点移动搜索没有每 hunk 索引
- 证据:SPEC.md:37 承诺每 hunk 索引。Diff/CommentAnchor.cs:179-213 实为窗口尺寸 w-1…w+1 × 全文每个起始行的暴力扫描,每候选重算 `LineHashesOf`(O(行数×窗口)/锚点);`MaxDistance` 默认 0=不限距(:141)。:252 的 Dictionary 仅是模糊相似度的参考哈希多重集,非位置索引;Diff 目录无任何 hunk 索引(grep 验证)。
- 缺口:四种重定位状态正确且有 16 测试,但性能契约未实现;大文件多锚点批量重锚退化,结果断言无法暴露复杂度。
- 建议处理类型:代码修复(行哈希→行号索引)或规格降级(改写 SPEC.md:37 并记录理由),二者择一。

### F-ED-02(PARTIAL)依赖边界多一个零使用引用
- 证据:SPEC.md:11「只依赖 BCL 与 Forge.IO」;csproj:10 引用 Forge.Abstractions,src 内 grep 零命中(零使用,任务已知项)。
- 缺口:纯边界违规,无运行时影响,拖累依赖图与 AOT 裁剪面。
- 建议处理类型:代码修复(删该 ProjectReference,一行 diff)。

### F-ED-12(PARTIAL)保存的规范化路径整份物化
- 证据:SPEC.md:25「不在内存里拼整份文件」为无条件表述。TextFileCodec.cs:168-174:当 `NormalizeLineEndingsOnSave && snapshot.LineEnding != format.LineEnding` 时,`Normalize(snapshot.GetText(), …)` 生成整份字符串,再 `format.Encoding.GetBytes(normalized)` 生成整份字节数组——同一时刻至少三份全文驻留(原文、规范化串、字节数组)。非规范化路径(:176-210)确为 rope 分块 + Encoder.Convert 流式。
- 缺口:大文件 + 行尾规范化开启时内存尖峰,违反规格字面契约;测试只覆盖正确性(Normalisation_on_save_rewrites_all_breaks…),无内存行为断言。
- 建议处理类型:代码修复(规范化也走分块:逐 rope 块做 CR/LF 状态机转换)或规格修订(在 SPEC.md:25 明示规范化例外及其代价)。

### F-ED-20(PARTIAL)批处理启动命令行与规格不一致且零测试
- 证据:SPEC.md:27 指定 `cmd /d /s /c`。BuildBatchCommandLine 实际生成 `/e:ON /v:OFF /d /c ""<script>" <args…>"`(无 /s)。脚本恒被引号包裹(≥4 引号),cmd「恰好两引号则保留」分支不会命中,剥首尾引号与 /s 大概率等效——但属推断而非测试证明。另有未记录限制:脚本路径含 `"^%`/尾随 `\`、参数含换行→抛异常;`%`→`%%cd:~,` 转义。
- 缺口:npm shim 类服务器(typescript-language-server.cmd 等,发现表第一轮主力)启动正确性零测试;转义规则复杂无回归保护。
- 建议处理类型:补测试(命令行断言或假 cmd 脚本端到端)+ 规格/代码对齐(补 /s 或改写 SPEC.md:27)。

### F-ED-35(UNVERIFIED)发现一次性与失败降级
- 证据:调用点 src/Forge.App/Composition/EditorServices.cs:68;一次性/失败降级取决于 App 接线,别路审计范围。Editor 侧 API 无问题。

### F-ED-67(SPEC_GAP)编码检测契约整体未入规格
- 证据:检测/回退/二进制拒绝/行尾保持有 27 测试,但 SPEC 只写 WriteAsync 原子流式契约(:25);编码判定与行尾默认值等数据完整性行为完全未记录。
- 建议处理类型:规格补写(用户数据丢失/乱码的第一道防线,应为显式契约)。

### PASS* 项(实现存在、无专项测试)
F-ED-01/03/05(结构约束)、14(flush)、19(字符串 id)、24(屏障)、39(workDoneProgress 声明)、61(include 环)、63(语法目录)、66(500ms 重试)。见缺测试清单。

## 已实现但缺测试清单

| 行为 | 代码位置 | 风险 |
|---|---|---|
| 字符串 id 回传 | JsonRpcConnection.cs:327,369 | 假服务器只发 int id,类型改写无法被发现 |
| .cmd/.bat 引号/转义 | LanguageServerProcess.cs BuildBatchCommandLine | Windows 主力启动路径零覆盖 |
| 500ms 超时重试→二次超时不匹配+诊断 | RuleScanner.cs:136-153,230-260 | 增量/全量一致性关键保护 |
| include 环/256 深度 | Rule.cs:290-301、CompiledGrammar.cs:276 | 损坏语法可致挂死/诊断洪水 |
| 用户语法目录与覆盖顺序 | GrammarRegistry.cs:145-160 | 工作区覆盖优先级无保护 |
| 功能请求前同步屏障 | LanguageSession.cs:510 | 无会话级顺序断言 |
| 保存中新编辑→IsDirty | TextDocument.cs:194-209 | 规格明文竞态仅由实现保证 |
| Changed 内再编辑的重入派发 | TextDocument.cs:419-450 | 规格明文重入安全无测试 |
| initialize 能力载荷断言 | LspClient.cs:640-720 | 声明丢失(F-ED-40 类)测试全绿 |
| 折叠深度 256/零分配 | StructuralFolding.cs:278,343,359 | 上限与性能无断言 |
| 每扩展 32 版本截断 | LanguageServerDiscovery.cs:338 | 无测试 |
| 目录目标保存立即失败 | TextFileCodec.cs:150 | 本套仅测 ReadOnly |
| FlushAsync→Flush(true) 顺序 | TextFileCodec.cs:146-147 | 持久性顺序不可观测 |

## 程序→规格:未记录行为

1. **编码检测全套规则**(TextFileCodec.cs:45-120):UTF-32 BOM 先于 UTF-16、零字节奇偶判无 BOM 宽编码、Latin1 兜底、NUL/PNG 头判二进制;保存默认保持原行尾、NormalizeLineEndingsOnSave 默认 false、Mixed→LF(:213);自动创建缺失目录(:129);默认 UTF-8 无 BOM+LF(:10)。
2. **TextSearch 子系统**(Text/TextSearch.cs,21 测试):字面/正则/全词、默认忽略大小写、逐行锚点、零长匹配跳过——SPEC 未提搜索。
3. **LSP 默认值**:RequestTimeout=30s(LspClient.cs:26)、ClientVersion="1.0"、头上限 8KB(JsonRpcConnection.cs:48)、completion 上限与无效项计数丢弃、positionEncodings 只声明 utf-16(:713-718)。
4. **workspace.semanticTokens.refreshSupport=false**(LspClient.cs:572):与 SPEC:33 语义刷新期望存在张力,未记录。
5. **批处理加固**(LanguageServerProcess.cs):/e:ON /v:OFF、路径含 `"^%`/尾随 `\` 拒绝、参数换行拒绝、`%`→`%%cd:~,`、idleExitMilliseconds=2000(:130)、RestartPolicy 退避参数。
6. **发现额外上限与 tsserver**:MaxInstallationChildren=8(:89,SPEC 只写 32/扩展)、typescript 自动附 tsserver 路径(:128-190,含 npm 全局布局)。
7. **文本模型**:maxUndoEntries=256(TextDocument.cs:34)、批量变更须有序不重叠(:343-360)、事件最小等价变更合并、MarkSaved() 无参旁路(:178,可把未写入版本标为已保存)。
8. **会话运维**:StderrRing、懒式重启(下次使用时经 EnsureClientAsync)、空闲清扫、池按工作区+语言复用。
9. **常量**:折叠 TabWidth=4/WindowSize=32K(StructuralFolding.cs:37-39);WorkspaceDiagnostics 排序 severity→path→line(:113-120)。
10. **TextMate 运维**:LineTimeBudget 整行预算(独立于 500ms)、热模式 Promote 为 Compiled、BoundedCache、诊断限流。
11. **主题层**:ThemeMapper/ScopeTable/TokenCategoryGroup 有测试无规格(首条匹配、特定优先)。

## 规格质量问题

- **Q1(矛盾,SPEC.md:27)**:「`cmd /d /s /c` 引号规则」与实现 `/e:ON /v:OFF /d /c`(无 /s)不符。要么代码补 /s,要么规格改写为实际开关并论证等价性;现状是规格断言了一个不存在的命令行。
- **Q2(矛盾/遗漏,SPEC.md:31 与 :33)**:规格要求声明 `workspace.diagnostics.refreshSupport`,代码未声明(FAIL F-ED-40);同时代码声明 `workspace.semanticTokens.refreshSupport=false` 而规格 :33 期望语义刷新事件闭环,两处未互相校准。规格应同时写明两个 refreshSupport 的期望值。
- **Q3(过时/失实,SPEC.md:37)**:「移动搜索建每 hunk 索引,避免平方级扫描」描述了不存在的实现(暴力扫描,见 F-ED-53)。性能契约写成既成事实,审计/读者会被误导。
- **Q4(模糊,SPEC.md:25)**:「不在内存里拼整份文件」无条件成立,但规范化路径整份物化(F-ED-12)。流式契约需要明示例外或修复代码。
- **Q5(重复,SPEC.md:41 与 :46)**:「未决」节的捕获物化限制与 TextMate 节末句几乎逐字重复;未决节应只留真正未决项,或合并。
- **Q6(覆盖缺口)**:编码检测/行尾契约(F-ED-67)、TextSearch 及未记录行为节的默认值(30s 超时、256 撤销、8KB 头上限等)无规格归属;编码检测关系数据完整性,优先级最高。
- **Q7(层级混杂,SPEC.md:17-19)**:AvaloniaEdit 准入/迁移去留属架构级治理,混在模块边界节;「应优先复用」与自有内核继续演进并存,去留未决。建议上移 forge-architecture 留引用。
- **Q8(状态,SPEC.md:4)**:治理规格长期 draft,建议评审后转 active。

## 业务闭环缺口

1. **诊断刷新闭环断在能力协商**(F-ED-40→41→38):处理链路齐全但不声明 refreshSupport,合规服务器不发 refresh;「分析更新→重拉→单路径发布」只能靠服务器越权发送才成立。
2. **语义刷新同病**(Q2):refreshSupport=false 使 SemanticTokensRefreshRequested 在合规服务器下永不触发,分类重算只能靠文本编辑驱动。
3. **拉取→写回依赖 App 自觉**:会话 RequestDiagnosticsAsync 不自动 PublishDiagnostics,闭环靠 FileDocumentViewModel.Lsp.cs:641-650 手工衔接;新调用方漏写回即破坏单路径承诺。建议提供 pull-and-publish 组合 API 或在规格明写调用方义务。
4. **大文件重锚性能**(F-ED-53):每锚点 O(全文×窗口),多锚点放大为 O(锚点数×全文)。
5. **规范化保存内存尖峰**(F-ED-12):极端 OOM 致保存失败(原子性保住原文件,功能失败)。
6. **.cmd 启动无回归保护**(F-ED-20):转义回归表现为「启动即退」→Unavailable,难归因。
7. **规格明文行为缺测试固定**:字符串 id、保存竞态、重入、500ms 重试、include 环(见缺测试清单)。

---
附注:Abstractions 零使用见 F-ED-02;内化署名完整(THIRD_PARTY_NOTICES.md 含 Tint commit、文件清单、许可证,发布须随附);无 UI/tree-sitter/GregsEx 残留。