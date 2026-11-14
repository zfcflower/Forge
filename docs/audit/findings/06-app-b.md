# App B(工作台外壳/会话 UI/通知/布局)审计发现

审计范围:src/Forge.App/SPEC.md 指定主题(行号见矩阵)。只读审计,未改任何源码/规格/测试,未跑 build/test。
路径约定:代码相对 src/Forge.App;测试 T=tests/Forge.App.Tests,L=tests/Forge.App.LiveSwitch.Tests;T 子目录缩写 W=Workbench,V=ViewModels,N=Notifications,F=Foundation,Lo=Localization,Ap=Appearance,G=Git,Ch=Changes,Vi=Views,Pr=Providers,Au=Automations,Co=Composition,Te=Terminal。规格位置为 SPEC.md 行号。
结论:PASS=实现+测试证据;PASS*=实现存在、无直接测试(纯视觉/资源键静态核对也算证据);PARTIAL/FAIL/SPEC_GAP/UNVERIFIED 见详情。测试反模式判据已按 .github/skills/test-anti-patterns/SKILL.md 校准(下列测试均含真实断言,未发现空断言/吞异常式假证据)。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-B-01 | 面板按最终分配宽高测量排列,视口不借整窗/整区高度;无底部留白/滚动补偿 | 50 | Workbench/Controls/WorkbenchHost.cs:368-375(Place 直接按最终 rect Measure/Arrange) | W/PanelScrollExtentTests::Last_row_viewport_and_scrollbars_stay_inside_panel | PASS |
| F-APP-B-02 | 分割/抽屉/最大化/缩放后滚到末端完整见最后一行 | 50 | 同上(状态矩阵) | 同上(list×panel×state 矩阵) | PASS |
| F-APP-B-03 | 仍可见面板不被临时排列到零尺寸 | 50 | WorkbenchHost.cs:117-261(仅隐藏区域得 default rect) | 同上 | PASS |
| F-APP-B-04 | logo 18×18 DIP;PNG/ICO 保留透明通道 | 52 | MainWindow.axaml:57-62 | MainWindowStartupTests::Embedded_logo_preserves_transparent_background_and_visible_artwork | PASS |
| F-APP-B-05 | 标题栏用 forge.png;窗口 Title 仍 Forge | 54 | MainWindow.axaml:11,57-66(另有 FORGE 字标,见 Q9) | MainWindowStartupTests::Opening_the_shell_applies_the_logo_and_screen_placement | PASS |
| F-APP-B-06 | ICO 含 7 尺寸层;ApplicationIcon 嵌入;复制到发布目录 | 54 | Forge.App.csproj:8,12;Assets/forge.ico(ICO 目录计数=7) | MainWindowStartupTests::Embedded_windows_icon_contains_all_shell_and_high_DPI_sizes | PASS |
| F-APP-B-07 | 所有窗口经应用样式共用图标 | 54 | App.axaml:797-800(:is(Window) Icon setter) | 无 | PASS* |
| F-APP-B-08 | 启动居中 WorkingArea;按桌面缩放换算并计原生边框;小屏降低最小尺寸 | 54 | MainWindow.axaml.cs:72-92 | MainWindowStartupTests::Startup_fits_and_centres_the_entire_frame_in_the_working_area | PASS |
| F-APP-B-09 | 打开后按实际屏幕/缩放/边框再校正一次;后续移动/缩放不重居中 | 54 | MainWindow.axaml.cs:157-167(仅构造+OnOpened 调用) | 同上 | PASS |
| F-APP-B-10 | 偏好 setter 即校验器:空字体族回退默认、字号夹 9–28,即时生效 | 57 | Preferences/AppPreferences.cs:233-239,890-897 | F/PreferencesStoreTests::The_size_setters_clamp | PASS |
| F-APP-B-11 | 写回读整份 JsonNode 只换 preferences 段;AtomicFile 原子替换;provider/未知字段保留 | 57 | AppPreferences.cs:1300-1345 | F/PreferencesStoreTests::Round_trip_keeps_every_other_section | PASS |
| F-APP-B-12 | 读写均线程池;LoadAsync 只读不写不发通知 | 57 | AppPreferences.cs:1196-1210,609(ApplyLoaded) | F/PreferencesStoreTests::Missing_file_yields_defaults_and_writes_nothing | PASS |
| F-APP-B-13 | 每次修改重启 500ms 防抖、池上写一次;DisposeAsync 强制落盘 | 57 | AppPreferences.cs:1263-1297 | F/PreferencesStoreTests::A_burst_of_edits_becomes_one_write;Dispose_writes_the_pending_edit | PASS |
| F-APP-B-14 | 损坏→默认+LoadProblem,加载不覆盖;首写前 .bak 只备份一次 | 57 | AppPreferences.cs:1320-1332,1352-1410 | F/PreferencesStoreTests::A_corrupt_file_is_backed_up_once_before_the_first_write | PASS |
| F-APP-B-15 | 源生成 PreferencesJsonContext | 57 | AppPreferences.cs:1338 | F/PreferencesStoreTests(往返) | PASS |
| F-APP-B-16 | Register 一名一键;同 key 异文抛异常、同文幂等 | 58 | Localization/Loc.cs:110-135 | F/LocalizationTests::Registering_the_same_key_with_different_texts_throws | PASS |
| F-APP-B-17 | Language 永不 System;切换依次 Item[]→Item→LanguageChanged | 58 | Loc.cs:47-62,186-196 | F/LocalizationTests::Switching_the_language_raises_item_changes_and_language_changed | PASS |
| F-APP-B-18 | 缺失 key 返回 [key] 且每 key 只报一次 | 58 | Loc.cs:66-84,171-181(用 ZLogWarning,规格写 Trace) | F/LocalizationTests::A_missing_key_is_reported_once | PARTIAL |
| F-APP-B-19 | 每功能一表 ModuleInitializer 注册;EnsureRegistered 只保顺序点 | 58 | Loc.cs:141-149;Localization/Strings.*.cs | Lo/StringTableTests::Every_key_has_a_Chinese_and_an_English_text | PASS |
| F-APP-B-20 | LanguageChanged 按订阅者逐个 try/catch,不中断整批 | 58 | Loc.cs:205-224(RaiseAll) | 无 | PASS* |
| F-APP-B-21 | 系统语言链:zh*→注册表三键→LC_ALL/LC_MESSAGES/LANG→英;BCL 无 P/Invoke;读失败当不存在 | 58 | Localization/SystemLanguageDetector.cs:102-140 | Lo/SystemLanguageDetectorTests::A_present_registry_value_beats_the_environment_even_when_it_says_English | PASS |
| F-APP-B-22 | language 分区三选项;语言名不随界面语言变 | 58 | Localization/LanguageSettingsViewModel.cs | Lo/LanguageSettingsViewModelTests::Each_language_keeps_its_own_name_in_both_interfaces | PASS |
| F-APP-B-23 | 所有 LanguageChanged 订阅者记创建线程、忽略跨线程通知 | 62 | 部分有守卫(NotificationCenter.cs:283 等 23 文件);LanguageSettings/AppearanceSettings/Terminal/ApprovalQueue/Compaction/NewBranchDialog 等 VM 无守卫 | L/LiveSwitchTests::Language_subscriptions_do_not_grow_with_opened_and_closed_sessions(仅订阅数) | PARTIAL |
| F-APP-B-24 | SettingsSections.Add 按 Order 升序稳定插入;重复 id 抛;Title 是 Loc key 且切换重抬 | 59 | Preferences/SettingsSection.cs:26-29,43-57 | F/SettingsSectionsTests::A_duplicate_id_throws_and_leaves_the_registry_alone | PASS |
| F-APP-B-25 | 注册表组合根暴露;核心 Create()+App.axaml 模板;波次 Composition+SectionViewTemplate.Register;RegisterAll 一次调用 | 59 | Composition/AppComposition.cs:733;FeatureComposition.cs:16;InspectorComposition.cs:23 | F/SettingsSectionsRegistrationTests::The_runtime_sections_are_registered_where_the_panel_expects_them | PASS |
| F-APP-B-26 | 设置独立窗口 1040×760、最小 720×480、BorderOnly+自绘标题行 | 59 | Settings/SettingsWindow.axaml:10-17 | F/SettingsPanelTests::The_settings_window_renders_the_provider_and_paths_sections_in_grouped_order | PASS |
| F-APP-B-27 | 左导航 AGENT/界面/系统三组+图标 key;右 ScrollViewer+ContentControl;默认 provider | 59 | Settings/SettingsNav.cs;ViewModels/SettingsViewModel.cs:56-63(Order 最小=provider100) | F/SettingsPanelTests::The_settings_window_groups_sections_agent_then_interface_then_system | PASS |
| F-APP-B-28 | <880 DIP 左栏收 48 DIP 图标条+tooltip;手动收起优先;全部分区入口保留 | 59 | SettingsViewModel.cs:22,28,117,162-182;SettingsWindow.axaml:103-133 | F/SettingsPanelTests::A_narrow_column_folds_itself_and_the_toggle_overrides_the_width | PASS |
| F-APP-B-29 | 导航用 Button+Click 而非 ListBox | 59 | SettingsWindow.axaml.cs:71-77 | 无 | PASS* |
| F-APP-B-30 | 即时切换测试在独立项目共用一个 HeadlessUnitTestSession;App.Tests 不写 XAML 直绑实时断言 | 62 | L/Support.cs:7-25(单 fixture+禁并行) | L/LiveSwitchTests::Empty_session_state_text_follows_the_language_live | PASS |
| F-APP-B-31 | 长生命周期所有者 Dispose 停计时器并解 Tick(MainWindowVM/FilesPanelVM) | 63 | ViewModels/MainWindowViewModel.cs:3206-3212;ViewModels/Panels.cs:853-868 | 无(直接) | PASS* |
| F-APP-B-32 | AppComposition.DisposeAsync 释放 IDisposable 设置分区 VM | 63 | Composition/AppComposition.cs:868-872 | 无(直接;Co/CompositionDisposeTests 仅测 reviews 句柄) | PASS* |
| F-APP-B-33 | 三区按角色命名;默认 项目\|主\|检视;可互换、内容随角色 | 64 | Workbench/WorkbenchLayout.cs:264 | W/WorkbenchLayoutRegionTests::Swap_keeps_each_region_width_with_the_region | PASS |
| F-APP-B-34 | 项目区上项目下常规对话;1px 线可拖写 ProjectSplitRatio(默认0.5,随快照 projectSplit) | 65 | Workbench/Controls/RegionPresenter.cs:300-308;WorkbenchLayout.cs:602-614 | W/WorkbenchControlsTests(projectSplit 拖拽/恢复) | PASS |
| F-APP-B-35 | 两 panel 是同一 ProjectTreeVM 的两实例,TreeScope 各计划自己那半,各有刷新/命令/标题工具 | 65 | ViewModels/ProjectTree.cs:792,1221 | G/ProjectTreeWorktreeSessionTests | PASS |
| F-APP-B-36 | 常规树直接是会话行嵌子 Agent 行;归档收进各 worktree 历史(n) | 65 | ProjectTree.cs:1236 | G/ProjectTreeWorktreeSessionTests | PASS |
| F-APP-B-37 | 项目标题栏:添加项目+刷新;绿色20px IconProjectNew 为唯一添加入口;语言切换原位重建;常规标题栏新建+刷新同规格 | 65 | MainWindowViewModel.cs:2235-2270;Workbench/Controls/HeaderToolTemplate.cs:38-48 | W/AddProjectFlowTests::A_created_project_is_registered;W/WorkbenchIconSizeTests | PASS |
| F-APP-B-38 | panel 体内无工具条/主按钮,只有状态文本和树 | 65 | Views/ProjectView.axaml(体=状态+树) | 无 | PASS* |
| F-APP-B-39 | 整行可点 Activate(会话打开/有子展开/无子新建/子Agent前置);Enter/空格同效;行内按钮只执行按钮 | 65 | ProjectTree.cs:649-679;Views/ProjectView.axaml.cs:148-157 | TreeRowActivationTests | PASS |
| F-APP-B-40 | 选中属于用户:空白/Esc 清;按 Key 记住,行集合重建后放回;树不自取消选中 | 65 | Views/ProjectView.axaml.cs:17-143 | 无(直接) | PASS* |
| F-APP-B-41 | 项目名称行 Bold | 65 | Views/ProjectView.axaml:12 | 无 | PASS* |
| F-APP-B-42 | 每仓库第一子行=主检出:房子图标/HEAD 真实分支名/游离HEAD文案/永在且排前/根会话挂其下 | 65 | ProjectTree.cs:217,1443;ViewModels/TreeIcons.cs:47 | G/ProjectTreeMainCheckoutTests | PASS |
| F-APP-B-43 | HEAD 每刷新读一次,≤4 并发,失败按非仓库 | 65 | ProjectTree.cs:802,1178 | G/ProjectTreeMainCheckoutTests | PASS |
| F-APP-B-44 | 行尾+两种:项目行分支加号/常规与worktree行纯加号;12px 图标 22px 按钮;tooltip 区分 | 65 | Views/ProjectView.axaml:132-138;TreeIcons.cs:36-39 | G/ProjectTreeNewBranchFlowTests | PASS |
| F-APP-B-45 | 常规与项目共用容器图标 TreeIcons.Project | 65 | TreeIcons.cs:31-33(General=>Project) | 无 | PASS* |
| F-APP-B-46 | 检视区文件在上工具组在下;相邻 panel 间 1px 线均可拖(现一条) | 66 | RegionPresenter.cs:598-646 | W/WorkbenchControlsTests::The_inspector_has_one_panel_splitter_between_files_and_tools_and_restores_a_stored_height | PASS |
| F-APP-B-47 | 文件槽高由 InspectorSplitRatio;belowHeights 按 id 存 px,缺省取已拖均值、全无平分 | 66 | RegionPresenter.cs:517-595;Workbench/LayoutSnapshot.cs:48 | W/LayoutStateLoaderTests(belowHeights 往返) | PASS |
| F-APP-B-48 | 拖动只移一条线:增量由紧邻下方吸收;两侧≥PanelMinHeight;装不下两个最小值则不动 | 66 | Workbench/PanelStackSolver.cs:70-81 | W/PanelStackSolverTests::A_pair_too_short_for_two_minimums_does_not_move | PASS |
| F-APP-B-49 | 最小化 panel 只占标题栏;两侧线不可拖;让出高度按比例重铺 | 66 | RegionPresenter.cs:568-572,608-636 | W/WorkbenchControlsTests::Minimizing_the_bottom_group_leaves_its_title_bar_on_the_bottom_edge_of_main | PASS |
| F-APP-B-50 | 主区=主选项卡组(只会话标签)+底部组 | 67 | WorkbenchLayout.cs(MainTabs/BottomTabs) | W/WorkbenchLayoutRegionTests::Main_region_holds_main_tabs_then_bottom_group | PASS |
| F-APP-B-51 | 文件面板两标签各带中性数量徽标(选中翻色)+刷新;全部=索引文件数/已改动=范围内改动数;0 照画;无会话双0;数字进 AutomationProperties.Name | 68 | Views/FilesToolView.axaml:155-187;ViewModels/Panels.cs:387-398 | FilesPanelTests::Tab_badges_count_the_whole_index_and_the_change_list_and_show_zero_without_a_session | PASS |
| F-APP-B-52 | 自绘范围下拉(全部/未提交/已完成轮次);默认未提交 | 68 | FilesToolView.axaml:219-310;Panels.cs:1038,1062 | ChangedFilesScopeTests | PASS |
| F-APP-B-53 | 自绘基线分支下拉:本地/远端分组、组名不可选、origin/HEAD 不列、当前分支不列、命令执行后才关弹层 | 68 | ViewModels/BranchPickerViewModel.cs:217,323-324;Views/FilesToolView.axaml.cs:29(HideAfterCommand) | 无(直接) | PASS* |
| F-APP-B-54 | 文件面板顶部两行 git 头:行0 24px 分支+抓取/拉取/推送+数字+结果;行1 28px 发布+提交目标;无仓库同隐 | 69-70 | FilesToolView.axaml:509,586-592 | G/GitSyncViewModelTests | PASS |
| F-APP-B-55 | GitSync 线程模型:池上读/UI 整体应用/每 worktree 有界状态表/读随上下文取消/写跑完为止 | 70 | Git/GitSyncViewModel.cs:56-83,783,832 | G/GitSyncWorktreeRaceTests | PASS |
| F-APP-B-56 | 行0让位:三动词+详情右停靠永不裁;结果≤150px;分支名占余宽省略;200px 先分支后结果;tooltip 不写 remote | 70 | FilesToolView.axaml:593-714(DockPanel+MaxWidth150) | 无(布局) | PASS* |
| F-APP-B-57 | 行1远程读数是文本非按钮;200px 先收远程读数,两药丸完整 | 71-72 | FilesToolView.axaml:509-585(git-ship-remote) | 无(布局) | PASS* |
| F-APP-B-58 | 一会话一 AgentHost 可并发;关标签不取消运行;重开/取消归档走 ResumeRootAsync | 138 | Agents/SessionRuntime.cs:581;MainWindowViewModel.cs:2000-2020(OnTabClosed 仅 !IsRunning 释放) | W/SessionLifecycleShellTests | PASS |
| F-APP-B-59 | 项目上新建会话=新 worktree;对话框共享 BranchPicker;预填 forge/session-MMdd-HHmmss;250ms 防抖池上校验、过期丢弃;取消/Esc/关窗不建 | 138 | ViewModels/NewBranchDialogViewModel.cs:20 | V/NewBranchDialogViewModelTests;G/ProjectTreeNewBranchFlowTests | PASS |
| F-APP-B-60 | 新分支从所选起点解析的提交分叉,不设上游跟踪 | 138 | Services/WorktreeService.cs(worktree add -b) | G/ProjectTreeNewBranchFlowTests | PASS |
| F-APP-B-61 | 常规会话在 scratch/<日期-标题>/ | 138 | Services/WorktreeService.cs:181-185 | G/ProjectTreeWorktreeSessionTests | PASS |
| F-APP-B-62 | 释放点:归档/标签已关且回合结束/组合根拆除前(MainWindowVM.Dispose 先于 AppComposition.DisposeAsync) | 138 | MainWindowViewModel.cs:2078-2083,3116-3174;App.axaml.cs:82-85 | Co/CompositionDisposeTests | PASS |
| F-APP-B-63 | 检查点快照失败不卡运行中:记会话通知,状态重置与 TurnCompleted 在嵌套 finally | 138 | ViewModels/SessionWorkspace.cs:1375-1445 | V/SessionTurnFailureTests::A_failing_checkpoint_begin_leaves_the_session_idle_and_completes_the_turn_once | PASS |
| F-APP-B-64 | composer 卡单一 10px 圆角外轮廓:白条高44可见34底角方;灰区四角全圆上沿压10px;各画1px边框;聚焦双边框强调;卡本身透明 | 144 | Views/SessionView.axaml:13-56 | 无(几何) | PASS* |
| F-APP-B-65 | 上条左起:↑a↓b、资源按钮、子Agent开关、竖分隔+工作区按钮 | 144 | SessionView.axaml:477-580 | 无 | PASS* |
| F-APP-B-66 | 子Agent徽标只数 Running;0 无徽标回纯文字与 a11y 名;激活 AccentSubtle;hover 规则排 .active 后;无子Agent不出现 | 144 | SessionView.axaml:100-110,527-545 | V/SessionEventRoutingTests::The_strip_badge_counts_only_sub_agents_that_hold_a_run_slot | PASS |
| F-APP-B-67 | 资源按钮合四计数成菜单,点行开对应设置分区;计数口径(审批/技能/MCP/工具,0/0 起步) | 144 | SessionView.axaml:488-520;ViewModels/SessionInstrumentsViewModel.cs:763 | V/SessionInstrumentsViewModelTests | PASS |
| F-APP-B-68 | 工作区按钮=文件夹+目录末段+▾;四项菜单(项目/工作区×VSCode/资源管理器);无项目根只两项;无 code 隐 VSCode | 144 | SessionView.axaml:549-580 | V/SessionInstrumentsViewModelTests | PASS |
| F-APP-B-69 | 上条无路径/无运行状态点/无分支小片;↑↓保留;空会话占位只留标题提示 | 144 | SessionView.axaml:397,466-477 | 无 | PASS* |
| F-APP-B-70 | 右侧 token 读数:RC 一位小数+五段斜四边形量规+C/U·%;窗口未知只写 C;量规+读数是一个按钮 strip-context-compact(含 gauge/label);悬停说明与固定五段浮层 | 145 | SessionView.axaml:596-651;SessionInstrumentsViewModel.cs:250-254 | V/SessionInstrumentsViewModelTests | PASS |
| F-APP-B-71 | 量规=五个固定 Polygon 13×8 斜3,非 ProgressBar;常驻最右;无数据 RC0.0+全空心+0% 不隐藏不变尺寸 | 145 | SessionView.axaml:636-644;SessionInstrumentsViewModel.cs:26(Count=5) | V/SessionInstrumentsViewModelTests | PASS |
| F-APP-B-72 | 量规三态:触发线−10pp 警告/≥触发线超限/未知恒常态;点击=/compact 同 CompactNowAsync,可用性同 CanCompactNow | 145 | SessionInstrumentsViewModel.cs:600;SessionWorkspace.cs:241 | V/SessionEventRoutingTests::The_gauge_command_compacts_only_when_compaction_is_allowed | PASS |
| F-APP-B-73 | 用量只算根 Agent;C=ContextTokens、U/触发线=BudgetFor;重开会话下次请求前 0%;子Agent报告忽略 | 145 | SessionInstrumentsViewModel.cs:562(NoteContext) | V/SessionEventRoutingTests::The_context_gauge_follows_the_root_agents_reports_and_an_automatic_compaction | PASS |
| F-APP-B-74 | 成本=max(0,token)×单价/1e6 缓存读写分计;RC=CacheRead/Input;元数据 modelMeta>/models>目录逐字段合并+memo;未知模型无 %/$ | 145 | SessionUsageMath;ModelMetadataResolver | Co/ProviderMetadataTests | PASS |
| F-APP-B-75 | AgentNotice 警告 toast+当轮卡片一行;同前缀一小时整段重写3次→信息 toast 一次(会话级去重,首轮排除) | 145 | SessionWorkspace.cs:92,1856-1864 | V/CacheRewriteNoticeTests::Only_a_root_turn_that_wrote_and_read_nothing_counts | PASS |
| F-APP-B-76 | 已连接列表每行缓存寿命下拉(provider-cache-ttl-<id>);仅 SupportsPromptCacheTtl 显示;选择即保存只换该字段;自动=无字段、手写 auto 保留 | 145 | Providers/ProviderStore.cs(SaveCacheTtlAsync) | Pr/CacheTtlSettingTests | PASS |
| F-APP-B-77 | strip-cache-ttl 小片=AgentContextMeasured.CacheTtl;promptCacheTtl 经 SessionRuntime→AgentOptions.CacheTtl 真接线 | 146 | SessionInstrumentsViewModel.cs:296-313;Agents/SessionRuntime.cs:995 | Pr/CacheTtlSettingTests | PASS |
| F-APP-B-78 | strip-quota 订阅/限流双语义标签首词区分;倒计时≤90s 用秒≤5s 即将;颜色只跟服务端状态;提供商不符整段隐;每次 RefreshCounters 重算;不轮询不持久化 | 146 | SessionInstrumentsViewModel.cs:260-355 | V/SessionInstrumentsQuotaTests | PASS |
| F-APP-B-79 | InstrumentLayout 纯函数:只有 ↑↓ 让位(<640 隐);其余永不让位 | 146 | ViewModels/InstrumentLayout.cs:14 | V/InstrumentLayoutTests | PASS |
| F-APP-B-80 | Agent/模型/强度选择;模型弹层搜索+星标钉住在前;ThinkingStepsFor 滑条(只列接受档、换模型夹到最近档);新会话默认存 composer.json;刷新单失败不挡其余;改选下一回合前重建主 Agent | 147 | ViewModels/ComposerViewModel.cs:324,1026,1046 | V/ComposerSuggestionTests::Models_list_the_configured_and_extra_models_with_pinned_first;V/ComposerQueueTests::Choosing_an_agent_and_effort_sets_the_root_definition_for_the_next_turn | PASS |
| F-APP-B-81 | 空闲圆形强调发送(空草稿禁用);运行中停止+圆形…菜单:插话/排队/转弯/停止语义 | 147 | SessionView.axaml:286-310,985-1030 | V/ComposerQueueTests::Stop_pauses_the_queue_until_the_next_send | PASS |
| F-APP-B-82 | 词首/开技能+命令筛选(命令在前同名技能隐、发送前展开);@开文件选择器≤50;上下/Enter/Tab/Esc;插入带空格 | 147 | ComposerViewModel.cs:711-735 | V/ComposerCommandTests::Commands_come_before_skills_and_a_skill_with_a_command_name_is_hidden;V/ComposerSuggestionTests::Files_rank_name_prefix_then_name_then_path_then_fuzzy_and_are_capped | PASS |
| F-APP-B-83 | 插话经 Core Interject/TakeInterjections;回合卡提示三形态;小片按同文本+同附件实例移除,退回文本+附件数首枚 | 147 | SessionWorkspace.cs:1160-1188,1763 | V/InterjectionNoticeTests::Without_the_same_instances_the_first_chip_with_the_same_text_and_count_is_taken | PASS |
| F-APP-B-84 | 事件按 AgentId 分发到各自轮次卡;子Agent文本不进主会话 | 148 | SessionWorkspace.cs:1557-1611 | V/SessionEventRoutingTests::A_child_agents_text_never_reaches_the_main_transcript | PASS |
| F-APP-B-85 | FrameCoalescer:任意线程无锁入队;仅无排队 flush 时 Post 一次(Background+Interlocked);FIFO;单次上限2048 超出留下帧 | 148 | ViewModels/FrameCoalescer.cs:16-99 | V/FrameCoalescingTests::Single_flush_respects_the_item_cap | PASS |
| F-APP-B-86 | 文本增量后台合并 pending;flush 先排空文本再 FIFO 非文本 | 148 | SessionWorkspace.cs:155,1611(FlushAllPendingText) | V/SessionEventRoutingTests::Text_that_arrives_before_its_start_event_is_not_lost | PASS |
| F-APP-B-87 | 会话释放合并器停调度丢弃未处理,不再投递 UI | 148 | FrameCoalescer.cs:60-69;SessionWorkspace.cs:1996 | V/FrameCoalescingTests::Items_enqueued_after_dispose_are_dropped_without_throwing | PASS |
| F-APP-B-88 | 文件/规格树展开为 RangeObservableCollection+虚拟化 ListBox;池上构建 UI 一次 ReplaceAll | 148 | ViewModels/Collections.cs:9;各视图 VirtualizingStackPanel | FilesPanelTests::Row_count_equals_visible_nodes_with_depth_steps_of_at_most_one | PASS |
| F-APP-B-89 | 轮次卡头第N轮·时间·耗时·输入/输出:累计用量取本轮差值;子Agent用本run用量;未报— | 148 | ViewModels/Transcript.cs:298-306;SessionWorkspace.cs:1811-1814 | 无(直接) | PASS* |
| F-APP-B-90 | 助手回复 Markdown 渲染,复用 diff/计划同一 parser/renderer/styles;用户消息纯文本 | 150 | Changes/Markdown/*;Views/ChatMarkdownView | Vi/ChatMarkdownViewTests::Headings_lists_tables_and_quotes_render_with_their_markdown_classes | PASS |
| F-APP-B-91 | 增量状态机:已定稿前缀+未定稿区;边界=围栏外最后空行;可延长块由边界后第一行决定;松列表不切 | 150 | Changes/Markdown/StreamingMarkdown.cs | Ch/StreamingMarkdownTests::A_list_with_blank_lines_between_its_items_stays_one_list | PASS |
| F-APP-B-92 | 同段落/标题只换 Inlines、同围栏只换 Text;列表表格就地生长(已结束行保留控件);类型变才重建该块及后缀 | 150 | Views/ChatMarkdownView.axaml.cs | Vi/ChatMarkdownViewTests::A_streaming_table_grows_in_place_and_keeps_the_rows_of_ended_lines | PASS |
| F-APP-B-93 | 行尾半代理对先不显示 | 150 | StreamingMarkdown | Ch/StreamingMarkdownTests::A_surrogate_pair_split_across_chunks_never_shows_half_a_character | PASS |
| F-APP-B-94 | >4096 字段落按行收口无块距;Complete 全文重解析逐块比 (Kind,StartLine,EndLine) 不一致整体替换一次;分片传 top:false;最终恒等于一次全文解析 | 150 | StreamingMarkdown | Ch/StreamingMarkdownTests::A_completed_document_equals_one_parse_of_the_whole_text_for_any_chunking;A_rule_inside_a_document_is_not_read_as_front_matter | PASS |
| F-APP-B-95 | MarkdownCodeBlock=Border+ICopyableText 共享 md-code;语言标签空则隐;复制当前文本;Update 就地换;仅挂可视树时订阅 LanguageChanged;三处共用 | 151 | Changes/Markdown/MarkdownCodeBlock.cs:18,100 | Vi/MarkdownCodeBlockTests::The_copy_button_follows_a_language_switch_while_shown_and_stops_listening_once_detached | PASS |
| F-APP-B-96 | 每段回答各有复制按钮(整条原文);工具/提示行接续先封口 | 152 | Transcript.cs:288-296(SealText) | V/TurnTextViewModelTests::A_tool_row_seals_the_open_text_block_and_a_new_text_block_starts_after_it | PASS |
| F-APP-B-97 | 运行/排队子Agent各一标签;底部唯一状态栏(状态/任务/模型/用量/耗时/停止)无输入框;标签携带所属会话不跨会话 | 153 | Views/SubAgentView.axaml:23-47 | W/MainWindowWorkbenchIntegrationTests::Switching_to_another_session_drops_the_previous_sessions_sub_agent_tab | PASS |
| F-APP-B-98 | 已结束按序收进标题栏末端 已结束(n) 20px扁平+Flyout;选中经 TranscriptReplay 只读重放 | 153 | Views/FinishedSubAgentsHeaderView;MainWindowViewModel.cs:670 | V/FinishedSubAgentReplayTests | PASS |
| F-APP-B-99 | 已结束列表算内容(KeepsVisibleWhenEmpty);两者皆无整块移除,再现按原比例放回 | 153 | Workbench/WorkbenchModels.cs:133;WorkbenchLayout.cs:1395;MainWindowViewModel.cs:2336 | W/WorkbenchControlsTests::Empty_bottom_group_gives_its_space_back_to_main | PASS |
| F-APP-B-100 | 收起/展开开关↔IsBottomMinimized 双向同步;启动先快照后会话选择;收起后本会话不自动弹出 | 153 | MainWindowViewModel.cs:2156-2175,2351 | W/MainWindowWorkbenchIntegrationTests::Minimized_bottom_group_state_follows_the_active_session | PASS |
| F-APP-B-101 | 只有一个写入口:展开写会话标志经 SyncSubAgentRegion;树行/新终端调 RevealBottomGroup;TerminalWorkbench 拿回调;标题栏还原经 OnWorkbenchPropertyChanged 回报 | 153 | MainWindowViewModel.cs:293,2127-2131;WorkbenchLayout.cs:1604-1608 | 无(直接) | PASS* |
| F-APP-B-102 | 底部组标题栏同序:已结束(n)、+、缩小/还原、浮出;无图钉不自动隐藏;浮出按钮仅组活无标签时消失 | 153 | MainWindowViewModel.cs:316-317 | W/WorkbenchControlsTests::The_bottom_group_title_bar_has_no_pin | PASS |
| F-APP-B-103 | 入口=资源菜单第五行 运行中的资源(n),绑 OpenInspectorCommand;openInspector 为 null 时该行隐藏 | 155 | SessionView.axaml:512-519;SessionInstrumentsViewModel.cs:101 | V/SessionInstrumentsInspectorTests | PASS |
| F-APP-B-104 | 宿主=窗口覆盖层槽 Inspector;开建 VM+自有 DispatcherTimer;关(按钮/Esc/遮罩/拆除)即 dispose,不持定时器不订阅 Loc | 155 | MainWindow.axaml:196;ViewModels/ResourceInspectorViewModel.cs:346 | F/ResourceInspectorHeadlessTests | PASS |
| F-APP-B-105 | 列所有打开会话按组(表头标题/数量/当前会话标记),活动组最前;换会话只重排不关面板;空闲也挂监听;会话 Dispose 发事件清监听;面板按代号退订删行;Dispose 退订全部;Interlocked 合并 Background 刷新、缓冲复用 | 155 | ResourceInspectorViewModel.cs:248-360 | V/ResourceInspectorViewModelTests | PASS |
| F-APP-B-106 | AgentToolProgress 不派发;ConcurrentDictionary<(agent,call),LiveToolVM> 快照线程一次查表+volatile 写;开始/结束经 coalescer,AddTool 返回行 | 155 | ViewModels/SessionWorkspace.Tools.cs:73-79;Transcript.Tools.cs | V/RunningToolRegistryTests;V/LiveToolViewModelTests | PASS |
| F-APP-B-107 | 拉模型:条目缓存上次快照引用+行数上限,Tick 真变才裁(TextTail.Keep);会话时钟1s;面板定时器按 InspectorRefreshMilliseconds 只刷尾部;关面板成本0 | 155 | Transcript.Tools.cs:331-367;ResourceInspectorViewModel.cs:386,499 | V/LiveToolViewModelTests | PASS |
| F-APP-B-108 | 取消调用无 finished:IsRunning=false 与 Dispose 清表;Finish 隐尾部与停止;子Agent由 FinishToolsOf 清 | 155 | SessionWorkspace.Tools.cs:101,150 | V/RunningToolRegistryTests | PASS |
| F-APP-B-109 | 停止粒度:工具只停该条 CancelToolCall→Core;接受显正在停止隐按钮;false 保持原样;只读无停止按钮;子Agent用 SubAgentControl.Stop | 155 | Transcript.Tools.cs:299-320 | V/LiveToolViewModelTests | PASS |
| F-APP-B-110 | inspector 分区(InspectorComposition 注册,不改 AppComposition/App.axaml):行数20–1000默认200、间隔250–10000默认1000;行数每 tick 现读;间隔改立即重建定时器 | 155 | Settings/ResourceInspectorSettingsViewModel.cs:30,47;Composition/InspectorComposition.cs:23;ResourceInspectorViewModel.cs:490 | F/ResourceInspectorSettingsTests | PASS |
| F-APP-B-111 | 根 Agent 只在回合运行期登记(防幽灵行);子Agent不受限;回合边界只清根+不在活动名单者 | 155 | SessionWorkspace.Tools.cs:97-101 | V/RunningToolRegistryTests | PASS |
| F-APP-B-112 | 菜单行与面板标题数量同口径(运行调用+活动子Agent含排队);芯片徽标仍只数运行槽;排队 CanStop=false;统一列表按开始时间插入(排队 MinValue 排前);行结束前不重建;空态一行文案 | 155 | SessionWorkspace.cs:2112;ResourceInspectorViewModel.cs:369 | V/SessionEventRoutingTests::The_strip_badge_counts_only_sub_agents_that_hold_a_run_slot;V/ResourceInspectorViewModelTests | PASS |
| F-APP-B-113 | LayoutSnapshot v5:区域顺序/宽度/自动隐藏/底部高度与 bottomMinimized/panel 比例/预览开关宽/浮动;belowHeights 可选追加不升版本 | 160 | Workbench/LayoutSnapshot.cs:14-75 | W/LayoutSnapshotTests::Current_layout_version_is_5 | PASS |
| F-APP-B-114 | v4→v5 读 bottomCollapsed 为 bottomMinimized 停写旧字段;v3 另重置 projectSplit;未知版本回默认 | 160 | Workbench/LayoutStateLoader.cs:30-64 | W/LayoutStateLoaderTests::ReadLayout_migrates_a_v3_snapshot_by_resetting_the_left_column_split;ReadLayout_migrates_a_v4_snapshot_and_keeps_the_left_column_split | PASS |
| F-APP-B-115 | OpenTabsSnapshot v2 存 open-tabs(含预览路径) | 161 | LayoutSnapshot.cs:115-150 | W/LayoutSnapshotTests::Open_tabs_round_trip_keeps_version_2 | PASS |
| F-APP-B-116 | 区域/panel 分隔条松手各触发一次 LayoutChanged(EndPanelSplitterDrag)+800ms 防抖写快照,末次夹紧值入快照 | 162 | WorkbenchLayout.cs:407;MainWindowViewModel.cs:151,349,908 | W/MainWindowWorkbenchIntegrationTests::Adjusted_region_panel_and_preview_widths_are_saved_and_restored_after_a_restart | PASS |
| F-APP-B-117 | Version 缺省0序列化才写当前;版本不符/缺字段/解析失败/存储异常回默认;取消照常抛 | 164 | LayoutSnapshot.cs:24-25;LayoutStateLoader.cs:80-110 | W/LayoutStateLoaderTests::ReadLayout_throws_OperationCanceledException_for_an_already_cancelled_token;ReadLayout_returns_null_when_the_json_has_no_version_field | PASS |
| F-APP-B-118 | 未知 id 丢弃;缺失项保持默认位置 | 165 | WorkbenchLayout.cs:1077-1104 | W/LayoutSnapshotTests::Unknown_floating_id_is_ignored_on_restore | PASS |
| F-APP-B-119 | 浮动窗口在其选项卡存在后才应用;坐标全屏幕外移回主窗口屏幕 | 166 | Workbench/Controls/FloatingPanelWindow.cs:451-486 | W/FloatingWindowPlacementTests::A_window_whose_rectangle_misses_every_screen_is_centred_on_the_owner_screen;W/MainWindowWorkbenchIntegrationTests::Restore_applies_the_layout_then_reopens_the_tabs_then_floats_the_restored_tab | PASS |
| F-APP-B-120 | 检视区宽度存不含预览列;恢复重开预览只再借一次 PreviewWidth | 167 | WorkbenchLayout.cs(Capture/Apply) | W/LayoutSnapshotTests::Reopening_preview_after_restore_does_not_borrow_its_width_twice | PASS |
| F-APP-B-121 | 未放置浮动不存坐标;恢复居中于主窗口 | 168 | LayoutSnapshot.cs:92-97;FloatingPanelWindow.cs:55 | W/LayoutSnapshotTests::Unplaced_floating_window_is_saved_without_coordinates;W/FloatingWindowPlacementTests::A_window_that_was_never_placed_is_centred_over_the_owner | PASS |
| F-APP-B-122 | 切换选项卡触发保存(预览/工具组活动 tab 持久化);panel 无放大状态可存 | 170 | WorkbenchLayout.cs(ActiveTab→OnLayoutChanged) | W/WorkbenchControlsTests::Panel_title_bars_have_no_maximize_button | PASS |
| F-APP-B-123 | 主题亮/暗调色板数值(背景/面板/边框/文字/强调) | 172 | Resources/Tokens.axaml:6-15,110-230(实装跟随 L293,与 L172 数值不符) | 无 | SPEC_GAP |
| F-APP-B-124 | RequestedThemeVariant=Default 随系统;映射 Fluent 系统色 | 172 | App.axaml:20;Tokens.axaml ThemeDictionaries(System*) | Ap/AppearanceHeadlessTests | PASS |
| F-APP-B-125 | 区域/panel/堆叠空隙均 1px 边框令牌;热区≥6px 骑缝各溢2.5px 不吃布局 | 172 | Workbench/WorkbenchTypes.cs:93-99;Controls/WorkbenchSplitters.cs:11 | W/WorkbenchControlsTests::Region_splitter_hit_area_is_six_pixels_wide;Regions_gaps_and_edge_strips_fill_the_window_width_so_the_splitter_hit_areas_take_no_layout_space | PASS |
| F-APP-B-126 | 同区堆叠空隙画 BorderBrush 线;整像素分配(Floor 余数给最后一块) | 172 | RegionPresenter.cs:300-308 | W/WorkbenchControlsTests::A_stacked_region_draws_a_separator_above_the_lower_panel_title_bar | PASS |
| F-APP-B-127 | 主按钮单独 PrimaryButtonBackgroundBrush(#2F7449亮/#5FBF86暗)达 AA;强调令牌不变 | 172 | Tokens.axaml:139,218 | 无(对比度) | PASS* |
| F-APP-B-128 | 表单几何一档 Fluent 键(圆角3/按钮5/高28/padding 等);Controls.axaml 无 TextBox/ComboBox/CheckBox 类型选择器;ButtonPadding 与描边刷留给 Fluent | 172 | Tokens.axaml:60-80;Resources/Controls.axaml | W/FormControlGeometryTests::Fluent_geometry_keys_carry_one_tier_for_every_built_in_control | PASS |
| F-APP-B-129 | 按钮语义类 primary/ghost/icon/tab/link 独占配色;CornerRadius 绑 ButtonCornerRadius;分段壳绑 ControlCornerRadius;浮层绑 OverlayCornerRadius;RadiusSm/Md/Lg 留表面 | 172 | Controls.axaml:63,78 等 | W/FormControlGeometryTests::Every_text_input_and_button_in_a_form_lays_out_on_the_same_row_height | PASS |
| F-APP-B-130 | 工作台标签保持自绘 TabHeaderView,不迁 TabItem | 172 | Workbench/Controls/TabHeaderView.cs:197 | 无 | PASS* |
| F-APP-B-131 | 界面/代码字体族与字号分开改、即时生效、重启保持(9–28 步进1) | 174 | AppPreferences.cs:233-239 | Ap/AppearanceSettingsViewModelTests | PASS |
| F-APP-B-132 | 字体只经资源键;FontScale 比例=字号/13 AwayFromZero,默认 11/12/13/14/18/22;Base 正文 Md 强调;全 DynamicResource;mono 类用 FontMono+FontSizeCode | 175 | Appearance/FontTokens.cs:31-60;Tokens.axaml:17-24 | Ap/FontScaleTests | PASS |
| F-APP-B-133 | 默认界面族 Segoe UI 回退链;Inter 注册不进默认链,fonts:Inter#Inter 可手输 | 175 | Tokens.axaml:250-252;Program.cs:23(WithInterFont) | 无(直接) | PASS* |
| F-APP-B-134 | 应用器:UI 线程合并自有子字典整体替换;仅四属性值变才换;覆盖 Fluent 两键;族解析失败保留上一有效值/回退默认链,同一非法文本只 Trace 一次不抛 | 176 | Appearance/AppearanceApplier.cs:55-110 | Ap/AppearanceApplierTests | PASS |
| F-APP-B-135 | 时序:组合根创建按默认应用一次(仅 Application 存在且当前线程才建应用器);加载后 UI 线程 SyncAll+重应用;拆除先释放应用器与分区 | 177 | AppComposition.cs:964-1013,862 | Ap/AppearanceHeadlessTests | PASS |
| F-APP-B-136 | SystemFontCatalog:首次池上枚举、去重序数忽略大小写排序、同 Task 缓存;失败空列表+Trace | 178 | Appearance/SystemFontCatalog.cs | Ap/SystemFontCatalogTests | PASS |
| F-APP-B-137 | appearance 分区两组:可编辑 AutoCompleteBox、整数 NumericUpDown、组内预览、只重置本组;族名选中/回车/失焦提交;字号即时;外部改动回显;列表只在 UI 线程交编辑框 | 179 | Appearance/AppearanceSettingsViewModel.cs | Ap/AppearanceSettingsViewModelTests | PASS |
| F-APP-B-138 | TerminalControl 自读代码字体偏好 | 180 | Terminal/TerminalControl.cs | Te/TerminalThemeFontTests | PASS |
| F-APP-B-139 | 按区域分表+类型化访问器;同句两区各注册各键不跨表;英文复数 .one/.other 由 LocText.Plural 选键 | 184-185 | Localization/Strings.*.cs;LocText.cs | Lo/StringTableTests::Plural_keys_come_in_one_other_pairs | PASS |
| F-APP-B-140 | 数字千分位一律逗号、Id 不分组、时间 HH:mm:ss、耗时双语格式;数字参数先格式化传入 | 186 | Localization/TextFormat.cs;Loc.cs:88-100 | Lo/FormattingTests | PASS |
| F-APP-B-141 | 刷新规则:XAML 索引器绑定;VM getter 现算;仅带 Dispose 的所有者订阅并向下 RefreshText;存后显示的文本用 LocText;代码控件挂/离订阅退订;菜单打开取文本;固定 panel/tab 记标题键 | 187 | Loc 绑定模式;MarkdownCodeBlock.cs:100;PanelChrome 标题键 | L/LiveSwitchTests::Empty_session_state_text_follows_the_language_live;Lo/StringTableTests::Every_axaml_loc_binding_refers_to_a_registered_key | PASS |
| F-APP-B-142 | 不翻译清单:模型输出/用户输入/文件内容路径/插件文案/会话标题(创建语言存库)/开发者异常英文 | 188 | 各实现(异常文本英文,如 Loc.cs:127) | 无(直接) | PASS* |
| F-APP-B-143 | 防回归:表外无 CJK(白名单空)、中英齐全、英文无 CJK、占位符一致、XAML/C# 键已注册、格式样例、检测分支矩阵 | 189 | — | Lo/NoHardcodedChineseTests::No_source_file_outside_the_string_tables_holds_CJK_characters;Lo/StringTableTests::Both_languages_use_the_same_placeholders | PASS |
| F-APP-B-144 | UI 自动化只认 AutomationId,不随语言变 | 191 | 各 axaml AutomationProperties.AutomationId 常量 | 各 headless 测试按 id 定位(如 W/WorkbenchControlsTests) | PASS |
| F-APP-B-145 | 唯一入口 NotifyAsync;开关关→什么都不做;IsBackground→SystemNotifier;否则窗口顶部居中 toast | 192 | Notifications/NotificationCenter.cs:120-145 | N/NotificationCenterTests::Switch_off_shows_nothing_and_calls_no_desktop_notifier;Background_goes_to_the_desktop_notifier_with_resolved_text | PASS |
| F-APP-B-146 | 禁自建 toast/横幅/弹窗/第三方库/绕过直调 ISystemNotifier;接线按生命周期(会话可空/服务注入/叶子读 Current) | 193 | Notifications/NotificationExtensions.cs;NotificationCenter.Current | N/NotificationWiringTests::The_fire_and_forget_helper_swallows_a_failing_notification | PASS |
| F-APP-B-147 | 仅四等级;等级同时决定颜色与停留 4/4/6/8s | 195-199 | NotificationCenter.cs:36-44,108-115;NotificationLevel.cs | N/NotificationCenterTests::DurationOf_matches_the_documented_stays | PASS |
| F-APP-B-148 | 两行 LocText 进 notifications.*;稳定去重 key;缺省 key=等级+标题+正文 | 200 | NotificationCenter.cs:148-152;Localization/Strings.Notifications.cs | N/NotificationCenterTests::DefaultKeyOf_covers_level_and_resolved_text | PASS |
| F-APP-B-149 | 新增接线须在 NotificationWiringTests 补用例钉住路径/等级/key | 200 | — | N/NotificationWiringTests 仅3方法(approval 等待+helper×2) | PARTIAL |
| F-APP-B-150 | Current 接缝同 ApprovalServices;组合根窗口前创建、拆除最后释放 | 201 | AppComposition.cs(Notifications 最后 dispose) | N/NotificationCenterTests::Dispose_is_idempotent_and_clears_current | PASS |
| F-APP-B-151 | toast 最新在上最多5;同 key 合并×N、重启计时、退出条目可召回 | 201 | NotificationCenter.cs:170-215 | N/NotificationCenterTests::Repeat_reclaims_an_item_that_was_already_leaving;Six_distinct_notifications_evict_the_oldest | PASS |
| F-APP-B-152 | 到时标 IsLeaving 淡出+下滑,ExitDelay180ms 后移除,邻居平移在窗口内完成 | 201 | NotificationCenter.cs:230-260;ToastStackPanel.cs | N/NotificationCenterTests::Stay_matches_level_and_removal_needs_one_exit_delay | PASS |
| F-APP-B-153 | 不用 RenderTransform 样式动画;样式只管透明度;ToastStackPanel 镜像附加属性动画→TranslateTransform.Y;新条目-6px 滑入、离开+6px 下滑 | 201 | Notifications/ToastStackPanel.cs:63-70,168-180 | N/ToastStackLayoutTests;N/NotificationOverlayTests::ToastStackPanel_arranges_a_plain_child_where_the_pure_function_predicts | PASS |
| F-APP-B-154 | 计时用 TimeProvider ITimer(测试假时钟);集合与计时只 UI 线程;NotifyAsync 任意线程经 post 缝合,无 Avalonia 退内联 | 201 | NotificationCenter.cs:60-80 | N/NotificationCenterTests(假时钟+池线程用例) | PASS |
| F-APP-B-155 | 视图在主窗工作区行顶部居中、IsHitTestVisible=False;盯主窗 WindowState 与所有 Forge 窗口 IsActive 写 IsBackground | 202 | Views/NotificationOverlay.axaml.cs:223-229;MainWindow.axaml:203 | N/NotificationOverlayTests::Overlay_shows_when_an_item_appears_and_hides_when_it_is_gone | PASS |
| F-APP-B-156 | 开关偏好 notifications 默认开;旧 gitNotifications 只读兼容不再写回;分区 order550 单复选框 | 202 | AppPreferences.cs:632,960-962;SettingsSection.cs:171;NotificationsSettingsViewModel.cs | G/GitSettingsSectionGitTests::A_file_carrying_only_the_legacy_gitNotifications_key_loads_and_rewrites_as_notifications | PASS |
| F-APP-B-157 | Dispose:Interlocked 幂等、就地停计时器字典、仅 UI 线程清集合否则 post、disposed 首闸;三用例钉住 | 203 | NotificationCenter.cs:296-330 | N/NotificationCenterTests::Disposing_from_a_pool_thread_leaves_the_toast_list_to_the_ui_thread;A_queued_toast_that_runs_after_disposal_is_dropped;Nothing_is_shown_or_sent_after_disposal | PASS |
| F-APP-B-158 | 权限引导四态;宽面 INotificationPermissionSection 不加成员到冻结接口;初值按平台;SendTest 真发一条以 TryNotifyAsync 结果落 Granted/Denied(volatile 进程内记住) | 205-206 | Notifications/NotificationPermissionGuide.cs:60-170 | N/NotificationPermissionGuideTests | PASS |
| F-APP-B-159 | OpenSettings 用平台页面 URI 常量;Linux/NotApplicable 直接 false 不调 opener;默认 shell opener;失败或>5s 只 trace 返 false,除取消不抛 | 206 | NotificationPermissionGuide.cs:175-215 | N/NotificationPermissionGuideTests | PASS |
| F-APP-B-160 | ISystemNotifier 增 TryNotifyAsync;NotifyAsync 委托;center 不看结果;true 仅代表交给平台 | 207 | Notifications/SystemNotifier.cs:118-150 | N/SystemNotifierTests;N/NotificationCenterTests::A_desktop_notification_the_platform_dropped_leaves_the_center_unchanged | PASS |
| F-APP-B-161 | notification-permission 分区 order560:状态文案随语言重算、发送测试(运行禁用换文案回填)、打开系统设置(仅 CanOpenSettings)、reprompt 开关;键 notifications.permission.* | 208 | Notifications/NotificationPermissionSettingsViewModel.cs;SettingsSection.cs:197 | N/NotificationPermissionSettingsViewModelTests | PASS |
| F-APP-B-162 | 组合根持单例暴露 NotificationPermissionGuide;W9 只调冻结接口 | 208 | AppComposition(NotificationPermissionGuide 槽) | N/NotificationPermissionCompositionTests | PASS |
| F-APP-B-163 | 调用方清单(会话/Git/环境约20路径)各带稳定 key | 209 | SessionWorkspace.cs:1395-1445(turn-failed:/checkpoint:);ComposerViewModel.cs:1080;GitSyncViewModel | 仅 approval/附件路径有接线测试 | PARTIAL |
| F-APP-B-164 | 不接清单:生命周期对话框当场失败/压缩/插话排队回执/折叠高亮降级/语言模型切换/TODO步骤提交成功 | 210 | 各调用点(无 Notify) | 无 | PASS* |
| F-APP-B-165 | 提供商未配置/Host 未找到→明确 UI 状态,不抛不伪造模型调用 | 211 | SessionWorkspace.cs:185-190;ComposerViewModel.cs:1080 | 无(直接) | PASS* |
| F-APP-B-166 | 审批降级:只读自动放行、todo 显式放行、其余 Ask、拒绝卡显已拒绝(细节归审批路) | 211 | Approval/ApprovalRules | Approval/ApprovalRulesTests | PASS |
| F-APP-B-167 | 项目/文件/规格/已改动树均扁平 RangeObservableCollection+虚拟化 ListBox;展开折叠区间插删;展开态跨刷新按路径/id 保留 | 213 | Collections.cs:9;ProjectTree/Panels 刷新 | FilesPanelTests::Expanded_folders_survive_a_refresh | PASS |
| F-APP-B-168 | 文件/规格树刷新整表 ReplaceAll 滚动回顶(规格明示未改 diff) | 213 | Panels.cs:530(BuildTreeAsync) | FilesPanelTests::Stale_refresh_never_overwrites_a_newer_result | PASS |
| F-APP-B-169 | 行模型负责缩进档与引导线,视图不递归;后台计划只吃 UI 快照的不可变数据 | 213 | TreeIcons.cs:153;ProjectTree.cs(TreeNodePlan record) | TreeVisualTests::Guide_levels_track_expanded_ancestors_and_collapse_removes_the_level | PASS |
| F-APP-B-170 | 图标单一来源 Icons.axaml(FIS 24)、唯一渲染 FluentIcon(PathIcon 零逻辑子类)、Viewbox 固定24网格;无自绘图标控件/视图手写 d;不覆盖内置 PathIcon 主题 | 215,294 | Resources/Icons.axaml:147-158;Workbench/Controls/FluentIcon.cs:16;Views 无 Data="M" | IconGeometryTests | PASS |
| F-APP-B-171 | 尺寸两档:panel 标题栏/体内工具14px(重点工具按 IconSize 放大)、行内微标记12px;窗口行 设置/终端15、最小化/最大化13、关闭12 | 294(216 过时) | Workbench/Controls/WorkbenchButtons.cs:19;MainWindow.axaml:150-186 | W/WorkbenchIconSizeTests::Every_panel_title_bar_icon_is_the_same_size_apart_from_the_accent_tools | PASS |
| F-APP-B-172 | 行高28;项目/会话行图标12、Agent行14;截断显全文 tooltip;活动会话行浅强调底;气泡单 Regular 靠色;标签条激活换 Filled;chat_sparkle 共享、document 预览标签、comment_add、文件夹开/闭、规格节点图标 | 217 | Controls.axaml:500-505;Views/ProjectView.axaml;TreeIcons.cs | TreeVisualTests::Folder_expand_swaps_to_open_folder_geometry;W/WorkbenchTabIconTests | PASS |
| F-APP-B-173 | 箭头 chevron_right 填充12px 在10px 槽左右各溢1px;折叠向右展开 rotate90;项目树箭头列再让6px | 219 | Controls.axaml:521-529;ProjectView.axaml:62-71 | TreeVisualTests::Child_chevron_slot_aligns_with_parent_icon_left | PASS |
| F-APP-B-174 | 缩进 Step=16、RowInset=6、无箭头 Agent 行 IconIndent=22;文件树同 Step(规格分量8+8与代码10+6不符,见 Q8) | 220 | TreeIcons.cs:155-172 | TreeVisualTests | PASS |
| F-APP-B-175 | 文件/规格树展开文件夹下画1px竖线至最后子孙;项目树不画(Plain);行模型祖先展开位掩码 ForMask 缓存数组;折叠展开递归刷新掩码 | 221 | TreeIcons.cs:180-230 | TreeVisualTests::Spec_tree_uses_file_tree_guides_and_icons | PASS |
| F-APP-B-176 | 悬停/选中底6px圆角,自箭头处铺到右缘不盖缩进区 | 222 | Controls.axaml:500-514 | TreeVisualTests::Hover_surface_left_edge_moves_right_with_indent | PASS |
| F-APP-B-177 | 文件名比例字体正文色;规格树标题链接样式点击打开、状态灰色小字跟随 | 223 | Views/SpecTreeToolView.axaml | TreeVisualTests::Spec_tree_uses_file_tree_guides_and_icons | PASS |
| F-APP-B-178 | 会话=主Agent 一行;子Agent直接接会话行下(PlanSessionAgents 提升);箭头只展开标题打开;孙Agent按父AgentId嵌套 | 225 | ProjectTree.cs:1574 | G/ProjectTreeWorktreeSessionTests | PASS |
| F-APP-B-179 | 点子Agent行=会话前置(必要时加载)+展开底部组+选标签;已结束走 OpenFinished 只读回放 | 225 | ProjectTree.cs:868,1835;MainWindowViewModel.cs:658,670 | V/FinishedSubAgentReplayTests | PASS |
| F-APP-B-180 | 底部组选中子Agent标签→树行 IsActiveAgent,同 active-row 浅强调底 | 225 | ProjectTree.cs:1057,1754 | 无(直接) | PASS* |
| F-APP-B-181 | Kind 选线框图标(Scout/Worker/Plan/Reviewer/未知回落);行首只图标箭头跟标题后;状态=圆点变体仅正常完成绿对勾;原文案退 tooltip;不画引导线 | 225 | ProjectView.axaml:148-186 | TreeVisualTests::Agent_rows_put_chevron_after_title_and_colour_and_animate_by_state | PASS |
| F-APP-B-182 | 图标按 AgentState 上色;运行呼吸1↔0.45 1.6s;TreeMotion.ReducedMotion 开关未接系统(规格明示) | 225 | Controls.axaml:606-615;TreeIcons.cs:145 | TreeVisualTests(同上) | PASS |
| F-APP-B-183 | 子Agent行双来源:PlanStoredAgents 查全部会话按 parent_session_id 推;加载后实时名单替换;RefreshAgentTrees 不清无实时名单的子行;InitializeAsync 末补刷新;LoadHistoryAsync 发 SubAgentsChanged 切换 | 225 | ProjectTree.cs:1000-1020,1520-1559 | 无(直接) | PASS* |
| F-APP-B-184 | 重启层级递归 parent_session_id 还原;状态从最后存储消息推断(Cancelled=停止/错误悬空=失败/最终文本=完成);推不出显中性已结束,未知不当完成 | 225 | ProjectTree.cs:1539-1559 | 无(直接) | PASS* |
| F-APP-B-185 | 已改动徽标同改动面板语义(HEAD整树/含未跟踪/按会话目录裁剪/scratch有数);二进制只状态字母不显+0-0;watcher700ms;状态+大小+mtime 指纹缓存无变化不重算 | 227 | Panels.cs:722,790 | ChangedTreeTests;FilesPanelTests | PASS |
| F-APP-B-186 | 会话标签按 id 唯一:重开复用重绑;归档先关标签再释放运行时与 VM | 229 | MainWindowViewModel.cs:1995-2000,2078-2083 | W/SessionLifecycleShellTests | PASS |
| F-APP-B-187 | 最小宽 项目/检视200主360;首次默认 260/275/预览420;之后以快照为准 | 231 | WorkbenchTypes.cs:87-90,112 | W/RegionWidthSolverTests;W/LayoutSnapshotTests | PASS |
| F-APP-B-188 | 拖动:另一侧紧邻区吸收全部增量;被挤侧主先缩到最小其余由近到远;越过最小一半(0.5)才自动隐藏;主永不隐藏 | 232-234 | Workbench/RegionWidthSolver.cs:27-110 | W/RegionWidthSolverTests::Drag_never_collapses_main;Drag_left_on_left_splitter_collapses_project_when_pushed_past_overshoot_threshold | PASS |
| F-APP-B-189 | 空间够但边栏总宽过大:先按序缩到最小再隐非 protected;主最后≥360;protected 只缩不藏 | 235 | RegionWidthSolver.cs:130-240 | W/RegionWidthSolverTests::Fit_protected_side_region_shrinks_but_is_not_hidden;Fit_main_below_minimum_shrinks_sides_to_minimum_then_hides_inspector | PASS |
| F-APP-B-190 | 窗口变窄先缩主再检视再项目仍不够依次隐藏;变宽增量给主 | 236 | RegionWidthSolver.Fit | W/RegionWidthSolverTests::Fit_narrowing_shrinks_main_before_any_side_region;Fit_widening_gives_surplus_to_main_and_leaves_sides_alone | PASS |
| F-APP-B-191 | 纯函数计算、拖动路径零分配(含挂 UI 的整条同步事件链)、松手才写快照;不用 GridSplitter | 237 | RegionWidthSolver;WorkbenchSplitters;ObservableObject 缓存 args | W/RegionWidthSolverTests::Drag_allocates_nothing_per_call;W/WorkbenchControlsLifecycleTests::Splitter_drag_synchronous_chain_allocates_zero_after_warmup | PASS |
| F-APP-B-192 | ObservableObject 按属性名缓存 PropertyChangedEventArgs;控件只对影响自身的属性名刷新 | 238 | ViewModels/ObservableObject.cs | W/WorkbenchControlsLifecycleTests(同上) | PASS |
| F-APP-B-193 | 标题栏28px(TitleBarHeight),浮出窗口标题条同读 | 240 | Workbench/Controls/PanelChrome.cs:26;FloatingPanelWindow.cs:141 | W/WorkbenchControlsTests::A_panel_title_bar_is_as_high_as_the_title_bar_constant | PASS |
| F-APP-B-194 | 选项卡只在本组内拖动排序不跨组 | 240 | Workbench/Controls/PanelTabStrip.cs | W/WorkbenchControlsTests::Dragging_a_tab_along_its_strip_reorders_the_group | PASS |
| F-APP-B-195 | StackPanel 方向组件局部;无 Window/App 范围方向类型样式 | 240 | MainWindow.axaml:21-25(仅 TextBlock 样式);各容器显式 Horizontal | 无 | PASS* |
| F-APP-B-196 | 标题两级:PART_Title Md+Bold、PART_Label Base+Normal;字重只400/700;浮出标题自建同取 Md+Bold;激活/未激活只靠颜色 | 240 | Resources/Workbench.axaml:255-260,138-140;FloatingPanelWindow.cs:169-174 | W/WorkbenchControlsTests::Panel_titles_are_bold_at_the_emphasis_size;Tab_labels_are_regular_at_the_body_size;A_floating_panel_title_is_bold_at_the_emphasis_size | PASS |
| F-APP-B-197 | 图钉一个按钮一个名字:tooltip 恒自动隐藏;实心=已停靠空心=已隐藏;作用于整区;底部组无图钉,其收起=缩小28px标题栏,三驱动同一状态 | 241 | PanelChrome.cs:64-74;WorkbenchLayout.cs:695-706 | W/WorkbenchControlsTests::The_pin_shows_its_state_solid_when_docked_and_outline_when_auto_hidden;The_bottom_group_title_bar_has_no_pin | PASS |
| F-APP-B-198 | 缩小/还原按能力显示(IconMinimize/arrow_expand 与窗口 square_multiple 分开);缩小只留28px标题栏正文隐而不毁;同伴回收高度;最后展开同伴消失自动还原;不持久化;无放大能力 | 242-243 | PanelChrome.cs:107-134;Workbench.axaml:331-333 | W/WorkbenchControlsTests::Panel_title_bars_have_no_maximize_button | PASS |
| F-APP-B-199 | 新窗口/停靠只给可浮出对象 | 244 | WorkbenchLayout.Float | W/WorkbenchControlsTests::Float_button_is_hidden_on_the_project_panel;Float_button_is_shown_on_the_files_panel | PASS |
| F-APP-B-200 | 已结束(n):20px扁平按钮+小chevron+Flyout;空列表禁用 | 245 | Views/FinishedSubAgentsHeaderView.axaml | W/WorkbenchControlsTests::Finished_subagents_menu_is_at_most_28_pixels_high_with_a_finished_sub_agent | PASS |
| F-APP-B-201 | 除预览组 X 外不放菜单/关闭;X 倒序关全部标签→组不再 docked-visible、借宽归还 Main;关闭经终端与文件守卫串行,取消即停 | 246 | PanelChrome PART_Close;MainWindowViewModel(ContentCloseGuard) | W/WorkbenchControlsTests::Close_button_closes_the_preview_group_and_returns_its_width_to_main | PASS |
| F-APP-B-202 | 选项卡自带关闭;右键菜单每 tab 一份实例 | 246 | PanelTabStrip.cs | W/WorkbenchControlsTests::Each_tab_header_has_its_own_context_menu_instance | PASS |
| F-APP-B-203 | 标签25px贴底;激活轮廓 Render 自绘:填充环+同心 R+1 内弧+交点外短直段+切线匹配贝塞尔;外翻画在控件矩形外(ClipToBounds=False);PART_Tabs 禁裁切留5px;ScrollViewer 负责视口裁切;模板 Border 只留1px占位 | 247 | TabHeaderView.cs:197-310;Workbench.axaml:108,214 | 无(几何数学;外观需肉眼) | PASS* |
| F-APP-B-204 | focus-visible 只换环色为强调;未激活持焦只画环不画底 | 247 | Workbench.axaml:178-183;TabHeaderView.cs:200-201 | 无 | PASS* |
| F-APP-B-205 | 可关闭标签恒留16px关闭槽+6px间距;×仅激活/悬停可见可点;会话标签14px气泡(未激活线框激活同路径填充,色随文字);IsIconVisible=false 不预留;不可关闭标签对称12,0,12,0 文字居中 | 248 | Workbench.axaml:146-167 | W/WorkbenchControlsTests::A_tool_tab_without_a_close_box_centres_its_label;W/WorkbenchTabIconTests | PASS |
| F-APP-B-206 | 溢出…按钮 PART_Overflow 20px 独占 Auto 列 IconEllipsisSolid;只列视口外标签;点击激活并滚入;无溢出宽0不振荡;layout 后用 Offset/Viewport/Bounds 判定零分配只在翻转写 HasOverflow;滚轮横向保留 | 248 | PanelTabStrip.cs:397-440;Workbench.axaml:218-231 | W/PanelTabStripOverflowTests | PASS |
| F-APP-B-207 | 仅自动隐藏边栏在窗口边缘显32px停靠条(DockStripBackgroundBrush+内侧1px);停靠不显不占宽,只按边缘计入 solver;同侧两边栏并排 | 289 | Workbench/Controls/AutoHideStrip.cs:50-53;WorkbenchHost.cs:290-298 | W/WorkbenchControlsTests::Two_auto_hidden_regions_on_the_same_edge_sit_side_by_side;Docked_region_has_no_edge_strip_until_it_is_auto_hidden | PASS |
| F-APP-B-208 | 每 panel 一竖排标签旋转90°从上往下;外侧3px指示条:抽屉开 Idle 色/关 Accent;点标签开关抽屉 | 289 | AutoHideStrip.cs:120-150 | W/WorkbenchControlsTests::Strip_indicator_uses_accent_when_closed_and_idle_color_when_open;Strip_button_opens_the_drawer_over_main | PASS |
| F-APP-B-209 | 抽屉贴停靠条内侧弹出覆盖主区;点外部/Esc 收起;底部组无单独按钮条 | 289 | WorkbenchHost.cs:401-414,783-797 | W/WorkbenchControlsTests::Escape_closes_the_open_drawer;Press_outside_the_drawer_closes_it;Open_drawer_overlays_main_without_resizing_it | PASS |
| F-APP-B-210 | 按住标题栏空白横拖出三落点,放下两区互换宽度随区;自动隐藏区不能拖;按钮条位置/抽屉方向/预览靠主区侧由角色+顺序推导 | 291 | WorkbenchHost.cs:70,347-356;Controls/SwapDropOverlay.cs;WorkbenchLayout.cs:264 | W/WorkbenchControlsTests::Dragging_a_title_bar_across_another_region_swaps_the_two_regions;Auto_hidden_region_cannot_start_a_swap_from_its_drawer;W/WorkbenchLayoutRegionTests::EdgeOf_follows_the_region_after_a_swap | PASS |
| F-APP-B-211 | 大面积 VS Code 配色:亮主体白/侧栏#F8F8F8/分隔#D4D4D8/正文#3B3B3B;暗#1F1F1F/#181818/#3F3F46;主区 SurfaceBrush 边栏 SidebarBrush(:sidebar),边栏视图根透明 | 293 | Tokens.axaml:110-230;Workbench.axaml:328-330 | W/WorkbenchControlsTests::Window_header_uses_the_same_chrome_colour_as_a_panel_title_bar | PASS |
| F-APP-B-212 | 图标硬要求:只 FIS 24 内化用到的图形;仓不存 SVG 不引包;同义空/实=同图标 Regular/Filled 两 key;Viewbox 缩放网格非墨迹;刻意不覆盖内置 PathIcon 主题 | 294 | Resources/Icons.axaml;FluentIcon.cs:16 | IconGeometryTests | PASS |
| F-APP-B-213 | 专用图形键:arrow_autofit_width/document_bullet_list_clock/panel_bottom(filled,20网格放大)/window_new_rotate_180/arrow_expand/square_multiple;运行期生成几何豁免;许可证见 THIRD_PARTY_NOTICES | 294 | Icons.axaml:95-96,112-115;THIRD_PARTY_NOTICES.md | W/WorkbenchTabIconTests | PASS |
| F-APP-B-214 | 边栏未自设换行 TextBlock 单行字符省略,截断自动挂全文 tooltip | 294 | Views/TrimmedTextToolTip.cs:20-40 | 无 | PASS* |
| F-APP-B-215 | IconButtonHoverBrush(#D4D4D8/#3F3F46)与 PressedBrush(#C4C4C9/#4A4A52);PanelIconButton/AutoHideButton/窗口标题行与对话框 ghost/icon/tab 共用,不借 BorderBrush;Fluent 内置边框不走这两令牌 | 295 | Tokens.axaml:133-135,212-214;Workbench.axaml:39-56 | W/WorkbenchControlsTests::Hovering_an_icon_button_paints_it_with_the_button_brush | PASS |
| F-APP-B-216 | 一种 chrome 底色:窗口顶行/各 panel 标题栏(含标签条)/停靠条/浮出自绘标题条统一 PanelTitleBarBrush(#EFEFEF/#101010);旧 SidebarTitleBarBrush 已删;激活标签取所在 panel 背景 | 298 | Tokens.axaml:125,206;Workbench.axaml:248;全仓无 SidebarTitleBarBrush | W/WorkbenchControlsTests::Window_header_uses_the_same_chrome_colour_as_a_panel_title_bar | PASS |
| F-APP-B-217 | 只允许浮出选项卡(任意组含 TODO/评审/规格)与文件 panel;其他 panel 不能浮出 | 311 | WorkbenchLayout.cs:912 | W/WorkbenchControlsTests::Float_button_is_shown_on_a_tab_group_with_an_active_tab | PASS |
| F-APP-B-218 | 文件 panel 浮出带预览组进同窗(仍左侧),回位一起回 | 312 | FloatingPanelWindow.cs:243-269 | W/WorkbenchControlsTests::Floating_the_files_panel_takes_the_preview_group_into_its_window | PASS |
| F-APP-B-219 | 文件 panel 给会话相对路径,打开前按活动会话工作目录解析绝对;open-tabs 存绝对;恢复不依赖当时活动会话 | 313 | MainWindowViewModel(打开/恢复路径解析) | W/MainWindowWorkbenchIntegrationTests::Restored_preview_file_shows_its_content | PASS |
| F-APP-B-220 | 一对象一 FloatingPanelWindow,同 VM 重套模板(滚动等临时态不保留);浮出 tab 留原集合只标记不显示;组活动 tab 永不浮动;停靠/关窗天然回原槽 | 314-315 | FloatingPanelWindow.cs:139-280;WorkbenchModels.cs:114-127 | W/WorkbenchControlsTests::Closing_a_floating_window_docks_its_tab_back_into_its_slot;Dock_button_in_a_floating_window_docks_its_tab_back | PASS |
| F-APP-B-221 | 浮动默认 900×640(文件320×420);文件浮窗标题栏带工具按钮;最小化期间不写回几何 | 316 | WorkbenchTypes.cs:123-126;FloatingPanelWindow.cs:497-500 | W/WorkbenchControlsTests::Floating_tab_window_opens_at_its_default_size;Floating_files_window_opens_at_its_default_size;W/WorkbenchControlsLifecycleTests::Minimized_floating_window_does_not_write_its_geometry;Floating_files_window_renders_the_files_open_changes_header_tool | PASS |
| F-APP-B-222 | 主窗确认真正关闭才置 IsShuttingDown(取消复位);之后关浮窗不回位,浮动项照常进快照 | 317 | MainWindow.axaml.cs:94-118;FloatingPanelWindow.cs:113-122 | W/WorkbenchControlsTests::Closing_the_window_during_shutdown_keeps_its_tab_floating | PASS |
| F-APP-B-223 | 浮窗与主窗同设 ExtendClientAreaToDecorationsHint+TitleBarHeightHint=-1;整体1px BorderBrush 边框 | 318-319 | FloatingPanelWindow.cs:41-43,149 | 无(直接) | PASS* |
| F-APP-B-224 | 浮窗标题条可拖窗/双击最大化还原;热区仅自己那条28px(文件窗在右);预览组标签条仍归标签手势;按钮/输入框上不拖;IsMaximized 沿用 FloatGeometry 持久化 | 320 | Workbench/Controls/WindowCaption.cs:40-73;FloatingPanelWindow.cs:224 | W/WindowCaptionTests::Only_the_title_bar_of_the_floating_files_panel_drags_its_window;A_press_on_the_floating_dock_button_keeps_its_clicks_and_starts_no_move | PASS |
| F-APP-B-225 | 主窗 BorderOnly+双 Extend 提示(去系统标题栏、客户区铺到顶边无白带);标题行40px 自绘 PanelTitleBarBrush;窗口 Background 仍 SurfaceBackgroundBrush | 322 | MainWindow.axaml:17-20,44-49 | MainWindowStartupTests;W/WorkbenchControlsTests::Window_header_uses_the_same_chrome_colour_as_a_panel_title_bar | PASS |
| F-APP-B-226 | 标题行左侧只有 Forge 图标(会话标题/分支不再绑定,VM 属性保留) | 322 | MainWindow.axaml:55-66 | W/WorkbenchControlsTests::Window_header_replaces_the_search_box_with_the_magnifier_button_and_the_host_state | PASS |
| F-APP-B-227 | automations-open 闪电+文字;徽标 automations-running 只计执行中+等待中,0 隐,不计已完成;点击开设置自动化分区 | 322 | MainWindow.axaml:70-88;MainWindowViewModel.Automations.cs:24-31 | 无(直接) | PASS* |
| F-APP-B-228 | 放大镜 shell-search:搜索还不是功能,tooltip 搜索(即将可用) | 322 | MainWindow.axaml:104-117 实绑 OpenPaletteCommand(已功能,见 L439) | Palette/PaletteHostTests | SPEC_GAP |
| F-APP-B-229 | ●扩展宿主:圆点复用 status-dot(运行绿/启动黄/失败红/余灰);tooltip=Status.HostTooltip(状态+pid/失败原因);文字只写扩展宿主 | 322 | MainWindow.axaml:127-139 | W/WorkbenchControlsTests::Window_header_replaces_the_search_box_with_the_magnifier_button_and_the_host_state | PASS |
| F-APP-B-230 | terminal-open=圆角方框 IconTerminalFrame+实心下半屏 IconTerminalScreen 只填不描边 | 322 | MainWindow.axaml:146-153 用单一 IconTerminal(=panel_bottom filled,合 L294);两键不存在 | 无 | SPEC_GAP |
| F-APP-B-231 | 右组:终端、设置、最小化、最大化、关闭;最大化图标随状态换;无底部应用状态栏 | 322 | MainWindow.axaml:146-186;MainWindow.axaml.cs:43-58 | 无(直接) | PASS* |
| F-APP-B-232 | 标题行可拖/双击最大化,与浮窗共用 WindowCaption 同一套 CaptionPress(按钮/输入框不拖、双击最大化、三连击按移动);ToggleMaximize 同路 | 322 | WindowCaption.cs:40-73;MainWindow.axaml.cs:143-151 | W/WindowCaptionTests::A_caption_press_classifies_by_button_click_count_and_interactive_source;The_main_window_title_row_hands_its_press_to_the_caption_rule | PASS |

## 非 PASS 项详情

### F-APP-B-18(PARTIAL)缺失 key 日志级别与规格不符
Loc.cs:171-181 `ReportMissing` 用 `AppLogs.Loc.ZLogWarning`,规格 L58 写“每个 key 只 Trace 一次”。行为(每 key 一次、返回 [key])正确且有测试(F/LocalizationTests::A_missing_key_is_reported_once),仅级别偏差。建议:规格改 Warning 或实现降 Trace(二选一,属文案/实现微修)。

### F-APP-B-23(PARTIAL)语言切换通知链:创建线程守卫未全覆盖
规格 L62 要求“所有 LanguageChanged 订阅者记下创建线程,收到别的线程的通知就忽略”。实测约 30 个订阅点中 23 个文件有 `CurrentManagedThreadId` 守卫(如 NotificationCenter.cs:283、MainWindowViewModel.cs:3043),但以下订阅者无守卫也未 post 缝合:LanguageSettingsViewModel.cs:29、Appearance/AppearanceSettingsViewModel.cs:48、Terminal/TerminalViewModel.cs:120、Terminal/TerminalSettingsViewModel.cs:19、Terminal/TerminalWorkbench.cs:56、Approval/ApprovalQueueViewModel.cs:24、Approval/ApprovalCardViewModel.cs:129、Approval/ExtensionsSettingsSectionViewModel.cs:46、Settings/CompactionSettingsViewModel.cs:29、Settings/ModelsVisibilitySectionViewModel.cs:95、Settings/Prompts/PromptsSettingsViewModel.cs:50、Settings/UsageLogSettingsViewModel.cs:33、Network/NetworkSettingsSectionViewModel.cs:63、ViewModels/NewBranchDialogViewModel.cs:49。AutomationsSettingsViewModel 用 post  marshaling(另一种安全模式,规格未承认)。缓解:Loc.RaiseAll 逐订阅者 try/catch(Loc.cs:205-224)使跨线程抛错只丢该订阅者刷新并记 Warning;生产单 UI 线程不触发。缺环:headless/跨线程场景这些订阅者文本停旧语言。建议处理类型:实现补齐守卫(或规格明确允许 post 模式并列出豁免)。

### F-APP-B-149 / F-APP-B-163(PARTIAL)通知接线回归测试缺口
规格 L200 要求每条新接线在 NotificationWiringTests 钉住“路径/等级/key”,L209 列出约 20 条调用路径。实际 N/NotificationWiringTests.cs 只有 3 个方法(approval 等待通知 + fire-and-forget helper 两例)。代码侧 key/等级真实存在(如 SessionWorkspace.cs:1419 `turn-failed:<session>` Error、1434-1445 `checkpoint:<session>` Warning、ComposerViewModel.cs:1080 ProviderMissing),但除审批/附件外均无接线回归,等级/key 漂移不会被测试发现。建议处理类型:补测试(按 L209 清单逐条)。

### F-APP-B-123(SPEC_GAP)主题调色板两处规格矛盾
L172 规定亮 背景#F5F6F5/边框#E3E6E3/文字#2B2F2C、暗 #1E2120/#262A28/#343936/#DDE3DF;L293 规定另一套 VS Code 中性色。实现(Tokens.axaml:6-15,110-230)完全跟随 L293(#F8F8F8/#D4D4D8/#3B3B3B;暗 #181818/#1F1F1F/#3F3F46/#CCCCCC)。L172 为过时文本。建议处理类型:规格修订(删 L172 旧值,指向 L293)。

### F-APP-B-228(SPEC_GAP)窗口顶部“搜索还不是功能”过时
L322 称放大镜“搜索还不是功能,tooltip 搜索(即将可用)”;L439 明确同一按钮打开命令面板且代码已实现(MainWindow.axaml:104-117 绑 OpenPaletteCommand,tooltip=shell.searchPlaceholder)。建议处理类型:规格修订 L322。

### F-APP-B-230(SPEC_GAP)终端按钮图标描述过时
L322 要求 IconTerminalFrame+IconTerminalScreen 两枚几何;Icons.axaml 无此两键,实装为单一 IconTerminal=Fluent panel_bottom 20 filled 放大(Icons.axaml:112-115),与 L294 一致。建议处理类型:规格修订 L322。

## 已实现但缺测试清单(PASS*)

- F-07 应用样式窗口图标;F-20 RaiseAll 逐订阅者隔离;F-29 导航 Button+Click;F-31 两处 DispatcherTimer Stop+解 Tick;F-32 DisposeAsync 释放分区 VM。
- F-38 体内无工具条;F-40 树选中保持/恢复;F-41 项目名 Bold;F-45 共用容器图标。
- F-53 基线下拉分组/组名不可选/命令后关闭;F-56/57 git 行让位规则(200px)。
- F-64 composer 卡几何;F-65 上条顺序;F-69 上条噪音移除。
- F-89 轮次卡头用量差值;F-101 RevealBottomGroup 单写入口。
- F-127 主按钮对比度;F-130 自绘 TabHeaderView;F-133 Inter 注册链。
- F-142 不翻译清单;F-164 通知不接清单;F-165 提供商/Host 降级 UI。
- F-180 IsActiveAgent 行底;F-183/184 Agent 树双来源与重启状态推断。
- F-195 StackPanel 方向局部;F-203/204 标签弧形几何与聚焦环(外观需肉眼,UNVERIFIED 视觉部分);F-214 TrimmedTextToolTip。
- F-223 浮窗 Extend 提示+1px 边框;F-227 自动化徽标计数;F-231 标题行按钮组/最大化图标切换。

## 程序→规格:未记录行为

1. **SettingsFileMutex 跨存储写锁**(Composition/SettingsFileMutex.cs;AppPreferences.cs:1303、ProviderStore.cs:167 等 7 处):规格 L57 只描述 JsonNode 合并,未记录多存储互斥机制。
2. **toast 上限只数未退出条目**(NotificationCenter.cs:186-192):正在离开的 toast 不占 5 条配额,规格 L201 未定义此口径。
3. **FontSizeWordmark=18 令牌**(Tokens.axaml:26):不在 L175 字体令牌清单。
4. **设置窗口标题行“重新加载”按钮**(SettingsWindow.axaml:76-82;OnReloadClick 池上读):L59 设置外壳描述未提。
5. **标题行 plan-open 按钮**(MainWindow.axaml:119-127):L301 有记录,但 L322 的右组枚举漏列(规格内部缺漏)。
6. **AutomationsRunningText 用 CurrentCulture**(MainWindowViewModel.Automations.cs:29):与 L186“数字不随区域”不一致(小计数无实际分组差异,仍属偏差)。
7. **ResourceInspector 的 DispatcherTicker.Dispose 只 Stop() 不解 Tick**(ResourceInspectorViewModel.cs:593):与 L63 所述风险同类;因生命周期短且生产调度器持续泵,影响有限。
8. **PreferencesStore 对“加载后才变损坏”的文件也备份**(AppPreferences.cs:1320-1332):规格只说首次写入前备份损坏原件,实现是超集。
9. **启动降低的 MinWidth/MinHeight 不恢复**(MainWindow.axaml.cs:83-86):规格未说明是否恢复,实现为永久降低。
10. 陈旧注释:InstrumentLayout.cs 仍提 CI 标记(已随 Git panel 删除);WorkbenchModels.cs:104-107 注释“add-project asks for 24”混淆按钮高与图标尺寸(实际 IconSize=20)。

## 规格质量问题

- **Q1(L172 vs L293)**:两套调色板数值矛盾(背景/边框/文字/暗色全不同),实现跟随 L293;L172 过时。
- **Q2(L216 vs L294)**:图标尺寸矛盾——L216“panel 工具13px、最小化/最大化11px”vs L294“14px、13px”;代码=14/13(WorkbenchButtons.cs:19、MainWindow.axaml:168,176),L216 过时。
- **Q3(L218 vs L65)**:行尾工具矛盾——L218“10px 图标/20px 按钮”vs L65“12px/22px”;代码=12/22(ProjectView.axaml:132-138),L218 过时。
- **Q4(L66 vs L68)**:检视区工具 tab 顺序矛盾——“规格树|TODO|评审”vs“TODO|评审|规格树”;代码=todo/review/specs(W/WorkbenchLayoutRegionTests::InspectorTools_holds_todo_review_and_specs_tabs_in_order)。
- **Q5(L322 vs L439)**:搜索按钮“还不是功能/即将可用”与命令面板入口矛盾;代码已实现,L322 过时。
- **Q6(L322 vs L294)**:终端图标 IconTerminalFrame/Screen 描述与 panel_bottom 单几何矛盾;L322 过时。
- **Q7(L172)**:堆叠 panel 示例“(项目 | Git)”引用已删除的 Git panel(L66 明示删除),过时残留。
- **Q8(L220 vs L219/代码)**:缩进分量“8px 箭头槽+8px 间距”与 L219“10px 槽”及代码 ChevronSlot=10/IconGap=6(TreeIcons.cs:155-163)矛盾;总量 16/22 一致,分量自相矛盾。
- **Q9(L54 vs L322)**:“以 forge.png 替代原来的 Forge 文本”与“Forge 字标右侧”歧义;代码 logo+FORGE 字标并存(MainWindow.axaml:57-66)。
- **Q10(L54)**:“不占任务栏”歧义——可读作 WorkingArea 放置(实现如此)或 ShowInTaskbar=false(主窗未设,默认占任务栏)。
- **Q11(L68)**:规范性文本留未决问题“(未确认是否应改默认)”“(未确认)”,默认范围/基线候选悬而未决。
- **Q12(L52)**:logo 尺寸/透明条款插在“面板滚动边界”标题下,应归应用标识主题,结构错位。
- **Q13(L60、L155)**:“见缺口计划 W1 行记录的实际越界清单”“缺口 #13,W8”等历史叙述/外部波次引用混入规范正文。

## 业务闭环缺口

1. **语言切换通知链(部分闭环)**:Loc 双索引器通知+RaiseAll 隔离✓、LiveSwitch 独立会话✓、订阅数不增长测试✓;但 L62 创建线程守卫约 14 个订阅者缺失(见 F-23),跨线程场景这些订阅者刷新被吞。生产单 UI 线程不受影响。
2. **通知接线回归(缺环)**:机制(分流/去重/计时/释放/权限降级)测试完整;但 L209 约 20 条调用路径仅审批/附件有接线钉住,等级与 key 的回归保护缺失(见 F-149/163)。
3. **布局快照迁移链(闭环)**:v3→v4→v5、缺 version/解析失败/存储异常回默认、取消上抛、belowHeights 可选追加、浮动恢复顺序与屏幕校正均有实现+测试(LayoutStateLoaderTests/LayoutSnapshotTests/FloatingWindowPlacementTests)。
4. **事件合并与丢弃世代号(闭环)**:FrameCoalescer disposed 闸+2048 上限+文本先排空;NotificationCenter Entry.Version 使过期计时回调失效;SessionWorkspace.Dispose→coalescer.Dispose。均有测试。
5. **会话标签唯一性与归档(闭环)**:按 id 复用重绑、归档先关标签再释放、关标签不取消运行、回合结束补释放(OnTabClosed/2057/2374)。
6. **通知权限降级(闭环)**:TryNotifyAsync→guide 状态(volatile)→设置页/测试通知/reprompt;NotApplicable 平台直落 false;组合根单例与冻结接口分离有测试。
7. **Dispose 链(闭环,一处微瑕)**:MainWindowViewModel.Dispose(停表+解 Tick+终端限时+最终快照)先于 AppComposition.DisposeAsync(分区 VM→automations→runtimes→approvals→stores→notifications→preferences 落盘);微瑕:ResourceInspector ticker 只 Stop 不解 Tick(未记录行为 7)。
8. **检查器幽灵行防护(闭环)**:回合边界只清根+非活动 agent、子 Agent 终止事件清、取消无 finished 的兜底清表,RunningToolRegistryTests 覆盖。
