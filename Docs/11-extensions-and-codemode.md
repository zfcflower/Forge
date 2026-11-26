# 11 扩展体系与 codemode

FORGE 的扩展执行全部在进程外：主程序保持 Native AOT、不可变；插件、声明式工作流与 `run_code` 脚本由常驻 JIT 的 Extension Host 负责编译与调度，实际执行在独立的 Plugin Worker 或一次性 Script Executor 里。

## 11.1 三种扩展形态

| 形态 | 载体 | 执行者 | 典型用途 |
|---|---|---|---|
| Markdown 技能 | `SKILL.md`（frontmatter + 正文） | 模型阅读并执行 | 行为与知识、动态工作流 |
| 声明式工作流（L0） | `workflow.json`（步骤 + `after` + JSON 路径映射） | Host 内 `WorkflowEngine` | 组合已有工具，不写 C# |
| 动态 C# 插件（L2） | `manifest.json` + `.cs` 源码或预编译程序集 | 每插件一个 `PluginWorker` | 新工具、命令、Agent、提示词贡献 |

- 插件可以注册：工具、可执行命令（`IPluginCommand`）、Markdown 工作流、Agent 声明、提示词贡献者、面板声明、权限需求。
- 面板只是声明（`UiSchema.DefinitionJson`），本阶段**不注册也不渲染**（见 14）。
- 模型提供商内置，不开放插件注册。

## 11.2 SDK 与 IPC

- `Forge.Plugin.Sdk` 3.0.0：插件源码引用的稳定契约与全部线上 DTO（工具/命令/工作流/Agent/提示词贡献者/面板/贡献集/权限/状态查询/代码执行请求与结果，外加 `IHostFacade`、`IPlugin` 等入口）。逐项清单以 `src/Forge.Plugin.Sdk/SPEC.md` 为准，本文件不复制。
- 线格式 MessagePack，成员按整数 `Key` 编号：已发布的 Key 不改号、不复用，新增成员只追加；枚举按整数传输、接收端校验已定义值；无反射序列化。
- IPC：命名管道（Linux/macOS 为 Unix 域套接字）+ 4 字节长度前缀 + MessagePack 帧；协议版本 2；单帧上限 16 MiB（零长度/超限帧读侧拒绝并跳帧重同步，超过 64 MiB 重同步上限才关连接）；错误分层为 `IpcProtocolException` / `IpcRemoteException` / `IpcTimeoutException` / `IpcConnectionClosedException` / `IpcVersionMismatchException`（拒绝该帧、连接保持可用）。
- 操作分组：宿主控制（`host.stop`/`host.tool_call`/`plugin.problem`）、插件（`plugins.refresh`/`plugins.status`/`plugin.execute`/`approve`/`revoke`/`disable`/`enable`/`delete`/`plugin.command`/`plugin.prompt`）、注册（`tools.register`/`unregister`、`contributions.register`/`unregister`）、脚本（`code.execute`、执行器侧 `executor.load/run/stop`）、扩展编写（`extensions.stage`/`pending`/`commit`/`discard`）、安装（`extensions.install`）、Worker（`worker.load`/`execute`/`command`/`prompt`/`unload`/`stop`）、终端（`terminal.*`）。

## 11.3 插件包与发现

- `manifest.json`：`id`、`version`、`entryAssembly`、`permissions: [{kind, scope?}]`、`dependencies`，以及 `kind`（缺省 `plugin`）、`workflow`、`tool:{name,description,inputSchema}`。
- 旧 `plugin.json` 的 1.x 包只产生 `LegacyPluginPackage` 问题，不解析、不装载、不执行。
- 发现与准备（manifest 解析、源码读取、Roslyn 编译、镜像哈希）逐插件包 try/catch：文件/路径/无效包一律转成 `PluginIssue`，健康插件照常注册；取消照抛。`id`/`version` 按路径段规则校验（拒绝 `..`、非法文件名字符、首尾空白、路径分隔符、Windows 保留设备名）。
- 编译产物按内容寻址存放，未改动的插件不重编；无源码的预编译插件按程序集字节哈希。
- 编译缓存有主动失效（内存卫生）：refresh 替换/移除、revoke、disable、delete、commit 成功时按缓存键逐出；磁盘上的版本化产物不动，内容未变的插件回归时仍命中 `bin`。

