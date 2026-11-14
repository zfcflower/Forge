---
id: forge-host-client
type: module-design
status: draft
title: Forge.HostClient
parent: forge-architecture
depends-on: [forge-ipc]
---

# Forge.HostClient

主程序(Native AOT)侧访问 Extension Host 的唯一入口 `IExtensionHostClient`;`ExtensionHostClient` 同时实现
`IExtensionContributionClient`,提供非 UI 贡献快照、命令/提示词调用和 `run_code` 代理。

## 边界

- 只依赖 Abstractions、Ipc、Ipc.Protocol、Plugin.Sdk;AOT 兼容(MessagePack 源生成格式化器,无反射序列化)。线上负载即 `Forge.Ipc.Protocol` 与 SDK 的类型,本模块不另定义线上 DTO;工具参数文本只转换一次为 UTF-8 字节(`ToolArguments`,`Utf8JsonReader` 校验,不建 DOM)。不引用 Roslyn、ExtensionHost。
- 提供 `ITool` 代理(`ExtensionHostTool`)、命令代理(`ExtensionHostCommand`);参数在本地校验,无效参数不发 IPC。
- 不读审批数据库,也不携带 SQLite 依赖:插件状态与待审批信息一律经 `plugins.status` 查询。
- 无论 Host 由本进程启动还是外部已在运行,绑定/上下文规则完全一致:谁启动 Host 不影响信任判断。

## 关键决策

- **回传调用必须绑定在途调用(fail-closed)。** Host 转来的 `host.tool_call` 必须是 `{runId, pluginId, name, args}`。
  缺 `runId`、空白 `runId` 或只有旧字段(`sessionId`/`workingDir`/`depth`)的**无绑定旧式调用直接拒绝**,不再执行:
  这是协议不匹配,不是兼容路径。缺 `runId` 由解码拒绝(线上对非空成员上的 nil 与短数组都抛),因此代码里只剩空白
  `runId`、空白 `pluginId`、空白 `name` 三条显式检查,没有“未绑定”这一独立分支。`runId` 必须命中本连接世代仍在途的私有 pending 记录,且记录的 plugin 身份必须与
  `pluginId` 相等(`$run_code` 为脚本保留身份),否则在 resolver 与审批之前拒绝。会话、工作目录、深度、插件链
  **只取 pending 记录**(发起调用时的 `ToolContext` 与可信链),Worker 自报的 `sessionId`/`workingDir`/`depth`
  一律忽略。未知、已结束、已取消、已超时或重放(运行已结束后再次出现)的 run 一律无效。旧的
  `HostToolWorkingDirectory` 选项保留源码兼容但不再被读取。
- **回传调用按会话路由(`IHostToolCallRouter`)。** 构造时传入路由器后,`host.tool_call` 的工具解析与审批都走
  `Resolve(sessionId, name)` / `ApproveAsync(sessionId, request, ct)`,`sessionId` **只取该 run 注册时的 Host 校验上下文**
  (`run.Context.SessionId`),绝不取 Worker 消息里的字段;插件命令审批同样用其有效上下文的会话。路由器未知/已关闭的会话
  必须 fail-closed(返回 null / Deny)。旧的 `Func<string, ITool?>` + 审批委托构造保持不变(无会话,测试沿用);
  传了路由器则路由器优先。`run_code` 脚本内的调用仍用该次执行的注册表,但审批同样走路由器。
- **可信调用链走私有 AsyncLocal。** 每次执行(tools/commands/prompts/code)都分配 128 位加密随机 `runId`、
  绑定当时连接世代、原始 `ToolContext`、完整可信链、自身取消生命周期和每运行回调门。嵌套代理只在本客户端执行
  某回调期间读取该私有上下文,并以新 `runId`、父链与父深度(深度由主程序推算,不取 Worker 值)发起新执行;
  用户参数无法注入其中。链中重复插件(环)与超过 8 环的链在本地拒绝;`run_code` 的嵌套规则见下一条;
  父运行结束时其所有嵌套子请求随生命周期取消。运行结束/连接拆除会作废 pending 记录,迟到的回调找不到可执行目标。
