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
- 协议:`worker.load/execute/unload/stop`。`worker.execute` 的任何结局都是 `{content,isError}`;`IpcError` 只用于参数畸形、不支持的操作与加载失败。

## 关键决策

- 重载先建后换:新插件创建成功才替换并卸载旧的,失败不影响运行中的插件。
- 并发上限默认 8;工具异常只返回错误结果,不拖垮进程;取消向上抛出而不回复。
- 入口类型:唯一公开 `IPlugin` 实现,多个则按 `Info.Id` 匹配,零或多匹配即 `load_failed`。
- 连接先订阅处理器再连管道,避免 Host 在接入瞬间发出的 `worker.load` 丢失。
- 退出码:0 正常/Host 关闭,1 协议或连接失败,2 命令行错误。
