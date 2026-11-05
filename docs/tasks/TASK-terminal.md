---
id: task-terminal
type: task-spec
status: draft
title: 可独立打开的交互终端(PTY 在 ExtensionHost)
parent: forge-app
---

# 可独立打开的交互终端(PTY 在 ExtensionHost)

## Request

goal:可独立打开的终端。用户决定(2026-11-05):**完整交互终端**,PTY 放进 ExtensionHost;后续要能在终端里运行其它 Agent CLI,**多选项卡、多终端实例**。

## Product(已确认)

- 终端是主区底部组里的选项卡(与子 Agent 并列,SPEC 已为命令行区预留),可开多个;选项卡可按工作台规则浮出到新窗口。
- 工作目录绑定会话 worktree(architecture:会话与工作区一对一)。
- 支持全屏/交互程序(vim、top、各种 Agent CLI):需要 VT 解析与渲染、键盘(含组合键)、鼠标滚动、复制粘贴、窗口缩放同步。
- 默认 shell 由偏好 `TerminalShell`/`TerminalArguments` 决定(为空走平台默认)。

## 待细化(本对话里问用户)

- 终端是否随会话关闭而结束,还是可脱离会话保留。
- 回滚缓冲行数、字体(是否跟随“代码字体”偏好)、配色是否跟随主题。
- 关闭运行中终端时是否确认。
- VT 支持范围(xterm-256color / truecolor / 鼠标上报 / OSC 8 链接)。

## 契约

- `src/Forge.Abstractions/Terminal.cs`:`ITerminalHost`、`ITerminalSession`、`TerminalStartRequest`、`TerminalSize`(主 Agent 已定稿,本任务实现,不改签名;确需改动先问)。
- Host 侧:PTY(Windows ConPTY、POSIX openpty/forkpty 或 posix_spawn+pty)放在 Host **唯一**的 P/Invoke 内部类(`WorkerProcessOwnership.cs` 所在约束,见 forge-extension-host SPEC 与 architecture)里;这条约束是“只允许 Host 一个内部类做 P/Invoke”,若必须拆第二个类,先改 architecture 并问用户。
- IPC:新增 `terminal.start/input/resize/stop` 请求与 `terminal.output/exit` 通知;字节以 base64 传或二进制帧,遵守 `IpcProtocol.MaxFrameBytes` 并分片;输出背压不能拖垮 Host(有界队列 + 丢弃策略要写进 SPEC)。
- App:`HostClient` 实现 `ITerminalHost`;VT 解析器与终端渲染控件自写(NuGet 白名单外,不引入三方终端库;值得内化的开源实现须先问用户)。

## 归属路径

`src/Forge.ExtensionHost/**` 中终端相关新文件与 dispatch 注册、`src/Forge.HostClient/**` 中终端客户端、`src/Forge.App/Terminal/**`(新目录:VT 解析、渲染控件、视图模型)、`Localization/Strings.Terminal.cs`、设置分区 `terminal`、对应测试目录;SPEC:forge-extension-host、forge-host-client、forge-app、architecture(P/Invoke/IPC 约束如有变化)。

## 注意

“Agent 自建扩展”任务也会改 ExtensionHost/HostClient 的请求分发;两边只追加各自的 op,提交前 rebase,冲突按 op 合并。

## 共同前提(所有五个任务)

- **底座已合入**(`19c97d8`):偏好 `AppPreferences`/`PreferencesStore`、实时文案 `Loc`、设置分区 `SettingsSections`(`src/Forge.App/Preferences/`、`Localization/`)。用法与坑以 forge-app SPEC“偏好、文案与设置分区”为准:分区在 `AppComposition.Create()` 里向 `composition.Settings` 追加并在 `App.axaml` 加模板;文案表用 `[ModuleInitializer]` 自注册;视图模型里缓存的文本订阅 `Loc.LanguageChanged`。
- **新文案**一律注册到本功能自己的表 `src/Forge.App/Localization/Strings.<功能>.cs`(中英两份),不要改 `ViewModels/Strings.cs`,也不要改别的功能的表。
- **设置项**放在本功能自己的设置分区(`SettingsSectionIds` 里预留了 id 与顺序),不要改设置页本身。
- **并行**:五个任务在不同对话里同时进行。只改本 spec“归属路径”列出的路径;需要越界时先停下问用户。共享文件(`AppComposition.cs`、`App.axaml`)只做“追加注册”这类最小改动,提交前 `git pull --rebase`。
- **本文件位置**:`docs/tasks/`(随仓库提交,新 worktree 自带);任务完成后由本任务删除它。
- **流程**:按 brainstorming 工作流进行;本 spec 的 Product 是已确认部分,“待细化”的问题在本对话里向用户问清再定 Design。分工与模型按 `AGENTS.md`。
