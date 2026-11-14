# Mcp/Approval/Workflows 审计发现

审计范围:三份模块 SPEC 全部规范性声明;实现核对限 src/Forge.Mcp、src/Forge.Approval、src/Forge.Workflows、src/Forge.Abstractions(跨规格核对 Forge.App/SPEC.md、Forge.ExtensionHost/SPEC.md)。只读审计,未改源码/规格/测试,未跑 build/test/git。

方法:先 grep 定位再精读决定性行;测试证据为 xUnit `[Fact]/[Theory]`(三套件共 506 个测试方法,已全量清点方法名)。已读 `.github/skills/test-anti-patterns/SKILL.md` 并据此做名称级全量审查+关键用例抽查(未逐行通读全部测试体,见「测试质量注记」)。

路径缩写(下文代码/测试位置均相对仓库根):
- M = src/Forge.Mcp,A = src/Forge.Approval,W = src/Forge.Workflows
- TM = tests/Forge.Mcp.Tests,TA = tests/Forge.Approval.Tests,TW = tests/Forge.Workflows.Tests

结论口径:PASS=实现符合且有测试证据;PASS*=实现存在、核对无误但缺(直接)测试;PARTIAL=部分符合;FAIL/MISSING/SPEC_GAP/UNVERIFIED 本次未出现。

## 功能点矩阵

