# Git 与 PR 工作流任务 rebase 到 main 的冲突记录(2026-11-06)

分支 `workspace-1` 以 `2846799` 为基,批次 ① 分两个提交(Workspace、App)rebase 到 `main`(`5ac019a`,已合入界面语言、终端、字体、审批队列与 Agent 自建扩展)。Workspace 提交无冲突;App 提交冲突与处理如下。

## 冲突与处理

| 文件 | 冲突 | 处理 |
|---|---|---|
| `Preferences/AppPreferences.cs` | 终端加了 scrollback 字段,git 加了拉取策略与网络超时,同处 5 段 | 两边都保留(常量、字段、属性、Load、ToSection、`PreferencesSection` JSON 属性)。 |
| `Workbench/WorkbenchTypes.cs` | `TabKind` 末尾 main 加 `Terminal`,git 加 `Git` | 两者都保留,`Git` 在后(该枚举不持久化序号)。 |
| `App.axaml` | xmlns 与设置分区模板都在同处追加 | 都保留;去掉重复的 `xmlns:loc`;补回扩展分区模板被冲突截断的 `</DataTemplate>`。 |
| `Composition/AppComposition.cs` | main 的 `AgentRuntimeServices` 改为构造后再赋 `Approvals`/`ToolRouter`;git 在构造参数末尾加 `Git: gitTools` | 合并:构造参数带 `Git:`,之后照 main 赋两个属性。 |
| `Agents/SessionRuntime.cs` | main 把工具集改成 `CollectExternalTools()`(热加载时整体替换外部工具);git 加了 `BuildTools()` 把 git 工具接在后面 | 以 main 为准,git 工具并进 `CollectExternalTools()` 末尾——否则插件热加载 `ReplaceExternalTools` 时 git 工具会被整体替换掉。`BuildTools()` 保留为它的别名供测试读。审批规则沿用 main 的 `SessionPolicy`,git 工具不进放行表。 |
| `ViewModels/MainWindowViewModel.cs` | using、构造函数(main 的终端与 `openChangesTool` 字段 vs git 打开 Git 选项卡)、`Dispose`(main 的释放链 vs git 面板退订) | 全部保留;Git 选项卡在终端两行之后打开;`Dispose` 先退订 Git 面板再走 main 的释放链。 |
| `ViewModels/ProjectTree.cs` | main 把 `Strings.*` 换成 `ProjectStrings.*`;git 改写了新建 worktree 会话流程(先问分支名) | 用 git 的流程,把其中的 `Strings.NotAGitRepository()`/`Strings.WorktreeSessionTitle()` 换成 `ProjectStrings` 的对应成员。 |
| `ViewModels/StatusBarViewModel.cs` | main 改为按 key/计数实时计算文本(`LocText`);git 加了 `aheadBehindText` 与 `SetAheadBehind` | 以 main 为准,追加 git 的字段与方法(超前落后文本是 `↑n ↓m`,不随语言变)。 |
| `SPEC.md` | 两处段落各自改写 | 以 main 的段落为准,把 git 的“`git` 设置分区”一句并入;未决里 git 写工具一条改成“经会话审批队列逐次询问”(审批对话框已在 main 落地)。 |
| `tests/.../SettingsPanelTests.cs` | main 已把分区列表改成“所有预留 id” | 用 main 的版本(已含 `Git`)。 |

## rebase 后的适配(语义冲突,git 未报)

- main 删除了 `ViewModels/Strings.cs`。`MainWindowViewModel.ConfirmGitActionAsync` 用的 `Strings.Confirm` 改为 git 自己表里新增的 `git.confirm.button`,取消按钮用 `ShellStrings.Cancel`。
- main 的 `StringTableTests` 有功能前缀白名单与“`.one`/`.other` 成对”规则:把 `git` 加进白名单;`git.hint.other`(“其他失败”的提示,不是复数形式)改名为 `git.hint.unclassified`。
- main 把会话审批策略字段从 `TodoPolicy` 改名为 `SessionPolicy`:`SessionRuntimeGitToolsTests` 里反射读取的字段名随之更新。
- main 让 App 测试的“跟随系统”固定为中文,并规定 `Forge.App.Tests` 不写依赖 XAML 直绑 `Loc` 实时刷新的断言:`GitSettingsSectionTests` 的渲染用例不再切到英文,改为断言中文标签。

## 验证

`dotnet build Forge.slnx` 0 警告 0 错误;全量 13 个测试项目全部通过(App 1129、Workspace 425、Integration 263 ……)。
`AppearanceSettingsViewModelTests` 在单独运行时偶发失败(`Unrelated_preference_changes_do_not_touch_the_editors` 期望 0 次变更、实际 2 次),与本任务无关:失败时并没有运行任何 git 测试,疑为同进程其他测试切换 `Loc` 语言触发的通知。留给字体任务的负责人。
