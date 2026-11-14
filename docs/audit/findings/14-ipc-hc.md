# Ipc/Ipc.Protocol/HostClient 审计发现

范围:`src/Forge.Ipc/SPEC.md`(36 行)、`src/Forge.HostClient/SPEC.md`(190 行);`Forge.Ipc.Protocol` 无独立 SPEC,按 Ipc SPEC:11 与 architecture.md:23 核对。只读审计,未运行 build/test。
证据缩写:`IT`=tests/Forge.Ipc.Tests,`HT`=tests/Forge.HostClient.Tests,`Integ`=tests/Forge.Integration.Tests(范围外,仅补充证据)。行号为审计时快照。
测试基建质量(按 test-anti-patterns 技能):FakeHost 是真命名管道服务端,等待用事件日志/TCS 而非 sleep;`Task.Delay` 仅模拟 Host 延迟或 `Timeout.Infinite`+ct;断言具体(拒绝文案、调用计数、帧字节)。未发现 Critical/High 反模式;IT/NamedPipeFramingTests.cs:230,268 的 `WhenAny+Delay(2s)` 后随显式断言,可接受。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-IPC-01 | 命名管道(Unix 域套接字)+4 字节小端长度前缀+MessagePack v4 源生成 DefaultAot;只依赖 BCL+MessagePack,AOT | Ipc:11 | FrameCodec.cs:30;IpcMessages.cs:84;NamedPipeTransports.cs:120-168;Forge.Ipc.csproj:3,10 | IT/FrameCodecTests::Encode_prefixes_the_body_with_its_little_endian_length;IT/NamedPipeTransportTests::Connect_round_trip_and_clean_close_over_a_real_pipe | PASS |
| F-IPC-02 | 一个传输实例一条连接,重连即新传输;传输管帧边界、peer 管关联 | Ipc:15 | NamedPipeTransports.cs:46-50(复用抛 InvalidOperationException) | 复用拒绝:无;分工由 IT/IpcPeerRequestTests、NamedPipeFramingTests 全套间接证明 | PASS* |
| F-IPC-03 | 帧体=IpcHeader(Version/Id/Operation/IsResponse/Error)+至多一个负载;失败响应只有头;版本 2 | Ipc:16 | IpcMessages.cs:9,36-52;FrameCodec.cs:74-89,100-133 | IT/FrameCodecTests::Request_round_trips_header_and_payload、Failure_round_trips_code_message_and_data_without_a_payload;IT/IpcMessagesTests::Request_header_sets_version_id_and_operation | PASS |
| F-IPC-04 | 负载保持编码、Read<T>() 一次解码、无 DOM;IpcEmpty;工具参数原始 UTF-8 只在工具端解析 | Ipc:17 | IpcMessages.cs:24-29,76,90-110;Ipc.Protocol/HostMessages.cs:8-10,20;HostToolCalls.cs:50-54;HostClient/ToolArguments.cs:13-37 | IT/FrameCodecTests::Read_rejects_a_payload_of_the_wrong_type、Byte_payloads_travel_as_binary_and_stay_exact;HT/HostToolCallRelayTests::Arguments_are_handed_to_the_tool_verbatim_without_normalization | PASS |
| F-IPC-05 | EncodeRaw/RequestRawAsync internal 仅测试程序集可见;产品走类型化 API | Ipc:18 | FrameCodec.cs:50;IpcPeerImpl.cs:71-72;Forge.Ipc.csproj:7(IVT 仅 Integ) | Integ/Wire.cs:73(范围外);HT 经 FakeHost 原始帧等效:HT/HostToolCallRunBindingTests::Blank_or_non_string_runId_is_refused_before_identity_checks | PASS |
| F-IPC-06 | 发送写池化 IpcFrameBuffer、传输 SendAsync 完成前复制;入站缓冲交给消费者后不再复用、切片可引用 | Ipc:19 | FrameCodec.cs:149-213;PipeSession.cs:81-131,296-327(所有权判定漏洞见 D-1) | IT/FrameCodecTests::Frame_buffer_dispose_is_idempotent_and_makes_the_buffer_unusable 等 5 个;IT/NamedPipeFramingTests::Frames_queued_before_disconnect_are_delivered_before_closed | PARTIAL |
| F-IPC-07 | ValidatePipeName 108 仅名称上限;MaxNameLength=平台套接字路径上限-临时目录-CoreFxPipe_-NUL,失败保守 16,Windows 108 | Ipc:20 | IpcTransport.cs:37-84 | IT/NamedPipeTransportTests::Max_name_length_is_the_longest_name_a_real_pipe_accepts、Validate_pipe_name_rejects_names_longer_than_108_characters | PASS |
| F-IPC-08 | deadline 发送前创建、覆盖发送与等待;id 严格关联、不匹配忽略;关闭使全部在途失败;ping 自动应答 | Ipc:24 | IpcPeerImpl.cs:84-91,312-317,435-445,495-527 | IT/IpcPeerRequestTests::Sixty_concurrent_requests_each_receive_their_own_result_when_answered_in_reverse、Response_with_an_unknown_id_is_ignored_and_the_pending_request_still_completes;IT/IpcPeerLifecycleTests::Transport_failure_faults_every_pending_request_with_connection_closed;IT/IpcPeerDispatchTests::Ping_is_answered_with_ipc_empty_and_never_reaches_handlers | PASS |
| F-IPC-09 | 错误分层 Protocol/Remote/Timeout/ConnectionClosed/VersionMismatch(拒帧、连接保持) | Ipc:26 | IpcTransport.cs:122-133;IpcMessages.cs:113-127;IpcPeerImpl.cs:288-310 | IT/IpcPeerDispatchTests::Response_with_a_foreign_version_faults_its_request_once_and_the_connection_stays_usable、Request_with_a_foreign_version_is_refused_with_version_mismatch_and_not_dispatched、Notification_with_a_foreign_version_is_dropped_without_a_reply | PASS |
| F-IPC-10 | 丢弃帧可见:transport/peer ProtocolError 每丢帧各报一次(零长/超限/头解码失败),连接保持 | Ipc:28 | PipeSession.cs:197-216;IpcPeerImpl.cs:236-247 | IT/NamedPipeFramingTests::Zero_length_frame_raises_a_protocol_error_and_the_next_frame_is_delivered;IT/IpcPeerDispatchTests::Malformed_frames_raise_a_protocol_error_and_the_next_valid_request_is_dispatched、Empty_frame_raises_a_protocol_error_and_the_next_valid_request_is_dispatched | PASS |
| F-IPC-11 | 放弃的连接必须真的停下:ConnectTimeout 到期用链接令牌取消底层连接并处置流 | Ipc:30 | NamedPipeTransports.cs:147-168 | 无(客户端连接超时无任何测试;仅服务端 IT/NamedPipeTransportTests::Server_without_a_client_times_out_with_timeout_exception) | PASS* |
| F-IPC-12 | 单帧 16MiB;零长/超限读侧拒绝跳帧重同步,超 64MiB 才关连接;写侧拒绝不发送 | Ipc:32 | IpcMessages.cs:12;FrameExtractor.cs:62-79;PipeSession.cs:96-103,204-216;IpcPipeOptions.cs:26 | IT/NamedPipeFramingTests::Oversized_write_is_rejected_before_any_bytes_are_sent、Frame_declaring_more_than_the_resynchronize_cap_closes_the_connection、Frame_declaring_exactly_the_resynchronize_cap_is_skipped、Negative_length_header_closes_the_connection;IT/FrameCodecTests::Decode_accepts_a_body_exactly_at_the_limit | PASS |
| F-IPC-13 | Ipc.Protocol:宿主内部类型化负载,依赖 Ipc+Plugin.Sdk,不属 SDK,AOT | Ipc:11;arch:23 | Ipc.Protocol/Forge.Ipc.Protocol.csproj:3,9-10;HostMessages.cs;TerminalMessages.cs;HostToolCalls.cs | 形状经 HT 全套间接验证(HT/Terminal/TerminalStartProtocolTests::Start_sends_required_fields_with_values 等);无专属测试项目 | PASS |
| F-IPC-14 | HostToolCalls.CallAsync:非调用方取消的失败映射错误结果(denied 标签),取消原样抛;ArgumentsUtf8 原始字节、空={} | arch:23 | Ipc.Protocol/HostToolCalls.cs:14-54 | CallAsync 映射:范围内无(消费者是 Worker/执行器);ArgumentsUtf8 部分:HT/HostToolCallRelayTests::Missing_arguments_default_to_an_empty_object(:392) | PASS* |
| F-HC-01 | 唯一入口 IExtensionHostClient;同时实现 IExtensionContributionClient(快照/命令/提示词/run_code) | HC:10-13 | ExtensionHostClient.cs:17;IExtensionHostClient.cs:31;IExtensionContributionClient.cs:16 | HT 全套(经 HostClientHarness) | PASS |
| F-HC-02 | 只依赖 Abstractions/Ipc/Ipc.Protocol/Plugin.Sdk;AOT 源生成;不另定义线上 DTO;ToolArguments 一次 UTF-8+Utf8JsonReader 不建 DOM;无 Roslyn/ExtensionHost | HC:17 | Forge.HostClient.csproj:6-9;ToolArguments.cs:13-37 | HT/ExtensionHostToolProxyTests::Malformed_arguments_are_rejected_locally_without_any_request、Blank_arguments_are_sent_as_the_empty_payload | PASS |
| F-HC-03 | ITool/命令代理;参数本地校验,无效不发 IPC | HC:18 | ExtensionHostTool.cs:39-46;ExtensionHostCommand.cs:30-35;ExtensionHostClient.cs:514-519 | HT/ExtensionHostToolProxyTests::Invoke_sends_plugin_execute_with_parsed_arguments_and_the_call_context;HT/ExtensionHostCommandTests::Denied_approval_returns_an_error_and_sends_nothing | PASS |
| F-HC-04 | 不读审批数据库、无 SQLite;状态/待审批经 plugins.status 与 extensions.pending | HC:19 | Forge.HostClient.csproj(无 SQLite);ExtensionHostClient.cs:200-228;Authoring.cs:41-54 | HT/PluginStatusPromptTests::Statuses_decode_permissions_and_survive_optional_fields;HT/ExtensionAuthoringPayloadTests::Pending_sends_an_empty_payload_and_decodes_both_rows | PASS |
| F-HC-05 | 自启或外部 Host,绑定/上下文规则完全一致 | HC:20 | ExtensionHostClient.cs:1822-1998(不区分启动方式),343-433(launchesHost 只管进程) | 绑定测试全部经外部 FakeHost(HT/HostClientRootContextTests 等);自启路径 HT/ExtensionHostClientLifecycleTests::Start_names_a_host_that_exited_before_it_listened_with_its_exit_code | PASS |
| F-HC-06 | host.tool_call fail-closed:{runId,pluginId,name,args};旧式无绑定拒绝;缺 runId 解码拒绝;runId 命中本世代 pending 且身份相等;会话/目录/深度/链只取 pending;未知/结束/取消/超时/重放无效;HostToolWorkingDirectory 保留不读 | HC:24-31 | ExtensionHostClient.cs:1605-1660,1782-1820,1930-1964;Options.cs:27 | HT/HostToolCallRunBindingTests::Missing_runId_is_refused_and_neither_resolver_nor_approval_runs、Forged_pluginId_is_refused_and_neither_resolver_nor_approval_runs、Replay_after_completion_is_refused_and_the_resolver_never_runs_again、Callback_from_an_earlier_connection_after_reconnect_is_refused;HT/HostToolCallRelayTests::Unsolicited_call_with_only_legacy_fields_is_refused_as_unbound、The_legacy_options_fallback_is_never_consulted、Malformed_bound_payloads_are_rejected_before_resolution | PASS |
| F-HC-07 | IHostToolCallRouter 按会话路由:sessionId 只取 run.Context.SessionId;命令审批用有效上下文会话;未知会话 fail-closed;旧委托构造保留、路由器优先;run_code 用该次注册表但审批走路由器 | HC:32-36 | ExtensionHostClient.cs:1639-1660,1586-1596,107-115,715-720;IHostToolCallRouter.cs:12-18 | HT/HostToolCallRouterTests::The_router_receives_the_session_the_run_was_registered_with_not_a_forged_one、A_router_denial_means_the_tool_is_not_invoked;HT/HostToolCallRelayTests::A_resolver_without_an_approval_path_fails_closed_and_never_invokes_the_tool | PASS |
| F-HC-08 | 私有 AsyncLocal 可信链:128 位加密随机 runId、世代绑定、原始 ToolContext、完整链、自身取消、每运行门;嵌套新 runId+父链+主程序推算深度;环与 >8 环本地拒绝;父结束子随取消;结束/拆除作废 pending | HC:37-41 | ExtensionHostClient.cs:44,21,1822-1881(GetHexString(32)),1966-1998,2000-2014,1741-1757;ExtensionRuns.cs:83-159 | HT/HostToolCallRecursionTests::Plugin_A_to_B_is_allowed_with_the_trusted_session_chain_depth_and_fresh_ids、Plugin_B_back_to_A_is_refused_locally_without_IPC;HT/HostToolCallRunBindingTests::Replay_after_completion_is_refused_and_the_resolver_never_runs_again;8 环上限分支无直接测试 | PASS |
| F-HC-09 | RootRunId:顶层=自身 runId、嵌套从父 pending 复制;四类 WireContext 携带 rootRunId;独立执行不同根;A→B 沿用 A 根 | HC:42-47 | ExtensionHostClient.cs:1862,1996,2074-2079;ExtensionRuns.cs:52-55 | HT/RootInvocationIdentityTests::Top_level_plugin_execute_carries_its_own_runId_as_rootRunId(四类各一)、Nested_plugin_call_keeps_the_parent_root_with_a_fresh_runId、Independent_same_plugin_roots_have_different_roots、Forged_rootRunId_in_the_callback_is_ignored | PASS |
| F-HC-10 | 四入口在 BeginRun/审批/IPC 前校验有效上下文目录非空白且完全限定;EffectiveContext=ParentContext??context;不规范化相对路径;拒绝形状按入口固定;命令目录校验先于审批 | HC:48-55 | ExtensionHostClient.cs:2060-2072,527-533,625-631,706-713,269-276 | HT/HostClientRootContextTests::Plugin_tool_with_an_invalid_root_is_rejected_without_ipc_or_approval(四入口 Theory)、Plugin_tool_sends_a_nonexistent_absolute_root_verbatim;HT/ExtensionHostCommandTests::Relative_working_directory_is_rejected_locally | PASS |
| F-HC-11 | run_code 只在顶层:Ancestors 非空即本地拒绝;host.tool_call name==run_code 在 pending/resolver/审批前拒绝 | HC:56-60 | ExtensionHostClient.cs:618-622,1614-1619 | HT/HostToolCallRecursionTests::Plugin_callback_to_run_code_is_refused_without_IPC、Script_callback_naming_run_code_on_the_wire_is_refused_before_resolver_and_approval | PASS |
| F-HC-12 | 审批不延长 run 生命周期:Allow 后、调用前复查链接令牌与 IsActive;已结束回错误、连接级取消直接返回;都不调用工具 | HC:61-64 | ExtensionHostClient.cs:1727-1738 | HT/HostToolCallRunBindingTests::Parent_completion_while_approval_ignores_cancellation_then_allows_does_not_invoke_the_target;HT/HostToolCallConcurrencyTests::Canceled_run_cannot_start_a_tool_after_approval | PASS |
| F-HC-13 | run 资源确定性释放:租约计数、TryLease 前置、End() 先 ended 再 Cancel、取消返回且最后租约归还才 Dispose;Token 构造时捕获;每 run 处置 linked source | HC:65-72 | ExtensionRuns.cs:15,91-159;ExtensionHostClient.cs:1830-1837,1883-1908 | HT/HostToolCallConcurrencyTests::The_per_run_gate_caps_concurrent_callbacks_at_one、Independent_roots_do_not_share_the_callback_cap;HT/HostToolCallRunBindingTests::Sibling_runs_keep_their_original_contexts_isolated;ODE 竞争无专项压力测试 | PASS |
| F-HC-14 | 失败回执有界不打断事件循环:拒绝/丢弃/不可解码→Diagnostics(16)+错误结果;plugins.status 例外先诊断再抛;子调用按 MaxInnerResultChars、脚本按 MaxResultChars 截断;每运行并发门 | HC:73-77 | ExtensionHostClient.cs:22,2268-2282,1762;RunCodeProxyTool.cs:404-423;ExtensionRuns.cs:44 | HT/HostToolCallConcurrencyTests::Callback_content_is_bounded_and_marked、Outer_code_output_is_bounded_and_marked;HT/ContributionSnapshotTests::Malformed_complete_snapshot_is_atomically_refused_and_keeps_the_previous_state;16 条淘汰无直接测试 | PASS |
| F-HC-15 | 每调用一个预算:peer 默认无限;RequestTimeout 默认 120s;run_code 用脚本 timeout(60/600)覆盖排队+执行;到期→IpcTimeoutException;run_code 转"脚本已停止"并作废 run | HC:78-82 | ExtensionHostClient.cs:1023-1027,2032-2055,660-668;IExtensionContributionClient.cs:10-11 | HT/RunCodeProxyTests::Per_call_script_timeout_may_exceed_a_small_default_RequestTimeout、Expired_script_call_fails_and_the_next_call_recovers、Default_timeout_is_60s_and_values_above_600s_clamp;HT/ExtensionHostToolProxyTests::Host_that_never_answers_times_out_with_IpcTimeoutException_and_the_connection_stays_usable | PASS |
| F-HC-16 | 超时只结束该调用:移除 pending、发取消通知、抛 IpcTimeoutException;peer/世代/Status/快照不变;只有真关闭才 Failed;迟到应答作废 | HC:83-88 | ExtensionHostClient.cs:2032-2050;IpcPeerImpl.cs:127-141,459-478(通知名实为 "cancel",见 SQ-2) | IT/IpcPeerRequestTests::Connection_stays_usable_after_a_timeout_and_a_late_response_is_ignored、Timeout_throws_timeout_exception_and_sends_cancel_carrying_the_request_id;HT/PluginStatusPromptTests::Status_timeout_throws_IpcTimeoutException_and_keeps_the_connection | PASS |
| F-HC-17 | StartTimeout 报 Host 状态:先"没在 N ms 接受连接",再进程状态(运行带 pid/退出带码)+输出尾部;外部 Host 两段为空;放弃尝试先取消再处置 | HC:89-93 | ExtensionHostClient.cs:920-1016(重试+attempt.CancelAsync),989-1003;HostProcess.cs:122-137 | HT/ExtensionHostClientLifecycleTests::Start_timeout_reports_the_timeout_as_the_failure_detail、Start_timeout_names_a_host_that_is_still_running_instead_of_reporting_a_crash、Start_names_a_host_that_exited_before_it_listened_with_its_exit_code;放弃尝试取消无直接测试(同 F-IPC-11) | PASS |
| F-HC-18 | 崩溃保留快照、同实例重连:Failed 保留 Tools/贡献、StopAsync 清空;重连先拆旧连接(不发 host.stop、不清快照、waitForSelfExit:false 杀旧进程树);世代标签;Running 只丢更早世代(有丢弃才发事件);退役前移+作废 pending;旧世代 Closed 不改状态;自动重启属组合根 | HC:94-105 | ExtensionHostClient.cs:343-433,835-918,1038-1067,1344-1410,1224-1228 | HT/ExtensionHostClientCrashTests::A_host_crash_marks_the_client_Failed_with_a_detail_once_and_keeps_the_tool_snapshot、StartAsync_after_a_crash_recovers_on_the_same_instance_and_drops_the_stale_snapshot;HT/ExtensionHostClientRecoveryTests::Recovery_keeps_a_registration_from_the_connect_to_Running_window_and_drops_what_the_new_host_omits、Recovery_transition_drops_every_stale_entry_with_exactly_one_ToolsChanged、Recovery_ends_the_previous_generations_host_process_and_StopAsync_leaves_no_process_behind | PASS |
| F-HC-19 | Running 提交与关闭回调同一把 statusGate,锁内复核世代/未关/Starting;窗口内关闭→Failed+原因、StartAsync 抛 IpcConnectionClosedException、可再启动;一次启动至多一次 Failed | HC:106-110 | ExtensionHostClient.cs:2163-2215,1044-1067 | HT/ExtensionHostClientRecoveryTests::A_connection_that_closes_before_the_Running_commit_fails_the_start_and_stays_startable、A_start_that_times_out_reports_exactly_one_Failed_status | PASS |
| F-HC-20 | 先等自退超时才杀:host.stop 等应答→ProcessExitTimeout 自退→超时杀树;无应答立即清理;HostExitCode 可观测(自启离开给码、运行中/外部 null、每次 Start 重置),仅具体类 | HC:111-118 | ExtensionHostClient.cs:848-916,169-178;HostProcess.cs:139-197 | HT/HostStopHandlingTests::Acknowledged_stop_waits_the_exit_timeout_before_killing_a_host_that_stays_alive、Unanswered_stop_kills_the_host_without_waiting_for_self_exit;退出码正值断言仅 Integ/StopLifecycleTests:73、Integ/Terminal/TerminalClientTests:64(范围外) | PASS |
| F-HC-21 | 贡献世代化整数快照:整体替换;unregister 移除该插件全部非工具贡献;内容相同只更新世代不发事件;原子不可变视图;跨插件名按 ordinal plugin id 先到先得+诊断;畸形逐条丢弃;整集不可解码不更新不抛;$run_code 保留;panels 只记录 | HC:119-125 | ExtensionHostClient.cs:1254-1342,1502-1573;ContributionSnapshots.cs:29-146 | HT/ContributionSnapshotTests::Replace_removes_what_the_new_snapshot_omits、Same_set_registration_updates_the_generation_without_an_event_or_new_proxies、Command_name_clash_is_deterministic_first_plugin_id_wins_with_a_diagnostic、Reserved_run_code_identity_cannot_register_tools_or_contributions、Panels_are_acknowledged_but_not_registered_as_runnable_views | PASS |
| F-HC-22 | Agent 严格转换(枚举/数值不夹取);工作流原样 Markdown;提示词只在 GetPluginPromptAsync 求值 | HC:126-128 | ContributionSnapshots.cs:148-225;ExtensionHostClient.cs:230-341 | HT/ContributionSnapshotTests::Unknown_agent_thinking_is_dropped_with_a_diagnostic_not_coerced;HT/PluginStatusPromptTests::Prompt_is_evaluated_with_the_bound_context | PASS |
| F-HC-23 | 命令/提示词代理走审批与绑定上下文:plugin.command 以 Execute 审批,被拒/无路径错误且不发 IPC;plugin.prompt 带绑定上下文;随机 runId+自身预算;回调按 pending 规则;无同步阻塞 IPC | HC:129-132 | ExtensionHostClient.cs:689-817,230-341 | HT/ExtensionHostCommandTests::Approval_uses_Execute_and_the_working_directory_then_the_command_runs_bound、Missing_approval_path_fails_closed_without_IPC、Command_callbacks_run_against_the_command_root_with_trusted_depth;HT/PluginStatusPromptTests::Prompt_callbacks_run_against_the_prompt_root | PASS |
| F-HC-24 | 提示词软失败只覆盖可解释失败(未注册/空白 id/未运行/环/目录无效/Remote/超时/IOException 含断连),无兜底 catch;调用方 OCE 原样传播 | HC:133-136 | ExtensionHostClient.cs:230-341(catch 仅 Remote/Timeout/IOException) | HT/PromptDisconnectTests::Mid_flight_transport_drop_returns_null_with_a_diagnostic、Caller_cancellation_still_throws;HT/PluginStatusPromptTests::Unregistered_prompt_returns_null_and_sends_nothing、Prompt_host_error_returns_null_with_a_diagnostic | PASS |
| F-HC-25 | run_code 显式代理:CreateRunCodeTool 返回 AOT ITool 不静默入插件列表;描述/schema 由注册表稳定生成;本地校验 code/timeout_seconds(必填非空≤MaxScriptChars;正有限数;60/600;先夹取再转 TimeSpan);加密 runId+上下文入 CodeExecutionRequest;编译/执行留 Host;每子调用审批、每运行门、main 侧截断;超时作废 run | HC:137-143 | ExtensionHostClient.cs:189-195,597-687;RunCodeProxyTool.cs:36-152;IExtensionContributionClient.cs:8-13 | HT/RunCodeProxyTests::Invalid_arguments_are_rejected_locally_without_any_request、Oversize_script_is_rejected_locally、Finite_huge_timeout_clamps_to_the_maximum_without_overflow、Code_execute_carries_the_context_tools_and_options | PASS |
| F-HC-26 | 可调用面单推导点:Snapshot 一次读四字段;描述/指纹/declarations 同源;代理按类型在读 Definition 前剔除(防栈溢出),再按名排除其它 run_code;内容哈希指纹(等值不重建、代理增删不重建);ordinal 排序 | HC:144-152 | RunCodeProxyTool.cs:36-54,170-232 | HT/RunCodeProxyTests::Description_is_stable_across_tool_registration_order、Own_run_code_tool_is_excluded_from_the_description_and_declarations;HT/RunCodeLiveRegistryTests::Proxy_inside_its_own_live_registry_survives_definition_and_invoke、Live_registry_child_scenario(子进程验证) | PASS |
| F-HC-27 | plugins.status 只读 IPC 无回落:未运行/断连抛 InvalidOperationException/IpcConnectionClosedException;取消原样;超时 IpcTimeoutException+诊断;不可解码 InvalidDataException 带 op 名;只有真解码空列表才返回空 | HC:153-158 | ExtensionHostClient.cs:200-228,2121-2140 | HT/PluginStatusPromptTests::Disconnect_during_status_propagates_as_IpcConnectionClosedException、Caller_cancellation_of_status_propagates_as_OperationCanceledException、Valid_empty_registry_returns_empty_and_is_distinct_from_unknown、Malformed_status_response_throws_InvalidDataException_and_keeps_the_connection | PASS |
| F-HC-28 | 状态逐行校验任一行坏整份无效:nil 项/空白 id/空白 status/permissions 非数组/未定义 kind/空白 scope/非 invalid 缺元数据;消息带下标+身份;invalid 行豁免;status 非空 token 透传 | HC:159-166 | HostAnswerRules.cs:31-122 | HT/PluginStatusRowValidationTests::Malformed_second_row_with_known_identity_rejects_the_whole_answer、Unknown_nonblank_status_token_is_forwarded_verbatim、Invalid_row_without_approval_metadata_remains_visible_with_its_error;HT/PluginStatusPromptTests::Malformed_entry_mixed_with_valid_rows_throws_without_a_partial_result | PASS |
| F-HC-29 | 解码即形状校验:非可空 nil/类型不符整份拒绝;行规则只查解码器不管的;plugins.status 与 commit 内嵌 status 共用行校验(标签区分) | HC:167-170 | IpcMessages.cs:90-110;HostAnswerRules.cs:57-122;Authoring.cs:400-445 | HT/PluginStatusRowValidationTests::Undecodable_second_row_rejects_the_whole_answer;HT/ExtensionAuthoringResponseTests::Commit_rejects_a_status_with_a_nil_member_as_undecodable;HT/HostToolCallRunBindingTests::Blank_or_non_string_runId_is_refused_before_identity_checks(nil/短数组) | PASS |
| F-HC-30 | 授权写入七方法:本地校验(空白 id/version/哈希、未知 kind、null 集合/项→ArgumentException 不发 IPC);stage/commit 预算 max(RequestTimeout,5min)、未配无 deadline;应答逐字段校验→诊断+InvalidDataException;StagedExtension/pending/commit 规则;Remote 与取消原样;超时先诊断再抛 | HC:171-181 | Authoring.cs:24-130,132-191,193-445 | HT/ExtensionAuthoringPayloadTests::Stage_rejects_null_collections_without_ipc;HT/ExtensionAuthoringResilienceTests::Compile_budget_defaults_to_the_five_minute_floor、Stage_without_a_request_timeout_has_no_deadline、Disconnect_during_stage_propagates_as_IpcConnectionClosedException;HT/ExtensionAuthoringResponseTests::Stage_with_diagnostics_accepts_missing_hashes_as_empty_strings、Pending_with_a_broken_second_row_rejects_without_a_partial_list | PASS |
| F-HC-31 | 终端启动:非 Running 抛 InvalidOperationException;start 不绑调用方 ct;取消→调用方立即 OCE,客户端继续等应答,已建终端清早到帧并补发 terminal.stop | HC:183 | Terminal.cs:49-106,108-149 | HT/Terminal/TerminalStartCancellationTests::Cancelled_start_throws_then_a_late_answer_makes_the_client_stop_that_terminal_and_drop_its_early_frames、Cancellation_that_races_with_the_answer_still_stops_the_terminal_the_host_built;HT/Terminal/TerminalStartProtocolTests::Start_while_stopped_throws_and_sends_nothing | PASS |
| F-HC-32 | 早到帧按 id 缓冲(总量 1MiB),登记表锁内回放,不丢不乱 | HC:184 | Terminal.cs:19,186-237,407-446 | HT/Terminal/TerminalOutputTests::Output_before_the_start_answer_is_buffered_and_replayed_in_order、Early_exit_without_output_still_reports_the_exit_code | PASS |
| F-HC-33 | Output 自写 ChannelReader,TryRead 累加,攒够 64KiB 或读空发 ack;队列受 Host 窗口约束;校验 seq 连续 | HC:185 | HostTerminalSession.cs:23,96-123,311-330,366-399 | HT/Terminal/TerminalOutputTests::Output_is_not_acknowledged_before_the_consumer_reads_it、Consuming_5x16KiB_acks_the_total_in_frames_within_64KiB、Skipped_sequence_faults_output_and_exited_with_a_protocol_error | PASS |
| F-HC-34 | WriteAsync 切 ≤64KiB terminal.input 串行发送 | HC:186 | HostTerminalSession.cs:157-215 | HT/Terminal/TerminalInputTests::Write_over_64KiB_splits_into_bounded_frames_that_concatenate_exactly、Concurrent_writes_keep_each_notification_homogeneous_and_ordered | PASS |
| F-HC-35 | 退出后写/缩放忽略、foreground false(业务错误/超时也 false);断连 Output 以 IpcConnectionClosedException 完成、Exited 以它失败 | HC:187 | HostTerminalSession.cs:125-155,230-269;Terminal.cs:452-475 | HT/Terminal/TerminalSessionLifecycleTests::Exit_completes_output_and_reports_the_code_then_ignores_late_calls、Foreground_returns_false_on_timeout_and_records_a_diagnostic;HT/Terminal/TerminalConnectionTests::Dropped_connection_faults_output_and_exited_with_a_connection_error、Reconnect_retires_old_sessions_and_leaves_the_new_generation_healthy | PASS |
| F-HC-36 | DisposeAsync 发 terminal.stop 限时 5s;超时/失败写带 terminalId 诊断,本地收尾 Exited=-1 | HC:188 | HostTerminalSession.cs:30,271-309 | HT/Terminal/TerminalSessionLifecycleTests::Dispose_sends_stop_with_the_terminal_id_and_reports_the_host_exit_code、Dispose_without_a_host_exit_falls_back_to_minus_one_with_normal_output_completion | PASS |
| F-HC-37 | 已知限制:peer 仅每 peer 默认超时、无 per-call 重载;客户端设无限并自建 deadline | HC:189-190 | IpcPeer.cs:42-46;ExtensionHostClient.cs:1023-1027,2032-2050 | HT/RunCodeProxyTests::Per_call_script_timeout_may_exceed_a_small_default_RequestTimeout;HT/ExtensionAuthoringResilienceTests::Stage_survives_a_compile_delay_longer_than_RequestTimeout | PASS |

