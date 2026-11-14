# App A(组合根/提供商配置/密钥/日志)审计发现

范围:`src/Forge.App/SPEC.md` 13–47 行(边界 15–20、组合根 24–38、诊断日志 42–46)及相关未决(460、462)。只读审计,未跑 build/test/git。结论:PASS(有测试)/PASS*(实现存在、无直接测试)/PARTIAL 等。
测试文件缩写(均在 tests/Forge.App.Tests/Providers/ 下,除注明):PST=ProviderStoreTests、PSS=ProviderSecretStoreTests、CWS=ClaudeWireSettingsTests、PCT=ProviderCatalogTests、PDT=ProviderDefinitionTests、PCr=ProviderCredentialsTests、Kimi=KimiAuthenticationTests、XAi=XAiAuthenticationTests、Meta=MetaAuthenticationTests、Gpt=ChatGptAuthenticationTests、OR=OpenRouterAuthenticationTests、MCR=ModelCatalogRefreshTests、MCS=ModelCatalogStorageTests、CPT=ComposerProviderTests、CHM=ComposerHiddenModelTests、CKR=CopilotKeyRouteTests、PCR=ProviderCatalogSettingsRowTests(Settings/)。另:ADT=Agents/AgentDefaultsStoreTests、ADR=Agents/AgentDefaultsRowTests、ADH=Settings/AgentDefaultsHiddenModelTests、PM=Composition/ProviderMetadataTests、ALS=Logging/AppLogSetupTests、AL=Logging/AppLogTests、TBL=Logging/TraceBridgeListenerTests、AVS=Logging/AvaloniaLogSinkTests、CDT=CompositionDisposeTests(根)。Core 测试在 tests/Forge.Core.Tests/Providers/:CSC=ClaudeSubscriptionCredentialsTests、CW=ClaudeWireTests、CA=CopilotAuthenticationTests、CCP=ClaudeCliProbeTests;PCmp=tests/Forge.Core.Tests/Prompts/PromptComposerTests.cs。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-A-01 | AOT、闭包无 Roslyn/DI/MVVM、仅源生成 JSON | SPEC:15 | Forge.App.csproj:4-5,19-27;Workbench/LayoutSnapshot.cs:150;Providers/ProviderDocuments.cs:363;Preferences/AppPreferences.cs:1124 | 无(静态核查:JsonSerializer 全走 context/typeInfo) | PASS* |
| F-APP-A-02 | 派生属性通知用 SetAndNotify | SPEC:15 | ViewModels/NotifyingObject.cs:17;Settings/ProviderEditorViewModel.cs:246,255 | 无 | PASS* |
| F-APP-A-03 | 无存储/Git 管道;禁 SqliteConnection/CREATE TABLE/write-tree/GIT_INDEX_FILE | SPEC:16 | grep 全 src/Forge.App 为空;仅组合 Core 存储(Composition/AppComposition.cs:510-514) | 无(静态) | PASS* |
| F-APP-A-04 | 审批库只读计数:无文件/无表→0,其余上抛"不可读";不持可写连接 | SPEC:17 | src/Forge.Approval/ApprovalStore.cs:134-170(ReadOnly+Pooling=false);Services/ApprovalCounter.cs:21;Services/SessionInstrumentSources.cs:71;Strings.Instruments.cs:355,452 | tests/Forge.Approval.Tests/ApprovalCountReadOnlyTests.cs::Missing_file_returns_zero_and_creates_nothing、Database_without_the_plugin_approval_table_returns_zero、Corrupt_file_throws_instead_of_returning_zero | PASS |
| F-APP-A-05 | UI 线程不碰存储;退出布局后台写、最多等 3 秒 | SPEC:18 | Preferences/AppPreferences.cs:1193-1220;Providers/ProviderServices.cs:57-60;ViewModels/MainWindowViewModel.cs:891-905 | 无(3 秒等待) | PASS* |
| F-APP-A-06 | 无 P/Invoke;只启动/重启 Host,退出杀树等待 | SPEC:19 | grep DllImport/LibraryImport 空;Composition/ExtensionHostSupervisor.cs:74;src/Forge.HostClient/HostProcess.cs:160-170 | 无(App 侧) | PASS* |
| F-APP-A-07 | 布局自写,无停靠框架 | SPEC:20 | csproj 无 Dock 包;Workbench/ | 无(静态) | PASS* |
| F-APP-A-08 | 构造顺序即依赖方向,拆除严格逆序 | SPEC:24 | Composition/AppComposition.cs:375-601、855-922 | CDT::DisposeAsync_releases_the_reviews_database_handle(仅钉 reviews) | PARTIAL |
| F-APP-A-09 | 数据根/常规工作区默认与两个环境变量覆盖 | SPEC:24 | Composition/AppPaths.cs:12,15,67,84-86 | 无 | PASS* |
| F-APP-A-10 | providers 段唯一配置源 {version,defaultId,items[]},字段齐全 | SPEC:26 | Providers/ProviderDocuments.cs:122-204,252;ProviderStore.cs:157-180 | PST::A_providers_section_with_an_item_per_provider_loads_in_order_with_its_default;PDT::A_definition_round_trips_through_the_stored_document | PASS |
| F-APP-A-11 | 旧单 provider 段首读迁移;首保存写 providers 删旧段 | SPEC:26 | ProviderStore.cs:99-112,287-362 | PST::The_legacy_single_provider_section_loads_as_one_connected_provider、The_legacy_section_migrates_to_the_protocol_and_step_it_names、A_legacy_kind_this_build_does_not_speak_is_unusable_rather_than_guessed、The_first_save_writes_providers_and_drops_the_legacy_section_but_keeps_unknown_fields | PARTIAL |
| F-APP-A-12 | JsonNode 整份读改写;未知字段保留;无效 JSON 拒写 | SPEC:26 | ProviderStore.cs:167-227,265-281 | PST::A_save_over_an_invalid_file_refuses_and_leaves_it_untouched、A_settings_file_whose_root_is_not_an_object_is_refused_instead_of_replaced | PASS |
| F-APP-A-13 | 零配置不载入/不写文件/无占位;横幅;模型列表空 | SPEC:26 | ProviderStore.cs:65-67,116-124;Strings.Providers.cs:312;Settings/ProvidersSettingsViewModel.cs:239 | PST::Nothing_configured_loads_no_provider_and_writes_nothing;CPT::An_empty_providers_section_offers_no_model_rather_than_a_placeholder | PASS |
| F-APP-A-14 | 密钥库 AES-GCM;key 32 字节、非 Win 0600;settings 只存 secretRef | SPEC:27 | ProviderSecretStore.cs:24-26,48,223-233,280-292,296-310 | PSS::A_secret_survives_a_new_store_instance_and_never_appears_in_the_file;PCT::A_valid_form_carries_only_a_reference_and_never_the_secret | PASS |
| F-APP-A-15 | 环境变量优先;行内「环境变量 key · VAR · 托管中」 | SPEC:27 | ProviderCatalog.cs:286-305;Strings.Providers.cs:381,402,553 | PCT::The_environment_variable_wins_over_a_stored_key | PASS |
| F-APP-A-16 | 库损坏报 Problem,不当"没有密钥"覆盖;无 DPAPI | SPEC:27 | ProviderSecretStore.cs:88-93,138-148,193-201 | PSS::A_tampered_payload_makes_the_store_report_a_problem_instead_of_looking_empty、A_write_to_an_unreadable_store_is_refused_instead_of_overwriting_it | PASS |
| F-APP-A-17 | 预设 7 家+自定义;协议/URL/变量/文档/种子/是否列模型 | SPEC:28 | src/Forge.Core/Providers/ProviderPresets.cs:130-262 | PDT::The_retired_built_in_presets_are_gone_and_a_stored_one_degrades_to_custom;各家预设行测试(见 F-64) | PASS |
| F-APP-A-18 | Anthropic 双凭据列模型;Kimi 不列、测试走最小对话 | SPEC:28 | ProviderServices.cs:283-289、225-246(VerifyChatAsync);ProviderPresets.cs:223 | MCR::A_claude_login_lists_the_accounts_models_through_the_versioned_endpoint;PCT::A_passing_test_fetches_the_models_and_the_save_writes_them | PASS |
| F-APP-A-19 | 订阅端点;FromPreset 按凭据种类取主/订阅端点 | SPEC:28 | ProviderPresets.cs:75-102,181-185;ProviderDefinition.cs:169-195 | PDT::The_openai_preset_is_one_vendor_reached_two_ways_and_the_credential_decides_the_endpoint | PASS |
| F-APP-A-20 | Copilot API 密钥走静态 api.individual.githubcopilot.com+明文 Bearer;设备码仍会话 token | SPEC:28 | src/Forge.Core/Providers/CopilotAuthentication.cs:56(实为 api.githubcopilot.com);ProviderCatalog.cs:489-497 | CKR::A_copilot_key_is_listed_on_the_static_host_as_a_plain_bearer_and_runs_no_session、The_editor_names_the_key_kind_and_keeps_the_static_base_url、Without_a_key_the_preset_still_asks_for_the_device_code_sign_in | PARTIAL |
| F-APP-A-21 | OpenCode Zen/Go/DeepSeek 退役;旧条目降级自定义保留 URL | SPEC:28 | ProviderDefinition.FromDocument 回落 Custom;ProviderPresets.cs:263-296 | PDT::The_retired_built_in_presets_are_gone_and_a_stored_one_degrades_to_custom、A_provider_stored_under_the_retired_chatgpt_preset_still_loads_as_custom_at_its_plan_endpoint | PASS |
| F-APP-A-22 | api-key(库或环境变量) | SPEC:29 | ProviderCatalog.cs:286-305,404-408 | PCT::A_provider_with_a_stored_key_is_ready_and_one_without_a_key_is_not | PASS |
| F-APP-A-23 | claude-subscription:复用本机凭据,5 分钟内刷新写回;anthropic-beta+身份首块 | SPEC:29 | Core/Providers/ClaudeSubscriptionCredentials.cs:60,168-174,325-352;ProviderCredentials.cs:1088-1098 | CSC::Token_expiring_within_five_minutes_is_refreshed_and_written_back、Expired_token_is_refreshed_and_a_missing_refresh_token_keeps_the_old_one;CW::The_claim_puts_the_billing_block_first_and_the_official_identity_second | PASS |
| F-APP-A-24 | copilot 设备码→ghu_→会话 token;Integration-Id 一致;baseUrl 取 endpoints.api | SPEC:29 | Core/Providers/CopilotAuthentication.cs:59,145,215,232-234;ProviderCredentials.cs:91-135 | CA::Poll_returns_the_github_user_token、Exchange_sends_the_headers_the_token_is_bound_to、Exchange_uses_the_api_host_github_reports | PASS |
| F-APP-A-25 | kimi-code:auth.kimi.com 设备码,Bearer | SPEC:29 | KimiAuthentication.cs:71,194-218;ProviderCredentials.cs:189-234 | Kimi::The_start_response_yields_the_user_code_and_both_urls、A_refresh_returns_the_rotated_bundle | PASS |
| F-APP-A-26 | xai:auth.x.ai,scope 含 grok-cli:access;刷新省略 refresh_token 沿用旧 | SPEC:29 | XAiAuthentication.cs:33,73,79,221 | XAi::The_start_request_carries_the_client_the_scopes_and_the_referrer、A_refresh_that_omits_the_rotating_token_keeps_the_one_it_used_and_defaults_to_an_hour | PASS |
| F-APP-A-27 | meta:设备码→identity→muse-code/key 铸 24h key,提前 5 分钟重铸 | SPEC:29 | MetaAuthentication.cs:33,77,84-95;ProviderCredentials.cs:599-640 | Meta::The_mint_carries_the_identity_token_and_the_api_version_and_returns_the_key、A_key_inside_the_five_minute_skew_is_re_minted_with_the_stored_identity_token | PASS |
| F-APP-A-28 | chatgpt:code+verifier→/oauth/token;JWT claim 账号 id;每请求两头 | SPEC:29 | ChatGptAuthentication.cs:84,93,110-115,143-144,298,385-390 | Gpt::The_exchange_posts_the_code_and_reads_the_account_out_of_the_access_token、A_token_without_the_account_claim_cannot_name_a_plan、A_refresh_re_reads_the_account_id_from_the_new_access_token | PASS |
| F-APP-A-29 | bundle 只存加密库(kimi:/xai:/meta:/chatgpt:+id),不进 settings/日志 | SPEC:29 | ProviderCredentials.cs:82,323-341,550 等 | Kimi/XAi/Meta/Gpt::A_successful_sign_in_persists_the_bundle_and_never_writes_it_to_the_settings(四份);OR::A_finished_sign_in_stores_the_key_where_a_pasted_one_goes_and_never_in_the_settings | PASS |
| F-APP-A-30 | 刷新按 provider 串行;401 仅当被拒 token 仍是当前值才清库 | SPEC:29 | ProviderCredentials.cs:41-47,198-234,254-264,400-436 | Kimi::Two_concurrent_reads_share_one_refresh_and_return_the_same_access_token、A_rejected_refresh_keeps_a_stored_bundle_whose_refresh_token_is_already_newer;XAi 同名两方法;Meta::A_refused_re_mint_keeps_a_stored_bundle_whose_identity_token_is_already_newer;Gpt::Two_concurrent_reads_share_one_refresh_and_return_the_same_rotated_bundle | PASS |
| F-APP-A-31 | claudeWire 形状;缺段/缺开关=开;只有字面 false 关 | SPEC:29 | ClaudeWireSettings.cs:51-74 | CWS::The_switch_is_one_boolean_and_only_false_turns_the_claim_off、A_switch_that_is_not_a_boolean_reads_as_the_default、A_missing_section_claims_the_official_client_with_the_documented_defaults | PASS |
| F-APP-A-32 | 关后逐字节同旧版;三字段形状校验,坏值退各自默认 | SPEC:29 | ClaudeWireSettings.cs:76-94;Core/Providers/AnthropicWire.cs | CWS::Wrong_shapes_fall_back_to_the_default_instead_of_disabling_the_claim、Values_are_taken_as_written_when_they_fit_their_shape;CW::Without_the_claim_the_request_is_what_it_was_before | PASS |
| F-APP-A-33 | 追加 version=本机 CLI(PATH→WinGet→local→VS Code);build 不写运行时问;note 忽略 | SPEC:29 | ProviderStore.cs:40,245-253;Core/Providers/ClaudeCliProbe.cs:54-77,314-331;ClaudeWireSettings.cs:42-48 | PST::Saving_a_claude_subscription_provider_appends_the_wire_section_with_its_notes;CWS::The_documentation_sentence_names_every_value_it_accepts;CCP | PASS |
| F-APP-A-34 | 只作用 claude-subscription;每次加载读入;无设置页 | SPEC:29 | ProviderCatalog.cs:475-477;ProviderStore.cs:92 | CW(声称仅 BearerOnly+Wire);PST(claudeWire 组) | PASS |
| F-APP-A-35 | 段只增删不改值;已有段原样;加载路径绝不写文件 | SPEC:29 | ProviderStore.cs:228-255(仅 Save 调用) | PST::A_section_the_user_edited_is_never_rewritten、Removing_the_last_claude_provider_removes_the_section_and_leaves_the_rest_alone、A_save_without_any_claude_provider_does_not_create_the_section | PARTIAL |
| F-APP-A-36 | 已知后果:删最后一个 Claude 丢 enabled:false | SPEC:29 | ProviderStore.cs:222-227,236 | PST::Removing_the_last_claude_provider_removes_the_section_and_leaves_the_rest_alone | PASS |
| F-APP-A-37 | 提示词跟开关(System/SystemNamed 除首句全同);用户模板优先 | SPEC:29 | Core/Prompts/BuiltInPrompts.cs:13-31;Agents/SessionRuntime.cs:962-967 | PCmp:48-49(SystemNamed.Replace("Forge, ","")==System) | PASS |
| F-APP-A-38 | 声称开=wire 生效且连着 claude-subscription | SPEC:29 | ProviderCatalog.cs:564-567,595;AppComposition.cs:563 | 无 | PASS* |
| F-APP-A-39 | 凭据首读才解析;首事件前 401 重试一次,之后不重试 | SPEC:29 | ProviderCredentials.cs:1046-1079(catch 只包首次 MoveNext)、1174 | 无(tests 无 CredentialedChatProvider 引用) | PASS* |
| F-APP-A-40 | 三源合并;/models 价格折每百万、provider-models.json 持久化;未知不补 | SPEC:30 | ProviderCatalog.cs:363-378;ProviderModelsFile.cs:22;Core ModelMetadataResolver | PM::The_provider_window_beats_the_catalog_while_the_user_override_beats_the_provider、The_model_list_metadata_maps_a_reported_window_and_per_token_prices、An_override_with_only_a_cache_price_invents_nothing;MCS::A_provider_model_list_survives_a_restart_with_its_order_and_metadata | PASS |
| F-APP-A-41 | Memo 按(提供商,模型);三处整体作废;纯内存 | SPEC:30 | ModelMetadataMemo.cs:26-64;ProviderServices.cs:52-56,98,143,247 | MCS::A_resolved_metadata_is_memoized_and_dropped_when_an_input_changes(仅单元级) | PASS* |
| F-APP-A-42 | /models 刷新显式(点击+每日);启动不逐个访问 | SPEC:30 | ModelCatalogRefreshService.cs:111-114,148-150 | MCR::The_startup_check_fetches_the_snapshot_and_leaves_the_provider_lists_alone、The_daily_run_refreshes_every_ready_provider_list | PASS |
| F-APP-A-43 | CreateProvider 按 ProviderId,空/未知回落默认;重试中间件 | SPEC:31 | ProviderCatalog.cs:776-789,416-434 | PCT::The_default_is_returned_for_a_missing_id_and_an_unknown_id_falls_back_to_it、An_empty_catalogue_creates_a_provider_that_reports_the_missing_configuration | PASS |
| F-APP-A-44 | composer 并集;>1 家显示提供商名;选中定 ProviderId;默认只模型+强度 | SPEC:31 | ViewModels/ComposerViewModel.cs;Services/ComposerPreferences.cs:20-21,46-47 | CPT::Two_providers_produce_a_union_with_a_provider_caption_per_row、Picking_a_row_sets_both_the_model_and_the_provider_of_the_next_turn、A_single_provider_does_not_repeat_its_name_on_every_row | PASS |
| F-APP-A-45 | modelsHidden 唯一定义 IsModelHidden;当前模型例外;只重过滤不重建 | SPEC:31 | AppPreferences.cs:595;ComposerViewModel.cs:981;AgentDefaultsRowViewModel.cs:61 | CHM::A_hidden_model_is_not_offered_while_the_current_model_stays、Unhiding_brings_a_model_back_without_rebuilding_the_composer | PASS |
| F-APP-A-46 | defaultContextWindow 一属性两写法;统一解析;1–1000%、"50 %" | SPEC:32 | ContextWindowDefault.cs:64-117 | PCT::A_share_typed_in_the_section_is_accepted_and_saved_as_a_string、A_rejected_global_window_is_reported_with_the_invalid_message_and_nothing_is_written;PST::The_global_context_window_round_trips_a_count_as_a_number_and_a_share_as_a_string | PASS |
| F-APP-A-47 | 优先级链;绝对值让位指名窗口覆盖;百分比 floor≥1;未知不造数 | SPEC:32 | ContextWindowDefault.cs:131-152;ProviderCatalog.cs:753-773 | PCT::The_global_window_overrides_the_catalog_and_a_per_model_window_still_wins、A_share_window_scales_the_window_the_chain_already_knows、A_share_window_is_clamped_to_one_token_and_may_exceed_one_hundred_percent | PASS |
| F-APP-A-48 | 价格覆盖不挡全局窗口;空=删属性;非法不保存并报错 | SPEC:32 | ProviderCatalog.cs:760-768;ProvidersSettingsViewModel.cs:322-332;ProviderStore.cs:192-199 | PCT::An_invalid_global_window_is_reported_and_not_saved | PASS |
| F-APP-A-49 | 百分比=字符串/绝对=数字;旧版按无默认;只改两字段 | SPEC:32 | ContextWindowDefault.cs:104-127;ProviderStore.cs:186-215 | PST::The_global_context_window_round_trips_and_leaves_unknown_catalog_fields_alone | PASS |
| F-APP-A-50 | 四价格保留;默认三列;缓存写列仅 Anthropic 或非零缓存写价;空=未知 | SPEC:33 | Settings/ModelMetadataViewModel.cs:34-35,65 | PCT::The_cache_write_column_only_appears_where_it_is_billed、Metadata_rows_never_write_a_zero_for_an_unknown_half;PDT::Metadata_overrides_never_invent_a_zero | PASS |
| F-APP-A-51 | 只读能力列(模态+档位数)来自快照,未知留空 | SPEC:33 | Settings/ModelMetadataViewModel.cs | PCR::The_capability_summary_names_inputs_and_reasoning | PASS |
| F-APP-A-52 | sourceUrl 默认 models.dev;每日抓写紧凑快照;启动后台读 | SPEC:34 | ModelCatalogStore.cs:28,42;ModelCatalogRefreshService.cs:104-150;src/Forge.Abstractions/Automations.cs:94-95(每日 05:00) | MCS::A_stored_snapshot_is_read_back_by_the_next_start、A_damaged_snapshot_reads_as_no_catalog_with_a_stated_problem;MCR::The_daily_run_asks_again_and_a_304_keeps_the_snapshot | PASS |
| F-APP-A-53 | 请求侧无过滤;全量入库;预设→目录映射,自定义全局兜底首方优先 | SPEC:34 | Core ModelCatalogFetcher(仅 url+etag)、ModelCatalogView.Lookup;ProviderCatalog.cs:369 | Core ModelCatalogFetcherTests;PM::A_model_the_settings_do_not_list_falls_back_to_the_catalog_snapshot(外部实测声明无法联网复核) | PASS |
| F-APP-A-54 | ETag 仅同源;改址强制 GET;304 记 checkedAt;失败旧快照生效;改址即重抓 | SPEC:34 | ModelCatalogRefreshService.cs:126-145;ProvidersSettingsViewModel.cs:339-377 | MCR::A_changed_address_does_not_send_the_old_sources_validator、A_304_still_counts_as_todays_check、A_failed_fetch_keeps_the_catalog_the_app_already_had;PCR::A_new_address_is_saved_and_fetched_at_once、An_unchanged_address_saves_without_fetching | PASS |
| F-APP-A-55 | settings.json 写者共用按路径互斥;非对象根/无效 JSON 拒写 | SPEC:35 | Composition/SettingsFileMutex.cs:14-25;7 写者全 Acquire(见未记录 2) | 无 | PASS* |
| F-APP-A-56 | agents 段共用文件规则:只替换节点、未知保留、同锁+AtomicFile、拒无效 | SPEC:36 | Agents/AgentDefaultsStore.cs:135-186 | ADT::A_save_keeps_unknown_sections_unknown_agent_ids_and_unknown_entry_fields、A_save_over_an_invalid_file_refuses_and_leaves_it_untouched、A_save_over_a_non_object_root_refuses_and_leaves_it_untouched | PASS |
| F-APP-A-57 | 加载不抛;损坏按空表;自己→Worker→首提供商首模型+Off;无提供商无模型 | SPEC:36 | AgentDefaultsStore.cs:94-113,211-247 | ADT::A_missing_file_loads_empty_and_never_throws、A_missing_or_broken_agents_node_loads_as_an_empty_map、An_own_entry_wins_over_the_worker_entry、Without_entries_the_first_model_of_the_first_connected_provider_runs_at_off、Without_a_connected_provider_an_agent_has_no_model | PASS |
| F-APP-A-58 | ThinkingStepsFor:表优先、effort 兜底、夹到最近接受档 | SPEC:36 | ProviderCatalog.cs:386-392,719-745;ThinkingEfforts.Clamp | CPT::Changing_to_a_model_with_fewer_steps_clamps_the_effort_to_an_accepted_one;ADR | PASS |
| F-APP-A-59 | SessionRuntime 替换占位 ModelSettings("default");根用 main | SPEC:36 | Agents/SessionRuntime.cs:968,1019-1043 | Agents/SessionRuntimeTemplatesTests.cs(AgentDefaults 接线) | PASS |
| F-APP-A-60 | 先提交配置再写密钥;被拒旧密钥不动;仅刷新模型先落密钥 | SPEC:37 | Settings/ProviderEditorViewModel.cs:375-385、497-507(draft) | PCT::A_refused_settings_write_keeps_the_dialog_open_and_the_old_key、A_test_on_an_existing_provider_leaves_its_stored_key_until_the_save_succeeds | PASS |
| F-APP-A-61 | 新 id 去重(同名加序号);按 id 匹配 | SPEC:37 | ProviderEditorViewModel.cs:296;ProviderStore.cs:364-380 | PCT::A_second_provider_with_the_same_name_gets_its_own_id | PASS |
| F-APP-A-62 | 分区:横幅/全局窗口/目录行(失焦/Enter 提交、非 http(s) 报错、改址重抓、刷新禁用、失败下行) | SPEC:38 | ProvidersSettingsViewModel.cs:93-100,322-393;ProvidersSettingsView.axaml:69-70,168-169 | PCR::Before_any_fetch_the_row_says_it_never_updated、After_a_fetch_the_row_says_when_and_how_much、An_address_that_is_not_absolute_http_is_reported_and_nothing_is_saved | PASS |
| F-APP-A-63 | 已连接行;删除两次点击 | SPEC:38 | ProvidersSettingsViewModel.cs:763-770;ProvidersSettingsView.axaml.cs:104;Strings.Providers.cs:327 | 无(ConfirmDelete) | PASS* |
| F-APP-A-64 | 自定义区文案+添加;内置行按能力出按钮;同家多份 | SPEC:38 | Strings.Providers.cs:318;BuiltInProviderRowViewModel | CKR::The_preset_offers_a_key_beside_the_device_code_sign_in;Kimi::The_kimi_preset_offers_a_key_and_a_sign_in_and_defaults_to_the_sign_in;OR::The_openrouter_preset_offers_a_key_and_a_sign_in_and_its_row_shows_both_buttons | PASS |
| F-APP-A-65 | 编辑器:名称/URL(仅自定义)/密钥+头;复选框=密钥 vs 变量名(预设起填) | SPEC:38 | ProviderEditorViewModel.cs:115-121,188-204;ProviderEditorWindow.axaml:32,36 | PCT::Environment_mode_stores_the_variable_name_and_switching_back_clears_it、An_incomplete_form_reports_why_it_cannot_be_saved | PASS |
| F-APP-A-66 | headers 第一冒号/大小写不敏感/后行覆盖/错报行号;覆盖同名内置头;{session} 无会话不发 | SPEC:38 | ProviderEditorViewModel.cs:325-352;Core/Providers/ProviderHeaders.cs:12-38;HttpChatProvider.cs:218-222;ProviderModelListing.cs:105 | PCT::Request_headers_round_trip_through_the_box_and_the_stored_document、A_malformed_header_line_is_refused_with_its_number、Editing_the_headers_asks_for_a_new_test | PASS |
| F-APP-A-67 | 测试→保存;改 URL/密钥/头回测试态;取消删测试密钥;编辑已通过直接保存 | SPEC:38 | ProviderEditorViewModel.cs:100,167-180,357-476,488-528 | PCT::A_passing_test_fetches_the_models_and_the_save_writes_them、A_failed_test_keeps_the_test_button_and_reports_the_reason、Cancelling_after_a_test_removes_the_draft_key_and_never_writes_the_real_one、Editing_a_passing_provider_saves_directly_until_the_address_changes | PASS |
| F-APP-A-68 | Claude 登录:检测命令+登录态;安装指引+重查(激活也查);复用凭据、尽力列模型 | SPEC:38 | Settings/ProviderLoginViewModel.cs:97-115,194-228;ProviderLoginWindow.axaml.cs:29,45 | PCr::A_credentials_file_with_an_access_token_reads_as_signed_in(凭据层;UI 无) | PASS* |
| F-APP-A-69 | 六登录分支:5 设备码+OpenRouter 回环 | SPEC:38 | ProviderLoginViewModel.cs:284,372,424,479,531,552 | 凭据层全覆盖(各 SignIn 测试);VM 无 | PASS* |
| F-APP-A-70 | Meta「正在开通访问」+铸钥;action_url 显示;ChatGPT user code+种子模型 | SPEC:38 | ProviderLoginViewModel.cs:472-484,508,525-531,589 | Meta::A_mint_without_a_key_says_where_to_finish_the_setup、A_successful_sign_in_shows_the_code_then_enables_access_and_persists_the_bundle | PASS |
| F-APP-A-71 | OpenRouter PKCE+127.0.0.1 回环+auth/keys→普通 API 密钥,无刷新 | SPEC:38 | OpenRouterAuthentication.cs:36,92-136 | OR::The_authorize_url_carries_the_callback_the_challenge_and_S256、The_exchange_posts_the_code_and_the_verifier_and_returns_the_key、A_cancelled_sign_in_releases_the_listener_and_stores_nothing | PASS |
| F-APP-A-72 | 弹窗不编辑模型清单等;Anthropic 默认最新 Opus,其余第一项 | SPEC:38 | ProviderEditorViewModel.cs:296-318;ProviderDefinition.cs:205-228 | PDT::The_preferred_default_is_the_newest_opus_for_anthropic_and_the_first_id_elsewhere、The_anthropic_preset_lists_its_models_and_its_plan_starts_on_the_newest_opus | PASS |
| F-APP-A-73 | Agent 默认 UI:继承项/思考夹档/消失隐藏型号追加/线程池写/拒写显示原因 | SPEC:38 | AgentDefaultsRowViewModel.cs:50-146;ProvidersSettingsViewModel.cs:601 | ADR::A_stored_model_no_provider_lists_is_appended_and_stays_selected;ADH::A_hidden_model_is_not_offered、A_stored_default_that_is_hidden_is_still_shown_and_kept_selected | PASS |
| F-APP-A-74 | MEL+ZLogger 仅 App 引用;其他项目 Trace | SPEC:42 | csproj:25-26;Core ClaudeSubscriptionCredentials.cs:348 | 无(静态) | PASS* |
| F-APP-A-75 | AppLog 唯一工厂(默认 Null);Avalonia 前配置;退出 Dispose 冲刷 | SPEC:42 | Logging/AppLog.cs:15;Program.cs:15-16;AppLogSetup.cs:24-52 | ALS::The_file_factory_writes_one_plain_text_line_per_entry_and_flushes_on_dispose、Configure_installs_the_file_factory_and_the_bridge_and_teardown_is_idempotent | PASS |
| F-APP-A-76 | Create 调用时刻解析工厂;AppLogs 领域类别;ZLogger 插值 | SPEC:42 | AppLog.cs:20-23,65-89;AppLogs.cs:8-46 | AL::A_logger_created_before_a_factory_is_installed_reaches_the_factory_installed_later | PASS |
| F-APP-A-77 | AppLog.Use 换工厂;无 DI 容器 | SPEC:42 | AppLog.cs:30-40 | AL::Disposing_the_scope_restores_the_previous_factory_for_existing_loggers、Disposing_a_scope_twice_does_not_undo_a_later_installation | PASS |
| F-APP-A-78 | TraceBridge:[tag]→类别否则 trace;级别映射;category 只作前缀 | SPEC:43 | TraceBridgeListener.cs:18,58-85,88-105 | TBL::Parse_splits_a_leading_tag、Event_types_map_to_log_levels、A_category_argument_is_kept_in_the_text_and_does_not_change_the_level、Concurrent_writers_each_produce_whole_lines | PASS |
| F-APP-A-79 | AvaloniaLogSink:Warning+、保留级别、area 类别、位置填值、不抛 | SPEC:43 | AvaloniaLogSink.cs:14-61;Program.cs:29 | AVS::A_warning_keeps_its_level_and_logs_under_its_area_with_the_template_filled、Levels_below_the_minimum_are_neither_enabled_nor_logged、Templates_fill_holes_left_to_right_and_keep_what_has_no_value | PASS |
| F-APP-A-80 | App 不再写 Trace | SPEC:43 | grep src/Forge.App 为空(仅注释) | 无(静态) | PASS* |
| F-APP-A-81 | 配置失败空操作照常启动;崩溃 Critical+冲刷、未观察 Warning;释放后退化 Null | SPEC:43 | AppLogSetup.cs:36-45,58-79;AppLog.cs:83-85,105-107 | ALS::Configure_with_an_unwritable_log_directory_keeps_the_app_running_without_a_file;AL::A_factory_disposed_before_first_use_does_not_throw_from_IsEnabled_Log_or_BeginScope(CaptureCrashes 无测试) | PASS |
| F-APP-A-82 | 滚动日志按天+10MB;后台删 14 天前,失败忽略 | SPEC:44 | AppLogSetup.cs:16-18,118-123,131-172 | ALS::FileName_uses_the_local_day_and_the_sequence、PruneOldLogs_deletes_only_old_log_files、PruneOldLogs_survives_a_file_that_is_held_open | PASS |
| F-APP-A-83 | 一行一条 本地时间 ms+级别缩写+类别;ZLogger 直写 UTF-8;无 JSON | SPEC:44 | AppLogSetup.cs:123,175-179 | ALS::The_file_factory_writes_one_plain_text_line_per_entry_and_flushes_on_dispose | PASS |
| F-APP-A-84 | 默认 Information;FORGE_LOG_LEVEL 覆盖;FORGE_TRACE_FILE 废弃 | SPEC:44 | AppLogSetup.cs:12,81-108;grep 全库无 FORGE_TRACE_FILE | ALS::ParseLevel_accepts_the_documented_names_and_falls_back_to_information | PASS |
| F-APP-A-85 | 不记密钥/token/头值/正文;吞异常 Warning、失效 Error | SPEC:45 | ProviderSecretStore.cs:198(仅异常类型);ProviderCredentials.cs:1172;ClaudeSubscriptionCredentials.cs:348-351(仅路径) | CA::Exchange_failure_does_not_echo_the_user_token;CSC::Malformed_json_throws_without_exposing_the_token、Failed_refresh_throws_without_the_token(抽查) | PASS |
| F-APP-A-86 | 热路径不写 Information 及以下 | SPEC:46 | grep Transcript/TurnText/Workbench 无;ViewModels 仅 SessionWorkspace.cs:2000(关标签事件) | 无(静态抽查) | PASS* |
| F-APP-A-87 | 未决:TurnCheckpointService 归属未定 | SPEC:460 | AppComposition.cs:519(仍在 App) | 与未决状态一致 | PASS |
| F-APP-A-88 | 未决:ReviewStore 靠 ClearAllPools 释放 | SPEC:462 | Core/Storage/SqliteSessionStore.cs:333,353 | CDT::DisposeAsync_releases_the_reviews_database_handle | PASS |

