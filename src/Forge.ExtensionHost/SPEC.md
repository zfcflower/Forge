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
- **哈希上报绕行。** 协议无“上报哈希”操作:发现时 upsert `Draft` 记录并刷新内容哈希,主程序读 `approvals.json` 后调用 `plugin.approve`;与当前内容不符的批准被拒(`approval_mismatch`)。
- **审批变更与执行串行。** 每插件一把异步闸门(生命周期跨 WorkerSlot):`plugin.approve`/`plugin.revoke`/`plugins.refresh` 与一次执行的“重算哈希→审批校验→确保 Worker 已加载→再校验→派发”互斥。执行在离开临界区前登记在途计数;`plugin.revoke` 在闸门内作废批准、注销工具、杀 Worker 进程树(以此终止在途调用)、等在途计数归零后才返回,因此 revoke 之后不会再有新执行启动、也不会有被恢复的 Worker 残留。
- **Worker 生命周期。** 超时、崩溃、取消后不合作的调用、启动/加载失败一律计一次异常事件(见失败预算),杀进程树、重启、重载、必要时重新注册工具,只失败在途调用。`host.tool_call` 经 Worker 管道到 Host 再中继主程序,Worker 不自开主管道;中继原样透传 Worker 给出的 `args`(含 `sessionId`/`workingDir`/`depth`),Host 不合并也不改写上下文。`plugin.execute` 的 `depth` 原样转给 `worker.execute`。
- **失败预算。** 每插件一个连续失败计数(在 `PluginState`,与 WorkerSlot 解耦、跨 slot 重建存活):4 次异常事件后拒绝一切执行(`worker_disabled`),只由 `plugins.refresh` 或一次真正完成的成功工具调用清零。启动成功、加载成功、`plugin.approve` 都不清零。
- **取消。** 调用方取消后不立即杀进程:Host 再等一个可配置的宽限窗口(`WorkerSlot.CancelGraceWindow`,默认 2 s)让该执行自行完成;窗口内完成则按正常结果返回,否则取消该请求(尽力发出 IPC `cancel`)并杀 Worker 进程树、重启并重载,计一次“取消杀”。工具超时监督独立于调用方取消:请求本身带 `--tool-timeout` 期限,超时照旧杀进程树。IPC 层把“下发 cancel”与“放弃本地响应”绑在一起,所以宽限窗口内 Host 只能等 Worker 自行完成,无法既下发 cancel 又观察结果;插件若只在收到取消后才退出,会走到杀进程树那一支。
- **进程树清理顺序。** 停止 Worker 时:尽力发 `worker.stop`(不等待其响应)→ 在父进程仍存活时 `Process.Kill(entireProcessTree: true)` → 再拆管道/对端。先等优雅退出会让父进程先死,随后无法枚举其后代。**已知缺口:** 若 Worker 已自行退出(崩溃),BCL 无法枚举其残留后代,这些后代不会被清理;要覆盖该场景需要 Job Object(Windows)或进程组(Unix)决策,当前不引入 P/Invoke。
- **产物布局。** 编译产物按缓存键内容寻址存放,未改动的插件不重编(命中后仍逐个重算镜像文件摘要);无源码的预编译插件按程序集字节哈希。
- **坏插件不拖垮启动。** 每个插件的发现/准备(`manifest.json` 解析、源码读取、编译、镜像哈希)都包在 try/catch 里:文件/路径/无效包错误一律转成 `PluginIssue`(取消异常照抛),健康插件照常注册。manifest 的 `id`/`version` 按 `VersionedOutputStore` 的路径段规则校验(拒绝 `..`、非法文件名字符、首尾空白、路径分隔符),读取器接受的值不会在后面拼路径时才抛异常。
- 工作流引擎(Forge.Workflows)已引用但尚未接线。无向主程序推送插件问题的协议操作,问题仅写 stderr。
