---
id: task-ui-language
type: task-spec
status: draft
title: 界面语言可选(中文 / English,即时切换)
parent: forge-app
---

# 界面语言可选(中文 / English,即时切换)

## Request

goal:界面语言可选。用户决定(2026-11-05):**中文 + English,切换后即时生效**;默认跟随系统。

## Product(已确认)

- 设置分区“语言”(`language`):跟随系统 / 中文 / English。
- 切换后所有可见文案立刻刷新,包括已打开的会话卡片头、状态栏、标题栏按钮提示、右键菜单、空态文案。
- 模型对话内容、用户输入、文件内容不翻译。

## 已细化(2026-11-05 用户确认)

- **格式**:句式随语言,数字与时间不随区域——千分位一律 `,`(`N0` 不变文化),时间一律 24 小时 `HH:mm:ss`,耗时中文“4.2 秒 / 3 分 05 秒”、英文“4.2s / 3m 05s”。
- **术语表**:主 Agent 起草(见 Design),随 Design 一起审。
- **插件文案**不参与本地化,原样显示;只本地化 Forge 自己的外框文案。
- **跟随系统**:保留 `InvariantGlobalization`;Windows 读注册表,其它平台读 `LANG`/`LC_ALL`。
- **越界许可**:`Localization/Loc.cs`(仅系统语言检测)、新增“语言”设置分区(新文件 + `AppComposition.cs`/`App.axaml` 追加)、非视图文件里用户可见的诊断文案、断言中文文案的现有测试。

## Design

### 文案表与键

按界面区域分五张表,每张一个文件、一个类,键带前缀,全部在 `[ModuleInitializer]` 注册;`CoreStrings`(`settings.*`)不动。

| 文件 | 类 | 前缀 | 覆盖 |
|---|---|---|---|
| `Strings.Shell.cs` | `ShellStrings` | `shell.` `workbench.` `status.` `provider.` `diag.` | 标题栏、窗口按钮、确认框、工作台面板/选项卡标题与标题栏按钮提示、选项卡右键菜单、状态栏、提供方状态、启动诊断 |
| `Strings.Projects.cs` | `ProjectStrings` | `project.` | 项目区、右键菜单、添加项目/初始化仓库对话框、会话默认标题 |
| `Strings.Session.cs` | `SessionStrings` | `session.` `turn.` `subagent.` | 会话视图、轮次卡、工具行、通知、子 Agent 标签与“已结束” |
| `Strings.Inspector.cs` | `InspectorStrings` | `files.` `changes.` `todo.` `review.` `spec.` | 文件 panel、预览、改动、TODO、评审、规格树 |
| `Strings.Language.cs` | `LanguageStrings` | `language.` | 语言分区 |

- 每张表除键常量外暴露**类型化访问器**:无参文案是 `static string X => Loc.Instance[Key]`,带参数的是方法(内部 `Loc.Instance.Format`)。调用点从 `Strings.X` 改成 `<表>.X`;`ViewModels/Strings.cs` 在最后一批删除,`Strings.Number` 等纯格式函数移到 `Localization/TextFormat.cs`。
- 同一句话在两个区域出现时各自注册各自的键(例如工作台选项卡标题“评审”归 Shell,评审 panel 内文案归 Inspector),不跨表引用——五批并行时各表互不依赖。
- **复数**:英文名词按数量变形(`1 file` / `2 files`)。键分 `.one`/`.other` 两条注册(中文两条同文),由 `LocText.Plural(keyBase, count)` 选键;只用于英文确实需要变形的地方。
- 占位符 `{0}{1}…` 在中英两份里必须一一对应;数字参数由调用方先经 `TextFormat.Number` 格式化成字符串再传入,保证千分位规则统一。

### 刷新机制(切换即时生效)

切换语言极少发生,允许 O(n) 全量刷新;但平时零开销、不能泄漏。

