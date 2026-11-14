# App F(设置分区/工具面/MCP 管理/提示词模板/面板)审计发现

范围:src/Forge.App/SPEC.md 行 389(工具目录)、391(模型可见性)、393(agents/idle-archive)、395(MCP 管理)、397(MCP 工具面)、399(压缩与 /compact)、403(Web)、405(网络)、413(chat)、417(files)、60(instrument)、419–432(模板)、434–448(面板)、450–464(未决)。只读审计,行号为实际文件行号(任务给定行号整体偏大 1)。
**路径约定**:代码路径省略 `src/Forge.App/` 前缀(SPEC.md 即 src/Forge.App/SPEC.md);测试路径省略 `tests/Forge.App.Tests/` 前缀;其余项目路径写全。测试证据格式 文件::方法。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-F-01 | tools 分区按来源分组,内置固定 8 组顺序,扩展/MCP 只读注明跟随来源 | SPEC.md:389 | ViewModels/Settings/ToolsSettingsSectionViewModel.cs:66-77,131-186;ToolRowViewModel:20-24 | Settings/ToolSwitchSettingsTests.cs::Built_in_groups_come_first_in_fixed_order_then_extension_then_mcp| PASS |
| F-APP-F-02 | read_artifact+3 子 Agent 控制工具必需,无开关,文件写了也忽略 | SPEC.md:389 | Agents/ToolCatalog.cs:28-31,53-62;Agents/SessionRuntime.cs:627 | Settings/ToolSwitchSettingsTests.cs::Required_tools_are_never_switched_off_even_when_the_file_names_them| PASS |
| F-APP-F-03 | 关 spawn_agent 或 agents 开关→同一禁用集合,每回合重应用 | SPEC.md:389,393 | Agents/SessionRuntime.cs:608,617-629,567 | Agents/SessionRuntimeSubAgentLimitsTests.cs::Switching_spawning_off_takes_spawn_agent_out_and_keeps_the_control_tools| PASS |
| F-APP-F-04 | 同名工具首个注册者为准(内置优先) | SPEC.md:389 | ToolsSettingsSectionViewModel.cs:139-143;SessionRuntime.cs:1230-1236 | Settings/ToolSwitchSettingsTests.cs::The_first_registration_of_a_name_wins_in_the_catalog| PASS |
| F-APP-F-05 | toolsDisabled 去空白/去重/序数排序/空不写;每回合 SetDisabledTools;集合不变不重发布 | SPEC.md:389 | Preferences/AppPreferences.cs:375-396,754,866-872;SessionRuntime.cs:567,627-628 | Settings/ToolSwitchSettingsTests.cs::The_disabled_list_is_trimmed_deduplicated_and_sorted| PASS |
| F-APP-F-06 | 目录来自活动会话 DescribeTools:Core 走 DescribeCore、插件 Extension、run_code+5 个 extension_* 为扩展编写组 | SPEC.md:389 | Agents/SessionRuntime.cs:1073-1163;ViewModels/MainWindowViewModel.cs:197 | 无(DescribeTools 分类无直接测试;分组经 Build 合成目录测试) | PASS* |
| F-APP-F-07 | models-visibility order110 AGENT 组 provider 之后;每提供商每模型一行+复选框+提供商名 | SPEC.md:391 | Composition/RuntimeSettingsComposition.cs:37-45;Settings/ModelsVisibilitySectionViewModel.cs:163-181;Preferences/SettingsSection.cs:191 | Settings/ModelsVisibilitySectionTests.cs::One_row_per_model_of_every_connected_provider | PASS |
| F-APP-F-08 | 说明行/页内搜索不落盘/N 个已隐藏按名去重 .one/.other/无提供商空状态 | SPEC.md:391 | ModelsVisibilitySectionViewModel.cs:117-156,255-273;Localization/Strings.ModelsVisibility.cs:61-62 | Settings/ModelsVisibilitySectionTests.cs::The_hidden_count_follows_the_preference_in_both_languages| PASS |
| F-APP-F-09 | Reload 选中时单趟构建;搜索只重填集合不重建行;ListBox 虚拟化 | SPEC.md:391 | ModelsVisibilitySectionViewModel.cs:163-222;Settings/ModelsVisibilitySectionView.axaml:40 | Settings/ModelsVisibilitySectionTests.cs::The_search_reuses_the_rows_and_never_re_reads_the_catalogue| PASS |
| F-APP-F-10 | modelsHidden 规范化同 toolsDisabled;两提供商同名模型共旗标 | SPEC.md:391 | AppPreferences.cs:580-601,800,866-872;ModelsVisibilitySectionViewModel.cs:236-244 | Settings/ModelsVisibilitySectionTests.cs::The_hidden_list_is_trimmed_deduplicated_and_sorted| PASS |
| F-APP-F-11 | 会话选型器与 Agent 默认下拉经 IsModelHidden;PropertyChanged 让打开的 composer 重过滤 | SPEC.md:391 | ViewModels/ComposerViewModel.cs:981,1017;Settings/AgentDefaultsRowViewModel.cs:61 | Providers/ComposerHiddenModelTests.cs::Unhiding_brings_a_model_back_without_rebuilding_the_composer| PASS |
| F-APP-F-12 | 当前模型不因隐藏消失;隐藏的已保存默认值按“无人列出”路径补行 | SPEC.md:391 | ComposerViewModel.cs:981;AgentDefaultsRowViewModel.cs:61 | Providers/ComposerHiddenModelTests.cs::A_hidden_model_is_not_offered_while_the_current_model_stays| PASS |
| F-APP-F-13 | agents 分区两旋钮:上限 1–16 默认 4、派生开关默认开,键名正确 | SPEC.md:393 | Settings/AgentRuntimeSectionsViewModel.cs:15-53;AppPreferences.cs:90-93,400-407 | Settings/AgentsSettingsSectionTests.cs::The_cap_round_trips_to_the_preference| PASS |
| F-APP-F-14 | setter 与 SubAgentLimits.From 同一 Clamp;建宿主注入+每回合核对,值不变不碰宿主;扩容立即唤醒排队 | SPEC.md:393 | Agents/SubAgentLimits.cs:38-48;SessionRuntime.cs:639-660,568;RuntimeSettingsComposition.cs:29-35 | Agents/SessionRuntimeSubAgentLimitsTests.cs::A_changed_cap_moves_on_the_next_turn_without_rebuilding_the_host| PASS |
| F-APP-F-15 | 派生关→spawn_agent 入禁用集合,steer/follow_up/stop 保留,与工具开关同集合 | SPEC.md:393 | SessionRuntime.cs:608,623-628 | Agents/SessionRuntimeSubAgentLimitsTests.cs::Switching_spawning_off_takes_spawn_agent_out_and_keeps_the_control_tools| PASS |
| F-APP-F-16 | idle-archive order570 SYSTEM:开关默认开、天数 1–365 默认 7、scope automation/all | SPEC.md:393 | AgentRuntimeSectionsViewModel.cs:66-163;AppPreferences.cs:94-97,157-158;SettingsSection.cs:198 | Settings/IdleArchiveSettingsSectionTests.cs::The_switch_round_trips_and_enables_the_body| PASS |
| F-APP-F-17 | Func<IdleArchiveSettings> 每轮一读;关闭在 openSessions/周任务查询前短路;其余清理照常 | SPEC.md:393 | Composition/AppComposition.cs:692;Automations/Housekeeping.cs:199-206 | Automations/HousekeepingTests.cs::A_disabled_step_archives_nothing_and_tells_no_window_while_the_other_steps_still_run| PASS |
| F-APP-F-18 | 四分区在 RuntimeSettingsComposition 注册,SectionViewTemplate 显式工厂,IconBot/IconArchive,文案表 | SPEC.md:393 | Composition/RuntimeSettingsComposition.cs:37-77;Localization/Strings.AgentSettings.cs | Foundation/SettingsPanelTests.cs::A_wave_section_renders_its_own_body| PASS |
| F-APP-F-19 | mcp 页增删改/启停/测试/重连/工具清单/stderr 尾/导入/预设 | SPEC.md:395 | ViewModels/Settings/McpSettingsSectionViewModel.cs:79-85;McpServerRowViewModel.cs:45-51,90-128 | Mcp/McpSettingsPageHeadlessTests.cs::The_action_bar_exposes_the_add_import_presets_and_reload_buttons| PARTIAL |
| F-APP-F-20 | 写被编辑那份文件;mutex+原子;保留其它条目/根键/未知字段;${VAR} 原样;注释丢失两空格;根非对象拒写 | SPEC.md:395 | Services/McpConfigStore.cs:108-121,199-217;src/Forge.Mcp/McpConfigFile.cs:361-372,400-402,563 | Mcp/McpConfigStoreTests.cs::Upsert_writes_into_the_target_file_and_leaves_the_other_file_byte_identical| PASS |
| F-APP-F-21 | 导入按节点原样拷贝;同名默认不勾选;源缺失说明路径原因 | SPEC.md:395 | McpImportCandidateViewModel.cs:15-20,82-94;McpSettingsSectionViewModel.cs:860-897 | Mcp/McpConfigStoreTests.cs::Import_writes_entries_verbatim_keeping_placeholders_and_unknown_fields(UI 默认勾选/缺失提示无直接测试) | PASS |
| F-APP-F-22 | 7 预设只填编辑器不静默写文件;需目录者留 {path} | SPEC.md:395 | src/Forge.Mcp/McpPresets.cs:21-32;McpSettingsSectionViewModel.cs:761-776 | 无 | PASS* |
| F-APP-F-23 | 保存=写→重读两份→ApplyConfigAsync 差量(删先停/改回收传输重启/增懒启动);改名同写删旧键;单台失败隔离 | SPEC.md:395 | Composition/McpServices.cs:130-152,318-340;src/Forge.Mcp/McpServerManager.cs:126-200 | Mcp/McpServicesTests.cs::SaveAsync_adds_the_server_to_the_manager_and_the_file| PASS |
| F-APP-F-24 | 坏根文件≠删除:来自该文件的运行中服务器保留;重新读取走同一 apply | SPEC.md:395 | McpServices.cs:384-423;McpSettingsSectionViewModel.cs:85,248 | Mcp/McpServicesTests.cs::A_file_that_cannot_be_parsed_does_not_read_as_deleting_every_server| PASS |
| F-APP-F-25 | 四参数直连偏好(60s/1s/0/200);超时与重连延迟经 Func 现读对下次连接生效;stderr 尾仅显示截断 | SPEC.md:395 | McpSettingsSectionViewModel.cs:172-208;McpServices.cs:53,96-103,296-306;src/Forge.Mcp/McpServerManager.cs:61 | Mcp/McpServicesTests.cs::ToolSearchLimit_follows_the_preferences| PASS |
| F-APP-F-26 | 文件 IO/Apply/Probe 在线程池;StateChanged 合并一次 dispatcher post;展开才读清单/stderr;展开/导入有上限 | SPEC.md:395 | McpSettingsSectionViewModel.cs:30-33(500/500),1037-1052;McpServerRowViewModel.cs:90-103,353-378;McpServices.cs:118-121,313-316 | Mcp/McpSettingsPageHeadlessTests.cs::The_mcp_page_rerenders_after_the_ui_language_switches(合并 post 无直接测试) | PASS |
| F-APP-F-27 | 就绪服务器工具经 CollectExternalTools 入面,Origin.Mcp,组/来源=服务器名,只读 | SPEC.md:397 | SessionRuntime.cs:1150-1162,1197-1237 | Mcp/McpReadyServerTests.cs::Without_a_limit_the_runtime_offers_every_ready_tool_and_no_search_tool| PASS |
| F-APP-F-28 | limit=0 全给;>0 pin(超额取最近)→本回合文本打分→目录序补足;打分层级;按(快照,上限,文本)缓存;按暴露名排序 | SPEC.md:397 | Agents/McpToolExposure.cs:66-213,296-321 | Mcp/McpToolExposureTests.cs::Select_with_a_zero_limit_returns_the_whole_catalog_without_truncating| PASS |
| F-APP-F-29 | 有隐藏时提供 mcp_tool_search(只读免审批);命中即 pin 并当场重发布 | SPEC.md:397 | SessionRuntime.cs:808-836;src/Forge.Mcp/McpToolSearchTool.cs:40(ReadOnly) | Mcp/McpReadyServerTests.cs::Executing_the_search_tool_pins_its_match_so_the_next_surface_offers_it| PASS |
| F-APP-F-30 | 每回合比较选中名与持有名,不一致才 ReplaceExternalTools;pin 会话绑定;内部锁 | SPEC.md:397 | SessionRuntime.cs:740-763,843+(SameMcpTools 含 side-effect 比较);McpToolExposure.cs:34,205 | Mcp/McpReadyServerTests.cs::SameMcpTools_holds_an_unchanged_surface_and_republishes_a_changed_side_effect| PASS |
| F-APP-F-31 | compaction 段默认 {true,85,50};触发 50–95、目标≥1<触发;非法读默认保留 enabled | SPEC.md:399 | Agents/CompactionSettingsStore.cs:14-46,100-137 | Agents/CompactionSettingsStoreTests.cs::A_missing_file_reads_as_the_defaults| PASS |
| F-APP-F-32 | 编辑入口在 provider 分区,行内校验,失焦/回车保存 | SPEC.md:399 | Settings/CompactionSettingsViewModel.cs:19-88;Settings/ProvidersSettingsView.axaml.cs:49-57 | Agents/CompactionSettingsStoreTests.cs::The_view_model_validates_inline_and_saves_only_valid_values| PASS |
| F-APP-F-33 | 实时生效:CompactionChanges→Changed→SetCompactionPolicy;每回合再核对;运行中回合下次请求按新线 | SPEC.md:399 | AppComposition.cs:576;SessionRuntime.cs:220-224,571,911-931 | Agents/SessionRuntimeCompactionTests.cs::A_settings_save_reaches_a_running_session_and_its_next_request| PASS |
| F-APP-F-34 | 换策略不重置迟滞/上次提示词大小,缓存不破;释放后不监听;窗口/最大输出取模型目录 | SPEC.md:399 | SessionRuntime.cs:919,1310-1313;迟滞保留为 Core 契约(src/Forge.Core/SPEC.md:73) | Agents/SessionRuntimeCompactionTests.cs(链路与退订)| PASS |
| F-APP-F-35 | /compact 解析:大小写不敏感、后接空白、/compactfoo 不算、无附图;不发消息转 CompactNowAsync | SPEC.md:399 | ViewModels/SessionWorkspace.cs:950-973(解析),909-916(发送拦截+附件门) | ViewModels/CompactCommandTests.cs::The_command_is_recognised_with_its_trimmed_focus| PARTIAL |
| F-APP-F-36 | focus 追加 Additional focus;keepRecent 保留;自动关时手动可用;回合中拒绝;压缩期禁发送;四种结果提示;失败/Stalled 提示 | SPEC.md:399 | SessionWorkspace.cs:976,983-1043,1730-1731;focus/keepRecent/无视开关为 Core 契约(src/Forge.Core/SPEC.md:77-79) | ViewModels/SessionEventRoutingTests.cs:252-287(量规点击=手动压缩、CanExecute 变化、结果提示) | PASS |
| F-APP-F-37 | 量规每次模型请求与每次压缩后刷新;点击等同 /compact | SPEC.md:399 | SessionWorkspace.cs:241,1021,1726-1727 | ViewModels/SessionEventRoutingTests.cs:252-287| PASS |
| F-APP-F-38 | web 分区 AGENT order140;web 段后端/searxng 地址/私网默认关;密钥存 ProviderSecretStore 固定引用 | SPEC.md:403 | Web/WebServices.cs:15-22,80-105,140-142;AppComposition.cs:498-504 | Web/WebToolsTests.cs::Settings_round_trip_and_keep_other_sections| PASS |
| F-APP-F-39 | web_fetch 恒注册;web_search 仅后端可用时注册(SearchAvailable≡CreateBackend 非空);实例只造一次全会话共用 | SPEC.md:403 | WebServices.cs:214-262 | Web/WebToolsTests.cs::Without_a_usable_backend_only_web_fetch_is_offered| PASS |
| F-APP-F-40 | 每回合比较 Web 工具名,不一致才重发布;改后端/密钥/地址下回合生效;不影响有无的改动不破缓存;运行中回合保留工具集 | SPEC.md:403 | SessionRuntime.cs:673-731 | Web/WebSearchSessionSurfaceTests.cs::Web_search_follows_the_settings_from_the_next_turn_of_an_open_session| PASS |
| F-APP-F-41 | web_search 每次调用现造后端;回合中途后端移除返回“未配置”失败(审批照常) | SPEC.md:403 | WebServices.cs:260 | Web/WebToolsTests.cs::Web_search_obtained_while_configured_reports_a_later_unconfiguration_instead_of_searching| PASS |
| F-APP-F-42 | 抓取客户端关自动重定向+AddressFilter(允许私网或非私网);搜索客户端不过滤;均经 NetworkStack | SPEC.md:403 | WebServices.cs:172-199 | 无(构造参数无直接测试;连接时过滤在 Core SPEC:113/115 侧) | PASS |
| F-APP-F-43 | network 分区 SYSTEM order580 IconGlobe;store 读写 network 段各字段;节点树/只替换本段/mutex+原子/根非对象拒写 | SPEC.md:405 | AppComposition.cs:361-373;Network/NetworkSettingsStore.cs:78-107 | Network/NetworkSettingsTests.cs::Round_trip_keeps_other_sections_and_unknown_fields| PASS |
| F-APP-F-44 | 读取从不抛:按字段回退默认、列表留有效项、模式缺值回退 system;LoadProblem+启动 toast+页顶显示;坏文件静默默认由偏好存储报告 | SPEC.md:405 | NetworkSettingsStore.cs:44-72,140-186;ViewModels/MainWindowViewModel.cs:495-500;NetworkSettingsSectionViewModel.cs:198-201 | Network/NetworkSettingsTests.cs::Invalid_values_fall_back_per_field_and_are_reported| PASS |
| F-APP-F-45 | Save 先规范化,NetworkSettingsRules.Problems 非空抛 ArgumentException | SPEC.md:405 | NetworkSettingsStore.cs:78-90 | Network/NetworkSettingsTests.cs::Save_refuses_invalid_settings_and_writes_nothing| PASS |
| F-APP-F-46 | 只校验所选模式;未选模式无效文本保留存储值;应用=保存+INetworkStack.Apply 无需重启 | SPEC.md:405 | NetworkSettingsSectionViewModel.cs:227-249,330-364 | Network/NetworkSettingsTests.cs::Only_the_selected_modes_are_validated| PASS |
| F-APP-F-47 | 测试用未应用候选经 CreateResolver 解析,默认 api.anthropic.com,15s 超时,结果/错误写状态行 | SPEC.md:405 | NetworkSettingsSectionViewModel.cs:18-20,252+;AppComposition.cs:370 | Network/NetworkSettingsTests.cs::Test_resolves_with_the_entered_settings_without_applying_them| PASS |
| F-APP-F-48 | 两条内联提示:有效 socks4:// 手动代理;DoH 有效 URL 且主机为名字 | SPEC.md:405 | NetworkSettingsSectionViewModel.cs:172-196,354-362 | Network/NetworkSettingsTests.cs::Socks4_hint_follows_the_scheme_of_a_valid_manual_proxy| PASS |
| F-APP-F-49 | 组合根通知后提供商前建 NetworkStack,交给提供商/MCP/Web;拆除在 HttpClient 后;构造失败就地释放三处 | SPEC.md:405 | AppComposition.cs:346-373,410-435,456-467,497,911-913;WebServices.cs:186-196 | CompositionDisposeTests.cs(拆除链;构造失败释放无直接测试) | PASS |
| F-APP-F-50 | chat 分区 INTERFACE order320 IconChat;缩略图长边+发图开关直写偏好,下次读取生效,防抖持久化;SectionViewTemplate 注册 | SPEC.md:413 | Composition/ViewerSettingsComposition.cs:34-45;Settings/ChatSettingsSectionViewModel.cs:19-65 | Settings/ChatSettingsSectionTests.cs::Thumbnail_edge_and_the_send_switch_round_trip_through_the_preferences| PASS |
| F-APP-F-51 | files 分区 order360 IconEye 编辑器后终端前;三项范围/默认取自偏好夹取常量;下次预览生效;FilePreviewSettings 唯一换算处 | SPEC.md:417 | ViewerSettingsComposition.cs:28-30,48-55;Settings/FilesSettingsSectionViewModel.cs:43-88;ViewModels/Preview/FilePreviewSettings.cs:14-18 | Settings/FilesSettingsSectionViewModelTests.cs::Rows_and_notebook_kilobytes_round_trip_through_the_preferences| PASS |
| F-APP-F-52 | approvals 只读分区:插件 id/版本/状态/更新时间;库不可读说“不可读” | SPEC.md:60 | ViewModels/Settings/InstrumentSettingsSections.cs:99-143 | 无(仅 Foundation/SettingsPanelTests.cs:448,505 导航存在性) | PASS* |
| F-APP-F-53 | skills 只读分区:SkillCatalog.Discover 名称/描述/SKILL.md 路径 | SPEC.md:60 | InstrumentSettingsSections.cs:146-175 | 无 | PASS* |
| F-APP-F-54 | mcp 作为“只读 instrument 分区(名称/状态/启用/最近错误)” | SPEC.md:60 | 实现为管理页(McpSettingsSectionViewModel.cs;App.axaml:186-188),符合 SPEC.md:395 | Mcp/McpSettingsPageHeadlessTests.cs(管理页) | SPEC_GAP |
| F-APP-F-55 | tools 分区=活动会话目录+扩展宿主插件/工具/命令总数行 | SPEC.md:60 | ToolsSettingsSectionViewModel.cs:113-127;App.axaml:189-200 | Settings/ToolSwitchSettingsTests.cs(分组;总数行无测试) | PASS* |
| F-APP-F-56 | instrument 正文选中/打开时才读源(审批库/技能走线程池),语言切换重建行 | SPEC.md:60 | InstrumentSettingsSections.cs:27-95 | 无 | PASS* |
| F-APP-F-57 | 契约 Abstractions/PromptTemplates.cs、Core 实现、App 入口 PromptTemplateServices 挂 AppComposition 与 AgentRuntimeServices | SPEC.md:421 | Prompts/PromptTemplateServices.cs:96-132;AppComposition.cs:291,561,645 | Settings/PromptsSettingsViewModelTests.cs::Saving_through_the_service_raises_Changed | PASS |
| F-APP-F-58 | Command:$ARGUMENTS/$@ 整串、$1..$9 引号成组、未用参数空行追加、单次左到右不二次展开;内置 review/explain/tests/fix | SPEC.md:422 | src/Forge.Core/Prompts/Templates/CommandExpander.cs:6-12,54-113;BuiltInCommands.cs:14-41 | tests/Forge.Core.Tests/Prompts/Templates/CommandExpanderTests.cs(20 项) | PASS |
| F-APP-F-59 | Agent:覆盖继承未写元数据(尤其 tools);内置不得改名;用户不得占内置名/id;同名按 id 先到;空 tools 无效;默认键=文件 id;文件 model/thinking 规则 | SPEC.md:423 | src/Forge.Core/Prompts/Templates/PromptTemplateStore.cs:523+ | Agents/SessionRuntimeTemplatesTests.cs::A_project_file_named_like_scout_cannot_replace_scout_or_widen_its_tools| PASS |
| F-APP-F-60 | Slot 固定 id 只覆盖;Core 三槽+App pr.workflow 六段/三条自动化槽;占位符单次替换;声明槽未声明 {名} 无效;无声明槽不检查 | SPEC.md:424 | PromptTemplateServices.cs:8-88;src/Forge.Core/Prompts/Templates/PromptTemplateRegistry.cs:11-17;PromptTemplateStore.cs:497-503,576+ | tests/Forge.Core.Tests/Prompts/Templates/PromptTemplateValidationTests.cs| PASS |
| F-APP-F-61 | 每模板 <id>.md,id 规则(1–64/字符集/开头/结尾/设备名);内置→全局→项目;replace/append 合成从最后 replace 起;无效文件列出带问题码 | SPEC.md:426 | PromptTemplateRegistry.cs:123-146;PromptTemplateStore.cs:268,476-478,616-619 | tests/Forge.Core.Tests/Prompts/Templates/PromptTemplateLayeringTests.cs(23 项)| PASS |
| F-APP-F-62 | 恢复默认=删文件;复制预填 BuiltInText;修订号 SHA-256 前 12 字节,外改 Conflict;新建 null 修订;临时文件+改名;IO 离 UI 线程;>256KiB/非 UTF-8 拒读 | SPEC.md:426 | PromptTemplateStore.cs:23,33,143-206,310-326 | Settings/PromptsSettingsViewModelTests.cs::Saving_writes_the_file_and_the_next_save_uses_the_new_revision| PASS |
| F-APP-F-63 | 建宿主 Resolve 一次快照,系统槽/Agent/压缩提示词宿主生命周期固定;pr_workflow 按调用目录现取;交接摘要取全局 | SPEC.md:428 | SessionRuntime.cs:957-1003;PromptTemplateServices.cs:128-131;AppComposition.cs:542,849 | Agents/SessionRuntimeTemplatesTests.cs::An_edit_to_the_system_slot_during_a_session_does_not_change_its_prefix| PASS |
| F-APP-F-64 | prompts 分区 AGENT order130;分组/种类图标/来源与附加徽标/过滤/悬停路径;内置只读+复制;自定义保存/还原/删除确认;新建选种类作用域;槽位不可新建 | SPEC.md:430 | Settings/Prompts/PromptsSettingsViewModel.cs:64-129,353;PromptsSettingsSection.cs;SettingsSection.cs:189 | Settings/PromptsSettingsViewModelTests.cs::The_list_groups_commands_agents_and_prompts_in_that_order| PASS |
| F-APP-F-65 | 项目作用域=前台会话工作目录;无前台只全局;未保存修改首次点击警告、同目标二次才放弃 | SPEC.md:430 | AppComposition.cs:716-717;PromptsSettingsViewModel.cs:179-205,311 | Settings/PromptsSettingsViewModelTests.cs::Copying_to_project_needs_a_workspace| PASS |
| F-APP-F-66 | Skills 独立分区共用作用域与徽标约定;/ 弹层命令在前同名命令胜出;插件 WorkflowDefinition 以后接入 | SPEC.md:432 | ComposerViewModel.cs:711-721(合并✓);skills 分区为只读 instrument(InstrumentSettingsSections.cs:146-175),无来源徽标 | Providers/ComposerPreferencesTests.cs(/ 弹层)| PARTIAL |
| F-APP-F-67 | 单覆盖层五来源一列表一预览;视图只做键盘路由与聚焦;状态在 VM 三分部;IO 在 IPaletteHost 后 | SPEC.md:436 | Views/PaletteView.axaml.cs:25-26,103-125;ViewModels/PaletteViewModel*.cs;MainWindowViewModel.Palette.cs | Palette/PaletteViewTests.cs::Opening_the_palette_puts_the_focus_on_the_query_box| PASS |
| F-APP-F-68 | Ctrl+P/Ctrl+Shift+P/Ctrl+Shift+F;同手势再按关闭不清查询;标题栏放大镜同动作;关闭时宿主属性 null | SPEC.md:438 | MainWindow.axaml:32-34;MainWindowViewModel.Palette.cs:51-54,74-95 | Palette/PaletteHostTests.cs::Each_gesture_opens_its_source_and_the_same_gesture_again_clears_the_overlay_slot | PASS |
| F-APP-F-69 | 来源前缀 > # : @ 仅首字符生效;切换前台会话关闭面板 | SPEC.md:438 | Services/PaletteQuery.cs:11-44;MainWindowViewModel.Palette.cs:65-71 | Palette/PaletteViewModelTests.cs::A_leading_prefix_switches_the_source| PASS |
| F-APP-F-70 | 五来源:FileIndex(git ls-files/有界遍历+gitignore)复用 FilterFiles;GrepEngine 字面量大小写不敏感按路径行号排序;会话快照排除子 Agent/软删除、归档徽标;命令静态表与 switch 一一对应;符号 LSP 上限 2000 | SPEC.md:440 | Services/PaletteSources.cs:17-22,79,131-149;PaletteFileCache.cs;PaletteSymbols.cs:16;PaletteCommands.cs(20 id)↔MainWindowViewModel.Palette.cs(20 case) | Palette/PaletteSourcesTests.cs(24 项)| PASS |
| F-APP-F-71 | 快照+后台刷新;换代丢弃迟到答案;文件快照按工作区缓存原子换出;内容 180ms 去抖单飞前次取消;每键一遍快照过滤 | SPEC.md:442 | PaletteViewModel.Snapshot.cs:21,196,295,331,376;PaletteFileCache.cs:18-34 | Palette/PaletteViewModelTests.cs::Opening_shows_the_cached_snapshot_before_the_refresh_answers_and_starts_it_once| PASS |
| F-APP-F-72 | 预览≤256K 字符、首 40 行或命中±12、带行号、后台解码、最近文件缓存;不可读/空/二进制(首块 NUL)给“没有可预览”;截断区显示已读末尾 | SPEC.md:444 | Services/PalettePreview.cs:14-23;PaletteViewModel.Preview.cs:26,87 | Palette/PalettePreviewTests.cs::A_position_shows_the_lines_around_it| PASS |
| F-APP-F-73 | 空/失败以不可打开 notice 行呈现(无会话/无文档/无 LSP/失败原因/首快照在读),Enter 无效 | SPEC.md:446 | PaletteViewModel.Snapshot.cs:49-109;PaletteViewModel.cs:191-194 | Palette/PaletteViewModelTests.cs::Accept_on_a_notice_row_does_nothing_and_leaves_the_palette_open| PASS |
| F-APP-F-74 | palettePerSourceLimit 默认 50 钳 [10,500];排除目录逗号/分号/换行分隔任意层级整段大小写不敏感;palette 分区写穿活偏好下次按键生效 | SPEC.md:448 | AppPreferences.cs:99-101;Settings/PaletteSettingsSectionViewModel.cs:29-66;Composition/PaletteComposition.cs:27-37;PaletteSources.cs:237,276-287 | Palette/PaletteSettingsSectionTests.cs::The_result_limit_editor_writes_through_to_the_live_preference| PASS |
| F-APP-F-75 | 排除在结果侧过滤:向引擎要 4 倍上限再丢排除行;过滤后仍超或引擎截断→状态行“只显示前 N” | SPEC.md:448 | PaletteViewModel.Snapshot.cs:24,82-85;PaletteSources.cs:79;PaletteViewModel.cs:191-194 | Palette/PaletteViewModelTests.cs::Rows_under_an_excluded_directory_are_dropped| PASS |
| F-APP-F-76 | 未决 452:文件预览已做/未做清单(code action、重命名、文档符号列表、inlay hints、语义 range 未做;语义叠加不重映射;插件面板渲染未做) | SPEC.md:452 | 全 src 无 CodeAction/InlayHint/rename 实现;但文档符号已经面板 @ 来源实现:ViewModels/FileDocumentViewModel.Symbols.cs、Services/PaletteSymbols.cs | Palette/PaletteSymbolsTests.cs| PARTIAL |
| F-APP-F-77 | 未决 453:用户消息纯文本;代码块无行号/折叠/高亮/复制提示;不封口围栏与超长单行整块重解析;Mermaid 归 W11 | SPEC.md:453 | ViewModels/Transcript.cs:24(UserMessageViewModel 纯文本);Changes/Markdown/(无行号/高亮);全 src 无 Mermaid 渲染 | 无(渲染行为无头测试未覆盖,与规格自述一致) | PASS |
| F-APP-F-78 | 未决 454:git_branch/git_sync 按名为写操作逐次审批;读自动放行;git 工具不进 run_code | SPEC.md:454 | SessionRuntime.cs:1335-1360(DefaultApprovalPolicy+AutoAllowReadOnly),1203-1208(SnapshotToolRegistry 仅扩展工具) | 无(App 层未见 git 审批分类专项测试) | PASS* |
| F-APP-F-79 | 未决 455:资源检查器只列本窗口打开会话;新空闲会话下次拉取才挂监听;折叠尾部按会话时钟 1s;不落库不跨进程 | SPEC.md:455 | ViewModels/ResourceInspectorViewModel.cs:583-588;MainWindowViewModel.Inspector.cs | 无(未找到检查器专项测试) | PASS* |
| F-APP-F-80 | 未决 457:继承超线迟滞两次无效停住(归 T0);交接摘要不沿继承链;历史检索不跟链;对话框四类提示未做 | SPEC.md:457 | src/Forge.Core/SPEC.md:149 同文;App 对话框无 fingerprint/CacheTtl 提示代码(检索未命中) | 无(本次未复核) | UNVERIFIED |
| F-APP-F-81 | 未决 458:降级只推送不开 PR;分叉项按刷新时 HEAD 种类禁用(点击读实时 HEAD);无活动会话只能空白分叉 | SPEC.md:458 | ViewModels/ProjectTree.Menu.cs:137,583,637(ForkCommand 存在) | Workbench/ForkMergeShellTests.cs(存在;断言未复核) | UNVERIFIED |
| F-APP-F-82 | 未决 459:树刷新每工作区三次 git 调用、15s 缓存、并发上限;先发布树后补标未做;改名分支 Info.Branch 不即时更新 | SPEC.md:459 | ViewModels/ProjectTree.Menu.cs:167(GitStateLifetime=15s),233-408 | 无(缓存时长无专项测试) | PASS* |
| F-APP-F-83 | 未决 460:TurnCheckpointService 是否上移 forge-workspace 未定 | SPEC.md:460 | 仍在 Agents/TurnCheckpointService.cs(描述准确,仍未决) | 不适用 | PASS |
| F-APP-F-84 | 未决 461:Worker 资源限制受威胁模型门控,不在本模块实现 | SPEC.md:461 | 未检索到实现(与“未实现”一致;门控决策无法从代码验证) | 无 | UNVERIFIED |
| F-APP-F-85 | 未决 462:ReviewStore 不可释放,靠 SqliteSessionStore 拆除 ClearAllPools;CompositionDisposeTests 钉住 | SPEC.md:462 | src/Forge.Workspace/Review/ReviewStore.cs:11(无 IDisposable);src/Forge.Core/Storage/SqliteSessionStore.cs:333,353 | CompositionDisposeTests.cs::DisposeAsync_releases_the_reviews_database_handle | PASS |
| F-APP-F-86 | 未决 463:生命周期推迟项(只读并行/一会话多工作区/恢复某轮/跨工作区子 Agent/obj bin 抢占;三项已否决) | SPEC.md:463 | turn_checkpoints 主键仍 (session_id,turn):src/Forge.Core/Storage/StorageSchema.cs(与“要扩成 (session,worktree,turn)”一致,仍未做) | 无 | PASS* |
| F-APP-F-87 | 未决 464:界面小项推迟(ChangesPanel 徽标/刷新失败无呈现位/SemiBold 混重/小图标/箭头槽宽) | SPEC.md:464 | 未逐项复核 | 无 | UNVERIFIED |