- **根身份是可信的调用令牌,不是插件名。** `ExtensionRun` 记录 `RootRunId`:顶层 `BeginRun` 生成 `runId` 并令
  `RootRunId` 等于它;嵌套 `BeginRun` 从 pending 记录里的父运行复制 `RootRunId`,不从 `ToolContext`、Worker 回调
  或线缆上任何字段读取回填。四类序列化上下文(`plugin.execute`/`code.execute`/`plugin.command`/`plugin.prompt`)
  统一由 `WireContext` 从 pending 记录构造并携带 `rootRunId`,所以同一插件(或 `$run_code`)的两次**独立**执行
  得到不同根身份——Host 不能再用首个插件名或 `$run_code` 判定“同根”,同根排队才安全;A→B 的嵌套调用沿用 A 的
  根身份而各自持有新的 `runId`。
- **执行根的工作目录必须可信且绝对。** 四类执行入口(插件工具、`run_code`、命令、提示词)在 `BeginRun`、审批与
  任何 IPC **之前**校验**有效上下文**的工作目录:非空白且完全限定(`Path.IsPathFullyQualified`)。有效上下文由
  `EffectiveContext` 统一推导为 `origin.ParentContext ?? context`:嵌套调用一律继承可信父运行记录里的会话与目录,
  调用方传进来的替换路径不能覆盖父上下文;也不把相对路径规范化成进程当前目录,相对就是相对并被拒绝。拒绝时的
  形状按入口固定:插件工具与 `run_code` 返回 `IsError` 的 `ToolResult`,命令返回错误 `CommandResult`(审批请求也
  用有效上下文里的目录),提示词返回 `null` 并记诊断;四条路径都不发 IPC、不调用审批、不解析工具。命令入口因此把
  取 origin 与目录校验排在审批**之前**(旧实现先审批后取 origin)。根登记(`tools.register`/`contributions.register`)
  与 peer 可用性的既有语义不变。
- **`run_code` 只在顶层执行。** 主程序自己发起的顶层 `run_code`(无受信父调用的 Agent/UI 调用)正常执行;
  只要 `origin.Ancestors` 非空,即处于任何受信父调用中(脚本或插件回调),`InvokeCodeAsync` 就在本地拒绝,
  不再只按祖先名字是否为 `$run_code` 特判。Worker 经 Host 转来的 `host.tool_call` 若 `name == run_code`,
  一律在 pending 绑定查找、resolver 与审批**之前**拒绝,不看该 run 是否为脚本。用户已批准“插件与脚本都禁止子调用
  `run_code`”,所以这条规则按“嵌套扩展调用”统一表述,而不是只写脚本。
- **审批不延长 run 的生命周期。** 审批回调可以忽略取消令牌并仍然返回 `Allow`。因此 `Allow` 之后、真正调用工具
  之前必须重新检查链接令牌与 `run.IsActive`:父调用在审批挂起期间结束(超时、取消、连接拆除)时该 run 已失效,
  即使目标工具忽略 `CancellationToken` 也不得启动。已结束回错误结果,仅连接级取消则直接返回取消;两条路径都不
  调用工具。
- **run 资源确定性释放。** `ExtensionRun` 持有取消源与每运行门,并用租约计数保护:回调在读取 token、使用门、
  把子 run 链接到父 token 之前必须 `TryLease()`(run 已结束则失败并拒绝),结束时 `ReleaseLease()`。
  发起调用方本身持有第一个租约,`EndRun` 在 `End()` 之后归还它。`End()` 先置 ended 再 `Cancel()`,取消返回且
  最后一个租约归还后才 `Dispose()` 取消源与门;取消进行中不释放,因此不会出现 `Cancel` 与 `Dispose` 并发、
  在已释放源上 `Register`/`CreateLinkedTokenSource` 这类 ODE 竞争。
  run 记录在构造时捕获 `CancellationToken`,调用方不再读取 `Lifetime.Token` 属性。每个 run 结束时处置自己的
  linked source,长期存活的会话/调用者令牌上不残留注册;嵌套子 run 仍链接父 run 的令牌,而父 run 在其回调
  持有租约期间不会被释放。
