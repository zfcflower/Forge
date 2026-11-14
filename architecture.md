---
id: forge-architecture
type: architecture-design
status: active
title: Forge 架构
parent: forge-goal
depends-on: [forge-goal]
---

# Forge 架构

系统拓扑、分层边界与跨模块决策。产品目标见 `forge-goal`。标注"(未确认)"的为推断。

## 分层与依赖方向

解决方案按层拆为多个项目,依赖只能自上而下,下层不得引用上层。

| 项目 | 职责 |
|---|---|
| `Forge.Abstractions` | 主程序内部共享契约(工具、审批、存储、提示词、Agent 事件);零外部依赖 |
| `Forge.IO` | 文件与进程原语 + Agent 的文件/搜索/shell 工具:`AtomicFile`、工作区路径守卫、有界文件读写、目录遍历、`.gitignore` 匹配、grep 引擎、`ProcessRunner`、7 个 `ITool`(`read`/`write`/`edit`/`ls`/`find`/`grep`/`bash`);只依赖 BCL 与 `Abstractions` |
| `Forge.Ipc` | 命名管道 + 4 字节长度前缀 MessagePack 帧的传输、帧编解码、请求关联、协议版本;主程序、宿主、Worker 共用;AOT 兼容 |
| `Forge.Ipc.Protocol` | 宿主内部各操作的类型化负载(插件执行、Worker、`host.tool_call`、终端);依赖 `Ipc`、`Plugin.Sdk`,不属插件 SDK;AOT 兼容 |
| `Forge.Plugin.Sdk` | 供插件源码引用的稳定 SDK 与契约,独立版本化,不依赖 Core |
| `Forge.Approval` | 用户插件的源码哈希审批引擎:纯逻辑求值 + SQLite 持久化;AOT 兼容 |
| `Forge.Core` | Agent 循环、会话、模型提供商(OpenAI 兼容、Anthropic、Responses)、SubAgent 调度与提示词缓存、提示词三层合成、网络栈、自动化引擎 |
| `Forge.Workspace` | Git 与 Worktree、规格图(spec-graph)读写、评审意见存储 |
| `Forge.Mcp` | MCP 客户端与服务器管理 |
| `Forge.Editor` | 文本缓冲、LSP 客户端(含语义 token)、TextMate 着色、补全、Diff 模型;不含 UI |
| `Forge.Workflows` | 声明式工作流引擎(L0):JSON 步骤、`after` 依赖、并行、JSON 路径映射;工具调用经 `IHostFacade`,不持注册表、不审批、不落盘 |
| `Forge.Scripting`(JIT) | Roslyn 编译/源码哈希缓存/版本化产物/可卸载 AssemblyLoadContext,以及 `run_code` 常驻编译服务;脚本执行面在 `ScriptRuntime` |
| `Forge.ScriptRuntime` | `run_code` 的脚本执行面、运行回路与产物校验;只依赖 SDK 与 BCL,不含 Roslyn,供 Host、编译服务与一次性执行器共同引用 |
| `Forge.ScriptExecutor`(JIT) | `run_code` 的一次性执行器:每次调用一个全新进程,装载一个已验证产物、运行一次,随后被 Host 杀掉并等待整棵进程树;不编译、不缓存 |
| `Forge.ExtensionHost`(JIT) | 常驻进程:插件发现与注册、生命周期、审批把关、编译服务(经 `Scripting`)、声明式工作流执行、Worker 与执行器管理、进程归属、交互终端 PTY |
| `Forge.PluginWorker`(JIT) | 独立进程:加载并执行一个动态 C# 插件;可被整体终止(run_code 脚本不在这里执行,走 `ScriptExecutor`) |
| `Forge.HostClient`(AOT) | 主程序侧的 `ExtensionHostClient`:启动/连接 Host、`ITool` 代理、`host.tool_call` 回传(审批在主程序)、Host 崩溃保留工具快照;只依赖 Abstractions、Ipc、Ipc.Protocol、Plugin.Sdk |
| `Forge.App` | Avalonia 桌面 UI 与组合根(含 `ExtensionHostClient`);唯一知道所有主程序项目的层 |

主程序侧(Native AOT)依赖闭包:`Abstractions`、`IO`、`Ipc`、`Ipc.Protocol`、`Plugin.Sdk`、`Approval` ← `Core`、`Workspace`、`Mcp`、`Editor`、`HostClient` ← `App`。JIT 侧:`Plugin.Sdk`、`Ipc`、`Ipc.Protocol`、`ScriptRuntime` ← `Scripting` ← `ExtensionHost`(另引用 `Workflows`、`Approval`);`PluginWorker` 引用 `Scripting`;`ScriptExecutor` 引用 `ScriptRuntime`(不引用 `Scripting`,部署闭包不含 Roslyn)。主程序不引用 Roslyn;`ExtensionHost` 不引用 `Core`。