计数:87 项 = PASS 68、PASS* 10、PARTIAL 4、SPEC_GAP 1、UNVERIFIED 4、FAIL 0、MISSING 0。

## 非 PASS 项详情

### F-19(PARTIAL)— MCP 页无独立“重连”动作
规格 395 明列“重连”。行级动作只有启停(toggleRunning)、测试(test→ProbeAsync)、刷新工具清单(refreshTools)(McpServerRowViewModel.cs:45-51;Localization/Strings.Mcp.cs:50-55 无 reconnect 键)。失败服务器恢复需停→启两步;管理器有自动退避重连,但页面没有规格承诺的一键重连。处理类型:实现缺口(小)——补行级重连命令,或把规格“重连”改为“启停(可代替重连)”。

### F-35(PARTIAL)— /compact 发送拦截与附件门无测试
解析边界已被 CompactCommandTests 两个 Theory 钉住(/compactfoo、/compact-now、please /compact、大小写、多行 focus)。但闭环前半段——SendAsync 中 `TryParseCompactCommand(typed, out var focus) && !Composer.HasAttachments` 时清草稿、不发消息、转 CompactNowAsync(SessionWorkspace.cs:909-916)——无测试驱动;带附件时应作普通消息发送的分支同样无测试。回归后果:/compact 被当消息发给模型,或带附件误触发压缩。后半段(忙/无可压缩/失败提示、压缩期禁发送、量规联动)由 SessionEventRoutingTests:252-287 经量规点击覆盖。处理类型:补测试(发送路径,含附件门两侧)。

