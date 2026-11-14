---
id: forge-script-runtime
type: module-design
status: draft
title: Forge.ScriptRuntime
parent: forge-architecture
depends-on: [forge-plugin-sdk]
---

# Forge.ScriptRuntime

`run_code` 的脚本执行面、运行回路与产物校验。只依赖 SDK、BCL 与 MessagePack(线格式):不含 Roslyn、不含 Ipc、不含 P/Invoke,因此可被 Host(JIT)、编译服务与一次性执行器共同引用,而执行器部署闭包里不会带进编译器。

## 责任

- **脚本可见表面。** `ScriptGlobals`(`Tools`/`Log`/`Cancellation`)、`ToolsFacade`(`Call`/`Call<T>`/`CallJson`/`CallJson<T>`/`Json`/`Json<T>`,以及 W14 的 `List()`/`Describe(name)`)、`ToolCallException`。类型仍在 `Forge.Scripting` 命名空间,脚本源码因此与旧引擎完全兼容;`ScriptGlobals` 不再 `sealed`,编译出的脚本类型从它派生,`Log`/`Tools`/`Cancellation` 就是脚本自己的成员(与旧 Roslyn 提交类同一形态)。`List`/`Describe` 只读本次运行的 `ToolDeclaration`(不发 IPC),它们是主程序提示里不再逐工具列 schema 的另一半。
- **运行回路。** `ScriptHarness.RunAsync(entry, host, settings, ct)`:每次运行新建 facade/log/令牌,在 `Task.Run` 上启动入口委托,`WaitAsync(runToken)` 收口预算,1 s 宽限后仍未停下即判为“被放弃”并返回 `RequiresTermination=true`;脚本返回 `Task` 时在同一预算下等待并解包其 `Result`;结果按 `MaxResultChars` 截断、日志按 `MaxLogChars` 有界后附在结果之后。
- **产物校验。** `ScriptArtifact`:格式版本、`MaxAssemblyBytes`(8 MiB)、`MaxLoadFrameBytes`(映像 + 描述 + 帧开销上界,用于证明仍低于 Ipc 16 MiB 帧)、`EnsureImageWithinBounds`、`ValidateDescriptor`/`Validate`(长度、SHA-256、globals 程序集 MVID、运行时 major.minor、契约版本(必须与运行中的 `ScriptArtifact.ContractVersion` 逐字相等)、入口名形状任一不符即抛 `ScriptArtifactException`,不装载)、`GlobalsMvid`/`RuntimeVersion`/`Sha256`。
- **执行器协议形状。** `ScriptExecutorProtocol`(`executor.load`/`executor.run`/`executor.stop`)、`ScriptArtifactLoadRequest`(原始 PE 字节 + 描述)、`ScriptRunRequest`,均为 MessagePack 类型;内层工具调用复用 SDK 的 `ExtensionProtocol.HostToolCall` → `HostToolResult`(宿主侧再映射到自己的 `HostToolCallRequest` 线类型,那属 `Forge.Ipc.Protocol`,不是本模块依赖)。

## 边界

- 不编译、不装载程序集、不做反射 Emit 或动态代码生成(结果解包仅用单处 `GetProperty("Result")` 读 `Task<T>.Result`):入口委托由调用方(编译器在同一进程内装载,或执行器用 `AssemblyLoadContext.LoadFromStream` 装载)交给本模块。
- 不解析工具、不评估审批、不截断主程序结果的内容策略:每次调用只经传入的 `IHostFacade` 离开进程,拒绝(`IsError`)以可捕获的 `ToolCallException` 呈现;`run_code` 自身永久拒绝(`Denied=true`)。
- 不是沙箱:内存与 CPU 不能在进程内限制,空转靠上层杀进程收尾;`RequiresTermination` 是“必须终止承载进程”的通知,不是能力边界。
- 预算由调用方给,本模块只消费:`TimeoutMilliseconds` 是本次运行可用的绝对毫秒数,不含排队与启动。

## 关键决策

- **放弃而非停止。** 预算结束只结束**等待**,不结束脚本线程:宽限窗口内自行收尾的按普通错误结果返回(文案说“已停止”),窗口外仍在运行的按放弃返回并置 `RequiresTermination`,文案明确“未停止、已被放弃、执行器进程必须终止”。放弃的运行有意保留线程与令牌源,避免其后回调撞上已释放对象。
- **每次运行重建能力。** facade、log、globals、令牌逐次新建,工具名集合只来自本次 `ScriptRunSettings.Tools`;因此同一进程里的两次运行不会互相继承工具、日志或调用能力(一次性执行器进程本身也不复用)。
- **探针式结果格式化。** `ScriptOutput` 处理 JSON 元素/节点、字符串原样、布尔小写、`char`、最后退回 `JsonSerializer`;异常描述过滤框架帧,优先显示 `script.csx` 行号的脚本帧(最多 4 帧),没有脚本帧时才显示 2 帧。

## 验证约定

`Forge.Scripting.Tests` 通过 `ScriptCodeRunner` 覆盖值、日志、JSON、内层工具、错误、诊断、截断与运行期校验;失控循环只在可整体回收的子进程里跑,断言 `RequiresTermination`、`abandoned`、`must be terminated` 与预算秒数,不断言宿主能在进程内停下一个不合作的循环。`Forge.Integration.Tests` 经真实 Host 与一次性执行器覆盖同一语义。