计数:总 88;PASS 67;PASS* 17;PARTIAL 4(F-08/11/20/35);FAIL/MISSING/SPEC_GAP/UNVERIFIED 0。

## 非 PASS 项详情

### F-APP-A-08 PARTIAL — 拆除非严格逆序;FileArtifactStore 从不释放
证据:构造 AppPaths→preferences(:303)→network(:361)→审批路径(:375)→host(:389)→ApprovalServices(:394)→workspace(:402)→mcp(:426)→editor(:451)→providers(:460)→5 存储+artifacts(:510-518)→checkpoints/worktrees(:519-520)→runtimes(:601)。拆除(:855-922):automations→runtimes→**approvals(:883)**→五存储(:888-892)→editor→mcp→workspace→host→httpClient(:911)→…→preferences。approvals 构造早于 workspace/mcp/editor/存储却先拆(注释给理由:先放会话处理器再撤卡);`artifacts`(FileArtifactStore,IAsyncDisposable,Core/Storage/FileArtifactStore.cs:13)在 DisposeAsync 中无任何释放调用,池化句柄靠 SqliteSessionStore 的进程级 ClearAllPools 顺带释放——与未决 462 的 ReviewStore 同构但未记录。
缺口:「严格逆序」绝对化表述与偏离矛盾;artifacts 未释放是真实资源缺口,测试只钉 reviews 一处。
建议:规格改为「按依赖逆序,审批/通知有注明例外」;artifacts 纳入 DisposeAsync 或并入未决 462;补拆除断言。类型:规格修订+代码小修+补测试。