### F-66(PARTIAL)— skills 分区未实现“共用作用域与徽标约定”
规格 432 称 skills 与 prompts 分区共用徽标约定。实际 skills 是只读 instrument(InstrumentSettingsSections.cs:146-175),行仅名称/描述/路径,无 内置/全局/项目 来源徽标、无覆盖/无效标记;作用域仅隐含在 Discover(workspace,userDir) 双目录扫描。/ 弹层合并(命令在前、同名命令胜出)已实现(ComposerViewModel.cs:711-721)。插件 WorkflowDefinition 接入为未来项(Forge.Workflows 引擎存在但未接技能目录)。处理类型:规格修订(徽标约定改“规划中”)或实现补齐,择一。

### F-54(SPEC_GAP)— 规格 60 的 mcp“只读 instrument”描述过时
规格 60 仍写 mcp 只读(名称/状态/启用/最近错误)、四 VM 同在 InstrumentSettingsSections.cs、共用 App.axaml 的 InstrumentSectionBody 模板。实际:mcp/tools 已升级(规格 395/389 正确);InstrumentSettingsSections.cs 只剩 approvals/skills;App.axaml:180-188 中 approvals/skills 用 InstrumentSectionView、mcp 用 McpSettingsView、tools 用内联模板;“InstrumentSectionBody”在整个 src 不存在(仅 SPEC.md 提及)。App.axaml:178-179 注释也仍写 read-only。处理类型:规格修订(行 60 改为两个只读 instrument+指向 389/395),顺带清理过时注释。