## 关键决策

**性能第一。** 这是与 `forge-goal` 一致的最高约束,冲突时它优先:
- UI 线程只做渲染与输入;LSP、着色、Git、文件扫描一律后台线程,结果经不可变快照回到 UI。
- 列表(会话、文件树、消息、Diff)使用 Avalonia 虚拟化;Diff 与编辑器增量渲染,不整体重绘。
- Agent 流式输出按渲染帧批量合并,每帧最多更新一次 UI(已确认)。

**会话与工作区同一时刻一对一。** 每个会话绑定一个 Git Worktree,会话的工具调用、文件读写和终端的工作目录均限定在其中;一个工作区同一时刻至多一个活动根会话(存储层唯一索引强制),旧会话可被接手、归档后进历史。并行工作用分叉工作区,而非多个会话共用一个目录。

**文件与命令能力留在进程内,不走 shell。** Agent 读写文件、列目录、找文件、搜内容全部由 `Forge.IO` 的原生实现完成,不启动外部进程;只有真正需要外部程序时才创建子进程(搜索可由可选的 ripgrep 加速,但它只做加速,不是前提)。工具面固定为 `read`/`write`/`edit`/`ls`/`find`/`grep`/`bash` 七个,名字与内置 Agent 的白名单一致;`bash` 先用 Direct(参数向量、不经 shell),只有出现 shell 语法时才走平台解释器。所有工具都有按字节的输出上限、超时、取消与工作区路径校验,超限只截断结果而不会停止读取子进程管道。(已确认)

**Native AOT 主程序(已确认要求;可行性已由 Stage 0 实测确认)。** 主程序不可变、Native AOT 发布;不在其中使用 Roslyn 动态编译或 `AssemblyLoadContext` 加载插件。主程序侧全部项目(`Abstractions`/`IO`/`Ipc`/`Ipc.Protocol`/`Plugin.Sdk`/`Approval`/`Core`/`Workspace`/`Mcp`/`Editor`/`HostClient`/`App`)已开启 `IsAotCompatible` 且 AOT 分析器 0 警告;Workspace 的 JSON 输出改走反射无关的 `JsonPayload`,持久化类型走 `WorkspaceJsonContext` 源生成。体积与延迟均按实测记录,不给估算:`25.3 MiB` 是 Stage 0 可行性探针(**只含一个最小 Avalonia 窗口**)的数,不是产品基线。2026-11 在 .NET 11 / Avalonia 12.1.6 上重测(win-x64,同一台机器):最小 Avalonia 窗口 16.5 MiB,加 AvaloniaEdit+TextMate 编辑器栈 28.7 MiB,完整 `Forge.App` 49.7 MiB——即约 +12 MiB 编辑器栈、+21 MiB 产品自身代码与其余模块,其中嵌入资源(TextMate 语法、Avalonia 资源、图标)8.6 MiB。构建开关实测:`OptimizationPreference=Size` 降到 42.8 MiB,再关托管堆栈追踪 40.8 MiB;两者都不建议默认开(前者牺牲速度,后者丢掉插件与 Agent 链路最需要的托管堆栈)。headless 对话 81-84ms、GUI 到窗口句柄 166ms 同样来自 Stage 0 探针,产品化后未重测。风险:Avalonia 与 Microsoft.Data.Sqlite/SQLitePCLRaw 未声明 AOT 兼容,靠实测背书,CI 需三平台冒烟;原生库不能静态链接,"单文件"不成立;Linux/macOS 发布未在本机验证(需 CI)。只有核心依赖确实无法解决时才重新评估放弃 AOT。

**扩展点统一为契约,执行在进程外。** 工具、命令、提示词、Agent、工作流经 `Forge.Plugin.Sdk` 注册,由 `ExtensionHost` 通过 IPC 向主程序报告;主程序侧以代理 `ITool` 暴露。模型提供商内置,仅在设置中配置,不开放插件注册。

**插件 UI 为声明式。** 插件不能返回进程内控件,改由 IPC 返回 UI Schema(第一阶段 Form、Table、Markdown、Text、Button、Status),由主程序渲染 Avalonia 控件。原 `PanelDefinition(Func<object>)` 契约需迁移。