注:代码路径省略 src/ 前缀;ExtensionHostClient.*.cs 属 src/Forge.HostClient,Ipc 各文件属 src/Forge.Ipc。Ipc=src/Forge.Ipc/SPEC.md,HC=src/Forge.HostClient/SPEC.md,arch=architecture.md。
计数:51 项 = 47 PASS + 3 PASS*(F-IPC-02/11/14)+ 1 PARTIAL(F-IPC-06)。无 FAIL/MISSING/SPEC_GAP/UNVERIFIED 项。

## 非 PASS 项详情

### D-1(F-IPC-06,PARTIAL)入站零拷贝所有权判定有边界漏洞,可能把仍被管道复用的池数组交给消费者
- 证据链:FrameExtractor.TryExtract(src/Forge.Ipc/FrameExtractor.cs:85-87)单段帧体直接取 `slice.First`(指向 PipeReader 内部内存),多段才 ToArray。PipeSession.EnqueueAsync(src/Forge.Ipc/PipeSession.cs:304-312)以 `TryGetArray && Offset==0 && Count==owned.Length` 判"整数组所有权"免拷贝——本意识别 Extractor 已 ToArray 的新数组,但无法区分"恰好占满整个池数组的管道段"。
- 触发前提:4 字节前缀恰结束于一个管道段末尾,帧体从下一段偏移 0 开始且长度恰等于该段底层数组长度(StreamPipeReader 租自 ArrayPool,典型 64KiB)。此时免拷贝,随后 `reader.AdvanceTo`(PipeSession.cs:222)使该段被 BCL 归还池并复用——消费者持有的"不再复用"缓冲会被改写,违反 Ipc SPEC:19 不变量,可致负载静默损坏。
- 定性:机制推断成立、前提苛刻(精确尺寸/分段对齐),审计禁止运行故未复现;现有测试不覆盖该对齐。宁可标缺陷:PARTIAL。
- 建议:代码修复——单段帧体一律 ToArray,或由 Extractor 显式传递所有权标志;补精确对齐回归测试。