### Forge.Mcp(36 项)

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-MCP-01 | 自写 JSON-RPC/传输,只依赖 Forge.Abstractions+BCL,不引 MCP SDK | SPEC.md:11 | Forge.Mcp.csproj:9;M/RpcJson.cs:9;M/JsonRpcPeer.cs | TM/Fixtures/JsonRpc.cs(手写协议夹具)+全套件 | PASS |
| F-MCP-02 | stdio+可流式 HTTP 两传输;不支持 OAuth/旧版 SSE/subscribe/completions/进度 | SPEC.md:15 | M/StdioTransport.cs;M/StreamableHttpTransport.cs;能力旗标仅解析不实现(M/McpModels.cs:41,45,无功能消费方) | TM/ChildProcess/StdioTransportTests.cs::Handshake_list_and_call_work_over_a_real_child_process;TM/Http/StreamableHttpTransportTests.cs(21) | PASS |
| F-MCP-03 | 每请求截止期限覆盖发送+等待全程,关联调用方取消与连接生命周期 | SPEC.md:24 | M/JsonRpcPeer.cs:56-64(deadline CTS+linked(ct,deadline,lifetime),SendAsync 与 WaitAsync 同令牌) | TM/Client/McpClientRequestTests.cs::Timeout_throws_timeout_exception_and_sends_cancellation_for_that_request 等 16 | PASS |
| F-MCP-04 | 超时/取消在已发出后发 notifications/cancelled(initialize 除外) | SPEC.md:24 | M/JsonRpcPeer.cs:66-91,295-319 | TM/Client/McpClientRequestTests.cs::Timeout_throws_timeout_exception_and_sends_cancellation_for_that_request;TM/ChildProcess/StdioTransportTests.cs::Timed_out_request_sends_cancellation_and_handshake_messages_arrive_on_stdin_as_lines | PARTIAL |
| F-MCP-05 | id 按类型与值精确关联,字符串 id 不匹配数字 id | SPEC.md:24 | M/JsonRpcPeer.cs:15,360(只发/只收数字 id,TryReadId 拒绝字符串) | TM/Client/McpClientRequestTests.cs::String_id_response_does_not_complete_the_numeric_request_with_the_same_digits | PASS |
| F-MCP-06 | stdio 字节分帧;非 JSON stdout 入有界诊断环且继续读;stderr 严格有界;两个独立读任务 | SPEC.md:26 | M/StdioTransport.cs:54-57,168-169,291,417;M/BoundedLog.cs:4-35 | TM/ChildProcess/StdioTransportTests.cs::Stderr_lines_are_captured_and_non_json_stdout_is_routed_to_the_log | PASS |
| F-MCP-07 | 子进程退出 Closed 必带退出码;EOF 后 500ms 观察窗口 | SPEC.md:26 | M/StdioTransport.cs:37,345-352,505-509 | TM/ChildProcess/StdioTransportTests.cs::Process_exit_faults_pending_requests_and_raises_closed_with_the_exit_code(真实子进程) | PASS |
| F-MCP-08 | HTTP 按 SSE 流分别维护 Last-Event-ID | SPEC.md:26 | M/StreamableHttpTransport.cs:51-52,324-327,381(server/post 两条独立+每 PostStream 自带) | TM/Http/StreamableHttpTransportTests.cs::Server_stream_pushes_notifications_and_resumes_with_last_event_id_after_the_stream_ends;::Initialize_answer_over_sse_carries_the_event_id_that_later_resumes_the_server_stream | PASS |
| F-MCP-09 | 每个 POST SSE 响应流独立取消/释放 | SPEC.md:26 | M/StreamableHttpTransport.cs:193,501-506(PostStream+CancelPostStreams) | TM/Http/StreamableHttpTransportTests.cs::Concurrent_json_calls_each_receive_their_own_answer;::Close_ends_the_session_with_a_single_delete | PASS |
| F-MCP-10 | 旧代际消息不完成新请求 | SPEC.md:26 | M/StreamableHttpTransport.cs:58,105,592-597(generation 守卫+DispatchPayload 检查) | 无直接测试(现有测试只覆盖新客户端重新初始化,未对同一传输重启后喂旧代际消息) | PASS* |
| F-MCP-11 | 会话 DELETE 带原始会话号、独立关闭期限 | SPEC.md:26 | M/StreamableHttpTransport.cs:262-289,453-467;SessionShutdownTimeout=5s(:26) | TM/Http/StreamableHttpTransportTests.cs::Close_ends_the_session_with_a_single_delete;::Close_sends_the_session_header_on_delete | PASS |
| F-MCP-12 | 暴露名 <server>__<tool>,稳定哈希消歧、不依赖列表顺序 | SPEC.md:16,28 | M/McpToolNaming.cs(Build/Disambiguate/IsLossless) | TM/Naming/McpToolNamingTests.cs::Hash_suffix_is_stable_across_calls_and_processes_for_the_same_input(共11) | PASS |
| F-MCP-13 | 同一名字在管理器生命周期内不重新绑定到别的工具 | SPEC.md:28 | M/McpServerManager.cs:808-838(AssignNameLocked,nameByIdentity/identityByName 永不清除,grep 无 Remove) | TM/Manager/McpServerManagerTests.cs::Tools_with_the_same_server_and_colliding_sanitized_names_get_distinct_deterministic_names(确定性已测;「工具消失后名字不被占用」无直接用例) | PASS* |
| F-MCP-14 | 懒启动;状态 Stopped/Starting/Ready/Failed | SPEC.md:30 | M/McpServerManager.cs:462-495(EnsureClientAsync);M/McpServerState.cs | TM/Manager/McpServerManagerTests.cs::Servers_are_lazy_until_started_and_start_publishes_prefixed_tools;::Concurrent_starts_connect_only_once | PASS |
| F-MCP-15 | 连接/初始化失败停止并回收传输 | SPEC.md:30 | M/McpServerManager.cs:559-590(catch:Failed+DisposeClient+RecycleTransport) | TM/Manager/McpServerManagerTests.cs::Initialize_failure_marks_the_server_failed_with_last_error_and_rethrows;::Transport_factory_failure_marks_the_server_failed | PASS |
| F-MCP-16 | 调用方取消不标记失败、不安排重连 | SPEC.md:30 | M/McpServerManager.cs:541-558(OCE 分支:Stopped、清错误、不 ScheduleReconnect) | TM/Manager/McpServerManagerTests.cs::Caller_cancellation_during_start_is_not_recorded_as_a_failure | PASS |
| F-MCP-17 | 崩溃后退避重连(指数、封顶、次数上限、可取消) | SPEC.md:30 | M/McpServerManager.cs:892,917-963(ScheduleReconnect/Backoff) | TM/Manager/McpServerManagerTests.cs::Reconnect_attempts_back_off_exponentially_from_the_initial_delay;::Reconnect_gives_up_after_max_attempts 等 6 | PASS |
| F-MCP-18 | 配置读全局+工作区,项目按名覆盖全局,坏条目诊断不致命 | SPEC.md:17 | M/McpConfigLoader.cs:17,82,300 | TM/Config/McpConfigLoaderTests.cs::Project_entry_replaces_the_global_entry_of_the_same_name_and_keeps_the_rest(共21) | PASS |
| F-MCP-19 | url 仅接受绝对 http/https URI | SPEC.md:17 | M/McpConfigLoader.cs:170-176;M/McpConfigFile.cs(Validate) | TM/Config/McpConfigLoaderTests.cs::A_url_that_is_not_http_or_https_is_rejected_on_every_platform | PASS |
| F-MCP-20 | 写变换只替换被编辑条目,其它条目/根键保留;不展开 ${VAR};注释不保留 | SPEC.md:18 | M/McpConfigFile.cs:362,375(JsonNode+WriteIndented),519-537(原文态写回) | TM/Config/McpConfigFileTests.cs::Upsert_replaces_only_the_target_entry_and_leaves_other_roots_and_entries_alone(共44) | PASS |
| F-MCP-21 | 文件 IO 与锁不在本库;App McpConfigStore 用 SettingsFileMutex+AtomicFile | SPEC.md:18 | M/McpConfigFile.cs 无 File 写 API(grep 证实);src/Forge.App/Services/McpConfigStore.cs:207,215 | TM/Config/McpConfigFileTests.cs 全部为纯文本变换测试;App 侧锁/原子写不在本审计范围 | PASS |
| F-MCP-22 | 预设只是命令行数据,库不含用户可见文案 | SPEC.md:19 | M/McpPresets.cs:11(record 仅 Id/Command/Args/Url/NeedsPath,无标题/说明) | TM/Config/McpPresetsTests.cs::ToEntry_fills_the_path_token_into_every_argument_that_contains_it(共7) | PASS |
| F-MCP-23 | 原文态 McpServerEntry(RawNodeJson)与展开态 McpServerConfig 分离;Source 记录来源文件 | SPEC.md:32 | M/McpConfigFile.cs:41;M/McpConfig.cs:33;M/McpConfigLoader.cs:300-320(展开) | TM/Config/McpConfigFileTests.cs::Read_keeps_fields_verbatim_without_expanding_variables | PASS |
| F-MCP-24 | ApplyConfigAsync 差量:删先停再释放;改连传输回收再重启;新增启用但懒启动;disabled 以文件为准;单台失败隔离 | SPEC.md:34 | M/McpServerManager.cs:126-217(RetireAsync/StopServerAsync+RecycleTransportAsync+Enabled=!Disabled+catch 不中断) | TM/Manager/McpServerManagerApplyTests.cs::ApplyConfig_restarts_a_changed_entry_on_a_transport_built_from_the_new_entry;::ApplyConfig_marks_a_failed_restart_on_that_server_only_and_does_not_throw(共25) | PASS |
| F-MCP-25 | 选项活读 Func<Options>:超时每次建客户端读、退避每次读 | SPEC.md:36 | M/McpServerManager.cs:14,34,509,920 | TM/Manager/McpServerManagerApplyTests.cs::Options_are_read_again_for_each_connection_so_a_changed_timeout_applies_without_rebuilding_the_manager;::Backoff_delays_are_read_fresh_for_each_scheduled_reconnect | PASS |
| F-MCP-26 | MaxToolSchemaChars 默认 64K、0 关闭;超限工具不提供且 LastError 写明数量与首个 | SPEC.md:38 | M/McpServerState.cs:57;M/McpServerManager.cs:763-789 | TM/Manager/McpToolSurfaceLimitTests.cs::A_tool_whose_schema_exceeds_the_cap_is_not_offered_and_the_state_says_why(共4) | PASS |
| F-MCP-27 | MaxToolDescriptionChars 默认 4K,截断加省略号、不拆代理对 | SPEC.md:38 | M/McpServerState.cs:60;M/McpServerManager.cs:858-870(CapDescription) | TM/Manager/McpToolSurfaceLimitTests.cs::A_description_cut_never_splits_a_surrogate_pair | PASS |
| F-MCP-28 | ConfigOfTool 走暴露名分配同一张表反查 | SPEC.md:40 | M/McpServerManager.cs:222-240(identityByName 反查) | TM/Manager/McpServerManagerApplyTests.cs::ConfigOfTool_resolves_a_disambiguated_exposed_name 等 3 | PASS |
| F-MCP-29 | 上限 0=全部;上限生效 App 选面(pin→打分),隐藏时提供 mcp_tool_search,命中即 pin 重发布;小写词包含打分+缓存 | SPEC.md:42 | M/McpToolSearchTool.cs:28-31(pin 回调);src/Forge.App/Agents/McpToolExposure.cs(选面在 App) | TM/Manager/McpToolSearchToolTests.cs(8);App 侧 tests/Forge.App.Tests/Mcp/McpToolExposureTests.cs(范围外,存在) | PASS |
| F-MCP-30 | mcp_tool_search 只读、不执行任何服务器工具 | SPEC.md:20 | M/McpToolSearchTool.cs:40(SideEffect.ReadOnly),仅回调 pin | TM/Manager/McpToolSearchToolTests.cs::Definition_is_read_only_with_the_documented_name_and_a_valid_json_schema | PASS |
| F-MCP-31 | content 按块保留;McpContent 带 Type/Text/MimeType/Uri/SizeBytes,图片带 Data;audio 只留占位 | SPEC.md:44 | M/McpContentFlattener.cs:155-165;M/McpModels.cs:51 | TM/Client/McpContentDataTests.cs::A_displayable_image_within_the_cap_keeps_its_data_on_the_content_block;TM/Client/McpClientListAndResultTests.cs(30) | PASS |
| F-MCP-32 | 只留 png/jpeg/gif/webp;单张解码>5MB 丢字节;单结果合计>20MB 其后只留占位;audio 不留字节 | SPEC.md:44 | M/McpContentFlattener.cs:15(5MB),22(4×5MB=20MB),155,162 | TM/Manager/McpToolAdapterImageTests.cs::Images_past_the_per_result_budget_fall_back_to_their_placeholder_text;::An_svg_image_stays_a_placeholder | PASS |
| F-MCP-33 | Adapter 返回块列表:文本优先(剔除已成图片的块)后逐图 ImageBlock;result.Text 仅在无结构化 content 时为唯一文本块;字符截断只作用文本 | SPEC.md:44 | M/McpToolAdapter.cs:44-49,76-85(BlocksOf/Truncate) | TM/Manager/McpToolAdapterImageTests.cs::Text_and_image_keep_the_text_first_and_the_image_second(共8);TM/Client/McpClientListAndResultTests.cs::CallToolAsync_uses_structured_content_only_when_there_are_no_content_blocks | PASS |
| F-MCP-34 | 副作用默认 Execute,readOnlyTools 可标只读;调用仍走主程序审批 | SPEC.md:16 | M/McpServerManager.cs:848-852(CreateAdapter);M/McpConfig.cs:39(IsReadOnlyTool);审批执行在 App(App/SPEC.md:338 审批卡经 ConfigOfTool) | TM/Manager/McpServerManagerTests.cs::Read_only_tools_from_config_map_to_read_only_side_effects | PASS |
| F-MCP-35 | 工具清单刷新:list_changed 通知+手动刷新 | SPEC.md:26(隐含于管理) | M/McpServerManager.cs:430-435,733-757(RefreshToolsCoreAsync) | TM/Manager/McpServerManagerTests.cs::Tools_list_changed_notification_refreshes_the_snapshot_and_raises_tools_changed | PASS |
| F-MCP-36 | 进程清理:优雅关闭→超时杀进程;Dispose 停进程 | SPEC.md:26,30 | M/StdioTransport.cs:241-251(ShutdownTimeout→Kill→KillTimeout) | TM/ChildProcess/StdioTransportTests.cs::Graceful_close_shuts_the_child_down;::Start_is_idempotent_while_the_process_runs_and_dispose_stops_it | PASS |

