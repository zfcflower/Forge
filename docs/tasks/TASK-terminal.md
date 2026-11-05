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

## 细化结论(用户 2026-11-05 确认)

- **生命周期**:终端属于会话。关会话标签不结束;会话归档、App 退出时结束;Host 崩溃即结束;重启不恢复。
- **回滚**:默认 10000 行,终端设置分区可改(1000–100000)。
- **字体**:跟随“代码字体”偏好(`CodeFontFamily`/`CodeFontSize`),实时生效。**配色**:跟随亮/暗主题。
- **关闭确认**:仅当 shell 下有前台子进程时确认。
- **VT 范围**:基础能力 + 256 色/truecolor + 鼠标上报(SGR 1006)+ OSC 8 超链接 + OSC 52 剪贴板写入(不允许读)。
- **入口**:Ctrl+` 快捷键、状态栏“终端”按钮、底部组标题栏“+”。
- **背压**:确认窗口流控,不丢字节。
- **已批准的越界最小改动**:`Preferences/AppPreferences.cs` 加 `TerminalScrollbackLines`;`Forge.Abstractions/Terminal.cs` 加 `HasForegroundProcessAsync`;`WorkerProcessOwnership.cs` 类声明加 `partial`;工作台接线(`WorkbenchTypes.cs`、`WorkbenchLayout.cs`、`PanelTabStrip.cs`、`MainWindowViewModel.cs`,以及入口所需的 `MainWindow.axaml` KeyBinding 与状态栏按钮)。都只做追加。

## Design

### 拓扑

```
App: TerminalControl ← TerminalEmulator(后台泵) ← ITerminalSession ── HostClient(ExtensionHostClient : ITerminalHost)
                                                             ║ 主管道 JSON 帧,字节 base64
Host: TerminalManager → TerminalSession(读泵 + 流控窗口) → WorkerProcessOwnership(partial,.Pty.cs:ConPTY / openpty)
```

### 线协议(主管道,沿用 `IpcMessage`;字段 camelCase;`data` 是 base64,解码后 ≤ 64 KiB/帧,远低于 `MaxFrameBytes`)

| op | 方向 | 形状 | 说明 |
|---|---|---|---|
| `terminal.start` | 请求 App→Host | `{sessionId, workingDirectory, columns, rows, shell?, arguments?, environment?}` → `{terminalId, processId, shell}` | 工作目录须为已存在的绝对路径;列 1–1000、行 1–500;错误码 `invalid_request`/`terminal_limit`(每 Host 32 个)/`terminal_start_failed`/`host_stopping` |
| `terminal.input` | 通知 App→Host | `{terminalId, data}` | 管道内有序;未知或已退出的 id 静默忽略。按键走通知,不必每个按键等一次往返 |
| `terminal.resize` | 请求 | `{terminalId, columns, rows}` → `{}` | 未知或已退出的 id 一律视为成功(与退出的竞态无害) |
| `terminal.foreground` | 请求 | `{terminalId}` → `{busy}` | 未知 id 返回 false;Windows 查 Job 里的活动进程数是否 > 1;POSIX 比较 `tcgetpgrp(master)` 与 shell 的进程组 |
| `terminal.stop` | 请求 | `{terminalId}` → `{}` | 杀整棵进程树,先发出 `terminal.exit` 再应答;幂等 |
| `terminal.ack` | 通知 App→Host | `{terminalId, bytes}` | App 已消费的解码字节数 |
| `terminal.output` | 通知 Host→App | `{terminalId, seq, data}` | `seq` 从 0 递增,App 校验连续 |
| `terminal.exit` | 通知 Host→App | `{terminalId, exitCode}` | 该 id 的最后一条消息:输出已发完、进程树已消失。POSIX 被信号杀死时为 128+信号 |

- **op 常量与 DTO** 由 Host(`Forge.ExtensionHost/TerminalProtocol.cs`)和 HostClient(`Forge.HostClient/TerminalProtocol.cs`)各写一份,各自用源生成 JSON。不放进 `Forge.Plugin.Sdk`:终端不是插件 API,也不该抬 SDK 版本。两份是否一致由集成测试端到端钉住。
- **流控**:每个终端最多有 1 MiB 已发未确认的字节;到达上限时 Host 停止读 PTY,于是子进程写入阻塞,和真终端一样。没有丢弃路径:只有终端关闭时,未发送的输出才被丢弃。Host 每个终端的内存上限约为 1 MiB 窗口加一个 64 KiB 读缓冲,一个刷屏的终端也占不满主管道。

### Host(`Forge.ExtensionHost`)

- **新文件**:`TerminalProtocol.cs`、`TerminalManager.cs`(登记表、上限、各 op 处理)、`TerminalSession.cs`(读泵、窗口、退出收口)、`WorkerProcessOwnership.Pty.cs`(原生部分)。`ExtensionHost.DispatchAsync` 只追加 `terminal.*` 分支;通知分支只追加 `terminal.input`/`terminal.ack`。
- **Windows**:先 `CreatePipe` ×2、`CreatePseudoConsole`,再用 `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 加 `CREATE_SUSPENDED` 调 `CreateProcessW`,然后分配进私有 Job(KILL_ON_JOB_CLOSE),最后才 `ResumeThread`:进程在被 Job 收容之前不会执行代码。缩放用 `ResizePseudoConsole`。关闭时 `ClosePseudoConsole` 期间要继续排空输出管道(旧版 Windows 不排空会死锁)。前台检测用 `QueryInformationJobObject(BasicAccountingInformation).ActiveProcesses > 1`。要求 Windows 10 1809 及以上。
- **POSIX**:`posix_openpt`/`grantpt`/`unlockpt`/`ptsname` 打开主从端,先 `ioctl(TIOCSWINSZ)` 设初始尺寸;用 `posix_spawnp` 启动,带 `POSIX_SPAWN_SETSID`,并以文件动作打开从端作 0/1/2、设工作目录。setsid 之后子进程既是会话首进程又是组长,`kill(-pgid)` 和既有的收割循环原样复用。不用 `forkpty`:多线程运行时里 fork 不安全。macOS 的 SETSID 常量、取控制终端的方式和 `ptsname` 分支都标“待 CI 验证”,不声称已支持。
- **shell 默认值**(`shell` 为空时):Windows 依次取 PATH 上的 `pwsh.exe`、系统自带的 `powershell.exe`、`%COMSPEC%`;POSIX 取 `$SHELL`(须存在),否则 `/bin/sh`,参数默认 `-l`。环境变量继承 Host 的,再加 `TERM=xterm-256color`、`COLORTERM=truecolor`、`TERM_PROGRAM=Forge`,最后用请求里的 environment 覆盖。
- **退出**:shell 退出后杀掉它残留的进程树(契约要求 `Exited` 在进程树消失后才完成),排空输出,发 `terminal.exit`。主管道断开、`host.stop` 与关闭栅栏都会杀掉全部终端;杀树失败计入 `HostCleanupReport`,与 Worker 走同一套“可验证清理”规则。