### D-2(F-IPC-11,PASS*)放弃连接的取消无回归测试
- 实现:NamedPipeTransports.cs:147-168(链接 CTS+CancelAfter+超时 Dispose);HostClient 放弃前 attempt.CancelAsync(ExtensionHostClient.cs:950)。
- 缺口:范围内无"客户端 ConnectTimeout 到期"用例,更无规格描述的回归场景——服务端超时后才监听,未取消的放弃尝试会抢到唯一管道实例随即断开,Host 视为对端离开 exit 0,此后重连全失败。IT/NamedPipeTransportTests:59 只测服务端;HT/ExtensionHostClientLifecycleTests::Host_that_becomes_ready_late 用受控 accept 且默认 ConnectTimeout=10s,触不到危险窗口。
- 风险:Ipc SPEC:30 与 HC SPEC:93 用整段固化的历史故障模式,当前无测试防回退(如改回 WaitAsync 包装)。
- 建议:补测试——真实管道,先不监听→短 ConnectTimeout 超时→开始监听→断言无幽灵连接且后续连接成功。

### D-3(F-IPC-14,PASS*)HostToolCalls.CallAsync 失败映射范围内无测试
- 实现:Ipc.Protocol/HostToolCalls.cs:14-48——调用方取消原样抛;Remote 按 code 含 "denied" 加标签;关闭/超时/协议/IO 映射 "the host did not answer" 错误结果。
- 缺口:该方法是 Worker/脚本执行器唯一回调入口,IT 与 HT 均不驱动(HT 仅 RelayTests:392 用 ArgumentsUtf8)。denied 标签与异常分类边界无证据。
- 建议:补测试(回环 peer 构造各类失败应答,或给 Ipc.Protocol 建专属测试)。

