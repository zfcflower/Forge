---
id: forge-plugin-worker
type: module-design
status: draft
title: Forge.PluginWorker
parent: forge-architecture
depends-on: [forge-ipc, forge-scripting]
---

# Forge.PluginWorker

独立 JIT 进程:加载并执行一个插件,可被 Host 整体终止。一个进程一个插件。

## 边界

- 依赖 Ipc、Plugin.Sdk、Scripting(`PluginLoadContext`)。**不做权限检查**:所有对宿主工具的调用经 `host.tool_call` 走同一管道,由 Host 转主程序审批。
- 协议:`worker.load/execute/command/prompt/unload/stop`。`worker.load` 携带 `assemblySha256`、`files:{相对名: sha256}` 与 manifest `permissions`,返回 `{tools, contributions:PluginContributionSet}`;`worker.execute/command/prompt` 都必须携带 Host 绑定的 `context`(SDK `ExtensionCallContext`)与 `timeoutMilliseconds`。`worker.execute` 的结局是 `{content,isError}`;`worker.command`/`worker.prompt` 返回 SDK 的 `CommandOutput`/`PromptOutput`;`IpcError` 只用于参数畸形、不支持的操作与加载失败。**Worker 是纯插件进程**:`worker.run_code` 已移除,脚本由一次性 `Forge.ScriptExecutor` 执行。
- `host.tool_call` 只发 `{runId,name,args}`:runId 是 Host 派发时绑定的那个,session、工作目录与深度由 Host 的绑定表决定,Worker 既不发送也不可信。
- **绑定上下文只校验、不推算根。** 四个执行操作收到的 `ExtensionCallContext` 里的 runId 必须是 `[A-Za-z0-9_-]` 且 ≤128 字符;`rootRunId` 若带则同样校验形状,空链且缺省时按“根即本 runId”补全(兼容旧调用),链非空却缺 `rootRunId` 一律 `invalid_arguments` 拒绝——Worker 不拿插件身份或链首项补根,插件看到的 context 与 Host 判定的是同一个根。回调只能带 runId,不能提供或替换根。
- 不再有 `--script-only` 模式:Worker 只服务插件操作,脚本编译/运行能力不进入 Worker(它仍引用 `Forge.Scripting` 只为 `PluginLoadContext`/`ManagedImage`)。

## 关键决策