**三种扩展形态,不混为一套。** Markdown 技能描述 Agent 行为与知识;声明式 Workflow(JSON,第一阶段只含顺序、并行、JSON 路径映射,优先复用 `run_code`)组合已有工具;C# 插件实现新逻辑。保存下来的 run_code 脚本也可作为持久化扩展。

**Extension Host 与 Worker。** `ExtensionHost` 是常驻的普通 .NET JIT 进程,要求用户已安装对应 .NET 运行时(已确认;启动时检测并给出明确错误)。新创建和已批准的 C# 插件默认都在独立 `PluginWorker` 进程执行;共享 Host 仅为高信任低风险插件的可选性能优化。批准只代表允许执行,不代表可进入共享进程。`AssemblyLoadContext` 只负责加载卸载,不提供隔离。取消令牌无法停止不合作的代码,必要时终止整个 Worker(含进程树)。

**审批与哈希。** 新插件默认未批准,展示源码差异与权限需求,用户批准后才可启用。每个插件记录 SourceHash、ApprovedHash、DependencyHash、SDKVersion 等;重启只恢复哈希匹配的已批准版本;源码或依赖变化使批准失效;不能通过改 manifest 或缓存状态绕过。插件调用主程序能力一律经 IPC,由主程序的权限引擎判断、审批与审计。

**IPC。** 主程序↔Host、Host↔Worker 都用 `NamedPipeServerStream`/`ClientStream`(Linux/macOS 上由 Unix 域套接字实现)。帧体 = `IpcHeader`(`Version`、`Id`(0=通知)、`Operation`、`IsResponse`、`Error`)+ 至多一个 MessagePack 负载;协议版本 2,有最大帧长,支持并发、取消、超时与断连处理;版本不匹配拒绝该帧并提示、连接保持可用。`PluginId` 属上层消息语义、不在通用帧头;批量调用(batch)尚无契约面。帧格式与请求生命周期细节见 `forge-ipc`。交互终端的字节流(`terminal.*`)也走主管道:输出按每终端 1 MiB 确认窗口流控,窗口满时 Host 停读 PTY,不丢输出(协议见 `forge-extension-host`)。

**可靠性。** Worker 支持超时、进程树清理、并发上限、崩溃自动恢复(在途请求明确失败、重启后重新注册工具);Windows 可设 Job Object 内存与进程数上限,其他平台不承诺。Host 崩溃时主程序检测退出、失败在途请求、重启 Host、恢复已批准插件并重新注册工具。进程隔离不是安全沙箱:同一操作系统账户下插件仍可访问该账户可访问的文件与网络;OS 级沙箱不在早期阶段承诺。

**提示词三层。** 系统提示词 → 全局 Agent → 项目 Agent,逐层叠加,机制与 PI harness 一致。系统提示词本身是一个可覆盖的内置槽位;命令、Agent 与其它内置提示词统一为"内置 → `~/.forge` → 项目 `.forge`"三层的提示词模板(替换或追加),细节见 `src/Forge.App/SPEC.md` 的"提示词模板"。

**SubAgent 与缓存。** 相同目的的 SubAgent 共享稳定的提示词前缀以命中提示词缓存;动态内容(时间、工作区状态等)作为请求末尾的一条临时 user 消息,不进历史与存储。内置 Scout、Worker、Plan、Reviewer,每种可单独设置模型与思考强度;同类型实例可按 id 续跑。嵌套深度上限 3,同时活跃的 Agent 数按整棵树计数(默认 4),父 Agent 等待子 Agent 结果时交还名额;均由 `forge-core` 强制。

**codemode。** 模型输出一段 C# 脚本,脚本可并行(`Task.WhenAll`)调用已注册工具,只把最终结果一次返回模型。已确认的执行架构是**常驻编译服务缓存编译产物 + 一次性 JIT 执行器**:编译服务不执行脚本,每次调用取得从未执行过脚本的执行器,成功、失败、取消或超时都终止并等待其归属进程树退出后才完成调用。不得复用已执行过脚本的进程,也不以静态状态/线程/句柄污染检测决定是否复用。主程序仍保持 Native AOT,只持有 `run_code` 代理工具;工具调用经 IPC 回到主程序审批、限流和截断。产物只经内存管道交付并从已验证字节装载;执行器部署依赖闭包不得含 Roslyn。可选的预启动执行器池只能提供 Take,不能 Return 已用执行器;是否采用须由分段实测决定,不是当前实现前提。一次性进程提供状态与生命周期隔离,不提供文件/网络权限沙箱。正式路径为“Host 常驻编译服务 + 每次调用一个一次性执行器”:执行过的进程不复用,调用返回前先杀并等待整棵进程树,因此不再有跨调用残留;分段测量(暖调用约 134–137 ms)与实现自测数据见 `forge-extension-host`。