### Forge.Approval(24 项)

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APPR-01 | Evaluate 纯函数无 IO;store 重载读内存快照 | SPEC.md:15 | A/ApprovalEngine.cs:18-27(仅 store.Get),31-110 无 IO | TA/ApprovalEngineTests.cs::Exact_match_through_the_store_is_allowed(共27) | PASS |
| F-APPR-02 | ApprovedHash=SHA256(Source+Dependency+Sdk);任一漂移失效;行尾归一化 | SPEC.md:21 | A/Hashing.cs:17,60,69-73(CRLF/CR→LF,去一个尾随 LF) | TA/HashingTests.cs::Fingerprint_is_the_sha256_of_source_dependency_and_sdk_joined_by_newlines(共25);TA/ApprovalEngineTests.cs::Line_ending_only_edit_of_an_approved_plugin_stays_allowed | PASS |
| F-APPR-03 | 状态矩阵:Draft→Require;Disabled/Uninstalled→Block;无记录→Require;三哈希+指纹全等才 Allowed | SPEC.md:23 | A/ApprovalEngine.cs:41-110 | TA/ApprovalEngineTests.cs::Tampered_record_fails_the_fingerprint_even_when_the_candidate_matches_it;::Disabled_or_uninstalled_record_is_blocked_even_when_hashes_match | PASS |
| F-APPR-04 | SetApproved 保留封锁;Enable 只解 Disabled(有 hash 回 Approved 否则 Draft);Revoke 对封锁空操作 | SPEC.md:23 | A/ApprovalStore.cs:326,355,391(三个事务内 switch) | TA/ApprovalStoreTests.cs::Set_approved_on_a_disabled_version_does_not_lift_the_block 等 6;TA/ApprovalStickyBlockTests.cs::Concurrent_reapproval_never_lifts_a_concurrent_disable | PASS |
| F-APPR-05 | approvals.db;TEXT 复合主键 BINARY;status 枚举名;“O”时间戳+updated_utc_ticks;approval_meta;user_version 阶梯;WITHOUT ROWID | SPEC.md:25 | A/ApprovalDatabase.cs:9,13-31(SchemaSql),24-26 | TA/ApprovalSchemaTests.cs::Fresh_store_creates_schema_version_1_wal_and_marker;TA/ApprovalStoreTests.cs::Database_path_is_approvals_db_inside_the_full_directory_path | PASS |
| F-APPR-06 | List/Get 的 ordinal 与「最近更新」语义在 C# 内存,不在 SQL ORDER BY | SPEC.md:25 | A/ApprovalStore.cs:252-289(Get 内存扫描+UpdatedAt 比较+版本 ordinal 决胜),525-531(OrderedSnapshot) | TA/ApprovalStoreTests.cs::Get_without_version_returns_the_most_recently_updated_version;::List_orders_by_plugin_id_then_ordinal_version | PASS |
| F-APPR-07 | 无删除记录 API;Uninstalled+清 ApprovedHash=永久封锁,SetApproved 不能恢复 | SPEC.md:27 | A/ApprovalStore.cs 无 Delete/Remove(grep 证实);状态机见 F-APPR-04 | TA/ApprovalStoreTests.cs::Set_approved_on_an_uninstalled_version_does_not_make_it_runnable(Host 侧 plugin.delete 调用链在 ExtensionHost,范围外) | PASS |
| F-APPR-08 | CountReadOnlyAsync:只读非池化;缺文件/缺表→0;其余 SQLite 失败上抛 | SPEC.md:29 | A/ApprovalStore.cs:137-175(sqlite_master 探测) | TA/ApprovalCountReadOnlyTests.cs::Corrupt_file_throws_instead_of_returning_zero(共6) | PASS |
| F-APPR-09 | ListReadOnlyAsync:同一只读非池化连接列全部记录,按 id+版本排序;缺文件/缺表→空;其余失败上抛 | SPEC.md:29 | A/ApprovalStore.cs:177-214(ReadOnly+Pooling=false+OrderByPlugin) | 无(全仓库 tests/ grep 无 ListReadOnlyAsync 引用;消费方 src/Forge.App/ViewModels/Settings/InstrumentSettingsSections.cs) | PASS* |
| F-APPR-10 | 每写=BEGIN IMMEDIATE:事务内读行→状态机→参数化 upsert→COMMIT;persist-before-publish | SPEC.md:31 | A/ApprovalStore.cs:450-477(Change:BeginTransaction(deferred:false)→ReadRow→transition→Write→Commit→records[key]);A/ApprovalDatabase.cs:43-54(ON CONFLICT 参数化) | TA/ApprovalConcurrencyTests.cs::Parallel_tasks_on_one_store_lose_no_update_and_match_the_table;TA/ApprovalFailureTests.cs::Write_to_a_read_only_database_throws_and_leaves_the_snapshot_unchanged | PASS |
| F-APPR-11 | busy 耗尽原样抛 SqliteException 不重试;取消在事务开始前检查 | SPEC.md:33 | A/ApprovalStore.cs:455-461(两次 ThrowIfCancellationRequested 均在 BeginTransaction 前;无重试循环) | TA/ApprovalFailureTests.cs::Cancelled_write_throws_and_writes_nothing;::Locked_database_fails_within_the_busy_budget_and_retries_cleanly | PASS |
| F-APPR-12 | 单长连接 Pooling=false、lock(gate) 串行;pragma 顺序 busy_timeout→journal_mode→synchronous;DefaultTimeout 同值;synchronous=FULL | SPEC.md:35 | A/ApprovalDatabase.cs:61-80;A/ApprovalStore.cs:457(lock) | TA/ApprovalSchemaTests.cs::Fresh_store_creates_schema_version_1_wal_and_marker | PASS |
| F-APPR-13 | Dispose:wal_checkpoint(TRUNCATE)→关连接→ClearAllPools | SPEC.md:35 | A/ApprovalStore.cs:421-441 | TA/ApprovalFailureTests.cs::Dispose_removes_journal_files_and_frees_the_directory | PASS |
| F-APPR-14 | WAL 回退读返回值;非 wal 时 stderr 一行;JournalMode 属性给实际值 | SPEC.md:37 | A/ApprovalDatabase.cs:78;A/ApprovalStore.cs:86-91 | TA/ApprovalSchemaTests.cs::Journal_mode_reports_the_mode_sqlite_actually_selected | PASS |
| F-APPR-15 | data_version 失效:先版本后行;构造/Reload/失效重读三路径同序;整表读成功才换快照+版本 | SPEC.md:39 | A/ApprovalStore.cs:75-84(构造),408-418(Reload),483-493(Refresh) | TA/ApprovalDataVersionTests.cs::Second_instance_commit_is_visible_without_reload(共4);TA/ApprovalConstructorRaceTests.cs::External_revoke_between_snapshot_read_and_version_capture_is_not_lost | PASS |
| F-APPR-16 | 重启恢复=新建 store 或 Reload;Reload 无条件重读、失败保留旧快照 | SPEC.md:15,39 | A/ApprovalStore.cs:408-418 | TA/ApprovalReloadTests.cs::Failed_reload_keeps_the_previous_snapshot;TA/ApprovalRestartTests.cs::Disable_enable_and_revoke_blocks_persist_across_reopen_for_exact_hash_only | PASS |
| F-APPR-17 | Legacy 导入:同一 IMMEDIATE 事务;无标记+表空+JSON 存在;空键跳过;同键后者胜;标记权威;改名尽力;ImportLegacyJson=false 仍写标记 | SPEC.md:41 | A/ApprovalMigration.cs:25-105(EnsureSchema/ImportLegacyJson/WriteMarker);A/ApprovalStore.cs:508-516(TryMigrateLegacyFile) | TA/ApprovalMigrationTests.cs::Rewritten_stale_legacy_file_is_not_reimported(共4) | PASS |
| F-APPR-18 | fail-closed:垃圾字节/目录/高 schema/未知 status/坏 legacy JSON→InvalidDataException 含路径 | SPEC.md:45 | A/ApprovalStore.cs:97-106;A/ApprovalMigration.cs:37-40,95-101 | TA/ApprovalSchemaTests.cs::Garbage_database_file_fails_construction 等 4;TA/ApprovalStorePersistenceTests.cs::Corrupt_legacy_state_file_fails_construction_with_invalid_data | PASS |
| F-APPR-19 | 时间戳只接受规范 “O”(带 offset、7 位小数、逐字符相等);坏行消息含库路径/行/列;Reload/读/写路径同归类且不换快照、事务回滚 | SPEC.md:45 | A/ApprovalDatabase.cs:171-228(TimestampFormat+TryParseExact+FormatTimestamp 逐字符比对+Malformed) | TA/ApprovalTimestampTests.cs::Mutating_a_row_with_a_corrupt_timestamp_fails_without_changing_the_table(共3) | PASS |
| F-APPR-20 | 运行期写失败(只读/磁盘满/busy)以 SqliteException 上抛,内存不变 | SPEC.md:45 | A/ApprovalStore.cs:463-475(异常自然上抛,records 仅在 Commit 后更新) | TA/ApprovalFailureTests.cs::Write_to_a_read_only_database_throws_and_leaves_the_snapshot_unchanged | PASS |
| F-APPR-21 | 无 OperatingSystem.IsWindows 分支(旧平台分叉测试前提已移除) | SPEC.md:49 | grep src/Forge.Approval 无命中 | TA 无平台分叉用例(与规格一致) | PASS |
| F-APPR-22 | AOT 无反射;Legacy 导入走源生成 ApprovalJsonContext | SPEC.md:11 | A/ApprovalJsonContext.cs:14-16;A/Forge.Approval.csproj:3(IsAotCompatible);A/ApprovalMigration.cs:90(JsonSerializer.Deserialize 走 Context) | TA/ApprovalMigrationTests.cs(导入路径即源生成路径) | PASS |
| F-APPR-23 | Debug-only 测试接缝 AfterInitialSnapshotRead/SnapshotRead;InternalsVisibleTo 仅 DEBUG | SPEC.md:53 | A/ApprovalStoreOptions.cs:18-24(#if DEBUG);A/AssemblyInfo.cs:3-5;A/ApprovalStore.cs:65-68,77-80,499-502 | TA/ApprovalConstructorRaceTests.cs(用 AfterInitialSnapshotRead 注入外部 Revoke);TA/ApprovalDataVersionTests.cs::Snapshot_is_read_once_until_an_external_commit_or_reload(数整表读次数) | PASS |
| F-APPR-24 | 单属主被期望非必需:跨进程逐行事务安全,提交后对方 data_version 失效重读 | SPEC.md:47 | A/ApprovalStore.cs:450-493(行级事务+Refresh) | TA/ApprovalConcurrencyTests.cs::Two_processes_run_200_store_updates_each_with_exact_totals_and_no_busy(共4);TA/ApprovalCrashTests.cs::Killed_writer_leaves_last_commit_visible_and_the_store_writable | PASS |

### Forge.Workflows(17 项)

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-WF-01 | 只依赖 BCL+Forge.Plugin.Sdk;工具调用一律 IHostFacade.CallToolAsync;不持注册表/不审批/不落盘 | SPEC.md:13-15 | W/Forge.Workflows.csproj:25;W/WorkflowEngine.cs:459(_host.CallToolAsync);src/Forge.Plugin.Sdk/Tools.cs:39;grep W 无 File/Directory IO | TW/Support/RecordingHost.cs(全部测试经 IHostFacade 记录调用) | PASS |
| F-WF-02 | after 省略=依赖紧邻上一步;after:[]=无依赖 | SPEC.md:23 | W/WorkflowPlan.cs:189(隐式 [index-1]) | TW/WorkflowSchedulingTests.cs::Implicit_after_chains_each_step_to_the_previous_one;::Empty_after_list_makes_a_parallel_root | PASS |
| F-WF-03 | 绑定只能指向自己的传递依赖 | SPEC.md:23 | W/WorkflowPlan.cs:379-415(ValidateBindings:IsAncestor BFS,自绑定/并行均报 BindingOnNonDependency) | TW/WorkflowValidationTests.cs::Binding_to_a_parallel_step_is_rejected_and_the_message_suggests_after;::Binding_to_a_transitive_dependency_is_allowed | PASS |
| F-WF-04 | 指令只有 $ref/$literal;路径字段只有 content/isError/status/error/json | SPEC.md:24 | W/ArgTemplate.cs:247-259(未知指令报错);W/WorkflowPath.cs:171,335-347 | TW/WorkflowValidationTests.cs::Unknown_or_malformed_directive_is_reported;TW/WorkflowBindingTests.cs(12) | PASS |
| F-WF-05 | $input 根:不是步骤、不要求进 after;Input null/路径缺失→该步 Failed 不调工具;? 可选省略 | SPEC.md:25-29 | W/RunState.cs:79-88(TryResolveInput);W/WorkflowEngine.cs:451-454(解析失败即 Failed 返回) | TW/InputBindingTests.cs::Missing_path_without_optional_fails_the_step_and_skips_successors;::No_input_fails_the_binding_with_a_clear_error(共17) | PASS |
| F-WF-06 | $ 前缀必需,裸 input.x 当步骤名;无入参错误文本明确「run 没有 $input」 | SPEC.md:56-59 | W/RunState.cs:84("the run has no $input; RunOptions.Input was not supplied");W/WorkflowPath.cs:90-179 | TW/InputBindingTests.cs::Dollar_input_is_not_a_step_id_even_when_a_step_is_named_input | PASS |
| F-WF-07 | 就绪集调度取最小下标;完成递减后继依赖数,归零启动;顺序稳定 | SPEC.md:32-33 | W/WorkflowEngine.cs:141-147(SortedSet),167-175(ready.Min),205-218(仅 Succeeded 递减) | TW/WorkflowSchedulingTests.cs::Single_slot_run_starts_the_lowest_index_ready_step_first;::Single_slot_schedule_is_identical_across_repeated_runs | PASS |
| F-WF-08 | MaxConcurrency 默认 8;完成事件走无界 Channel<int>;取消写哨兵唤醒 | SPEC.md:33-34 | W/RunOptions.cs:9,15;W/WorkflowEngine.cs:130-131,158-160(ct.Register→TryWrite(Wake)) | TW/WorkflowSchedulingTests.cs::Fan_out_never_exceeds_max_concurrency_and_reaches_it | PASS |
| F-WF-09 | 失败只传播依赖方:工具报错/异常→Failed 不抛出;失败永不递减后继→Skipped;无关分支跑完;run Failed;onError 只有 fail | SPEC.md:35-37 | W/WorkflowEngine.cs:207-210,304-306,429-437,459-466;W/WorkflowDefinition.cs:52-56(StepErrorMode 仅 Fail) | TW/WorkflowFailureTests.cs::Failed_step_skips_its_transitive_dependents_and_independent_branches_still_run;::Failure_in_a_diamond_skips_the_join_but_not_the_unrelated_branch(共16) | PASS |
| F-WF-10 | 绑定解析失败记 Failed 且不调工具 | SPEC.md:35 | W/WorkflowEngine.cs:451-454(TryResolveArguments 失败先于 CallToolAsync 返回) | TW/WorkflowBindingTests.cs::Failed_binding_skips_the_steps_that_depend_on_it | PASS |
| F-WF-11 | 取消/超时只停调度;在飞收令牌;drain 默认 5s;不合作标 Cancelled 立即返回;被放弃步骤 RunState 延迟到任务结束释放 | SPEC.md:38-41 | W/RunOptions.cs:27;W/WorkflowEngine.cs:178-181(drain CTS),233-243(DisposeStateAsync),301-302 | TW/WorkflowFailureTests.cs::Tool_that_ignores_cancellation_is_abandoned_after_the_drain_timeout;::Timeout_cancels_the_in_flight_step_and_reports_timed_out | PASS |
| F-WF-12 | 时间统一 TimeProvider:时间戳/耗时/超时 CTS(delay,TimeProvider)/drain CTS | SPEC.md:42-44 | W/WorkflowEngine.cs:150-154,180,440-441(GetUtcNow/GetElapsedTime/GetTimestamp) | TW/WorkflowSchedulingTests.cs::Duration_and_timestamps_come_from_the_injected_clock(Support/ManualClock.cs 假时钟) | PASS |
| F-WF-13 | AOT 零反射 WorkflowJsonContext;无指令 args 原样 JsonElement 不重序列化;有指令 Utf8JsonWriter 写一次再解析 | SPEC.md:45-47 | W/Forge.Workflows.csproj:3;W/WorkflowJsonContext.cs:12-13;W/WorkflowEngine.cs:487-516(IsPassThrough 直传;否则 Write→Parse) | TW/WorkflowBindingTests.cs::Args_without_directives_reach_the_tool_as_the_original_json_text | PASS |
| F-WF-14 | steps 缺失由 Utf8JsonReader 扫描判定(不靠 Steps is null);注释/尾随逗号选项与 Context 一致 | SPEC.md:48-52 | W/WorkflowJson.cs:42-44,56-74(HasStepsProperty,Skip+AllowTrailingCommas) | TW/WorkflowParsingTests.cs::A_commented_document_without_steps_is_still_a_format_error | PASS |
| F-WF-15 | 校验不抛异常、确定顺序;Kahn 剥点定位环,按最小下标各报一次带路径 | SPEC.md:53-55 | W/WorkflowValidator.cs:10-14;W/WorkflowPlan.cs:242-325(DetectCycles:indegree+queue+TryFindCycle+渲染路径) | TW/WorkflowValidationTests.cs::Two_step_cycle_is_reported_once_with_its_path;::Validate_never_throws_for_an_in_memory_definition_full_of_errors(共24) | PASS |
| F-WF-16 | RunAsync 入口:无效定义→Invalid 不调工具;RunOptions 参数守卫 | SPEC.md:20-21 | W/WorkflowEngine.cs:43-63(MaxConcurrency≥1、Timeout>0、Drain≥0、Validate→InvalidResult) | TW/WorkflowValidationTests.cs::Run_of_an_invalid_in_memory_definition_reports_invalid_without_calling_tools;TW/WorkflowFailureTests.cs::Max_concurrency_below_one_is_rejected_before_any_call 等 3 | PASS |
| F-WF-17 | 格式与路径子集契约成文(README);解析错误带行/列/路径 | SPEC.md:21-22 | W/README.md:35-66(根/段语法/字段表);W/WorkflowJson.cs:39-128(WorkflowFormatException 带 $.steps 路径) | TW/WorkflowParsingTests.cs::Malformed_json_throws_format_exception_with_line_and_position(共13) | PASS |

## 非 PASS 项详情

### F-MCP-04(PARTIAL)— 取消通知的「已发出后」边界不符
- 证据:M/JsonRpcPeer.cs:66-71,88-91 在 `OperationCanceledException` 时无条件调 `SendCancellation`(仅排除 initialize 与已关闭,:297);不检查请求是否已发出。若调用方令牌在 `SendAsync` 完成前取消(或已预先取消,:191-200 的测试场景),`notifications/cancelled` 仍会为一个从未上线的 id 发出。规格 SPEC.md:24 写明「超时/取消**在已发出后**发」。
- 影响:轻微。MCP 接收端对未知 requestId 的取消通知按协议应忽略;不会造成错误结果,但与规格字面不一致,且该边界无测试(TM/Client/McpClientRequestTests.cs::Already_cancelled_token_fails_the_request_before_waiting_for_a_reply 只断言抛 OCE,未断言通知有无)。
- 建议处理类型:二选一——规格措辞改为「超时/取消即发(initialize 除外),未发出的请求其取消通知由服务端忽略」(改规格,低成本);或 peer 记录发送完成标志后再发通知(改实现+补测试)。主路径(发出后等待期超时/取消)行为正确且测试充分。

### F-MCP-10(PASS*)— 旧代际守卫无直接测试
- 证据:M/StreamableHttpTransport.cs:592-597 `IsCurrentGeneration` 在 DispatchPayload/PostStream 读循环(:331,339,350,421)处处检查;StartAsync 递增代际并 CancelPostStreams(:105,122)。实现核对无误。
- 缺口:TM/Http/StreamableHttpTransportTests.cs 无「同一传输 Start→旧流仍在投递→再 Start→旧消息不得完成新请求」用例;现有 Expired_session_404/Re_initialization 用例走的是新客户端。代际守卫是规格点名的安全属性(防串答),建议补一条脚本化旧代际注入测试。处理类型:补测试。

### F-MCP-13(PASS*)— 生命周期内不重绑定只有间接证据
- 证据:M/McpServerManager.cs:808-838 双字典永不删除(grep 无 `nameByIdentity.Remove`/`identityByName.Remove`),同一 (server,tool) 恒返回已分配名;结构上满足规格。
- 缺口:无「工具从清单消失后再出现/服务器移除后重加,名字仍不指向别的工具」的直接用例;现有命名测试只证明确定性与消歧。处理类型:补测试(低优先)。

### F-APPR-09(PASS*)— ListReadOnlyAsync 零测试
- 证据:A/ApprovalStore.cs:177-214 实现与规格 SPEC.md:29 一致(只读、Pooling=false、sqlite_master 探测、OrderByPlugin 排序、FileNotFoundException→空)。
- 缺口:全仓库 tests/ 无任何引用(grep 证实)。它是设置页 `approvals` 分区的唯一数据源(src/Forge.App/ViewModels/Settings/InstrumentSettingsSections.cs),承担「库不可读时说不可读」的 fail-closed 展示语义;排序、缺表返回空、坏库上抛三条分支均无保护。CountReadOnlyAsync 有 6 个测试而同类 API 没有,属明显不对称。处理类型:补测试(建议优先,参照 ApprovalCountReadOnlyTests 结构)。

## 已实现但缺测试清单

| 项 | 代码位置 | 缺的测试 |
|---|---|---|
| HTTP 代际守卫(旧代际消息不完成新请求) | M/StreamableHttpTransport.cs:592-597 | 同一传输重启后旧流消息注入 |
| 管理器名字生命周期不重绑定 | M/McpServerManager.cs:808-838 | 工具消失/服务器重加后名字归属 |
| ApprovalStore.ListReadOnlyAsync | A/ApprovalStore.cs:177-214 | 全部(排序/缺文件/缺表/坏库上抛) |
| 取消通知边界(未发出请求是否发 cancelled) | M/JsonRpcPeer.cs:66-71,295-319 | 预取消令牌下通知有无(配合 F-MCP-04 裁决) |

## 程序→规格:未记录行为

均为核对实现时发现、规格未写的规则/默认值/副作用(多数已有测试,不构成缺陷,但规格作为 ground truth 应补齐或明确放权):

Forge.Mcp:
1. `MaxListPages=100` 分页护栏(M/McpModels.cs:90;测试 ListToolsAsync_stops_at_max_list_pages...)。规格未提 tools/list 分页上限。
2. `RequestTimeout<=0` → 无限等待(M/JsonRpcPeer.cs:27-29)。规格未定义 0/负值语义。
3. 取消通知 fire-and-forget、独立 5s 发送期限、吞异常(M/JsonRpcPeer.cs:302-319)。
4. 客户端应答服务器发起的请求:ping→空结果、roots/list→按 options、其它→method-not-found(M/JsonRpcPeer.cs:252-288;McpClientHandshakeTests 覆盖)。规格「不支持清单」未说明这些是协议义务性应答。
5. 重连默认参数:`MaxReconnectDelay=30s`、`MaxReconnectAttempts=5`(M/McpServerState.cs:45,48)——两份规格均未记录(App/SPEC.md:395 只记初始 1s);`MaxResultChars=64K`(:50)规格点名却未给值。
6. stdio 关闭默认:`ShutdownTimeout=2s`、`KillTimeout=2s`、`StderrCapacityChars=8192`、stdout 污染环 Clamp(512..8192)(M/StdioTransport.cs:21-23,55-57,69)。
7. HTTP 默认:`MaxServerStreamRetries=3`、`SessionShutdownTimeout=5s`(M/StreamableHttpTransport.cs:20,26);405=无推送通道不重试(:测试 Server_stream_405...)。
8. 未知 content 块载荷 512 字符截断(M/McpContentFlattener.cs;测试 CallToolAsync_truncates_unknown_block_payloads_at_512_characters)。
9. `MaxResultChars<=0` → 不限(M/McpToolAdapter.cs:27)。
10. `McpToolRegistrySource`(IToolRegistry 适配器,M/McpToolRegistrySource.cs:9)有 3 个测试但**无生产消费方**(src 内除本模块外零引用,App 直接消费 manager)——规格未提及其存在与定位。
11. 管理器公开 API 面(ProbeAsync/ToolsOf/StartAllAsync/SetEnabledAsync/RestartAsync/RefreshToolsAsync,M/McpServerManager.cs:108,280,410,416,430)规格未枚举;Probe 仅在 App/SPEC.md:395 出现。
12. 能力解析含 ResourcesSubscribe/Completions 旗标(M/McpModels.cs:41,45)但无功能消费——纯信息性,与「不支持清单」不矛盾,规格可注明以免误读。

Forge.Approval:
13. Dispose 吞 checkpoint 的 SqliteException(A/ApprovalStore.cs:433-436)——规格只说「先 checkpoint 再关」,未说失败容忍。
14. ListReadOnlyAsync 捕获 FileNotFoundException→空(A/ApprovalStore.cs:209-212),覆盖 Exists 与 Open 之间的删除竞态;规格「文件不存在返回空」可涵盖但未写明竞态。
15. Upsert 的 CreatedAt/UpdatedAt 缺省规则( supplied 优先→沿用旧值→now,A/ApprovalStore.cs:220-246)——规格只说「签名与语义不变」,规则本身未成文(测试覆盖)。
16. `DefaultTimeout` 取 BusyTimeout **向上取整到秒**(A/ApprovalDatabase.cs:68),规格说「取同一个值」(:35),严格说是同值取整;「两者会叠加」的机理表述存疑(见规格质量 6)。

Forge.Workflows:
17. RunOptions 守卫的拒绝行为(MaxConcurrency<1、Timeout<=0、Drain<0 抛 ArgumentException,W/WorkflowEngine.cs:43-56)——规格未写,测试已覆盖。
18. args 省略/null → 空对象 `{}`(W/WorkflowEngine.cs:489-491)——README:22 已写,SPEC 未写(SPEC 把格式细节放权 README,可接受)。
19. 失败错误文本含异常类型名(W/WorkflowEngine.cs:431-436);drain 超时错误文本带秒数(:301-302)——对外可见字符串,规格未约定格式。

## 规格质量问题

1. **跨规格重复(MCP 管理)**:Forge.App/SPEC.md:395,397 与 Forge.Mcp/SPEC.md:18,34,36,42 就「文件写规则/差量应用/选项活读/选面打分缓存」近乎逐字重复。当前一致,但同一契约两处维护是漂移温床(例如重连默认值已经只出现在 App 侧)。建议:库契约归 Forge.Mcp,App 段只留接线与 UI 行为并引用。
2. **Approval 组合指纹段过时风险**:Forge.Approval/SPEC.md:21 与 A/Hashing.cs:23 的 `name@version` 拼接格式,在生产中已被 Forge.ExtensionHost 的 `DependencyHashSchema`(forge.dependency-fingerprint/v2,域分离+长度定帧,src/Forge.ExtensionHost/DependencyHashSchema.cs:33、PluginCatalog.cs:185)取代;ExtensionHost/SPEC.md:22 明言「Hashing 保持字节不变,Host 不再用其拼接格式」,但 Approval 规格只字未提——单读 Approval 规格会误以为生产依赖哈希仍是拼接格式。`Hashing.DependencyHash` 现为测试-only 死代码(生产零引用,grep 证实)。建议 Approval SPEC:21 加一句交叉引用。
3. **「已发出后」模糊**(Forge.Mcp/SPEC.md:24):实现无条件发取消通知(见 F-MCP-04),规格字面与实现边界不一致,需裁决改哪边。
4. **默认值缺写**(Forge.Mcp/SPEC.md:38):MaxResultChars 被点名为库默认值却不给值(实际 64K);MaxReconnectDelay=30s、MaxReconnectAttempts=5 两份规格均未记录。安全/容量相关默认值应成文。
5. **跨规格重复(审批)**:Forge.ExtensionHost/SPEC.md:21-23 与 Forge.Approval/SPEC.md:23,27 的状态机/封锁语义重复(当前一致);ExtensionHost 侧另加闸门/哈希重算/退回 Draft 规则,Approval 规格未引用。建议同 1,单一所有权。
6. **机理表述存疑+规格-测试耦合**(Forge.Approval/SPEC.md:35,43):「busy_timeout 与 DefaultTimeout 两者会叠加」未经验证(Microsoft.Data.Sqlite 的 DefaultTimeout 语义是命令超时/打开期 busy,实现按秒取整);:43 把具体测试方法名与 CI 实测毫秒数写进规格,测试改名即规格过时。建议弱化为「同一预算、两处生效」并移除方法名。
7. 小疵:Forge.Workflows/SPEC.md:23 「上一歩」为日文汉字(应为「步」);Forge.Mcp/SPEC.md:15 不支持清单与能力旗标解析的关系未注明(见未记录行为 12)。

## 业务闭环缺口

1. **审批只读旁路无保护(最实的缺口)**:设置页 approvals 分区 → `ListReadOnlyAsync` → 坏库应显示「不可读」。实现存在但零测试(F-APPR-09);排序/缺表/上抛语义一旦回归,用户看到的是静默错数据而非「不可读」。闭环其余环节(写→提交→快照→跨进程可见→重启恢复→并发)测试强:Two_processes_run_200_store_updates_each、External_revoke_between_snapshot_read_and_version_capture、Concurrent_reapproval_never_lifts_a_concurrent_disable、Killed_writer_leaves_last_commit_visible 均覆盖。
2. **工作流 revoke 不终止在途 run(已知、已记录、未闭环)**:Forge.ExtensionHost/SPEC.md:24 明言 revoke 不取消 Host 内已在途的 `WorkflowEngine.RunAsync`,run 可在 revoke 后继续经 host.tool_call 调主程序工具直到预算耗尽。引擎侧只有调用方令牌一条停止路径(W/WorkflowEngine.cs:156),Host 未把 revoke 接到该令牌——终止方式「待定义」。Workflows 规格未交叉引用此缺口。处理类型:跨模块设计决策(ExtensionHost 拥有),建议在 Workflows SPEC 未决段登记。
3. **MCP 重连耗尽后的恢复**:退避上限(默认 5 次)后服务器停在 Failed,仅手动 Restart/Probe 可恢复(M/McpServerManager.cs:921;测试 Reconnect_gives_up_after_max_attempts)。UI 有重连/测试按钮(App/SPEC.md:395),闭环成立;但「耗尽后是否提示用户」两份规格均未约定,属轻微规格空白。
4. **MCP 启动失败→诊断闭环**:Failed+LastError+stderr 尾(CaptureStderr,M/McpServerManager.cs:578-580)+Probe 带 stderr(测试 Probe_failure_carries_the_stderr_tail_of_the_server_state)——完整。进程清理(优雅关→杀→Dispose)有真实子进程测试。无缺口。
5. **工具审批闭环(范围边界说明)**:库侧 SideEffect 标记+ConfigOfTool 反查已验证;审批执行在 App/Core(App/SPEC.md:338),不在本次三模块范围内,未见库侧缺口。
6. **工具可达性闭环**:上限选面→隐藏→mcp_tool_search→pin→重发布,库侧 8 测试+App 侧 McpToolExposureTests 存在,「上限永不可达」保证有实现与测试支撑。无缺口。

## 测试质量注记(按 test-anti-patterns 技能)

- 名称级全量审查 506 个测试:未发现无断言/自指断言/吞异常式命名模式;断言目标均为具体行为(退出码、精确哈希、诊断码、调度顺序、字节级 base64 等)。
- 抽查:TW/WorkflowFailureTests.cs 的 `Task.Delay` 均为假工具内模拟工作/清理(:122,168,278,313 为 `Timeout.Infinite` 模拟不合作工具,:172 为 50ms 模拟清理),不是 sleep 同步;时间断言走 ManualClock 假时钟,符合技能对墙钟依赖的豁免。TA 跨进程压测按规格把子进程 BusyTimeout 提到 60s,避免把合法 busy 读成丢更新。
- 轻微风险:TW/WorkflowFailureTests.cs:172 的 50ms 真实等待在极端负载下可能偏紧(低);TM 的 Eventually 夹具为轮询式异步等待(可接受)。
- 局限:未逐行通读全部测试体(配额),以上为名称全量+关键用例抽查结论;「测试证据」列引用均为已核对存在的方法名。

## 汇总

- 功能点 77:MCP 36、Approval 24、Workflows 17。
- PASS 73,PASS* 3(F-MCP-10、F-MCP-13、F-APPR-09),PARTIAL 1(F-MCP-04),FAIL/MISSING/SPEC_GAP/UNVERIFIED 0。
- 三份规格与实现总体高度一致,安全关键路径(审批封锁/哈希失效/fail-closed、工作流失败传播/取消、MCP 退出码/传输回收)均有决定性代码与强测试证据;缺口集中在只读旁路测试缺失、代际守卫测试缺失与少量规格未记录的默认值。