### F-76(PARTIAL)— 未决 452“文档符号列表未做”与面板符号来源矛盾
code action、重命名、inlay hints、语义 range 确未实现(全 src 无命中),该部分准确。但“文档符号列表”已由面板 @ 符号来源实现(FileDocumentViewModel.Symbols.cs、PaletteSymbols.cs 上限 2000,测试齐全),规格 440 自己也描述了它。若指编辑器内独立大纲面板,措辞需限定;否则过时。语义叠加 400ms 防抖细节未复核。处理类型:规格修订(移除或限定该项)。

### F-80/81/84/87(UNVERIFIED)
交接(457)、分叉合并(458)、Worker 资源限制门控(461)、界面小项(464):代码入口存在(Handover 对话框、ForkCommand、无资源限制实现),配额内未逐条复核断言;ForkMergeShellTests 可作后续入口。保留未决身份,不判过时。

## 已实现但缺测试清单

PASS* 项:F-06 DescribeTools 来源分类;F-22 MCP 预设(7 项/{path}/只填编辑器);F-52 approvals 行内容与“不可读”;F-53 skills 行内容;F-55 tools 总数行;F-56 instrument 读源时机/语言重建;F-78 git 审批分类与 run_code 排除(App 层);F-79 资源检查器边界;F-82 树 git 读数 15s 缓存;F-86 turn_checkpoints 主键形状。

