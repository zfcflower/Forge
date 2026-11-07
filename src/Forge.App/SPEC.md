---
id: forge-app
type: module-design
status: draft
title: Forge.App
parent: forge-architecture
---

# Forge.App

Forge 的 Native AOT 桌面主程序(Avalonia 12.1.x,仅桌面)。承载组合根与停靠式工作台:把 `forge-core`(Agent 循环、存储)、`forge-workspace`、`forge-mcp`、`forge-editor`、`forge-host-client`(插件/CodeMode 代理)与 `forge-approval` 串成一个进程。

## 边界

- Native AOT(`IsAotCompatible` + `PublishAot`),依赖闭包不含 Roslyn、`AssemblyLoadContext`、`Forge.Scripting`/`Forge.ScriptExecutor`;只用源生成 JSON(`SettingsJsonContext`、`WorkbenchJsonContext`);无 DI 容器/MVVM 包(手写 `INotifyPropertyChanged` + `ICommand`)。
- **不实现任何存储或 Git 管道。** 只经 `ISessionStore`/`ISessionCatalog`/`IProjectRegistry`/`ITodoStore`/`IUiStateStore`/`ITurnCheckpointStore`、`GitCli`、`WorktreeManager`、`SpecGraph`、`ReviewStore` 组合;`src/Forge.App` 中不得出现 `SqliteConnection`、`CREATE TABLE`、`write-tree`、`GIT_INDEX_FILE`。
- 不直接读审批数据库做判断(哈希经 `plugins.status` 取);为状态面用只读连接数 `plugin_approval` 的行数,进程里不持有任何可写审批连接,也不建表、不迁移。计数只在文件不存在(不打开、不创建文件/`-wal`/`-shm`)或没有 `plugin_approval` 表时返回 0;其余错误上抛,状态栏显示"不可用",不把损坏或锁住的库显示成没有插件。
- UI 线程不碰存储:Microsoft.Data.Sqlite 的 `*Async` 是同步执行的,所以所有存储/SQLite/文件调用(含归档、重命名、`ui_state` 写入、TODO 增删)都经后台助手,不在调度器上 await。唯一例外是退出:最后一次布局保存在后台执行,但 UI 线程最多等它 3 秒(超时只记 Trace),保证关窗时快照尽量落盘又不卡死。
- 无 P/Invoke;进程归属是 `forge-extension-host` 的职责。App 只启动/重启 Host 子进程并保证退出时杀树等待。
- 工作台布局自写(`Workbench/`),不用任何停靠框架;原内化的 Dock 已移除。

## 组合根

构造顺序即依赖方向,拆除严格逆序:`AppPaths` → (审批数据库路径,不打开)→ `ExtensionHostSupervisor`(`--worker`/`--executor` 显式传路径)→ `WorkspaceServices` → `McpServices` → `EditorServices` → `ProviderSettings` + forge.db 上的 5 个存储 + `TurnCheckpointService` + `WorktreeService` → `AgentRuntimeManager`。审批库归 Host 所有,App 只拿到 `<host 数据目录>/approvals.db` 的路径。数据根默认 `%LOCALAPPDATA%/Forge`,`FORGE_DATA_DIR` 覆盖;常规工作区默认 `文档/Forge/General`,`FORGE_GENERAL_WORKSPACE` 覆盖。开发验证一律设置这两个变量,不写真实用户目录。

**模型提供方与模型列表。** 提供方仍由 `settings.json` 的 `provider` 段配置;OpenAI 兼容提供方的模型菜单“刷新”会显式请求 `<baseUrl>/models`,读取并去重 `data[].id`,请求限时 20 秒且不会在启动时访问网络。密钥优先使用 `apiKeyEnv` 指定的环境变量,不会写入配置文件。DeepSeek E2E 示例见 `settings.deepseek.e2e.example.json`:设置 `DEEPSEEK_API_KEY` 后将示例复制为数据目录的 `settings.json`(可用 `FORGE_DATA_DIR` 隔离测试数据),初始提供 `deepseek-chat` 和 `deepseek-reasoner`,刷新可再从服务端拉取模型列表。

## 关键决策

**面板滚动边界。** 工作台的区域和面板必须按最终分配的宽高测量与排列,滚动视口不得沿用整窗口或整区域的高度。分割、抽屉、最大化/恢复及窗口缩放后,`ScrollViewer` 与虚拟化 `ListBox` 滚到末端应能完整看到最后一行;不可用额外底部留白或滚动事件补偿掩盖布局错误。仍可见的面板不得在每次排列前临时排列到零尺寸,避免改变其滚动偏移。

标题栏 logo 尺寸为 18×18 DIP;PNG 与多尺寸 ICO 均保留源图的透明通道,不得合成灰色或主题色背景。

**应用标识与启动窗口。** 标题栏以 `Assets/forge.png` 替代原来的 Forge 文本,窗口标题仍保留 Forge 供任务栏与辅助功能识别。PNG 为提供的原始 logo;同源的 `Assets/forge.ico` 含 16/24/32/48/64/128/256 像素图层,经 `ApplicationIcon` 嵌入 Windows 可执行文件(安装后的程序与默认指向该程序的快捷方式使用同一图标),并复制到发布目录供安装打包引用。所有窗口通过应用样式共用该图标。主窗口启动居中于所在屏幕的 `WorkingArea`,不占任务栏;按桌面缩放换算尺寸并计入原生窗口边框。可用区域小于通常最小尺寸时降低最小尺寸,避免窗口底部或右侧越界;打开后用实际屏幕、桌面缩放与边框再校正一次,不在用户后续移动或调整尺寸时重新居中。

**偏好、文案与设置分区。** 三件共用的底座,界面语言、字体、终端、Git/PR、扩展与审批五个功能都从这里取,不再各写一份。