- **失败回执有界、不打断事件循环。** 被拒绝的回调、被丢弃的声明与无法解码的贡献集都写入
  `Diagnostics`(最近 16 条)并返回错误结果,不会抛出到 IPC 事件循环。`plugins.status` 的失败不在此列:它先记
  诊断再把异常抛给调用方(见插件状态一条)。每次子调用仍按 main 端结果上限截断
  (`CodeExecutionOptions.MaxInnerResultChars`),脚本输出按 `MaxResultChars` 截断;每运行的并发门同样由 pending
  记录携带。
- **每次调用一个预算,不是每连接一个。** `IIpcPeer` 只有每 peer 一个默认超时,所以本客户端把它设为无限,自己给
  每个请求挂预算:`RequestTimeout`(默认 120 秒)约束普通往返,`run_code` 用脚本自己的 `timeout_seconds`
  (默认 60 秒、上限 600 秒)作为该次 `code.execute` 的预算,排队与执行都算在同一条 deadline 里。这样合法的 600 秒
  脚本不会被全局默认值截断,其他请求也没有被放宽。预算到期映射为 `IpcTimeoutException`;`run_code` 把它转成
  “脚本已停止”的错误结果,并作废该 run。
- **超时只结束那一次调用,不动连接。** 预算到期是**调用级**结果,不是连接级故障:客户端只把该请求从 pending
  移除、给 Host 发一条 `ipc.cancel` 通知,然后抛 `IpcTimeoutException`;peer、连接世代、`Status` 与已登记的
  工具/贡献快照全部不变,紧接着的下一个请求走同一条连接。只有连接真的关闭(对端 `Closed`、管道 IO 失败)才会把
  状态改成 `Failed` 并让在途调用拿到 `IpcConnectionClosedException`。所以“超时后连接仍可用”是本模块的契约,
  不是调用方的期望:同一 `runId` 的迟到应答在 run 结束后一律作废(`EndRun` 已作废 pending 记录),不会被下一
  个请求误取。
- **连接超时要说明 Host 自己的状态。** `StartTimeout` 到期时抛出的 `TimeoutException` 先报“没在 N ms 内接受
  连接”,再给出自启进程当时的状态:仍在运行(带 pid)的是启动慢,已退出(带退出码)的是启动前就死了,
  两种都不需要再靠猜;随后附上同一进程的标准输出/标准错误尾部(有则附)。只连接外部 Host、自身没启动进程时
  这两段为空:`HostConnection.HostExecutablePath` 为空就没有可报告的进程。一个连接尝试的时长为
  `IpcPipeOptions.ConnectTimeout`,因此它决定了客户端发现“自启进程已退出”的最慢延迟。**放弃的尝试必须被取消**:管道层在 `ConnectTimeout` 到期后只停止等待,底层连接仍会继续重试并可能在之后连上 Host;Host 把“连上却不发请求、随即断开”当作主程序已离开并自行退出(退出码 0、无输出),于是重试方再也连不上。因此本客户端在处置该次尝试的 peer/transport 之前,先用同一令牌取消这次尝试,让被放弃的连接停下来。
