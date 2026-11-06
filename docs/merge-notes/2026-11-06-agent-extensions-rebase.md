# 审批队列 + Agent 扩展任务 rebase 到 main 的冲突记录(2026-11-06)

分支 `workspace-5` 以 `f9a25d7` 为基,rebase 到 `main`(`5f05796`,已合入“界面语言”“终端”“字体”任务)。18 个提交里有 7 个产生文本冲突,均为两边各自追加,处理原则是“两边都保留”。

## 冲突与处理

| 文件 | 冲突 | 处理 |
|---|---|---|
| `src/Forge.HostClient/ExtensionHostClient.cs`(类声明,出现两次) | main 把类改成 `partial` 并实现 `ITerminalHost`;本任务追加 `IExtensionAuthoringClient` | 合并为 `public sealed partial class ExtensionHostClient : IExtensionContributionClient, ITerminalHost, IExtensionAuthoringClient`。 |
| `src/Forge.HostClient/SPEC.md` | main 在“权限校验”条目后追加“终端”条目;本任务在同处续写权限条目并追加“扩展写入”条目 | 本任务的续行紧接权限条目在前(它们是同一条的续行),终端条目在后。 |
| `src/Forge.ExtensionHost/ExtensionHost.cs`(请求分发 `switch`) | main 追加 `terminal.*` 四个 case;本任务追加 `extensions.*`/`plugin.disable/enable/delete` 七个 case | 两组 case 都保留,终端在前。 |
| `src/Forge.App/App.axaml` | main 加 `xmlns:appearance`,本任务加 `xmlns:ap` | 两个都保留。 |
| `src/Forge.App/Composition/AppComposition.cs`(`using`) | `using Forge.App.Appearance;` 与 `using Forge.App.Approval;` | 两个都保留。 |
| `src/Forge.App/Composition/AppComposition.cs`(Host 创建处) | main 把 `hostProblem` 改为 `LocText`;本任务在此新建 `SessionToolCallRouter` | 保留 main 的 `var hostProblem = default(LocText);`,并在它前面创建路由器。 |
| `tests/Forge.App.Tests/Foundation/SettingsPanelTests.cs` | main(字体任务)采用“provider 在首、paths 在尾”的宽松断言与全部预留 id;本任务曾改为精确的 `[provider, extensions, paths]` | 采用 main 的宽松断言与 id 列表(其中已含 `extensions`),另加一行 `Assert.Contains(SettingsSectionIds.Extensions, rendered)`。 |
| `src/Forge.App/SPEC.md` | main 在“关键决策”末尾追加“交互终端”段,并从“未决”删掉“命令行区未做”;本任务在同处追加“审批队列与 Agent 扩展”段,并删掉“未决”里的“审批对话框” | 终端段在前、审批段在后;“未决”取 main 的版本,再去掉“审批对话框”。 |

## rebase 后的适配(语义冲突,git 未报)

- main 把 Host 状态问题从 `string` 改成 `LocText`(`Action<HostStatus, LocText?>`、`JitHostLayout.Locate(out LocText)`)。`ApprovalServices.Create`/`OnHostStatusChanged` 改收 `LocText`,对外的 `HostProblem`/`StatusDetail` 每次读取时按当前语言解析,所以切换语言后会跟着变。
- main 新增的 `NoHardcodedChineseTests` 查出审批代码里有 17 处硬编码中文:两份 axaml 里的 10 个 `AutomationProperties.Name` 改为绑定已有的 `Extensions.*` 文案;`ApprovalRules` 的 7 条规则文件问题消息改成 `LocText`(新增 `Extensions.Rules.*` 七个键,中英两份)。
- `StringTableTests.Every_key_carries_a_known_feature_prefix` 的前缀白名单里追加了 `Extensions`(最小追加,与终端、字体任务的处理相同)。

## 验证

`dotnet build Forge.slnx` 0 警告 0 错误;`dotnet test Forge.slnx` 所有项目全绿(App 1055、Integration 263、HostClient 486、Core 473 等)。随后 `main` 快进到 `workspace-5`。

## 过程中的一次失误

第一次 rebase 时,自写的冲突解析脚本没有处理 CRLF 行尾,带着冲突标记的文件被 `git add` 了。发现后立即 `git rebase --abort` 回到 rebase 前的状态,修好脚本(兼容 `\r\n`,解析失败即非零退出,不再继续)后重新 rebase。最终树里没有冲突标记(已 grep 校验)。