### F-APP-A-11 PARTIAL — 迁移映射与规格文字不符
证据:ProviderStore.cs:297-313:旧段 `openai-responses` 保留 OpenAiResponses 协议(预设=Custom),非规格「其余→自定义 OpenAI 兼容」;未知 kind 判不可用(:308-311)不迁移。测试钉住代码行为(PST::The_legacy_section_migrates_to_the_protocol_and_step_it_names、A_legacy_kind_this_build_does_not_speak_is_unusable_rather_than_guessed)。
缺口:按规格字面实现会把 responses 协议降级成 chat/completions。
建议:规格改「Anthropic→Anthropic 预设;其余已知协议→自定义(保留协议);未知协议不可用」。类型:规格修订。

### F-APP-A-20 PARTIAL — Copilot API 密钥静态主机与规格不一致
证据:SPEC:28 写 `api.individual.githubcopilot.com`;代码 CopilotAuthentication.cs:56 `DefaultApiHost="https://api.githubcopilot.com"`,测试 CKR:73 断言 `https://api.githubcopilot.com/models`。其余行为(明文 Bearer、无会话交换、无集成头、设备码不变)符合规格且有测试。`api.individual…` 仅是 Core 测试中服务端返回 endpoints.api 的样例(CA:146)。
建议:修正规格主机名(实现+测试一致)。类型:规格修订。

