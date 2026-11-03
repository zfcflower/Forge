---
id: forge-host-client
type: module-design
status: draft
title: Forge.HostClient
parent: forge-architecture
depends-on: [forge-ipc]
---

# Forge.HostClient

主程序(Native AOT)侧访问 Extension Host 的唯一入口 `IExtensionHostClient`。

## 边界

- 只依赖 Abstractions、Ipc、Plugin.Sdk;AOT 兼容(源生成 JSON,无反射序列化)。不引用 Roslyn、ExtensionHost。
- 提供 `ITool` 代理(`ExtensionHostTool`);参数在本地校验,无效参数不发 IPC。

## 关键决策

- **回传工具调用 fail-closed。** `host.tool_call` 顺序为 解析 → 审批 → 执行:无 resolver 报未知工具,有 resolver 但无审批通道则拒绝,绝不未经审批执行。
- **回传上下文取自发起调用。** Host 的 `host.tool_call` 总带 `sessionId`/`workingDir`/`depth`,原样用于审批与 `ToolInvocation`。`workingDir` 必须**完全限定**(`Path.IsPathFullyQualified`,不是 `Path.IsPathRooted`:Windows 上 `C:relative` 与 `\relative` 仍依赖当前盘/目录),否则不进审批、不执行,直接返回错误结果。字段**缺席**(旧 Host)才走兜底 `HostToolWorkingDirectory`/当前目录,且兜底值本身也必须是完全限定路径,否则同样 fail-closed。字段**存在**但为 `null`/空/空白/非完全限定一律 fail-closed —— 显式空白**不**回退,解析层不再把空白折叠成 "没有字段"。这是行为变更:旧实现把空白当缺省并静默落到主程序目录。
- **崩溃保留快照,同实例重连。** Host 崩溃置 `Failed` 但保留最后已知 `Tools`;主动 `StopAsync` 清空。`StartAsync` 在 `Stopped` 与 `Failed` 均合法:从 `Failed` 是重连(新连接,必要时新进程)。重连在建立新连接**之前**先拆掉上一条连接:不发 `host.stop`、不清工具快照,只处置旧 peer/transport,并按 `TerminateAsync(..., waitForSelfExit: false)` 杀掉并释放旧的自启进程树(否则旧进程只会在 `StopAsync` 时随最新一代一起被漏掉)。每条连接有一个单调递增的世代号,`tools.register`/`tools.unregister` 按收到它的那条连接打标签;成功进入 `Running` 时只丢弃**更早世代**的登记(确有丢弃才触发一次 `ToolsChanged`),因此新 Host 在连接就绪到 `Running` 之间发出的、或与这次切换并发到达的注册不会被误清,而未被新 Host 重新注册的旧条目仍不会残留。世代号同时决定"哪条连接还在说话":拆解前先把当前世代退役(`activeGeneration` 前移),因此旧连接的迟到 `Closed` 回调(以及连接重试期间被丢弃的候选 peer 的关闭)不会改写新连接的状态。崩溃后的自动、有上限的重启策略不属于本模块,由组合根(Forge.App)实现:它观察 `StatusChanged == Failed` 并调用同一实例的 `StartAsync`。
- **Running 与关闭回调同一把锁。** 连上之后提交 `Running` 必须在关闭回调用的同一把状态锁(`statusGate`)内,并在锁内复核三件事:该连接的世代仍是当前世代、对端尚未关闭、状态仍是 `Starting`。连接若在“连上→Running”这段窗口里关闭,关闭回调写下的 `Failed` 不会被覆盖:客户端保持/变为 `Failed` 并带上关闭原因,本次 `StartAsync` 抛 `IpcConnectionClosedException` 并拆掉这条已死连接,同一实例随后可从 `Failed` 重启。一次启动最多报告一次 `Failed`——谁先把状态移出 `Starting` 谁报。
- **先等自退,超时才杀。** `StopAsync` 发 `host.stop` 并等应答;应答到手后给 Host `ProcessExitTimeout` 自行跑完关停(停 Worker 进程树)并以退出码 0 结束,只有超过该期限才杀进程树。没有应答(超时或连接已断)时立即清理,不再等。
- **已知限制:** `IIpcPeer` 仅有每 peer 一个默认超时,`RequestTimeout` 约束包括长插件运行在内的所有往返;无 per-call 超时。
