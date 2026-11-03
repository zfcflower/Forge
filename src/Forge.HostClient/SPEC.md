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
- **回传上下文取自发起调用。** Host 的 `host.tool_call` 总带 `sessionId`/`workingDir`/`depth`,原样用于审批与 `ToolInvocation`。`workingDir` 必须是绝对路径,否则不进审批、不执行,直接返回错误结果。`HostToolWorkingDirectory`/当前目录只作为旧 Host(请求不带 `workingDir`)的兜底。
- **崩溃保留快照,同实例重连。** Host 崩溃置 `Failed` 但保留最后已知 `Tools`;主动 `StopAsync` 清空。`StartAsync` 在 `Stopped` 与 `Failed` 均合法:从 `Failed` 是重连(新连接,必要时新进程),成功进入 `Running` 时先丢弃崩溃前的旧快照并只触发一次 `ToolsChanged`,未被新 Host 重新注册的条目不会残留。崩溃后的自动、有上限的重启策略不属于本模块,由组合根(Forge.App)实现:它观察 `StatusChanged == Failed` 并调用同一实例的 `StartAsync`。
- **已知限制:** `IIpcPeer` 仅有每 peer 一个默认超时,`RequestTimeout` 约束包括长插件运行在内的所有往返;无 per-call 超时。
