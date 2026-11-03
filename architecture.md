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
| `Forge.Ipc`(新增) | 命名管道 + 4 字节长度前缀 JSON 的协议、帧编解码、协议版本;主程序、宿主、Worker 共用;须 AOT 兼容 |
| `Forge.Plugin.Sdk`(新增) | 供插件源码引用的稳定 SDK 与契约,独立版本化,不依赖 Core |
| `Forge.Core` | Agent 循环、会话、模型提供方(OpenAI 兼容、Anthropic)、SubAgent 调度与提示词缓存、提示词三层合成 |
| `Forge.Workspace` | Git 与 Worktree、规格图(spec-graph)读写、评审意见存储 |
| `Forge.Mcp` | MCP 客户端与服务器管理 |
| `Forge.Editor` | 文本缓冲、LSP 客户端(含语义 token)、TextMate 着色、补全、Diff 模型;不含 UI |
| `Forge.ExtensionHost`(新增,JIT) | 常驻进程:插件发现与注册、生命周期、Roslyn 编译与缓存、声明式工作流执行、Worker 管理、版本与依赖管理 |
| `Forge.PluginWorker`(新增,JIT) | 独立进程:加载并执行动态 C# 插件与 run_code 脚本;可被整体终止 |
| `Forge.HostClient`(新增,AOT) | 主程序侧的 `ExtensionHostClient`:启动/连接 Host、`ITool` 代理、`host.tool_call` 回传(审批在主程序)、Host 崩溃保留工具快照;只依赖 Abstractions、Ipc、Plugin.Sdk |
| `Forge.CodeMode` / `Forge.Plugins` | 现有进程内实现(Roslyn 脚本、ALC 宿主)迁入 `ExtensionHost`/`PluginWorker`;迁移前不得被 `App` 引用(迁移方案见 Stage 2) |
| `Forge.App` | Avalonia 桌面 UI 与组合根(含 `ExtensionHostClient`);唯一知道所有主程序项目的层 |

主程序侧(Native AOT):`Abstractions`、`Ipc` ← `Core`、`Workspace`、`Mcp`、`Editor` ← `App`。JIT 侧:`Plugin.Sdk`、`Ipc` ← `ExtensionHost` ← `PluginWorker`。主程序不引用 Roslyn、`Plugins`、`CodeMode`;`ExtensionHost` 不引用 `Core`。(新分层待 Stage 0–2 验证,未确认)

## 关键决策

**性能第一。** 这是与 `forge-goal` 一致的最高约束,冲突时它优先:
- UI 线程只做渲染与输入;LSP、着色、Git、文件扫描一律后台线程,结果经不可变快照回到 UI。
- 列表(会话、文件树、消息、Diff)使用 Avalonia 虚拟化;Diff 与编辑器增量渲染,不整体重绘。
- Agent 流式输出按渲染帧批量合并,每帧最多更新一次 UI(已确认)。

**会话与工作区一对一。** 每个会话绑定一个 Git Worktree,会话的工具调用、文件读写和终端的工作目录均限定在其中;并行会话之间不共享工作目录。

**Native AOT 主程序(已确认要求;可行性已由 Stage 0 实测确认)。** 主程序不可变、Native AOT 发布;不在其中使用 Roslyn 动态编译或 `AssemblyLoadContext` 加载插件。五个主程序项目(`Abstractions`/`Core`/`Workspace`/`Mcp`/`Editor`)已开启 `IsAotCompatible` 且 AOT 分析器 0 警告;Workspace 的 JSON 输出改走反射无关的 `JsonPayload`,持久化类型走 `WorkspaceJsonContext` 源生成。实测(win-x64):AOT 产物 25.3 MiB,headless 对话 81-84ms、GUI 到窗口句柄 166ms。风险:Avalonia 与 Microsoft.Data.Sqlite/SQLitePCLRaw 未声明 AOT 兼容,靠实测背书,CI 需三平台冒烟;原生库不能静态链接,"单文件"不成立;Linux/macOS 发布未在本机验证(需 CI)。只有核心依赖确实无法解决时才重新评估放弃 AOT。

**扩展点统一为契约,执行在进程外。** 工具、命令、提示词、Agent、工作流经 `Forge.Plugin.Sdk` 注册,由 `ExtensionHost` 通过 IPC 向主程序报告;主程序侧以代理 `ITool` 暴露。模型提供方内置,仅在设置中配置,不开放插件注册。

**插件 UI 为声明式。** 插件不能返回进程内控件,改由 IPC 返回 UI Schema(第一阶段 Form、Table、Markdown、Text、Button、Status),由主程序渲染 Avalonia 控件。原 `PanelDefinition(Func<object>)` 契约需迁移。

**三种扩展形态,不混为一套。** Markdown 技能描述 Agent 行为与知识;声明式 Workflow(JSON,第一阶段只含顺序、并行、JSON 路径映射,优先复用 `run_code`)组合已有工具;C# 插件实现新逻辑。保存下来的 run_code 脚本也可作为持久化扩展。

**Extension Host 与 Worker。** `ExtensionHost` 是常驻的普通 .NET JIT 进程,要求用户已安装对应 .NET 运行时(已确认;启动时检测并给出明确错误)。新创建和已批准的 C# 插件默认都在独立 `PluginWorker` 进程执行;共享 Host 仅为高信任低风险插件的可选性能优化。批准只代表允许执行,不代表可进入共享进程。`AssemblyLoadContext` 只负责加载卸载,不提供隔离。取消令牌无法停止不合作的代码,必要时终止整个 Worker(含进程树)。