- **偏好。** `Preferences/AppPreferences.cs`:`AppPreferences` 的 setter 就是校验器(空字体族回退默认值,字号夹到 9–28),改完立刻生效;`PreferencesStore` 读写 `settings.json` 的 `preferences` 段。写回先把整份文件读成 `JsonNode`,只替换 `preferences` 节点,再用 `Forge.IO.AtomicFile` 原子替换,因此 `provider` 段(仍由 `ProviderSettings` 读)和任何未知字段原样保留。读写都在线程池(`LoadAsync`/`SaveAsync` 内部 `Task.Run`),UI 线程不碰文件;`LoadAsync` 只读不写,也不发通知(加载发生在窗口绑定之前,语言由组合根自己推给 `Loc`)。只有用户改了偏好才写:每次修改重启 500 ms 防抖窗口,窗口结束在线程池写一次;`DisposeAsync` 强制把待写的改动落盘(组合根拆除时调用)。文件损坏或 `preferences` 段无效时用默认值并填 `LoadProblem`,绝不因加载覆盖文件;第一次写入前先把损坏的原文件复制成 `settings.json.bak`,且只备份一次。源生成 JSON 走新增的 `PreferencesJsonContext`。
- **文案。** `Localization/Loc.cs` 是实时文案表:`Register(key, 中文, English)` 一名一键;`Language` 的 setter 把 `System` 解析成具体语言(永不为 `System`),切换时依次发出 `PropertyChanged("Item[]")`、`PropertyChanged("Item")` 与 `LanguageChanged`——Avalonia 编译索引器绑定(`{Binding [Key], Source={x:Static loc:Loc.Instance}}`,AOT 安全)只认 `Item`,只发 `Item[]` 会让所有绑定文本停在旧语言。缺失 key 返回 `[key]` 且每个 key 只 Trace 一次;同 key 两次注册文本不同直接抛异常(文本相同则幂等)。每个功能一张表:新建 `Localization/Strings.<功能>.cs`,类名 `<功能>Strings`,键名带功能前缀,在 `[ModuleInitializer]` 里调用 `Loc.Register(...)` 注册——没有共享清单文件,并行功能不会互相冲突,`Loc.EnsureRegistered()`(组合根启动时调用)因此只保住“所有表已注册”这个顺序点。通知按订阅者逐个 try/catch:某个订阅者抛错不能中断整批刷新(注意:Avalonia 对同一 INPC 源只挂一个弱事件订阅,其内部某个绑定抛错仍会截断其后的绑定,见下方 headless 测试一条)。“跟随系统”保留 `InvariantGlobalization`,由 `SystemLanguageDetector.Resolve`(纯函数,输入显式传入)决定:文化名为 `zh*` → Windows 注册表 `PreferredUILanguages` → `MuiCached\MachinePreferredUILanguages` → `International\LocaleName`(注册表优先于环境变量,Git Bash 会把 `LANG` 设成英文)→ `LC_ALL`/`LC_MESSAGES`/`LANG` 第一个已设置的 → 英文;注册表用 BCL 的 `Microsoft.Win32.Registry`,无 P/Invoke,读失败当作不存在。macOS 从 Dock 启动常无 `LANG`,会落到英文(未验证)。语言在设置分区 `language`(`Localization/LanguageSettingsViewModel.cs`:跟随系统 / 中文 / English,语言名不随界面语言变)。
- **设置分区。** `Preferences/SettingsSection.cs` 的 `SettingsSections` 是分区注册表:`Add` 按 `Order` 升序插入(同序保持加入顺序),id 重复抛异常;分区标题是 `Loc` key,`SettingsSection.Title` 在 `Loc.LanguageChanged` 时由设置视图模型重新抬一次。注册表由组合根创建并暴露成 `AppComposition.Settings`:功能在 `AppComposition.Create()` 里 `Settings.Add(new SettingsSection(id, 标题 key, order, 该功能的设置视图模型))`,再在 `App.axaml` 加一个按该视图模型类型的 `DataTemplate`,不碰设置页本身。设置页分左右两栏:左侧是分区导航列表(`ListBox` 绑 `Sections.Items`、`SelectedItem` 双向绑 `SettingsViewModel.SelectedSection`,导航标题带分区 id 作 `AutomationId`),右侧 `ScrollViewer` 里一个按选中分区 `ViewModel` 的 `ContentControl`;默认选中 `provider`。现有“提供方”“路径与服务”已改成 `provider`、`paths` 两个分区(顺序来自 `SettingsSectionIds`),内容与行为不变;git 功能新增 `git` 分区(拉取策略 `ff-only`/`rebase`/`merge` 与网络超时 10–600 秒,偏好在 `AppPreferences` 里同名属性、setter 即校验器,存 `settings.json` 的 `preferences` 段)。同一分区还有等待 PR 检查的四项:轮询间隔 `GitChecksPollSeconds`(默认 30,夹到 10–300)、最长等待 `GitChecksMaxWaitMinutes`(默认 30,夹到 1–180)、完成时系统通知开关 `GitNotifications`(默认开)、`gh` 可执行文件路径 `GitGhPath`(默认空 = 用 PATH 里的 `gh`,保存时去首尾空白,设置页输入框占位符是 `gh`)。这四项都在每次使用时从实时偏好读取,改完不用重启:`gh` 路径在加载偏好之后才有值,所以组合根传给 PR 工具和 Git panel的是 `ConfiguredGh`/`ConfiguredPrPlatform`(每次调用取当前路径,路径没变就复用同一个 `GhCli`),轮询时间由 `GitToolPreferences.ChecksOptions` 每次现算。设置页自身的文案(面板标题、两个分区标题、路径行标签、两个按钮)注册在 `Localization/Strings.Core.cs`,语言切换时随 `LanguageChanged` 刷新。
- **在 headless 测试里切换语言**:前一个 headless 会话的控件被 Avalonia 静态量(`XamlIlRuntimeHelpers` 的 `s_lastApplication`、合成器序列化队列)强引用,回收不掉;它们对 `Loc.Instance` 的绑定在下次切换时跨线程抛错,截断同一弱事件订阅里新窗口的绑定。生产里只有一个 UI 线程,不会发生。因此即时切换测试放在独立项目 `tests/Forge.App.LiveSwitch.Tests`,整个程序集共用一个 `HeadlessUnitTestSession`;`Forge.App.Tests` 里不要写依赖 XAML 直绑 `Loc` 实时刷新的断言。所有 `LanguageChanged` 订阅者记下创建线程,收到别的线程的通知就忽略。
- **DispatcherTimer 与释放。** `DispatcherTimer.Stop()` 只是停用,调度器下次轮询才把它移出列表;不再泵的调度器会一直持有 `Tick` 目标。长生命周期所有者在 `Dispose` 里停止计时器并解开 `Tick`(`MainWindowViewModel`、`FilesPanelViewModel`);`AppComposition.DisposeAsync` 释放实现了 `IDisposable` 的设置分区视图模型。