- **崩溃保留快照,同实例重连。** Host 崩溃置 `Failed` 但保留最后已知 `Tools` 与贡献快照;主动 `StopAsync` 清空。
  `StartAsync` 在 `Stopped` 与 `Failed` 均合法:从 `Failed` 是重连(新连接,必要时新进程)。重连在建立新连接
  **之前**先拆掉上一条连接:不发 `host.stop`、不清快照,只处置旧 peer/transport,并按
  `TerminateAsync(..., waitForSelfExit: false)` 杀掉并释放旧的自启进程树(否则旧进程只会在 `StopAsync` 时随最新
  一代一起被漏掉)。每条连接有一个单调递增的世代号,`tools.register`/`tools.unregister` 与
  `contributions.register`/`contributions.unregister` 按收到它的那条连接打标签;成功进入 `Running` 时只丢弃
  **更早世代**的登记(确有丢弃才触发一次 `ToolsChanged`/`ContributionsChanged`),因此新 Host 在连接就绪到
  `Running` 之间发出的、或与这次切换并发到达的注册不会被误清,而未被新 Host 重新注册的旧条目仍不会残留。
  世代号同时决定"哪条连接还在说话":拆解前先把当前世代退役(`activeGeneration` 前移)并作废该世代的 pending
  run,因此旧连接的迟到 `Closed` 回调(以及连接重试期间被丢弃的候选 peer 的关闭)不会改写新连接的状态。崩溃后的
  自动、有上限的重启策略不属于本模块,由组合根(Forge.App)实现:它观察 `StatusChanged == Failed` 并调用同一实例
  的 `StartAsync`。
- **Running 与关闭回调同一把锁。** 连上之后提交 `Running` 必须在关闭回调用的同一把状态锁(`statusGate`)内,并在锁内
  复核三件事:该连接的世代仍是当前世代、对端尚未关闭、状态仍是 `Starting`。连接若在“连上→Running”这段窗口里关闭,
  关闭回调写下的 `Failed` 不会被覆盖:客户端保持/变为 `Failed` 并带上关闭原因,本次 `StartAsync` 抛
  `IpcConnectionClosedException` 并拆掉这条已死连接,同一实例随后可从 `Failed` 重启。一次启动最多报告一次
  `Failed`——谁先把状态移出 `Starting` 谁报。
- **先等自退,超时才杀。** `StopAsync` 发 `host.stop` 并等应答;应答到手后给 Host `ProcessExitTimeout` 自行跑完关停
  (停 Worker 进程树)并以退出码 0 结束,只有超过该期限才杀进程树。没有应答(超时或连接已断)时立即清理,不再等。
  自启进程的退出码因此可观测:`ExtensionHostClient.HostExitCode` 在本客户端**自己启动**的 Host 离开后给出该码
  (0 = 自行退出,被杀的 POSIX 进程为 128+信号),正在跑、或本客户端只是连到外部 Host(`HostExecutablePath` 为空)
  时为 `null`;每次 `StartAsync` 先重置为 `null`,拆除时若确实处置了自启进程则写入它的退出码(崩溃后仍保留,直到下次启动)。
  它与 `HostProcessId` 一样只在具体类上,不在 `IExtensionHostClient`。
  注意 `Process.GetProcessById` 拿到的 `Process` 在 Unix 上读 `ExitCode` 会抛 `InvalidOperationException`(不是本对象
  启动的),需要退出码的调用方必须用本属性。
- **非 UI 贡献是世代化的整数快照。** `contributions.register` 携带某插件的完整 `PluginContributionSet`,
  `contributions.unregister` 移除该插件的全部非工具贡献。替换而非追加,所以同一插件不会留下重复或过期条目;
  内容完全相同的新世代登记只更新世代号,不重建代理、不触发 `ContributionsChanged`。视图是原子替换的不可变列表
  (`Commands`/`Workflows`/`Agents`/`PromptContributors`)。名称跨插件按 ordinal plugin id 去重(先到先得,后者被丢弃并
  记录诊断),避免同名命令/工作流/Agent 指向错误实现。畸形声明(空名、空 body、未定义的 `thinking` 序号、
  非正 `maxOutputTokens`、空提示词 id)逐条丢弃并诊断;整个贡献集无法解码时该插件的快照不更新,不抛异常。
  `$run_code` 是保留身份,不能登记工具或贡献。`panels` 只做声明记录,本阶段不注册也不渲染。
