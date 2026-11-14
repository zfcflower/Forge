# Core C(提供商/认证/缓存/模型元数据)审计发现

只读静态审计。Core=src/Forge.Core/SPEC.md,App=src/Forge.App/SPEC.md,行号 Lxx;代码路径相对仓库根;测试证据 `文件::方法`,未注路径者均在 tests/Forge.Core.Tests/Providers/。PASS*=实现存在缺测试。真实服务端行为静态不可验,统一归 F-CORE-C-77。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-CORE-C-01 | Anthropic 显式断点+寿命 | Core L22 | src/Forge.Core/Providers/AnthropicProvider.cs:271-310,335-362,598-608 | AnthropicProviderTests::Places_cache_breakpoints_on_system_tools_and_message_index;PromptCacheWireTests::Anthropic_always_asks_for_the_one_hour_lifetime | PASS |
| F-CORE-C-02 | OpenAI `prompt_cache_key` 路由+`prompt_cache_retention`(5m→in_memory,其余→24h) | Core L22 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:80-88,162-170 | VendorCacheKnobsTests::OpenAis_retention_follows_the_lifetime_policy;PromptCacheWireTests::Prompt_cache_key_carries_the_routing_key_when_enabled | PASS |
| F-CORE-C-03 | xAI 只发 `x-grok-conv-id` | Core L22 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:106-109 | VendorCacheKnobsTests::Xai_gets_the_conversation_id_header_and_nothing_else | PASS |
| F-CORE-C-04 | Copilot 缓存按 `X-Interaction-Id` 分桶,CacheWiring.Copilot 发路由键 | Core L22 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:112-115;ProviderPresets.cs:202 | VendorCacheKnobsTests::Copilot_gets_the_interaction_id_its_cache_is_keyed_on | PASS |
| F-CORE-C-05 | `Copilot-Integration-Id` 适配器本来就发(配额桶/白名单) | Core L22 | 实际由 App 经 Headers 注入:src/Forge.App/Providers/ProviderCatalog.cs:492、ProviderServices.cs:277;Core 适配器不发 | CopilotAuthenticationTests::Exchange_sends_the_headers_the_token_is_bound_to(仅换 token 请求) | PARTIAL |
| F-CORE-C-06 | 两个缓存头取 `CacheRoutingKey ?? SessionId`(交接/分叉同桶) | Core L22 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:99-104 | VendorCacheKnobsTests::The_cache_headers_are_never_sent_after_a_downgrade(断言 lineage-root);Agents/PromptCacheLoopTests::Cache_routing_key_is_the_lineage_root_for_an_inheriting_session | PASS |
| F-CORE-C-07 | 降级后缓存旋钮一律不发 | Core L22 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:82,94-97,162 | VendorCacheKnobsTests::The_cache_headers_are_never_sent_after_a_downgrade | PASS |
| F-CORE-C-08 | Copilot OpenAI 面缓存写入摊进 prompt_tokens、只报 cached_tokens 读取、不做写入估算 | Core L22 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:727-750(CacheWriteTokens 恒沿用前值,从不估算) | PromptCacheWireTests::Usage_maps_deepseek_cache_fields;SessionUsageMathTests::The_cost_bills_every_path_at_its_own_price | PASS |
| F-CORE-C-09 | DeepSeek/Kimi/Meta/OpenRouter/自定义无请求级旋钮(仅自动前缀缓存) | Core L22 | src/Forge.Core/Providers/ProviderPresets.cs:110-128(仅 OpenAi/XAi/Copilot 声明) | VendorCacheKnobsTests::A_vendor_without_the_knobs_is_never_sent_them | PASS |
| F-CORE-C-10 | 预设携带 CacheWiring,只有声明的厂商发对应字段 | Core L22 | src/Forge.Core/Providers/ProviderPresets.cs:103,180,202,247;src/Forge.App/Providers/ProviderCatalog.cs:448-451 | VendorCacheKnobsTests::A_vendor_without_the_knobs_is_never_sent_them 等 4 例 | PASS |
| F-CORE-C-11 | DeepSeek 无论选项如何都不发缓存字段 | Core L22,L71 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:45-50,122-124 | PromptCacheWireTests::DeepSeek_never_gets_a_prompt_cache_key;VendorCacheKnobsTests::DeepSeek_is_left_out_whatever_the_options_say | PASS |
| F-CORE-C-12 | `provider.sendPromptCacheKey` 默认关,用户可为接受网关打开 | Core L22,L71 | src/Forge.Core/Providers/ProviderOptions.cs:17;src/Forge.App/Providers/ProviderDocuments.cs:122 | PromptCacheWireTests::Prompt_cache_key_is_off_by_default | PASS |
| F-CORE-C-13 | 被 400 拒的旋钮走与 Anthropic 同一条一次性降级路径(共享 ProviderDegradations+Notice) | Core L22 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:56-75;HttpChatProvider.cs:117-131 | VendorCacheKnobsTests::A_model_that_refuses_the_cache_fields_is_resent_without_them | PARTIAL |
| F-CORE-C-14 | 稳定前缀(系统→全局→项目→角色)、工具按名排序、动态信息仅末尾临时 user 消息 | Core L24 | src/Forge.Core/Agents/ToolRegistry.cs:31;AgentLoop.cs:377-383 | ProviderNeutralPromptHeadTests::The_composed_head_carries_no_vendor_identity;Prompts/PromptComposerTests | PASS |
| F-CORE-C-15 | 头部对所有提供商同一份字节;preamble/billing 只在 AnthropicProvider、只在 Wire 生效时加,不进 StablePrefix/指纹 | Core L24 | src/Forge.Core/Providers/AnthropicProvider.cs:260-282;Prompts/PromptComposer.cs:39-53 | ProviderNeutralPromptHeadTests::The_fingerprint_is_the_same_with_and_without_the_claimed_identity;::Another_vendor_never_sees_the_claimed_identity | PASS |
| F-CORE-C-16 | 缓存断点落在已持久化历史倒数第二条 | Core L24 | src/Forge.Core/Agents/AgentLoop.cs:377;AnthropicProvider.cs:339-356 | AnthropicProviderTests::Places_cache_breakpoint_on_last_block_of_merged_message | PASS |
| F-CORE-C-17 | 压缩/继承:近期原文与继承前缀逐字节不变 | Core L24,L71 | src/Forge.Core/Agents/(SessionHistory/SummaryCompactor,经测试验证) | Agents/InheritanceTests::First_child_request_repeats_the_parents_last_request_on_the_wire;::Multi_level_inheritance_keeps_every_level_byte_identical;::Child_compaction_summarizes_only_its_own_messages_and_keeps_the_inherited_prefix | PASS |
| F-CORE-C-18 | 三适配器;Responses 仅 Meta/ChatGPT 订阅,POST {base}/responses | Core L117 | src/Forge.Core/Providers/ProviderFactory.cs:31-39;ProviderPresets.cs:182-183,225-230;OpenAiResponsesProvider.cs:37 | ProviderFactoryTests::Creates_openai_responses_provider 等 3 例 | PASS |
| F-CORE-C-19 | Responses 请求形状:instructions/input/reasoning.effort/store:false | Core L117 | src/Forge.Core/Providers/OpenAiResponsesProvider.cs:58-87,127 | OpenAiResponsesProviderTests::Serialises_the_item_shaped_request;::Omits_reasoning_tools_and_an_empty_prompt_when_unused | PASS |
| F-CORE-C-20 | Responses 流按 output_text.delta / function_call_arguments.delta / response.completed 解析 | Core L117 | src/Forge.Core/Providers/OpenAiResponsesProvider.cs:400,426,451 | OpenAiResponsesProviderTests::Streams_text_and_usage_from_the_terminal_event;::Assembles_a_tool_call_from_argument_deltas;::Reports_a_stream_without_a_terminal_event_as_retryable | PASS |
| F-CORE-C-21 | 七档推理,Max 在两个 OpenAI 适配器都夹到 xhigh | Core L117 | src/Forge.Abstractions/Providers.cs:4;OpenAiResponsesProvider.cs:300-307;OpenAiCompatibleProvider.cs:513-522 | OpenAiResponsesProviderTests::Maps_thinking_effort_to_reasoning_effort | PASS |
| F-CORE-C-22 | 重试只发生在首个事件之前 | Core L117 | src/Forge.Core/Providers/RetryingChatProvider.cs:60-101(yielded 闸门) | RetryingChatProviderTests::Does_not_retry_once_events_reached_the_caller;::Retries_retryable_status_before_any_event | PASS |
| F-CORE-C-23 | HttpClient 必须无限超时;每读空闲超时默认 120s 判停滞 | Core L117 | src/Forge.Core/Providers/ProviderFactory.cs:23-29;HttpChatProvider.cs:14;IdleTimeoutStream.cs:31-46 | ProviderTimeoutRegressionTests::Factory_rejects_an_http_client_with_a_finite_timeout;::Stalled_stream_before_any_event_surfaces_as_a_retryable_provider_exception;::Caller_cancellation_during_a_stall_stays_operation_canceled;::Slow_but_steady_stream_survives_longer_than_the_idle_timeout | PASS |
| F-CORE-C-24 | Headers 覆盖同名头;{session} 取本会话 id;无会话不发;模型列表同样不发 | Core L117 | src/Forge.Core/Providers/ProviderHeaders.cs:13-38;HttpChatProvider.cs:225-231;ProviderModelListing.cs:105-108;AgentLoop.cs:393 | ProviderHeadersTests::Session_placeholder_is_filled_with_the_turns_session_id;::A_configured_header_replaces_the_built_in_one_of_the_same_name;::Model_listing_sends_static_headers_but_no_session_header | PASS |
| F-CORE-C-25 | Retry-After 暂存 Exception.Data(契约缺口) | Core L117 | src/Forge.Core/Providers/ProviderErrors.cs:11,103-125 | RetryingChatProviderTests::Honors_retry_after_header | PASS |
| F-CORE-C-26 | OpenAI 兼容合并相邻同角色 user/assistant(空行拼接、tool_calls 保序、tool 不合并、system 原位),字节稳定 | Core L117 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:195-300 | OpenAiCompatibleMessageMergeTests::Merges_consecutive_user_messages_with_blank_line_between_paragraphs;::Merges_assistant_text_and_tool_calls_in_order;::Serialises_merged_history_identically_across_calls 等 7 例 | PASS |
| F-CORE-C-27 | AuthStyle Default/BearerOnly/None;Anthropic BearerOnly 加 3 项 beta | Core L119 | src/Forge.Core/Providers/AnthropicProvider.cs:119-180;OpenAiCompatibleProvider.cs:128-145;ClaudeSubscriptionCredentials.cs:139-144 | ProviderAuthStyleTests::Anthropic_default_sends_the_api_key_header;::Anthropic_bearer_only_sends_the_token_and_the_beta_headers;::Anthropic_none_leaves_the_credential_to_the_callers_headers;::OpenAi_none_sends_no_auth_header | PASS |
| F-CORE-C-28 | Headers 在 ApplyAuth 之后逐个覆盖 | Core L119 | src/Forge.Core/Providers/HttpChatProvider.cs:225-231 | ProviderAuthStyleTests::Anthropic_callers_headers_override_the_provider_header | PASS |
| F-CORE-C-29 | SystemPromptPreamble 非空时必须是 system 第一块(L119);L123 修正为 billing 在前 | Core L119 vs L123 | src/Forge.Core/Providers/AnthropicProvider.cs:273-296(billing 先、preamble 次) | ProviderAuthStyleTests::Anthropic_puts_the_identity_preamble_in_its_own_first_system_block(无 Wire);ClaudeWireTests::The_claim_puts_the_billing_block_first_and_the_official_identity_second | PARTIAL |
| F-CORE-C-30 | 读 claudeAiOauth;距 expiresAt 5 分钟内/过期/缺失/单位不可判即刷新 | Core L121 | src/Forge.Core/Providers/ClaudeSubscriptionCredentials.cs:60,168-174,368-397 | ClaudeSubscriptionCredentialsTests::Token_expiring_within_five_minutes_is_refreshed_and_written_back;::Unknown_expiry_with_a_refresh_token_is_refreshed_once;::Expiry_written_in_seconds_is_read_as_seconds | PASS |
| F-CORE-C-31 | 刷新端点 platform.claude.com/v1/oauth/token、client_id、UA axios/… | Core L121 | src/Forge.Core/Providers/ClaudeSubscriptionCredentials.cs:121-124,241-243 | ClaudeSubscriptionCredentialsTests::Default_path_and_oauth_constants_are_the_claude_code_ones;::Token_expiring_within_five_minutes_is_refreshed_and_written_back | PASS |
| F-CORE-C-32 | 成功后原子写回原文件、保留外来字段;写回失败只记 Trace 不失败 | Core L121 | src/Forge.Core/Providers/ClaudeSubscriptionCredentials.cs:324-351(tmp+File.Move) | ClaudeSubscriptionCredentialsTests::Token_expiring_within_five_minutes_is_refreshed_and_written_back;::Unwritable_credentials_file_still_returns_the_refreshed_token | PASS |
| F-CORE-C-33 | 无文件/无节点/空 token→None;JSON 坏/刷新失败→不可重试 ProviderException 且不含 token | Core L121 | src/Forge.Core/Providers/ClaudeSubscriptionCredentials.cs:155-215,255-285 | ClaudeSubscriptionCredentialsTests::Missing_file_is_not_signed_in;::Malformed_json_throws_without_exposing_the_token;::Failed_refresh_throws_without_the_token;::Credentials_never_print_their_token | PASS |
| F-CORE-C-34 | 并发刷新保护(闭环重点;规格未规定) | Core L121(未提) | src/Forge.Core/Providers/ClaudeSubscriptionCredentials.cs:155-175(无任何串行化);src/Forge.App/Providers/ProviderCredentials.cs:79(直通) | 无 | SPEC_GAP |
| F-CORE-C-35 | 额度头解析宽容:缺头/非数字/负利用率→null;全缺→null;reset 只认 unix 秒 | Core L122 | src/Forge.Core/Providers/AnthropicRateLimits.cs:48-75,139-153 | AnthropicRateLimitsTests::A_response_without_the_headers_reports_no_reading_rather_than_an_empty_account;::Unparseable_values_are_dropped_instead_of_guessed | PASS |
| F-CORE-C-36 | 读数承载 MessageCompleted.RateLimit 与 ProviderException.RateLimit;ReadRateLimit 虚钩基类 null | Core L122 | src/Forge.Core/Providers/HttpChatProvider.cs:69,141-152,262-270;AnthropicProvider.cs:100-101 | AnthropicRateLimitsTests::An_accepted_turn_carries_the_reading_on_the_completion;::A_rejected_request_keeps_the_reading_on_the_exception;ClaudeWireTests::A_429_keeps_both_the_reading_and_the_claimed_identity | PASS |
| F-CORE-C-37 | unified(订阅窗)与 requests/tokens(Console 限流)语义分离;Throttle 不参与 IsRejected/ResetAt | Core L122 | src/Forge.Core/Providers/AnthropicRateLimits.cs:80-101;src/Forge.Abstractions/Providers.cs:111-124 | AnthropicRateLimitsTests::The_console_rate_limits_are_read_as_their_own_thing;::A_console_reading_survives_a_missing_half_and_an_unreadable_reset | PASS |
| F-CORE-C-38 | 两类重置时间格式(unix 秒/RFC 3339)"解析器都接受" | Core L122 | src/Forge.Core/Providers/AnthropicRateLimits.cs:104-113(仅 RFC3339)、147-153(仅 unix 秒) | AnthropicRateLimitsTests::A_console_reading_survives_a_missing_half_and_an_unreadable_reset | PARTIAL |
| F-CORE-C-39 | Classify 先看读数:任一窗 rejected 即耗尽,ResetAt 过期→null;再退回文本/长 Retry-After | Core L122 | src/Forge.Core/Providers/ProviderQuota.cs:55-66 | AnthropicRateLimitsTests::A_rejected_window_is_a_spent_quota_even_without_a_retry_after_or_a_marker;::A_window_whose_reset_has_passed_reports_no_reset_time;::A_429_that_reports_no_window_is_left_to_the_retry_layer | PASS |
| F-CORE-C-40 | 官方身份四件套:billing system[0] 无断点、身份 system[1]、UA、x-app+SDK 头+?beta=true;入口同现两处 | Core L123 | src/Forge.Core/Providers/AnthropicProvider.cs:104,138-160,210-218,273-282;AnthropicWire.cs:76-86 | ClaudeWireTests::The_claim_puts_the_billing_block_first_and_the_official_identity_second;::The_claim_sends_the_headers_the_official_client_sends | PASS |
| F-CORE-C-41 | anthropic-beta 取本机 CLI 自报;官方"18 项"静态兜底表 | Core L123 | src/Forge.Core/Providers/AnthropicProvider.cs:133-136;ClaudeCliProbe.cs:171-176;AnthropicWire.cs:52-70(实为 17 项,注释称 18) | ClaudeWireTests::The_beta_list_is_the_one_the_local_cli_announces | PARTIAL |
| F-CORE-C-42 | 缓存形状一套:两 system 块+消息末块同 TTL、工具无断点、billing 无断点;API 密钥声明 extended-cache-ttl beta | Core L123 | src/Forge.Core/Providers/AnthropicProvider.cs:174-177,271-310,312-316 | ClaudeWireTests::The_claim_uses_the_official_clients_cache_shape;AnthropicProviderTests::Places_cache_breakpoints_on_system_tools_and_message_index | PASS |
| F-CORE-C-43 | 寿命由 CacheHint.Ttl 一处决定:null/1h→1h,5m→不写字段;system 与消息同值 | Core L123 | src/Forge.Core/Providers/AnthropicProvider.cs:593-596(同一 CacheTtl 供 system 与消息) | PromptCacheWireTests::Anthropic_always_asks_for_the_one_hour_lifetime;::An_asked_for_lifetime_is_written_on_the_message_breakpoint | PASS |
| F-CORE-C-44 | ResolveCacheTtl:auto 先 5m,间隔≥CacheTtlPromotionGap(默认 5min)下次写入永久升 1h,不降档;AgentContextMeasured.CacheTtl 上报 | Core L123 | src/Forge.Core/Agents/AgentLoop.cs:416-440,727;AgentOptions.cs:63;AgentInstance.cs:41-46 | Agents/PromptCacheLoopTests::Auto_starts_short_and_moves_to_the_hour_only_after_a_pause;::Auto_never_moves_to_the_hour_for_a_session_that_never_pauses;::An_explicit_cache_lifetime_reaches_the_cache_hint | PASS |
| F-CORE-C-45 | 身份声称只在 BearerOnly+Wire;Wire=null 请求与旧版字节一致 | Core L123 | src/Forge.Core/Providers/AnthropicProvider.cs:104,110 | ClaudeWireTests::Without_the_claim_the_request_is_what_it_was_before | PASS |
| F-CORE-C-46 | System/SystemNamed 除首句名字逐字符相同;ClaimsOfficialIdentity 选择;用户模板优先 | Core L123 | src/Forge.Core/Prompts/BuiltInPrompts.cs:29;src/Forge.App/Providers/ProviderCatalog.cs:513,595 | Prompts/PromptComposerTests:48-49(首句与替换相等断言) | PASS |
| F-CORE-C-47 | beta 被拒降级一次:同一 ChatRequest 重建,beta→3 项/密钥 0 项、ttl 不写、身份与断点不动;第二次 400 是真错误 | Core L123 | src/Forge.Core/Providers/HttpChatProvider.cs:117-131;AnthropicProvider.cs:74-84,133-135,593-596 | ClaudeWireDegradationTests::A_refused_beta_is_resent_with_the_required_betas_and_the_short_cache;::An_api_key_request_drops_its_only_beta_before_resending;::A_downgrade_happens_once_so_a_second_refusal_is_a_real_error;::A_rejection_that_does_not_name_a_beta_is_not_downgraded | PASS |
| F-CORE-C-48 | 降级按 Id\|AuthStyle\|入口 记进程级备忘;ForgetDegradations 供账号/设置变更清空 | Core L123 | src/Forge.Core/Providers/AnthropicProvider.cs:39,64,68;ProviderDegradations.cs:13-21;src/Forge.App/Providers/ProviderServices.cs:91,111(Reload 时 Clear) | ClaudeWireDegradationTests::Dispose(ForgetDegradations);无跨实例复用专测 | PASS |
| F-CORE-C-49 | 降级可见:MessageCompleted.Notice→AgentNotice→外壳 toast | Core L123 | src/Forge.Core/Providers/HttpChatProvider.cs:142-152;AnthropicProvider.cs:89-93;AgentLoop.cs:195 | ClaudeWireDegradationTests::A_refused_beta_is_resent_with_the_required_betas_and_the_short_cache(断言 Notice 非空含 beta);VendorCacheKnobsTests::A_model_that_refuses_the_cache_fields_is_resent_without_them | PASS |
| F-CORE-C-50 | CLI 版本发现顺序 PATH→WinGet→~/.claude/local→VS Code 扩展;--version;保存时写 claudeWire.version | Core L123 | src/Forge.Core/Providers/ClaudeCliProbe.cs:76-93,314-335;App 保存侧见 App L29 | ClaudeCliProbeTests::Probing_without_a_cli_answers_nothing_and_returns_immediately;App ClaudeWireSettingsTests/ProviderStoreTests | PASS |
| F-CORE-C-51 | TryReadBuild:127.0.0.1 监听+ANTHROPIC_BASE_URL+读 billing 即杀进程;Lazy 每进程一次、失败即空;build 钉死不再问 | Core L123 | src/Forge.Core/Providers/ClaudeCliProbe.cs:48,112-149,190-227,297-303;AnthropicProvider.cs:116-117 | ClaudeCliProbeTests::The_suffix_is_read_from_the_billing_block_the_cli_writes;::A_pinned_suffix_wins_and_the_cli_is_not_asked;::The_runtime_suffix_goes_into_the_billing_block | PASS |
| F-CORE-C-52 | 探针 token 卫生:只取请求体 billing,头部不复制/不记录/不落盘;读不到→省略点号段 | Core L123 | src/Forge.Core/Providers/ClaudeCliProbe.cs:190-227(仅 ExtractBuild/ExtractBetas,无日志无落盘);AnthropicWire.cs:79-82 | ClaudeCliProbeTests::A_body_without_the_billing_block_reports_no_suffix;::Without_a_suffix_the_block_omits_the_dot_part(卫生本身无专测) | PASS* |
| F-CORE-C-53 | Copilot 设备码端点/client_id/scope;轮询 pending/slow_down→null,其余抛 | Core L124 | src/Forge.Core/Providers/CopilotAuthentication.cs:53,109-114,155-187 | CopilotAuthenticationTests::Start_posts_the_device_code_request;::Poll_returns_null_while_the_user_has_not_finished;::Poll_throws_on_a_terminal_error_and_names_the_code | PASS |
| F-CORE-C-54 | 换会话 token:Authorization: token、Copilot-Integration-Id、Editor-Version/Editor-Plugin-Version;IsValidAt 留 60s | Core L124 | src/Forge.Core/Providers/CopilotAuthentication.cs:29-33,210-215(214 行 Editor-Plugin-Version 误用 EditorVersion) | CopilotAuthenticationTests::Exchange_sends_the_headers_the_token_is_bound_to(断言了错误值);::A_session_is_invalid_inside_the_last_minute | PARTIAL |
| F-CORE-C-55 | 非官方客户端用法界面如实标注 | Core L124 | src/Forge.App/Localization/Strings.Providers.cs:503-504(中英文明示 unofficial) | 无(文案存在,展示无测试) | PASS* |
| F-CORE-C-56 | 列表地址按协议:Anthropic {base}/v1/models?limit=1000,其余 {base}/models 无查询 | Core L125 | src/Forge.Core/Providers/ProviderModelListing.cs:49,58-70 | ProviderModelListingTests::The_anthropic_listing_hangs_off_its_version_segment_and_asks_for_one_page;::Every_other_protocol_lists_at_its_own_base_without_a_query | PASS |
| F-CORE-C-57 | 调用方给头;20s 链接超时抛 TimeoutException;按服务端顺序去重 | Core L125 | src/Forge.Core/Providers/ProviderModelListing.cs:43,105-126,141-160 | ProviderModelListingTests::Lists_ids_in_server_order_and_dedupes;::Caller_cancellation_propagates_and_is_not_reported_as_a_timeout;::Sends_the_callers_headers_and_replaces_its_own | PASS |
| F-CORE-C-58 | 窗口认 context_length/max_context_length/top_provider.context_length;价格每 token→每百万 | Core L125 | src/Forge.Core/Providers/ProviderModelListing.cs:179-196 | ProviderModelListingTests::Scales_reported_per_token_prices_to_one_million_tokens;::Reads_the_window_from_either_property_name;::Ignores_prices_that_are_not_numbers_or_are_negative | PASS |
| F-CORE-C-59 | 中间件管道形状:端点只管协议,重试最外层(RetryMiddleware=RetryingChatProvider 包装);无逐字拷贝 | Core L126 | src/Forge.Core/Providers/Internal/ChatProviderPipeline.cs:14-48 | 无(测试无引用) | PASS* |
| F-CORE-C-60 | 预设纯数据(协议/URL/环境变量/文档/种子/是否列表),不参与运行时判断;删除后旧配置降级自定义 | Core L127 | src/Forge.Core/Providers/ProviderPresets.cs:91-103,269-296(FindOrDefault→Custom) | ModelCatalogViewTests::A_preset_that_is_no_longer_built_in_has_no_catalog_provider_of_its_own;App ProviderDefinitionTests | PASS |
| F-CORE-C-61 | 元数据链:目录快照→provider 上报→用户 modelMeta,逐字段合并,未知保持未知不补 0 | Core L129 | src/Forge.Core/Providers/ModelMetadataResolver.cs:30-44 | ModelMetadataResolverTests::The_strongest_source_wins_each_field_on_its_own;::Everything_unknown_stays_unknown;::Merge_ignores_a_null_or_unknown_source | PASS |
| F-CORE-C-62 | Parser 读 limit.context/output、四价格、modalities.input、reasoning+effort 档 | Core L129 | src/Forge.Core/Providers/ModelCatalogParser.cs:123-141,152-162,199-201 | ModelCatalogParserTests::Reads_the_window_prices_inputs_and_steps;::A_budget_only_reasoning_model_leaves_the_step_set_unknown | PASS |
| F-CORE-C-63 | Fetcher 用存下 ETag 条件请求,304 即无需更新;超时/状态/形状失败返回消息不抛 | Core L129 | src/Forge.Core/Providers/ModelCatalogFetcher.cs:54,88,99-135 | ModelCatalogFetcherTests::The_stored_etag_is_sent_back_and_a_304_keeps_the_snapshot_on_disk;::A_failed_status_is_reported_with_its_code;::A_transport_error_is_a_message | PASS |
| F-CORE-C-64 | model-catalog.json 读写;损坏或版本不同当作没有快照 | Core L129 | src/Forge.Core/Providers/ModelCatalogJson.cs:20,138-158 | ModelCatalogJsonTests::Everything_written_reads_back_unchanged;::A_file_this_build_cannot_trust_is_reported_and_read_as_no_catalog | PASS |
| F-CORE-C-65 | Normalize(小写/去 vendor//:-free/-latest);预设→目录提供商映射优先,自定义全局兜底一方优先;精确→最长已知+快照后缀→包含;版本递增不继承 | Core L129 | src/Forge.Core/Providers/ModelIdRules.cs:15-88;ModelCatalogView.cs:27-42,114-131,210-233 | ModelCatalogViewTests::A_mapped_preset_answers_from_its_own_catalog_entry;::A_dated_snapshot_of_a_known_model_resolves_to_it;::A_version_bump_is_a_different_model_and_resolves_to_nothing;::A_known_id_contained_in_a_longer_one_still_matches | PASS |
| F-CORE-C-66 | SessionUsageMath:命中率分母 InputTokens;计费输入先减缓存;token 夹≥0 按每百万计费;窗口占比夹 0..1 | Core L129 | src/Forge.Core/Providers/SessionUsageMath.cs:18-57;AnthropicProvider.cs:918(input+cacheRead+cacheWrite) | SessionUsageMathTests::The_cache_hit_ratio_is_the_cached_share_of_the_prompt;::Billed_input_is_the_prompt_without_the_cached_parts;::The_cost_bills_every_path_at_its_own_price;::The_context_ratio_is_clamped_below_a_full_window 等 14 例 | PASS |
| F-CORE-C-67 | CacheRoutingKey=lineage_root_id(缺失回退会话 id) | Core L71 | src/Forge.Core/Agents/AgentLoop.cs:392,464-466 | Agents/PromptCacheLoopTests::Cache_routing_key_is_the_lineage_root_for_an_inheriting_session;::Cache_routing_key_falls_back_to_the_session_id | PASS |
| F-CORE-C-68 | DeepSeek 用量 prompt_cache_hit_tokens 计缓存读;与 cached_tokens 同现以后者为准 | Core L71 | src/Forge.Core/Providers/OpenAiCompatibleProvider.cs:727-750 | PromptCacheWireTests::Usage_maps_deepseek_cache_fields(参数化含同现场景) | PASS |
| F-CORE-C-69 | 每会话首请求缓存用量+origin_kind+提供商+模型写一条诊断日志 | Core L71 | src/Forge.Core/Agents/AgentLoop.cs:27,33,520-545(CacheDiagnostics 或 Trace) | Agents/PromptCacheLoopTests::First_request_report_is_written_once_per_instance_with_origin_provider_and_model;::Failing_diagnostics_sink_does_not_fail_the_turn | PASS |
| F-CORE-C-70 | AgentOptions.MessageCacheTtl(provider.promptCacheTtl)填 CacheHint.MessageTtl,Anthropic 目前忽略 | Core L71 | API 不存在:src/Forge.Abstractions/Providers.cs:22(CacheHint 无 MessageTtl);实际 AgentOptions.CacheTtl→CacheHint.Ttl 已接线:AgentLoop.cs:378-390;src/Forge.App/Agents/SessionRuntime.cs:130 | Agents/PromptCacheLoopTests::An_explicit_cache_lifetime_reaches_the_cache_hint(证明已生效,与"忽略"相反) | FAIL |
| F-CORE-C-71 | Classify:402/带标记或长 Retry-After 的 429/带标记的 400/403;三级标记+速率措辞一票否决;未知从不算额度 | Core L135 | src/Forge.Core/Providers/ProviderQuota.cs:16,21-40,68-117,188-213 | ProviderQuotaTests::Plain_429_is_not_quota;::Rate_limits_and_loose_wording_are_not_quota;::A_definite_marker_wins_over_rate_wording;::Unknown_400_is_not_quota 等 24 例 | PASS |
| F-CORE-C-72 | 重试层对额度不重试;AgentHost 发 AgentQuotaExhausted 且不重试 | Core L135 | src/Forge.Core/Providers/RetryingChatProvider.cs:103-107;Agents/AgentHost.cs:414-423 | QuotaRetryAndProbeTests::Quota_failure_is_not_retried;::Payment_required_is_not_retried;::Long_retry_after_is_not_waited_out;Agents/AutomationHostSupportTests:106-121 | PASS |
| F-CORE-C-73 | QuotaProbeSchedule 无重置时间时按退避用最小请求探测恢复 | Core L135 | src/Forge.Core/Providers/QuotaProbeSchedule.cs;ProviderQuotaProbe.cs:23-25(MaxOutputTokens=1、Thinking Off、"ping") | QuotaProbeScheduleTests::Backoff_sequence_without_jitter_is_5_10_20_60_then_stays_60;::Waiters_share_one_probe_loop_and_complete_together;QuotaRetryAndProbeTests::Probe_returns_true_and_sends_minimal_request | PASS |
| F-CORE-C-74 | 未决:L148 "promptCacheTtl:1h 未接线" | Core L148 | 已接线:AgentLoop.cs:416-424;AnthropicProvider.cs:593-596;App L146 明言"现在真的接线了" | Agents/PromptCacheLoopTests::An_explicit_cache_lifetime_reaches_the_cache_hint | FAIL |
| F-CORE-C-75 | 未决:L155 额度读数只覆盖 Anthropic | Core L155 | 仍属实:ReadRateLimit 仅 AnthropicProvider.cs:100-101 覆写,OpenAI/Responses 无覆写 | AnthropicRateLimitsTests(仅 Anthropic 头) | PASS |
| F-CORE-C-76 | Retry-After 存 Data 的契约缺口仍未正式化 | Core L117 | src/Forge.Core/Providers/ProviderErrors.cs:11,103-117(仍是 Data) | RetryingChatProviderTests::Honors_retry_after_header | PASS |
| F-CORE-C-77 | 服务端行为断言:缺身份块 400、billing 首块被受理、axios UA 免限流、X-Interaction-Id 命中率、sdk-cli 成本减半、limit=1000 被接受、token 与集成头绑定 | Core L119,L121-125 | 实现侧形状均已对齐(见 F-29/31/40/04/56/54);服务端真实反应静态不可验 | 无(均为实测叙述) | UNVERIFIED |

计数:77 项 = PASS 64 + PASS* 3 + PARTIAL 6 + FAIL 2 + SPEC_GAP 1 + UNVERIFIED 1。

## 非 PASS 项详情

### F-CORE-C-70 / F-CORE-C-74(FAIL,规格过时,两处同根)
- 证据:Core L71 声称 `AgentOptions.MessageCacheTtl` 填 `CacheHint.MessageTtl` 且 Anthropic 忽略;Core L148 未决声称 `promptCacheTtl:"1h"` 未接线。实际 `CacheHint` 只有 `(CacheSystemAndTools, CacheThroughMessageIndex, Ttl)`(src/Forge.Abstractions/Providers.cs:22),`AgentOptions` 只有 `CacheTtl`(AgentOptions.cs:60);`provider.promptCacheTtl` 经 SessionRuntime.cs:130 → `AgentOptions.CacheTtl` → `ResolveCacheTtl`(AgentLoop.cs:416-440)→ `CacheHint.Ttl` → AnthropicProvider.cs:593-596 完整生效。App L146 已写明"现在真的接线了"。
- 缺口:规格描述了一个不存在的 API 与一个已解决的未决项;L148 所述"1h 条目须排在 5m 条目之前"的冲突已被 L123"所有断点同一 TTL"设计消解。
- 建议处理类型:规格修订(删 L71 末句、关闭 L148 未决项,改引 L123 的单一 TTL 设计)。

### F-CORE-C-34(SPEC_GAP,并发刷新无保护)
- 证据:`ReadAsync`(ClaudeSubscriptionCredentials.cs:155-175)读→判到期→刷新→写回全程无锁;App `ReadClaudeAsync`(ProviderCredentials.cs:79)直通。并发回合(根+子 Agent)同时到期会各自 refresh。
- 闭环缺环:若端点轮换 refresh token,第二次刷新用旧 token 被拒(invalid_grant)→不可重试 ProviderException→回合失败;写回虽各自原子(tmp+Move)但后写覆盖先写,可把已轮换的旧 token 写回。App L29 对四种设备码 bundle 有"刷新按 provider 串行",Claude 订阅无对应规则。
- 建议处理类型:实现+规格(实例级 SemaphoreSlim+刷新前重读双检,或 App 层串行;规格补并发规则)。

### F-CORE-C-54(PARTIAL,Copilot 交换头取值错误,测试钉住缺陷)
- 证据:CopilotAuthentication.cs:214 `Editor-Plugin-Version` 发的是 `EditorVersion`("Forge/1.0",:67),而设备码请求(:119)发 `EditorPluginVersion`("forge/1.0",:69)。同一流程两处取值不一致,疑似复制粘贴错误。
- 测试反模式:CopilotAuthenticationTests::Exchange_sends_the_headers_the_token_is_bound_to 断言 `Assert.Equal("Forge/1.0", request.Header("Editor-Plugin-Version"))`——期望值来自实现缺陷而非独立契约,属弱 oracle,缺陷被测试固化。
- 建议处理类型:实现修复+测试修正(改用 EditorPluginVersion 并断言 "forge/1.0");服务端是否在意此值属 UNVERIFIED。

### F-CORE-C-13(PARTIAL,头旋钮被 400 拒不降级)
- 证据:OpenAiCompatibleProvider.CanDegrade(:61-65)只匹配 400 消息中的 `prompt_cache_key`/`prompt_cache_retention`;`x-grok-conv-id`/`X-Interaction-Id` 被网关 400 拒绝时不触发降级,回合直接失败。Core L22 说"被 400 拒的旋钮走与 Anthropic 同一条一次性降级路径",未排除头旋钮。
- 缺口:规格承诺与实现范围不一致;严格网关拒绝未知头时缓存头成为硬故障点。
- 建议处理类型:二选一——实现把两个头名纳入 CanDegrade,或规格明确"仅 body 字段旋钮"。

### F-CORE-C-41(PARTIAL,beta 兜底表 17≠18)
- 证据:Core L123 与 AnthropicWire.cs:49-51 注释均称"18 项",`Betas`(:52-70)实列 17 项。主路径取 CLI 自报(runtimeBetas,AnthropicProvider.cs:133-136)不受影响;CLI 不可读且 Wire.Betas 为空时落到这张少一项的表。
- 建议处理类型:核对官方清单补齐或改正数字;补兜底表内容断言。

### F-CORE-C-05(PARTIAL,Copilot-Integration-Id 发送层误记)
- 证据:Core L22 与 ProviderPresets.cs:123 注释称"适配器本来就发";实际 Core 适配器不发,由 App 构造 Headers 时注入(ProviderCatalog.cs:492、ProviderServices.cs:277)。经 App 目录的 Copilot 请求确实带头,但自定义 Copilot 配置不会带,规格归错了层。
- 建议处理类型:规格修订(注明 App 注入)+注释同步;或下沉到适配器。

### F-CORE-C-29(PARTIAL,L119 与 L123 矛盾,实现从后者)
- 证据:L119 要求 preamble 必须是 system 第一块;L123 明确修正"billing 在前、身份在后"。实现:Wire 生效时 billing[0]、preamble[1](AnthropicProvider.cs:273-296);Wire=null 时 preamble 才是首块。两条测试分别钉住两种形状(ProviderAuthStyleTests:92、ClaudeWireTests:26),实现自洽,是 L119 未随修正更新。另 ClaudeSubscriptionCredentials.cs:127-131 注释仍复述旧断言("第一块必须是它,否则除 Haiku 外 400")。
- 建议处理类型:规格修订(L119 改为"无 Wire 时首块;有 Wire 时 billing 首块、身份次块")+代码注释同步。

### F-CORE-C-38(PARTIAL,"解析器都接受"与实现不符)
- 证据:unified 的 `ReadReset`(AnthropicRateLimits.cs:147-153)只接受 unix 秒;throttle 的 `ReadTimestamp`(:104-113)只做 `DateTimeOffset.TryParse`(RFC 3339),并不接受 unix 秒——尽管其注释声称"Either an RFC 3339 timestamp or unix seconds"。L122"解析器都接受"若读作"两个解析器各自接受两种格式"则为假;若读作"两族格式各有解析器接受"则为真。
- 建议处理类型:规格澄清(各认各的格式)+修正 ReadTimestamp 注释;若确需双格式则补实现与测试。

### F-CORE-C-77(UNVERIFIED)
服务端真实行为(400/429 语义、身份块受理顺序、UA 限流、缓存命中率与入口成本差、limit=1000 接受度、token 与集成头 HMAC 绑定)只能实测确认;规格中的实测叙述(见规格质量 Q4)是唯一证据来源,本审计不背书其数值。

## 已实现但缺测试清单

1. **F-CORE-C-52 探针 token 卫生**:ClaudeCliProbe 只从缓冲提取 billing/betas、无日志无落盘(静态确认),但无测试证明 Authorization 头不被复制/记录/落盘。建议:注入含假 token 的 loopback 请求,断言输出与 Trace 均不含 token。
2. **F-CORE-C-59 管道组合**:ChatProviderPipeline/RetryMiddleware 无任何测试引用;重试最外层顺序(Build 的 outside-in)未钉住。
3. **F-CORE-C-55 非官方标注展示**:文案存在(Strings.Providers.cs:503-504),登录窗展示无测试。
4. **跨实例降级备忘复用**(F-48 子项):ClaudeWireDegradationTests 与 VendorCacheKnobsTests 均单实例;无"新实例读 ProviderDegradations 直接走降级形状、省一个往返"的测试。
5. **401/403 刷新重试边界**(App CredentialedChatProvider,ProviderCredentials.cs:1058-1065):整个测试树无 `Credentialed` 引用;首事件前 401→刷新→重试一次、第二事件后不重试、二次失败上抛,均无测试。
6. **并发刷新**(F-34):无实现亦无测试。
7. **beta 兜底表内容**(F-41):ClaudeWireTests:98 用注入列表,17/18 项差异无断言。
8. 测试隔离注记:ProviderDegradations 是进程级静态可变状态,VendorCacheKnobsTests:128 以独占 URL 规避、ClaudeWireDegradationTests 以 Dispose 清空——已有缓解,但新增降级测试若忘清理会跨测试污染(潜在 High,现行做法合格)。

## 程序→规格:未记录行为

1. **重试默认参数**:maxAttempts=3、base 500ms、上限 30s、Retry-After 上限 5min、equal jitter(RetryingChatProvider.cs:13-15,117-141)。Core L117 只说"首事件前重试",参数全部未记录;5min 上限与 L135"长 Retry-After→额度"的 5min 阈值(LongRetryAfter)耦合,应一并写明。
2. **用量记录管道**:ProviderOptions.UsageRecorder + UsageAttempt(HttpChatProvider.cs:110-165;UsageAttempt.cs)每 attempt 一条记录、取消也记、默认关。两份 SPEC 均未记录(App 有 JsonlUsageRecorder 实现与测试)。
3. **x-claude-code-session-id 的 UUID 化**:非 UUID 会话 id 经 MD5 造 v5 UUID(AnthropicProvider.cs:221-233),规格只列头名未说映射规则。
4. **SDK 头硬编码**:`x-stainless-retry-count` 恒 "0"、`x-stainless-timeout` 恒 "600"(AnthropicProvider.cs:158-159);重试/降级重发时 retry-count 不递增,与真实 SDK 指纹有偏差(规格未要求,记录备查)。
5. **anthropic-version: 2023-06-01** 常量随每个 Messages 请求发送(AnthropicProvider.cs:12,124),规格未提。
6. **缓存价格缺失补 0**:目录解析(ModelCatalogParser.cs:162)与 /models 解析(ProviderModelListing.cs:195-196)把缺失的 cache_read/cache_write 价补 0,与 L129"绝不补 0"的精神有张力(合并层确实不补,但快照层把"未知缓存价"变成"免费缓存价",成本读数会偏低)。代码注释自辩,规格未记录。
7. **全局兜底匹配少一档**:ModelCatalogView.FindGlobal(:140-158)只有精确+快照后缀两档,无"包含";三档匹配只在 ProviderIndex.Find(:210-233)。L129 未区分两者。
8. **凭据刷新 20s 超时**(ClaudeSubscriptionCredentials.cs:63)未记录。
9. **401 重试实际含 403**:IsAuthFailure(ProviderCredentials.cs:1176)为 `401 or 403`,App L29 只写 401。
10. **ListRequest 容忍已带 /v1 的 base**(ProviderModelListing.cs:67)——小的未记录容错。

## 规格质量问题

- **Q1 内部矛盾(最严重)**:Core L119"preamble 必须是 system 第一块" vs L123"此处修正……billing 块在前、身份块在后"。修正段写了,被修正段未改,两处同时有效即矛盾;代码注释 ClaudeSubscriptionCredentials.cs:127-131 也复述旧说。
- **Q2 过时/跨规格矛盾**:Core L71(MessageCacheTtl/CacheHint.MessageTtl/Anthropic 忽略)与 L148(未接线)描述不存在的 API 和已解决的未决项;与 Core L123 自身及 App L146("现在真的接线了")直接冲突。未决清单失去可信度。
- **Q3 跨规格大面积重复**:App L29 ↔ Core L121/123/124(claudeWire 默认值与形状校验、sdk-cli 实测理由、ClaudeCliProbe 发现顺序、build 不存配置、System/SystemNamed、ClaimsOfficialIdentity、5 分钟刷新、Copilot 设备码流程);App L28 ↔ Core L127+L22(预设目录与 CacheWiring);App L34 ↔ Core L129(ETag/304、预设→目录映射、全局兜底);App L146 ↔ Core L71/L129(promptCacheTtl 接线、成本公式、命中率口径、ModelMetadataResolver 链)。已出现漂移实例:接线状态(Q2)、401 vs 401/403(未记录行为 9)。建议单一归属+引用。
- **Q4 实测叙述混入规范**:Core L123 含大量历史/实测叙述——"31–33k vs 62–68k(两趟反序复现)"、"12/13/14 项不等"、"68a、d83、da9"、"0.19 秒"、"挂到 91 秒"、"haiku 三臂无差别"、"这与公开报告的方向相反"、"此处修正本 spec 先前的一句判断";L122"实测(2026-11-13)那次 429……retry-after: 3240";L125"实测该值被接受"/"未在本机实测"。这些是证据不是规则,应移入 docs/research 并以规则+引用留在规格(任务要求以规范性规则为准,叙述本身不判实现缺陷)。
- **Q5 同段自相矛盾**:Core L22 先给 Copilot 定义 X-Interaction-Id 旋钮,同段又把 Copilot 列入"没有任何请求级旋钮"名单;ProviderPresets.cs:112 枚举注释同样把 Copilot 列在 None 下。应改为"其服务的模型无请求级旋钮,网关按头分桶"。
- **Q6 数字失真**:L123"18 项"vs AnthropicWire.Betas 实 17 项(注释亦称 18)。
- **Q7 模糊**:L122"两类重置时间……解析器都接受"两种读法(见 F-38);L22"被 400 拒的旋钮"未界定含头旋钮与否(见 F-13)。
- **Q8 措辞与实现微差**:L123"头部一律不复制"——探针实际从请求头提取 anthropic-beta(ClaudeCliProbe.ExtractBetas),应写"凭据头不复制/不记录/不落盘"。

## 业务闭环缺口

1. **凭据并发刷新**(F-34):无串行化、无刷新前重读;refresh token 轮换下并发回合可互相作废并致回合失败,写回竞态可把旧 token 覆盖回文件。四种设备码 bundle 有串行规则(App L29),Claude 订阅没有——闭环不对称。建议:Core 加实例级信号量+双检,规格补规则与测试。
2. **401 重试边界无测试**(缺测试清单 5):刷新后重试一次、首事件后不重试的边界是凭据闭环的关键防线,当前完全无测试;且 403 也在重试范围而规格只写 401。刷新失败→不可重试→回合快速失败,这条已闭环(ClaudeSubscriptionCredentialsTests::Failed_refresh_throws_without_the_token)。
3. **头旋钮 400 硬失败**(F-13):严格网关拒绝 X-Interaction-Id/x-grok-conv-id 时无一次性降级,与规格承诺的"旋钮被拒不失败"闭环不符。
4. **降级可见性与记忆**:Notice→AgentNotice→toast(AgentLoop.cs:195;App L146)与 Reload 清空备忘(ProviderServices.cs:91,111)均已闭环;缺跨实例复用测试(缺测试清单 4)。
5. **额度×重试×探测**:Classify 读数优先→RetryingChatProvider.IsQuota 不重试→AgentQuotaExhausted 上报→QuotaProbeSchedule 退避探测,全链有测试,闭环完整。唯一注记:重试层 Classify 用空 ModelSettings(RetryingChatProvider.cs:105-107),仅作布尔判定无副作用。
6. **token 泄露防护**:凭据异常/ToString/写回 Trace 均不含 token(有测试);Copilot 交换失败不回显 token(有测试);探针卫生静态成立但无测试(缺测试清单 1)。
7. **取消与超时**:空闲超时与调用方取消严格区分(IdleTimeoutStream+ReadEventAsync,ProviderTimeoutRegressionTests 全谱系)、刷新 20s、列表 20s、目录 60s,取消传播有测试,闭环完整。
