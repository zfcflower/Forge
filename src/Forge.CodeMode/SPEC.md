---
id: forge-codemode
type: module-design
status: stale
title: Forge.CodeMode
parent: forge-architecture
depends-on: [forge-architecture]
---

# Forge.CodeMode

Roslyn 脚本宿主:模型给出一段 C# 脚本,脚本通过门面并行调用已注册工具,只把最终结果返回一次。只依赖 `Forge.Abstractions` 与 Roslyn 脚本包,不引用 `Core`、`Mcp`、`Plugins`。

## 边界

- 对外入口:`CodeModeTool`(`run_code`)、`CodeModeRegistration.Create`、`CodeModeOptions`。脚本可见表面只有 `ScriptGlobals`(`Tools`、`Log`、`Cancellation`)与 `ToolsFacade`,其余类型对脚本不可见。
- 内部工具经 `IToolRegistry` 按名解析,以调用方 `ToolContext` 与取消令牌执行(工作目录因此限定在会话工作区内)。`run_code` 自身被排除,脚本不能递归。
- 审批:`IApprovalPolicy` 求值,`Ask` 走 `IApprovalHandler`(交互式询问经 `ApprovalGate` 串行化);没有 handler 时按拒绝处理,以 `ToolCallException` 抛回脚本,脚本可捕获。只读是否自动放行由策略决定,CodeMode 不另设开关。
- `run_code` 定义级副作用为 `Execute`(脚本可触达写工具),因此每个内部调用仍独立过审批。

## 关键决策

**沙箱 = 白名单 + 语义检查。** `ScriptOptions.WithReferences/WithImports` 整体替换默认集合,只留 BCL 小集合、`Forge.CodeMode`/`Forge.Abstractions` 与 `CodeModeOptions` 追加项;`#r`/`#load` 解析器置空,`AllowUnsafe` 关闭。核心库本身带有 `System.IO.File`、`System.Type`、线程类型,因此再用语义模型遍历拒绝:禁用命名空间(`System.IO`/`Net`/`Diagnostics`/`Reflection`/`Runtime.InteropServices`/`Runtime.Loader`/`Runtime.CompilerServices`/`Xml`/`Microsoft` 等)、禁用类型(`Environment`、`AppContext`、`AppDomain`、`Activator`、`GC`、`Console`、`Thread`/`ThreadPool`/`Timer`、`TaskFactory`、句柄类型)、受限类型只留成员白名单(`System.Type`/`MemberInfo` 仅 Name/FullName/Namespace/ToString),外加 `unsafe`、`typeof`、指针。连带报告按"最具体 API"合并去重。这不是进程隔离。

**编译缓存。** 按源码 SHA-256 缓存 `CompiledScript`(脚本与错误文本一起缓存,命中即跳过 Roslyn 与语义检查),LRU 上限 `CompilationCacheCapacity`。编译在 `Task.Run` 上执行;`ScriptRunner` 由缓存的 `Script` 惰性生成并复用,可并发调用。`WarmUpAsync` 预先编译并跑一个极小脚本,避免首个真实调用付 Roslyn 启动成本。

**超时不依赖脚本配合。** 运行以 `WaitAsync(runToken)` 收口:脚本即便 await 不可取消的操作,预算耗尽也会立刻把结果还给模型;被放弃的执行继续占线程直到自行结束。`Cancellation` 令牌暴露给脚本供长循环自查;内部工具调用也用它,所以超时后新的内部调用立即失败。描述里如实写明"非进程隔离、内存不限、CPU 空转只丢线程"。

**结果形状。** 脚本最后表达式(或 top-level `return`)是结果;若它是 `Task` 则解包。字符串原样返回,`JsonElement`/`JsonNode` 取原始 JSON,其余按 System.Text.Json 序列化。`Log(...)` 有界捕获后以 `[log]` 段落附在结果之后。内部工具结果超限截断并附注,失败以 `ToolCallException`(带工具名与 `Denied` 标记)抛给脚本。

**并发。** 每次运行一个 `SemaphoreSlim`,默认 8 个在飞内部调用;脚本用 `Task.WhenAll` 才并行,逐个 await 自然串行。

**描述稳定性。** 描述由注册表生成:表面说明 + 按名称 ordinal 排序的工具列表(副作用、一行描述、压缩后的 JSON schema)。注册表内容变化时按指纹重建,内容不变时字节相同,可命中提示词缓存。

**错误文本。** 编译错误与禁用 API 都以 `line N, column M: 说明` 返回,数量与总长度有界;开启调试信息使运行时栈带 `script.csx:line N`,模型能自行定位重试。

## 未决

- 白名单与禁用名单的条目未经实战校准(尤其 `System.Type` 的允许成员、`Regex` 回溯型 CPU 炸弹)。
- 内存与 CPU 无法限制;超时后被放弃的执行会残留线程,尚无回收手段。
- 描述内联全部工具的 JSON schema,工具很多时描述会显著变长,尚无裁剪或分层策略。
- 脚本可自定义类型,`Call<T>` 依赖 System.Text.Json 的反射序列化;若将来走 AOT 需另做源生成。
- `MaxConcurrentToolCalls` 是每次运行的上限,不是全局上限;多个会话并行时总并发数为运行数乘以该值。