**原生进程归属边界。** P/Invoke 只允许位于 JIT Host 的一个内部类,负责平台分支、原生句柄与进程树归属;Native AOT 主程序、执行器和插件 Worker 不含 P/Invoke。这一点已经满足:唯一的原生类是 `Forge.ExtensionHost.WorkerProcessOwnership`,Worker 原先自管的 `setpgid` 已随 `WorkerProcessGroup` 一起删除,执行器与主程序本来就没有。Windows 每执行器一个独立 Job Object,启用 KILL_ON_JOB_CLOSE,以 Job 终止为主清理机制。Linux/macOS 的进程组改由 Host 侧 `posix_spawn` + `POSIX_SPAWN_SETPGROUP(0)` 建立:组在 spawn 系统调用内原子创建,子进程执行第一条指令前就已是自成一组的组长,因此事后 `setpgid` 那个(实测必然 `EACCES` 的)窗口不存在,`kill(-pgid)` 恒有效,执行器也自动获得自己的组。宿主另有强制的收割循环(BCL 不收割非自己启动的子进程,僵尸会让 `HasExited` 永远为假),顺序为 `waitid(WNOWAIT)` 观察退出 → `kill(-pgid)` 趁 pid 未被释放时扫组 → `waitpid` 释放 pid,以免收割放大 pid 复用窗口。Linux 已在 WSL2 上验证(见 `forge-extension-host`);macOS 的 `pipe`+`fcntl`(无 `pipe2`)、`WNOWAIT` 常量与 `siginfo` 偏移分支**完全未验证**,须由 CI 的 macOS runner 确认,不得据此声称 macOS 已支持。进程组**不是**硬边界:后代自己 `setsid`/`setpgid` 即可脱离,POSIX 也没有 KILL_ON_JOB_CLOSE 的等价物——Host 被强杀时 Worker 只靠管道断开自行退出,其残留后代不被清理,因此不得宣称与 Windows Job 等价。操作系统权限沙箱及 CPU/内存/活动进程限制须另行确认威胁模型,不因进程隔离而视为已实现。交互终端的 PTY(Windows ConPTY、POSIX `posix_openpt` + `posix_spawnp(SETSID)`)放在同一个类的 `partial` 文件 `WorkerProcessOwnership.Pty.cs` 里,不新增原生类;终端 shell 在 Windows 上也进私有 Job,在 POSIX 上按会话扫树(见 `forge-extension-host`“交互终端”)。

**评审。** 评审意见属于工作区,按行/块锚定到 Diff;支持草稿批量发送和单条发送。评审方式可配置为 Reviewer SubAgent 一次评审或人工逐行评审。

**动态工作流。** 工作流以 Markdown 技能文件定义(frontmatter + 步骤文本,同 ThinkRail/PI 的 SKILL.md),由 Agent 阅读并执行;用户与插件均可提供,由路由工作流按任务类型选择(参考 choosing-a-workflow)。不引入新语法,也不提供 C# 状态机形态(已确认)。

## 开源代码内化

agent-framework、MCP、编辑器/Git 及其他成熟三方包,均只取有用源码并改命名空间内化;署名按许可证要求保留,不要求则不保留(见 `forge-goal`)。

**NuGet 引用规则:** 仅针对 Forge 主程序的开发。官方 .NET 库(`Microsoft.*`、`System.*`,即 dotnet 组织发布的包)一律在白名单内,无需逐个批准(2026-11-14 用户确认),版本仍钉在引用处,仍须服从 Native AOT 与依赖方向约束;下表列的是三方(非 dotnet 官方)库,新增任何三方包须先经用户同意。