### F-APP-A-35 PARTIAL — 「加载路径绝不写文件」被旧密钥迁移打破
证据:claudeWire 治理本身完全符合(只增删不改值、仅 Save 触发、测试齐全)。但同句「加载路径绝不写文件(启动不得编辑用户文件)」与 ProviderStore.cs:358→390-401 冲突:Load 迁移分支 AdoptLegacyLiterals→secrets.Set,启动时写 provider-secrets.json(搬旧段明文密钥入加密库,注释明示有意,失败仅 Warning)。settings.json 在 Load 中确实不写。
建议:规格限定「加载绝不写 settings.json;旧段明文密钥首读搬入密钥库是唯一例外」。类型:规格修订(澄清)。

## 已实现但缺测试清单(PASS* 汇总)

| 项 | 缺的测试 |
|---|---|
| F-01/03/07/74/80 | AOT/禁词/无 Trace 等静态约束无守卫测试 |
| F-02 | SetAndNotify 派生通知无直接单测 |
| F-05 | 退出 3 秒有界等待、超时只记日志 |
| F-06 | App 侧杀树等待(HostProcess 层在 HostClient) |
| F-09 | AppPaths 两个环境变量覆盖 |
| F-38 | ClaimsOfficialIdentity 双条件(仅 API 密钥安装不声称) |
| F-39 | CredentialedChatProvider 首读解析/首事件前 401 重试一次/之后不重试——整条无测试(重复输出回归风险最高) |
| F-41 | Memo 三处作废接线(保存/刷新/快照),仅单元级 Set/Invalidate |
| F-55 | SettingsFileMutex 两写者交错不丢段的并发测试 |
| F-63 | 删除两次点击 ConfirmDelete |
| F-68/69 | ProviderLoginViewModel Claude 检测/激活重查与六分支 UI 接线(凭据层已覆盖) |
| F-86 | 热路径日志禁写守卫 |
| 另 | F-81 CaptureCrashes(Critical+冲刷、未观察 Warning);F-14 POSIX 0600(Windows 环境无法覆盖) |

