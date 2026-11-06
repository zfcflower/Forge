# 终端任务 rebase 到 main 的冲突记录(2026-11-06)

分支 `implement-terminal-task` 以 `db0d1dd` 为基,rebase 到 `main`(`bfa286a`,已合入“界面语言”任务)时产生的冲突与处理。

## 冲突与处理

| 文件 | 冲突 | 处理 |
|---|---|---|
| `src/Forge.App/App.axaml` | 两边各在根元素上追加了一个 xmlns(`loc:` 与 `term:`) | 两者都保留。 |
| `src/Forge.App/ViewModels/MainWindowViewModel.cs` — using | main 去掉了冗余 using,终端加了 `using Forge.App.Terminal;` | 保留终端的 using。 |
| 同上 — 构造函数 | main 把“打开改动”工具按钮改成字段 `openChangesTool` 以便换语言时替换;终端在同处构造 `TerminalWorkbench` 与“新建终端”按钮 | 两者都保留,终端两行放在前面。 |
| 同上 — `OnLanguageChanged` | 两边各自新增了同名处理器,也都订阅了 `Loc.Instance.LanguageChanged`(合并后会重复订阅、重名编译失败) | 只保留 main 的处理器(带 UI 线程守卫),把终端那三步(重建“新建终端”按钮、重排底部组工具、刷新状态栏按钮文本)并进去;删掉终端的重复订阅与 `Dispose` 里重复的退订。 |
| 同上 — `ScheduleLayoutSave` | main 加了 `if (disposed) return;`,终端修复提交加了 `if (closing) return;` | 合为 `if (disposed \|\| closing)`:两个条件分别覆盖拆除后和拆除进行中。 |

## rebase 后的适配(语义冲突,git 未报)

- main 把 `StatusBarViewModel.SetWorkspaceProblem` 的参数从 `string` 改成了 `LocText`(换语言时状态栏文本跟着变)。终端的“没有会话”提示改为传 `new LocText(TerminalStrings.FailureNoSession)`,行为更好:语言切换后也会更新。
- main 新增的 `StringTableTests.Every_key_carries_a_known_feature_prefix` 硬编码了功能前缀白名单。终端 task-spec 规定文案键前缀为 `terminal.`,所以把 `terminal` 加进白名单(该测试文件不在终端归属路径内,这是合并所需的最小追加)。

## 验证

rebase 并适配后 `dotnet build Forge.slnx` 0 警告 0 错误;App/HostClient/Ipc 测试全绿。集成测试里 4 个终端用例失败,查明与 rebase 无关:见同目录 `2026-11-06-terminal-store-pwsh.md`。