## 11.4 审批引擎

- 组合指纹：`ApprovedHash = SHA256(SourceHash + DependencyHash + SdkVersion)`，三者任一漂移即批准失效；行尾归一化（CRLF/CR/LF 等价）使纯行尾修改不失效。
- `DependencyHash` 由 Host 的 `DependencyHashSchema` v2 算出：manifest 依赖、声明的权限、镜像内各文件摘要三类元组，每条带类别域（`dep`/`perm`/`file`），域、键、值各自按 UTF-8 字节长度定帧，条目按序数排序、重复折叠。改任一依赖 DLL、入口程序集、新增同目录文件或改权限，都会让旧批准失效。
- “镜像” = 入口程序集 + 其可加载目录内**所有文件**，逐个按读到的字节算 SHA-256；加载前重算并作为 `worker.load` 的 `assemblySha256` 与 `files` 下发，Worker 校验并拒绝不匹配（换文件 → 拒载）。
- 状态矩阵：`Draft` → `RequiresApproval`（可批准）；`Disabled`/`Uninstalled` → `Blocked`（再批准不可恢复）；无记录 → `RequiresApproval`。只有三个候选哈希与记录一致且指纹等于 `ApprovedHash` 才 `Allowed`。`SetApproved` 只刷新内容哈希并保留封锁状态；`Revoke` 对封锁状态是空操作。
- 持久化 `<data>/approvals.db`：一行一个 `<pluginId, version>`，主键 `WITHOUT ROWID`、BINARY 排序；`status`/`origin` 存枚举名；时间戳只接受规范 `"O"` 文本；`PRAGMA user_version` 是 schema 阶梯（0 新建/空库、1 无 origin 列、2 当前，>2 由更新版本写过即 `InvalidDataException`）。
- 每条写 = 一个 `BEGIN IMMEDIATE` 事务（事务内 SELECT → 状态机 → 单行 upsert）；提交成功后才发布到内存快照（persist-before-publish）；`synchronous=FULL`；WAL 不可用时回退回滚日志并写一行 stderr。
- 读路径：先读 `PRAGMA data_version`（约 3 µs），未变命中内存快照，变了整表重读再交换；`Reload()` 无条件重读、成功才换。
- 一次性 legacy 导入：`user_version=0` 时在同一事务里建表、导入 `approvals.json`、写标记并置 2；标记是权威（旧 JSON 被还原也不会复活已撤销的批准）。
- 失败即封闭：垃圾字节、被截断、路径是目录、版本高于本程序、无法识别的状态/来源/时间戳，构造期一律 `InvalidDataException`（消息含路径与行列名），绝不返回一个空 store。
- 应用侧只读：App 不持有可写审批连接；设置页 `approvals` 分区用只读连接计数与列记录，库不可读时说“不可读”，不显示成“没有插件”。

## 11.5 Extension Host