**工作台布局。** 三个区域按角色命名,不按位置:项目区、主区、检视区。默认顺序 项目 | 主 | 检视,可以拖拽互换,内容跟着角色走。
- 项目区:顶部常规对话区,其下 项目 → worktree → 会话;归档会话收进各 worktree 的“历史 (n)”。项目 panel 的标题栏右侧放“新建常规对话”与“刷新项目”两个工具(`Workbench.ProjectPanel.HeaderTools`,语言切换时原位重建),panel 体内不再有自己的工具条——标题栏归 `PanelChrome` 所有,体内再放一条工具条会看着像断掉的第二层标题栏;体内只有“添加项目”主按钮、状态文本和树。项目树整行可点:点行的任意位置执行该行的动作(`TreeNodeViewModel.Activate`:会话行打开会话;有子项的行展开/折叠;没有子项的项目/worktree/常规对话行新建会话),Enter/空格对选中行同样生效;点在行内按钮上只执行按钮本身。
- 项目区下方是独立的 Git panel(`WorkbenchIds.GitPanel`,`WorkbenchLayout.GitPanel`,可钉住/自动隐藏、可放大),与项目 panel 上下等分;不再是检视区的选项卡。
- 主区:主选项卡组(只放会话标签)+ 底部组(子 Agent 选项卡组,命令行以后也放这里)。
- 检视区:文件 panel(全部 | 已改动)在上,TODO | 评审 | 规格树 选项卡组在下。Git panel(左侧独立 panel)显示分支/上游/超前落后;fetch/pull/push/刷新是标题栏上的图标按钮(`MainWindowViewModel.RefreshGitTools`);显示进行中的 rebase/merge 与冲突文件、远程与分支列表,随活动会话刷新(attach、每回合结束、每次操作后;不自动 fetch);超前落后文本同时上状态栏,分支名显示在标题行会话标题之后。分支有 PR 时,Git panel还显示 PR 头(#号、标题、状态)、“等待检查 / 取消等待”与实时进度,以及只读的评审线程列表,CI 标记 `CI ✓/✗/…` 上状态栏(见下一段)。在文件 panel 里点文件,会在检视区靠主区一侧打开**预览组**(选项卡组,做文件预览与逐行审阅)。预览列与文件列之间的分隔条命中带骑在 1px 缝隙上(`IsPreviewLeftOfFiles` 时在预览列右缘,否则在文件列右缘);铺满主区时覆盖矩形用与显示相同的列宽(`RegionPresenter.PreviewColumnWidth`),覆盖右缘正好落在文件列左缘(留 1px 分隔)。区域内每个 panel 在测量阶段就按最终分配的矩形测量,排列阶段复用同一布局计算,长行文件等宽内容只在本列内滚动,不会伸到文件列下面;预览通过 `Forge.Editor` 的 `GrammarRegistry` 按扩展名选语法,用 `SyntaxTokenTextBlock` 渲染 token 类别颜色。区域边界和预览/文件列间的分隔条都可拖动调整宽度。原中区的固定“文件”“改动”两个标签都移进预览组:
  - “全部”里单击复用预览选项卡,右键才新建。
  - “已改动”里点文件,打开固定的“改动”选项卡(按轮次 / 未提交改动,多文件连续 Diff),并定位到该文件。
  - 预览组的“铺满主区”按钮让它盖住整个主区,方便专注审阅。
- `WorkbenchLayout` 是唯一的布局状态所有者,不依赖 Avalonia。视图模型只通过它开关选项卡,不碰控件树。
- 预览列的宽度只由一个状态驱动:检视区“当前是否含预览列”(= 文件 panel 停靠且预览组有停靠的选项卡)变化时才借或还 `PreviewWidth`。打开、关闭、浮出最后一个预览、浮出文件 panel 都走这一处,不在各操作里各自加减,否则浮出期间的开关会让宽度漂移。

**Git panel的 PR 部分(等待检查、通知、评审线程)。** 全部在 `Git/`,面板只拿一个可空的 `GitPrServices(Platform, Poller, Options, Notifier, Post)`:为空就是纯本地 git 面板,永不启动 `gh`。

- **读 PR 是第二阶段,不占 `IsBusy`。** 本地读完后,仅当仓库有远程且当前在某个分支上才去问平台(本地独立仓库、分离 HEAD 都不会启动 `gh`,现有测试仓库因此不受影响)。顺序:`GetAvailabilityAsync` → `ViewPullRequestAsync`(当前分支)→ PR 存在时 `GetReviewThreadsAsync`。都在线程池、挂在可取消的 CTS 上,新一轮读取、清空上下文、`Dispose` 都会取消上一轮;结果回到 UI 线程再套用,过期的结果丢弃。`gh` 缺失或未登录只影响 PR 部分:显示一块提示(`git-gh-not-ready`)加平台给的原因,本地 git 照常;“没有 PR”“读取失败”各是一行说明。可用性不缓存,每次刷新重问(调用次数可预测)。
- **等待检查。** “等待检查”对已加载的 PR 都出现(不看 PR 状态,字面照规格;已合并/关闭时结果就是 `NotOpen`)。点击后 `PrWatcher` 在线程池跑 `PrChecksPoller.WaitAsync`(与 `pr_checks` 工具同一个轮询器),每次读数和最终结果经 `Post` 缝合回 UI 线程,面板显示“通过 n · 失败 n · 进行中 n”、合并状态和一句结论(12 种 `PrWaitOutcome` 各有本地化文案,需要处理的用错误色)。取消后不再投递任何东西(包括已排队的读数、结果和通知),取消不发通知。每个 worktree 一个监视(`WorktreeState.Watch`),切走再切回仍在;正在运行的监视不被 16 项上限驱逐;启动它的会话关标签或归档(`MainWindowViewModel.ReleaseSession`)、面板 `Dispose` 时取消。
- **状态栏 CI 标记只反映监视器**:等待中 `CI …`(看到失败检查即 `CI ✗`),结束后 `CI ✓`(就绪)/`CI ✗`(检查失败)/`CI –`(无 CI)/`CI !`(其余需要看一眼的结果),PR 已合并或关闭则清空。没有被动拉取检查快照,所以没点过“等待检查”的分支不显示标记(偏离:规格措辞可读成常驻指示,这里只做监视器驱动的一版,免得后台自己轮询 gh)。
- **系统通知。** `SystemNotifier`(`ISystemNotifier`)在等待自然结束时发一条:标题“Forge:PR #n”,正文就是结论句,按语言取字。受 `GitNotifications` 控制(每次调用读),失败只 `Trace` 一行 `[notify]`,绝不抛出,单次最长 5 秒,超时杀整个进程树。Windows:`powershell.exe -NoProfile -NonInteractive -Command` 跑固定脚本经 WinRT `ToastNotificationManager` 弹 toast,标题和正文只通过环境变量 `FORGE_NOTIFY_TITLE`/`FORGE_NOTIFY_BODY` 传入并用 `CreateTextNode` 放进 XML,**不会**拼进脚本,所以 PR 文本无法注入;**未打包程序没有自己的应用标识,toast 显示为“Windows PowerShell”发出**(给 Forge 自己的标识需要带 AppUserModelID 的开始菜单快捷方式,未做)。macOS:`osascript` 的 `on run argv`,文本作参数传入;Linux:`notify-send --app-name=Forge -- 标题 正文`。**macOS 与 Linux 两条路径只按工具文档写成,没有在对应系统上运行过**;Windows 路径在开发机上跑通(退出码 0)。
- **评审线程区(只读)。** 未解决的排在前面,每条显示 `路径:行`(过时线程用原始行)、已解决/未解决与过时标记、第一条评论(作者 + 截断到 400 字)、回复数、以及线程里**最新一条带 agent 标记行**的回复(`PrReplyLedger.TryParseMarker`)的结论(已应用 abc1234 / 反驳 / 需澄清)和去掉标记行的回复正文。回复、解决是 agent 工具的事,面板不提供。语言切换时按新语言重建条目。
- **截图按钮。** 有 PR 且项目声明了截图命令时,PR 分组显示“截图”按钮,旁边显示将要执行的命令;点击即运行(用户点按钮本身就是确认,不再弹审批),与 `pr_screenshots` 共用 `ScreenshotRunner`(执行命令在池线程,受 `.forge/screenshots.json` 的 timeoutSeconds 约束),结果写进面板结果区与失败提示。
- **接缝(给测试)。** `GitPrServices.Post` 是 UI 线程投递缝合(默认 `UiThreadDispatcher.Instance.Post`);`PrWatcher` 的构造参数 `post`/`onProgress`/`onCompleted`;`SystemNotifier(enabled, run, platform, time)` 的进程运行器、平台、时钟均可注入,`CreateStartInfo` 是纯函数(`internal`),命令行参数可直接断言;轮询器的 `TimeProvider` 决定等待时钟。

**会话运行。** 一个主会话一个 `AgentHost`(`AgentRuntimeManager` → `SessionRuntime`),各自取消源,可并发;关标签不取消运行;重开/取消归档走 `ResumeRootAsync`。在项目上新建会话 = 新 worktree(`SiblingOfRepository`:`<仓库>.worktrees/<slug>`),分支名经文本提示输入、默认 `forge/session-MMdd-HHmmss`(取消/留空中止不建;显式名不加后缀,非法或已存在走既有失败通知,不建会话);常规会话在 `scratch/<日期-标题>/`。会话工具 = 宿主工具 + `run_code` + git 工具(`SessionRuntime.BuildTools()`,`AgentRuntimeServices.Git` 可空注入;git 工具不进 `run_code` 的脚本注册表,脚本调用绕过逐次审批呈现)+ 文件与 shell 工具(`AgentRuntimeServices.FileTools`,由 `Forge.IO.FileTools` 一次构造、全会话共享,取会话 worktree 作为工作区)。文件工具同样**不进** `run_code` 的注册表:脚本里的 `bash` 不会经过逐次审批呈现。它们读选项的工厂是 `ToolPreferences.From(偏好)`,改 `settings.json` 里的 `toolsShellPath`/`toolsRipgrepPath`/`toolsBashTimeoutSeconds` 不需重启(无设置页,只能手改文件——见推迟)。运行时的释放点在:归档、标签已关且该回合结束、组合根拆除前(`MainWindowViewModel.Dispose` 先于 `AppComposition.DisposeAsync`)。检查点快照失败不把会话卡在“运行中”:记入会话通知,状态重置与 `TurnCompleted` 放在嵌套 `finally`。

**步骤提交(`StepCommitter`)。** TODO 条目从非 done 变成 done 时(Agent 或用户在 TODO panel 勾选),把会话 worktree 里全部未提交改动提交到会话分支;开关 `gitStepCommits`(默认开)。子会话(`ParentSessionId` 非空)继承父会话的 worktree,其步骤一律跳过(不把父会话的改动卷进子步骤);无 worktree 的常规会话跳过。范围是 `add -A` + `commit`(会话 worktree 专属),提交说明优先 `commit_subject`、否则条目标题,正文是 summary;verification 不进提交说明。失败(钩子拒绝、冲突中、游离 HEAD)不回滚 done:工具结果与 TODO 行显示失败原因并提供“重试提交”;提交尝试失败后把 index 复位到 HEAD(mixed `git reset`,不动工作区),被拒绝的改动回到未暂存。晚取消:提交对象与 ref 已存在、只差慢 `post-commit` 钩子时被取消(git 被杀),重读 HEAD,前进了就把新 sha 记到该步骤并报已提交;取消发生在 `git add` 阶段则照常抛取消。同 worktree 的提交按路径门串行,不同 worktree 并行。计划摘要(`plan_summaries`)加上各步骤的 sha/summary/verification 由 `PrBodyRenderer`(Workspace)渲染成 PR 正文。

**会话输入框(composer)。** 圆角卡片(1px 边框、8px 圆角、聚焦时强调色边框),内含排队消息小片、多行输入与工具条。工具条左侧:Agent 选择(默认主 Agent 或内置 Scout/Worker/Plan/Reviewer)与模型选择(模型名 + 强度信号格);模型弹层有搜索、已配置模型加 `settings.json` 的 `provider.models`(星标钉住、钉住在前)、强度滑条(Off/Low/Medium/High,只在标题旁显示当前值,滑条下不放文字)、“新会话默认值”(存 `composer.json`)与“刷新”。改 Agent/模型/强度会在下一回合前重建会话主 Agent。工具条右侧:空闲时是 VS Code 式圆形强调色发送按钮(上箭头,草稿为空时禁用);回合进行中是停止按钮 + 圆形“…”菜单:插话(消息在当前工具批次之后送达正在运行的 Agent;若回合先结束则成为下一回合)、排队(显示为可移除小片,回合结束自动发送;运行中 Enter/Ctrl+Enter 默认排队)、转弯(取消当前回合并立即发送)、停止(取消并暂停队列;空草稿发送即恢复)。在词首输入 `/` 打开技能筛选(`.github/skills`、`.forge/skills` 与用户 `skills` 目录下的 SKILL.md;目前没有内置斜杠命令),输入 `@` 打开工作区文件选择器(子串/模糊匹配,最多 50 条);两者都支持 上/下/Enter/Tab/Esc,插入 `/名称 ` 或 `@路径 `。根 Agent 插话由 Core 的 `AgentInstance.Interject`/`TakeInterjections` 与 `AgentInterjected` 事件实现。

**事件路由与渲染。** 按 `AgentEvent.AgentId` 分发到各自的轮次卡模型,子 Agent 文本不进主会话。所有非文本事件经 `FrameCoalescer<AgentEvent>` 合并:生产者在任意线程无锁入队(`ConcurrentQueue`),只有当前没有已排队的 flush 时才 `Dispatcher.UIThread.Post` 一次(`DispatcherPriority.Background`,用 `Interlocked` 标志控制);一次 flush 在 UI 线程按入队顺序处理当前积累的项目,单次上限 `MaxItemsPerFlush`(2048),超出留到下一帧。优先级选 `Background` 而非 `Render`:skill 建议大批量/低优先级更新用 `Background` 以免挤占输入与渲染,且 flush 本身不占用渲染帧;持续流下仍是每调度周期最多一次 UI 更新(≈ 每帧一次),无需 `TopLevel.RequestAnimationFrame` 或 16ms 定时器。文本增量在后台线程合并进 `TranscriptViewModel` 的 pending 缓冲,flush 由同一个合并器调度:每次 flush 先排空文本、再按 FIFO 处理非文本事件,所以文本落在其后的非文本事件之前;非文本事件间顺序与 Core 发出顺序一致。会话释放时合并器停止调度并丢弃未处理项,释放后不再往 UI 投递。流式增量收进 `TextSegmentViewModel` 段落(≤2048 字、段落边界收口),一次 flush 只重写未闭合段,避免整段回复反复复制。Diff 行、文件树、规格树各自展开成一个 `RangeObservableCollection` 行列表配 `ListBox`/`VirtualizingStackPanel`(展开 = 区间插入/移除),树/行视图模型、`FileIndex` 与规格图读取都在线程池构建、UI 线程一次 `ReplaceAll`。轮次卡头“第 N 轮 · 时间 · 耗时 · 输入/输出”:`AgentTurnCompleted` 是实例累计用量,卡上显示本轮差值;子 Agent 用 `AgentSubAgentFinished` 的本 run 用量;未上报一律显示“—”。

**子 Agent 区。** 每个运行/排队中的子 Agent 一个标签,标签底部唯一的状态栏(状态、任务、模型、用量、实时耗时、“停止” → `SubAgentControl.Stop`),无输入框;标签携带所属主会话、绝不跨会话。结束的按顺序收进标题栏末端“已结束 (n)”(20px 扁平按钮 + Flyout 列表),选中后从子会话存储经 `TranscriptReplay` 重放为只读标签。“已结束”列表也算内容:没有未浮动的标签但有已结束条目时,整块仍留在布局里(标记为 `TabGroupModel.KeepsVisibleWhenEmpty`,由视图模型按条目数设置);两者都没有时整块从布局移除,再出现时按原比例放回。会话底部“子 Agent · 收起/展开”开关(带运行计数)是手动收起入口,收起后本会话内不再自动弹出。

**轮次改动。** 每个主轮次开始/结束(含失败/取消)用 `GitCli.SnapshotTreeAsync` 取树哈希写 `turn_checkpoints`;轮次号从已存检查点续接,否则重开会话后新轮次的检查点会被“已完成轮次不覆盖”规则静默丢弃。非 git 目录跳过。

**布局快照自管。** 两份快照,都用源生成 JSON 存在 `ui_state`:
- `LayoutSnapshot`(v3)存 `layout`:区域顺序、宽度、自动隐藏状态,底部组高度与可见性,各区 panel 高度比例,预览组的开关与宽度,浮动窗口(装的对象 + 几何)。
- `OpenTabsSnapshot`(v2,增加预览选项卡路径)存 `open-tabs`。
- 拖动区域分隔条/panel 分隔条松手时各触发一次 `LayoutChanged`(`EndPanelSplitterDrag`),配合 800ms 防抖快照写入,确保末次夹紧值也入快照。

恢复规则:
- 版本不符(含旧 Dock 时代的 v2)、缺 `version` 字段或解析失败,一律回落默认布局,不迁移。快照的 `Version` 缺省为 0,序列化时才写入当前版本,所以缺字段不会被当成当前版本。存储异常同样回落;取消照常抛出。
- 未知 id 丢弃;缺失项保持默认位置。
- 浮动窗口在它装的选项卡存在之后才应用;坐标落在所有屏幕之外时,移回主窗口所在屏幕。
- 检视区宽度存的是不含预览列的宽度:恢复时预览选项卡重新打开会再借一次 `PreviewWidth`,若按总宽存就会重复计入。
- 未放置过的浮动窗口不存坐标,恢复时居中于主窗口。
- 放大状态不持久化;切换选项卡会触发保存(预览组与工具组的活动选项卡要持久化)。

**主题。** 灰白为主体,护眼绿只做强调色;亮:背景 #F5F6F5 / 面板 #FFFFFF / 边框 #E3E6E3 / 文字 #2B2F2C / 强调 #3A8F5C;暗:#1E2120 / #262A28 / #343936 / #DDE3DF / #5FBF86;`RequestedThemeVariant=Default` 跟随系统,同时映射 Fluent 系统色。区域间分隔线 2px、panel 间 1px,都用边框令牌;线本身细,可拖热区 ≥6px。主按钮用单独的 `PrimaryButtonBackgroundBrush`(#2F7449 亮 / #5FBF86 暗),白字在 #3A8F5C 只有 3.99:1、在 #2F7449 有 5.65:1,达到 WCAG AA;强调令牌不变。基础按钮样式只作用于 Forge 自有样式类,不全局覆盖 `Button`。

**字体(界面字体与代码字体)。** 用户可分别改界面字体族与字号、代码字体族与字号,即时生效,重启后保持(偏好 `UiFontFamily`/`UiFontSize`/`CodeFontFamily`/`CodeFontSize`,字号 9–28 步进 1)。
- **令牌即契约**:字体只经资源键传递,控件不订阅偏好。`FontUi`/`FontMono`(`FontFamily`)、`FontSizeXs/Sm/Base/Md/Lg/Xl`(界面字号等比缩放,`FontScale.Compute`:比例 = 字号/13,`Math.Round(默认值×比例, AwayFromZero)`,13 时等于 11/12/13/15/18/22)、`FontSizeCode`(代码字号原值,默认 13)。`Tokens.axaml` 保留静态默认值供设计器与无应用器的测试;所有使用处一律 `DynamicResource`,不要再写 `{StaticResource FontSize*}`/`{StaticResource FontMono}` 或写死字号。`:is(TextBlock).mono`(含 `SelectableTextBlock` 的工具参数/结果)用 `FontMono` + `FontSizeCode`。界面字体经 `Appearance/AppearanceStyles.axaml` 的 `:is(Window)` 样式继承到树、标签、聊天、按钮。默认界面字体族写成 `fonts:Inter#Inter, …`(内嵌 Inter;裸 `Inter` 会解析成 Segoe UI)。
- **应用器** `Appearance/AppearanceApplier`(UI 线程):在 `Application.Current.Resources` 里合并一个自有子字典,每次应用新建并整体替换(一次替换实际引发 2 次资源通知:旧字典移除 + 新字典挂上,已接受);只在四个字体属性变化且值变了时替换;同时覆盖 Fluent 的 `ContentControlThemeFontFamily`/`ControlContentThemeFontSize`,使内置控件与弹出层跟随。族名解析失败保留上一个有效值,没有上一个时回退默认回退链,同一非法文本只 Trace 一次,不抛。
- **时序**:组合根(`AppComposition.AppearanceFeature`)创建时按默认偏好应用一次(只在 `Application` 存在且就是当前线程时才创建应用器,否则只有设置分区);偏好加载不发通知,`InitializeWorkbenchAsync` 加载后在 UI 线程同时**重同步设置分区编辑框**(`AppearanceSettingsViewModel.SyncAll`)并重新应用——缺了前者,编辑框停在默认值,失焦提交会把默认值写回覆盖已保存的字体。拆除时先释放应用器与分区。
- **系统字体目录** `Appearance/SystemFontCatalog`:首次请求时在线程池枚举 `FontManager.Current.SystemFonts` 族名,去重、序数忽略大小写排序,同一 `Task` 缓存;枚举失败返回空列表并 Trace(仍可手输)。
- **设置分区 `appearance`**(`AppearanceSettingsViewModel` + `AppearanceSettingsView.axaml`,编译绑定):界面、代码两组,各有可编辑下拉(`AutoCompleteBox`,包含过滤,允许手输逗号分隔回退链)、字号 `NumericUpDown`(整数,输入的小数四舍五入)、本组字体预览、只重置本组的“恢复默认”。族名在选中候选、回车或失焦时提交,不随按键应用;字号即时提交;外部改动回显。字体列表只在构造线程(UI)上交给编辑框。文案表 `Localization/Strings.Appearance.cs`(`appearance.`)。
- 终端的 `TerminalControl` 自己读代码字体偏好。推迟:行高、字重、连字、按语言区分字体、整段样例代码预览。
- 文件与 shell 工具的三项偏好(shell 路径、ripgrep 路径、`bash` 默认超时)本轮没有设置页,只能手改 `settings.json` 的 `preferences` 段,值非法时回退默认(与终端偏好目前的处境相同)。推迟:这三项与终端分区一起做一个“工具”设置分区。

**字符串与自动化。** 界面语言中文 / English,切换即时生效。用户可见文案按界面区域分表:`Localization/Strings.Shell.cs`(`shell.` `workbench.` `status.` `provider.` `diag.` `dialog.`)、`Strings.Projects.cs`(`project.`)、`Strings.Session.cs`(`session.` `turn.` `subagent.`)、`Strings.Inspector.cs`(`files.` `changes.` `todo.` `review.` `spec.`)、`Strings.Language.cs`(`language.`)、`Strings.Format.cs`(`format.`)、`Strings.Core.cs`(`settings.`)、`Strings.Appearance.cs`(`appearance.`);其它功能各自的表照旧。规则:
- 每张表暴露类型化访问器(无参是属性,带参数是方法);同一句话在两个区域出现时各注册各的键,不跨表引用。英文需要变形的名词用 `.one`/`.other` 两条键,由 `LocText.Plural` 选键;键名其它地方不得以 `.one`/`.other` 结尾。
- 格式:句式随语言,数字与时间不随区域——`TextFormat.Number` 千分位一律 `,`,标识符(进程号、行号)用 `TextFormat.Id` 不分组,时间一律 `HH:mm:ss`,耗时“4.2 秒 / 3 分 05 秒”与“4.2s / 3m 05s”;数字参数先格式化成字符串再传入。
- 刷新:XAML 静态文案绑 `{Binding [key], Source={x:Static loc:Loc.Instance}}`;视图模型的文本在 getter 里现算,只有带 `Dispose` 的长生命周期所有者订阅 `Loc.LanguageChanged` 并向下调用卡片/行的 `RefreshText()`,逐行逐卡不订阅静态事件;存下来以后再显示的文本(启动诊断、提供方状态、通知、轮次脚注)存成 `LocText`,显示时解析;代码建的控件在挂到可视树时订阅、离开时退订,菜单在打开时取文本;工作台固定 panel/选项卡记标题键,切换时重设。
- 不翻译:模型输出、用户输入、文件内容与路径、插件文案、会话标题(默认标题按创建时的语言生成并存库)、开发者才看得到的异常文本(英文)。
- 防回归:`tests/Forge.App.Tests/Localization/` 扫描 `src/Forge.App` 的 `.cs`/`.axaml`,`Localization/Strings.*.cs` 之外不得有中日韩字符(白名单为空,确需例外写成“文件 + 原文”精确匹配并注明原因);表完整性(中英齐全、英文无中日韩字符、占位符一致、XAML 与 C# 用到的键都已注册);格式样例;系统语言检测分支矩阵。即时切换在 `tests/Forge.App.LiveSwitch.Tests`。

UI 自动化只认 `AutomationProperties.AutomationId`,它不随语言变。

**降级。** 提供方未配置 / Host 未找到降级为明确的 UI 状态,绝不抛异常或伪造模型调用。审批:只读自动放行,`todo` 工具显式放行(写会话清单而非工作区);其余写/执行为 Ask,经审批队列由用户当场决定(见“审批队列与 Agent 扩展”),拒绝时轮次卡显示“已拒绝”。

**树一律扁平虚拟化。** 项目树、文件树、规格树、Diff 都是扁平 `RangeObservableCollection` 行列表 + 虚拟化 `ListBox`;展开/折叠只对子树做区间插入/删除,展开状态跨刷新保留(按路径/id 记住);文件树与规格树刷新仍是整表 `ReplaceAll`,滚动位置会回到顶部(未改为按路径 diff 的区间更新);行模型负责缩进档数与引导线,视图不递归。后台计划只吃 UI 线程上快照好的不可变数据(会话运行集合、Agent 层级都归调度器所有)。

**树与图标。** 文件树、项目树、规格树同一套样式(参考用户 2026-11-05 给的图):
- 标题栏/工具按钮的图标是 24 网格的填充路径(`Resources/Tokens.axaml`、`Workbench.axaml`,`F0` 前缀 = even-odd),同一个家族内最细处不低 2 单位——12–15px 渲染时低于这个尺寸就糊成点。设置按钮是六齿齿轮(`IconGear`):八齿在 13–15px 下读成一团星芒,六齿才看得出是齿轮。panel 工具图标 13px、设置 15px、窗口最小化/最大化 11px。
- 行高 28px;左侧项目/会话/Agent 图标为与文字近似大小的 14px `LineIcon` 描边线框,不填充;超长标题省略并在截断时显示全文 tooltip。活动会话行有浅强调底色。文件/规格树图标继续用 1.25px 描边的 `Path`。文件夹分闭合/打开两种,展开时换成打开的;文件是带折角的纸张;规格节点用文件图标,有子节点的用文件夹图标。
- 箭头是细线折线 `TreeChevronGeometry`,折叠向右,展开时 rotate 90°。
- 缩进:每级 `IndentMarks.Step` = 箭头槽宽(8px)+ 箭头与图标间距(14px)= 22px;子项箭头的左边缘与父项图标的左边缘对齐,子项图标落在父项文字起点;没有箭头的文件行,图标与同级文件夹的图标对齐。Agent 行保持 16px 一级、不画引导线。
- 引导线:每个展开的文件夹下面一条 1px 竖线,对齐它的箭头中线,一直延伸到最后一个子孙行。行模型维护“祖先展开”位掩码,`IndentMarks.ForMask` 返回缓存数组,模板不分配;折叠/展开时递归刷新子孙的掩码。
- 悬停/选中底是 6px 圆角,从本行箭头处开始铺到右边,不盖住左侧缩进区。
- 文件名用比例字体、正文色;规格树标题是链接样式(点击打开),状态以灰色小字跟在标题后。

**Agent 树。** 会话下有子 Agent 时,在会话行插入“主 Agent → 子 Agent → 孙 Agent”层级:按 `AgentSubAgentStarted` 的父 `AgentId` 链接嵌套;按 `Kind` 选线框图标(主 Agent 星形、Scout 放大镜、Worker 锤子、Plan 三行、Reviewer 对勾、未知回落菱形)。排版参考用户给的折叠组图:行首只放图标,展开箭头跟在标题后面,状态文案以灰色小字跟在标题后;不画引导线,只靠缩进和图标区分层级。图标按状态上色(`AgentState`):运行中 `RunningBrush` 加透明度呼吸动画(1↔0.45,1.6s),排队/未知灰、完成绿、失败红、已停止橙。Avalonia 12.1.4 读不到系统的“减少动画”设置,而本模块不允许 P/Invoke,所以只留了 `TreeMotion.ReducedMotion` 开关,尚未接到系统设置。子 Agent 仍在中下标签区。重启后层级靠递归查询 `parent_session_id` 还原(直接子的父是主 Agent 行,其余以祖先会话 id 为父);存储没有运行状态列,状态从最后几条存储消息推断(`Cancelled.` 结果=已停止、错误结果或悬空调用=失败、最终文本=完成),推不出显示中性"已结束",不把未知当成完成。

**已改动徽标。** 与"改动"面板同语义(对 HEAD 的整树差异、含未跟踪、路径按会话目录裁剪,`scratch/…` 下也有计数);二进制只显示状态字母,绝不显示 `+0 -0`;watcher 700 ms 一跳,补丁按"状态 + 每文件大小/修改时间"指纹缓存,无变化不重算。

**会话标签与归档。** 会话标签按会话 id 唯一:重开同一会话复用并重绑已有标签;归档先关闭其标签再释放运行时与视图模型。

**区域宽度分配。** 每区有最小宽度:项目/检视 200px、主 360px。拖动区域分隔条时:
- 分隔条另一侧紧邻的区域吸收全部增量。
- 被挤压的那一侧,主区先缩到最小宽度,其余区域再按离分隔条由近到远依次缩到最小宽度。
- 边栏缩到最小宽度后继续拖,越过最小宽度的一半(`CollapseOvershoot` = 0.5)才自动隐藏;这段粘滞是为了防止贴着最小宽度拖动时误隐藏。主区永不隐藏。
- 空间够但边栏总宽过大时(例如第一次 arrange 之前就打开了预览,检视区已经加上 `PreviewWidth`),也同样先把边栏按顺序缩到最小、再隐藏非 protected 的边栏,主区最后拿到 ≥360px;`protected` 区域只缩不藏。
- 窗口变窄时先缩主区,再缩检视区、项目区,仍不够就依次自动隐藏;变宽的增量给主区。

计算由纯函数 `RegionWidthSolver` 完成,拖动路径不分配内存,松手才写快照。不用 `GridSplitter`,因为它只能调相邻两列。零分配要算上挂着 UI 的整条同步事件链:`ObservableObject` 按属性名缓存 `PropertyChangedEventArgs`;控件只对影响自身的属性名刷新(宽度类属性只触发 arrange,不重算标题栏)。

**Panel 标题栏。** 左侧是标题,选项卡组改为选项卡条(只在本组内拖动排序,不跨组)。右侧图标按钮按能力显示:
- 钉住/自动隐藏:边栏与底部组有,作用于整个区域或整个底部组。
- 放大/还原:所在区域可见 panel ≥2 时显示。放大时该 panel 占满区域,其他 panel 收起(`IsVisible=false`)并整体淡入(120ms `CubicEaseOut`,`TreeMotion.ReducedMotion` 时直接显示不淡入)。
- 缩小/还原:有可接收空间的展开同伴时显示;缩小仅保留 28px 标题栏,正文隐藏但不销毁。同区域的纵向同伴回收高度;预览组缩小为检视区顶部整宽标题条,文件与工具列回收其下空间。放大清除该区域的缩小状态;最后一个展开同伴消失时自动还原。缩小状态不持久化,不替代整个区域的自动隐藏。
- 新窗口/停靠:只给可浮出的对象。
- 其他按需工具,如子 Agent 的“已结束 (n)”:标题栏里是 20px 扁平按钮(“已结束 (n)” + 小 chevron),点开 Flyout 列出已结束的子 Agent;空列表时按钮禁用。

除预览组的关闭按钮外不放菜单和关闭按钮;预览组标题栏有 X 按钮,点击倒序关掉组内全部标签 → 组不再 docked-visible、借用宽度归还 Main,与关掉最后一个预览标签同终态。用户关闭经终端与文件内容守卫串行处理,取消即停止;关闭其他标签也逐个等待。选项卡自带关闭,右键菜单每个选项卡一份实例。选项卡视觉参考 VS 工具窗口标签:25px 高、贴标题栏底边;未激活透明、文字居中次要色,悬停文字变主色;激活标签有 1px 上左右边框和 4px 上圆角,底色取所在 panel 的背景,压住标签条下方的 1px 分隔线,像从 panel 里长出来一样。可关闭标签始终预留 16px 关闭槽与 6px 间距,关闭 × 只在激活或悬停时可见、可点击,不改变标签与相邻标签的几何。

**文件预览与编辑。** 打开文本文件即进入 AvaloniaEdit 可编辑视图,默认不换行,可水平/垂直滚动到完整内容;20px 图标切换换行,支持选择、复制、撤销/重做与工具栏保存/Ctrl+S,不再切换着色预览与无着色编辑模式。文本与符号不得过滤或用显示占位字符替换。原生文档与撤销历史属于文件模型,切换标签或分离视图不销毁缓冲。着色使用 AvaloniaEdit.TextMate,不重建全文件行列表。迁移期间原生文档增量镜像到 `Forge.Editor.TextDocument`,保存仍经其原子编码保存,保存中的后续编辑保持脏。镜像故障须可见,保留原生未保存文本并阻止错误保存,不能把同步故障当成已保存。文件读取/文本物化/保存在线程池;内容显示不等待 LSP 启动,后台打开语言会话,变更/保存/关闭沿既有同步泵发送,失败显示独立语言服务状态。绝对路径去重复用文件标签,避免同 URI 多份缓冲。未保存关闭/替换需确认,多文件确认全部通过且内容未变才允许继续,不伪造已保存状态;保存中禁止替换/关闭。归档不保存也不丢弃仍打开的编辑。加载取消、标签关闭取消在途加载和语言会话打开。

文件超过 8 MiB 明确拒绝,不再静默截取 20000 行;超过 1 Mi 字符跳过 TextMate,仍可显示、编辑和保存完整文本。预览/文件列之间拖动保持检视区总宽不变,在预览最小宽与文件侧栏最小宽之间分配,不沿用打开预览时向主区借宽的 setter。

**文件编辑与行评审。** 同一代码视图支持文本选择、行号选择、复制、撤销/重做和保存,语言服务沿现有 `Forge.Editor` 接入。选中文本或点击行号后显示评论图标,点击图标展开锚定所选末行的浮动评论框,显示行范围与“保存草稿 / 立即发送 / 取消”;草稿复用 `ReviewStore` 持久化,单条发送只包含该评论,批量发送将当前文件的草稿合成一次消息,队列接受成功后才标记已发送。浮层不推开后续代码、不向源文档插入占位文本,自身输入和复制不被编辑器截获。

以下交互边界已向用户提问,当前未获可用答复,暂采用默认项:常用 LSP 交互覆盖诊断、补全、悬停和跳转定义;发送目标固定为文件所属会话,忙时沿既有队列排队,不随当前活动会话切换;草稿允许基于未保存代码,发送前必须保存成功且内容仍对应评论锚点,失败保留草稿并提示;普通文件仍可编辑保存,无所属会话时不启用评审发送。编辑后评论锚点须跟随文本变化,无法重定位时明确显示失效,不把错误行号当成有效评审。

实现状态:原生 TextPresenter 原型不采纳。采用 AvaloniaEdit、浮层及原生 LSP 交互,删除旧文件预览行列表与着色控件;共享 Diff 的词法实现不随之删除。构建通过不替代真实编辑、评论提交、语言交互与 Native AOT 发布验证。

依赖与语言范围(已确认):AvaloniaEdit 的配套额外依赖进入白名单,允许使用其 TextMate 集成及语法包,不再要求仅接入自有词法实现。语言范围按所采用方案实际支持的语法提供;无可用语法的文本仍可编辑,明确使用纯文本,不删改源文字符。LSP 能力按已安装并配置的服务器提供,不把语法着色支持宣传为语言服务器已就绪。

语言交互边界:使用原生补全窗口、诊断波浪线、悬停文本与 F12/Ctrl(或 Cmd)点击定义跳转。初版补全由 Ctrl+Space 或 `.` 触发,只跟随第一个定义目标,悬停以纯文本展示;未声明 snippet、insert/replace edit 或 item defaults 支持,不把不支持或非法编辑偷偷映射为文件开头。异步结果须验证文档、版本、会话与绑定代次,分离视图取消请求。跨文件定义沿源文件的工作区与固定评审会话打开,显式无所属会话不得回退到活动会话;已打开目标保持自身归属。

评论提交边界:草稿可基于未保存代码,发送前保存并验证锚点与内容仍一致。队列接受与数据库标记不是跨系统原子事务,不承诺 exactly-once 或崩溃恢复;接受后标记失败必须提示并在本次运行中阻止重复发送,后续提交或重新加载时重试标记。尚未交给运行时的评论消息被用户撤回或所属会话关闭时,恢复为草稿;恢复失败须提示并阻止再次发送,后续提交或重新加载时重试恢复。标记和恢复按同一 ReviewStore 串行化,避免重试覆盖撤回状态。已交给运行时的消息不恢复;应用退出时异步恢复可能尚未完成,仍属于未提供持久队列与崩溃恢复的边界。保存草稿期间禁止取消释放其锚点,避免数据库留下不可见评论。持久草稿未记录基准提交,重载依赖保存的文本锚点;唯一匹配重定位只搜索不超过 20000 行的文件,超过时仍接受原位置精确匹配,失配不得发送。

**停靠条与自动隐藏。** 以区域为单位。仅自动隐藏的边栏在其所在窗口边缘显示 32px 停靠条(`AutoHideStrip`,底色 `DockStripBackgroundBrush`,内侧 1px 分隔线);停靠状态不显示停靠条,宽度也不占用布局,只为自动隐藏区域按边缘计入 `RegionWidthSolver`;两个自动隐藏边栏在同一侧时并排。条上每个 panel 一个竖排标签(文字旋转 90°,从上往下读),标签外侧(靠窗口边缘)一条 3px 指示条:抽屉打开时为 `DockIndicatorIdleBrush`,抽屉关闭时为 `AccentBrush`。点标签 = 开/关抽屉。抽屉贴着停靠条内侧弹出,覆盖在主区上方,点外部或按 Esc 收起。底部组收成主区底边的按钮条。

**区域置换。** 按住任一 panel 标题栏的空白处横向拖,出现三个落点,放下后两区互换,宽度跟着区域走。自动隐藏中的区域不能拖。按钮条位置、抽屉方向、预览组的“靠主区一侧”都由“角色 + 当前顺序”推导。

**配色。** 大面积颜色参考 VS Code AI 窗口:亮色主体白、侧栏/标题栏/停靠条 `#F8F8F8`、分隔线 `#E5E5E5`(左中右区域与 panel 标题栏之间的细线清晰可见)、正文 `#3B3B3B`;暗色主体 `#1F1F1F`、侧栏 `#181818`、分隔线 `#2B2B2B`。强调色仍是护眼绿。主区(会话、子 Agent、预览)用白色 `SurfaceBrush`,两侧边栏 panel 用 `SidebarBrush`(`PanelChrome` 的 `:sidebar`),边栏里的视图根背景透明。图标统一为线条风格:`LineIcon` 以固定 1.25 DIP 笔宽描 24 网格的路径,大小与正文相当(12–14px)。边栏里未自行设定换行的 `TextBlock` 单行按字符省略,被截断时由 `TrimmedTextToolTip` 自动挂上全文 tooltip。

两侧面板标题另用 `SidebarTitleBarBrush`:亮色 `#EFEFEF`、暗色 `#101010`,与内容 `SidebarBrush` 区分;主区标题配色保持不变。

**浮动窗口。** 只允许浮出选项卡(任意组的任意选项卡,含 TODO/评审/规格)和文件 panel;其他 panel 不能浮出。
- 文件 panel 浮出时,预览组随它进同一个窗口(仍在其左侧),回位时一起回。
- 文件 panel 给出的是会话相对路径;打开前统一按活动会话工作目录解析成绝对路径,`open-tabs` 里也存绝对路径,恢复时不依赖当时的活动会话。
- 一个对象一个 `FloatingPanelWindow`,用同一视图模型重新套数据模板(视图内的滚动等临时状态不保留)。
- 浮出的选项卡仍留在原组的集合里,只标记浮动、不显示,所以点“停靠”或关窗时天然回到原槽位,不用另记“家”;组里活动选项卡永远不是浮动的。
- 浮动默认尺寸 900×640(文件 panel 320×420)。文件 panel 的浮动窗口标题栏同样带它的工具按钮。最小化期间不写回几何(否则存下 -32000 坐标)。
- 关停:主窗口确认真正关闭后才置 `IsShuttingDown`(关闭被取消就复位),之后关浮动窗口不回位,浮动项照常进快照。
- 浮动窗口与主窗口一样设 `ExtendClientAreaToDecorationsHint` + `ExtendClientAreaTitleBarHeightHint=-1`(见“窗口顶部”),否则顶部留一道白带。
- 浮动窗口整体带 1px 边框(`BorderBrush`):`BorderOnly` 装饰下系统边框几乎不可见,靠它把浮出面板与背景分开。
- 浮出窗口的标题条可拖动窗口、双击最大化/还原:热区只有它自己那条 28 px 标题条(文件 panel 窗口里是右侧那条),左侧预览组的选项卡条仍归选项卡手势;按在工具按钮/停靠按钮/输入框上不拖动。最大化沿用 `FloatGeometry.IsMaximized` 持久化,不新增状态。

**窗口顶部。** `WindowDecorations="BorderOnly"` + `ExtendClientAreaToDecorationsHint="True"` + `ExtendClientAreaTitleBarHeightHint="-1"`:保留系统可缩放边框、去掉系统标题栏,且客户区一直铺到窗口上边缘(不设扩展提示时 Windows 会在顶部留一条系统框高的白带,实测 6.5 px)。标题行由 App 自绘,故窗口底色 = `SurfaceBackgroundBrush`、无独立标题栏底色。标题行 40 px:左 Forge + 会话标题(其后灰显当前分支,无分支即隐藏),中 380×26 占位搜索框(无行为),右 设置/最小化/最大化/关闭(设置紧挨最小化左侧);提供方状态移到状态栏。搜索框背景比窗口亮一档、边框 1 px 且比窗口暗一档(亮/暗各一对令牌)。标题行可拖动、双击最大化,与浮出窗口共用 `Workbench/Controls/WindowCaption.cs` 的同一套按下分类(`CaptionPress`:按钮/输入框上的按下不拖动,双击最大化,其余左键按下移动;三连击按移动处理)——自绘标题栏的两处窗口只有这一份规则,`MainWindow` 与 `FloatingPanelWindow` 都只转调它,`ToggleMaximize` 按钮同路。

**交互终端(`Terminal/**`)。** 终端是底部组里的选项卡(`TabKind.Terminal`,id 前缀 `terminal:`),可开多个,也可浮出。工作目录取会话的 worktree。
- **VT 引擎(`Terminal/Vt/`)**:纯 BCL、不依赖 Avalonia,AOT 无警告。包括 DEC ANSI 解析器(UTF-8 跨分块续接,非法字节输出 U+FFFD)、主屏和备用屏、回滚环(偏好 `TerminalScrollbackLines`,1000–100000,默认 10000)、SGR 16/256/truecolor、DECSET/DECRST(含鼠标 1000/1002/1003/1006、2004 括号粘贴、2026 同步输出)、OSC 0/1/2 标题、OSC 8 链接、OSC 52 只写剪贴板、OSC 10/11 按主题回答,以及 xterm 按键与 SGR 鼠标编码、East Asian Width 宽字符。缩放时不重排行。实测吞吐约 99 MiB/s(20 MiB 混合文本),稳态滚动零分配。**已记录偏差**:在整屏顶行执行 `CSI M` 时,被删的行会进回滚(xterm 不会),这是为了保证不丢内容。
- **线程**:每个终端一个后台泵,读 `Output`,持锁喂仿真器,置脏标志;UI 按帧合并刷新,UI 线程从不解析。键盘输入和泵线程产生的设备应答(DA/DSR 等)走**同一个有序通道**,由单个写任务送达 Host,保证管道内有序。
- **`TerminalControl`**:自绘,每行生成 `FormattedText` 并按行版本号缓存。支持键盘与 IME 输入;复制粘贴为 Ctrl+Shift+C/V,macOS 用 Cmd;粘贴前去掉转义字符,启用 2004 时加括号包裹。支持拖选、双击选词、三击选行(开启鼠标上报时按住 Shift 仍是本地选择);滚轮在开启上报时发给程序,在备用屏发方向键,否则滚动回滚区。Ctrl+点击 OSC 8 链接只放行 http/https。尺寸变化防抖 50 ms 后同步给 Host。视口锚定在回滚内容上,新输出不会把正在查看的历史顶走。配色来自 `TerminalPalette`(亮暗各一套),字体跟随“代码字体”偏好。
- **`TerminalViewModel`**:标题取 OSC 标题,没有时为“终端 n”。状态分启动中、运行、已退出(退出码)、失败(原因)。退出后标签保留,画面末尾附一行“进程已退出”。Host 未运行时直接显示失败状态,不抛异常。视图先于会话就绪布局时,先记下尺寸,会话就绪后补发。
- **`TerminalWorkbench` 协调器**:按会话 id 持有终端。新建时取会话的 `WorkDir` 和偏好里的 shell 与参数(参数按空格切分,双引号分组)。切换会话时,其它会话**停靠在任意组**里的终端标签被移出、但不结束,切回时放回底部组;浮出的标签留在自己的窗口里。会话归档时结束该会话的全部终端;App 退出时,先于组合根拆除结束全部终端,限时等待。关闭标签时如果有前台进程,不弹对话框,而是在终端视图里显示横幅,让用户选“结束并关闭”或“取消”。
- **工作台接线**:`WorkbenchLayout.TabCloseRequested` 是一个**单值** `Func<…, Task<bool>>` 属性,不是事件,因为多播委托只会留下最后一个返回值;`PanelTabStrip` 的关闭改走它。终端标签不写进 `open-tabs` 快照。入口有三个:Ctrl+`、状态栏的“终端”按钮、底部组标题栏的“+”。文案表是 `Localization/Strings.Terminal.cs`(键名前缀 `terminal.`)。
- **设置分区未注册**(用户决定):注册会和 `SettingsPanelTests` 对分区列表的硬性断言冲突。`TerminalSettingsViewModel` 及其模板已写好,但没有接线;shell、参数和回滚行数目前只能经偏好文件修改。
- **已知遗留**:浮出的终端标签仍会写进 `LayoutSnapshot.Floating`(恢复时被丢弃,无副作用);关闭被否决时,浮出的标签不会提到最前。延后项:缩放重排行、缓冲区内搜索、OSC 133、重启后恢复、已退出终端的“重新启动”、图片协议、双向文本、macOS 实机验证。

**审批队列与 Agent 扩展(`Approval/`)。**
- `ApprovalQueue` 归 UI 线程,条目是工具调用卡或插件审批卡;主窗口右下角非模态叠加层(约 420 px、可折叠,每张卡标注来源会话),不阻塞其它会话;有待审批项时会话标签与状态栏显示计数。
- `SessionApprovalHandler : IApprovalHandler` 每个主会话一个(注入 `AgentHost.ApprovalHandler`):先查记住规则,命中即 `Allow`;否则入队等待;`ct` 取消或会话释放时撤卡。
- 记住规则 `ApprovalRules`:键 = 工具名 + 副作用 + 作用域(会话/项目/全局),只记“允许”。会话级只在内存;项目级与永久写 `<数据根>/approval-rules.json`(源生成 JSON、`AtomicFile`、后台线程、写入串行且快照在写锁内取),不放进 `AppPreferences`。项目 id 取自 `ISessionCatalog`,常规会话归 General。文件损坏一律当作无规则并备份 `.bak`。`extension_delete`、插件审批卡、插件命令永不可记住;插件新版本获批时清除其新旧工具名下的全部记住项。
- 策略:`extension_list/create/update/disable` 为 `Allow`(只写暂存区,真正的闸门是插件卡),`extension_delete` 走 `Ask`(即删除确认)。
- `ExtensionAuthoringService` 实现 Core 的 `IExtensionAuthoring`:暂存 → 有诊断则直接把诊断返给 Agent(不入队)→ 插件卡入队 → 批准时把卡上展示的哈希原样传给 `extensions.commit`、拒绝则 `extensions.discard`。插件卡新建显示全文、更新显示与线上版本的逐文件 Diff,另示权限、依赖、三种哈希与诊断;只有“批准/拒绝”。重启后 `extensions.pending` 的项以“重启前提交”卡出现,不阻塞任何 Agent。`SessionRuntime` 订阅 `ToolsChanged` 并调用 `AgentHost.ReplaceExternalTools` 热加载。
- 设置分区 `extensions`(`SettingsSectionIds.Extensions`):已安装扩展(启用/禁用/删除,删除二次确认;enable 只在这里提供)、待审批项、按作用域分组的记住规则(逐条撤销)。文案在 `Localization/Strings.Extensions.cs`(中英)。
- 插件/工作流的 `host.tool_call` 经 `SessionToolCallRouter`(`Approval/`)按 run 注册会话路由:工具取自该会话 `AgentHost.Tools`(内置+外部),策略用与 agent loop 相同的 `SessionPolicy`,Ask 走该会话的 `SessionApprovalHandler`(记住的规则与卡片会话均正确),未知/已释放会话拒绝;五个 `extension_*` 工具对插件永不可解析、永不放行(它们的策略是"免问",真正的门是插件卡)。`SessionRuntime` 在构建 host 时注册、释放时注销;`ExtensionHostSupervisor.Create` 接收路由器。
- 子 Agent 的 `host.tool_call`/`run_code` 内部调用:其 `ToolContext.SessionId` 是子会话 id,但子 Agent 与根共用 AgentHost。`SessionRuntime.RunTurnAsync` 消费事件时,`AgentSubAgentStarted`(任意深度)把 `ChildSessionId` 登记为根 scope 的别名,`AgentSubAgentFinished` 或运行时释放时注销(释放时清掉全部别名,释放后不再登记)。子调用按根的工具集解析(变更仍须审批卡;`extension_*` 仍不可达),卡片显示根会话(与 Core 一致)。别名登记发生在事件被消费时,先于子 Agent 的首次模型往返,实际不会落后于其首个工具调用。
- 插件命令审批的 `ToolName` 是 `plugin-command:<pluginId>:<name>`(`ExtensionHostClient.PluginCommandApprovalName`):模型侧工具名只含字母数字、`_`、`-`,不含 `:`,因此永远不会与内置/外部工具名相等,策略 Allow 规则与"始终允许"都碰不到它。插件命令卡不可记住(`ApprovalRules.IsRememberable` 为 false,无记住按钮,`Allow` 也拒绝入库),每次调用单独确认;卡片标题"插件命令审批"。
- 同一会话可同时显示多张工具卡:Core 的 approvalGate 只串行 agent loop 自己的调用,路由器进来的插件/工作流调用不经过它。每次请求有自己的卡与等待,决定和取消只影响自己的卡(`SessionApprovalHandler`/`ApprovalQueue` 按卡片实例工作)。不加跨组件闸门:agent loop 持着工具调用等插件步骤时再加闸会死锁。
- 插件卡无用户决定的撤回(会话释放、关闭、取消、卡片 Dispose)产出独立的 `Withdrawn`(≠ Deny,映射到 Core `ExtensionSubmitOutcome.Withdrawn` 而非 `Invalid`):不 Discard 暂存稿,稿件留待 `extensions.pending` 重启后作为"重启前提交"卡出现;给 agent 的文本如实说"已撤回、草稿保留待定、不是拒绝"。工具卡的撤回仍是 Deny。
- 提交时先(加载规则后)清除旧/新工具名的已记住规则,再提交,提交后按实际注册名再清一次;提交失败时规则保持已清除。`approval-rules.json` 的 `rules: null`/缺失视为损坏文件(无规则 + `.bak`),加载任务永不 Faulted,`RequestAsync` 对加载异常降级为"询问"。
- 工作流扩展卡显示声明的工具(名称/描述/输入 schema)。

## 未决

- 文件预览已支持编辑保存、TextMate 预览着色及 LSP 文档同步;编辑模式尚为无着色 TextBox,LSP 诊断、语义 token 叠加与补全尚未接到 UI。评审面板仍只读,逐行审阅的行评论交互与声明式插件面板渲染未做。
- git 写工具(`git_branch`/`git_sync`)按工具名划为写操作,经会话的审批队列逐次询问;读工具自动放行,且 git 工具不暴露给 `run_code` 脚本(脚本内调用不经逐次审批呈现)。
- 搜索框暂无行为(占位)。
- `TurnCheckpointService` 是否上移到 `forge-workspace`(目前只做编排)未定。
- Worker 资源限制受威胁模型决策门控,不在此模块实现。
- `ReviewStore`(Forge.Workspace)不可释放:`reviews.db` 的池化句柄是靠 `SqliteSessionStore` 拆除时进程级 `ClearAllPools` 才释放的(`CompositionDisposeTests` 钉住)。拆除顺序或那次清池一变就会回退;给 `ReviewStore` 加 `IDisposable` 属于 forge-workspace,未做。