PASS 项内无测试子行为(补测优先级从高到低):
1. /compact 发送拦截+附件门(见 F-35)。
2. MCP 超时/重连延迟“对下一次连接生效”(Func 现读链 McpServices.cs:96-103→McpServerManager.cs:61 存在,无测试)。
3. StateChanged 合并一次 dispatcher post(McpSettingsSectionViewModel.cs:1037-1052,无并发测试)。
4. 构造失败就地释放三处(AppComposition.cs:427-435,462-467;WebServices.cs:186-196)——泄漏类缺陷无测试最难发现。
5. MCP 导入 UI:同名默认不勾选、缺失源提示(McpImportCandidateViewModel.cs:20)。
6. Web 两客户端构造参数(关重定向/AddressFilter/搜索不过滤,WebServices.cs:172-199)。
7. models-visibility ListBox 虚拟化(仅 VM 层有测试)。

## 程序→规格:未记录行为

1. **关派生同时禁用 request_review**:SpawningTools=[spawn_agent,request_review](SessionRuntime.cs:608),规格 393 只写 spawn_agent。实现合理(request_review 起 Reviewer 子 Agent)且有测试,规格应补。
2. **models-visibility“全部显示”按钮**:ShowAllCommand 清空整个隐藏集(含无行可切的孤儿 id)(ModelsVisibilitySectionViewModel.cs:101-108),规格 391 未提;有 4 个测试。
3. **MCP 服务器失败 toast**:每台一条,key=mcp:<名称>(McpServices.cs:238-250),规格 395 只说落在状态与 stderr。
4. **新增服务器保存后立即后台启动**:manager 层懒启动,App 层 StartAdded 马上拉起(McpServices.cs:347-360);规格 395“新增的懒启动”只描述了一半;有测试。
5. **mcp_tool_search 结果数被 ToolSearchLimit 夹取**(SessionRuntime.cs:826),规格 397 未提;有测试。
6. **选面打分细节**:pin 上限 200 逐最旧、焦点词≤32、词长≥3、停用词表(McpToolExposure.cs:20-32),规格 397 只写“小写词包含”;pin 逐出有测试。
7. **ChatMermaid 偏好已预注册**:ChatMermaidTimeoutMilliseconds(500–30000 默认 2000)、ChatMermaidDiagramTypes(默认 flowchart,sequence,state,class,er)(AppPreferences.cs:151-164)已持久化并被 GapPlanPreferencesTests 测试,但全 src 无渲染消费者;规格仅 453 说“归 W11”。提前登记的死设置。
8. **手动压缩注释声称“重新启用自动压缩”**(SessionRuntime.cs:449):Core SPEC:77 只写“无视 Enabled 开关并重置迟滞”,App SPEC 399 未写;Core 代码未复核,注释与两份规格不对齐,需澄清。
9. **McpToolExposure.ClearPins 无生产调用方**(McpToolExposure.cs:262,仅测试调用)——死代码或预留,建议注明。
10. **过时注释**:McpToolExposure.cs:15 称“never locks”实际有 Lock(:34);App.axaml:178-179 仍称 mcp/tools 只读。
11. **tools 分区未知内置组按序数排序追加**(ToolsSettingsSectionViewModel.cs:169-172),规格只写固定顺序(轻微)。
12. **面板关闭后再打开清空上次查询**(PaletteViewModelTests::Opening_again_clears_the_query_of_the_previous_open 钉住);规格 438 只保证同手势再按不清查询,未写关闭→重开语义(轻微)。