- 常驻 JIT 进程，主程序是它唯一的 IPC 客户端。命令行：`--pipe`（必需）、`--plugins`、`--data`、`--bundled`、`--builtin`、`--dotnet`、`--worker`、`--executor`、`--tool-timeout`、`--concurrency`、`--script-workers`。
- 职责：发现与注册、生命周期、审批把关、编译服务（经 `Forge.Scripting`）、声明式工作流执行、Worker 与执行器管理、进程归属、交互终端 PTY。**不加载插件代码、不运行脚本**。
- **三个 JIT 进程与生命周期**：Host 常驻，每应用实例 1 个；`PluginWorker` 每插件 1 个（镜像字节一变就杀进程树重启，见 11.6）；`ScriptExecutor` 每次 `code.execute` 1 个，跑完（成功/失败/取消/超时）杀整棵进程树、绝不复用（见 11.7）。三者都跑在 **.NET 11 / `net11.0`** 上，AOT 主程序不加载那份运行时。
- **为什么必须进程外**：把 CoreCLR 托管进 Native AOT 进程不被支持——`nethost`/`hostfxr` 的托管 API 只支持 framework-dependent 部署，且 CLR 不能与 NativeAOT 运行时同进程共存；进程外 JIT 同时是“主程序不可变、插件代码进不来”这条边界的实现方式，不是权宜之计。
- 审批以磁盘字节为准：`plugin.execute` 每次从磁盘重读并重算哈希；批准在落盘前就在闸门内按磁盘当前字节重算并比对，不符即 `approval_mismatch` 且不写记录；记录自身已 `Approved` 却不再覆盖磁盘内容时退回 `Draft`。
- 每插件一把异步闸门：批准/撤销/刷新与一次执行的“重算哈希 → 审批校验 → 确保 Worker 已装载 → 再校验 → 派发”互斥；`plugin.revoke` 在闸门内注销工具、杀 Worker 进程树并等待在途计数归零（`RevokeDrainWindow` 默认 10 s）。
- 闸门按**根调用令牌**归属：顶层执行与审批/撤销/刷新独占排队；嵌套调用只在闸门空闲或正被同一个根持有时进入（同根兄弟并行做哈希/装载/发布），否则立即 `plugin_busy`；一旦有独占调用排队，新的同根嵌套调用也改为 `plugin_busy`，避免饿死审批。
- 条目生命周期：`plugins.refresh` 永不丢弃 `PluginState`；拿到闸门后必须按 id 重新解析条目（插件已删除 → `unknown_plugin`，条目已被替换 → `plugin_changed`）；锁序固定“刷新级闸门 → 每插件闸门”。
- 撤销工作流包没有 Worker 可杀：经取消令牌终止 Host 内已在途的 `WorkflowEngine.RunAsync`，并推 `plugin.problem` 的 `workflow-terminated` 通知。
- **失败预算**：每插件连续失败计数，4 次异常事件后拒绝一切执行（`worker_disabled`），只由 `plugins.refresh` 或一次真正成功的工具调用/命令/提示词求值清零；刚好耗尽时推一次 `worker-disabled` 通知。
- **取消**：调用方取消后再等 `CancelGraceWindow`（默认 2 s）让执行自行完成；窗口内完成按正常结果返回，否则取消该请求、杀 Worker 进程树、重启并重载，计一次“取消杀”。工具超时监督独立于调用方取消。
- **每次调用一个预算**：插件工具取请求 `timeoutMilliseconds` 并以 `--tool-timeout` 为上界，命令/提示词/脚本夹到 1–600000 ms；deadline 覆盖“等闸门 + 重算哈希 + 确保装载 + 排队等槽 + 派发”，派发只拿剩余预算；预算在派发前用尽返回 `plugin_queue_timeout`（不杀未执行过的健康 Worker、不计失败预算），真正派发后超期才杀树并计一次异常。
- **进程归属**（唯一的 P/Invoke 类 `WorkerProcessOwnership`）：
  - Windows：每 Worker 一个私有 Job Object，只置 `KILL_ON_JOB_CLOSE`；先 `AssignProcessToJobObject` 再发 `worker.load`，确保插件代码不可能在分配前运行。分配失败即杀刚启动的 Worker 并报 `worker_start_failed`，绝不在 Job 之外运行插件代码。
  - Linux/macOS：Host 侧 `posix_spawnp` + `POSIX_SPAWN_SETPGROUP(0)`（组在 spawn 内原子建立），stdout/stderr 经 `pipe2`/`pipe`+`fcntl` 重定向；`Assign(int)` 校验 `getpgid(pid) == pid`；强制收割循环顺序为 `waitid(WNOWAIT)` → `kill(-pgid, SIGKILL)` → `waitpid`。
  - 清理必须可验证：`Kill`/`TerminateJobObject`/`kill(-pgid)` 的返回值、异常与等待结果都不得吞；`Process.Kill` 抛错、等待超时、进程仍存活、slot 退役窗口（30 s）用尽、主管道未关闭都记入清理报告，`RunAsync` 在报告非空时返回非零退出码，`DisposeAsync` 抛 `host_shutdown_failed`。
- **关闭栅栏**：`host.stop`、连接关闭或 `DisposeAsync` 先把“正在停止”置为不可逆，再快照、关管道、拆资源；此后新到的 `plugins.*`/`code.execute` 一律 `host_stopping`；启动中的 Worker 从操作系统创建那一刻起登记，关闭时先等它们收尾再杀。
- **插件问题通知**：`plugin.problem{PluginId, Code, Message, Count}`，只在两处发送——工作流包在途 run 被终止（`workflow-terminated`，Count = 被终止的 run 数）与失败预算刚好耗尽（`worker-disabled`）。fire-and-forget、自带 5 s 期限；主程序把已知代号翻成本地语言，未知代号直接显示英文句子。
- 坏插件不拖垮启动；stderr 只在发送本身失败时留一行。

