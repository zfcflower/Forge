---
id: task-agent-extensions
type: task-spec
status: draft
title: 审批对话框 + Agent 自主创建/管理扩展
parent: forge-app
---

# 审批对话框 + Agent 自主创建/管理扩展

## Request

goal:用户侧动态扩展——Agent 可在使用中自主发现能力缺口,创建、修改、注册、禁用、删除工具与插件;扩展持久化、重启自动恢复;插件默认未批准,按源码哈希审批。用户决定(2026-11-05):**先做审批对话框;Agent 提交后由用户当场批准才生效**。

## Product(已确认)

- **审批对话框**(补上长期缺口:现在 Ask 类工具调用等于拒绝):
  - 工具调用审批:显示工具名、参数、副作用、工作目录;允许 / 拒绝 /(可选)本会话内对同类调用记住。
  - 插件审批:显示源码(新建为全文,修改为 Diff)、manifest 权限声明、源码哈希;批准后热加载。
- **Agent 扩展工具**:创建、修改、禁用、删除扩展;形态为声明式工作流(L0)与 C# 插件(L2)。创建/修改提交后进入“待审批”,用户批准才注册;禁用与删除不需要审批(删除需确认)。
- 扩展持久化,重启自动恢复(已批准的按哈希恢复;未批准的保持待审批)。

## 待细化(本对话里问用户)

- “记住”审批的范围与时长(本会话 / 本项目 / 永久),以及在哪里撤销。
- 扩展存放位置:全局插件目录还是项目内;与常规工作区 `extensions/` 草稿目录的关系(原规则:草稿永不自动加载)。
- 审批对话框是模态还是非模态队列(多个子 Agent 同时请求时)。
- Agent 修改已批准插件时,旧版本在新版本批准前是否继续生效。

## 契约

- 审批走已有 `IApprovalHandler`(`src/Forge.Abstractions/Tools.cs`);App 实现它并注入 `AgentHost.ApprovalHandler`。
- 插件批准/撤销已有 `ExtensionHostClient.ApprovePluginAsync`/`RevokePluginAsync` 与 `plugins.status`;manifest 权限、哈希、依赖规则见 forge-extension-host、forge-approval SPEC,不得削弱。
- 新的 Agent 工具定义放 Core 工具注册表;注意内置 Agent 白名单与可缓存工具集(forge-core SPEC)。

## 归属路径

`src/Forge.App/Approval/**`(新目录:对话框与队列)、`src/Forge.Core/Tools/**` 中新增扩展工具、`src/Forge.HostClient/**` 与 `src/Forge.ExtensionHost/**` 中为扩展写入/禁用/删除新增的 op、`Localization/Strings.Extensions.cs`、设置分区 `extensions`、对应测试;SPEC:forge-app、forge-core、forge-host-client、forge-extension-host、forge-approval。

## 注意

终端任务同时改 ExtensionHost/HostClient 的请求分发;只追加各自的 op,提交前 rebase。安全相关改动完成后必须走一次评审(AGENTS.md Review 角色)。

## 共同前提(所有五个任务)

- **底座已合入**(`19c97d8`):偏好 `AppPreferences`/`PreferencesStore`、实时文案 `Loc`、设置分区 `SettingsSections`(`src/Forge.App/Preferences/`、`Localization/`)。用法与坑以 forge-app SPEC“偏好、文案与设置分区”为准:分区在 `AppComposition.Create()` 里向 `composition.Settings` 追加并在 `App.axaml` 加模板;文案表用 `[ModuleInitializer]` 自注册;视图模型里缓存的文本订阅 `Loc.LanguageChanged`。
- **新文案**一律注册到本功能自己的表 `src/Forge.App/Localization/Strings.<功能>.cs`(中英两份),不要改 `ViewModels/Strings.cs`,也不要改别的功能的表。
- **设置项**放在本功能自己的设置分区(`SettingsSectionIds` 里预留了 id 与顺序),不要改设置页本身。
- **并行**:五个任务在不同对话里同时进行。只改本 spec“归属路径”列出的路径;需要越界时先停下问用户。共享文件(`AppComposition.cs`、`App.axaml`)只做“追加注册”这类最小改动,提交前 `git pull --rebase`。
- **本文件位置**:`docs/tasks/`(随仓库提交,新 worktree 自带);任务完成后由本任务删除它。
- **流程**:按 brainstorming 工作流进行;本 spec 的 Product 是已确认部分,“待细化”的问题在本对话里向用户问清再定 Design。分工与模型按 `AGENTS.md`。