## 程序→规格:未记录行为

1. **usageLog 段+用量记录器**:ProviderServices.cs:41-43 建 UsageLogSettingsStore(settings.json `usageLog` 段)+JsonlUsageRecorder(`<数据根>/usage/`),设置页有行。App/Core SPEC 均无记载。
2. **settings.json 实际 7 个写者**(规格说 3):preferences(AppPreferences.cs:1303)、providers(ProviderStore.cs:167)、agents(AgentDefaultsStore.cs:150)、compaction(CompactionSettingsStore.cs:105)、network(NetworkSettingsStore.cs:86)、usageLog(UsageLogSettings.cs:65)、web(WebServices.cs:83)。全走同一互斥锁,安全性成立,枚举过时。
3. **互斥锁仅进程内**:SettingsFileMutex.cs:10-11 明示双实例不在范围(各写有效文件但可能互丢段);规格未提。
4. **403 与 401 同待遇**:清库条件与流重试均含 403(ProviderCredentials.cs:220,422;IsAuthFailure:1174)。规格只写 401。
5. **Kimi 刷新余量 60 秒**(KimiAuthentication.cs:30),其余家 5 分钟;规格未写。
6. **claudeWire.version 每段 1–4 位上限**(ClaudeWireSettings.cs:87);规格只说三段数字。
7. **agents 段接受用户模板 id**:AgentIds.IsKnown=PromptTemplateIds.IsValid(AgentDefaultsStore.cs:31),不止五个内置 id;有测试(ADT::A_user_agent_id_round_trips_and_the_name_maps_to_the_same_id)。
8. **启动加载写密钥库**:旧段明文密钥迁移(ProviderStore.cs:390-401),见 F-35。
9. **用户头不能覆盖凭据绑定头**:BuildConfig 先用户头后 ChatGPT 账号头/Copilot 集成头(ProviderCatalog.cs:481-497),同名内置赢——「覆盖同名内置头」的合理例外(集成 id 与 token HMAC 绑定)未记录。
10. **编辑已有提供商测试也写 draft 密钥**(ProviderEditorViewModel.cs:497-507),取消删 draft 真密钥不动;规格「只针对未保存的新提供商」窄于实现(实现更安全)。
11. **modelCatalog 节点清空即整删**(ProviderStore.cs:211-214)。
12. **「立即更新」当日已抓则整趟跳过**(ModelCatalogRefreshService.cs:111-114,Manual;测试钉住),与按钮字面语义有落差,规格未定义。
13. **FileArtifactStore 是 forge.db 第 6 个存储且从不 Dispose**(AppComposition.cs:518;SPEC:137 有载,:24 说 5 个)。
14. 过时注释:ProviderCatalog.cs:357-360 称链含「built-in table」,Core SPEC:129 明确无内置价格表。