## 11.6 Plugin Worker

- 一个进程一个插件；协议 `worker.load/execute/command/prompt/unload/stop`。
- 加载前校验：入口程序集只读一次进内存，先算 SHA-256 与 `assemblySha256` 比对，再用**同一份字节**装载；`files` 每项必须存在且哈希一致；目录中任何未被声明（递归）的文件一律拒绝并指名。当前 Host 把入口也列入 `files`；Worker 为兼容旧 Host，允许入口只由 `assemblySha256` 覆盖。
- **私有依赖只从已验证字节装载**：托管 `.dll` 按程序集简单名交给 `PluginLoadContext.FromVerifiedImages`（只从内存 `LoadFromStream`，不探测插件目录、不读 `.deps.json`）；非共享且未提供的依赖以 `FileNotFoundException` 指名失败，不回退磁盘。
- **已验证插件进程内不装载任何本地库**：任何按文件头签名是本地二进制（无 CLI 头的 PE、ELF、Mach-O 含 fat）的文件一律拒绝（与扩展名无关）；ALC 的 `LoadUnmanagedDll` 抛 `DllNotFoundException` 而不是返回零，所以 `DllImport` 不可能拿到它。
- 注册记录器校验全部非 UI 贡献：名称非空、同类不重复、枚举必须是已定义值、`run_code` 身份不得被工具或命令占用、Agent 必须有 prompt/model/合法 `maxOutputTokens`、工作流必须有 body、只带元数据的命令明确失败；不合法项以 `load_failed` 指名失败，不静默丢弃。
- 权限需求必须是 manifest 权限的子集，否则 `load_failed`；超时按请求 `timeoutMilliseconds` 协作式取消，预算用完返回错误并注明由 Host 杀进程树；每插件有效并发默认 4（Host 派发闸门 `--concurrency` 默认 4；Worker 自身的 `--max-concurrency` 默认 8 只是内层上限）；工具异常只返回错误结果，不拖垮进程。
- 入口类型必须是唯一公开 `IPlugin` 实现（多个则按 `Info.Id` 匹配，零或多匹配即 `load_failed`）；`host.tool_call` 只发 `{runId, name, args}`，session/工作目录/深度由 Host 的绑定表决定。
- 无 P/Invoke、无 unsafe、不自管进程组。

## 11.7 Script Executor 与 codemode（`run_code`）

### 表面与编译

- 模型写一段 C# 脚本，脚本可 `await`、可并行（`Task.WhenAll`）调用已注册工具，只把最终结果一次返回模型。
- 编译服务常驻 Host（`ScriptCodeCompiler`，唯一缓存所有者），按“编译表面指纹 + 源码 + 全部选项”缓存编译产物与错误；同一缓存键的并发编译共享一次，取消隔离；失败编译也入 LRU。
- 产物是 PE 映像（`CSharpCompilation` + `Emit`，Embedded PDB、确定性），不是 CSharpScript 提交；`ScriptArtifact` 携带长度、SHA-256、globals MVID、运行时大版本、契约版本、入口名。
- 脚本白名单与语义检查：固定引用集与命名空间，`AllowUnsafe=false`，`#r`/`#load`/`unsafe`/`typeof`/指针在编译前拒绝，禁用命名空间与受限类型（`Environment`、`AppContext`、`Thread`、`TaskFactory` 等）由语义遍历拒绝；`using` 指令不拒（它不绑定可达 API），`System.IO` 仅放行 `Path` 的纯字符串成员，`File`/`Directory`/`Path.GetFullPath` 仍拒；诊断带行列、条数与字符数有界。这不是进程隔离（内存不限，CPU 空转靠杀进程）。
- 脚本可见表面（`Forge.ScriptRuntime`）：`ScriptGlobals`（`Tools`/`Log`/`Cancellation`）、`ToolsFacade`（`Call`/`Call<T>`/`CallJson`/`CallJson<T>`/`Json`/`Json<T>`/`List()`/`Describe(name)`）、`ToolCallException`；类型仍在 `Forge.Scripting` 命名空间，源码与旧引擎兼容。
- `Tools.List()`/`Describe(name)` 只读本次运行的元数据、不发 IPC；这是主程序提示里不再逐工具列 schema 的另一半——模型要细节时脚本自己问一句。

