---
id: forge-plugin-sdk
type: module-design
status: active
title: Forge.Plugin.Sdk
parent: forge-architecture
---

# Forge.Plugin.Sdk

插件源码引用的稳定 SDK 与契约,独立版本化(3.0.0),不依赖 `Core`/`Abstractions`。主程序(AOT)、Extension Host、Plugin Worker、ScriptRuntime 与 ScriptExecutor 都引用它;插件可见的共享类型在这里,宿主内部的线上负载在 `Forge.Ipc.Protocol`。

## 边界

- 只依赖 BCL 与 MessagePack(线上类型标注 `[MessagePackObject]`,源生成格式化器),`IsAotCompatible=true`。
- 定义:`ExtensionCallContext`、`ToolDeclaration`、`CommandDeclaration`/`IPluginCommand`、`WorkflowDeclaration`、`AgentDeclaration`、`IPluginPromptContributor`、`PanelDeclaration`、`PluginContributionSet`、`Permission`、`PluginStatusResponse`、`CodeExecutionRequest/Result`、`Command/PromptExecutionRequest/Output`,以及 `IHostFacade`、`IPlugin` 等插件侧入口。
- 定义 `ExtensionProtocol` 的 IPC 操作名及每个操作的参数 → 结果类型(工具/贡献注册、状态、命令、提示词、插件执行、批准/撤销/刷新、Agent 编写扩展的 `extensions.stage/pending/commit/discard` 与 `plugin.disable/enable/delete`、host.tool_call、worker.load/execute/command/prompt/unload/stop)。`run_code` 的一次性执行器协议(`executor.load/run/stop`)在 `forge-script-runtime`,不在这里。
- 不含 Roslyn、Ipc 实现、P/Invoke、Avalonia。

## 关键决策

- wire 是 MessagePack,成员按整数 `Key` 编号:已发布的 Key 不改号、不复用,新增成员只追加新 Key。枚举按整数传输,接收端校验已定义值。没有反射序列化。
- Agent 编写扩展的 DTO 在 `ExtensionAuthoring.cs`;协议常量与 DTO 只追加,不改既有 op 的形状。`UiSchema.DefinitionJson` 以 JSON 文本承载面板定义,由消费端解析。`StagedExtension.Tool`(`ExtensionToolSpec`)承载工作流包声明的工具,供审批卡展示;它同时进入工作流的依赖指纹。
- 契约版本 3.0.0(线格式改为 MessagePack):新 SDK 使旧审批失效;旧 `plugin.json`/`IForgePlugin` 包不受支持。
