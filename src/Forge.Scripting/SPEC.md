---
id: forge-scripting
type: module-design
status: draft
title: Forge.Scripting
parent: forge-architecture
depends-on: [forge-script-runtime]
---

# Forge.Scripting

Extension Host 的动态 C# 插件编译/缓存/加载层,以及 `run_code` 的编译服务:Roslyn 编译、源码哈希缓存、版本化产物、可卸载 AssemblyLoadContext、脚本白名单与产物发射。JIT 侧项目(不 AOT),只依赖 BCL + Roslyn + `Forge.Plugin.Sdk` + `Forge.ScriptRuntime`;脚本的**执行面与运行回路在 `Forge.ScriptRuntime`**,一次性执行器不引用本模块。

## 边界

- 插件编译:插件是普通类库(`IPlugin` 入口),不用 CSharpScript;不提供插件发现、注册、manifest 读取(属 ExtensionHost)。
- 脚本编译服务:公开入口 `ScriptCodeCompiler.CompileAsync(code, options, ct) → ScriptCompilation`(可装载 PE 映像 + `ScriptArtifactDescriptor`),Host 持有**唯一**实例,即唯一缓存所有者。脚本可见表面(`ScriptGlobals`/`ToolsFacade`/`ToolCallException`)在 `Forge.ScriptRuntime`;内层工具调用只经传入的 `IHostFacade` 离开进程,本模块不做工具解析、审批、会话/调用链绑定或结果截断策略(均由主程序执行)。
- 进程内运行入口:公开 `ScriptCodeRunner : ICodeRunner`(`ExecuteAsync(CodeExecutionRequest, IHostFacade, CancellationToken)`)保留,供测试与同进程用法把“编译 → 内存装载 → `ScriptHarness` 运行”走完整;一次性执行器**不**引用本模块,它只装载产物。
- 不做语法白名单/安全分析(插件):权限由 `forge-approval` 审批与 Worker 进程隔离承担,ALC 只负责加载与卸载。脚本侧相反:保留白名单 + 语义检查。

## 关键决策

**两级哈希。** `SourceHash` 始终由编译器从归一化源码(LF、去 BOM、去单尾换行)计算,供审批;调用方无法设置或替换它。需要携带已知/已批准哈希时用独立字段 `ExpectedSourceHash`:它只做校验,与计算值不一致则该次编译以 `FORGE0004` 失败,而结果里的 `SourceHash` 仍是计算值。`CacheKey` = 归一化源码摘要 + `ExpectedSourceHash`(有无与取值)+ 程序集名 + 契约版本 + 编译设置(含 `Usings` 的 null 与空列表之别)+ 引用集(含无法解析的引用),列表按条数与长度分帧,避免 `[System, P]` 与 `[System]` + 引用 `P` 撞键;期望哈希计入 key,使校验失败的结果不会顶替合法请求的缓存条目。确定性编译(同请求 → 同字节镜像)。

**版本化产物。** `<base>/<pluginId>/<version>/`,原子写,已存在版本绝不覆盖;防路径穿越。

**可卸载 ALC。** 共享集只有 TPA、`Forge.Plugin.Sdk` 与显式配置的共享程序集;宿主任意已加载(非 TPA)程序集不因“已加载”而被共享,因此插件自带的同名依赖仍绑定插件目录里的副本。路径模式下插件依赖经 `AssemblyDependencyResolver` + 平铺目录探测;`BeginUnload` 与 `WaitForCollectionAsync` 分离,弱引用确认回收,并且只在已卸载上下文的依赖文件句柄确实释放后才返回 `true`(超时未确认返回 `false`)。

**已验证镜像模式。** `PluginLoadContext.FromVerifiedImages` 接受“程序集简单名 → 已验证字节”的集合:该模式下所有托管依赖只从内存 `LoadFromStream`,不探测插件目录,也不读 `.deps.json`(既不建 resolver 也不回退路径),因此依赖在任意时刻(包括工具调用中途)首次被解析都只可能是被校验过的那份字节。非共享且未提供的依赖以 `FileNotFoundException` 指名失败,不回退到磁盘。**本地库在该模式下根本不装载**:`LoadUnmanagedDll` 不返回零(返回零等于交回默认探测,会去插件目录或操作系统里找),而是抛 `DllNotFoundException` 指名该库并说明已验证插件不支持本地库——因为插件程序集里的 `DllImport` 先经它自己的 ALC 解析,异常即到此为止,默认探测不可达(有测试用操作系统本来就能解析的绝对路径库固定住“不回退”);插件目录里同名文件在不在、内容是什么、扩展名是什么都不影响结论(有测试固定住 present/replaced/deleted 三种情况抛出同一异常)。集合里的镜像必须是托管程序集,判定用 `ManagedImage`(`PEReader`:`HasMetadata` 且有 assembly manifest),非托管 `.dll` 在构造上下文时即被拒绝并指名;空名、重名或 `null` 字节同属调用方错误。字节与上下文同生命周期:卸载时集合引用被清空,上下文被回收后不残留任何镜像数组。默认的按路径解析仍然保留给既有调用方(该模式仍把不可解析的名字返回零交给默认探测)。

