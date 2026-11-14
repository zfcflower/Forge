# App D(编辑器接线/LSP/问题面板/终端)审计发现

只读审计。规格:`src/Forge.App/SPEC.md`(实际行号:编辑器 262–272、语义 274、引用 276、问题 278、来源 282、诊断 284、终端 323–331;任务书行号各偏移 1)。
测试缩写:**AT**=tests/Forge.App.Tests,**ET**=tests/Forge.Editor.Tests。别名(AT 内,除注 ET):Editor/ 别名=Editor<名>Tests:EF=Feature,EW=LspWindow,ER=References,ERB=ReferencesBehaviour,EST=SemanticToken,ESL=SemanticTokenLifecycle,ECF=CompletionFallback,EP=Preferences;ViewModels/:PP=ProblemsPanelTests,PPL=ProblemsPanelLifecycleTests;WPT=Workbench/WorkbenchLayoutProblemsTabTests;RIP=Review/ReviewIconPlacementTests;SPT=Foundation/SettingsPanelTests;Terminal/ 内前缀 Terminal、后缀 Tests:TC=Coordinator,TL=Lifecycle,TI=Input,TIO=InputOrder,TP=Preference,TM=MovedTab,TMO=Mouse,TSE=Selection,TSB=Scrollback,TR=Resize,TH=ThemeFont,TU=Utf8,TMD=Modes,TSG=Sgr,TCH=Charset,TRS=Responses,THC=HyperlinkClipboard,TEE=EraseEdit,TPF=Performance,TVT=ViewTemplate,TAS=AlternateScreen,TGR=GridResize;TKE=KeyEncoderTests,TME=MouseEncoderTests(无前缀);ET:LDS=Languages/LanguageServerDiscoveryTests,LR=Languages/LanguageRegistryTests,WD=Lsp/WorkspaceDiagnosticsTests,LC=Lsp/LspClientTests,SLI=Highlighting/SemanticLineIndexTests,STD=Lsp/SemanticTokensDecoderTests,STM=Lsp/SemanticTokenMapTests,SFT=Folding/StructuralFoldingTests。
关键事实:LSP 测试走 EditorLspRig+ScriptedServer(进程内假服务器),终端走 FakeTerminalHost/VtTestHost;无真实 LSP/PTY 集成测试,依赖真实运行时者标 UNVERIFIED。
口径:PASS=实现+测试证实;PASS*=实现存在但无测试;PARTIAL=部分符合;FAIL=规范性声明未实现;UNVERIFIED=需真实运行时/构建。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-D-01 | 折叠安装 FoldingManager;范围优先 LSP foldingRange,无声明/无会话回退 StructuralFolding,XML/HTML 用 XmlFoldingStrategy | SPEC:264 | src/Forge.App/Editing/EditorFoldingController.cs:73-80,186-247;src/Forge.App/ViewModels/FileDocumentViewModel.Lsp.cs:934-963 | EF::Folding_produces_a_margin_and_the_commands_collapse_and_expand;EW::Folding_ranges_from_the_server_are_used_instead_of_the_heuristic;SFT | PASS |
| F-APP-D-02 | 折叠 250ms 防抖重算;结果按文档版本校验,过期丢弃 | SPEC:264 | EditorFoldingController.cs:26,160-232(CompareAge!=0 丢弃) | 无(防抖/版本丢弃无专门测试) | PASS* |
| F-APP-D-03 | 折叠状态属控件,不写盘、不跨标签继承 | SPEC:264 | EditorFoldingController.cs(无持久化);ForgeEditorBridge.cs:52-64 每视图一份 | 无(仅 EF::Turning_automatic_folding_off_removes_the_margin_and_turning_it_on_restores_it 证开关) | PASS* |
| F-APP-D-04 | 超过 20000 行不折叠并在提示行说明 | SPEC:264 | EditorFoldingController.cs:236-241;src/Forge.Editor/Folding/StructuralFolding.cs:29,49 | EF::A_document_above_the_folding_cap_says_so_instead_of_folding | PARTIAL |
| F-APP-D-05 | 显示/交互开关全集落 preferences,缺失取默认,改动立即套用已打开编辑器 | SPEC:265 | src/Forge.App/Editing/EditorOptionsApplier.cs:19-44;EditorSettings.cs:22-36;ForgeEditorBridge.cs:148-180 | EF::Line_numbers_scroll_below_current_line_and_rulers_follow_the_options、Settings_changes_reach_an_already_open_editor;EP::A_fresh_preferences_object_has_the_documented_editor_defaults 等 | PASS |
| F-APP-D-06 | 设置页“编辑器”分区给出默认值 | SPEC:265 | src/Forge.App/Composition/AppComposition.cs:314-321;src/Forge.App/App.axaml:255+(DataTemplate) | EP::The_editor_section_round_trips_and_keeps_the_other_sections;SPT(KnownSectionIds 含 Editor) | PASS |
| F-APP-D-07 | 工具栏⋯与右键菜单共用构建逻辑、各持实例;工具栏用 MenuFlyout;菜单项无绑定,打开读/点击写 | SPEC:266 | src/Forge.App/Views/EditorMenuBuilder.cs:21-131;FileDocumentView.axaml.cs:177-232 | EF::The_toolbar_ellipsis_opens_its_own_menu_without_touching_the_editor_one、The_context_menu_offers_every_display_toggle_and_its_checkboxes_drive_the_options | PASS |
| F-APP-D-08 | 菜单内容:撤销/重做/剪切/复制/粘贴/全选、显示开关、折叠、转换(大小写四种/前导 Tab↔空格/缩进选区/删除行)、查找替换 | SPEC:266 | EditorMenuBuilder.cs:25-95;FileDocumentView.axaml.cs:236-296 | EF::The_transform_commands_change_the_selection_and_the_line_commands_delete_a_line | PASS |
| F-APP-D-09 | Ctrl+F/Ctrl+H 用库 SearchPanel | SPEC:266 | FileDocumentView.axaml.cs:259-269 | EF::Ctrl_f_opens_the_library_search_panel_and_finds_the_next_match | PASS |
| F-APP-D-10 | Ctrl+Shift+[ 折叠当前块;Ctrl+Alt+[ / ] 全部折叠/展开 | SPEC:266 | FileDocumentView.axaml.cs:306-320 | 无(键路径无测试;菜单命令有测试) | PASS* |
| F-APP-D-11 | 选区品牌蓝 #3399FF 半透明(亮≈24%/暗≈30%),清前景与边框;搜索命中半透明黄 | SPEC:267 | src/Forge.App/Resources/Tokens.axaml:159-160(亮 #3D3399FF)、238-239(暗 #4D5AA9FF);EditorOptionsApplier.cs:52-66 | EF::The_selection_tints_rather_than_covers_the_text | PARTIAL |
| F-APP-D-12 | 评审图标在锚点右下角,靠右翻左下,夹在可视区内 | SPEC:268 | src/Forge.App/Review/ReviewIconPlacement.cs:24-38 | RIP(Flip/Clamp 用例) | PASS |
| F-APP-D-13 | 不做多光标;保持 EnableRectangularSelection,Alt+拖拽列选,设置页与菜单写明 | SPEC:269 | EditorOptionsApplier.cs:38;src/Forge.App/Localization/Strings.Editor.cs:68-69,106;EditorMenuBuilder.cs:91-94(禁用说明项) | 无(列选择行为属库,App 侧无测试) | PASS* |
| F-APP-D-14 | 诊断行尾按严重度着色零长度圆点(悬停显示该行消息);¶/→ 由 Forge 零长度元素绘制 | SPEC:270 | src/Forge.App/Editing/EditorDiagnosticAdornments.cs:57-119;EditorWhitespaceAdornments.cs;ForgeEditorBridge.cs:66-74;FileDocumentView.Lsp.cs:513-527 | EF::The_inline_adornment_marks_only_the_lines_with_diagnostics、Whitespace_tab_and_end_of_line_glyphs_are_drawn_when_their_options_are_on | PASS |
| F-APP-D-15 | 超链接只认 http/https/mailto,需 Ctrl/Cmd,经 Launcher;接管 OpenUriEvent,失败写提示行 | SPEC:271 | src/Forge.App/Editing/EditorLinkRouter.cs:29-82;EditorOptionsApplier.cs:33-34 | EF::A_clicked_hyperlink_is_routed_through_the_launcher_and_a_failure_is_reported、The_hyperlink_option_can_turn_links_off | PASS |
| F-APP-D-16 | 补全按服务器 triggerCharacters 自动触发;Ctrl+Space | SPEC:272 | FileDocumentView.Lsp.cs:196-200,277-296;FileDocumentViewModel.Lsp.cs:536-549 | EW::A_declared_trigger_character_opens_the_completion_list、Ctrl_space_applies_the_selected_completion_as_one_undo_step_and_syncs_the_server | PASS |
| F-APP-D-17 | 弹窗打开时方向键/PgUp/PgDn/Home/End 由编辑器移动选中项;ListBoxItem 用 completion 类恢复选中背景 | SPEC:272 | FileDocumentView.Lsp.cs:236-268,411-413;src/Forge.App/Resources/Controls.axaml:750-767 | EW::Arrow_keys_move_the_selection_in_the_completion_list | PASS |
| F-APP-D-18 | isIncomplete 在下一个字符后重试 | SPEC:272 | FileDocumentView.Lsp.cs:281-286 | 无 App 测试(AT 夹具全为 isIncomplete:false;LC:433 仅测客户端解码) | PASS* |
| F-APP-D-19 | Snippet 按纯文本插入:${1:name}→name、无默认不插、选择取首项、畸形整段消失、无 Tab 跳转 | SPEC:272 | src/Forge.App/Editing/EditorLspSnippets.cs:17-179;EditorLspMapping.cs:129-192 | ECF::A_snippet_becomes_the_text_it_would_insert、A_nested_placeholder_is_resolved_inside_its_default_text | PASS |
| F-APP-D-20 | 无会话/不支持时文档词表补全:光标附近窗口、200 项上限 | SPEC:272 | src/Forge.App/Editing/EditorWordCompletion.cs:18-98;FileDocumentView.Lsp.cs:351-361 | ECF::Word_completion_ranks_by_frequency_and_caps_the_list、Word_completion_scans_only_a_window_of_a_large_document 等 5 项 | PASS |
| F-APP-D-21 | SemanticTokenColorizer 只读可见行;色取 Syntax*Brush,按实际主题变体(含固定主题)解析 | SPEC:274 | src/Forge.App/Editing/SemanticTokenColorizer.cs:69-149;FileDocumentView.Lsp.cs:58-60(IsDarkTheme) | EST::A_semantic_token_repaints_the_identifier_with_the_editor_syntax_brush | PASS |
| F-APP-D-22 | 请求时机:视图绑定、编辑防抖 400ms、服务器 refresh、打开后 2.5s 兜底 | SPEC:274 | FileDocumentViewModel.Lsp.cs:36-39,721-793 | ESL::A_refresh_request_from_the_server_asks_again_and_the_new_classification_replaces_the_old、An_edit_during_an_in_flight_request_ends_with_the_newer_text_classified_and_painted(2.5s 兜底无专门测试) | PASS |
| F-APP-D-23 | full/delta 客户端解码;按行分桶,只重建变更行(BuiltLines/ReusedLines);映射在 UI 线程外;结果带快照版本 | SPEC:274 | FileDocumentViewModel.Lsp.cs:832-856(Task.Run+previous);src/Forge.Editor/Lsp/LspClient.cs:411-442 | SLI(BuiltLines/ReusedLines 断言)、Lsp/SemanticTokensDecoderTests、Lsp/SemanticTokenMapTests;ESL::A_delta_capable_server_gets_a_delta_request_after_an_edit_and_the_edits_are_applied | PASS |
| F-APP-D-24 | 答案落地时版本已过期→立即再要一份 | SPEC:274 | FileDocumentViewModel.Lsp.cs:858-862 | EST::An_answer_that_arrives_after_the_document_changed_is_stamped_stale_and_asked_for_again | PASS |
| F-APP-D-25 | 语义 refresh 触发链路的能力声明一致性 | SPEC:274 | src/Forge.Editor/Lsp/LspClient.cs:570-573(workspace.semanticTokens.refreshSupport=**false**)、751(仍处理 refresh) | ESL::A_refresh_request_from_the_server_asks_again(假服务器无视能力声明) | PARTIAL |
| F-APP-D-26 | Shift+F12 请求 references(含声明);未声明 referencesProvider 写“不支持查找引用” | SPEC:276 | FileDocumentView.Lsp.cs:206-210;FileDocumentViewModel.Lsp.cs:216-217,432-443(includeDeclaration:true) | ER::Shift_F12_lists_one_row_per_location_with_its_file_and_position;ERB::A_server_without_references_support_shows_the_unsupported_status_and_is_not_asked | PASS |
| F-APP-D-27 | 结果本文件在前→路径→行→列排序;去重;远程 URI 丢弃 | SPEC:276 | FileDocumentViewModel.Lsp.cs:479-527(MapReferences,Task.Run) | ERB::Duplicates_are_folded_remote_uris_dropped_and_the_own_file_comes_first、Only_remote_locations_leave_an_empty_list_not_a_failure | PASS |
| F-APP-D-28 | Flyout 列表(ReferenceListView),行=文件名·目录·行:列;Enter/双击跳转:本文件 RevealEditorPosition,跨文件同定义跳转路径 | SPEC:276 | FileDocumentView.Lsp.cs:646-692;src/Forge.App/Views/ReferenceListView.axaml(.cs);ViewModels/ReferenceListViewModel.cs:20-27 | ER::Enter_on_a_same_file_reference_moves_the_caret_to_it;跨文件复用 OpenDefinition(EW::F12_on_another_file_raises_a_navigation_request_and_leaves_this_document_untouched) | PASS |
| F-APP-D-29 | 空结果写状态行“没有找到引用”(不弹通知) | SPEC:276 | FileDocumentView.Lsp.cs:630-634;Strings.Inspector.cs(EditorLspReferencesNone) | ER::An_empty_answer_shows_the_no_references_notice_instead_of_a_list、An_empty_answer_writes_the_status_line_and_raises_no_notification | PASS |
| F-APP-D-30 | 引用请求绑定代次/文档版本/编辑器实例,过期即丢;分离视图取消 | SPEC:276 | FileDocumentView.Lsp.cs:120-122,617-625;FileDocumentViewModel.Lsp.cs:1121-1123 | ERB::Cancelling_mid_request_yields_null_and_a_later_request_gets_only_its_own_answer、An_answer_for_a_document_that_changed_during_the_request_is_discarded | PASS |
| F-APP-D-31 | 问题面板=右侧工具组固定标签 | SPEC:278 | src/Forge.App/Workbench/WorkbenchLayout.cs:126-128(closable:false);ViewModels/MainWindowViewModel.cs:284-285 | WPT(7 项,含快照/恢复) | PASS |
| F-APP-D-32 | WorkspaceDiagnostics 订阅 SessionStarted/SessionClosed 与每会话 Diagnostics.Changed,变化冒泡一次通知;跨文件跨工作区 | SPEC:278 | src/Forge.Editor/Lsp/WorkspaceDiagnostics.cs:52-62,169-242 | WD::Changed_fires_when_a_session_publishes_diagnostics、Changed_fires_when_a_session_starts_and_when_it_closes、Reconcile_drops_a_session_the_pool_forgot_without_an_event_and_later_publishes_do_not_reach_the_aggregator | PASS |
| F-APP-D-33 | 通知合并成一次 UI 刷新;展平排序在 UI 线程外;不轮询、闲置零开销 | SPEC:278 | ViewModels/ProblemsPanelViewModel.cs:50-56,155-215(FrameCoalescer+Interlocked+Task.Run);WorkspaceDiagnostics.cs(无定时器) | PPL::A_storm_of_change_events_collapses_into_at_most_one_extra_pass、The_snapshot_is_read_off_the_ui_thread | PASS |
| F-APP-D-34 | 严重度→路径→行排序;错误/警告/信息/提示四档筛选;标题“错误数·警告数·总数” | SPEC:278 | WorkspaceDiagnostics.cs:113-131;ProblemsPanelViewModel.cs:243-290 | PP::Each_severity_filter_hides_its_rows_and_recounts_the_summary;WD::Every_session_contributes_rows_sorted_by_severity_then_path_then_line | PASS |
| F-APP-D-35 | 超 2000 条只列前 2000 并明说截断;上限不进设置 | SPEC:278 | ProblemsPanelViewModel.cs:22,88-90,265-270 | PP::The_row_cap_truncates_the_list_but_keeps_the_real_totals_in_the_summary | PASS |
| F-APP-D-36 | 双击/Enter 打开文件并定位;工作区取自诊断本身而非活动会话 | SPEC:278 | Views/ProblemsPanelView.axaml.cs:43-60;ViewModels/MainWindowViewModel.Problems.cs:20-58(workspaceOverride+FindReviewOwner) | PP::Opening_a_row_forwards_that_rows_diagnostic(仅 VM 转发;壳层 OpenProblemAsync 无测试) | PASS* |
| F-APP-D-37 | 面板只在被显示时刷新(附加可视树/DataContext 变化);语言切换重建文案 | SPEC:278 | ProblemsPanelView.axaml.cs:22-40;ProblemsPanelViewModel.cs:300-307 | PPL::A_language_switch_drops_the_cached_rows_and_translates_the_summary(attach 触发无测试) | PASS |
| F-APP-D-38 | 启动自动检测:PATH 已知命令(C#/TS/JS/JSON/CSS/HTML/Python/Go/Rust/C/C++/Shell/YAML/Lua);不用 Pylance | SPEC:282 | src/Forge.Editor/Languages/LanguageServerDiscovery.cs:445-465(PathCandidates,无 Pylance) | LDS::A_command_on_the_path_is_used_with_its_declared_arguments、The_first_path_candidate_wins_for_a_language | PASS |
| F-APP-D-39 | VS Code/Cursor 扩展目录(Roslyn dll、.roslyn 同名片、OmniSharp)与安装目录 node 服务;三种布局展开 | SPEC:282 | LanguageServerDiscovery.cs:203-246,251-300,468-490 | LDS::A_server_inside_a_vs_code_extension_is_launched_through_dotnet、A_versioned_editor_installation_is_found_one_level_deep、A_server_shipped_with_the_editor_installation_runs_on_node、Without_the_launcher_the_extension_is_not_used | PASS |
| F-APP-D-40 | 有界离线:仅存在性检查、不下载不安装不执行;缺目录跳过;每扩展最多 32 版本取最新 | SPEC:282 | LanguageServerDiscovery.cs:86-90,325-340,378-420(仅 File/Directory.Exists) | LDS::The_newest_extension_version_is_preferred、A_missing_root_is_skipped_without_throwing | PASS |
| F-APP-D-41 | jsonc 独立语言(.jsonc/tsconfig/jsconfig/devcontainer/.vscode 下任意 .json);判定序:精确文件名→.vscode 段→最长扩展名 | SPEC:282 | src/Forge.Editor/Languages/LanguageRegistry.cs:150-215,380-394;LanguageServerDiscovery.cs:437-439 | LR::Json_with_comments_is_its_own_language_so_the_server_allows_comments、Longest_extension_wins_over_a_shorter_suffix、Exact_file_names_are_matched_case_insensitively、Without_a_jsonc_language_a_vscode_settings_file_stays_plain_json | PASS |
| F-APP-D-42 | TS 带 initializationOptions.tsserver.path:服务器同级 npm 全局→npm prefix→安装目录;找不到不带选项 | SPEC:282 | LanguageServerDiscovery.cs:131-194 | LDS::A_typescript_server_is_told_where_tsserver_lives、Without_a_tsserver_the_typescript_server_is_left_on_its_own、The_npm_global_layout_supplies_tsserver_next_to_the_server_shim | PASS |
| F-APP-D-43 | 合并规则:已配置语言(含显式无服务器/disabled)不被覆盖;未配置者用发现结果并继承扩展名表与初始化选项 | SPEC:282 | LanguageRegistry.cs:49-91(WithDiscoveredServers/_configured);src/Forge.App/Composition/EditorServices.cs:66-77 | LDS::An_explicit_configuration_wins_over_discovery、A_language_configured_without_a_server_is_left_alone、A_language_disabled_in_the_configuration_is_not_resurrected_by_discovery、A_language_that_was_never_configured_gets_the_discovered_server_and_its_extensions | PASS |
| F-APP-D-44 | 命中静默使用,状态行“语言服务:<名>(来源)”;隐藏启动器取实际文件名;找不到提示已查 PATH 与 VS Code 扩展/安装目录 | SPEC:282 | ViewModels/FileDocumentViewModel.Editing.cs:558-597(StatusFor/ServerName);Localization/Strings.Inspector.cs:194,230 | EW::A_discovered_server_is_named_on_the_editor_status_line | PASS |
| F-APP-D-45 | 同时声明推送 publishDiagnostics 与拉取 textDocument/diagnostic,从 diagnosticProvider 判断 | SPEC:284 | src/Forge.Editor/Lsp/LspClient.cs:644-660;LspCapabilities.cs:95 | EW::Pulled_diagnostics_reach_the_editor_without_a_push_notification、Published_diagnostics_are_rendered_and_cleared_on_the_real_text_view;LC | PASS |
| F-APP-D-46 | 拉取时机:视图绑定、编辑防抖 500ms、workspace/diagnostic/refresh;结果写回会话存储走同一映射/渲染/重放链路 | SPEC:284 | FileDocumentViewModel.Lsp.cs:43,574-655(PublishDiagnostics 写回)、1002-1004(GetForPath 重放) | EW::Pulled_diagnostics_reach_the_editor_without_a_push_notification;LC:1190(refresh 应答);晚绑定重放无专门测试 | PASS |
| F-APP-D-47 | 声明 window.workDoneProgress(Roslyn 加载前提) | SPEC:284 | LspClient.cs:704-706(=true) | 无(无 initialize 能力断言测试) | PASS* |
| F-APP-D-48 | 声明 workspace.diagnostics.refreshSupport(Roslyn 通知重拉前提) | SPEC:284 | LspClient.cs:552-575:workspace 能力**无 diagnostics 节点**;全模块唯一 refreshSupport 是 semanticTokens=false(:572) | 无 | FAIL |
| F-APP-D-49 | 发现的 Roslyn 另带 --autoLoadProjects 100 | SPEC:284 | LanguageServerDiscovery.cs:92,471-475 | LDS::A_server_inside_a_vs_code_extension_is_launched_through_dotnet(逐参数断言含 --autoLoadProjects 100) | PASS |
| F-APP-D-50 | 首次拉取可能早于项目加载被丢:打开后延时重拉兜底 | SPEC:284 | FileDocumentViewModel.Lsp.cs:46,600-614(PullCatchUp 2500ms) | 无(兜底时序无专门测试) | PASS* |
| F-APP-D-51 | $/progress 报告加载时状态行显示“正在加载项目” | SPEC:284 | LspClient.cs:828-856(BusyChanged);LanguageSession.cs:941;FileDocumentViewModel.Lsp.cs:553-566;Strings.Inspector.cs:231 | LC::Server_progress_brackets_one_busy_window(仅模块侧;App 状态行文案无测试) | PASS* |
| F-APP-D-52 | 终端=底部组选项卡(TabKind.Terminal,id 前缀 terminal:),可多个、可浮出;工作目录=会话 worktree | SPEC:323 | Workbench/WorkbenchTypes.cs:70,79;Terminal/TerminalWorkbench.cs:66-113,300-308 | TC::Open_docks_a_terminal_tab_into_the_bottom_group_and_reveals_it_through_the_session、Floating_terminals_survive_a_session_switch | PASS |
| F-APP-D-53 | VT 引擎纯 BCL 不依赖 Avalonia;UTF-8 跨分块续接,非法字节 U+FFFD | SPEC:324 | Terminal/Vt/ 全目录无 Avalonia using;TerminalEmulator.Parser.cs:147 | TU::Multibyte_character_split_one_byte_per_feed_reassembles、Invalid_byte_becomes_replacement_character_and_parsing_continues 等 5 项 | PASS |
| F-APP-D-54 | VT 引擎 AOT 无警告 | SPEC:324 | 无法只读验证(需发布构建) | 无 | UNVERIFIED |
| F-APP-D-55 | 主屏/备用屏;回滚环偏好 TerminalScrollbackLines 1000–100000 默认 10000 | SPEC:324 | Terminal/Vt/TerminalScreen.cs、ScrollbackBuffer.cs;Preferences/AppPreferences.cs:44-49 | TAS、TerminalScrollbackTests::Scrollback_ring_keeps_newest_lines_when_full;TerminalPreferenceTests::Scrollback_preference_clamps_to_1000_and_100000、Lowering_scrollback_trims_the_live_ring | PASS |
| F-APP-D-56 | SGR 16/256/truecolor | SPEC:324 | TerminalEmulator.Sequences.cs:555-680 | TSG(17 项) | PASS |
| F-APP-D-57 | DECSET/DECRST:鼠标 1000/1002/1003/1006、2004 括号粘贴、2026 同步输出 | SPEC:324 | TerminalEmulator.Sequences.cs:475-520;TerminalModes.cs | TMD::Decset_selects_mouse_tracking_mode、Decset_1006_toggles_sgr_mouse;TerminalMouseTests | PASS |
| F-APP-D-58 | OSC 0/1/2 标题、OSC 8 链接、OSC 52 只写剪贴板、OSC 10/11 按主题回答 | SPEC:324 | TerminalEmulator.Parser.cs:741-860(52 读请求忽略) | THC::Osc52_writes_decoded_text_to_clipboard、Osc52_query_is_ignored_without_answer_or_write;TerminalResponsesTests::Osc_10_and_11_answer_host_colors_as_rgb;TerminalLifecycleTests::Osc_title_becomes_the_tab_title | PASS |
| F-APP-D-59 | xterm 按键与 SGR 鼠标编码;East Asian Width 宽字符 | SPEC:324 | Terminal/Vt/KeyEncoder.cs、MouseEncoder.cs、CellWidth.cs | TKE(17)、MouseEncoderTests(11)、TerminalCharsetTests(5);TerminalSelectionTests::Wide_characters_copy_once_not_per_cell | PASS |
| F-APP-D-60 | 缩放时不重排行 | SPEC:324 | TerminalEmulator.cs(Resize 截断);TerminalViewModel.cs:218-246 | TR::Widening_pads_without_reflowing_wrapped_lines、Narrowing_truncates_right_cells_and_resets_margins;TerminalGridResizeTests | PASS |
| F-APP-D-61 | 实测吞吐约 99 MiB/s(20 MiB 混合文本);稳态滚动零分配 | SPEC:324 | Terminal/Vt/(热路径无分配设计) | TPF::Twenty_megabytes_feed_completes_within_budget(仅 5s 宽松预算≈≥4 MiB/s,实测值只打印;无分配断言) | PARTIAL |
| F-APP-D-62 | 已记录偏差:整屏顶行 CSI M 被删行进回滚 | SPEC:324 | TerminalEmulator.Sequences.cs:55-56;TerminalEmulator.cs:435-451(ScrollUp toScrollback)、DeleteLines | TEE::Dl_deletes_lines_and_pulls_content_up;TerminalScrollbackTests::Full_screen_scroll_pushes_oldest_line_into_scrollback | PASS |
| F-APP-D-63 | 每终端一个后台泵读 Output,持锁喂仿真器置脏标志;UI 按帧合并刷新,UI 从不解析 | SPEC:325 | TerminalViewModel.cs:363-475(PumpAsync/Feed)、556-606(MarkDirty/Flush,Interlocked+Dispatcher.Post) | TL::Start_reaches_running_with_an_empty_status_line;TerminalInputOrderTests | PASS |
| F-APP-D-64 | 键盘输入与泵产生的设备应答(DA/DSR)走同一有序通道,单写任务送达 Host | SPEC:325 | TerminalViewModel.cs:203-211(SendInput)、299-303(Respond)、502-534(Feed 冲刷)、731-757(RunInputAsync 单写) | TIO::Rapid_typed_chunks_interleaved_with_device_answers_reach_the_session_in_order_and_whole、A_slow_write_holds_later_input_and_device_answers_back_instead_of_being_overtaken 等 4 项 | PASS |
| F-APP-D-65 | TerminalControl 自绘,每行 FormattedText 按行版本号缓存 | SPEC:326 | TerminalControl.cs:462,538(LinePaint.Version) | TVT::Terminal_view_model_resolves_to_the_terminal_view(渲染冒烟;缓存本身无断言) | PASS |
| F-APP-D-66 | 键盘与 IME 输入;复制粘贴 Ctrl+Shift+C/V,macOS 用 Cmd | SPEC:326 | TerminalControl.cs:841-879(OnTextInput/IsCopy/IsPaste 分平台) | TI::Enter_tab_and_arrows_send_their_exact_bytes、Typed_text_is_sent_as_utf8_and_control_text_is_not_duplicated;TerminalSelectionTests::Drag_selects_the_range_and_copy_returns_its_exact_text | PASS |
| F-APP-D-67 | 粘贴前去掉转义字符;启用 2004 时加括号包裹 | SPEC:326 | TerminalControl.cs:1489-1533(StripEscapes+200~/201~) | TI::Bracketed_paste_wraps_and_normalizes_newlines、Paste_without_bracketed_mode_sends_raw_text_with_normalized_newlines | PASS |
| F-APP-D-68 | 拖选、双击选词、三击选行;开启鼠标上报时 Shift 仍本地选择 | SPEC:326 | TerminalControl.cs:1031-1105,1376-1405 | TSE::Double_click_selects_the_word_and_triple_click_the_line;TerminalMouseTests::Shift_press_with_reporting_on_stays_a_local_selection | PASS |
| F-APP-D-69 | 滚轮:开启上报发程序;备用屏发方向键;否则滚动回滚区 | SPEC:326 | TerminalControl.cs:1225-1305 | 无(三分支均无测试) | PASS* |
| F-APP-D-70 | Ctrl+点击 OSC 8 链接只放行 http/https | SPEC:326 | TerminalControl.cs:1073-1078,1427-1449 | 无(仿真器侧 OSC8 有测试,控件侧方案过滤无测试) | PASS* |
| F-APP-D-71 | 尺寸变化防抖 50ms 后同步 Host | SPEC:326 | TerminalControl.cs:37,111;TerminalViewModel.cs:218-246 | TH::Code_font_size_change_re_measures_the_cell_and_resizes_the_grid( resize 链路;50ms 时序无断言) | PASS |
| F-APP-D-72 | 视口锚定回滚内容,新输出不顶走正在查看的历史 | SPEC:326 | TerminalControl.cs:706-752(SyncScrollAnchor/ShiftSelection) | 无(锚定补偿无专门测试) | PASS* |
| F-APP-D-73 | 配色 TerminalPalette 亮暗各一套;字体跟随“代码字体”偏好 | SPEC:326 | Terminal/TerminalPalette.cs;TerminalControl.cs:241-288 | TH::Dark_palette_carries_the_documented_16_colours、Theme_switch_re_answers_osc10_with_the_new_foreground、Code_font_size_change_re_measures_the_cell_and_resizes_the_grid | PASS |
| F-APP-D-74 | VM 标题取 OSC,否则“终端 n”;状态启动中/运行/已退出(码)/失败(因);退出后标签保留、画面末尾附“进程已退出”行 | SPEC:327 | TerminalViewModel.cs:167-170,627-636;TerminalView.axaml(底部状态行);Strings.Terminal.cs:51 | TL::Osc_title_becomes_the_tab_title、Exit_reports_the_code_in_a_localized_line | PASS |
| F-APP-D-75 | Host 未运行直接显示失败状态,不抛异常 | SPEC:327 | TerminalViewModel.cs:369-383(Fail) | TL::Missing_host_fails_with_the_localized_reason_and_never_throws、Unknown_terminal_fails_with_the_host_message_and_never_throws | PASS |
| F-APP-D-76 | 视图先于会话就绪布局:先记尺寸,会话就绪后补发 | SPEC:327 | TerminalViewModel.cs:391-422(启动后比对网格并 ResizeQuietly) | 无(就绪前布局时序无专门测试) | PASS* |
| F-APP-D-77 | 协调器按会话 id 持有;新建取 WorkDir+偏好 shell/参数(空格切分、双引号分组) | SPEC:328 | TerminalWorkbench.cs:33-36,66-113;TerminalCommandLine.cs:13-55 | TP::New_terminals_use_the_current_shell_and_arguments、Command_line_splits_on_spaces_with_double_quote_grouping | PASS |
| F-APP-D-78 | 切换会话:其它会话停靠在任意组的终端标签移出不结束;切回放回底部组;浮出标签留在自己窗口 | SPEC:328 | TerminalWorkbench.cs:119-170(FindTab 全布局查找) | TC::Switching_sessions_undocks_but_keeps_the_process_alive、Floating_terminals_survive_a_session_switch;TerminalMovedTabTests(3 项,含拖到其它组) | PASS |
| F-APP-D-79 | 会话归档结束该会话全部终端(含浮出) | SPEC:328 | TerminalWorkbench.cs:172-189;MainWindowViewModel.cs:2081-2083 | TC::Archiving_a_session_ends_its_terminals_including_floating_ones | PASS |
| F-APP-D-80 | App 退出先于组合根拆除结束全部终端,限时等待 | SPEC:328 | MainWindowViewModel.cs:3136-3139(Dispose 顺序)、3219-3232(外层 5s);TerminalWorkbench.cs:23,194-237(内层 4s 并行) | TC::Dispose_ends_every_terminal_of_every_session(超时分支无测试) | PASS |
| F-APP-D-81 | 关闭标签有前台进程:不弹对话框,终端视图内横幅“结束并关闭/取消” | SPEC:328 | TerminalWorkbench.cs:243-291(探测 2s,超时按 busy);TerminalView.axaml(terminal-close-banner);TerminalViewModel.cs:293,641-648 | TC::Busy_terminal_close_is_vetoed_and_shows_the_banner、Confirming_the_banner_ends_the_process_and_closes_the_tab、Cancelling_the_banner_keeps_the_tab_and_the_process、Idle_terminal_close_is_allowed_and_ends_the_process、Closing_an_already_gone_tab_is_a_no_op_veto | PASS |
| F-APP-D-82 | TabCloseRequested 是单值 Func<…,Task<bool>> 属性非事件;PanelTabStrip 关闭走它 | SPEC:329 | Workbench/WorkbenchLayout.cs:969,999;Controls/PanelTabStrip.cs:87-103(TryCloseTabAsync) | TC 关闭系列(经该钩子) | PASS |
| F-APP-D-83 | 终端标签不写进 open-tabs 快照 | SPEC:329 | Workbench/LayoutSnapshot.cs:110-118(OpenTabsSnapshot 无终端字段);MainWindowViewModel.cs:851-862 | 无(结构性排除,无终端专项快照测试) | PASS* |
| F-APP-D-84 | 入口三个:Ctrl+`、标题行 terminal-open(设置左边)、底部组“+”;文案表 Strings.Terminal.cs(terminal. 前缀) | SPEC:329 | MainWindow.axaml:29(Ctrl+OemTilde)、139-140;MainWindowViewModel.cs:317,2226-2227(terminal-new);Localization/Strings.Terminal.cs | TC::Open_docks...(命令路径;键绑定无测试) | PASS |
| F-APP-D-85 | 设置分区未注册(用户决定):TerminalSettingsViewModel 及模板已写好但未接线;shell/参数/回滚只能经偏好文件改 | SPEC:330 | Terminal/TerminalSettingsViewModel.cs(完整);App.axaml:89(模板);全仓无 SettingsSectionIds.Terminal 注册 | TVT::Terminal_settings_view_model_resolves_to_the_settings_body;TerminalPreferenceTests::Scrollback_section_clamps_before_reaching_the_preferences | PASS |
| F-APP-D-86 | 已知遗留:浮出终端标签仍写进 LayoutSnapshot.Floating,恢复时被丢弃无副作用 | SPEC:331 | WorkbenchLayout.cs:1050-1063(Capture 全量浮出)、ApplyFloating:61-89(FindTab 为 null 即丢) | 无(通用未知 id 丢弃路径,无终端专项测试) | PASS* |
| F-APP-D-87 | 已知遗留:关闭被否决时浮出标签不提到最前 | SPEC:331 | TerminalWorkbench.cs:285-288(`if (!tab.IsFloating)` 才 ActivateTab) | 无 | PASS* |
| F-APP-D-88 | 真实 LSP/PTY 运行时行为(Roslyn 项目加载/真实 shell 前台进程探测/真实退出码) | SPEC:284,328 | 依赖 ITerminalHost/LanguageServerProcess 真实实现 | 无集成测试:AT 用 ScriptedServer(EditorLspRig.cs:18-23)与 FakeTerminalHost;ET 用 FakeLanguageServerProcess | UNVERIFIED |

**计数:88 项 = PASS 65 + PASS* 16 + PARTIAL 4 + FAIL 1 + UNVERIFIED 2。**

## 非 PASS 项详情

### F-APP-D-48(FAIL)workspace.diagnostics.refreshSupport 未声明
- 证据:`src/Forge.Editor/Lsp/LspClient.cs:552-575` 的 workspace 能力节点无 diagnostics;全模块唯一 `refreshSupport` 在 :572,属 semanticTokens 且为 false。
- 缺口:SPEC:284 要求两者同时声明。workDoneProgress 已声明(:706),diagnostics.refreshSupport 缺失。客户端虽能应答 `workspace/diagnostic/refresh`(:751,LC:1190 证实),但守规矩的 Roslyn 见不到能力声明就不会发。
- 闭环缺环:项目加载完成后的“服务器主动通知重拉”一环断掉,诊断更新只剩编辑防抖 500ms 与打开后 2.5s 兜底(见业务闭环缺口 1)。
- 建议:实现修复(WriteClientCapabilities 的 workspace 节点补 diagnostics:{refreshSupport:true})+能力断言测试。

### F-APP-D-25(PARTIAL)semanticTokens.refreshSupport=false 与 refresh 触发矛盾
- 证据:LspClient.cs:570-573 声明 false;:751 仍处理 `workspace/semanticTokens/refresh`;App 侧订阅 SemanticTokensRefreshRequested(FileDocumentViewModel.Lsp.cs:733-735)。
- 缺口:SPEC:274 把服务器 refresh 列为请求时机,能力声明却告诉服务器“别发”。ESL::A_refresh_request_from_the_server_asks_again_and_the_new_classification_replaces_the_old 用假服务器直发请求,掩盖了真实服务器不会发的事实。
- 建议:改 true,或规格明示不依赖服务器 refresh;二者择一。

### F-APP-D-04(PARTIAL)20000 行折叠上限只挡本地路径
- 证据:EditorFoldingController.cs:236-241 的行数检查在 `ComputeLocallyAsync` 内;`RecomputeAsync`(:186-216)对服务器返回的 foldingRange 不做行数检查直接 Apply。
- 缺口:SPEC:264 措辞无条件。服务器声明 foldingRangeProvider 时超大文件仍会折叠;EF::A_document_above_the_folding_cap_says_so_instead_of_folding 只覆盖无会话路径。风险低(服务器范围应用成本低),但与规格文字不符。
- 建议:规格澄清上限仅约束本地路径,或服务器路径补同样检查。

### F-APP-D-11(PARTIAL)暗色选区色相偏离品牌蓝
- 证据:Tokens.axaml:159 亮色 `#3D3399FF`(α=61≈24%,色相 #3399FF ✓);:238 暗色 `#4D5AA9FF`(α=77≈30% ✓,但色相是 #5AA9FF,不是规格指定的 #3399FF)。
- 缺口:SPEC:267 指定品牌蓝 #3399FF 两档透明度;暗色实际用了更亮的蓝。测试只断言“染色不遮字”,未断言色值。
- 建议:规格承认暗色提亮,或改回 #3399FF;低危外观偏差。

### F-APP-D-61(PARTIAL)性能声明只有宽松预算测试
- 证据:TPF::Twenty_megabytes_feed_completes_within_budget 断言 20 MiB < 5s(≈≥4 MiB/s),99 MiB/s 只作为输出打印;“稳态滚动零分配”无任何分配断言。
- 缺口:SPEC:324 的“实测约 99 MiB/s、零分配”是历史测量叙述,现有测试不能证实也不能证伪。
- 建议:测量值移出规范性声明;如保留零分配契约则补分配测试。

### F-APP-D-54 / F-APP-D-88(UNVERIFIED)
- AOT 无警告需 `dotnet publish` 验证,只读审计不执行构建。
- 真实 LSP/PTY:Roslyn 真实加载时序、`HasForegroundProcessAsync` 对真实进程树的探测、真实 shell 退出码/信号路径均无集成测试;客户端接线已被假件充分覆盖,故其余项不因此降级。
- 建议:补真实运行时冒烟集成测试(可标 trait 排除出常规 CI)。

## 已实现但缺测试清单(PASS*,16 项)

| ID | 缺口 | 建议 |
|---|---|---|
| 02 | 折叠 250ms 防抖与过期版本丢弃无测试 | 用可控时钟/版本推进断言丢弃 |
| 03 | 折叠状态不持久化无测试 | 断言重开视图后无折叠 |
| 10 | Ctrl+Shift+[ / Ctrl+Alt+[/] 键路径无测试 |  headless 按键断言 |
| 13 | 矩形选择(Alt+拖拽列编辑)App 侧无测试 | 库行为,至少断言选项开启与菜单文案 |
| 18 | isIncomplete=true 的下一字符重试无 App 测试 | ScriptedServer 回 isIncomplete:true |
| 36 | 壳层 OpenProblemAsync(跨工作区打开+评审归属)无测试 | 双工作区诊断打开断言 workspaceOverride/FindReviewOwner |
| 47 | initialize 声明 workDoneProgress 无断言 | 能力快照测试 |
| 50 | 打开后 2.5s 延时重拉兜底无测试 | 可控时序断言第二次 pull |
| 51 | $/progress→“正在加载项目”状态行(App 侧)无测试 | 假服务器发 begin/end 断言状态文案 |
| 69 | 滚轮三分支(上报/备用屏方向键/回滚)无测试 | headless 滚轮断言 SendInput |
| 70 | Ctrl+点击 OSC8 的 http/https 过滤无控件级测试 | 断言 file:// 不打开 |
| 72 | 视口回滚锚定补偿无测试 | 查看历史时注入输出断言 scrollOffset |
| 76 | 会话就绪前布局尺寸补发无测试 | 先 Resize 后 Start 断言 ResizeAsync 收到真实网格 |
| 83 | open-tabs 不含终端无专项测试 | 快照序列化断言 |
| 86 | 浮出终端写 Floating/恢复丢弃无终端专项测试 | 捕获→恢复断言无副作用 |
| 87 | 关闭否决时浮出不前提(遗留行为)无测试 | 断言 IsFloating 分支 |

另未测:F-22 兜底、F-37 attach、F-46 重放、F-71 时序、F-80 超时、F-84 键绑定。

## 程序→规格:未记录行为

1. **终端非零退出码弹系统通知**(TerminalViewModel.cs:447-455,NotificationCenter.Warning)。SPEC:327 只写状态行与标签保留,未记录通知副作用。
2. **BEL 刻意静音**(TerminalViewModel.cs:316-320)。规格未置可否。
3. **补全触发符无条件追加 `.`**(FileDocumentViewModel.Lsp.cs:536-549,“每个服务器都意味着它即使忘了声明”)。SPEC:272/280 只说“服务器声明的触发符”。
4. **引用列表 2000 行截断+提示**(FileDocumentViewModel.Lsp.cs:33-34,521-527;ReferenceListViewModel.cs:41-45)。SPEC:276 未记录(2000 上限只写在问题面板条目)。
5. **安装目录最多探 8 个子目录**(LanguageServerDiscovery.cs:89-90,277-283)。SPEC:282 只写了扩展 32 版本上限。
6. **tsserver 查找末尾追加 PATH 回退 `resolve("tsserver")`**(LanguageServerDiscovery.cs:174-183)。规格来源序列未含此项。
7. **粘贴规范化换行**(TerminalControl.cs PasteAsync;测试名 Bracketed_paste_wraps_and_normalizes_newlines 证实)。SPEC:326 只说“去掉转义字符”。
8. **菜单/工具栏复制无选区时复制整个文件**(FileDocumentView.axaml.cs:333-341);Ctrl+C 键路径走库(整行),两条路径不一致且均未记录。
9. **终端初始网格 100×30**(TerminalViewModel.cs:46-49);**前台进程探测 2s 超时按“有程序”处理**(TerminalWorkbench.cs:27-30,262-270)。规格未记录默认值/超时策略。
10. **终端右键上下文菜单(复制/粘贴)**(TerminalControl.cs:122 附近)。SPEC:326 未记录。
11. **OSC 8 链接 id 上限 4096、OSC 串 1 MiB 上限、CSI 参数 32 个上限**(TerminalEmulator.Parser.cs:9-18;AT TerminalHyperlinkClipboardTests::Osc8_ids_stop_at_4096_and_overflow_prints_without_link)。防御上限未记录。
12. **语义叠加与词法共用 1 Mi 字符上限,超限清空索引**(FileDocumentViewModel.Lsp.cs:797-808;ForgeEditorBridge.cs:22)。SPEC:250 只记录 TextMate 跳过,未提语义层同限。
13. **暗色选区色相 #5AA9FF**(见 F-11)。

## 规格质量问题

1. **SPEC:330 理由过时**。“注册会和 SettingsPanelTests 对分区列表的硬性断言冲突”不再成立:tests/Forge.App.Tests/Foundation/SettingsPanelTests.cs:490-517 的 KnownSectionIds 已预留 `SettingsSectionIds.Terminal`(注释明言“parallel feature tasks need not edit this list”),且测试只断言首/尾分区与 Contains,无分区列表硬断言。未注册仍是事实(用户决定),但依据应更新。
2. **SPEC:274/284 与能力声明的矛盾/契约缺失**。284 要求声明 workspace.diagnostics.refreshSupport(实现缺失,F-48);274 依赖服务器 semanticTokens/refresh 却未记录客户端 refreshSupport 立场(实现声明 false,F-25)。规格应把“客户端能力声明”写成可断言契约,否则此类缺口只能靠真实服务器暴露。
3. **SPEC:324 历史测量混入规范性声明**。“实测吞吐约 99 MiB/s”“稳态滚动零分配”是一次性测量叙述,测试只证宽松预算(F-61);同段“已记录偏差”写法好,应把测量值同样降级为注记。
4. **SPEC:276 模糊+漏记**。“列表以 Flyout 贴在光标处”未区分文本光标与鼠标指针:实现用 `PlacementMode.Pointer`(FileDocumentView.Lsp.cs:655),Shift+F12 是键盘操作,鼠标可能在远处,浮出位置与“光标处”读感不符;引用 2000 截断未记录(见未记录行为 4)。
5. **SPEC:282 单段超载**。约 700 字一段混排 12 语言表、三布局、32 版本、jsonc 判定序、tsserver 三来源、合并规则、状态行文案、Pylance 许可,且漏记 8 子目录上限与 PATH tsserver 回退;建议拆子弹并补上限。
6. **SPEC:264 上限措辞与实现不一致**(F-04):“超过 20000 行不折叠”未限定本地启发式路径。
7. **SPEC:267 色值与实现不一致**(F-11):暗色选区非 #3399FF。

## 业务闭环缺口

1. **Roslyn 诊断重拉闭环断环(最高优先)**:refreshSupport 未声明(F-48)→ 服务器加载完项目后不通知;打开 2.5s 兜底(F-50)若早于加载完成即被丢弃,此后用户不编辑就永远没有诊断。编辑触发与兜底都在,唯独“服务器就绪→主动重拉”一环缺失。修复=补声明+断言测试。
2. **语义分类同型断环**:semanticTokens.refreshSupport=false(F-25)使项目型服务器分析完成后的 refresh 不会到来,2.5s 兜底后分类可能长期停留在旧分析结果,直到下一次编辑。
3. **跨工作区问题打开零壳层测试**:OpenProblemAsync 的 workspaceOverride 与 FindReviewOwner(MainWindowViewModel.Problems.cs:20-89)决定评审归属正确性,注释自述“错工作区会把评论挂到别的会话”,但该路径无测试(F-36),是问题面板闭环里风险最高、覆盖最薄的一段。
4. **真实运行时整体未验证**(F-88):Roslyn 加载、真实 PTY 前台探测(关闭横幅的判据)、真实退出码路径都只有假件证据;横幅闭环在假件下完整,真实进程树探测失败/超时策略(2s→按 busy)未经实机验证。
5. **退出限时等待的超时分支无测试**(F-80):4s/5s 预算存在且顺序正确(先终端后组合根),但 Host 不答复时的降级路径只靠代码审查。
6. **终端设置入口缺口(已记录的决策)**:shell/参数/回滚只能改偏好文件(F-85 与规格一致);鉴于 SettingsPanelTests 已预留 terminal id(规格质量问题 1),重新评估注册成本的条件已变化,建议提请用户复核该决定。
