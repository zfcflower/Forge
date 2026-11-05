---
id: forge-plugin-sdk
type: module-design
status: active
title: Forge.Plugin.Sdk
parent: forge-architecture
---

# Forge.Plugin.Sdk

插件源码引用的稳定 SDK 与契约,独立版本化(2.0.0),不依赖 `Core`/`Abstractions`。主程序(AOT)、Extension Host、Plugin Worker、ScriptRuntime 与 ScriptExecutor 都引用它,它是 AOT 主程序与 JIT 进程之间唯一的共享类型面。

## 边界

- 零外部依赖(仅 BCL),`IsAotCompatible=true`。
- 定义:`ExtensionCallContext`、`ToolDeclaration`、`CommandDeclaration`/`IPluginCommand`、`WorkflowDeclaration`、`AgentDeclaration`、`IPluginPromptContributor`、`PanelDeclaration`、`PluginContributionSet`、`Permission`、`PluginStatusResponse`、`CodeExecutionRequest/Result`、`Command/PromptExecutionRequest/Output`,以及 `IHostFacade`、`IPlugin` 等插件侧入口。
- 定义 `ExtensionProtocol` 的 IPC 操作名与 wire 形状(工具/贡献注册、状态、命令、提示词、插件执行、批准/撤销/刷新、host.tool_call、worker.load/execute/command/prompt/unload/stop)。`run_code` 的一次性执行器协议(`executor.load/run/stop`)在 `forge-script-runtime`,不在这里。
- 不含 Roslyn、Ipc 实现、P/Invoke、Avalonia。

## 关键决策

- wire 统一 camelCase,枚举用整数且严格校验已定义值;泛型反射序列化只留在 JIT 侧,不进 HostClient/主程序。
- 契约版本 2.0.0:旧 `plugin.json`/`IForgePlugin` 包不受支持,新 SDK 使旧审批失效。