**本地二进制识别。** `ManagedImage.ReadNativeBinaryFormat` 只看文件头而与扩展名无关,返回 ELF(`\x7fELF`)、Mach-O(32/64 位与字节序反转的四种 magic)、Mach-O universal/fat(fat 与 fat64)或无 CLI 头的 PE,数据文件返回 `null`。Worker 在装载前用它拒收本地二进制,扩展名不构成放行依据。

**脚本表面 = 白名单 + 语义检查。** `ScriptSurface` 固定引用集(BCL 子集 + `Forge.ScriptRuntime` + `Forge.Plugin.Sdk` + `Forge.Scripting`)与导入(`Forge.Scripting`/`Forge.Plugin.Sdk`,供脚本点名 `ToolCallException`/`HostToolResult` 等),`AllowUnsafe=false`、无元数据/源解析器(普通 `CSharpCompilation`,不是 CSharpScript)、文件路径固定 `script.csx`;`#r`/`#load`/`unsafe`/`typeof`/指针在编译前由语法扫描拒绝,禁用命名空间与受限类型(`Environment`、`AppContext`、`Thread`、`TaskFactory` 等)由语义遍历拒绝。本程序集仍被引用(使其余类型可被点名而不是“查无此类型”,从而能被语义遍历拒绝),`Forge.Scripting` 命名空间只允许 `ScriptGlobals`/`ToolsFacade`/`ToolCallException`:其余公共类型(`VersionedOutputStore` 写文件、`PluginLoadContext` 装载程序集、`ManagedImage` 读文件、`ScriptCompiler` 任意编译、`ScriptCodeRunner` 自身、`ScriptCodeCompiler` 自身)在脚本里按禁用 API 拒绝。诊断与违规都带 `line N, column M`,条数与字符数有界;启用调试信息使运行时栈出现 `script.csx:line N`。这不是进程隔离:内存不限,CPU 空转靠终止 Worker 收拾。

**按请求重建能力。** 缓存只驻留编译产物与固定编译表面:`ScriptCodeCompiler` 不缓存工具元数据、facade、审批结果或调用上下文。缓存键 = 编译表面指纹(版本 + 导入 + 引用路径 + 三个自有程序集 MVID)+ 源码 + 全部 `CodeExecutionOptions`,因此换选项不会命中上一次的错误文本;失败编译同样入 LRU,重复的错误脚本不再进 Roslyn。工具元数据不进缓存键(脚本按名调用,元数据不进编译),每次运行按本次 `Tools` 重建 `ToolsFacade`,上一请求的工具名在下一请求里即未知。装载出的入口委托按编译产物加锁创建一次并复用;每次运行的 globals/log/facade/令牌各自独立。

**单飞与取消隔离。** 同一缓存键的并发 `CompileAsync` 共享同一次编译(脚本与插件编译器都如此);每个调用者的取消只让它自己立刻抛 `OperationCanceledException`,共享编译只在最后一个等待者取消后才取消,取消的编译不写入缓存,下一次调用重新编译。

**脚本编译产出可装载产物。** 用 `CSharpCompilation` + `Emit(Embedded PDB, deterministic)` 把脚本发成一个 PE 映像,而不是 `CSharpScript` 提交/委托。`ScriptSourceBuilder` 生成唯一入口类型 `ForgeScriptEntry : ScriptGlobals`(私有构造从传入的 globals 初始化 `Tools`/`Log`/`Cancellation`)、`static Task<object?> Run(ScriptGlobals, CancellationToken)` 与 `async Task<object?> RunScriptAsync()`;脚本正文原样落在后者的方法体里,前面加 `#line <脚本行> "script.csx"`,因此诊断行列与栈帧 `script.csx:line N` 都与脚本自己的坐标一致。脚本自带的 `using` 指令被提升到文件头,正文用补偿的 `#line` 保持原有行号。语义等价:顶层语句、顶层 `await`、`return`、末尾表达式(改写为 `return (object?)…`)、返回 `Task` 由运行回路解包。末尾表达式的判定**只看语义模型给出的类型**,不看语法节点名:第一次编译把末尾语句表达式当作语句编出语义模型,`GetTypeInfo(...).Type` 非 `void` 即改写成 `return (object?)(…)` 再编一次,只有真正的 `void`(例如末尾一句 `Log(...)`、`await` 一个非泛型 `Task`)或 **ref-like 类型**(`IsRefLikeType`,例如返回 `Span<T>` 的末尾调用;ref struct 无法装箱进 `object?`,改写会直接 CS0029)才保持语句并让脚本结果为 `null`——旧 CSharpScript 引擎对这两类同样返回 `null`,而它同样返回末尾表达式的值:因此 `await Tools.Call("x");`、`Tools.CallJson("…");`、`new List<int>{1,2};`、`x = 5;` 作为最后一行都返回其值,而不是 `null`。已知差异:脚本里**声明类型**(class/struct)不再允许——旧引擎允许,新形态是方法体。另一处已知差异:末尾非 void 语句表达式被改写为 `return (object?)(…)` 后,该行的诊断**列**会偏移 17 个字符(`return (object?)(` 前缀长度),行号不受 `#line` 影响而保持正确。描述(`AssemblyName`/`SourceHash`/`CacheKey`/`ContractVersion`/`RuntimeVersion`/`EntryPointType`/`EntryPointMethod`/`GlobalsMvid`/`PeSha256`/`PeLength`)随映像一起产出,`SourceHash` 与 `CacheKey` 仍是两个概念(字节完整性 vs 缓存身份)。

