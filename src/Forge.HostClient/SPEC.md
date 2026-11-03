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
- **崩溃保留快照。** Host 崩溃置 `Failed` 但保留最后已知 `Tools`;主动 `StopAsync` 清空。`StartAsync` 仅在 `Stopped` 合法,重连即新实例。
- **已知限制:** `IIpcPeer` 仅有每 peer 一个默认超时,`RequestTimeout` 约束包括长插件运行在内的所有往返;无 per-call 超时。