1. **XAML 静态文案**(按钮文字、提示、占位符、`AutomationProperties.Name`、菜单头)直接绑 `{Binding [key], Source={x:Static loc:Loc.Instance}}`。`AutomationId` 不变,UI 自动化与测试只认它。
2. **视图模型计算出的文本**(轮次卡头、状态栏、摘要计数、提示):文本属性在 getter 里从状态现算(不缓存成字段),刷新时只需重新抬 `PropertyChanged`。
3. **只有带 `Dispose` 的长生命周期所有者订阅 `Loc.LanguageChanged`**(`MainWindowViewModel`、`StatusBarViewModel`、`SessionWorkspaceViewModel`、各 panel 视图模型、`ProjectTreeViewModel`、`WorkbenchLayout`),在 `Dispose` 里退订,并向下逐个调用子项的 `RefreshText()`(轮次卡、工具行、通知、树行、子 Agent 标签)。逐行/逐卡的视图模型**不得**订阅静态事件——否则每张卡一个强引用,会话关闭后也回收不掉。
4. **存下来以后再显示的文本**(启动诊断、提供方状态、会话通知、轮次脚注、被拒绝的工具)存成 `LocText`(键 + 参数),显示时才解析;不再存解析好的字符串。异常消息本身(BCL/OS 给的 `exception.Message`)作为参数原样插入,不翻译。
5. **代码里建的控件文本**(选项卡右键菜单、标题栏图标按钮提示、浮动窗口“停靠”按钮、最大化/还原提示):控件在挂到可视树时订阅、离开可视树时退订,或在菜单 `Opening` 时现取文本。
6. **工作台标题**:`PanelModel`/`TabModel` 记下标题键;`WorkbenchLayout` 收到 `LanguageChanged` 时重设固定 panel/选项卡的标题。会话、文件选项卡的标题是数据,不翻译。

不翻译的东西:模型输出、用户输入、文件内容与路径、插件给的工具名与描述、会话标题(默认标题“常规 11-05 12:30”/“General 11-05 12:30”按**创建时**的语言生成并存库,之后就是用户数据)、开发者才看得到的异常文本(改成英文,例如“设置分区 id 重复”)。

### 跟随系统

`Loc.SystemLanguage` 改成调用一个纯函数 `SystemLanguageDetector.Resolve(...)`(新文件 `Localization/SystemLanguageDetector.cs`,输入全部显式传入,便于测试):

1. `CurrentUICulture` / `InstalledUICulture` 是 `zh*` → 中文(不变全球化下二者为空名,一般跳过)。
2. Windows(`OperatingSystem.IsWindows()` 保护,`Microsoft.Win32.Registry` 属于 BCL,AOT 可用,无 P/Invoke):依次读 `HKCU\Control Panel\Desktop` 的 `PreferredUILanguages`(用户改过显示语言时才有)、`HKCU\Control Panel\Desktop\MuiCached` 的 `MachinePreferredUILanguages`、`HKCU\Control Panel\International` 的 `LocaleName`,第一个存在的值决定结果。Windows 上注册表优先于环境变量——Git Bash 会把 `LANG` 设成 `en_US.UTF-8`,从那里启动的中文 Windows 不该变成英文。读失败(权限、IO)当作不存在。
3. 再看 `LC_ALL` → `LC_MESSAGES` → `LANG`。
4. 都没有 → 英文。

macOS 从 Dock 启动时通常没有 `LANG`,会落到英文——已知限制,未验证。

### 语言设置分区

`Localization/LanguageSettingsViewModel.cs`:三个单选(“跟随系统 / Follow system”、“中文”、“English”——语言名用其本身书写,不随界面语言变),选中值双向写 `AppPreferences.Language`;组合根已有的 `OnPreferencesChanged` 负责推给 `Loc`,偏好由 `PreferencesStore` 防抖落盘。“跟随系统”下附一行灰字说明当前解析成了哪种语言。在 `AppComposition.Create()` 里 `Settings.Add(new SettingsSection(SettingsSectionIds.Language, LanguageStrings.SectionTitle, SettingsSectionIds.LanguageOrder, vm))`,`App.axaml` 追加一个 `DataTemplate`。