| 库 | 版本 | 用在 | 约束 |
|---|---|---|---|
| Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent、Avalonia.Fonts.Inter | 12.1.6 | `Forge.App` | UI |
| Avalonia.Headless | 12.1.6 | 测试项目 | 仅测试 |
| Avalonia.AvaloniaEdit、AvaloniaEdit.TextMate(含其传递的 TextMateSharp 语法包) | 12.0.0 | `Forge.App` | 编辑器与 diff |
| Microsoft.Data.Sqlite | 11.* | 会话存储 | 含原生二进制,AOT 靠实测背书 |
| Microsoft.CodeAnalysis.CSharp(.Scripting) | 5.* | `ExtensionHost`、`PluginWorker` 等 JIT 侧 | 不进入主程序 |
| Microsoft.Extensions.Logging | 11.* | `Forge.App` | 日志抽象 |
| ZLogger | 2.6.2 | `Forge.App` | 诊断日志输出端,见 `forge-app` 诊断日志 |
| ZLinq | 1.6.1 | `Forge.App` | 只用在实测有分配的热路径,不启用 DropInGenerator |
| MessagePack | 4.0.0-rc.1 | `Forge.Ipc`、`Forge.Ipc.Protocol`、`Forge.Plugin.Sdk`、`Forge.ScriptRuntime` | IPC 线格式,只用源生成格式化器,见 `forge-ipc` |
| xunit、xunit.runner.visualstudio、Microsoft.NET.Test.Sdk | 2.* / 3.* / 18.* | 测试项目 | 仅测试 |
| Mermaider | 0.15.1(2026-10-06 发布,满足"稳定版 + 发布满两周";0.15.2/0.16/0.17 尚未满两周) | `Forge.App` 渲染层 | 2026-11-14 有条件批准、W11 已落地:仅用于把聊天/diff 里的 ```mermaid 块本地解析、布局并输出 SVG(ASCII 退路未启用);不引 JS、不联网;图类型白名单可配 |
| Svg.Skia | 4.9.1(2026-05-20 发布;5.x 依赖 SkiaSharp 4.148,与 Avalonia 12.1.6 的 3.119.4 不兼容,故不取) | `Forge.App` 渲染层 | 2026-11-14 有条件批准、W11 已落地;**AOT 验证口径(2026-11-15 用户接受)**:Windows 本地 `publish` AOT 0 警告 + CI 三平台 `publish` 矩阵为门,CI 测试矩阵(三 OS、JIT)里的真实渲染用例为运行时验证,AOT 产物里的实际渲染不单独起测试宿主。它只新增托管程序集,SkiaSharp 3.119.4 与 HarfBuzzSharp 8.3.1.3 与 Avalonia 同版(不新增原生资产),`Forge.App.csproj` 不再显式引用 SkiaSharp(由 Avalonia.Skia 传递,已核对 `project.assets.json` 解析为 3.119.4)。**传递依赖(2026-11-15 用户追认批准范围:批准根包即批准其传递闭包,不单独引用、版本由还原锁定)**:Mermaider → Sugiyama 0.15.1、Microsoft.Extensions.ObjectPool(官方 .NET 库,白名单内);Svg.Skia → ExCSS 4.3.1、ShimSkiaSharp / Svg.Model / Svg.Custom / Svg.Animation / Svg.SceneGraph 4.9.1(同一发布线的托管程序集)。仅用于 Mermaider 输出的 SVG 解析成 `SKPicture`(矢量回放,不栅格化) |

**插件的 NuGet 依赖不受上述白名单限制,由使用者控制(已确认)。** 默认只用 BCL 与 Plugin SDK;新增依赖须经用户审批并计入 DependencyHash;支持离线 NuGet 缓存;编译缓存键包含 SDK、Contracts、依赖与编译配置版本。

**编辑器依赖(已确认)。** 语言范围以 AvaloniaEdit 着色方案实际支持的语法为准;语法支持不等于内置 LSP 服务器,服务器安装与配置仍须明确呈现。现有 `Forge.Editor` 的无 UI 服务不能因替换控件而直接删除;须先验证 LSP、编码保存、着色、Diff 等能力迁移无损。

已核对的库缺口(12.0.0,以源码为准,不以 README 为准):该版本**没有多光标**(只有单 `Caret` 与 `RectangleSelection` 列选择),行尾符生成器被注释、`ShowTabs` 无读取者,`TextEditor.Load/Save(string)` 未实现。因此多光标按用户确认改用矩形列选择,`¶`/`→` 两个字形由 `Forge.App` 自己的零长度行内元素绘制,保存仍走 `Forge.Editor.TextFileCodec`。这些补位都在上层实现,不扩大库依赖。

## 编辑器着色

两层:本地 TextMate 语法立即给出关键字、字符串、注释等词法颜色;LSP 的 `semanticTokens` 返回后叠加覆盖标识符类别颜色。服务器未启动、崩溃或不支持语义 token 时只剩词法层,不影响编辑。不使用 tree-sitter(无需为三平台分发原生库)。TextMate 语法与兼容 Oniguruma 的正则引擎参考 Passenger 的做法,内化其源码(MIT,保留署名)。