- **Agent 严格转换,工作流保持 Markdown。** `AgentDeclaration` 转成 Abstractions `AgentDefinition`/`ModelSettings`
  时校验枚举与数值,不把未知值夹到相邻值;工作流按 `WorkflowDefinition` 原样传递(frontmatter + 步骤文本),
  不做 JSON 执行引擎;提示词贡献只在 `GetPluginPromptAsync` 时异步求值。
- **命令与提示词代理都走审批和绑定上下文。** 命令代理发 `plugin.command`/`CommandExecutionRequest`,先经 main 的
  审批路径(以 `ToolSideEffect.Execute` 请求),被拒或没有审批路径时返回错误结果且不发 IPC;提示词代理发
  `plugin.prompt`/`PromptExecutionRequest`,携带由 `ToolContext` 绑定的上下文。两者都带随机 `runId` 与自身预算,
  其回调按同一 pending 规则执行。同步阻塞 IPC 不引入。
- **提示词的软失败只覆盖可解释的失败,且不掩盖调用方取消。** `GetPluginPromptAsync` 在以下情形返回 `null` 并记诊断:
  未注册/空白标识、客户端未运行、环、工作目录无效、host 业务错误(`IpcRemoteException`)、超时,以及调用中途的
  连接断开或管道 IO 失败(`IpcConnectionClosedException` 本身是 `IOException`,所以只捕 `IOException` 这一具体类型)。
  不设兜底 `catch`;调用方自己的 `OperationCanceledException` 原样传播——取消是调用方的决定,不是“插件没有文本”。
- **run_code 是显式代理。** `CreateRunCodeTool(IToolRegistry, RunCodeProxyOptions?)` 返回 AOT 安全的 `ITool`
  (不要静默塞进插件工具列表)。描述与 schema 由主程序工具注册表稳定生成(按名称排序,registry 变化才重建),
  **描述只列可调用的名字,不再逐工具列描述与 JSON schema**(W14):模型要细节时让脚本调 `Tools.List()`/
  `Tools.Describe(name)`,每回合因此只付一份 `run_code` 定义而不是“工具数 × (描述 + schema)”。脚本侧的查询面在
  `forge-script-runtime`。解析 `code`/`timeout_seconds` 并在本地校验:脚本必填、非空、不超过 `MaxScriptChars`;`timeout_seconds` 必须是正的
  有限数(NaN、∞、非正一律拒绝),默认 60 秒、上限 600 秒。数值先与上限比较夹取、再转 `TimeSpan`,所以 `1e300`
  这类超大有限预算夹到上限,而不是在校验之前抛 `OverflowException`。加密随机 `runId` 与上下文放进 SDK `CodeExecutionRequest` 的
  `code.execute`;编译、语义检查与脚本进程一概留在 Host,本模块不含反射或 Roslyn。每个子调用单独审批,
  并发由每运行门限制,结果在主程序侧截断;超时/结束后该 run 立即作废,迟到回调被拒。
- **预算可以活读。** `RunCodeProxyOptions.ExecutionProvider`(W14)存在时,每次读 `Definition` 与每次执行都重新取一份
  `CodeExecutionOptions`,因此“内部调用并发上限 / 单次内部结果上限 / 返回结果上限”这三个用户设置改完即对下一回合的
  描述和下一个脚本生效,不必重建代理或会话宿主;`Execution` 是提供者为 null 时的固定值。读取点只有两处(描述构建、
  调用入口),不在热路径上循环读文件。
  提供者只在创建代理时由 `Validated` 严格校验(非法值抛 `ArgumentOutOfRangeException`);之后每次读取都再过一遍轻量钳制
  (`RunCodeProxyOptionsExtensions.Clamped`:非正的上限抬到 1,缓存容量到 0,全部合法时返回原实例不分配),所以提供者事后
  答出 0 的并发上限也不会让每运行的信号量(`SemaphoreSlim(0)`)把内层调用挂到超时。返回 null 仍是配置错误并抛出。
  `Definition` 的缓存把(指纹,定义)放进一个不可变对象,一次 `Volatile.Write` 原子发布,并发读者不会拿到一个线程的指纹配另一个线程的定义。
