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
- 命令行:`--pipe`(必需)、`--plugins`、`--data`、`--dotnet`、`--worker`、`--tool-timeout`、`--concurrency`。Worker 定位:`--worker` → `FORGE_PLUGIN_WORKER` → 与 Host 同目录 → 开发布局兄弟目录。

## 关键决策

- **审批以磁盘内容为准。** `plugin.execute` 每次按磁盘当前内容重算哈希,不信任缓存;内容变更需 `plugins.refresh` 并重新审批。审批记录在 `<data>/approvals.json`,损坏即 fail-closed 退出。
- **哈希上报绕行。** 协议无“上报哈希”操作:发现时 upsert `Draft` 记录并刷新内容哈希,主程序读 `approvals.json` 后调用 `plugin.approve`;与当前内容不符的批准被拒(`approval_mismatch`)。
- **Worker 生命周期。** 超时或断连杀进程树、重启、重载、必要时重新注册工具,只失败在途调用;连续失败 4 次后拒绝重启直到 `plugins.refresh`。`host.tool_call` 经 Worker 管道到 Host 再中继主程序,Worker 不自开主管道。
- **产物布局。** 编译产物按缓存键内容寻址存放,未改动的插件不重编;无源码的预编译插件按程序集字节哈希。
- 工作流引擎(Forge.Workflows)已引用但尚未接线。无向主程序推送插件问题的协议操作,问题仅写 stderr。
