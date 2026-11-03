---
id: forge-extension-host
type: module-design
status: draft
title: Forge.ExtensionHost
parent: forge-architecture
depends-on: [forge-ipc, forge-scripting, forge-approval]
---

# Forge.ExtensionHost

常驻 JIT 进程,主程序(Native AOT)的插件代理:发现、编译、审批把关、Worker 管理。主程序是它唯一的 IPC 客户端;协议见 `Forge.Plugin.Sdk/ExtensionProtocol.cs`。

## 边界

- 依赖 Ipc、Plugin.Sdk、Scripting、Approval;不引用 Core、Abstractions。不加载插件代码:执行一律交给每插件一个的 `PluginWorker` 进程。
- 命令行:`--pipe`(必需)、`--plugins`、`--data`、`--dotnet`、`--worker`、`--tool-timeout`、`--concurrency`。Worker 定位:`--worker` → `FORGE_PLUGIN_WORKER` → 与 Host 同目录 → 开发布局兄弟目录。`--dotnet` 只用于启动 `.dll` 形态的 Worker(命令行=`<runtime> <worker.dll> --pipe <name>`),不传给 Worker——Worker 只接受 `--pipe`/`--max-concurrency`。

## 关键决策

- **审批以磁盘字节为准。** `plugin.execute` 每次从磁盘重读并重算哈希,不信任缓存目录名;内容变更需 `plugins.refresh` 并重新审批。审批记录在 `<data>/approvals.json`,损坏即 fail-closed 退出。
- **审批绑定实际加载的字节。** 每个插件的“镜像”=入口程序集 + 其可加载目录(扁平依赖探测目录)`PluginImage` 内的**所有文件**,逐个按读到的字节算 SHA-256。`DependencyHash` = `Forge.Approval.Hashing.DependencyHash` 把 manifest 依赖与文件摘要(`file:<相对文件名>` 前缀)折在一起的确定性结果,因此替换任一依赖 DLL、入口程序集或新增同目录文件都会使批准失效。加载前重新计算并作为 `worker.load` 的 `assemblySha256` 与 `files` 下发,由 Worker 校验并拒绝不匹配(换文件 → 拒载);Host 侧在发送前已按批准哈希比对,不匹配即 `plugin_not_approved` 且要求 refresh。`files` 的键是入口程序集所在目录下**递归**所有文件的相对路径(`/` 分隔),与 Worker 的“声明外文件一律拒载”规则同一范围;入口程序集自身也列入其中,digest 与 `assemblySha256` 同源(同一次读取)。
- **哈希上报绕行。** 协议无“上报哈希”操作:发现时 upsert `Draft` 记录并刷新内容哈希,主程序读 `approvals.json` 后调用 `plugin.approve`。批准在落盘前就在闸门内按磁盘当前字节重算并比对(`sourceHash`、`dependencyHash` 与 SDK 版本),不符即 `approval_mismatch` 且不写记录——陈旧批准不会覆盖 `approvals.json`。若记录自身已标 `Approved` 却不再覆盖它所描述的内容(指纹对不上),该记录退回 `Draft`,以免留下“看似已批准、实际拒跑”的状态;仍持有可用批准时原样不动。
- **审批变更与执行串行。** 每插件一把异步闸门(生命周期跨 WorkerSlot):`plugin.approve`/`plugin.revoke`/`plugins.refresh` 与一次执行的“重算哈希→审批校验→确保 Worker 已加载→再校验→派发”互斥。执行在离开临界区前登记在途计数;`plugin.revoke` 在闸门内作废批准、注销工具、杀 Worker 进程树(以此终止在途调用)、等在途计数归零后才返回,因此 revoke 之后不会再有新执行启动、也不会有被恢复的 Worker 残留。
- **条目生命周期:闸门内换、闸门内判。** 每插件状态 `PluginState`(连同它的闸门)以插件 id 为键、贯穿整个 Host 生命周期,`plugins.refresh` **永不丢弃**它(包括插件已被删除时)。`plugins.refresh` 在**刷新级闸门**内逐个插件再取该插件的闸门,并在该闸门内完成:用扫描结果替换 `plugins[id]`、`SyncRecord` 同步审批记录、插件缺失时注销工具并停 Worker。`plugin.execute`/`plugin.approve`/`plugin.revoke` 允许先在闸门外捕获条目,**拿到闸门后必须按 id 重新解析**当前条目:插件已删除 → `unknown_plugin`;条目已被替换 → `plugin_changed`(每次 `plugins.refresh` 都为发现的插件重建条目对象,所以闸门前捕获的条目一律失效,调用方重试即可);绝不使用闸门前的旧条目继续哈希校验、装载或派发。锁序固定为**刷新级闸门 → 每插件闸门**,任何路径都不按相反顺序获取,因此不会死锁。
- **测试钩子。** Host 内有一个内部 seam `IHostTestHook.AfterEntryCapturedAsync`(位置:`plugin.execute` 捕获条目之后、取该插件闸门之前),仅当环境变量 `FORGE_HOST_TEST_HOOK` 指向的 JSON 文件(`{"pipe":…,"directory":…}`)pipe 名与本 Host 相符、且目录内存在 `armed` 标记时启用;集成测试用它把一次执行卡在“已捕获、未取闸门”的窗口里,再改动插件目录并 `plugins.refresh`。钩子等待有上限,不会永久卡住 Host。
- **Worker 生命周期。** 超时、崩溃、取消后不合作的调用、启动/加载失败一律计一次异常事件(见失败预算),杀进程树、重启、重载、必要时重新注册工具,只失败在途调用。`host.tool_call` 经 Worker 管道到 Host 再中继主程序,Worker 不自开主管道;中继原样透传 Worker 给出的 `args`(含 `sessionId`/`workingDir`/`depth`),Host 不合并也不改写上下文。`plugin.execute` 的 `depth` 原样转给 `worker.execute`。
- **失败预算。** 每插件一个连续失败计数(在 `PluginState`,与 WorkerSlot 解耦、跨 slot 重建存活):4 次异常事件后拒绝一切执行(`worker_disabled`),只由 `plugins.refresh` 或一次真正完成的成功工具调用清零。启动成功、加载成功、`plugin.approve` 都不清零。`plugins.refresh` 清零时若该 Worker 已因预算耗尽停摆,先丢弃 slot 与已发布的工具登记,重载成功后重新 `tools.register`(声明没变也重发),所以刷新后 Worker 重新运行且工具重新可见。
- **取消。** 调用方取消后不立即杀进程:Host 再等一个可配置的宽限窗口(`WorkerSlot.CancelGraceWindow`,默认 2 s)让该执行自行完成;窗口内完成则按正常结果返回,否则取消该请求(尽力发出 IPC `cancel`)并杀 Worker 进程树、重启并重载,计一次“取消杀”。工具超时监督独立于调用方取消:请求本身带 `--tool-timeout` 期限,超时照旧杀进程树。IPC 层把“下发 cancel”与“放弃本地响应”绑在一起,所以宽限窗口内 Host 只能等 Worker 自行完成,无法既下发 cancel 又观察结果;插件若只在收到取消后才退出,会走到杀进程树那一支。
- **进程树所有权(Windows:Job Object;Linux/macOS:进程组)。** 每 Worker 一个私有 Job Object:`CreateJobObject` → `SetInformationJobObject(JobObjectExtendedLimitInformation)` 只置 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`,不置 `BREAKAWAY_OK`/`SILENT_BREAKAWAY_OK`。进程启动后**先** `AssignProcessToJobObject` 再发 `worker.load`;选定的握手就是“Host 在分配完成后才发 `worker.load`”(Worker 连上管道后一直空闲直到收到 `worker.load`,因此插件代码不可能在分配前运行),子进程自动继承 Job,Worker 自身不需要做任何事。需要 Windows 8 / Server 2012 以上(嵌套 Job);Win7 上若 Host 已在某个 Job 内则分配失败,按下面的 fail-closed 处理。Host 存活期间,Worker 退出/崩溃/被杀/超时/取消/撤销/卸载一律 `TerminateJobObject` 再关句柄,所以**即便 Worker 已自行退出(崩溃),它留下的后代也会被杀**;Host 退出或崩溃时由操作系统按 KILL_ON_JOB_CLOSE 清空每个 Job。
- **失败即封闭(fail-closed)。** Job 创建、限额设置、分配任一失败:杀掉刚启动的 Worker 并报 `worker_start_failed`,**绝不在 Job 之外运行插件代码**。集成测试用环境变量 `FORGE_HOST_TEST_FAIL_JOB_ASSIGN=1` 强制分配失败来覆盖该路径;该变量只由 `WorkerProcessOwnership` 读取,生产路径上不存在。
- **Linux/macOS 的边界与已知限制。** Worker 连管道前的第一个动作是 `setpgid(0, 0)`(见 `forge-plugin-worker`),以自己 pid 为组号自成一组;Host 杀树时先 `kill(-pgid, SIGKILL)`(Worker 已退出时组内残留成员同样被杀),再用 `Process.Kill(entireProcessTree: true)` 兜底。POSIX 进程组**不是硬边界**:后代自己调用 `setsid`/`setpgid` 即可脱离,要不可逃逸需要 cgroups(不做)。`kill(-pgid)` 不做组成员校验:关句柄前 Worker 的 pid 若被系统复用并成为一个新组的组长,理论上会误杀该组(毫秒级时间窗,记录为已知限制);Host 被强杀时 Unix 侧没有 KILL_ON_JOB_CLOSE 的等价物,Worker 只会在管道断开后自行退出,其残留后代不被清理。
- **P/Invoke 隔离。** 仅两个 JIT 项目含 P/Invoke:Host 侧全部集中在 `WorkerProcessOwnership`,Worker 侧全部集中在 `WorkerProcessGroup`;都用 `DllImport(SetLastError = true)` 与安全句柄(`SafeJobHandle`/`SafeProcessHandle`),不使用 `unsafe`、不改 `AllowUnsafeBlocks`;Native AOT 主程序项目不含任何 P/Invoke。
- **进程树清理顺序。** 停止 Worker 时:尽力发 `worker.stop` 并给 `StopGraceWindow`(2 s)让它自行退出 → 按上面的 Job/进程组杀树(该步骤不依赖 Worker 仍存活)→ 兜底 `Process.Kill(entireProcessTree: true)` → 再拆管道/对端并关闭 Job 句柄。超时、取消、撤销、崩溃恢复走同一条清理路径,失败预算/重启/取消逻辑不变。
- **产物布局。** 编译产物按缓存键内容寻址存放,未改动的插件不重编(命中后仍逐个重算镜像文件摘要);无源码的预编译插件按程序集字节哈希。
- **坏插件不拖垮启动。** 每个插件的发现/准备(`manifest.json` 解析、源码读取、编译、镜像哈希)都包在 try/catch 里:文件/路径/无效包错误一律转成 `PluginIssue`(取消异常照抛),健康插件照常注册。manifest 的 `id`/`version` 按 `VersionedOutputStore` 的路径段规则校验(拒绝 `..`、非法文件名字符、首尾空白、路径分隔符),读取器接受的值不会在后面拼路径时才抛异常。
- 工作流引擎(Forge.Workflows)已引用但尚未接线。无向主程序推送插件问题的协议操作,问题仅写 stderr。