- **run_code 的可调用面只有一个推导点。** `RunCodeToolSurface.Snapshot` 一次读出每个工具的定义字段(名称/描述/
  schema/副作用),描述文本、变更指纹与 `code.execute` 的 `tools` 声明全部来自同一份快照,因此三者不会对“哪些工具
  可调用”或“读到的定义内容”产生分歧。所有 `RunCodeProxyTool` 实例在**读 `Definition` 之前**按类型剔除:代理的
  描述本身就由 registry 推导,若它已被登记进该 registry(自己的或共享同一 registry 的第二个代理),读它的
  `Definition` 会无基线递归,导致不可捕获的栈溢出——`catch`/`finally` 都救不回进程,所以排除必须发生在解引用之前,
  不能靠“读出来再按名字过滤”。剔除代理之后,再按名称排除其它恰好叫 `run_code` 的普通工具。指纹是内容哈希而不是
  对象身份:内容相同的新对象不重建描述,代理的增删也不重建(它对可调用面没有贡献);只有可调用面真的变化才重建,
  描述顺序按 ordinal 排序保持稳定。描述里印出的预算数字也参与指纹(W14):并发上限、内部结果上限、返回结果上限、
  日志上限与两个超时任一变化都会重建描述,所以提示里的数字不会与实际执行的边界脱节。`MaxScriptChars` **不**进指纹
  ——描述不印它,只在本地的参数校验里用。代价是每次读 `Definition` 都分配一份快照列表(条目数=工具数),换来策略单点与
  自包含安全;工具数在主程序规模下是几十量级。
- **插件状态只读 IPC,失败不伪装成“没有插件”。** `GetPluginStatusesAsync` 发 `plugins.status`,不回落到本地数据库,
  也没有“失败→空列表”的成功路径:未运行与调用中途断连会**抛出**(`InvalidOperationException`/
  `IpcConnectionClosedException`),调用方的取消原样以 `OperationCanceledException` 传播;请求预算到期会被转成
  `IpcTimeoutException`,先记一条诊断再抛出;应答无法解码时先记诊断,再抛出携带操作名与具体问题的
  `InvalidDataException`。三者调用方都必须理解为“未知”,而不是“没有插件”。只有真正解码成功、Host 明确报告
  空 `Plugins` 列表的应答才返回空列表。
- **状态逐行校验,任一行无效则整份应答无效。** `plugins` 的每一项都单独校验,不合格的那一行不会像旧实现那样被
  静默跳过(那会把“Host 报告损坏条目”伪装成“插件更少”):列表项为 nil、`pluginId` 空白、`status`
  空白、`permissions` 不是数组/含未定义 `PermissionKind`/scope 空白、以及非 `invalid` 行缺 `version`、
  `sourceHash`、`dependencyHash` 或 `sdkVersion` 都会拒绝**整个**应答。问题文本带数组下标(`plugins[3]`),已知身份时
  再带 `'(pluginId)'`,所以调用方与服务日志能定位到坏行。`status` 为 `invalid`(无内容、无可审批项)的行不要求
  version/哈希/SDK 版本非空,否则“为什么跑不起来”这唯一一行会被反过来判成不可读。`status` 只要求是非空 token、
  按原值透传:已文档化的 `draft`/`approved`/`disabled`/`uninstalled`/`invalid` 全数接受,Host 以后新增状态也不会让
  整份注册表变成“未知”。
- **解码即形状校验。** MessagePack 解码拒绝非可空成员上的 nil 与类型不符的字段,这类应答整份以“无法解码”
  拒绝(不再逐字段命名);之后的逐行规则只检查解码器不管的内容:空白字符串、nil 列表项、未定义的 `PermissionKind`
  与空白 scope。`invalid` 行的 `version`/哈希/`sdkVersion` 可为空串。`plugins.status` 与 `extensions.commit` 内嵌的
  `status` 共用同一份行校验(标签区分 `plugins[i]` 与 `status`),规则只有一处,消息带坏行下标与已知身份。