### D-4(F-IPC-02,PASS*)传输单用性拒绝无测试
- 实现:NamedPipeTransports.cs:46-50,复用抛 InvalidOperationException。所有测试恰好都遵守"重连即新传输",规则本身未被证明。建议:补一行断言级用例。

### D-5(PASS 项内的未测分支)
F-HC-08 的 8 环上限、F-HC-14 的 Diagnostics 16 条淘汰、F-HC-17 的放弃尝试取消、F-HC-20 的 HostExitCode 正值(范围内仅 Assert.Null)、F-HC-25 的 RunCodeProxyOptions.Validated,均有实现无范围内测试;主线已被相邻测试覆盖,不降整项结论,列入下节。

## 已实现但缺测试清单

| 行为 | 代码位置 | 说明 |
|---|---|---|
| 客户端 ConnectTimeout 取消放弃连接(含晚监听回归) | NamedPipeTransports.cs:147-168;ExtensionHostClient.cs:950 | 同 D-2,最高优先 |
| 入站缓冲精确对齐边界 | PipeSession.cs:304-312 | 同 D-1 |
| HostToolCalls.CallAsync 失败/denied 映射 | HostToolCalls.cs:14-48 | 同 D-3 |
| 传输复用拒绝 | NamedPipeTransports.cs:46-50 | 同 D-4 |
| 调用链 >8 环拒绝 | ExtensionHostClient.cs:1988-1994 | 环已测;HT 中 "limit is 8" 字样属 MaxScriptChars 非链长 |
| Diagnostics 16 条淘汰 | ExtensionHostClient.cs:2276-2280 | 多处断言内容,无上限断言 |
| HostExitCode 正值(自退 0/被杀码/崩溃后保留) | ExtensionHostClient.cs:904-908;HostProcess.cs:183-197 | 范围内仅 LifecycleTests:19,36,136 断言 null;正值在 Integ/StopLifecycleTests:73(范围外) |
| RunCodeProxyOptions.Validated 创建期拒绝 | RunCodeProxyTool.cs:363-394 | 无非法配置用例 |
| 拖尾第二负载值的容忍(U-12) | FrameCodec.cs:131 | "至多一个"读侧未强制,无测试 |
| IpcPeer 关闭有界等待(pump/handlers 各 2s) | IpcPeerImpl.cs:191,211 | 无边界测试 |
| 早到帧 32 id 上限驱逐、重复 terminalId 替换 | Terminal.cs:22,424-446,196-204 | 1MiB 与回放已测,驱逐/替换分支无 |

