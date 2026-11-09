---
id: forge-goal
type: goal-and-requirements
status: active
title: Forge — C# 编码 Agent Harness
---

# Forge

面向开发者的桌面编码 Agent Harness:全 C#、Avalonia UI,隔离并行开发,以 Spec 为事实依据。

## Overview

Forge 是一个桌面应用,承载 LLM 编码 Agent 的完整工作环境:会话、工作区、工具、子 Agent、工作流、编辑器与评审。产品形态与核心功能参考 ThinkRail,系统提示词/Agent 机制与 PI harness 一致。全部代码用 C# 实现,不含 Python 或 JS。

## Problem

开发者并行驱动多个编码 Agent 时,需要在多个分支上互不干扰地工作、让 Agent 以项目规格而非猜测为准、自带模型与密钥,并在合并前评审 Agent 的改动(可由 Reviewer 子 Agent 一次评审,或人工逐行评审,可配置)。现有工具多为非 C# 技术栈,难以用 C# 扩展,且 C# 开发者缺少可深度定制的同类 harness。(推断,未确认)

## Target Users

- 以 C#/.NET 为主要技术栈、需要扩展 harness 本身的开发者。
- 同时驱动多个 Agent 任务并需要严格评审的个人开发者。
- 需要最大自由度、要自定义动态工作流的开发者。

## Goals

- 性能第一:UI 响应、会话切换、Diff/编辑器渲染、工具并行调度均以性能为首要约束,与其他目标冲突时性能优先。
- 每个任务在独立 Git Worktree 中运行,并行任务互不污染工作目录。
- Agent 行为受项目 Spec 约束,Spec 与代码同仓演进。
- 任何能力(工具、命令、提示词、工作流、声明式 UI 面板)都可由用户侧 C# 扩展提供,且不需重新编译主程序;主程序为不可变的 Native AOT 程序。模型提供方内置,在设置中配置。
- Agent 的每处改动可在 Diff 中按行/块评论;评论可先存为草稿并一次性发送,也可单条单独发送。
- 评审方式可配置:Reviewer 子 Agent 一次评审,或人工逐行评审。

## Non-Goals

- 不提供 Web/移动版,只做桌面。
- 不含 Python/JS 代码或运行时依赖。
- 不随意引用第三方 NuGet 包(针对 Forge 主程序开发):仅限 BCL、官方基础包(Avalonia)、Microsoft.Data.Sqlite 与特别指定的包(neuecc 的库);Roslyn 只用于扩展宿主;其他包默认内化源码,不值得内化的由 Agent 提示用户后加入白名单。插件自己的 NuGet 依赖由使用者控制并经审批,不受此限(见 Technology)。
- 内化开源代码按需保留署名:许可证要求的保留,不要求的不保留;只取有用代码和结构。
- 不把进程隔离当作安全沙箱;未经审批的插件不执行,批准也不代表代码没有可靠性风险。

## Capabilities

**工作区与 Git**
- 隔离工作区:基于 Git Worktree 的并行开发,每个会话绑定一个工作区。
- 深度 Git 集成:分支、Worktree、提交、Diff、远程同步;文件面板可按轮次、未提交改动或所选本地/远端基线浏览会话差异,并在统一差异视图中逐文件审阅;按计划步骤提交;PR 生命周期(创建、同步、正文、CI 检查与等待、评审意见处理),经 GitHub `gh` CLI,不保存凭据。

**Agent 能力**
- Specs as ground truth:规格树(spec-graph)浏览与读写,Agent 以其为依据。
- MCP servers:接入并管理 MCP 服务器及其工具。
- 提供方自选:用户自配模型提供方与凭据(OpenAI 兼容、Anthropic)。
- codemode:Agent 生成 C# 脚本(Roslyn),在 Extension Host/Worker 中并行按需调用已注册的 C# 工具,只向模型返回最终一次结果。
- 动态工作流:可自定义,参考 ThinkRail 的 choosing-a-workflow(按任务类型路由到不同工作流技能)。
- 可自定义命令、工具、Prompts:系统提示词 + 全局 Agent + 项目 Agent 三层,机制与 PI harness 一致。
- 可重用 SubAgents:相同目的的子 Agent 复用提示词缓存以省 Token;内置 Scout、Worker、Plan、Reviewer;嵌套最多三级。
- 用户侧动态扩展:Agent 可在使用中自主发现能力缺口,创建、修改、注册、禁用、删除工具与插件;扩展持久化、重启自动恢复,不需要重新编译主程序。扩展形态为声明式工作流(L0,只含顺序、并行、JSON 路径映射)与动态 C# 插件(L2,由 Roslyn 编译);插件默认未批准,按源码哈希审批,默认在独立 Worker 进程执行;UI 面板为声明式 Schema,由主程序渲染。模型提供方内置,不开放插件注册。

**界面与编辑**
- Avalonia 桌面 UI,风格参考 ThinkRail,支持 Windows / macOS / Linux(已确认)。
- 界面语言可选;字体可自定义。
- 可独立打开的终端。
- 内置 TODO、规格树、文件树、评审意见面板。
- 编辑器能力:LSP 诊断、符号与语义 token,TextMate 语法着色(语义 token 叠加其上),Tab 补全、Git Diff、行/块人工评论审核。

## Technology

| Aspect | Choice | Rationale |
|---|---|---|
| 语言 | C# 14 / .NET 10 | 用户指定 |
| UI | Avalonia(仅桌面) | 用户指定;跨平台 |
| Agent 核心 | 参考 microsoft/agent-framework (dotnet) 与 microsoft/mcp | 只取必要源码并改命名空间,不引用 NuGet 包 |
| 编辑器/Git 及其他三方 | 学习成熟开源库并内化源码 | NuGet 白名单:BCL、Avalonia、Roslyn、neuecc 的库;新增须经用户同意 |
| 开发 Skills | 采用 Avalonia-skills、BlazingFastDotnet、dotnet/skills 中适用者 | 约束本项目开发实践 |
| codemode | Roslyn 脚本,在 Extension Host/Worker 中编译执行 | 全 C#,并行调用工具;主程序保持 AOT |
| 主程序发布 | Native AOT(可行性经 Stage 0 实测确认) | 主程序不可变、启动快;用户扩展不需重新编译 |
| 扩展执行 | 独立 Extension Host(JIT,要求已安装 .NET 运行时)+ Plugin Worker 进程 | Roslyn 与 AssemblyLoadContext 不进入 AOT 主程序;崩溃与死循环可整体终止 |

内化的开源代码只取有用部分与结构参考,不引用库本身;署名按许可证要求保留,不要求则不保留。