### 执行

- **每次调用一个全新执行器进程**：编译 → spawn `Forge.ScriptExecutor` → `executor.load`（原始 PE 字节 + 描述，单帧 <16 MiB，映像 ≤8 MiB）→ `executor.run`（只拿剩余预算）→ 结果；成功、失败、取消、超时或 `RequiresTermination` 之后，Host **先杀掉并等待整棵进程树**才返回。已执行过的进程绝不复用。
- 执行器只依赖 Ipc/Protocol/SDK/ScriptRuntime，不引用 Roslyn；发布闭包里没有 `Microsoft.CodeAnalysis*`。
- 运行回路 `ScriptHarness`：在 `Task.Run` 上跑，预算由 `WaitAsync` 收口；超时/取消先给 1 秒宽限，停下即普通错误结果，否则判为“被放弃、必须终止承载进程”；脚本返回 `Task` 时在同一预算下等待并解包；结果与日志有界后附在结果后。
- 内层工具调用经 `host.tool_call` 回 Host → 主程序审批、限流、截断；未知工具与 `run_code` 自身永久拒绝（不递归）。
- 预算与上限（`tools` 分区可改）：内部调用并发上限 1–32（默认 8）、返回结果字符上限 1000–400000（默认 40000）、单次内部结果上限 500–400000（默认 20000）、可见工具名单（逗号/分号/空白分隔，末尾 `*` 前缀通配，空白 = 全部候选）；`run_code` 的 `timeout_seconds` 默认 60、上限 600；`--script-workers` 默认 4 是并发槽位，不是池。
- **脚本面**：只收“插件工具 + 所有 `SideEffect == ReadOnly` 的文件/web/MCP 三族”；`write`/`edit`/`copy`/`move`/`delete`/`bash`、所有 git/PR 工具（含只读）、扩展编写工具一概不进（既调不到也看不到）；同名遮蔽：插件工具先注册者生效，并剔除与内置/会话策略/文件/git/PR/web 工具同名的插件工具；用户关掉的工具同样从脚本面去掉。
- **脚本内审批折叠**：同（run, tool）的待审调用串行——首个建卡，其余等在卡后（不建卡、不发 toast），卡上显示“本脚本还有 N 个同类调用在等待”，并有“允许本次运行内剩余同类调用”按钮；许可只存在 handler 内存里（以 runId 为键，FIFO ≤64），不落盘。
- 嵌套规则：`run_code` 只能在顶层执行；脚本或插件回调里再调 `run_code` 一律拒绝；`$run_code` 是保留身份，插件不得以它注册或执行。
- 实测（正式路径）：暖调用约 134–137 ms（进程创建约 48 ms、新进程首次初始化约 38 ms、杀树约 2 ms）；实现自测约 129–131 ms。预启动池未采用。

## 11.8 扩展编写工具与安装

- 模型用 `extension_list` / `extension_create` / `extension_update` / `extension_disable` / `extension_delete` 操作扩展（仅根 Agent 可见、可调用；子 Agent 一律剔除）。
- 暂存 → 审批 → 安装三步：
  - 暂存只写 `<plugins>/.pending/<id>/`；`.pending` 与 `.trash` 被 `PluginCatalog` 扫描跳过（**暂存区永不加载**）。路径/大小/白名单：`.cs`/`.json`/`.md`，单文件 ≤256 KiB、总量 ≤1 MiB，作者不得提供 `manifest.json`，plugin 恰好一个 `.cs`，workflow 必须有 `workflow.json` 且 `tool` 合法；任何不合规是一个 IPC 错误且一个文件都不写。
  - `extensions.commit` 在刷新级闸门 → 该插件闸门内：按暂存目录当前字节重算三种哈希并与请求比对（不符返回 `approval_mismatch` 且什么都不动）→ 线上目录移进 `.trash`、暂存目录上位 → 停旧 Worker → 走既有批准路径 → 发布工具/贡献；任一步失败把旧目录换回、恢复旧条目与旧记录（用 `CancellationToken.None` 重新应用旧镜像）；成功后删 `.trash`、跨版本时对旧版本 `Revoke`。
  - `extensions.discard` 删除草稿。
