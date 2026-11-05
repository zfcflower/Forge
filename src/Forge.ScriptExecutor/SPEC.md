---
id: forge-script-executor
type: module-design
status: draft
title: Forge.ScriptExecutor
parent: forge-architecture
depends-on: [forge-ipc, forge-plugin-sdk, forge-script-runtime]
---

# Forge.ScriptExecutor

`run_code` 的一次性 JIT 执行器:每次调用一个全新进程,装载**一个**已验证产物、**运行一次**,随后由 Host 杀掉并等待其整棵进程树。它不编译、不缓存、不装载插件,因此既没有可继承的静态状态,也没有“这次能不能复用”的判定问题。

## 责任

- 连接 Host 提供的具名管道(`--pipe <name>`,唯一参数),服务 `executor.load`/`executor.run`/`executor.stop`;其它操作一律 `unsupported_operation`。
- `executor.load`:`ScriptArtifact.Decode`(先按 base64 长度拒收)→ `ScriptArtifact.Validate`(长度/SHA-256/globals MVID/运行时/契约版本必须等于运行中的 `ScriptArtifact.ContractVersion`/入口名)→ `AssemblyLoadContext.LoadFromStream` → 反射取入口(`ForgeScriptEntry.Run`)并绑定为 `ScriptEntryPoint` 委托 → 应答 `{}`。第二次 load 以 `already_loaded` 拒绝。
- `executor.run`:`ScriptHarness.RunAsync` 在 `ScriptRunRequest` 给的工具/选项/剩余预算下运行一次,返回 `CodeExecutionResult`;运行前未 load 以 `not_loaded` 拒绝,第二次 run 以 `already_ran` 拒绝,缺 context/runId 以 `invalid_arguments` 拒绝。
- 内层工具调用经 `host.tool_call` 以 `{runId,name,args}` 回 Host,由 Host 按 active-run 表补身份后转主程序;执行器不做权限检查,也不自报 session/工作目录/深度。

## 边界

- 只依赖 Ipc、Plugin.Sdk、ScriptRuntime;不引用 Scripting/Roslyn。发布闭包里没有 `Microsoft.CodeAnalysis*`,也没有 `Forge.Scripting`(见验证约定)。
- 无 P/Invoke:进程归属(Windows Job Object / POSIX 进程组)由 Host 的 `WorkerProcessOwnership` 负责,执行器自身只被杀。
- 不写临时 DLL/PDB:映像只以内存流装载;`Assembly.Load`、磁盘探测、`Assembly.LoadFrom` 一概不用。
- 不信任自身收到的字节:不校验通过就不装载;描述与字节不符、或来自另一份 globals/运行时构建的产物一律拒收。

## 关键决策

- **一次性是构造性的。** 不是靠“线程/静态状态/句柄污染检测”决定是否复用,而是进程用完即弃:第二次 load/run 是协议错误,不是可选项。
- **上限先于分配。** 编码后的 base64 长度先与 `MaxEncodedAssemblyChars` 比对再解码,避免为不可信长度分配内存;`MaxAssemblyBytes`(8 MiB)、base64 膨胀(4/3)、描述上限与信封之和(`MaxLoadFrameBytes`≈11.25 MB)仍低于 `IpcProtocol.MaxFrameBytes`(16 MiB)。
- **产物完整性 = 字节,缓存身份 = 键。** `PeSha256` 只用于字节校验,`CacheKey`/`SourceHash` 只用于身份,两者不互相顶替;globals 程序集 MVID、运行时 major.minor 与产物契约版本(`ScriptArtifact.ContractVersion`,逐字比较,不是“非空即过”)也参与校验,所以换一份 ScriptRuntime 构建、换运行时大版本或换产物契约都不会“装载成功却语义错位”。
- **管道无默认期限。** 运行自带预算,硬期限与杀进程树归 Host;执行器只负责尽快返回,超时后由 Host 收拾。
- 退出码:0 正常/Host 关闭,1 连接或协议失败,2 命令行错误。

## 验证约定

- 直接对真实执行器进程验证:插件操作(`worker.load/execute/command/prompt`)被 `unsupported_operation` 拒绝、未 load 的 run 被 `not_loaded` 拒绝、第二次 load/run 被 `already_loaded`/`already_ran` 拒绝、缺 context 被 `invalid_arguments` 拒绝。
- 发布闭包以 `dotnet publish src/Forge.ScriptExecutor` 后 grep 目录证明无 `Microsoft.CodeAnalysis*`;源码 grep 证明无 `DllImport`/`LibraryImport` 与 `Assembly.Load`。
- 端到端经真实 Host:成功、放弃、超时、取消、崩溃后目标 pid 必须消失;并发调用互不污染。