## 规格质量问题

1. **跨规格重复(提供商段)**:App:29(claudeWire 默认/入口实测/提示词配对/ClaudeCliProbe)↔ Core:123(同套规则全文含 2026-11-13 实测;实测叙述第三份在 ClaudeWireSettings.cs:33-37 注释);App:28(预设与退役)↔ Core:127;App:30(三源合并)↔ Core:129;App:34(ETag/304/失败回退)↔ Core:129。建议单一归属:线级/解析归 Core,配置口/UI/维护归 App,另一侧只留引用。
2. **跨规格矛盾**:Core:123 把 ClaimsOfficialIdentity 解释为「即 claudeWire 是否生效」,漏掉 App:29 要求且代码实现的第二条件(连着 claude-subscription,ProviderCatalog.cs:564-567)。
3. **计数错误/内部矛盾**:App:29「六种认证」实列 7 种;同条「四种设备码登录」与 App:38「六个分支…五个设备码」矛盾(Copilot 也是设备码,ghu_ token 同样只存加密库,引用 `copilot:` ProviderCredentials.cs:82)。
4. **过时命名**:App:24 `ProviderSettings` 不存在(实为 ProviderServices,AppComposition.cs:460);App:15 `SettingsJsonContext` 全库不存在(实为 ProvidersJsonContext/PreferencesJsonContext/WorkbenchJsonContext 等);App:24「5 个存储」与 :137 artifacts 不一致。
5. **与实现矛盾的绝对化表述**:App:24「拆除严格逆序」(F-08);App:29「加载路径绝不写文件」(F-35);App:28 Copilot 主机名错(F-20);App:26 迁移「其余→自定义 OpenAI 兼容」(F-11)。
6. **模糊**:App:34/38 未定义「立即更新」是否强制重抓(实现当日跳过);App:29 version 未写每段位数上限;App:35「三个写者」枚举过时(实际 7)。
7. **历史叙述**:App:28「OpenCode Zen/Go/DeepSeek 已移除」是迁移史非现行契约,宜移入变更注记;:29 入口实测长段与 Core:123 重复。

