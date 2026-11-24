# 08 交互终端

终端是底部组里的选项卡（`TabKind.Terminal`），可开多个、可浮出；工作目录取会话的 worktree，进程由 Extension Host 的 PTY 提供。

## 8.1 VT 引擎

纯 BCL、不依赖 Avalonia，Native AOT 无警告：

- DEC ANSI 解析器，UTF-8 跨分块续接，非法字节输出 `U+FFFD`。
- 主屏与备用屏；回滚环（偏好 `TerminalScrollbackLines`，1000–100000，默认 10000）。
- SGR 16 / 256 / truecolor。
- DECSET/DECRST：鼠标上报 1000/1002/1003/1006、括号粘贴 2004、同步输出 2026。
- OSC 0/1/2 标题、OSC 8 链接、OSC 52 只写剪贴板、OSC 10/11 按主题回答。
- xterm 按键与 SGR 鼠标编码、East Asian Width 宽字符。
- 缩放时不重排行；仓内实测记录为约 99 MiB/s（20 MiB 混合文本）、稳态滚动零分配，但未记录平台/负载/是否含搜索等复现条件，**不作为跨平台验收指标**；macOS 分支仍未验证。
- 已记录偏差：在整屏顶行执行 `CSI M` 时被删的行会进回滚（xterm 不会），这是为了保证不丢内容。

## 8.2 线程模型

- 每个终端一个后台泵：读 `Output`、持锁喂仿真器、置脏标志；UI 按帧合并刷新，UI 线程从不解析。
- 键盘输入与泵线程产生的设备应答（DA/DSR 等）走**同一个有序通道**，由单个写任务送达 Host，保证管道内有序。
- 终端会话（PTY）实现在 Extension Host 内，因此终端依赖 JIT 组件与 .NET 11 运行时（见 11.5、13.7）。Host 未运行时直接显示失败状态，不抛异常；视图先于会话就绪布局时先记下尺寸，会话就绪后补发。

## 8.3 终端控件

- 自绘，每行生成 `FormattedText` 并按行版本号缓存。
- 键盘与 IME 输入；复制粘贴 Ctrl+Shift+C/V（macOS 用 Cmd）；粘贴前去掉转义字符，启用 2004 时加括号包裹。
- **按键编码按键盘布局**（Avalonia 12.2 的 `KeyText`/`AlternateKeyText`/`ConsumedModifiers`）：Ctrl 和弦优先用平台给出的控制字符（德语布局的 Ctrl+`[` 发 `ESC`、俄语布局发 `^Я`），布局自己用掉的 Ctrl/Alt 视为文本（AltGr 的 `@`、macOS Option 打出的字符不再额外发 ESC 前缀），Alt 和弦保留 ESC 前缀；`KeyEncoder` 只把字符/命名键变成字节，布局判断在 `KeyChord`。
- 拖选、双击选词、三击选行（开启鼠标上报时按住 Shift 仍是本地选择）；滚轮在开启上报时发给程序、在备用屏发方向键，否则滚动回滚区。
- Ctrl+点击 OSC 8 链接只放行 http/https。
- 尺寸变化防抖 50 ms 后同步给 Host；视口锚定在回滚内容上，新输出不会把正在查看的历史顶走。
- 配色来自 `TerminalPalette`（亮暗各一套），字体跟随“代码字体”偏好（终端可单独覆盖族与字号，空 = 代码字体、0 = 代码字号）。
- **缓冲区搜索**：终端聚焦时 Ctrl+F 开搜索条；查询防抖 150 ms 后后台扫描（含 scrollback），世代号丢弃过期结果；匹配高亮与当前匹配强调、上/下跳、大小写开关、Esc 关闭；结果集变空也会重绘清除旧高亮。

## 8.4 状态与重启

- 标题取 OSC 标题，没有时为“终端 n”。
- 状态：启动中、运行、已退出（退出码）、失败（原因）；退出后标签保留，画面末尾附一行“进程已退出”。
- **重启**：已退出/失败状态在状态行出「重启」按钮，按同一 request（shell/参数/工作目录）起新进程；仿真器不重置、画面与回滚保留，新输出接在旧画面下；运行中不显示；失败走同一条本地化 Fail 路径。

## 8.5 多终端与会话协调