### HostClient(`Forge.HostClient`)

- `ExtensionHostClient` 加 `partial`,在新文件 `ExtensionHostClient.Terminal.cs` 实现 `ITerminalHost`。未处于 `Running` 时 `StartAsync` 抛 `InvalidOperationException`。
- `HostTerminalSession.Output` 是自写的 `ChannelReader` 子类:`TryRead` 时累加已读字节,攒够 64 KiB 或队列读空就发一次 `terminal.ack`。消费即确认,不需要改契约。队列只受窗口约束,因此天然有界。
- `WriteAsync` 把输入切成 ≤ 64 KiB 的 `terminal.input` 通知。退出后的写入、缩放一律忽略;退出后 `HasForegroundProcessAsync` 返回 false。
- 连接断开(Host 崩溃、重连换代)时:`Output` 以 `IpcConnectionClosedException` 完成,`Exited` 也以该异常失败。`DisposeAsync` 发 `terminal.stop`,限时 5 秒。

### App(`Forge.App/Terminal/**`)

- **`Vt/`(纯逻辑,不依赖 Avalonia)**:
  - `VtParser`:DEC ANSI 状态机。UTF-8 解码跨分块续接,非法字节输出 U+FFFD。
  - `TerminalBuffer`:主屏 + 备用屏,单元格是值类型。回滚是环形行缓冲,长度上限取偏好。缩放不重排行(截断或补齐),重排列入延后项。
  - `TerminalEmulator`:负责下列序列,未知序列忽略、不破坏状态。
    - C0 控制字符;ESC 7/8/D/E/M/c/=/>,G0/G1 的 DEC 线框字符集。
    - 光标移动 CUU…VPA/HPA;ED、EL、ECH、ICH、DCH、IL、DL、SU、SD、REP;DECSTBM;Tab 设置 HTS/TBC。
    - SGR:属性、16/256/truecolor 前景和背景。
    - DECSET/RST 1、6、7、12、25、47、1047、1049、1000、1002、1003、1006、1004、2004、2026;DECSCUSR 光标形状。
    - 应答类:DSR 5/6、DA1/DA2(写回输入)。
    - OSC 0/1/2 标题;OSC 8 链接;OSC 52 只写剪贴板;OSC 10/11 查询时按主题回答前景和背景色。
  - `KeyEncoder`:xterm 按键编码,含修饰键参数、应用光标/小键盘模式。`MouseEncoder`:SGR 1006 和 1000/1002/1003。`CellWidth`:按 East Asian Width 与 emoji 范围表区分宽字符,宽字符占两格;组合字符附在前一格上。
