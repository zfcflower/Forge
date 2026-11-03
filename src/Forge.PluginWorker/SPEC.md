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
- **残留窗口(已知且只能记录)。** `PluginLoadContext` 是 sealed 且不提供按字节解析私有依赖的入口,依赖(连同其 `AssemblyDependencyResolver` 读取的 `.deps.json`)仍在加载时按路径从磁盘读取。因此入口程序集不受替换影响,但“校验完成 → 依赖首次装载”之间存在依赖替换窗口。
- 一次 `worker.execute` 一个 `HostCallFacade`:该次调用的 `sessionId/workingDir/depth` 不可变地随实例携带,并发执行之间不共享任何可变上下文。
- Worker 只服务工具:插件调用 `AddCommand/AddWorkflow/AddPanel/RequirePermission` 时加载以 `load_failed` 失败并指名该项贡献,不静默丢弃。
- 命令行:`--pipe <name>` 与可选 `--max-concurrency <n>`;`--dotnet <path>` 接受但忽略(Worker 不启动子进程,旧 Host 仍会传它),其余未知参数仍是退出码 2。
- 并发上限默认 8;工具异常只返回错误结果,不拖垮进程;取消向上抛出而不回复。
- 入口类型:唯一公开 `IPlugin` 实现,多个则按 `Info.Id` 匹配,零或多匹配即 `load_failed`。
- 连接先订阅处理器再连管道,避免 Host 在接入瞬间发出的 `worker.load` 丢失。
- 退出码:0 正常/Host 关闭,1 协议或连接失败,2 命令行错误。