## 业务闭环缺口

1. **401 流级重试闭环无测试**(最高优先):「首事件前刷新重试一次、之后不重试」防重复输出的关键不变量整条无测试(F-39);凭据层并发/条件清库测试齐全,流级语义只靠代码审查成立。建议用 ScriptedAuthHandler 补两事件流测试。
2. **settings.json 并发闭环无测试**:闸门正确、7 写者全接入,但无「两写者交错不丢段」测试(F-55);跨进程限制未入规格。损坏拒写保原文件的测试齐全。
3. **拆除闭环**:artifacts 不释放、approvals 先拆偏离规格(F-08);CompositionDisposeTests 只钉 reviews.db;退出资源完整性依赖进程级 ClearAllPools 副作用,脆弱且未记录。
4. **密钥库闭环完整**:损坏→Problem→拒写、刷新串行+条件清库、draft 取消即删、先配置后密钥均有实现有测试;唯一规格冲突是加载期迁移写库(F-35),属措辞问题。
5. **目录刷新闭环完整**:同源 ETag、304 记 checkedAt、失败保旧快照、改址强制 GET+立即重抓、启动后台读全有测试;残留 Manual 当日跳过与「立即更新」字面落差(未记录 12)。
6. **日志内容边界**:抽查未见密钥/token/头值入日志,Copilot/Claude 错误不回显 token 有 Core 测试;但 App 侧无系统性脱敏守卫,ModelCatalogRefreshService.cs:137,198 把服务端失败消息(可能含响应体片段)原样记 Warning——低概率渗漏点,建议约束异常消息或补「日志不含密钥」捕获测试。