## 程序→规格:未记录行为

Ipc 层:
- U-1 MaxOutstandingRequests(默认 0=无限),超限发送前抛 InvalidOperationException(IpcPeer.cs:12;IpcPeerImpl.cs:540-556)。已测。
- U-2 入站处理并发门 64(IpcPeerImpl.cs:9,18),排队请求仍可被 cancel 命中。已测。
- U-3 IpcPeer.VersionMismatch 公开事件,不在 IIpcPeer 上(IpcPeerImpl.cs:61);HostClient 按具体类订阅(ExtensionHostClient.cs:1032)。
- U-4 DefaultTimeout≤0→无限(IpcPeerImpl.cs:35-38);HC SPEC:78 依赖但 Ipc SPEC 未写。
- U-5 服务端管道 CurrentUserOnly(NamedPipeTransports.cs:127)——安全默认,规格无。
- U-6 读队列背压 1024 帧/32MiB,超限读循环等待(PipeSession.cs:44-50,330-356;IpcPipeOptions.cs:19-22)。已测。
- U-7 传输静默吞 FrameReceived 消费者异常(PipeSession.cs:262-265);peer 吞通知订阅者异常(IpcPeerImpl.cs:334-337,已测)。消费者 bug 无声丢帧,无诊断面。
- U-8 入站 handler 抛 OCE 不回任何应答(IpcPeerImpl.cs:420-422),请求方靠自身预算收口;错误码 unhandled/handler_error(IpcPeerImpl.cs:408-425)未入规格。
- U-9 取消通知线名 "cancel"、发送预算 5s、ping 不发取消(IpcPeerImpl.cs:459-478;FrameCodec.cs:216)。HC SPEC:84 误写 ipc.cancel(SQ-2)。
- U-10 ValidatePipeName 另拒 `/`、`\`、`:`(IpcTransport.cs:58-61),规格只写长度。已测。
- U-11 MaxFrameBytes 施加 1024 下限、maxDiscard=max(maxFrame,MaxResynchronizeBytes)(PipeSession.cs:40-42)。
- U-12 Decode 不拒绝首负载后的拖尾字节(FrameCodec.cs:131,Read<T> 只解首值)——"至多一个负载"读侧未强制;未经测试验证,建议严格化或测试固化现状。
- U-13 Stop/Dispose 有界等待 pump 2s、handlers 2s(IpcPeerImpl.cs:191,211)、StopTimeout 5s(IpcPipeOptions.cs:29)。

HostClient 层:
- U-14 tools.register 规范化:无名丢弃、空描述→""、空 schema→"{}"、**未定义 SideEffect→Execute(最高权限)**(ExtensionHostClient.cs:1160-1191;ExtensionHostTool.cs:50-57)。安全默认,规格未写;已测(HT/ExtensionHostClientToolRegistryTests::Side_effect_ordinals_map_and_undefined_values_map_to_Execute 等)。
- U-15 命令审批名 plugin-command:{pluginId}:{commandName}(ExtensionHostClient.cs:1575-1583),防与内置工具撞名;规格未写,已测。
- U-16 重试退避 20→250ms、StartTimeout≤0 回退 30s(ExtensionHostClient.cs:922,927,983-985);HostConnection 默认 PipeName="forge-extension-host"、RuntimePath="dotnet"(IExtensionHostClient.cs:9,16)。
- U-17 输出尾部上限 4000 字符(HostProcess.cs:11);StopRequestTimeout 默认 5s、ProcessExitTimeout 默认 10s(Options.cs:21-24)——规格提名字未给值。
- U-18 版本不匹配→protocolProblem 附入关闭描述(ExtensionHostClient.cs:1069-1070,2303-2315);HC SPEC 未提客户端版本不匹配处理(arch:64 只说"拒绝并提示")。
- U-19 终端客户端预校验:列 1–1000、行 1–500、绝对目录、非空 sessionId(Terminal.cs:316-339);规则在 ExtensionHost SPEC:63(Host 侧),HC SPEC 未写客户端也预校验。已测。
- U-20 早到帧 32 id 上限+最旧驱逐、重复 terminalId 结束旧会话(Terminal.cs:22,424-446,196-204);HC SPEC:184 只写 1MiB。
- U-21 DisposeAsync 等 lifecycle 锁至多 30s 并吞 StopCore 异常(ExtensionHostClient.cs:478-505)。
- U-22 未配 deadline 时仍向 Host 报 120s 默认 WireTimeoutMilliseconds(ExtensionHostClient.cs:2057-2058)。
- U-23 "invalid" 比较 OrdinalIgnoreCase(HostAnswerRules.cs:81),规格未提大小写。
- U-24 RunCodeProxyOptions.Validated 创建期拒绝非法配置(RunCodeProxyTool.cs:363-394),规格只写运行期夹取。

## 规格质量问题

- SQ-1(矛盾/过时,最严重)arch:64"消息含…**PluginId**…;支持…**批量**…与断连处理"与 Ipc SPEC:36"batch 尚无契约面;PluginId 不在通用帧头"直接矛盾;实际帧头(IpcMessages.cs:36-43)只有 Version/Id/Operation/IsResponse/Error,Cancel/Timeout 是通知与本地机制而非头字段。该句是已退役 v1 JSON 信封(IpcMessages.cs:8)的遗留。建议:改写 arch:64 指向 forge-ipc v2 帧格式,batch 标未决。
- SQ-2(命名错误)HC SPEC:84"发一条 `ipc.cancel` 通知"——线上操作名是 `cancel`(FrameCodec.cs:216,IpcPeerImpl.cs:471),全仓库不存在 ipc.cancel;按规格名实现会使取消失效(IpcPeer 只识别 "cancel")。建议:更正并注明由 IpcPeer 自动发送。
- SQ-3(措辞不符)HC SPEC:25-27"只剩空白 runId、空白 pluginId、空白 name 三条显式检查"——实现只查 Length==0(ExtensionHostClient.cs:1797-1815),纯空白值由 pending 查找/身份比较/resolver 拒绝(HT/HostToolCallRunBindingTests::Whitespace_only_runId_matches_no_live_run 证明仍 fail-closed)。行为无缺陷,表述易误导。建议:改"空串显式检查;空白值由绑定查找拒绝"。
- SQ-4(过时注释,代码级)IpcPipeOptions.cs:7"Largest JSON body"、NamedPipeTransports.cs:172,207"JSON frames"——v2 为 MessagePack,与 SQ-1 同源。
- SQ-5(跨规格重复)终端流控常量三处:arch:64、ExtensionHost/SPEC.md:62-68、HC SPEC:182-188(1MiB/64KiB/32 终端/5s)。当前一致但所有权不清:客户端特有规则(早到帧 1MiB、32 id)只在 HC SPEC。建议线协议常量以 forge-extension-host 为单一所有者,HC 引用;HC SPEC:189"已知限制"与 Ipc SPEC:24 重复,宜单向引用。
- SQ-6(模糊)HC SPEC:17"工具参数…不建 DOM"字面限于 ToolArguments,但 RunCodeProxyTool.TryParseArguments(RunCodeProxyTool.cs:88-96)与 RunCodeDescription.Compact 用 JsonDocument 建 DOM。不矛盾,易被读成"模块任何路径不建 DOM"。建议显式豁免 run_code 参数/schema 解析。
- SQ-7(默认值/安全规则缺失)Ipc SPEC 未记录 U-1/2/4/5/6/9(尤其 CurrentUserOnly 与 64 并发门);HC SPEC 未记录 U-14"未定义 SideEffect→Execute"安全默认与 U-15 审批命名空间。这些是调用方/安全评审依赖的规则,应入规格。
- SQ-8(轻微)Ipc SPEC:18"只对测试程序集可见"准确但易误读:IVT 仅 Forge.Integration.Tests(Forge.Ipc.csproj:7),Forge.Ipc.Tests 不在列(经 LoopbackTransport 原始帧等效)。建议写明程序集名。

## 业务闭环缺口

已验证闭合(实现+测试双证据):
1. 最大帧长拒绝:写侧 Seal/SendAsync 拒绝、读侧跳帧重同步、>64MiB 关连接;codec/transport 双层测试含恰好等于上限的边界。
2. 版本不匹配:请求→version_mismatch 应答、响应→fault 对应 pending、通知→丢弃,三类均测且连接保持;HostClient 记 protocolProblem(此段无测试,U-18)。
3. 并发关联与乱序:60 并发反序逐一对应、16 并发代理调用、未知 id 忽略、每运行门限流、独立根不共享门。
4. 取消/超时传播:超时→cancel 携带请求 id→入站 handler 令牌取消(含排队在 64 门后者);调用方取消同样发 cancel;ping 不发;超时后连接可用、迟到应答作废均测。
5. 断连在途失败与重连:MarkClosed fault 全部 pending;崩溃 Failed 保留快照、同实例重连、旧世代丢弃/窗口注册保留、旧进程树终止、一次启动至多一次 Failed,均有专项测试。
6. host.tool_call 审批回路:解码拒绝→run_code 前置拒绝→pending 绑定→身份相等→resolve→审批→Allow 后复查 IsActive/token→调用→截断→应答;顺序与 fail-closed 各分支(无 resolver/无审批路径/审批抛错/拒绝/伪造身份/重放/跨世代)均测。

未闭合/弱环:
- G-1(最高)放弃连接取消环(D-2):故障模式实现已堵,但无回归测试,"证明"半边缺失。
- G-2 入站缓冲所有权环(D-1):精确对齐下"不再复用"不变量可被破坏,损坏沿 Read<T> 传入业务层;需修复+对齐测试。
- G-3 Worker 侧回调失败映射环(D-3):HostToolCalls.CallAsync 是脚本/Worker 唯一回调出口,denied 标签与异常分类在范围内无证据,闭环最后一跳(Worker 视角)未被本范围证明。
- G-4 取消的"无声应答"约定(U-8):handler 被取消后不回帧,请求方靠自身预算收口;ExtensionHost SPEC:42 已声明中继由 run 生命周期收口,逻辑闭合,但 Ipc SPEC 未写该约定,跨模块读者无法从 Ipc 规格推出"取消后等不到应答是协议行为"。
- G-5 次要未测分支:8 环上限、Diagnostics 16 淘汰、HostExitCode 正值、RunCodeProxyOptions 配置拒绝、早到帧驱逐/重复 id 替换(见清单)——不影响主环,属边界证明缺口。
