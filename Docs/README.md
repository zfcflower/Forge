# FORGE 功能总览

本目录按**能力类型**整理 FORGE 已实现的功能，每类一篇，说明精确到工具名、默认值、上限与用户可见入口。

**依据**：`architecture.md`、`goal-and-requirements.md`、`src/*/SPEC.md`（16 份模块 SPEC）与 `src/` 下的实际代码。事实顺序以 SPEC 为准，写进本文的每一条都能在代码里找到对应实现。

**口径**

- “已实现” = 代码与模块 SPEC 同时存在，且能在界面上找到入口（或由 Agent 工具直接提供）。
- SPEC 里明确标注“未实现 / 未接线 / 未验证”的内容不写进功能章节，单列在 [14-limitations.md](14-limitations.md)。
- 数字都是代码默认值；用户设置能改动的范围同时给出上下限。

## 产品定位

FORGE 是面向开发者的**桌面编码 Agent Harness**：把 LLM 编码 Agent 所需的会话、隔离工作区、工具、子 Agent、工作流、编辑器、评审与扩展串成一个应用。

- 全 C#、Avalonia 桌面 UI（Windows / macOS / Linux，仅桌面）。
- 每个会话绑定一个 Git Worktree，并行任务互不污染工作目录；一个工作区同一时刻只有一个活动根会话。
- 项目规格（spec-graph）与代码同仓演进，Agent 以规格为事实依据，规格可读可写。
- 主程序 Native AOT、不可变；插件、声明式工作流、codemode 脚本在进程外执行，用户侧扩展不需要重新编译主程序。
- 模型提供商与凭据自带、内置；UI 语言（中文 / English）、字体、快捷键可改。

## 技术栈

| 方面 | 选择 | 说明 |
|---|---|---|
| 语言 / 运行时 | C# 15 / .NET 11 | `global.json` 固定 |
| UI | Avalonia 12.1.6（Fluent） | 手写 INPC + ICommand，无 MVVM/DI 框架 |
| 编辑器 | AvaloniaEdit 12.0.0 + AvaloniaEdit.TextMate | 词法着色 + LSP 语义叠加 |
| 持久化 | Microsoft.Data.Sqlite 11.*、源生成 JSON | `forge.db` / `reviews.db` / `approvals.db` |
| 线格式 | MessagePack 4.0.0-rc.1（源生成、AOT） | 主程序 ↔ Host ↔ Worker/执行器 |
| 扩展编译 | Roslyn 5.*（仅 JIT 侧） | 主程序依赖闭包不含 Roslyn |
| 图表渲染 | Mermaider 0.15.1 + Svg.Skia 4.9.1 | 本地解析、布局、矢量回放，不联网 |
| 主程序发布 | Native AOT | win-x64 `Forge.App.exe` 72.92 MiB（2026-11-16 同机重测；早先记录 49.7 MiB，差值未逐项定位） |

### 进程拓扑

```
Forge.App（Native AOT，Avalonia UI，唯一知道全部主程序项目）
 ├─ Forge.Core / Workspace / Mcp / Editor / IO / Approval / HostClient（全部 AOT）
 └─ Forge.ExtensionHost（常驻 JIT，需已安装 .NET 运行时）
     ├─ Forge.PluginWorker   × N（每插件一个进程，可整体终止）
     └─ Forge.ScriptExecutor × 1/次调用（一次性执行器，跑完杀整棵进程树）
```

这三个 JIT 进程都需要用户已安装 .NET 11 运行时；AOT 主程序不需要（见 13.7）。

主程序与 Host、Host 与 Worker/执行器之间是命名管道（Linux/macOS 为 Unix 域套接字）+ 4 字节长度前缀 + MessagePack 帧（协议版本 2，单帧上限 16 MiB）。

## 模块地图