### 防回归

`tests/Forge.App.Tests/Localization/`:

- **扫描测试**:扫 `src/Forge.App/**/*.cs` 与 `*.axaml`(排除 `bin`/`obj` 与 `Localization/Strings.*.cs`),去掉 C# 注释(`//`、`/* */`)与 XAML 注释(`<!-- -->`)后,字符串字面量 / 属性值 / 文本节点里不得出现中日韩字符(CJK 统一表意文字、CJK 标点、全角形式)。白名单为空;以后确需例外,写成“文件 + 原文”精确匹配并注明原因。
- **表完整性**:每个键中英都有、英文不含中日韩字符、两边占位符集合相同;`.axaml` 里出现的每个 `[key]` 都已注册。
- **格式**:中英各一组固定样例(轮次卡头、耗时、计数、复数、`—` 未上报)。
- **系统语言检测**:`SystemLanguageDetector.Resolve` 的分支矩阵(注册表优先于 `LANG`、`zh_CN.UTF-8`/`zh-Hans`/`zh` 识别、`zhx` 不误判、全空 → 英文)。
- **即时切换(headless,独占会话)**:打开窗口和一个有轮次卡的会话,中 → 英切换后断言标题栏按钮提示、状态栏、轮次卡头、选项卡右键菜单、空态文案、设置分区标题全部变成英文,再切回中文。

原 `tests/Forge.App.Tests/StringsTests.cs` 迁入 `Localization/` 并改用新访问器;其它测试里断言中文文本的地方先把语言固定为中文。

### 分批与分工

| 批 | 内容 | 方式 |
|---|---|---|
| 0 | 主 Agent:`LocText`/`TextFormat`/`SystemLanguageDetector` 契约与五张表的空骨架;本 Design | 主 Agent |
| 1 | `Loc.cs` 接检测器、Windows 注册表实现、语言分区、检测器测试 | Worker + 测试 |
| 2a–2d | Shell、Projects、Session、Inspector 四块迁移,各自只碰自己的表和自己的视图/视图模型文件,不改 `ViewModels/Strings.cs` | 4 个 Worker 并行 |
| 3 | 删 `ViewModels/Strings.cs`、迁 `StringsTests`、扫描测试 + 完整性测试 + 即时切换测试 | Worker + 测试 |
| — | 每批评审(Review),通过后 rebase 到 `main` 再提交;最后更新 forge-app SPEC 并删除本文件 | 评审 + 主 Agent |

### 英文术语表(待审)

| 中文 | English | 中文 | English |
|---|---|---|---|
| 会话 | Session | 常规对话 | General chat |
| 项目 | Project | worktree | Worktree |
| 项目区 / 主区 / 检视区 | Projects / Main / Inspector | 预览 | Preview |
| 改动 / 已改动 / 全部 | Changes / Changed / All | 未提交改动 / 按轮次 | Uncommitted / By turn |
| 主 Agent / 子 Agent | Main agent / Sub-agent | 已结束 | Finished |
| 第 N 轮 | Turn N | 输入 / 输出(token) | in / out |
| 规格 / 规格树 | Spec / Spec tree | 评审 / 意见 | Review / Comment |
| TODO | To-do | 归档 / 历史 | Archive / History |
| 扩展宿主 / 插件 / 工具 | Extension host / Plugin / Tool | 审批 | Approval |
| 模型提供方 | Model provider | 密钥 | API key |
| 停靠 / 自动隐藏 | Dock / Auto-hide | 在新窗口打开 | Open in new window |
| 铺满主区 / 退出铺满 | Cover main area / Exit cover | 放大 / 还原 | Maximize / Restore |
| 检查点 | Checkpoint | 已转向 / 后续指令 | Steered / Follow-up |
| 运行中 / 排队中 / 已停止 / 失败 / 完成 | Running / Queued / Stopped / Failed / Done | 已拒绝 | Denied |