- **线程**:每个终端一个后台泵,读 `Output`、持锁喂解释器、置脏标志。UI 刷新按帧合并,脏了才 `Post` 一次,和 `FrameCoalescer` 同一套做法。渲染在 UI 线程短暂持锁,只读可见行。UI 线程从不解析。
- **`TerminalControl`**:自写 `Control.Render`。
  - 绘制:每行按属性分段生成 `TextLayout`,按行版本号缓存;背景和光标画矩形,选区叠加绘制;字形回退交给 `TextLayout`;格宽按代码字体测得。
  - 键盘与文本输入:`KeyDown` 交给 `KeyEncoder`;`TextInput`(含 IME)按 UTF-8 发送。
  - 复制粘贴:Ctrl+Shift+C/V,macOS 用 Cmd+C/V;启用 2004 时粘贴加括号包裹;右键菜单有复制、粘贴、清屏。
  - 选择:拖选、双击选词、三击选行;开启鼠标上报时按住 Shift 仍是本地选择。
  - 滚轮:开启上报时发给程序;在备用屏时发方向键;否则滚动回滚区。
  - 链接:Ctrl+点击 OSC 8 链接,只放行 http/https。
  - 尺寸:尺寸变化防抖 50 ms 后调用 `ResizeAsync`。
  - 主题:颜色来自 `TerminalPalette`,亮暗各一套 16 色,主题令牌给前景和背景,`ActualThemeVariant` 变化时重绘。
- **`TerminalViewModel`**:标题取 OSC 标题,没有时用“终端 n”;状态分启动中、运行、已退出(退出码)、失败(原因)。退出后标签保留,画面末尾附一行“进程已退出”。Host 未运行时直接显示失败状态,不抛异常。
- **`TerminalWorkbench` 协调器**:
  - 按会话 id 持有终端;新建时取会话的 `WorkDir`、偏好里的 shell 和参数(参数按空格切分、双引号分组)。
  - 切换会话时:当前会话之外、停靠着的终端标签移出底部组,但不结束;浮出的标签留在自己的窗口里。
  - 会话归档时结束该会话的全部终端;App 退出时结束全部,先于组合根拆除,限时等待。
  - 关闭钩子:如果终端有前台进程,先激活该标签,在终端视图里显示一条横幅“仍有程序在运行,关闭会结束它 [结束并关闭] [取消]”,不引入对话框框架。
- **工作台接线(已批准)**:
  - `TabKind.Terminal` 与 `terminal:<id>` 前缀;终端标签不写进 `open-tabs` 快照。
  - `WorkbenchLayout` 增加可否决的关闭钩子 `TabCloseRequested`(异步,返回是否允许);`PanelTabStrip` 的关闭改走它。
  - `MainWindowViewModel` 只把会话切换、归档、退出三件事转给协调器,并在底部组加“+”工具按钮。
  - `MainWindow.axaml` 加 Ctrl+` 快捷键;状态栏加“终端”按钮。
- **设置分区** `terminal`(order 400):shell、参数、回滚行数,在 `AppComposition.Create()` 追加注册,在 `App.axaml` 追加模板。文案放 `Localization/Strings.Terminal.cs`,键名前缀 `terminal.`。

### 验证

- **App 单测**(`tests/Forge.App.Tests/Terminal/`):解释器与仿真器逐序列核对缓冲内容和光标;UTF-8 被切在分块边界;宽字符与组合字符;备用屏与滚动区;回滚上限;按键和鼠标编码;各类应答;未知序列不破坏状态。
- **性能**:20 MB 普通文本喂给仿真器,吞吐 ≥ 30 MB/s(测试里阈值放宽);写入期间 UI 每帧最多刷新一次。
- **HostClient 单测**:用 FakeHost 测分片、ack 节奏、seq 校验、退出与断连语义、退出后的调用。
- **集成测试**(`tests/Forge.Integration.Tests/Terminal*`,真 Host):起 shell 并回显输入;缩放后程序读到的新尺寸;退出码;`stop` 后整棵树消失;前台进程检测;窗口满时停止读取、确认后恢复,且字节不丢、顺序不乱;主管道断开时杀掉全部终端;超出上限报 `terminal_limit`。
- **headless UI**:控件能渲染、按键能变成输入、选区能复制、关闭横幅能否决关闭。

### 延后项

缩放时重排行;缓冲内搜索;shell 集成(OSC 133);重启后恢复终端;已退出终端的“重新启动”;图片协议(sixel、kitty);双向文本与复杂文字;macOS 实机验证。

### 分工

- **主 Agent**:改 `Terminal.cs` 契约,完成后提交。
- **第一批,并行**:Host PTY;HostClient 终端客户端;App VT 引擎(纯逻辑)。每块配一个独立的测试会话。
- **第二批**:App 控件、视图模型、协调器、设置分区、工作台接线,依赖 VT 引擎与 HostClient。
- **收尾**:评审、修改,每完成一步提交一次;最后更新 SPEC,并删除本文件。

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