- **授权的扩展写入同样只读 IPC、逐项校验。** `IExtensionAuthoringClient` 的七个方法先做本地参数校验——空白
  id/version/哈希、未知 `kind`、`Files`/`Permissions`/`Dependencies` 为 null 或含 null 项都抛 `ArgumentException`
  且不发 IPC;`extensions.stage`/`extensions.commit` 可能在 Host 侧跑 Roslyn 编译,预算取 `RequestTimeout` 与
  5 分钟中较大者(客户端未配超时则不设 deadline),其余请求用 `RequestBudget`。应答逐字段校验,不合格先记
  `Diagnostics` 再抛带 op 名与具体问题的 `InvalidDataException`:`StagedExtension` 的 `pluginId`/`version`/`kind`
  必须非空白且 `kind` 只能是 `plugin`/`workflow`,`files`/`permissions`/`dependencies`/`diagnostics` 必须是非 null
  数组且元素非 null、每个文件 `path` 非空白;没有 diagnostics 时三个哈希都必须非空白(带诊断的草稿允许没有哈希,
  缺失的哈希按 `null`→`string.Empty` 归一);`extensions.pending` 的每一项按同一套规则校验,**任一项坏则整份应答
  无效**;`extensions.commit` 的 `toolNames` 必须是非 null 数组且元素非空白。Host 的业务错误
  (`IpcRemoteException`,如 `approval_mismatch`/`extension_blocked`/`unknown_plugin`)与调用方取消原样传播,预算
  到期映射为 `IpcTimeoutException` 并先记一条诊断。
- **终端(`ExtensionHostClient.Terminal.cs` 实现 `ITerminalHost`)。** 线协议见 `forge-extension-host`“交互终端”。
  - **启动**:未处于 `Running` 时 `StartAsync` 抛 `InvalidOperationException`。`terminal.start` 请求本身**不绑调用方的 ct**,因为取消会让对端丢掉应答,也就丢掉了 Host 可能已建好的终端的 id。调用方取消后立即得到 `OperationCanceledException`;客户端继续等应答,若终端已建好,就清掉它的早到帧并补发 `terminal.stop`,否则这个终端会占着 Host 的 32 个名额直到断连。
  - **早到帧**:`terminal.output`/`exit` 可能先于 start 应答到达,按 id 缓冲(总量 1 MiB),在登记表锁内回放,所以首批输出不丢、seq 不乱。
  - **输出与确认**:`Output` 是自写的 `ChannelReader`,`TryRead` 时累加已读字节,攒够 64 KiB 或队列读空就发一次 `terminal.ack`,消费即确认。队列只受 Host 的窗口约束,因此天然有界。客户端校验 `seq` 连续。
  - **输入**:`WriteAsync` 把输入切成 ≤ 64 KiB 的 `terminal.input` 通知并串行发送。
  - **退出与断连**:退出后,写入和缩放一律忽略,`HasForegroundProcessAsync` 返回 false。该查询会把业务错误与超时也当成 false(关闭确认宁可不拦)。连接断开(Host 崩溃、重连换代)时,`Output` 以 `IpcConnectionClosedException` 完成,`Exited` 也以它失败。
  - **释放**:`DisposeAsync` 发 `terminal.stop`,限时 5 秒;超时或失败时写一条带 terminalId 的诊断,本地照常收尾(`Exited` 为 -1),属于尽力而为:Host 侧这个终端要等断连才会被清理。
- **已知限制:** `IIpcPeer` 仍只有每 peer 一个默认超时,`RequestAsync` 没有 per-call 超时重载;本客户端因此把 peer
  默认超时设为无限、在 `SendAsync` 里为每次调用自建 deadline。若将来 Ipc 增加 per-call 超时,这里应改为直接使用它。