**取消与终止。** 运行回路在 `Forge.ScriptRuntime`(`ScriptHarness`):脚本在 `Task.Run` 上执行,预算由 `WaitAsync(runToken)` 收口,同步死循环不会占住 IPC 线程。超时或外部取消先给 1 秒宽限让脚本自行收尾:停下来就是普通错误结果;仍未停下则 `RequiresTermination=true`,文本明确写“未停止、已被放弃、执行器进程必须终止”,不把等待的取消说成已停止。放弃的执行保留线程与令牌源(避免其后续回调撞上已释放对象),由 Host 杀进程回收。编译前取消直接抛 `OperationCanceledException`:不编译脚本、不启动脚本、不触发任何 facade 回调。脚本返回 `Task` 时解包同样受预算约束。

**内层调用。** `Tools.Call`/`Call<T>`/`CallJson`/`CallJson<T>`/`Json<T>` 只把 JSON 参数交给 `IHostFacade.CallToolAsync`;按 `request.Tools` 拒绝未知工具并列出可用名(排除 `run_code`),`run_code` 自身永久拒绝(`Denied=true`,不递归)。主程序的拒绝以 `IsError` 结果文本返回并转成可捕获的 `ToolCallException`;host 调用失败(非取消)同样包装。每次运行一个 `SemaphoreSlim` 限并发,内层结果按 `MaxInnerResultChars` 截断。泛型反射解码只在此 JIT 进程内,不进 HostClient。

**脚本自述面(W14)。** `Tools.List()` 返回本次运行可调用的名字(序数排序);`Tools.Describe(name)` 返回该名字的副作用、完整描述与参数 schema,未知名字抛 `ToolCallException`。两者都只读本次运行的 `ToolDeclaration` 元数据:不发 IPC、不解析工具、不改变可调用集合。**这也是模型提示里不再逐工具列描述与 schema 的前提**——脚本要细节时自己问一句,而每回合的提示只付一份 `run_code` 定义。

**运行期校验。** 每个 `CodeExecutionOptions`、超时(>0)、`request.Tools`(列表不能为 null,允许空列表供纯计算脚本使用;名字非空、副作用枚举已定义)在入口校验,非法即抛(宿主/调用方配置错误);空脚本与超长脚本以错误结果返回而不抛。模型可见的 run_code 描述由主程序代理维护,见 `forge-host-client`;脚本引擎不维护另一份对外描述。

## 验证约定

并发以门闩验证实际同时进入与上限,运行期取消必须等脚本进入工具门面后再触发;编译可能很慢,不能用短定时器推断脚本已经启动。无公开的缓存命中观测点,测试以同源码更换工具集/选项后的可见行为验证能力隔离,不以耗时猜测缓存命中。`CompileAsync` 的单飞/取消并发测试用测试注入的编译委托与门闩停在编译中,不用短定时器。这些测试与被注入的编译委托一样只在 `DEBUG` 编译(`src/Forge.Scripting/AssemblyInfo.cs` 的 `InternalsVisibleTo` 同样只在 `DEBUG`,与 `Forge.Approval`/`Forge.HostClient` 一致),`Release` 构建既不向测试暴露 internal,也不编入依赖它的用例。失控脚本只在可整体回收的测试子进程执行;模块初始化器中的子进程入口须先在当前线程预热门面调用路径,避免首次类型初始化与执行脚本的工作线程相互等待。

## 未决

- 根任务完成不证明脚本全部工作结束:未等待的异步任务可在成功结果之后继续执行。主调用预算与 RequiresTermination 只覆盖被等待的任务,因此进程**不复用**是唯一正确的处置——由 `forge-extension-host` 的一次性执行器负责杀树,而不是由本模块证明“没有残留”。

- 失败的编译保留在缓存;依赖变化时的失效由 ExtensionHost 负责(`Evict`/`Clear`)。
- 脚本白名单与禁用名单的条目未经实战校准(尤其 `System.Type` 允许成员、`Regex` 回溯型 CPU 炸弹)。
- 内存与 CPU 无法在进程内限制;失控脚本只能整进程终止,1 秒宽限是常量而非按调用配置。
- 描述内联全部工具的 JSON schema,工具很多时描述会显著变长,尚无裁剪或分层策略。
