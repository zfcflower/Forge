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
- 协议:`worker.load/execute/unload/stop`。`worker.load` 携带 `assemblySha256` 与 `files:{相对名: sha256}`;`worker.execute` 携带 `sessionId/workingDir/depth`(缺 `depth` 视为 0)。`worker.execute` 的任何结局都是 `{content,isError}`;`IpcError` 只用于参数畸形、不支持的操作与加载失败。
- `host.tool_call` 必须带发起该次调用的 `sessionId`、`workingDir`、`depth`,不是共享字段。

## 关键决策

- 重载先建后换:新插件创建成功才替换并卸载旧的,失败不影响运行中的插件。
- **加载前校验,失败即拒。** 入口程序集只读一次进内存:先算 SHA-256 与 `assemblySha256`(十六进制,大小写不敏感)比对,再用这份**同一份字节**装载;之后才创建加载上下文,校验通过前没有任何插件代码可运行。`files` 里每个文件必须存在且哈希一致;目录中(递归)任何未被 `files` 声明的文件一律拒绝并指名该文件——入口程序集本身由 `assemblySha256` 覆盖,故不要求出现在 `files` 中。相对名按 `/` 分隔、大小写不敏感匹配,越出插件目录的声明直接拒绝。缺 `assemblySha256` 或缺 `files`(旧 Host)一律 `load_failed`。
- **私有依赖只从已验证字节装载。** `files` 里每个文件只读一次:同一份内存字节既用于算 SHA-256 与声明比对,也用于之后的装载。托管 `.dll` 按程序集简单名索引交给 `PluginLoadContext.FromVerifiedImages`,所以依赖何时首次被解析(包括工具调用中途)都无关紧要:磁盘副本事后被替换也不会执行。该模式下没有路径回退,不读 `.deps.json`;非共享依赖若不在 `files` 里,该次加载就以指名错误失败。**保证:已验证插件进程内不装载任何本地库。** 两道关口互相独立。第一道在装载前:`files` 里任何文件只要按文件头签名是本地二进制——无 CLI 头的 PE、ELF(`\x7fELF`)、Mach-O(32/64 位、字节序反转与 fat/fat64)——一律拒绝并指名该文件(`load_failed`),与扩展名无关,所以改名成 `.bin`/`.dat` 的 `.so`/`.dylib`/PE 一样被拒;托管 `.dll` 另需有托管元数据(`PEReader`:`HasMetadata` 且有 assembly manifest),否则同样拒绝并指名;同一声明里的两个文件声明同一程序集名也拒绝。纯数据文件(`.json`/`.txt`/`.pdb` 等任意扩展名)只要不是上述本地二进制仍可作为声明过的数据文件存在。第二道在加载上下文里:已验证模式下 `LoadUnmanagedDll` 绝不返回零(返回零等于交回默认探测,会去插件目录或操作系统里找),而是抛 `DllNotFoundException` 指名该库并说明已验证插件不支持本地库——插件程序集里的 `DllImport` 先经它自己的 ALC 解析,异常即到此为止;因此即便某个本地二进制逃过签名识别、或被就地替换,`DllImport` 也拿不到它。
- 一次 `worker.execute` 一个 `HostCallFacade`:该次调用的 `sessionId/workingDir/depth` 不可变地随实例携带,并发执行之间不共享任何可变上下文。
- Worker 只服务工具:插件调用 `AddCommand/AddWorkflow/AddPanel/RequirePermission` 时加载以 `load_failed` 失败并指名该项贡献,不静默丢弃。
- 命令行:`--pipe <name>` 与可选 `--max-concurrency <n>`;`--dotnet <path>` 接受但忽略(Worker 不启动子进程,旧 Host 仍会传它),其余未知参数仍是退出码 2。
- 并发上限默认 8;工具异常只返回错误结果,不拖垮进程;取消向上抛出而不回复。
- 入口类型:唯一公开 `IPlugin` 实现,多个则按 `Info.Id` 匹配,零或多匹配即 `load_failed`。
- 连接先订阅处理器再连管道,避免 Host 在接入瞬间发出的 `worker.load` 丢失。
- **自管进程组。** 非 Windows 上,进程启动后的第一个动作是 `setpgid(0, 0)`(`WorkerProcessGroup`,本项目的唯一 P/Invoke:`DllImport("libc", SetLastError = true)`,不用 unsafe),在解析命令行与连管道之前完成。从此 Worker 以自己的 pid 为组号自成一组,其后代默认留在组内,Host 用 `kill(-pgid, SIGKILL)` 整组清理。`setpgid` 失败或平台无 libc 时只往 stderr 写一行诊断并继续运行(Host 退回按句柄枚举杀树);Windows 上该方法不做任何事(子进程自动继承 Host 的 Job Object)。
- 退出码:0 正常/Host 关闭,1 协议或连接失败,2 命令行错误。