| 项目 | 层 | 职责 | 运行时 |
|---|---|---|---|
| `Forge.Abstractions` | 契约 | 工具、审批、存储、提示词、Agent 事件、自动化契约；零外部依赖 | AOT |
| `Forge.IO` | 底座 | 原子写、路径守卫、有界读写、遍历、gitignore、grep、进程执行；7 个文件/shell 工具；回收站、单实例锁 | AOT |
| `Forge.Ipc` | 传输 | 命名管道 + 长度前缀 + MessagePack 帧、请求关联、协议版本、错误分层 | AOT |
| `Forge.Ipc.Protocol` | 传输 | Host 内部操作的类型化负载（插件执行、Worker、工具回传、终端） | AOT |
| `Forge.Plugin.Sdk` | 契约 | 插件源码引用的稳定 SDK 与全部线上 DTO（3.0.0） | AOT |
| `Forge.Approval` | 安全 | 用户插件的源码哈希审批引擎：纯逻辑 + SQLite | AOT |
| `Forge.Core` | 核心 | Agent 循环、会话存储、三家提供商协议、提示词三层、SubAgent、压缩、自动化引擎、限速、网络栈 | AOT |
| `Forge.Workspace` | 领域 | Git/Worktree、Diff 模型、spec-graph、评审与结论存储、PR 平台（`gh`） | AOT |
| `Forge.Mcp` | 领域 | MCP 客户端与服务器管理（stdio / streamable HTTP、OAuth、资源订阅） | AOT |
| `Forge.Editor` | 领域 | 文本缓冲、LSP 客户端、TextMate、Diff 锚点、折叠、语言服务器发现 | AOT |
| `Forge.Workflows` | 扩展 | 声明式工作流引擎（L0）：`after` 依赖、并行、JSON 路径映射 | JIT（被 Host 引用） |
| `Forge.Scripting` | 扩展 | Roslyn 编译/缓存/版本化产物/可卸载 ALC；`run_code` 常驻编译服务 | JIT |
| `Forge.ScriptRuntime` | 扩展 | 脚本可见表面、运行回路、产物校验（不含 Roslyn） | JIT |
| `Forge.ScriptExecutor` | 扩展 | 一次性执行器：装载一个已验证产物、运行一次 | JIT |
| `Forge.ExtensionHost` | 扩展 | 常驻进程：插件发现/注册/生命周期/审批把关/编译服务/工作流执行/PTY | JIT |
| `Forge.PluginWorker` | 扩展 | 每插件一个进程：加载并执行一个动态 C# 插件 | JIT |
| `Forge.HostClient` | 客户端 | 主程序侧 Host 代理：`ITool` 代理、`host.tool_call` 回传、终端、扩展编写、崩溃快照 | AOT |
| `Forge.App` | UI | 组合根 + 停靠式工作台（布局、面板、编辑器、终端、设置、通知、更新） | AOT |

## 文档索引

| 文档 | 内容 | 关键点 |
|---|---|---|
| [01-sessions-and-workspaces.md](01-sessions-and-workspaces.md) | 会话、占用、接手、分叉、合并、移除、恢复 | 一工作区一活动根会话；交接事务；回收站 |
| [02-agent-runtime.md](02-agent-runtime.md) | Agent 循环、48 个内置工具、子 Agent、压缩、TODO/评审门禁、用量与限速、自动化 | 深度 2、并发 4、触发线 85% |
| [03-providers-and-network.md](03-providers-and-network.md) | 三家协议、七种认证、模型目录、缓存、官方身份、代理与 DNS | models.dev 快照；TTL auto/5m/1h |
| [04-git-diff-pr.md](04-git-diff-pr.md) | worktree、改动浏览、diff 审阅、步骤提交、PR 工具与检查面板 | `pr_create` 五道闸门；只允许 force-with-lease |
| [05-spec-graph.md](05-spec-graph.md) | 规格树模型、7 个 spec 工具、面板与写入守卫 | frontmatter 往返无损 |
| [06-editor-and-code-intelligence.md](06-editor-and-code-intelligence.md) | TextMate、LSP、诊断、补全、重命名、折叠、富预览、行评审 | 语义 token 按行复用 |
| [07-workbench-ui.md](07-workbench-ui.md) | 停靠布局、项目树、面板、转录、命令面板、通知、设置、外观、本地化 | 27 个设置分区；布局快照 v5 |
| [08-terminal.md](08-terminal.md) | 交互终端：VT 引擎、线程模型、搜索、重启、流控 | 回滚默认 10000 行；1 MiB 窗口 |
| [09-prompts-skills-workflows.md](09-prompts-skills-workflows.md) | 三层提示词、模板三态、技能四层、编排注入、声明式工作流 | 内置 4 命令 + 4 Agent + 4 编排技能 |
| [10-mcp.md](10-mcp.md) | MCP 传输、配置、OAuth、资源订阅、工具选面 | `mcp_tool_search`；工具 schema 64K 上限 |
| [11-extensions-and-codemode.md](11-extensions-and-codemode.md) | 插件 SDK/Host/Worker/审批/安装、codemode 一次性执行器 | 哈希审批；失败预算 4 次 |
| [12-storage-and-data.md](12-storage-and-data.md) | 数据目录、`forge.db` v11、`reviews.db`、设置文件、密钥库 | 时间戳写守卫；FTS5 回退 |
| [13-operations-and-tooling.md](13-operations-and-tooling.md) | 单实例、诊断日志、自动更新、CI、基准 | 三 OS 构建 + Windows AOT 冒烟；当前无测试项目 |
| [14-limitations.md](14-limitations.md) | 未实现、未接线、未验证、明确的取舍 | macOS 分支未验证；面板渲染未做 |
| [15-appendix.md](15-appendix.md) | 工具总表、设置分区表、数据文件表、常量表、术语表 | 速查 |

## 阅读建议

- 想知道**用户能做什么**：07（界面）→ 04（Git/PR）→ 06（编辑器）→ 08（终端）。
- 想知道**Agent 能做什么**：02（运行时与工具）→ 05（规格）→ 09（技能与工作流）→ 10/11（扩展）。
- 想知道**怎么接模型、怎么管凭据与网络**：03 → 12（存储）→ 13（日志与更新）。
- 想知道**边界在哪**：14（限制）与 15.4（常量表）。