## 规格质量问题

1. **矛盾+过时(最严重)**:SPEC.md:60 与 389/395 冲突——mcp/tools 不再只读;“四 VM 同文件”“共用 InstrumentSectionBody 模板”均与代码不符(模板名不存在)。见 F-54。
2. **未决项过时**:SPEC.md:452“未做:…文档符号列表…”与 440 面板符号来源(已实现有测试)矛盾,至少歧义。见 F-76。
3. **跨规格重复**:App:403 vs src/Forge.Core/SPEC.md:113,152(web_search 注册/现造后端/回合同步);App:399 vs Core:73,77,79(压缩策略/迟滞/手动压缩);App:405 vs Core:115(NetworkStack/NetworkSettingsRules/CreateResolver);App:421-428 vs Core:138(模板分层/命名/256KiB)。有交叉引用但规则正文双写,已现漂移实例(request_review 两份规格都没写)。建议 App 侧只留接线与设置面,契约正文单归 Core。
4. **遗漏**:393 未写 request_review 随派生开关禁用;391 未写“全部显示”;395 未写失败 toast 与新增即启动;397 未写搜索数夹取与 pin 逐出(见上节 1-6)。
5. **措辞与实现不符**:395“重连”无独立控件;“新增的懒启动”与 App 层立即启动不一致(F-19、上节 4)。
6. **未实现的约定**:432“skills 共用作用域与徽标约定”——skills 分区无任何徽标(F-66)。
7. **预注册未记录**:ChatMermaid* 偏好已存在并持久化,453 仍只说归 W11,设置项与未决状态脱节(上节 7)。
8. **轻微模糊**:391“N 个模型已隐藏”未说明只统计目录列出的行(孤儿隐藏 id 不计数;RefreshHiddenCount 只遍历 allRows,ShowAll 注释自认)。

