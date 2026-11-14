# Scripting/ScriptRuntime/ScriptExecutor 审计发现

范围:src/Forge.Scripting/SPEC.md(SC)、src/Forge.ScriptRuntime/SPEC.md(SR)、src/Forge.ScriptExecutor/SPEC.md(SE)。只读审计,未改源码/规格/测试,未跑 build/test。缩写:ST=tests/Forge.Scripting.Tests,IT=tests/Forge.Integration.Tests;代码路径省略 src/ 前缀(如 Scripting/…=src/Forge.Scripting/…)。按 test-anti-patterns 技能校准:抽查测试均有具体断言,失控循环在可回收子进程跑,并发用 TCS/门闩非短定时器,未发现 Critical 级反模式。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-SCRIPT-01 | JIT,只依赖 BCL+Roslyn+Sdk+ScriptRuntime | SC:12 | Scripting/Forge.Scripting.csproj;Directory.Build.props:3 | 无(结构检查) | PASS |
| F-SCRIPT-02 | 插件=普通类库 IPlugin,不用 CSharpScript;不做发现/注册/manifest | SC:16 | Scripting/ScriptCompiler.cs:304-320;全模块无 CSharpScript;发现在 ExtensionHost/PluginCatalog.cs | ST::ScriptCompilerTests.Valid_plugin_compiles_to_a_pe_image_without_diagnostics | PASS |
| F-SCRIPT-03 | CompileAsync→ScriptCompilation(PE+描述);Host 唯一实例=唯一缓存所有者 | SC:17 | Scripting/ScriptCodeCompiler.cs:88-139;ExtensionHost/ExtensionHost.cs:37(单字段传入 Supervisor) | ST::ScriptCodeCompilerConcurrencyTests.Concurrent_calls_for_one_key_share_one_compile;IT::ScriptPoolTests.Failed_compile_does_not_poison_next_call | PASS |
| F-SCRIPT-04 | 本模块不做工具解析/审批/绑定;内层只经 IHostFacade | SC:17 | ScriptCodeCompiler.cs 无工具逻辑;ScriptRuntime/ScriptToolsFacade.cs:96-121 | ST::ScriptCodeRunnerConcurrencyTests.Changed_tool_capabilities_do_not_leak_across_requests | PASS |
| F-SCRIPT-05 | ScriptCodeRunner:ICodeRunner 保留;执行器不引用本模块 | SC:18 | Scripting/ScriptCodeRunner.cs:33-80;ScriptExecutor/Forge.ScriptExecutor.csproj(无 Scripting) | ST::ScriptCodeRunnerBasicsTests 全套;IT::SingleUseExecutorProtocolTests.ExecutorDependencyManifest_DeclaresNoRoslynOrScriptingDependencies | PASS |
| F-SCRIPT-06 | 插件无语法白名单;脚本保留白名单+语义检查 | SC:19 | ScriptCompiler.cs 无分析器;ScriptCodeCompiler.cs:209-214、288-293 | ST::ScriptCodeRunnerSecurityTests.Banned_symbols_are_refused_with_position | PASS |
| F-SCRIPT-07 | SourceHash 归一化(LF/去BOM/去单尾换行)计算,调用方不可设置 | SC:23 | Scripting/CompilationRequest.cs:36-38、136-181 | ST::RequestIdentityTests.Source_hash_is_sha256_of_the_normalized_utf8_source 等 4 条(换行/BOM/尾换行) | PASS |
| F-SCRIPT-08 | ExpectedSourceHash 只校验;不符→FORGE0004;结果哈希仍为计算值 | SC:23 | ScriptCompiler.cs:233-245;ScriptDiagnostic.cs:35 | ST::ScriptCompilerTests.Expected_source_hash_mismatch_fails_before_Roslyn_runs_with_FORGE0004;ST::RequestIdentityTests.Stale_expected_source_hash_with_modified_source_does_not_become_the_result_hash | PASS |
| F-SCRIPT-09 | CacheKey=源码摘要+期望哈希+程序集名+契约+设置(Usings null/空)+引用集(含不可解析),条数+长度分帧 | SC:23 | CompilationRequest.cs:73-134、196-232(长度前缀) | ST::RequestIdentityTests.Usings_and_references_with_the_same_concatenation_do_not_collide / Null_and_empty_usings_do_not_share_a_cached_result / Unresolvable_reference_does_not_reuse_a_cached_success | PASS |
| F-SCRIPT-10 | 确定性编译:同请求同字节 | SC:23 | ScriptCompiler.cs:346-349、369;ScriptCodeCompiler.cs:60-70 | ST::RequestIdentityTests.Same_request_yields_the_same_hashes_and_byte_identical_images | PASS |
| F-SCRIPT-11 | <base>/<id>/<version>/ 原子写、绝不覆盖、防穿越 | SC:25 | Scripting/VersionedOutputStore.cs:60-92、204-212(Move overwrite:false)、234-254(Segment) | ST::VersionedOutputStoreTests.Save_is_idempotent_and_never_overwrites_an_existing_version / Concurrent_saves_of_one_version_leave_a_single_intact_file / Path_segments_that_escape_or_nest_are_rejected | PASS |
| F-SCRIPT-12 | ALC 共享集仅 TPA+Sdk+显式;宿主非 TPA 已加载不共享 | SC:27 | Scripting/PluginLoadContext.cs:92-110、250-260、581-599 | ST::PluginLoadContextTests.Shared_names_are_the_platform_set_and_the_contract_not_every_host_loaded_assembly / Plugin_local_copy_wins_over_a_host_loaded_assembly_with_the_same_simple_name | PASS |
| F-SCRIPT-13 | 路径模式 resolver+平铺探测 | SC:27 | PluginLoadContext.cs:87-90、396-408 | ST::PluginLoadContextTests.Dependency_is_resolved_from_the_plugin_directory_not_the_host | PASS |
| F-SCRIPT-14 | BeginUnload/WaitForCollectionAsync 分离;弱引用;文件句柄释放后才 true,超时 false | SC:27 | PluginLoadContext.cs:226-248、314-327、442-470 | ST::PluginLoadContextTests.Unload_eventually_releases_the_dependency_file_lock / Wait_for_collection_reports_false_when_a_dependency_file_is_still_held_open / Collected_context_has_released_the_dependency_file_when_the_wait_returns | PASS |
| F-SCRIPT-15 | 验证镜像模式:只内存 LoadFromStream,不探目录/不读 deps.json;缺失→FileNotFoundException 指名 | SC:29 | PluginLoadContext.cs:64-72、262-265、329-357 | ST::VerifiedDependencyImageTests.Dependency_is_loaded_from_the_supplied_bytes_not_the_replaced_file_on_disk / Unresolved_dependency_fails_naming_it_and_never_reads_the_plugin_directory;IT::VerifiedDependencyTests.Dependency_replaced_after_verification_never_runs_even_when_first_loaded_mid_call | PASS |
| F-SCRIPT-16 | 验证模式本地库抛 DllNotFoundException 不回退;路径模式仍返回零 | SC:29 | PluginLoadContext.cs:277-291、411-431 | ST::VerifiedNativeLibraryTests.Verified_mode_refuses_a_native_library_while_the_file_is_present_replaced_and_deleted / Verified_mode_refuses_a_native_library_the_operating_system_could_resolve / Path_mode_still_falls_through_to_default_probing_for_an_unresolvable_library | PASS |
| F-SCRIPT-17 | 镜像必须托管(PEReader HasMetadata+manifest);非托管构造时拒 | SC:29 | Scripting/ManagedImage.cs:15、41-70;PluginLoadContext.cs:378-383 | ST::VerifiedDependencyImageTests.Verified_mode_rejects_a_dependency_image_that_is_not_managed / A_pe_without_managed_metadata_is_not_a_managed_image | PASS |
| F-SCRIPT-18 | 空名/重名/null 字节=调用方错误 | SC:29 | PluginLoadContext.cs:367-392 | 无(无专用用例) | PASS* |
| F-SCRIPT-19 | 字节与上下文同生命周期;回收后不残留镜像 | SC:29 | PluginLoadContext.cs:314-327(DropAndUnload 清集合) | ST::VerifiedDependencyImageTests.Collected_context_leaves_no_verified_image_bytes_behind | PASS |
| F-SCRIPT-20 | ReadNativeBinaryFormat 只看头:ELF/Mach-O/fat/fat64/无CLI头PE;数据 null;装载前拒收 | SC:31 | ManagedImage.cs:20-38、74-83 | ST::VerifiedDependencyImageTests.A_native_binary_is_recognised_by_its_header_signature_whatever_the_extension;IT::VerifiedDependencyTests.A_plugin_file_without_managed_metadata_stops_the_load | PASS |
| F-SCRIPT-21 | ScriptSurface 固定引用/导入;AllowUnsafe=false;无 resolver;script.csx | SC:33 | Scripting/ScriptSurface.cs:25-97;ScriptCodeCompiler.cs:54-70 | ST::ScriptCodeRunnerSecurityTests.Ordinary_bcl_usage_stays_allowed / Unreferenced_namespaces_are_rejected_by_the_compiler_with_position | PASS |
| F-SCRIPT-22 | #r/#load/unsafe/typeof/指针编译前语法拒绝 | SC:33 | Scripting/ScriptSecurityAnalyzer.cs:95-131、371-378;ScriptCodeCompiler.cs:209-214 | ST::ScriptCodeRunnerSecurityTests.Syntax_bans_are_refused_before_compilation | PASS |
| F-SCRIPT-23 | 禁用命名空间/受限类型语义拒绝 | SC:33 | ScriptSecurityAnalyzer.cs:20-76、133-310 | ST::ScriptCodeRunnerSecurityTests.Banned_symbols_are_refused_with_position / Alias_and_member_access_do_not_bypass_the_guard | PASS |
| F-SCRIPT-24 | Forge.Scripting 只许 ScriptGlobals/ToolsFacade/ToolCallException;其余禁用 | SC:33 | ScriptSecurityAnalyzer.cs:84-89、393-395;ScriptSurface.cs:88-92(本程序集仍引用) | ST::ScriptCodeRunnerSecurityTests.Banned_symbols_are_refused_with_position(VersionedOutputStore/ManagedImage/ScriptCompiler/ScriptCodeRunner 用例)/ Plugin_load_context_is_unreachable_because_its_loader_assembly_is_not_referenced | PASS |
| F-SCRIPT-25 | 诊断 line/column、条数字符有界;栈现 script.csx:line N | SC:33 | ScriptCodeCompiler.cs:69、325-338;ScriptDiagnostic.cs:40-72;ScriptSecurityAnalyzer.cs:435-456 | ST::ScriptCodeRunnerBasicsTests.Syntax_error_reports_line_column_and_code / Diagnostic_count_limit_is_observed_with_more_note / Runtime_exception_names_type_and_script_location;ST::DiagnosticTextTests 全套 | PASS |
| F-SCRIPT-26 | 缓存只存产物+表面;不存工具元数据/facade/审批/上下文 | SC:35 | ScriptCodeCompiler.cs:179-196(键无 Tools);Scripting/ScriptCompilation.cs:54-124 | ST::ScriptCodeRunnerConcurrencyTests.Changed_tool_capabilities_do_not_leak_across_requests | PASS |
| F-SCRIPT-27 | 键=表面指纹(版本+导入+引用路径+3 MVID)+源码+全部选项 | SC:35 | ScriptSurface.cs:100-126;ScriptCodeCompiler.cs:179-196(8 选项字段与 Sdk/ExtensionExecution.cs:95-122 一一对应) | 选项侧:ST::ScriptCodeRunnerBasicsTests.Failed_compile_is_keyed_by_options_not_just_source;指纹/MVID 升级侧:无(grep Fingerprint 零命中) | PASS* |
| F-SCRIPT-28 | 失败编译入 LRU,错误脚本不再进 Roslyn | SC:35 | ScriptCodeCompiler.cs:142-170;ScriptCompilation.cs:54-124 | ST::ScriptCodeRunnerBasicsTests.Failed_compile_is_keyed_by_options_not_just_source;IT::ScriptPoolTests.Failed_compile_does_not_poison_next_call | PASS |
| F-SCRIPT-29 | 每运行按本次 Tools 重建 facade;旧工具名即未知 | SC:35 | ScriptRuntime/ScriptHarness.cs:115-121;ScriptToolsFacade.cs:46-55 | ST::ScriptCodeRunnerConcurrencyTests.Changed_tool_capabilities_do_not_leak_across_requests | PASS |
| F-SCRIPT-30 | 入口委托按产物加锁创建一次并复用 | SC:35 | 机制为死代码:ScriptCompilation.cs:33-46(EntryPoint() 全仓零调用);真实路径 ScriptCodeRunner.cs:76、82-90 每运行重新 Load(新 ALC+新委托) | 无 | FAIL |
| F-SCRIPT-31 | globals/log/facade/令牌每运行独立 | SC:35 | ScriptHarness.cs:107-121 | ST::ScriptCodeRunnerConcurrencyTests.Concurrent_runs_with_same_source_keep_logs_and_results_isolated | PASS |
| F-SCRIPT-32 | 单飞;每调用者取消只抛自身;最后等待者取消才取消共享;取消不入缓存 | SC:37 | Scripting/CompileFlight.cs:9-74;ScriptCodeCompiler.cs:88-170;ScriptCompiler.cs:127-203 | ST::ScriptCodeCompilerConcurrencyTests.Cancelled_waiter_leaves_other_waiters_unaffected / All_waiters_cancelled_cancels_shared_compile_and_next_call_recompiles 等 5 条(DEBUG 门闩);ST::CompilationCacheTests.CompileAsync_all_precancelled_waiters_leave_no_cancelled_cache_entry | PASS |
| F-SCRIPT-33 | PE 发射:ForgeScriptEntry:ScriptGlobals/私有构造/static Run/RunScriptAsync;#line;using 提升 | SC:39 | Scripting/ScriptSourceBuilder.cs:40-125;ScriptCodeCompiler.cs:69、246-252 | ST::ScriptCodeRunnerBasicsTests.Top_level_await_is_supported;ST::ScriptCodeRunnerTrailingExpressionTests.Trailing_statement_expression_diagnostic_keeps_the_script_line;IT::SingleUseExecutorExecutionTests.ValidScript_ReturnsItsValue_AndRefusesASecondRun | PASS |
| F-SCRIPT-34 | 末尾表达式只看语义类型;非 void 两遍编译改写;void/ref-like→null | SC:39 | ScriptCodeCompiler.cs:223-231;ScriptSourceBuilder.cs:161-175(GetTypeInfo+IsRefLikeType) | ST::ScriptCodeRunnerTrailingExpressionTests.Trailing_void_call_stays_a_statement / Trailing_ref_struct_call_stays_a_statement / Trailing_assignment_returns_the_assigned_value 等 9 条 | PASS |
| F-SCRIPT-35 | 已知差异:类型声明不允许;改写行列偏移 17 | SC:39 | ScriptSourceBuilder.cs:31(ResultCast 长 17)、58-97(正文落方法体) | 行号有(同上);类型拒绝与列偏移:无 | PASS* |
| F-SCRIPT-36 | 描述 10 字段随映像产出;SourceHash≠CacheKey | SC:39 | ScriptCodeCompiler.cs:272-283;ScriptRuntime/ScriptArtifact.cs:253-265 | IT::SingleUseExecutorRejectionTests 逐字段篡改各用例 | PASS |
| F-SCRIPT-37 | 编译前取消抛 OCE:不编译/不启动/无 facade 回调 | SC:41 | ScriptCodeRunner.cs:65、74;ScriptCodeCompiler.cs:99 | ST::ScriptCodeRunnerConcurrencyTests.Precanceled_token_runs_no_script_and_makes_no_facade_calls;IT::ScriptPoolTests.Precanceled_script_never_dispatches | PASS |
| F-SCRIPT-38 | Tools.* 只交 JSON;未知工具拒绝列可用名(排除 run_code);run_code 永久 Denied | SC:43 | ScriptToolsFacade.cs:77-91、166-210 | ST::ScriptCodeRunnerBasicsTests.Unknown_tool_error_lists_available_names_sorted_without_run_code / Self_call_is_refused_with_denied_flag_even_when_declared;IT::RunCodeScriptTests.Nested_run_code_from_script_refused;IT::SingleUseExecutorToolTests.RunCodeSelfCall_IsDeniedWithoutRecursion | PASS |
| F-SCRIPT-39 | IsError→可捕获 ToolCallException;host 失败(非取消)包装 | SC:43 | ScriptToolsFacade.cs:96-121 | ST::ScriptCodeRunnerBasicsTests.Refused_tool_result_is_catchable_without_denied_flag / Host_failure_is_wrapped_as_catchable_tool_error;IT::RunCodeScriptTests.Each_inner_tool_approved_individually_allow_and_deny | PASS |
| F-SCRIPT-40 | 每运行 SemaphoreSlim 限并发;MaxInnerResultChars 截断 | SC:43 | ScriptToolsFacade.cs:30、44、95、111 | ST::ScriptCodeRunnerConcurrencyTests.Parallel_tool_calls_share_a_bounded_slot;ST::ScriptCodeRunnerBasicsTests.Long_inner_result_is_truncated_at_max_inner_chars;IT::RunCodeScriptTests.WhenAll_overlaps_inner_calls_up_to_run_cap | PASS |
| F-SCRIPT-41 | 泛型反射解码只在 JIT 进程 | SC:43 | ScriptToolsFacade.cs:125-164 | ST::ScriptCodeRunnerBasicsTests.CallJson_generic_deserializes_tool_result / Json_helper_parses_without_touching_the_host | PASS |
| F-SCRIPT-42 | 入口校验 options/超时>0/Tools 非null(可空)/名非空/副作用已定义即抛;空/超长脚本错误结果 | SC:45 | ScriptCodeRunner.cs:41-62;ScriptHarness.cs:44-85 | ST::ScriptCodeRunnerBasicsTests.Null_tool_entry_blank_name_or_undefined_side_effect_are_rejected / Non_positive_limits_are_rejected / Empty_script_returns_an_error_not_an_exception / Overlong_script_returns_an_error_naming_both_lengths | PASS |
| F-SCRIPT-43 | run_code 描述归主程序代理;引擎不另维护 | SC:45 | Scripting 模块无描述构造(负面检查;正面归 forge-host-client,超范围) | 无(负面检查) | PASS |
| F-SCRIPT-44 | 验证约定:DEBUG-only internals/测试;子进程+初始化器预热;门闩 | SC:49 | Scripting/AssemblyInfo.cs:5-7;ST::ScriptCodeCompilerConcurrencyTests.cs:28/59/97/220(#if DEBUG);ST::ScriptCodeChildProcess.cs:38、76 | 即上述测试自身 | PASS |
| F-SCRIPT-45 | 未决:依赖变化失效由 ExtensionHost 负责(Evict/Clear) | SC:55 | API 存在(ScriptCodeCompiler.cs:172;CompilationCache.cs:91-113)但 ExtensionHost/PluginCatalog 零调用(grep) | 无 | SPEC_GAP |
| F-SR-01 | 只依赖 SDK+BCL:无 Roslyn/Ipc/PInvoke | SR:12 | ScriptRuntime/Forge.ScriptRuntime.csproj:10 引用 Forge.Ipc.Protocol,代码零使用(仅注释:ScriptExecutorProtocol.cs:10/14/20/23);另有 MessagePack 包(非 BCL);无 Roslyn/PInvoke | 无 | FAIL |
| F-SR-02 | ScriptGlobals/ToolsFacade 六方法/ToolCallException;Forge.Scripting 命名空间;非 sealed | SR:16 | ScriptRuntime/ScriptGlobals.cs:9-41;ScriptToolsFacade.cs:57-168 | ST::ScriptCodeRunnerBasicsTests(六方法逐一) | PASS |
| F-SR-03 | 每 run 新 facade/log/令牌;Task.Run;WaitAsync 收口;1s 宽限→RequiresTermination | SR:17 | ScriptHarness.cs:39、107-121、159-213 | ST::ScriptCodeRunnerTerminationTests.Synchronous_loop_hits_timeout_and_is_abandoned_for_termination / Synchronous_loop_ignores_external_cancel_and_is_abandoned(子进程,断言 abandoned/must be terminated/0.5 s);IT::ScriptPoolTests.Infinite_loop_timeout_then_recovers_without_failing_concurrent_script | PASS |
| F-SR-04 | 返回 Task 同预算解包 Result | SR:17 | ScriptHarness.cs:176-186、236-259 | ST::ScriptCodeRunnerBasicsTests.Returned_task_is_awaited_and_unwrapped | PASS |
| F-SR-05 | MaxResultChars 截断;MaxLogChars 有界日志附后 | SR:17 | ScriptHarness.cs:144-146、261-266;ScriptRuntime/ScriptOutput.cs:9-52 | ST::ScriptCodeRunnerBasicsTests.Long_result_is_truncated_at_max_result_chars / Long_log_is_truncated_at_max_log_chars / Log_is_captured_and_appended_after_result;IT::SingleUseExecutorExecutionTests.LogAndValue_ComposeIntoOneResult | PASS |
| F-SR-06 | MaxAssemblyBytes=8MiB;EnsureImageWithinBounds | SR:18 | ScriptArtifact.cs:32、81-90;ScriptExecutor/ExecutorSession.cs:94 | IT::SingleUseExecutorRejectionTests.Load_WithOversizedImage_IsRejected / Image_above_the_artifact_bound_IsRefusedAtTheImagesOwnSize | PASS |
| F-SR-07 | MaxLoadFrameBytes 上界证明低于 16MiB 帧 | SR:18 | ScriptArtifact.cs:35-50;算术成立但 IsFrameWithinBounds 全仓零调用、无测试;16MiB 强制在 Ipc/IpcMessages.cs:12+FrameCodec.cs:139 | 无 | PASS* |
| F-SR-08 | Validate/ValidateDescriptor:长度/SHA-256/MVID/运行时/契约/入口形状,不符抛异常不装载 | SR:18 | ScriptArtifact.cs:96-172;ExecutorSession.cs:95 | IT::SingleUseExecutorRejectionTests.Load_WithLengthMismatch_IsRejectedAndRunsNothing / Load_WithHashMismatch_IsRejectedAndRunsNothing / Load_WithBlankEntryPointType_IsRejected(拒绝不杀进程) | PASS |
| F-SR-09 | 契约版本逐字相等(非"非空即过") | SR:18 | ScriptArtifact.cs:22、110-114(Ordinal) | IT::SingleUseExecutorRejectionTests.Load_WithAnotherArtifactContract_IsRejected | PASS |
| F-SR-10 | GlobalsMvid/RuntimeVersion/Sha256 参与校验 | SR:18 | ScriptArtifact.cs:56-79、137-158、172-178 | IT::SingleUseExecutorRejectionTests.Load_WithUnknownGlobalsMvid_IsRejected / Load_WithMismatchedRuntimeVersion_IsRejected / Load_WithPatchLevelRuntimeDifference_IsAccepted | PASS |
| F-SR-11 | executor.load/run/stop;LoadRequest/RunRequest 均 MessagePack | SR:19 | ScriptRuntime/ScriptExecutorProtocol.cs:13-42 | IT::SingleUseExecutorProtocolTests(真实进程);IT::ScriptPoolTests.Code_execute_wire_round_trips_the_bound_context | PASS |
| F-SR-12 | 内层复用 HostToolCall/HostToolCallRequest→ToolResult | SR:19 | 实现不在本模块:ExecutorSession.cs:249-254→Ipc.Protocol/HostToolCalls.cs:14;ScriptRuntime 仅注释引用 | IT::SingleUseExecutorToolTests.InnerToolCall_LeavesThroughTheBoundFacade_AndReturnsItsReply | SPEC_GAP |
| F-SR-13 | 边界:不编译/不装载/不做反射 | SR:23 | 不编译/不装载成立;ScriptHarness.cs:236-259 ResultOf 用 GetProperty("Result") 反射,字面违反 | ST::ScriptCodeRunnerBasicsTests.Returned_task_is_awaited_and_unwrapped | PARTIAL |
| F-SR-14 | 不解析工具/不审批;IsError→ToolCallException;run_code Denied | SR:24 | ScriptToolsFacade.cs:77-121 | 同 F-SCRIPT-38/39;IT::HostToolCallTests.Denied_approval_yields_an_error_result_and_the_host_tool_does_not_run | PASS |
| F-SR-15 | 非沙箱;RequiresTermination=终止进程通知 | SR:25 | ScriptHarness.cs:130-139;无内存/CPU 限制代码 | ST::ScriptCodeRunnerTerminationTests;IT::SingleUseExecutorHostTests.HangingScriptBeyondBudget_IsKilled_AndExecutorDisappears | PASS |
| F-SR-16 | TimeoutMilliseconds 绝对毫秒,不含排队/启动 | SR:26 | ScriptHarness.cs:108-114;剩余预算 ExtensionHost/ScriptExecutorSupervisor.cs:212 | IT::ShortScriptBudgetTests.Small_budget_runs_a_brief_script_once_warm / A_script_that_overruns_its_small_budget_still_fails;IT::ScriptPoolTests.Pool_of_one_queued_script_times_out_without_starting | PASS |
| F-SR-17 | 放弃而非停止:文案区分;保留线程/令牌源 | SR:30 | ScriptHarness.cs:130-139(不 Dispose runCts)、267-281(文案) | ST::ScriptCodeRunnerTerminationTests(DoesNotContain "stopped" 文案);ST::ScriptCodeRunnerConcurrencyTests.Host_call_receives_cancellation_and_run_stops_without_termination | PASS |
| F-SR-18 | 每运行重建能力;工具名只来自本次 Tools | SR:31 | ScriptHarness.cs:107-121;ScriptToolsFacade.cs:46-55 | ST::ScriptCodeRunnerConcurrencyTests.Changed_tool_capabilities_do_not_leak_across_requests;IT::SingleUseExecutorIsolationTests.StaticState_DoesNotLeakAcrossTwoSeparateExecutors | PASS |
| F-SR-19 | 探针格式化;异常滤框架帧,脚本帧≤4、无则 2 | SR:32 | ScriptOutput.cs:62-141 | ST::ScriptCodeRunnerBasicsTests.Primitives_format_as_expected / String_result_is_returned_verbatim / Json_element_result_is_returned_as_raw_json / Runtime_exception_names_type_and_script_location;4/2 上限无专用断言 | PASS |
| F-SE-01 | --pipe 唯一参数;load/run/stop;其它 unsupported_operation | SE:16 | ScriptExecutor/Program.cs:17-66;ExecutorSession.cs:44-53 | IT::SingleUseExecutorProtocolTests.PluginAndWorkerOps_AreRefused;IT::ScriptPoolTests.Single_use_executor_refuses_plugin_ops_and_runs_one_script | PASS |
| F-SE-02 | load:bounds→Validate→LoadFromStream→反射绑入口→IpcEmpty | SE:17 | ExecutorSession.cs:55-122、210-225 | IT::SingleUseExecutorExecutionTests.ValidScript_ReturnsItsValue_AndRefusesASecondRun | PASS |
| F-SE-03 | 第二次 load→already_loaded | SE:17 | ExecutorSession.cs:57-64、111-119 | IT::ScriptPoolTests.Single_use_executor_refuses_plugin_ops_and_runs_one_script(:248 断言 already_loaded) | PASS |
| F-SE-04 | run:not_loaded/already_ran/缺 context/runId→invalid_arguments | SE:18 | ExecutorSession.cs:124-176 | IT::SingleUseExecutorProtocolTests.Run_WithoutSuccessfulLoad_IsRejected;IT::ScriptPoolTests.Run_without_a_usable_bound_context_is_rejected | PASS |
| F-SE-05 | host.tool_call(RunId/Name/JSON)回 Host;Host 按 active-run 补身份转主程序;执行器不查权限/不自报身份 | SE:19 | ExecutorSession.cs:249-254;ExtensionHost/CallBinding.cs:346-400、ExtensionHost.cs:1910、ScriptExecutorSupervisor.cs:206 | IT::SingleUseExecutorToolTests.InnerToolCall_LeavesThroughTheBoundFacade_AndReturnsItsReply;IT::RunCodeScriptTests.Inner_calls_keep_original_session_path_and_depth_and_ignore_forged_fields / Each_inner_tool_approved_individually_allow_and_deny | PASS |
| F-SE-06 | 闭包只 Ipc/Ipc.Protocol/Sdk/ScriptRuntime;发布无 Roslyn/Scripting | SE:23 | ScriptExecutor.csproj+传递闭包核对无 Roslyn | IT::SingleUseExecutorProtocolTests.ExecutorDependencyManifest_DeclaresNoRoslynOrScriptingDependencies(查 deps.json,非规格约定的 publish grep;测试注释明言 publish 检查不可行) | PARTIAL |
| F-SE-07 | 无 P/Invoke;归属归 Host | SE:24 | grep 执行器源码无 DllImport/LibraryImport;归属在 ExtensionHost | 无专用测试(本审计 grep 即规格约定方式);IT::ProcessOwnershipTests(Host 侧) | PASS |
| F-SE-08 | 不写临时 DLL/PDB;只内存流;无 Assembly.Load/LoadFrom | SE:25 | ExecutorSession.cs:210-232;grep 无 Assembly.Load/LoadFrom | IT::SingleUseExecutorProtocolTests.DecoyAssembly_BesideExecutor_IsNotLoaded | PASS |
| F-SE-09 | 校验不过不装载;跨构建拒收 | SE:26 | ExecutorSession.cs:88-109 | IT::SingleUseExecutorRejectionTests 8 条(拒绝后进程仍活) | PASS |
| F-SE-10 | 一次性构造性;无复用判定 | SE:30 | ExecutorSession.cs:20-24、57-64、132-139 | IT::SingleUseExecutorIsolationTests.StaticState_DoesNotLeakAcrossTwoSeparateExecutors / ConcurrentSessions_EachReturnTheirOwnResult_WithDistinctProcesses_AndLeaveNothingBehind;IT::SingleUseExecutorHostTests.TwoScriptCallsThroughHost_UseFreshExecutorProcesses_AndLeaveNoneBehind | PASS |
| F-SE-11 | 帧解码前 16MiB;映像 8MiB;MaxLoadFrameBytes<帧 | SE:31 | Ipc/FrameCodec.cs:139、IpcMessages.cs:12;ExecutorSession.cs:94;算术见 F-SR-07 | IT::SingleUseExecutorRejectionTests.Load_WithOversizedImage_IsRejected;帧强制专测在 Ipc 范围外 | PASS |
| F-SE-12 | PeSha256 校字节;CacheKey/SourceHash 仅身份;MVID/运行时/契约逐字 | SE:32 | ScriptArtifact.cs:98-106、155-172;ScriptCodeCompiler.cs:272-283 | IT::SingleUseExecutorRejectionTests.Load_WithHashMismatch_IsRejectedAndRunsNothing 等 | PASS |
| F-SE-13 | 管道无默认期限;硬期限+杀树归 Host | SE:33 | Program.cs:117(DefaultTimeout=Infinite);ScriptExecutorSupervisor.cs:109-111、278-294 | IT::SingleUseExecutorHostTests.HangingScriptBeyondBudget_IsKilled_AndExecutorDisappears;IT::SingleUseExecutorProtocolTests.KillTree_LeavesNoExecutorProcess;IT::SingleUseExecutorIsolationTests.GhostTask_DiesWithTheProcessTree_AndNeverWritesItsMarker | PASS |
| F-SE-14 | 退出码 0/1/2 | SE:34 | Program.cs:75-97;ExecutorSession.cs:35-42 | 无(未见断言退出码的用例) | PASS* |
| F-SE-15 | 验证约定:真实进程协议拒绝面 | SE:38 | IT::SingleUseExecutorRejectionTests.cs:118(真实 --pipe 进程) | IT::SingleUseExecutorProtocolTests.PluginAndWorkerOps_AreRefused / Run_WithoutSuccessfulLoad_IsRejected / RejectedLoad_KeepsExecutorUsableForSecondLoad | PASS |
| F-SE-16 | 验证约定:e2e 成功/放弃/超时/取消/崩溃后 pid 消失;并发不污染 | SE:40 | ScriptExecutorSupervisor.cs:278-294 | IT::SingleUseExecutorHostTests(2 条);IT::SingleUseExecutorIsolationTests(3 条);IT::ScriptPoolTests.Uncooperative_cancel_then_recovers | PASS |

统计:80 项 = PASS 69、PASS* 5(F-SCRIPT-18/27/35、F-SR-07、F-SE-14;另 F-SR-19 帧数上限子项无断言)、PARTIAL 2(F-SR-13、F-SE-06)、FAIL 2(F-SCRIPT-30、F-SR-01)、SPEC_GAP 2(F-SCRIPT-45、F-SR-12)、MISSING 0、UNVERIFIED 0。

## 非 PASS 项详情

### F-SCRIPT-30(FAIL)入口委托"加锁创建一次并复用"未接入真实调用链
SC:35 声明委托按产物加锁创建一次并复用。机制存在——ScriptCompilation.cs:33-46 `CompiledScript.EntryPoint()`(Lock+Volatile,每镜像装载一次)——但全仓(src+tests)零调用。真实链路 ScriptCodeRunner.ExecuteAsync(ScriptCodeRunner.cs:76)→LoadEntryPoint(:82-90)→ScriptCodeLoader.Load(ScriptCodeCompiler.cs:323-338)每运行新建 collectible ALC、重新 LoadFromStream、重新 CreateDelegate;CompileAsync 返回裸 ScriptCompilation,调用方结构性拿不到 CompiledScript。后果:进程内路径每运行新增一个无显式 Unload 的 ALC(仅靠 GC;放弃运行的 ALC 按设计留到杀进程);生产一次性执行器不受影响,但规格声明在唯一进程内入口上不成立且无测试。建议:代码修复(让 Runner 走 CompiledScript.EntryPoint)或规格修订(承认每运行装载),并补"同源码两次运行共享入口/ALC"测试。

### F-SR-01(FAIL)ScriptRuntime 引用 Forge.Ipc.Protocol,与"不含 Ipc"冲突(已知线索核实成立)
SR:12 "只依赖 SDK 与 BCL:不含 Roslyn、不含 Ipc、不含 P/Invoke"。Forge.ScriptRuntime.csproj:10 引用 Forge.Ipc.Protocol;模块内全部 .cs 中 Forge.Ipc/HostToolCallRequest/IpcEmpty 仅出现于 doc 注释(ScriptArtifact.cs:29、ScriptExecutorProtocol.cs:10/14/20/23),零代码使用。内层调用实际经 Sdk 的 IHostFacade(ScriptToolsFacade.cs:100);HostToolCallRequest 的使用方是 ExecutorSession.cs:254(经 Ipc.Protocol/HostToolCalls)。后果:执行器闭包多带一个未用程序集(不破坏"无 Roslyn"结论),边界声明失真;SR:19 又把 Ipc.Protocol 类型写进本模块责任,规格自相矛盾。建议:删该 ProjectReference(MessagePack 保留并在规格如实列出),或把"不含 Ipc"改为"不含 Ipc 传输层";同时消除"只依赖 SDK 与 BCL"与 MessagePack 的字面矛盾。

### F-SR-13(PARTIAL)"不做反射"被 Task 解包反射违反
SR:23 字面禁止反射;ScriptHarness.cs:236-259 ResultOf 用 GetProperty("Result")+GetValue 解包 Task<T>,静态 Dictionary 缓存。不编译/不装载成立;反射只读 Task.Result,无安全后果,但措辞误导。建议:规格修订为"不做入口绑定/装载反射;Task<T>.Result 解包除外"。

### F-SE-06(PARTIAL)发布闭包验证降级为 deps.json 检查
SE:23/39 约定 publish 后 grep 目录;实际测试 IT::SingleUseExecutorProtocolTests.cs:95-121 查 Forge.ScriptExecutor.deps.json libraries,注释明言 publish 目录检查不可行(测试输出目录共享、合法带有 Roslyn)。csproj 级闭包本审计已核对无 Roslyn。缺口:deps.json 只证明声明闭包,清单外复制项(Content/None CopyToOutput)无法发现。建议:规格改写验证约定,或补独立 publish 的 CI 步骤。证据强度足以支持结论,故不判 FAIL。

### F-SCRIPT-45(SPEC_GAP)Evict/Clear 失效责任无调用方
SC:55 把依赖变化失效交给 ExtensionHost;ScriptCodeCompiler.Clear(:178)与 CompilationCache.Evict/Clear(:96-113)存在,但 ExtensionHost(含 ScriptCompiler 唯一宿主消费方 PluginCatalog.cs)零调用。未发现正确性漏洞:插件键含源码哈希/引用路径/契约(CompilationRequest.cs:73-108),脚本键含 MVID+路径指纹(ScriptSurface.cs:100-126),身份变化自然不命中,且引用集每进程固定;缺的是内存卫生与主动失效接线。建议:规格改为"键身份保证正确性,Evict/Clear 为可选卫生 API",或在依赖/批准变化处接 Clear。

### F-SR-12(SPEC_GAP)HostToolCall 复用责任归属错模块
SR:19 把复用 ExtensionProtocol.HostToolCall/Ipc.Protocol.HostToolCallRequest 列为 ScriptRuntime 责任,实现却全在 ExecutorSession.cs:249-254 与 Ipc.Protocol/HostToolCalls.cs:14;ScriptRuntime 只有 IHostFacade 与注释。行为端到端成立(IT::InnerToolCall_LeavesThroughTheBoundFacade_AndReturnsItsReply)。建议:该句移入 SE:19 或改为"形状描述,实现见 forge-script-executor",与 F-SR-01 一并调和。

### PASS* 缺口说明
F-SCRIPT-18:空名/重名/null 拒绝无专用用例(非托管拒绝有)。F-SCRIPT-27:指纹"版本/MVID 升级→失效"侧无测试,选项侧有。F-SCRIPT-35:类型声明拒绝与列偏移 17 无断言(行号有)。F-SR-07:IsFrameWithinBounds 零调用、算术无测试,"低于 16MiB"目前是纸面证明。F-SE-14:退出码 0/1/2 及命令行拒绝无回归保护。F-SR-19 子项:4/2 帧上限无断言。

## 已实现但缺测试清单

| 项 | 实现位置 | 缺失的测试 |
|---|---|---|
| F-SCRIPT-18 空名/重名/null 拒绝 | PluginLoadContext.cs:367-392 | 三种调用方错误各一条 |
| F-SCRIPT-27 指纹升级失效 | ScriptSurface.cs:100-126 | MVID/引用路径变化→键变化 |
| F-SCRIPT-35 类型声明拒绝、列偏移 17 | ScriptSourceBuilder.cs:31/58-97 | class 脚本报错;改写行诊断列=原列+17 |
| F-SR-07 帧上界算术 | ScriptArtifact.cs:47-50 | IsFrameWithinBounds(16MiB) 及边界;或接入装载路径 |
| F-SE-14 退出码 | Program.cs:75-97;ExecutorSession.cs:35-42 | 坏参数→2;连接失败→1;stop/断管→0 |
| F-SR-19 帧数上限 | ScriptOutput.cs:126-141 | >4 脚本帧截断;无脚本帧时 2 帧 |
| F-SCRIPT-24 ScriptCodeCompiler 点名拒绝 | ScriptSecurityAnalyzer.cs:84-89 | 同规则兄弟类型已有用例,缺该类型一条 |

## 程序→规格:未记录行为

1. ScriptRuntime.csproj 引用 Forge.Ipc.Protocol 零使用(已知线索,成立;见 F-SR-01)。
2. Forge.Scripting.csproj 引用 Microsoft.CodeAnalysis.CSharp.Scripting 包,代码零使用——冗余依赖。
3. 执行器接受 `--pipe=<name>` 变体(Program.cs:38-42);连接超时 10s 常量(:14);管道名经 IpcPipe.ValidatePipeName(:57)。SE:16 只写 `--pipe <name>`。
4. executor.run:null Tools→空列表、null Options→默认(ExecutorSession.cs:182-185);harness 校验失败(如 timeout<1)落 IsError 结果而非 invalid_arguments(:207-210)。SE:18 未列。
5. 失败 load 不消耗一次性装载名额(ExecutorSession.cs:111-119;IT::RejectedLoad_KeepsExecutorUsableForSecondLoad 已固定)——SE:17 未区分成功/失败 load。
6. 未知工具错误最多列 40 名,超出加 ", ..."(ScriptToolsFacade.cs:20、185-210)。
7. TextBounds.Truncate 注记使总长超 maxChars(保留内容=maxChars,ScriptOutput.cs:146-171);ScriptLog 超限注记(:44-48)。
8. 验证模式对 `.resources` 卫星名返回 null 交回默认探测(PluginLoadContext.cs:331-334)——SC:29"所有托管依赖只从内存"的未记录例外(流装载无磁盘位置,实际风险低,应记录或一并拒绝)。
9. ExpectedSourceHash 先 Trim、空白=无期望、忽略大小写(CompilationRequest.cs:65-71;有测试)——SC:23 未记录。
10. 引用身份大小写不敏感去重+Ordinal 排序入键(CompilationRequest.cs:110-134;有测试 Reference_order_and_duplicates_do_not_change_the_key)——SC:23 未记录。
11. CompilationCache 公开 Hits/Missers 与 WarmUpAsync(CompilationCache.cs:38-40、119-145)——SC:49"无公开缓存命中观测点"仅对脚本缓存成立。
12. 脚本程序集名 "ForgeScript"+key[..16](ScriptCodeCompiler.cs:215);进程内 ALC collectible(:329)vs 执行器 ALC 非 collectible(ExecutorSession.cs:213)——规格未区分。
13. ScriptCodeRunner 额外要求 Context 非空(ScriptCodeRunner.cs:45-48;有测试)——SC:45 清单未列。
14. ScriptHarness.ResultProperties 进程级静态反射缓存(ScriptHarness.cs:41、236-259)——良性,但与"无静态状态"叙述需调和(缓存 PropertyInfo 非能力)。
15. ScriptOutput.Describe 兼容旧引擎 "Submission#" 帧(ScriptOutput.cs:128-131)——迁移残留。
16. CompiledScript.EntryPoint() 死代码(见 F-SCRIPT-30);ScriptArtifact.FormatVersion=1 声明后从未参与校验(ScriptArtifact.cs:25,实际用 ContractVersion)。

## 规格质量问题

1. SR:12 自相矛盾且与代码冲突:"不含 Ipc"vs SR:19 引用 Ipc.Protocol 类型+csproj 实际引用(零使用);"只依赖 SDK 与 BCL"vs MessagePack 包。三处需一次调和(F-SR-01/12)。
2. SR:23 "不做反射"过绝对:与 ScriptHarness.ResultOf 冲突(F-SR-13)。
3. SC:35 描述未实现行为:"委托加锁创建一次并复用"在真实调用链不成立(F-SCRIPT-30),规格与实现必须修一边。
4. SE:39 验证约定不可执行/过时:publish grep 被测试明确放弃(IT::SingleUseExecutorProtocolTests.cs:97-102 注释),实际 deps.json 检查(F-SE-06)。
5. 截断责任措辞冲突:SC:17"结果截断策略(均由主程序执行)"vs SC:43"内层结果按 MaxInnerResultChars 截断"(facade 执行);SR:24 同病。应统一为"限额值由主程序定,执行在 facade"。
6. SE:17 歧义:"第二次 load 拒绝"未说明失败 load 是否计数;代码/测试选择"仅成功 load 计数",应明示。
7. SC:49 范围不清:"无公开缓存命中观测点"对 CompilationCache.Hits/Missers 不成立,应限定脚本缓存。
8. SR:18 "格式版本"所指不明:FormatVersion 常量从未使用,实际校验 ContractVersion 字符串;删常量或写明关系。
9. 历史/迁移叙述:SC:39 两处"旧 CSharpScript 引擎…已知差异"、SR:16"与旧引擎完全兼容/与旧 Roslyn 提交类同一形态"——应移入 docs/merge-notes。三份规格无 T1/T2/T3 标记;同类历史长文在 ExtensionHost/SPEC.md:33(WSL2 实测)、:89(work78 测量)。
10. 跨规格重复且已漂移:architecture.md:31-33/39/72 与三份 SPEC 重复"一次性执行器/闭包无 Roslyn/内存交付/审批限流截断";ExtensionHost/SPEC.md:43-44 与 SE:12-34 重复执行器生命周期/预算/杀树,:85 与 SC:53 重复"进程不复用",:35 与 SE:24 重复"无 P/Invoke"。SE:39 已与测试漂移(问题 4),建议单一事实源+引用。
11. SC:29 全称表述未记录 `.resources` 例外(未记录行为 8)。

## 业务闭环缺口

已闭合且有测试的环:编译失败诊断回传(ST::Syntax_error_reports_line_column_and_code→IT::RunCodeScriptTests.Syntax_error_reports_line_and_does_not_run→IT::SingleUseExecutorExecutionTests.SyntaxError_ProducesAnErrorResult_WithoutAnArtifactToLoad→IT::ScriptPoolTests.Failed_compile_does_not_poison_next_call);产物校验失败拒绝装载且进程不死(IT::SingleUseExecutorRejectionTests 8 条);超时/取消后杀树并等待(IT::HangingScriptBeyondBudget_IsKilled_AndExecutorDisappears / KillTree_LeavesNoExecutorProcess / GhostTask_DiesWithTheProcessTree_AndNeverWritesItsMarker;Host 侧 ScriptExecutorSupervisor.cs:278-294 杀不掉即追加错误文本、绝不报成功);脚本内工具审批/限流/截断(IT::Each_inner_tool_approved_individually_allow_and_deny / WhenAll_overlaps_inner_calls_up_to_run_cap / Bounds_truncate_result_and_inner_calls;身份补全 IT::Inner_calls_keep_original_session_path_and_depth_and_ignore_forged_fields);ALC 卸载与句柄释放(ST::PluginLoadContextTests 4 条+Collected_context_leaves_no_verified_image_bytes_behind)。

未闭合:
1. 委托复用/进程内 ALC 生命周期(F-SCRIPT-30):进程内入口每运行新建 collectible ALC 无显式卸载,规格声明的复用未接线;长期进程内用法依赖 GC,放弃运行按设计留到杀进程。
2. 缓存失效责任接线(F-SCRIPT-45):Evict/Clear 无调用方;正确性由键身份兜底,主动失效与内存卫生无人负责。
3. 帧上界证明桥(F-SR-07):8MiB 与 16MiB 之间的 MaxLoadFrameBytes 算术无测试、无运行时调用,两模块限额没有测试把桥固定。
4. 执行器 fail-closed 退出面(F-SE-14):退出码与命令行拒绝无回归测试。
5. 发布闭包验证降级(F-SE-06):deps.json 检查覆盖不了清单外复制项;规格承诺的 publish grep 未落地。