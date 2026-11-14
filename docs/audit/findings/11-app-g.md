# App G(附件/首启向导/项目创建/工具图片/预览)审计发现

范围:`src/Forge.App/SPEC.md` L408(附件)、L410(向导与项目创建 W9)、L412(工具图片)、L416(富查看器)。只读审计;克隆清理涉及 `src/Forge.Workspace/Git/GitCli.Clone.cs` 委托契约。Core 附件编码/持久化与 chat/files 分区 UI 归别路,本文仅覆盖直接耦合接线。已读 test-anti-patterns SKILL。

结论汇总:**72 个功能点;PASS 60,PASS*(实现存在、缺直接测试)9,PARTIAL 2,SPEC_GAP 1,FAIL 0,MISSING 0,UNVERIFIED 0。**

## 功能点矩阵

测试证据路径省略公共前缀 `tests/Forge.App.Tests/`。

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-APP-G-01 | 粘贴/拖放/选取三入口共用一个分类器 | L408 | Views/SessionView.Images.cs:34-212(三入口均→`AttachFilesAsync`/`AddImageAsync`);ViewModels/ComposerViewModel.Images.cs:211,326 | Views/SessionViewAttachmentTests.cs::Dropping_an_image_a_pdf_and_a_text_file…、Pasting_file_paths_attaches_each_file_through_the_classifier、Pasting_a_bitmap_is_taken…、The_attach_button_with_a_platform_that_has_no_file_picker… | PASS |
| F-APP-G-02 | 扩展名定"声称类型",超限文件读前即拒 | L408 | ViewModels/FileAttachments.cs:92(KindFromExtension),122(ReadLimit);ComposerViewModel.Images.cs:230-234 | ViewModels/ComposerAttachmentTests.cs::A_file_on_disk_over_its_kind_limit_is_refused_before_reading、Each_kind_has_its_size_limit_and_the_read_is_bounded_by_the_claimed_kind;Views/SessionViewAttachmentTests.cs::A_file_one_byte_over_its_kinds_limit_is_refused_before_it_is_read | PASS |
| F-APP-G-03 | 前导字节嗅探定"实际类型" | L408 | FileAttachments.cs:137(Classify),216(SniffBinary:PDF/RIFF/ID3/fLaC/OggS/Matroska/ftyp/MPEG) | ComposerAttachmentTests.cs::Files_are_classified_by_their_leading_bytes(DataRow) | PASS |
| F-APP-G-04 | 严格 UTF-8:NUL/非法即拒并弹 Warning | L408 | FileAttachments.cs:156(TryDecodeText);ComposerViewModel.Images.cs:330-334,385-389(仅声称文本弹 NotText toast;未知扩展二进制走 336 行 Reject 无 toast) | ComposerAttachmentTests.cs::A_text_file_that_is_not_utf8_is_refused_with_a_toast、An_unknown_binary_is_refused_inline_without_a_toast(后者钉住"无 toast") | PARTIAL |
| F-APP-G-05 | 上限常量:PDF 32MB、音视频 20MB、文本 256KB、非图片≤10 | L408 | FileAttachments.cs:54-63;ImageAttachments.cs:13(图片 10 张沿用) | ComposerAttachmentTests.cs::Each_kind_has_its_size_limit…、The_eleventh_file_is_refused、A_text_file_over_its_limit_is_refused;SessionViewAttachmentTests.cs::Dropping_twelve_files_keeps_ten…、A_text_file_of_exactly_the_limit_is_still_accepted | PASS |
| F-APP-G-06 | 图片走缩放路径成 ImageBlock | L408 | ComposerViewModel.Images.cs:97-160(AddImageAsync→imageCodec.Prepare→ImageBlock) | ViewModels/ComposerImageTests.cs::An_image_is_attached_with_its_sniffed_type_and_base64_data、The_scaled_result_decides_the_type_and_bytes_sent、An_image_still_over_five_megabytes_after_scaling_is_refused | PASS |
| F-APP-G-07 | PDF/音频/视频成 FileBlock(带文件名) | L408 | ComposerViewModel.Images.cs:393-399(Encode) | ComposerAttachmentTests.cs::A_pdf_is_attached_as_a_file_block_and_taken_after_the_images | PASS |
| F-APP-G-08 | 文本内联 TextBlock:`File:` 首行+围栏=最长反引号串+1(TextAttachment) | L408 | ComposerViewModel.Images.cs:398(Encode→TextAttachment.Create);src/Forge.Core/Storage/TextAttachment.cs:19-25(围栏规则,Core 侧另审) | ComposerAttachmentTests.cs::A_text_file_is_inlined_in_the_attachment_shape | PASS |
| F-APP-G-09 | 模型门控:已知缺类→拒+Warning,key `attachment-refused:<会话>:<原因>`;未知放行 | L408 | ComposerViewModel.Images.cs:404-413(GateByModel),449-458(key),466-475(CurrentInputs 异常→未知);SessionWorkspace.cs:172(scope=session.Id) | ComposerAttachmentTests.cs::A_model_known_not_to_take_pdf_refuses_it_with_a_toast、An_unknown_capability_allows_the_file、A_catalogue_lookup_that_throws_counts_as_unknown;SessionViewAttachmentTests.cs::A_pdf_dropped_on_a_model_without_file_input…(断言精确 key `attachment-refused:<SessionId>:ModelRejects`) | PASS |
| F-APP-G-10 | 线路形状门控 ProviderAttachments.CanCarry,同样拒+通知 | L408 | ComposerViewModel.Images.cs:495-496(DefaultWireCarries),477-489(WireCarries 异常→放行);src/Forge.Core/Providers/ProviderAttachments.cs:59-68 | ComposerAttachmentTests.cs::A_wire_format_that_cannot_carry_the_file_refuses_it_with_a_toast、The_wire_gate_is_asked_per_media_type_and_provider;SessionViewAttachmentTests.cs::Audio_dropped_on_a_provider_whose_wire_format_cannot_carry_it… | PASS |
| F-APP-G-11 | 文本文件不受模型门控 | L408 | ComposerViewModel.Images.cs:219(早期门控跳过 Text),352-362(分类后仅非文本过门),531(阻塞计算跳过 Text) | 间接:ComposerAttachmentTests.cs::Switching_to_a_model…(阻塞旗标仅含 Pdf);无"Text-only 模型接受文本文件"正向用例 | PASS* |
| F-APP-G-12 | 换模型进入阻塞态弹一次 Warning,key `attachments-blocked:<会话>` | L408 | ComposerViewModel.Images.cs:518-559(RefreshAttachmentGate,进入才弹、离开静默) | ComposerAttachmentTests.cs::Switching_to_a_model_that_cannot_take_the_attachments_blocks_sending_without_dropping_them(持续阻塞不重复弹);SessionViewAttachmentTests.cs::Switching_to_a_model_that_cannot_take_a_chip…(精确 key) | PASS |
| F-APP-G-13 | 输入框下方常驻阻塞原因 | L408 | ComposerViewModel.Images.cs:66-68(AttachmentsBlockedHint);Views/SessionView.axaml `composer-attachments-blocked` 行 | SessionViewAttachmentTests.cs::Switching_to_a_model…(IsVisible 断言+移除后隐藏) | PASS |
| F-APP-G-14 | 阻塞时发送/排队/插话/转弯全禁用 | L408 | SessionWorkspace.cs:812-814(EvaluateCanSend),834(CanSendWhileRunning),160-162(Queue/Interject/Redirect 三命令共用该谓词) | SessionViewAttachmentTests.cs::Switching_to_a_model…(session-send IsEnabled=false);排队/插话/转弯共用同一谓词,未单独断言 | PASS |
| F-APP-G-15 | 附件绝不静默丢弃;移除或换回模型即解除 | L408 | ComposerViewModel.Images.cs:256-283(TakeAttachments 仅发送时取走),293-323(RestoreAttachments 不再门控),518-559 | ComposerAttachmentTests.cs::Switching_to_a_model…(Files 保留、换回静默解除、移除解除)、Restored_attachments_the_model_cannot_take_are_kept_and_block | PASS |
| F-APP-G-16 | 发送:ComposerPrompt(Text,Attachments)→RunTurnAsync(text,attachments) | L408 | SessionWorkspace.cs:820-829(TakeDraft),1343,1401(live.RunTurnAsync(text, prompt.Attachments,…));Agents/SessionRuntime.cs:423 | SessionViewAttachmentTests.cs::Sending_takes_the_chips_out_of_the_composer_and_puts_them_on_the_user_message;ViewModels/ComposerQueueTests.cs(发送链) | PASS |
| F-APP-G-17 | 插话带附件;未送达插话连同附件回队首 | L408 | SessionWorkspace.cs:1160-1175(Interject→TryInterject(text,attachments)),1255-1280(ScheduleNext:TakeUndeliveredInterjections→队首,含 leftover.Attachments);SessionRuntime.cs:416-423 | ComposerQueueTests.cs::An_interjection_with_an_image_is_delivered_to_the_running_turn_instead_of_queued、An_image_interjection_the_turn_never_reached_is_sent_next_with_its_image、An_interjection_the_turn_never_reached_is_sent_next | PASS |
| F-APP-G-18 | 用户气泡:图片缩略图+附件小片(图标/名称/大小);回放还原 | L408 | App.axaml:671-730(72×72 缩略图+FluentIcon 芯片);ViewModels/Transcript.cs:37-47,380;TranscriptReplay.cs:55-60,107-118(ImageBlock/FileBlock/TextAttachment 形状);FileAttachments.cs:340(Describe) | ComposerAttachmentTests.cs::A_replayed_user_message_shows_file_chips_and_keeps_the_typed_text_apart;ComposerImageTests.cs::A_replayed_user_message_shows_its_images_and_an_image_only_message_is_kept;SessionViewAttachmentTests.cs::Sending… | PASS |
| F-APP-G-19 | 文案在 Strings.Images.cs(session.images.*)与 Strings.Attachments.cs(session.attachments.*) | L408 | Localization/Strings.Attachments.cs:14-17、Strings.Images.cs | 文案注册由 Localization/KeyRegistryTests.cs、StringTableTests.cs 通用覆盖;无逐键专用用例 | PASS |
| F-APP-G-20 | 已知限制:插话通知只显示键入文本(仅附件为空串) | L408 | 实现已超前:Localization/Strings.Session.cs:202-214(ComposerInterjectedNotice(text,attachments) 计数+列名);SessionWorkspace.cs:1759 | ViewModels/InterjectionNoticeTests.cs::Attachments_only_counts_and_names_them_with_a_placeholder_for_an_unnamed_image、Text_with_attachments_quotes_the_text_and_counts_the_attachments 等 10 例 | SPEC_GAP |
| F-APP-G-21 | 向导是窗口内覆盖层(MainWindow ContentControl 绑 Onboarding) | L410 | MainWindow.axaml:192;ViewModels/MainWindowViewModel.Onboarding.cs:18-22 | Workbench/OnboardingShellTests.cs::An_empty_database_opens_the_wizard;Foundation/OnboardingViewTests.cs::The_wizard_renders_its_step_and_the_navigation_buttons… | PASS |
| F-APP-G-22 | 语言步直接写 AppPreferences.Language,当场切换、跳过也生效 | L410 | ViewModels/OnboardingViewModel.cs:150-176(SelectedLanguage),365-374(Select),246(Skip→RaiseFinished) | ViewModels/OnboardingViewModelTests.cs::Choosing_a_language_writes_the_live_preference、Skipping_before_walking_the_steps_finishes_once_without_touching_the_step | PASS |
| F-APP-G-23 | 提供商步只显示状态+深链 provider 分区,不重写编辑链 | L410 | OnboardingViewModel.cs:213-216(ProviderStatus),100(ProviderSetupRequested);MainWindowViewModel.Onboarding.cs:94(OpenSettingsSection(Provider)) | OnboardingViewModelTests.cs::The_provider_step_reads_the_real_catalogue_and_asks_the_shell_to_open_it | PASS |
| F-APP-G-24 | 通知步只经 INotificationPermissionGuide 读状态/开设置 | L410 | OnboardingViewModel.cs:262-292(RefreshAsync),414-443(OpenNotificationSettingsAsync);无 Notifications/** 直接调用 | OnboardingViewModelTests.cs::The_notification_step_shows_the_state_the_guide_reports、A_platform_that_refuses_to_open_its_settings_leaves_a_hint、Without_a_guide_the_step_explains_and_offers_nothing_to_open | PASS |
| F-APP-G-25 | 显示判定=ui_state 行(onboarding 键、schema 1、存在即看过),非偏好 | L410 | Services/OnboardingState.cs:20-22,32-46 | Services/OnboardingStateTests.cs::A_database_without_the_row_has_not_seen_the_wizard、Marking_completed_writes_the_row_and_reads_back_as_seen | PASS |
| F-APP-G-26 | 两条件:行不存在且无仓库项目;General 行不计数;已有仓库静默补记录 | L410 | MainWindowViewModel.Onboarding.cs:40-72(Kind==Repository 判定+MarkCompleted) | OnboardingShellTests.cs::A_fresh_database_already_holds_the_general_workspace_project、An_empty_database_opens_the_wizard、An_installation_that_already_has_a_project_is_never_put_through_setup(二次启动不再查项目) | PASS |
| F-APP-G-27 | ui_state 读不出按已看过,只记日志 | L410 | OnboardingState.cs:40-45(catch→true+ZLogWarning) | OnboardingStateTests.cs::An_unreadable_store_counts_as_seen、An_unwritable_store_does_not_throw、A_cancellation_is_not_swallowed | PASS |
| F-APP-G-28 | 触发:App 置标志;MainWindow 在 model.InitializeAsync 之后问;自造窗口宿主不置标志 | L410 | App.axaml.cs:53(ShowFirstRunWizardOnStartup=true);MainWindow.axaml.cs:200-212;MainWindowViewModel.Onboarding.cs:30 | OnboardingShellTests.cs::The_startup_switch_is_off_until_the_application_turns_it_on(App 置真本身无头不可测,代码核实) | PASS |
| F-APP-G-29 | 添加项目=一次性 PickAsync 三选(已有/克隆/新建) | L410 | ViewModels/MainWindowViewModel.Projects.cs:22-54 | Workbench/AddProjectFlowTests.cs::An_existing_folder_is_initialized…、A_clone_the_dialog_answers_is_registered、A_created_project_is_registered、A_closed_picker_changes_nothing | PASS |
| F-APP-G-30 | 已有文件夹:非仓库问一句 git init | L410 | MainWindowViewModel.Projects.cs:60-96 | AddProjectFlowTests.cs::An_existing_folder_is_initialized_after_confirmation_and_registered、Declining_the_git_init_registers_nothing | PASS |
| F-APP-G-31 | 三分支共用注册尾(AddAsync+缓存+两棵树);注册抛错→错误通知 | L410 | MainWindowViewModel.Projects.cs:124-152(RegisterProjectAsync,catch→Notify Error,key project-add-failed) | AddProjectFlowTests.cs::A_registration_the_store_refuses_is_reported_once_and_registers_nothing、A_created_project_is_registered、A_clone_the_dialog_answers_is_registered | PASS |
| F-APP-G-32 | 模态对话框:VM 为模型、Service 纯逻辑(名字规则/URL 建议名/目录解析/init/克隆) | L410 | ViewModels/ProjectCreationViewModel.cs;Services/ProjectCreationService.cs:77,138,317,372,382 | Services/ProjectCreationServiceTests.cs(名字规则 12 例、SuggestNameFromUrl 3 组 DataRow、ResolveDirectory、init 2 例);ViewModels/ProjectCreationViewModelTests.cs(14 例) | PASS |
| F-APP-G-33 | 克隆显示进度并可停止 | L410 | ProjectCreationViewModel.cs:248(Cancel),381-393(OnCloneProgress 100ms 节流+末帧补齐),222-228(ProgressText/Percent);服务层 GitCli.Clone.cs:235(--progress) | ProjectCreationServiceTests.cs::A_clone_reports_progress_and_returns_the_created_folder;ProjectCreationViewModelTests.cs::A_new_repository_shows_no_progress_while_it_runs(仅负例;VM 节流/末帧无直测) | PASS* |
| F-APP-G-34 | 取消/失败不留半截目录;已存在空目标保留但清空,重试不判"已存在" | L410 | ProjectCreationService.cs:491-527(RunCreateAsync,keepRoot:!createdHere);GitCli.Clone.cs:305-313(catch-all→DeleteTreeQuietlyAsync(keepRoot:existed));ProjectCreationService.cs:561-585(空目录可用) | ProjectCreationServiceTests.cs::A_cancelled_init_in_a_prepared_empty_folder_keeps_the_folder_and_empties_it、A_cancelled_init_of_a_new_folder_leaves_no_folder、A_clone_that_fails_reports_gits_message_and_leaves_no_folder、A_cancelled_creation_throws_instead_of_reporting_a_failure、A_directory_that_already_holds_something_is_refused_while_an_empty_one_is_usable | PASS |
| F-APP-G-35 | 克隆途中用户关窗等同取消 | L410 | Views/ProjectCreationDialog.axaml.cs:47-60(用户关+IsRunning→e.Cancel+model.Cancel);ProjectCreationViewModel.cs:254-268(Dispose 取消 runSource) | 模型级:A_cancelled_run_reports_the_cancel_text_and_does_not_complete;视图级否决/停留无测试 | PASS* |
| F-APP-G-36 | 完成通知在运行状态落定后发;仅"用户关闭且仍在跑"否决关闭 | L410 | ProjectCreationViewModel.cs:336(RaiseRunState(false))先于 351-353(RaiseCompleted);ProjectCreationDialog.axaml.cs:54(!e.IsProgrammatic),74(Completed→Close) | Views/ProjectCreationDialogTests.cs::A_successful_creation_closes_the_dialog_with_the_created_directory(真空窗:ShowDialog 完成+返回目录) | PASS |
| F-APP-G-37 | 克隆超时 0=不限→Timeout.InfiniteTimeSpan(null=git 层 2 分钟默认) | L410 | ProjectCreationService.cs:128-131;GitCli.cs:44(默认 2 分钟),90(??DefaultTimeout),964(Infinite 不建超时源) | ProjectCreationServiceTests.cs::The_clone_timeout_is_unlimited_for_zero_and_the_preference_otherwise(0→Infinite、30→30s、7200→夹取 3600) | PASS |
| F-APP-G-38 | 超时失败呈现 GitException.Message 而非 stderr | L410 | ProjectCreationService.cs:473-475(ExitCode<0 或空 stderr→Message);GitCli.cs:1364(超时抛 exit=-1) | 无(App 与 Forge.Workspace.Tests/Git/GitCliCloneTests.cs 均无超时克隆用例) | PASS* |
| F-APP-G-39 | URL 只收白名单 scheme/scp/本地路径;拒 `-` 开头、transport::、控制字符 | L410 | ProjectCreationService.cs:186-231(IsUsableUrl/AllowedSchemes/HasTransportPrefix);GitCli.Clone.cs:252-258,322(`--` 分隔+GIT_ALLOW_PROTOCOL 双保险) | ProjectCreationServiceTests.cs::An_address_must_be_absolute_and_must_not_be_an_option(DataRow)、A_clone_of_an_unusable_address_is_refused_without_touching_the_file_system | PASS |
| F-APP-G-40 | 带密码 URL 单独提示并拒绝;仅用户名放行 | L410 | ProjectCreationService.cs:265-281(CarriesPassword),235-243(DescribeUrlProblem 专用文案);GitCli.Clone.cs 报错去凭据(RedactCredentials) | ProjectCreationServiceTests.cs::Only_a_password_in_the_authority_counts_as_a_credential、A_refused_address_names_the_password_when_that_is_why、A_clone_whose_address_carries_a_password_never_starts_git;ProjectCreationViewModelTests.cs::A_remote_that_carries_a_password_is_named_as_such_and_blocks_the_start | PASS |
| F-APP-G-41 | 父目录必须完整绝对路径;相对路径不显示目标路径 | L410 | ProjectCreationService.cs:249-257(Path.IsPathFullyQualified);ProjectCreationViewModel.cs:172-190(DestinationText 相对→空) | ProjectCreationServiceTests.cs::A_relative_parent_directory_is_refused_before_anything_is_created(DataRow);ProjectCreationViewModelTests.cs::A_relative_parent_directory_is_named_in_the_hint_and_shows_no_destination | PASS |
| F-APP-G-42 | 对话框实时提示+服务开跑前再判一次 | L410 | ProjectCreationViewModel.cs:419-438(DescribeProblem),394-403(RefreshInputState);ProjectCreationService.cs:401-409(RunAsync 复检) | ProjectCreationViewModelTests.cs::An_unusable_name_is_named_in_the_hint…、An_unusable_remote_is_named…;ProjectCreationServiceTests.cs::A_refused_name_creates_nothing_and_says_which_rule_refused_it、A_missing_parent_directory_is_refused_before_anything_is_created | PASS |
| F-APP-G-43 | projects 分区:SettingsSectionIds.Projects、order 510、INTERFACE 组、ProjectsComposition 注册、SectionViewTemplate 挂视图 | L410 | Composition/ProjectsComposition.cs:17-37;Preferences/SettingsSection.cs:196(ProjectsOrder=510) | Views/ProjectsSettingsSectionViewTests.cs::The_section_renders_its_spinner_directory_field_and_buttons;Foundation/SettingsSectionsRegistrationTests.cs(分区注册通用) | PASS |
| F-APP-G-44 | 克隆超时 0–3600 秒与默认目录(空→常规工作区父目录,UI 显示"当前生效") | L410 | Preferences/AppPreferences.cs:154-156,554-555,574;Settings/ProjectsSettingsSectionViewModel.cs:77(EffectiveDirectoryText);MainWindowViewModel.Projects.cs:156-171(ParentDirectoryOf) | Settings/ProjectsSettingsSectionTests.cs::The_timeout_round_trips_to_the_preference、A_timeout_outside_the_range_lands_and_re_reads_clamped、The_field_range_is_the_preference_range、The_directory_is_trimmed_and_an_empty_one_falls_back、The_clear_button_empties_the_field… | PASS |
| F-APP-G-45 | 两值每次现读,保存后下一个对话框即生效 | L410 | ProjectCreationService.cs:117-122(DefaultParentDirectory),128-131(CloneTimeout)均每次读偏好 | ProjectCreationServiceTests.cs::The_default_parent_is_the_fallback_until_a_preference_names_one(改偏好即读回)、The_clone_timeout_is_unlimited_for_zero… | PASS |
| F-APP-G-46 | 文案三张表互不共用前缀:onboarding.* / projectCreate.*+settings.projects.* / projectFlow.* | L410 | Localization/Strings.Onboarding.cs:18-35、Strings.ProjectCreation.cs:23-30、Strings.ProjectFlow.cs:16-20 | Localization/KeyRegistryTests.cs、StringTableTests.cs(注册表通用校验) | PASS |
| F-APP-G-47 | 工具行折叠只显示名字/参数摘要/状态;展开后结果文本下列图片 | L412 | Views/ToolRowView.axaml:10-40(折叠行三字段;IsExpanded 容器内结果文本后挂 ToolRowImagesView) | VM 级:ViewModels/TranscriptToolImagesTests.cs;视图布局无专用头测 | PASS* |
| F-APP-G-48 | CaptureImages 钩子只挑 ImageBlock | L412 | ViewModels/Transcript.Tools.cs:122-132(Complete→CaptureImages(toolResult.Blocks));Transcript.ToolImages.cs:49-69 | TranscriptToolImagesTests.cs::A_finished_result_with_an_image_exposes_it_with_its_data_unchanged、A_text_only_result_leaves_the_row_without_images | PASS |
| F-APP-G-49 | 首读 Thumbnail 后台解码;两读只解一次;Codec 可替换 | L412 | ImageAttachments.cs:237(Codec),246-263(loading 旗标),265-287(Task.Run) | TranscriptToolImagesTests.cs::A_tool_image_decodes_its_thumbnail_lazily_and_exactly_once(CountingCodec:0→1→1,edge=300) | PASS |
| F-APP-G-50 | 长边=ChatToolImageThumbnailPixels(chat-thumbnail-edge,64–1024,默认 256);只约束解码不重编码;IImageCodec.Thumbnail(data,edge) 默认接口成员;旧单参 128px | L412 | Transcript.ToolImages.cs:15(ToolImageSettings);Composition/ViewerSettingsComposition.cs:29;AppPreferences.cs:102-104,430-433;ImageAttachments.cs:123(默认成员),129(128),192-208(有界解码) | Settings/ChatSettingsSectionTests.cs::Thumbnail_edge_and_the_send_switch_round_trip…、The_control_bounds_mirror_the_preferences_thumbnail_clamp;Foundation/ToolAndFilePreferencesTests.cs(持久化);TranscriptToolImagesTests.cs(edge 透传) | PASS |
| F-APP-G-51 | ToolRowImagesView:WrapPanel 间距 6、Border、悬停 Caption(媒体类型·KB)、AutomationId=tool-image | L412 | Views/ToolRowImagesView.axaml:8-31;ImageAttachments.cs:243(Caption) | 无(布局无头测;代码核实) | PASS* |
| F-APP-G-52 | 图片总是显示;ChatToolImagesToModel 只决定模型收不收 | L412 | Transcript.ToolImages.cs:49-69(显示不读开关);Composition/AppComposition.cs:573(SendToolImages)→Agents/SessionRuntime.cs:999(AgentOptions.SendToolImagesToModel) | 开关往返/持久化:ChatSettingsSectionTests、ToolAndFilePreferencesTests;"关→模型不收但 UI 仍显示"无 App 级端到端用例(Core 循环另审) | PASS* |
| F-APP-G-53 | 回放:存储 ToolResultBlock 用 Blocks 重建 ToolResult,图片随块回来 | L412 | ViewModels/TranscriptReplay.cs:91-97(new ToolResult(result.Blocks, result.IsError)→CompleteTool) | TranscriptReplayToolImageTests.cs::A_stored_text_and_image_tool_result_replays_as_text_with_an_image_placeholder、A_stored_error_result_with_an_image_keeps_its_error_state | PASS |
| F-APP-G-54 | 先分类再读;Classify 纯函数无 IO;LoadAsync 喂前 512 字节 | L416 | ViewModels/Preview/FilePreviewKind.cs:33-52;FileDocumentViewModel.Editing.cs:288-298,343-356(ReadFirstBytes) | ViewModels/Preview/FilePreviewClassifierTests.cs(7 例,含 Empty_bytes_still_classify_by_extension) | PASS |
| F-APP-G-55 | LFS 指针最先判(version 前缀,不管扩展名) | L416 | FilePreviewKind.cs:39-43;Preview/LfsPointer.cs(解析);PlaceholderFilePreviewViewModel.ForLfs | FilePreviewClassifierTests.cs::An_LFS_pointer_is_detected_from_the_bytes_before_any_extension、The_LFS_marker_alone…、Bytes_that_only_mention_lfs…、A_different_lfs_spec_version…;Preview/LfsPointerTests.cs(4 例);ViewModels/FilePreviewDocumentTests.cs::An_lfs_pointer_png_shows_the_pointer_card_with_its_fields | PASS |
| F-APP-G-56 | 扩展名映射;其余走文本路径,仅 NotSupportedException 出二进制占位;超 MaxFileBytes 仍"文件过大" | L416 | FilePreviewKind.cs:45-52;FileDocumentViewModel.Editing.cs:18(MaxFileBytes=8MiB),325-340 | FilePreviewClassifierTests.cs::Each_extension_classifies_to_its_preview_kind;FilePreviewDocumentTests.cs::Loading_a_binary_file_shows_the_binary_placeholder_not_an_error、Loading_a_text_path_still_creates_the_editor_document;ViewModels/FileLoadRobustnessTests.cs::A_file_over_16_MiB_is_rejected_with_a_visible_error_and_no_document | PASS |
| F-APP-G-57 | 预览只读:HasDocument=false,编辑器/工具条隐藏,IsDirty/CanSave 恒 false | L416 | FileDocumentViewModel.Editing.cs:53-60;Views/FileDocumentView.axaml:66-114(工具条/编辑器 IsVisible=HasDocument) | FilePreviewDocumentTests.cs::Loading_a_csv_path_sets_a_table_preview_and_no_editor_document | PASS |
| F-APP-G-58 | 标签始终显示路径;常驻"用默认程序打开"(file-open-external,Process.Start UseShellExecute);PDF 不内部渲染 | L416 | FileDocumentView.axaml:75-77(按钮无 HasDocument 门控);Preview/FilePreviewViewModel.cs:87-101(PreviewOpener);PlaceholderFilePreviewViewModel.ForPdf | FilePreviewDocumentTests.cs::A_non_page_document_words_the_same_button_as_the_system_app;按钮常驻性代码核实 | PASS |
| F-APP-G-59 | HTML 是可编辑源码;同一按钮换"在浏览器中打开"(files.preview.openInBrowser,按文件+语言现读) | L416 | FileDocumentViewModel.Editing.cs:397-398(ExternalOpenLabel),406-417(RefreshText) | FilePreviewDocumentTests.cs::An_html_page_opens_as_editable_source_and_offers_the_browser、A_non_page_document_words_the_same_button_as_the_system_app | PASS |
| F-APP-G-60 | 图片后台解码;ImageHeader 先读尺寸;>4096 直接解码到 4096;头不可读退回解码后缩放并释放;小图不放大;解码器可注入 | L416 | Preview/ImageHeader.cs:54-80(DecodeBounded);Preview/ImageFilePreviewViewModel.cs:18,49-95(Build),103-107(DefaultDecoder);FileDocumentViewModel.Editing.cs:135-140(Create(imageDecoder:)) | Preview/ImageHeaderTests.cs(10 例,含 Garbage_and_truncated_headers_are_unknown_never_an_exception);FilePreviewDocumentTests.cs::A_png_loads_an_image_preview_and_a_stubbed_decoder_is_a_notice、An_image_over_the_preview_limit_shows_the_placeholder_card_and_is_not_decoded(真实解码头测不覆盖,规格已承认) | PASS |
| F-APP-G-61 | RFC 4180:引号/成对引号/CRLF/LF/字段内换行/参差补齐;TSV 制表符不认引号 | L416 | Preview/TablePreviewReader.cs:30-180(quoting=separator==',') | Preview/TablePreviewReaderTests.cs(21 例:A_quoted_field_keeps_its_comma、Doubled_quotes…、A_quoted_field_spanning_two_lines…、Crlf_ends_a_row、A_ragged_row_is_padded…、Tsv_uses_tabs_and_treats_quotes_as_literal_characters 等) | PASS |
| F-APP-G-62 | 超限行流式读完只计数不建字符串;标题下说明跳过数 | L416 | TablePreviewReader.cs:159-166(Keep 提前返回),183-199(EndRow skipped++);TableFilePreviewViewModel.cs:57-60(Summary) | TablePreviewReaderTests.cs::A_thousand_row_file_stops_at_the_row_limit_with_the_skip_count、Skipped_rows_are_counted_including_a_last_row_of_empty_fields | PASS |
| F-APP-G-63 | 单字段≤4096 字符、单行≤1024 列(防御上限) | L416 | TablePreviewReader.cs:21,24,159-166(代理对例外可留 4097),168-176 | TablePreviewReaderTests.cs::A_field_past_the_cap_is_cut_and_the_next_field_still_parses、The_field_cap_never_splits_a_surrogate_pair、Fields_past_the_column_cap_are_dropped_but_the_row_still_ends | PASS |
| F-APP-G-64 | 表格读取可取消 | L416 | TablePreviewReader.cs:27(64K 字符 stride),196(每 1024 行) | TablePreviewReaderTests.cs::A_cancelled_parse_of_a_long_single_line_throws、A_cancelled_parse_of_many_short_rows_throws | PASS |
| F-APP-G-65 | 列宽按前 200 行最宽单元格估算;行用 VirtualizingStackPanel | L416 | TableFilePreviewViewModel.cs:13(WidthSampleRows=200),103-120;Views/Preview/TablePreviewView.axaml:45 | FilePreviewDocumentTests.cs::The_table_column_width_follows_the_code_font_size(仅字符宽;200 行采样与虚拟化无直测) | PASS* |
| F-APP-G-66 | 笔记本 nbformat 四种输出;按字符上限截断(不切代理对)并标出 | L416 | Preview/NotebookPreviewReader.cs:117-161(stream/execute_result/display_data/error),226-240(Truncate) | Preview/NotebookPreviewReaderTests.cs(11 例,含 Truncation_never_cuts_between_the_halves_of_a_surrogate_pair、An_execute_result_text_output_is_truncated_to_the_configured_limit) | PASS |
| F-APP-G-67 | image/png 按需用同一有界解码器;>5MB 不显示;坏 JSON 出提示不出异常 | L416 | NotebookPreviewReader.cs:176-195(MaxImageBytes=5MB),79-83(catch);NotebookFilePreviewViewModel.cs:205-247(DecodeAsync,默认 ImageFilePreviewViewModel.DefaultDecoder) | NotebookPreviewReaderTests.cs::An_image_png_output_carries_the_decoded_bytes、An_image_output_over_the_size_cap_is_skipped_without_decoding_it、Malformed_json_is_a_problem_message_never_an_exception、A_corrupted_png_payload_is_skipped… | PASS |
| F-APP-G-68 | 整个 .ipynb 以 8MiB 读取上限把关 | L416 | NotebookFilePreviewViewModel.cs:24(MaxNotebookBytes),53-60(Build 门控) | 无(测试未引用 MaxNotebookBytes/NotebookTooLarge) | PASS* |
| F-APP-G-69 | 非文本预览继承 FilePreviewViewModel(路径/名称/字节数/本地化标题/打开命令/失败提示) | L416 | Preview/FilePreviewViewModel.cs:13-84 | FilePreviewDocumentTests.cs(各预览类型)、PlaceholderPreviewView 相关用例 | PASS |
| F-APP-G-70 | 语言切换:FileDocumentView 在 LanguageChanged 调 RefreshText;VM 不自订阅;离屏重挂补刷 | L416 | Views/FileDocumentView.axaml.cs:58,62,141;FileDocumentViewModel.Editing.cs:406-417;各预览 RefreshText | FilePreviewDocumentTests.cs::A_language_refresh_renotifies_the_document_labels_and_the_table_preview、A_language_refresh_renotifies_the_placeholder_reason | PASS |
| F-APP-G-71 | Release 释放位图并计数、幂等;换文件/取消/被顶掉三路径都释放;竞赛输家也释放 | L416 | FileDocumentViewModel.Editing.cs:205-212(输家 finally Release),241-258(ReleaseCoreAsync/ReleaseDocumentAsync);FilePreviewViewModel.cs:57-66 | FilePreviewDocumentTests.cs::Releasing_the_document_clears_the_preview、Releasing_twice_is_counted_but_harmless、A_preview_that_loses_the_race_is_released_and_the_winner_is_not(只断言赢家计数,输家释放未被观察) | PARTIAL |
| F-APP-G-72 | 分类/CSV/笔记本/LFS 纯逻辑单测覆盖;真实解码与文件选择器头测不覆盖 | L416 | Preview/ 各纯函数 | Preview/ 五个测试文件共 53 例;FilePreviewDocumentTests 用注入解码器 | PASS |

## 非 PASS 项详情

### F-APP-G-04(PARTIAL)UTF-8 拒绝通知范围窄于规格措辞
- 证据:规格 L408 称"其余字节严格按 UTF-8 解码、含 NUL 或非法 UTF-8 即拒并弹 Warning 通知"。实现中只有**声称是文本**(扩展名命中 TextExtensions)的文件解码失败才弹 NotText toast(ComposerViewModel.Images.cs:330-334);未知扩展名的二进制文件走 `Reject(Unsupported)`(336 行),仅输入框下内联错误、无 toast,且被测试 `An_unknown_binary_is_refused_inline_without_a_toast` 明确钉住。
- 缺口:规格未区分"声称文本"与"未知类型"两条拒绝路径;按字面读,未知二进制的 UTF-8 拒绝也应弹通知。
- 建议处理类型:规格修订(通知范围限定为声称文本)或实现补齐(未知类型也弹 toast),需裁决;现状测试钉住的是窄行为。

### F-APP-G-20(SPEC_GAP)"已知限制"已过时
- 证据:规格 L408 末尾"插话通知只显示键入的文本(仅附件的插话通知为空串)"。实现 Strings.Session.cs:202-214 的 `ComposerInterjectedNotice(text, attachments)` 对仅附件插话输出"插话:N 个附件(名称列表)",图片无名显示占位词,名称随语言现解析;SessionWorkspace.cs:1759 已接线。InterjectionNoticeTests.cs 10 个用例钉住新行为(含单复数、语言切换、同名区分)。
- 建议处理类型:规格修订——删除该"已知限制",改述为通知计数+列名行为。

### F-APP-G-71(PARTIAL)竞赛释放测试只钉住赢家一半
- 证据:规格 L416 声称"这条有专门的并发测试(两个加载重叠,输的那份恰好释放一次、赢的那份只在关闭文档时释放一次)"。实际 `A_preview_that_loses_the_race_is_released_and_the_winner_is_not`(FilePreviewDocumentTests.cs:116-136)只断言 `winner.ReleaseCount` 0→1 与 Preview 同一性;输家预览对象未被捕获,其 `ReleaseCount` 无任何断言(且 StallingDecoder 返回 null,输家是 Failed 预览,无位图可释放)。实现侧输家释放确实存在(FileDocumentViewModel.Editing.cs:208-211 finally)。
- 缺口:若输家释放被回归删除,该测试仍绿;规格对测试证据的表述过度。
- 建议处理类型:测试补强(让解码器返回可跟踪的位图/可计数预览,断言输家 ReleaseCount==1)+ 规格措辞收敛。

### PASS* 项(实现存在、缺直接测试)
- **F-APP-G-11 文本门控豁免**:代码三处跳过 Text(早期门控/分类后门控/阻塞计算),但无"Text-only 模型成功接受文本文件"正向用例;现有证据仅间接(阻塞旗标只含 Pdf)。建议补 1 例。
- **F-APP-G-33 克隆进度 VM 显示**:服务层进度有真 git 用例;VM 的 100ms 节流、末帧补齐、IsProgressIndeterminate 无直测。建议用假 onProgress 回调钉节流与末帧。
- **F-APP-G-35 关窗否决(用户侧)**:OnClosing 的 `IsRunning && !IsProgrammatic` 分支无视图级测试;成功侧(程序化关闭不被否决)已被真空窗用例钉住。建议补"运行中用户关窗→取消→对话框停留显示 Cancelled"头测。
- **F-APP-G-38 超时消息分支**:`ExitCode<0→GitException.Message` 分支在 App 与 Workspace 克隆测试中均无覆盖(GitCliCloneTests.cs 无 Timeout 用例)。建议用假 GitException(exit=-1、stderr=进度噪声)断言呈现的是 Message。
- **F-APP-G-47 / F-APP-G-51 工具行视图布局**:折叠三字段、展开图片位置、WrapPanel 间距、Border/Caption/AutomationId 均代码核实,无头测。与规格承认的"真实渲染不覆盖"一致,但 AutomationId/间距可用头测钉。
- **F-APP-G-52 图片开关端到端**:App 接线完整(AppComposition.cs:573→SessionRuntime.cs:999),显示路径不读开关;但"开关关→模型请求不带图、界面仍显示"无 App 级用例(Core 循环别路审)。建议跨接线补 1 例或在 Core 审计中确认。
- **F-APP-G-65 列宽采样/虚拟化**:200 行采样上限与 VirtualizingStackPanel 无直测(字符宽换算有)。
- **F-APP-G-68 笔记本 8MiB 门控**:MaxNotebookBytes 常量与 Build 门控存在,无任何测试引用。建议补"超限→NotebookTooLarge 提示且不读文件"用例。

## 已实现但缺测试清单

| 项 | 位置 | 缺失用例 |
|---|---|---|
| 文本门控豁免正向 | ComposerViewModel.Images.cs:219,352,531 | Text-only 模型接受 .txt |
| VM 进度节流/末帧 | ProjectCreationViewModel.cs:381-393,325-330 | 假进度回调钉 100ms 节流与完成末帧 |
| 运行中用户关窗 | ProjectCreationDialog.axaml.cs:54-60 | 头测:否决关闭+取消+停留 |
| 超时→Message 分支 | ProjectCreationService.cs:473-475 | 假 GitException(exit=-1) |
| 工具行/图片视图布局 | ToolRowView.axaml、ToolRowImagesView.axaml | AutomationId、间距、折叠/展开 |
| 开关端到端 | AppComposition.cs:573 | 关→模型不收、UI 仍显示 |
| 列宽 200 行采样 | TableFilePreviewViewModel.cs:103-120 | >200 行时只采样前 200 |
| 笔记本 8MiB 门控 | NotebookFilePreviewViewModel.cs:53-60 | 超限提示且不读 |
| 竞赛输家释放计数 | FileDocumentViewModel.Editing.cs:208-211 | 输家 ReleaseCount==1(现有测试未观察) |

## 程序→规格:未记录行为

1. **图片读前宽松上限**:`MaxEncodedBytes×16`(80MiB)作为图片文件读前界限(ComposerViewModel.Images.cs:184),规格只说"图片沿用 ImageAttachmentRules"。
2. **未知 FileBlock 媒体类型按 Video 计**(最严格)(FileAttachments.cs:192-199 KindOf)。
3. **空文件不算文本**(LooksLikeText 空→false,FileAttachments.cs:203-210);**BOM 被剥离**(TryDecodeText:161-164)——规格只说"严格 UTF-8"。
4. **多文件拖放/粘贴遇 TooMany 即停**,不再尝试后续文件(SessionView.Images.cs:207-214)。
5. **未知扩展二进制拒绝仅内联、无 toast**(被测试钉住;见 F-APP-G-04)。
6. **名字规则细节**:长度≤100、Windows 设备名(CON/NUL/COM1…含扩展名)、尾随点/空格拒绝(ProjectCreationService.cs:77,323-356)——规格只笼统说"名字规则"。
7. **URL 附加规则**:裸词(无 `/` 或 `:`)拒绝、非法路径字符拒绝(ProjectCreationService.cs:194-200,229-230)。
8. **git 层双保险**:`GIT_ALLOW_PROTOCOL=https:http:ssh:git:file`、命令行 `--` 分隔、报错去凭据(GitCli.Clone.cs:252-258,322-326)——规格只描述了 App 侧规则。
9. **进度节流 100ms、占用检查防抖 200ms、代际守卫**(ProjectCreationViewModel.cs:20,27,447-453)。
10. **OnboardingState 写入尽力而为**(失败仅日志,MarkCompletedAsync:49-61);**项目列表读失败→跳过向导**(MainWindowViewModel.Onboarding.cs:63-70)——规格只提 ui_state 读失败。
11. **窗口销毁时 CloseOnboarding 不写"看过"记录**(MainWindowViewModel.Onboarding.cs:122-127)。
12. **表格头启发式**(首行文本+次行数字→表头,TablePreviewReader.cs:206-224)、**单元格显示宽 40 列上限**(TableFilePreviewViewModel.cs:16)、**东亚宽字符计 2 列**(DisplayWidth)。
13. **表格字符宽=CodeFontSize×0.62**(FilePreviewSettings.cs:24,53-55)——规格未提列宽与代码字号联动。
14. **CSV 字段上限代理对例外**(可留 4097 字符,TablePreviewReader.cs:163-165)——规格写"最多 4096"。
15. **笔记本截断标记在解析时定语言**(NotebookPreviewReader.cs:222-225 注释明示),语言切换不重译已截断文本中的标记——与 L416 的 RefreshText 重发本地化属性存在张力。
16. **注入解码器返回超大位图时的兜底缩放+释放**(ImageFilePreviewViewModel.cs:75-87)。
17. **无 guide 时通知步只解释**(OnboardingStrings.NotificationsNoGuide,OnboardingViewModel.cs:219-221)。
18. **插话通知计数+列名**(见 F-APP-G-20,规格"已知限制"已被实现推翻)。
19. **用户气泡图片缩略图固定 72×72**(App.axaml:684-685)。

## 规格质量问题

1. **L408 过时叙述(最严重)**:"已知限制:……插话通知只显示键入的文本(仅附件的插话通知为空串)"与实现+10 个测试直接矛盾(Strings.Session.cs:202-214)。规格把已修复的行为仍记为限制,误导后续审计与开发。
2. **L416 测试证据过度声称(严重)**:"这条有专门的并发测试(……输的那份恰好释放一次……)"——现有测试只断言赢家计数,输家释放不可观察(FilePreviewDocumentTests.cs:116-136)。规格把未钉住的一半写成已钉住。
3. **L408 措辞与实现范围不符(矛盾/模糊)**:"含 NUL 或非法 UTF-8 即拒并弹 Warning 通知"未限定"声称文本"路径;未知扩展二进制拒绝无 toast 且被测试钉住(见 F-APP-G-04)。
4. **L410 历史叙述混入规范**:"这是测试会话评审发现的主路径缺陷,现由……钉住"属评审史而非行为契约;同段大量 W0/W0b/W4/W9 波次考古建议移入 changelog。
5. **L416 精度缺口**:"单字段最多保留 4096 字符"未提代理对例外(实现可留 4097,有测试钉住);笔记本截断标记的语言时机(解析时固定)与"切换语言重发本地化属性"的关系未说明。
6. **重复**:L412 与 L414(chat 分区)重复叙述缩略图 64–1024/默认 256 与开关语义;L416 与 L418(files 分区)重复三项上限。两处各维护一份易漂移。
7. **L412 措辞模糊**:"每个结果一个 ToolImageViewModel"实际是**每个图像块一个**(一个结果可多图,Transcript.ToolImages.cs:53-60);建议改为"每个图像块一个"。

## 业务闭环缺口

1. **超时失败呈现链无测试守护**(F-APP-G-38):0=不限的翻译有测试,但"超时→exit=-1→呈现 Message 而非进度 stderr"整条分支零覆盖。GitCli 超时退出码约定一旦变化(≥0),服务会静默回退 stderr 尾,违反规格且无测试报警。建议补假 GitException 服务用例(处理类型:测试)。
2. **关窗否决的用户侧半环无视图测试**(F-APP-G-35):成功侧半环已被真空窗用例钉住;用户运行中关窗→取消→停留依赖 Avalonia `IsProgrammatic` 语义,框架升级回归无守护(处理类型:测试)。
3. **竞赛输家位图释放不可观察**(F-APP-G-71):实现正确但测试 oracle 缺半,位图泄漏回归无法被现有套件捕获;规格声称已钉住(处理类型:测试+规格)。
4. **附件拒绝通知范围分歧**(F-APP-G-04):规格与测试钉住的行为不一致,需裁决改哪边,否则下一波开发可能按规格字面"修复"出重复 toast(处理类型:规格裁决)。
5. **工具图片开关的跨层契约**(F-APP-G-52):App 只负责把偏好接进 AgentOptions;"模型确实不收"在 Core 循环,App 侧无端到端证据。若 Core 审计未覆盖,此契约两侧都无守护(处理类型:测试,跨审计路协调)。
6. **文本门控豁免与笔记本 8MiB 门控**(F-APP-G-11/68):均为规格明写的规则、实现存在、正向用例缺失,属低成本可补的守护缺口(处理类型:测试)。