## 业务闭环缺口

1. **/compact 输入闭环前半段无防护**(F-35):输入→解析→拦截→手动压缩→提示→量规刷新,后半段有测试,前半段没有;回归即把命令文本发给模型。优先补测。
2. **MCP 故障恢复闭环缺一步**(F-19):失败→重连只能停+启两步,规格承诺的一键重连不存在。
3. **设置保存→实时生效链总体闭合**:compaction(Changed+每回合核对)、web(每回合名字比较)、工具/派生开关(每回合 ApplyDisabledTools)、子 Agent 上限(每回合+PropertyChanged 立即刷新)、idle-archive(每轮工厂读)、palette/chat/files(活偏好直读)均有实现与测试。唯一未钉住的活读链是 MCP 超时/重连延迟,建议补“改偏好→下次连接用新值”测试。
4. **mcp.json 写回闭环完整**:占位符/未知字段保留、坏根拒写、坏文件保留运行中服务器、改名/跨文件移动删旧键、保存失败不落半截,均有测试。
5. **模板三层覆盖闭环完整**:分层/append/无效文件问题码/修订冲突/256KiB 与 UTF-8 拒读/宿主快照固定/pr_workflow 现取/交接全局,Core 与 App 测试齐全。
6. **网络设置校验与测试闭环完整**:按字段回退+LoadProblem+toast、只校验所选模式、候选测试不应用、两条提示、Apply 免重启,NetworkSettingsTests 22 项。
7. **skills 徽标是规格侧闭环缺口**(F-66):用户看不到来源/覆盖状态,与 prompts 分区体验不一致;需产品决策补齐或降规格。