## 设计提示

- **跟随系统目前恒为英文**:`Directory.Build.props` 开着 `InvariantGlobalization`,`CurrentUICulture` 是不变文化;底座已用 `InstalledUICulture` 与 `LANG`/`LC_ALL` 兜底。是否关闭不变全球化(会增大 AOT 体积并引入 ICU)由本任务向用户确认后决定。
- XAML 直接绑全局 `Loc` 时注意:已关闭窗口的绑定在切换语言时可能跨线程收到通知(底座已逐订阅者隔离异常);长生命周期的视图优先绑视图模型属性。

- 底座 `Loc` 已提供实时切换与按功能分表;本任务把现有 `src/Forge.App/ViewModels/Strings.cs`(约 200 条)和 XAML 里的硬编码中文迁到 `Localization/Strings.*.cs` 表,补英文。
- 视图模型里缓存的格式化文本要订阅 `Loc.LanguageChanged` 重算;XAML 用 `{Binding [Key], Source={x:Static loc:Loc.Instance}}`。
- 加一个测试扫描 `.axaml` 与 `.cs`,禁止新增未走 `Loc` 的中文字面量(白名单极少数例外)。

## 归属路径

`src/Forge.App/ViewModels/Strings.cs`(迁移后删除或只留转发)、`src/Forge.App/Localization/Strings.*.cs`(**不改**其它功能自建的表,只新增迁移用的表)、引用旧 `Strings` 的视图与视图模型、`tests/Forge.App.Tests/Localization/**`;SPEC:forge-app“字符串与自动化”段。

经用户许可追加(2026-11-05):`Localization/Loc.cs`(仅系统语言检测)、`Localization/` 下新文件(`LocText.cs`、`TextFormat.cs`、`SystemLanguageDetector.cs`、`LanguageSettingsViewModel.cs`)、`AppComposition.cs`/`App.axaml` 的追加注册、`Composition/*`、`Preferences/AppPreferences.cs`、`Services/WorktreeService.cs`、`MainWindow.axaml(.cs)`、`Workbench/**`、`Resources/Workbench.axaml` 里的用户可见文案,以及断言中文文案的现有测试。

## 注意

迁移面大、和其它四个任务都会碰到视图文件。按模块分批迁移、每批单独提交并尽快合入;遇到别的任务刚改过的文件先 `git pull --rebase`。

## 共同前提(所有五个任务)

- **底座已合入**(`19c97d8`):偏好 `AppPreferences`/`PreferencesStore`、实时文案 `Loc`、设置分区 `SettingsSections`(`src/Forge.App/Preferences/`、`Localization/`)。用法与坑以 forge-app SPEC“偏好、文案与设置分区”为准:分区在 `AppComposition.Create()` 里向 `composition.Settings` 追加并在 `App.axaml` 加模板;文案表用 `[ModuleInitializer]` 自注册;视图模型里缓存的文本订阅 `Loc.LanguageChanged`。
- **新文案**一律注册到本功能自己的表 `src/Forge.App/Localization/Strings.<功能>.cs`(中英两份),不要改 `ViewModels/Strings.cs`,也不要改别的功能的表。
- **设置项**放在本功能自己的设置分区(`SettingsSectionIds` 里预留了 id 与顺序),不要改设置页本身。
- **并行**:五个任务在不同对话里同时进行。只改本 spec“归属路径”列出的路径;需要越界时先停下问用户。共享文件(`AppComposition.cs`、`App.axaml`)只做“追加注册”这类最小改动,提交前 `git pull --rebase`。
- **本文件位置**:`docs/tasks/`(随仓库提交,新 worktree 自带);任务完成后由本任务删除它。
- **流程**:按 brainstorming 工作流进行;本 spec 的 Product 是已确认部分,“待细化”的问题在本对话里向用户问清再定 Design。分工与模型按 `AGENTS.md`。