**审批与哈希。** 新插件默认未批准,展示源码差异与权限需求,用户批准后才可启用。每个插件记录 SourceHash、ApprovedHash、DependencyHash、SDKVersion 等;重启只恢复哈希匹配的已批准版本;源码或依赖变化使批准失效;不能通过改 manifest 或缓存状态绕过。插件调用主程序能力一律经 IPC,由主程序的权限引擎判断、审批与审计。

**IPC。** 主程序↔Host、Host↔Worker 都用 `NamedPipeServerStream`/`ClientStream`(Linux/macOS 上由 Unix 域套接字实现)。消息含 ProtocolVersion、RequestId、PluginId、Operation、Arguments、Result、Error、Cancel、Timeout;有最大帧长;支持并发、取消、超时、批量与断连处理;协议版本不匹配时拒绝并提示。

**可靠性。** Worker 支持超时、进程树清理、并发上限、崩溃自动恢复(在途请求明确失败、重启后重新注册工具);Windows 可设 Job Object 内存与进程数上限,其他平台不承诺。Host 崩溃时主程序检测退出、失败在途请求、重启 Host、恢复已批准插件并重新注册工具。进程隔离不是安全沙箱:同一操作系统账户下插件仍可访问该账户可访问的文件与网络;OS 级沙箱不在早期阶段承诺。

**提示词三层。** 系统提示词 → 全局 Agent → 项目 Agent,逐层叠加,机制与 PI harness 一致。

**SubAgent 与缓存。** 相同目的的 SubAgent 共享稳定的提示词前缀以命中提示词缓存;动态内容(时间、工作区状态等)作为请求末尾的一条临时 user 消息,不进历史与存储。内置 Scout、Worker、Plan、Reviewer,每种可单独设置模型与思考强度;同类型实例可按 id 续跑。嵌套深度上限 3,同时活跃的 Agent 数按整棵树计数(默认 4),父 Agent 等待子 Agent 结果时交还名额;均由 `forge-core` 强制。

**codemode。** 模型输出一段 C# 脚本,脚本可并行(`Task.WhenAll`)调用已注册工具,只把最终结果一次返回模型。脚本的 Roslyn 编译与执行在 `ExtensionHost`/`PluginWorker` 中,不在 Native AOT 主程序里;主程序只持有 `run_code` 代理工具。子进程不直接执行工具,只通过 IPC 向主程序发起调用,审批、并发上限与结果截断全在主程序。取代此前的 T1/T2/T3 分层;`dotnet run` 文件式应用(T3)只作为可选能力,不是前提。现有进程内实现与白名单语义检查的迁移见 `forge-codemode`(待更新)。

**评审。** 评审意见属于工作区,按行/块锚定到 Diff;支持草稿批量发送和单条发送。评审方式可配置为 Reviewer SubAgent 一次评审或人工逐行评审。

**动态工作流。** 工作流以 Markdown 技能文件定义(frontmatter + 步骤文本,同 ThinkRail/PI 的 SKILL.md),由 Agent 阅读并执行;用户与插件均可提供,由路由工作流按任务类型选择(参考 choosing-a-workflow)。不引入新语法,也不提供 C# 状态机形态(已确认)。

## 开源代码内化

agent-framework、MCP、编辑器/Git 及其他成熟三方包,均只取有用源码并改命名空间内化;署名按许可证要求保留,不要求则不保留(见 `forge-goal`)。

**NuGet 引用规则:** 仅针对 Forge 主程序的开发。默认仅限 BCL、官方基础包与特别指定的包。已确认白名单:Avalonia(UI)及官方同仓包——`Avalonia.Controls.DataGrid`、`Avalonia.Controls.TreeDataGrid`、`Avalonia.Controls.WebView`、`Avalonia.Headless`(含 `.XUnit`,后者连带要求 xunit v3)、`Avalonia.Skia`(含随其传递引入的 SkiaSharp)、`Avalonia.Themes.*`、`Avalonia.Fonts.*`,均不内化;`AvaloniaUI.DiagnosticsSupport` 属 Avalonia Plus 商业工具链,不在白名单内;三方主题/图标/图表库(FluentAvalonia、SukiUI、Semi.Avalonia、Irihi.Ursa、Material.Avalonia、Citrus.Avalonia、ShadUI、LiveCharts、Projektanker.Icons.*、Lucide.Avalonia、Svg.Skia 等)未批准,引入前逐项确认。Microsoft.Data.Sqlite(会话存储,含原生二进制),均不内化;Roslyn 仅用于 `ExtensionHost`/`PluginWorker`,不进入主程序;测试项目另用 xunit(仅测试,不随产品发布;未确认是否纳入);neuecc 的库(https://github.com/neuecc,如 MessagePack-CSharp、MemoryPack、ZLinq)按需选用。其他三方包默认内化源码;若某包体量大或不值得内化,开发中由 Agent 提示用户,经同意后再加入白名单。

**插件的 NuGet 依赖不受上述白名单限制,由使用者控制(已确认)。** 默认只用 BCL 与 Plugin SDK;新增依赖须经用户审批并计入 DependencyHash;支持离线 NuGet 缓存;编译缓存键包含 SDK、Contracts、依赖与编译配置版本。

## 编辑器着色

两层:本地 TextMate 语法立即给出关键字、字符串、注释等词法颜色;LSP 的 `semanticTokens` 返回后叠加覆盖标识符类别颜色。服务器未启动、崩溃或不支持语义 token 时只剩词法层,不影响编辑。不使用 tree-sitter(无需为三平台分发原生库)。TextMate 语法与兼容 Oniguruma 的正则引擎参考 Passenger 的做法,内化其源码(MIT,保留署名)。