- 审批卡显示全文（新建）或与线上版本的逐文件 Diff（更新），另示权限、依赖、三种哈希与诊断；只有“批准/拒绝”。重启后 `extensions.pending` 的项以“重启前提交”卡出现，不阻塞任何 Agent。
- 删除后同 id+version 永久封锁（`Uninstalled` + 清空 `ApprovedHash`），必须换版本号。
- `plugin.disable`/`enable`/`delete` 各占插件闸门：disable 先注销工具与贡献、杀 Worker 树、等在途归零，再写记录；enable 只写记录再应用（批准已不覆盖磁盘内容时保持不运行、不报错）；delete 先按 disable 停掉，再标 `Uninstalled`、删目录与暂存草稿。
- 来源维度：`Installed` / `Bundled` / `BuiltIn`；`--bundled` 目录里的插件首启复制进插件目录（已存在则跳过，绝不覆盖用户改动），`--builtin` 目录只读发现，BuiltIn 不可删除/禁用（Host 侧 `builtin_blocked`，界面同时不显示按钮）。当前不附带任何捆绑/内置插件。
- `extensions.install`：主程序先下载、校验 SHA-256、解包到 `<data>/install/` 下的临时目录；Host 拒绝越界 `SourceDir`、验证 manifest、trash 回滚式落位，新版本恒从 `Draft` 起步——**安装从不批准**。

## 11.9 调用绑定、路由与审批

- 主程序为每次执行分配 `ExtensionCallContext`（`runId`、`sessionId`、`workingDirectory`、`depth`、`pluginChain`、`rootRunId`），新请求必带；Host 校验形状（runId `[A-Za-z0-9_-]`、≤128 字符；工作目录必须完全限定的绝对路径；depth 0–8；链 ≤8 环且无重复/空白；目标不在自己的链里）。
- 回传调用 `host.tool_call` 必须绑定在途调用：`{runId, pluginId, name, args}`；缺 `runId` 由解码拒绝，空白 `runId`/`pluginId`/`name` 显式拒绝；`runId` 必须命中本连接世代仍在途的私有 pending 记录，且 plugin 身份与 `pluginId` 相等。会话、工作目录、深度、插件链**只取 pending 记录**（发起时的可信上下文），Worker 自报的一律忽略。
- 回传按会话路由（`IHostToolCallRouter`）：工具解析与审批都走该会话的 `AgentHost.Tools` 与该会话的审批策略（`SessionRuntime.CreatePolicy`，`src/Forge.App` SPEC「审批队列与 Agent 扩展」）；未知/已关闭会话 fail-closed。子 Agent 的调用用子会话 id，`SessionRuntime` 在 `AgentSubAgentStarted`/`Finished` 上登记/注销根 scope 别名，所以子调用按根工具集解析、卡片显示根会话。
- 插件工具/命令/提示词的三条执行路径共用同一套准入规则、在途表与根内 wait-for 图；检测到潜在环时快速拒绝一条分支（`plugin_wait_cycle`），不扩大并发也不杀持槽调用。
- 插件命令审批名是 `plugin-command:<pluginId>:<name>`（模型侧工具名不含 `:`，永不与内置/外部工具名相等）；插件命令不可记住。**2026-11-24 起插件批准即授权，命令不再单独确认**（它的 `Scope` 恒为域内，两档一致）。
- `extension_*` 对插件永不可解析、永不放行；会话策略对 `extension_list/create/update/disable` 为 `Allow`（真正闸门是插件卡），`extension_delete` 为 `Ask`（两档都问）。
- App 侧记住规则（`ApprovalRules`）：键 = 工具名 + 副作用 + 作用域（会话/项目/全局），只记“允许”；会话级只在内存，项目级与永久写 `<data>/approval-rules.json`（源生成 JSON、原子写、后台线程、写入串行）。`extension_delete`、插件审批卡、插件命令永不可记住；**域外调用也不用记住规则与运行级许可**（规则键只有工具+副作用，记住一次就等于永久放行全域）；插件新版本获批时清除其新旧工具名下的全部记住项。
- 审批卡额外显示 MCP 服务器段（服务器名、传输、`command` + 参数、`cwd`、`url`、`env`/`headers` 的**键名**——值永不出现在卡片上）与即时撤销（记住规则覆盖本工具名时，按钮显示条数，点击撤销全部，本卡决策不受影响）。
- 插件卡无用户决定的撤回（会话释放、关闭、取消）产出独立 `Withdrawn`（≠ Deny）：不 Discard 暂存稿，稿件留待 `extensions.pending` 重启后出现；给 agent 的文本如实说“已撤回、草稿保留待定、不是拒绝”。工具卡的撤回仍是 Deny。