- 重载先建后换:新插件创建成功才替换并卸载旧的,失败不影响运行中的插件。
- **加载前校验,失败即拒。** 入口程序集只读一次进内存:先算 SHA-256 与 `assemblySha256`(十六进制,大小写不敏感)比对,再用这份**同一份字节**装载;之后才创建加载上下文,校验通过前没有任何插件代码可运行。`files` 里每个文件必须存在且哈希一致;目录中(递归)任何未被 `files` 声明的文件一律拒绝并指名该文件——入口程序集本身由 `assemblySha256` 覆盖,故不要求出现在 `files` 中。相对名按 `/` 分隔、大小写不敏感匹配,越出插件目录的声明直接拒绝。缺 `assemblySha256` 或缺 `files`(旧 Host)一律 `load_failed`。
- **私有依赖只从已验证字节装载。** `files` 里每个文件只读一次:同一份内存字节既用于算 SHA-256 与声明比对,也用于之后的装载。托管 `.dll` 按程序集简单名索引交给 `PluginLoadContext.FromVerifiedImages`,所以依赖何时首次被解析(包括工具调用中途)都无关紧要:磁盘副本事后被替换也不会执行。该模式下没有路径回退,不读 `.deps.json`;非共享依赖若不在 `files` 里,该次加载就以指名错误失败。**保证:已验证插件进程内不装载任何本地库。** 两道关口互相独立。第一道在装载前:`files` 里任何文件只要按文件头签名是本地二进制——无 CLI 头的 PE、ELF(`\x7fELF`)、Mach-O(32/64 位、字节序反转与 fat/fat64)——一律拒绝并指名该文件(`load_failed`),与扩展名无关,所以改名成 `.bin`/`.dat` 的 `.so`/`.dylib`/PE 一样被拒;托管 `.dll` 另需有托管元数据(`PEReader`:`HasMetadata` 且有 assembly manifest),否则同样拒绝并指名;同一声明里的两个文件声明同一程序集名也拒绝。纯数据文件(`.json`/`.txt`/`.pdb` 等任意扩展名)只要不是上述本地二进制仍可作为声明过的数据文件存在。第二道在加载上下文里:已验证模式下 `LoadUnmanagedDll` 绝不返回零(返回零等于交回默认探测,会去插件目录或操作系统里找),而是抛 `DllNotFoundException` 指名该库并说明已验证插件不支持本地库——插件程序集里的 `DllImport` 先经它自己的 ALC 解析,异常即到此为止;因此即便某个本地二进制逃过签名识别、或被就地替换,`DllImport` 也拿不到它。
- 一次 `worker.execute`/`worker.command`/`worker.prompt` 一个 `HostCallFacade`:该次调用的 `runId` 与绑定上下文不可变地随实例携带,并发执行之间不共享任何可变上下文。
- **注册记录器记录并校验全部非 UI 贡献。** `IRegistrationContext` 接受工具、可执行命令(`AddCommand(IPluginCommand)`)、Markdown 工作流、Agent 声明、提示词贡献者、面板声明与权限需求;名称/ID 非空、同类不重复、枚举(`ToolSideEffect`/`AgentThinkingEffort`/`PermissionKind`/面板 kind)必须是已定义值、`run_code` 身份不得被工具或命令占用;`Agent` 必须有 prompt、model 与合法 `maxOutputTokens`,`Workflow` 必须有 body。任何一项不合法都以 `load_failed` 指名失败,不静默丢弃。只有元数据的 `AddCommand(CommandDeclaration)` 明确失败:命令必须有可执行体,不能发布一个点了没反应的命令。`AddPanel` 只记录声明,不实例化控件。
- **权限需求必须是 manifest 子集。** `worker.load` 下发 Host 在批准前读到的 manifest 权限;注册期 `RequirePermission` 出现 manifest 未声明的项时加载以 `load_failed` 拒绝,不隐式授权也不丢项。
- **加载结果。** `worker.load` 返回 `{tools, contributions}`;运行期 `LoadedPlugin` 同时保留工具、命令体与提示词体以及完整声明快照,`worker.unload` 一并丢弃,卸载后任何注册项都不可再调用。
- **超时。** 每次插件调用按请求的 `timeoutMilliseconds` 建协作式取消预算;预算用完返回错误结果并注明由 Host 杀进程树收尾,Worker 自身不承诺能停下不合作的代码。
- 命令行:`--pipe <name>` 与可选 `--max-concurrency <n>`;`--dotnet <path>` 接受但忽略(Worker 不启动子进程,旧 Host 仍会传它),其余未知参数仍是退出码 2。
- 并发上限默认 8;工具异常只返回错误结果,不拖垮进程;取消向上抛出而不回复。
- 入口类型:唯一公开 `IPlugin` 实现,多个则按 `Info.Id` 匹配,零或多匹配即 `load_failed`。
- 连接先订阅处理器再连管道,避免 Host 在接入瞬间发出的 `worker.load` 丢失。
- **不含 P/Invoke,也不自管进程组。** 原生进程归属已全部迁到 Host 侧的 `forge-extension-host`(`posix_spawn` + `POSIX_SPAWN_SETPGROUP`):Worker 不再调用 `setpgid`(原来的 `WorkerProcessGroup` 已删除),本项目里没有任何 `DllImport`/`LibraryImport`、没有 `Marshal`、没有 unsafe,启动时也不需要做任何平台相关动作——进程组由 Host 在 spawn 系统调用内建立,子进程执行第一条指令时已是自成一组的组长,Host 用 `kill(-pgid, SIGKILL)` 整组清理并自管收割循环。这个组**不是硬边界**:插件代码可自行 `setsid`/`setpgid` 脱离,Host 被强杀时 POSIX 侧没有 KILL_ON_JOB_CLOSE 等价物;Windows 上 Worker 仍自动继承 Host 的 Job Object。
- 退出码:0 正常/Host 关闭,1 协议或连接失败,2 命令行错误。