- `TerminalWorkbench` 按会话 id 持有终端。新建时取会话 `WorkDir` 与偏好里的 shell、参数（按空格切分、双引号分组）。
- 切换会话时，其它会话停靠在任意组里的终端标签被移出但不结束，切回时放回底部组；浮出的标签留在自己的窗口里。
- 会话归档时结束该会话的全部终端；App 退出时先于组合根拆除结束全部终端，限时等待。
- 关闭有前台进程的标签不弹对话框，而是在终端视图里显示横幅，让用户选“结束并关闭 / 取消”。
- 入口三个：Ctrl+`、窗口标题行终端图标、底部组标题栏“+”；终端标签不写进 `open-tabs` 快照。

## 8.6 PTY、流控与平台

- 线协议走主管道（`Forge.Ipc.Protocol.TerminalMessages`）：`terminal.start`/`resize`/`foreground`/`stop` 请求，`terminal.input`/`ack` 与 `terminal.output`/`terminal.exit` 通知；输出帧每帧 ≤64 KiB、序号从 0 连续递增。
- 校验：工作目录必须存在且绝对；列 1–1000、行 1–500；每个 Host 最多 32 个终端。错误码 `invalid_request`/`terminal_limit`/`terminal_start_failed`/`host_stopping`；未知或已退出 id 的 input 静默忽略、resize 视为成功、foreground 返回 false、stop 幂等。
- **流控**：每终端最多 1 MiB 已发未确认字节，到达上限就停读 PTY（子进程写入随之阻塞，和真终端一样）；客户端攒够 64 KiB 或队列读空发一次 `terminal.ack`。正常运行时输出不丢。
- 输入进入每终端有界队列（1024 块，每块 ≤64 KiB）；队列满或终端正在关闭时整块丢弃（不截断、不阻塞分发）——这是输出流控之外唯一的丢弃路径。
- 退出收口（单飞、幂等）：
  - shell 自然退出：杀残留进程树并等它消失，`ClosePseudoConsole`，剩余输出仍按 1 MiB 确认窗口发到 EOF，最后发 `terminal.exit`（最多等 30 s，超时丢弃剩余输出并计入清理报告）。
  - 主动 `terminal.stop`/`host.stop`/管道断开：立即停止发送、杀树、等进程消失、关控制台、继续排空 5 s，然后发 exit。
  - `terminal.exit` 是该 id 的最后一条消息，且先于 `terminal.stop` 的应答（客户端靠这一点拿到真实退出码而不是本地 -1）；POSIX 上被信号杀死时退出码为 128+信号。
- 平台：
  - Windows：`CreatePipe`×2 → `CreatePseudoConsole` → `CreateProcessW`（`PSEUDOCONSOLE` + `CREATE_SUSPENDED`）→ 分配进私有 Job（KILL_ON_JOB_CLOSE）→ `ResumeThread`；必须设 `STARTF_USESTDHANDLES` 并把标准句柄置空，否则 shell 继承 Host 的 stdout/stderr，表现为没有任何输出。要求 Windows 10 1809+。
  - Linux/macOS：`posix_openpt` → `grantpt`/`unlockpt`/`ptsname` → `TIOCSWINSZ` 初值 → `posix_spawnp(POSIX_SPAWN_SETSID)`（从端作 0/1/2）；不用 `forkpty`（多线程运行时里 fork 不安全）；收割扫**整个会话**（Linux 读 `/proc/<pid>/stat` 的 session 字段，macOS 只发组信号）；前台检测比较 `tcgetpgrp(master)` 与 shell 进程组。
- 默认 shell：Windows 依次取 PATH 上第一个**非应用执行别名**的 `pwsh.exe`、`%ProgramFiles%\PowerShell\7\pwsh.exe`、`powershell.exe`、`%COMSPEC%`；POSIX 取 `$SHELL`（必须存在）否则 `/bin/sh`，参数默认 `-l`。环境变量继承 Host，再加 `TERM=xterm-256color`、`COLORTERM=truecolor`、`TERM_PROGRAM=Forge`，最后用请求里的 environment 覆盖。
- 已知限制：Store 版 `pwsh.exe` 别名启动的是打包进程，其子进程不在 Host 私有 Job 里，`terminal.stop` 与整树回收管不到（默认选择跳过别名；显式指定别名时照用）。

## 8.7 设置与未做

- 终端设置只有一处：`tools` 分区的终端组（字体族、字号、shell、参数、回滚行数）——独立的终端设置分区已删除。
- 未做（见 14）：缩放重排行、OSC 133、重启后恢复、图片协议、双向文本、macOS 实机验证。搜索扫描的并发预算缺口见 14.3：大缓冲下持锁逐行快照，与输出流并发时可能超一帧预算（待度量）。

相关文档：[07-workbench-ui.md](07-workbench-ui.md)（底部组与标签）、[11-extensions-and-codemode.md](11-extensions-and-codemode.md)（Host 的 PTY 实现）、[15-appendix.md](15-appendix.md)（常量）。