## 11.10 主程序侧：HostClient 与崩溃恢复

- `ExtensionHostClient` 是主程序访问 Host 的唯一入口：`ITool` 代理、贡献快照、命令/提示词调用、`run_code` 代理、终端、扩展编写与安装、插件状态查询。
- 每次调用一个预算（`RequestTimeout` 默认 120 秒；`run_code` 用脚本自己的 `timeout_seconds`；`extensions.stage/commit` 取 5 分钟与默认的较大者）；`IIpcPeer` 默认超时设为无限，由客户端自建 deadline。
- **超时只结束那次调用，不动连接**：移除 pending、给 Host 发 `cancel` 通知、抛 `IpcTimeoutException`；peer、世代、状态与已登记工具快照都不变，下一个请求走同一条连接。只有连接真的关闭才置 `Failed`。
- **崩溃保留快照，同实例重连**：Host 崩溃置 `Failed` 但保留最后已知工具与贡献快照；`StartAsync` 在 `Stopped` 与 `Failed` 都合法（从 `Failed` 是重连，新连接、必要时新进程）；重连前先拆上一条连接、杀掉旧自启进程树；世代号保证旧连接的迟到回调不改写新状态。崩溃后的自动重启策略由 `ExtensionHostSupervisor`（组合根）负责：观察 `StatusChanged == Failed` 并调用同一实例的 `StartAsync`。
- `StopAsync` 先发 `host.stop` 等应答，再给 Host `ProcessExitTimeout` 自行退出（停 Worker 进程树），超时才杀；`HostExitCode` 在自启 Host 离开后给出真实退出码（0 = 自行退出，被杀的 POSIX 进程为 128+信号）。
- 插件状态查询只读 IPC，没有“失败 → 空列表”的成功路径：未运行、断连、超时、应答无法解码都抛出或返回 `IpcTimeoutException`/`InvalidDataException`，调用方必须理解为“未知”而不是“没有插件”；`plugins` 任一行无效则整份应答无效，问题文本带下标与已知身份。
- `plugin.problem` 通知解码成事件（`PluginProblemReported`），没有应答也不重试；缺 id 或代号只记诊断丢弃，不打断事件循环。

## 11.11 codemode 的设置与可见面

- `tools` 分区顶部四个 `run_code` 旋钮（并发、返回结果上限、单次内部结果上限、可见工具名单）；三者经 `AgentRuntimeServices.CodeMode` → `RunCodeProxyOptions.ExecutionProvider` 与 `CodeModeToolRegistry` 在**每次使用**时现读，所以改完对下一回合的描述与之后每个脚本生效，不需要重发布工具集。
- 可见名单只能**收窄**代码定下的候选集（不能把写入/执行类工具放进来）；名单变化会改 `run_code` 的描述字节，因此该工具定义的提示词缓存失效——与“名单变了但缓存还在”不可兼得。
- `run_code` 是否存在由候选集合是否为空决定：没有装插件也会得到 `run_code`（候选含只读文件/web 工具）；扩展宿主启动期间它以 `IsError` 失败（代理存在、宿主未就绪），不隐藏工具。
- 描述由主程序代理维护（只列可调用名字，不逐工具列描述与 schema），`Tools.List()`/`Describe(name)` 提供细节；工具声明、变更指纹与 `code.execute` 的 `tools` 声明来自同一份 `RunCodeToolSurface.Snapshot`，三者不会分歧。

相关文档：[09-prompts-skills-workflows.md](09-prompts-skills-workflows.md)（L0 工作流与技能）、[10-mcp.md](10-mcp.md)（Host 的另一类扩展来源）、[14-limitations.md](14-limitations.md)（沙箱与平台边界）。
