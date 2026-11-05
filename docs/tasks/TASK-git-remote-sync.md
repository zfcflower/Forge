---
id: task-git-remote-sync
type: task-spec
status: draft
title: Git 与 PR 工作流(对标 ThinkRail 的全部 git/PR 能力)
parent: forge-app
---

# Git 与 PR 工作流(对标 ThinkRail 的全部 git/PR 能力)

## Request

goal:深度 Git 集成——分支、Worktree、提交、Diff、PR 流程。
用户决定(2026-11-05):边界是**学到 ThinkRail 在 git 与 PR 上的所有能力**,做进 Forge。这条决定取代了同日稍早的“暂不对接任何托管平台”。

## 对标清单(ThinkRail 现有能力 → Forge)

ThinkRail 的这些能力来自它的工具(工作区、计划步骤提交、计划摘要)和 `shipping-a-pr` 工作流。工作流源文件在 `%LOCALAPPDATA%/ai.thinkrail.app/stable/app/Resources/app/runtime/skills/host/shipping-a-pr/`,共 7 个 md,实施前通读,不要照搬文案。

| # | ThinkRail 能力 | Forge 现状 | 要做的 |
|---|---|---|---|
| 1 | 每个任务一个隔离工作区(worktree + 按任务命名的分支) | 已有(会话 = worktree,`forge/<slug>`) | 新建会话时可编辑分支名;分支名在会话标题栏可见 |
| 2 | **步骤提交**:计划步骤完成时,把这一步的改动提交到会话分支,提交说明遵循仓库现有风格(读 `git log` 判断是否 Conventional Commits) | 只有 `GitCli.CommitAsync` 和 `todo` 工具,两者没有关联 | TODO 项增加 `commitSubject`、`summary`、`verification` 字段;完成时由 App 提交这一步的改动;提交说明由 Agent 按仓库风格生成;步骤改动的范围用轮次检查点(`turn_checkpoints`)界定 |
| 3 | 计划摘要即 PR 正文(Summary / Changes / Testing + 自动生成的 Plan 段) | 无 | 会话级“计划摘要”;PR 正文由摘要加各步骤的 summary/verification 渲染 |
| 4 | **创建 PR** 的 5 道闸门:工作树干净且已提交、fetch 后 rebase 到 base、清理临时产物、核对验证证据、自审全量 diff;然后 push、创建 PR | 无 | 内置工作流 + 工具,闸门由代码强制执行,不只是写在提示词里 |
| 5 | **同步**:rebase 或 merge 到 base,理解双方改动后再解冲突,重新验证再 push | 无 | 冲突解决由 Agent 完成,UI 显示冲突文件与结果 |
| 6 | 修改 PR 标题与正文(先读取现状,改完再核对是否生效) | 无 | 同上 |
| 7 | 改动前后的截图附到 PR | 无 | 待细化(见下) |
| 8 | **检查/CI**:快照模式(查一次)与等待模式(盯到全绿,读失败日志修复,确认可合并:mergeState/draft/reviewDecision);“不确定”不当作通过 | 无 | 两种模式都做;等待模式在后台轮询,有界、可取消 |
| 9 | **处理评审意见**:先读完整个 PR,对每条意见先判断(采纳/反驳/澄清)再动代码,改完回复并标记已解决 | 本地评审面板(`ReviewStore`)只读 | 拉取 PR 评审线程到评审面板;Agent 按“先判断再改”的流程处理;回复与 resolve 回写平台 |
| 10 | 应用内评审意见包 → 逐条处理并标记已解决 | 评审面板只读,行评论未做 | 本地评审意见与 PR 评审意见共用同一套处理流程 |
| 11 | 远程同步:fetch/pull/push、上游、超前/落后 | 只有部分底层命令 | 分支与远程操作(原任务范围,保留) |

## Product(已确认)

- 平台:**GitHub,经 `gh` CLI**(复用用户本机已登录的 gh,Forge 不保存令牌)。没有 gh 或没登录时,PR 相关功能明确提示不可用,本地 git 能力不受影响。
- 分支与远程:查看当前分支与上游、超前/落后数;新建、切换、设置上游、删除;fetch、pull、push(含首次 `-u`)。失败要可见、可恢复:鉴权失败、非快进、冲突、远程不存在。
- 步骤提交、PR 生命周期、CI 监控、评审意见处理,都按上面对标清单的语义实现。

## 待细化(本对话里问用户)

- pull 策略:只允许 fast-forward / rebase / merge,是否可配置。
- 步骤提交是否默认开启;没有 TODO 计划的会话是否按“轮次”提交。
- 截图(#7):Forge 截谁的图?被开发项目的界面需要能启动它;可能先只支持用户手动附图。
- 等待模式的轮询间隔、上限与通知方式(状态栏、系统通知)。
- 入口位置:会话标题栏、项目树 worktree 行右键、还是检视区单独一个“PR” panel。
- 是否预留 GitLab(glab)的抽象层,还是只做 GitHub。

## 设计提示

- 分三层:`Forge.Workspace` 加 git 远程操作和 `GhCli`(与 `GitCli` 同样的进程与环境隔离,JSON 输出用源生成解析);Core 加给 Agent 用的工具(`git_sync`、`pr_create`、`pr_checks`、`pr_comments` 等,审批按副作用划分:push/创建/回复属于 Write);闸门与流程做成内置工作流(Markdown workflow,`Forge.Workflows`),由代码强制执行闸门。
- 建议按依赖分批交付,每批单独评审合入:① 分支与远程同步 → ② 步骤提交与计划摘要 → ③ PR 创建/同步/改正文 → ④ 检查与等待模式 → ⑤ 评审意见处理 → ⑥ 截图。

## 归属路径

- `src/Forge.Workspace/Git/**`:新增方法,不改既有签名语义;新增 `GhCli`。
- `tests/Forge.Workspace.Tests/Git/**`。
- `src/Forge.Core/Tools/**` 中新增的 git/PR 工具,以及 TODO 存储与 `todo` 工具的字段扩展(`ITodoStore` 契约变更须先说明)。
- `src/Forge.Workflows/**` 中的内置工作流。
- `src/Forge.App` 里本功能新增的视图和视图模型。
- `Localization/Strings.Git.cs`、设置分区 `git`。
- 涉及的 SPEC:forge-workspace、forge-core、forge-workflows、forge-app、goal(已更新)。

## 约束

- 所有 git 与 gh 调用都走受控的 CLI 封装,UI 线程不碰。凭据由 git/gh 自己处理;需要交互输入时可以取消,且不会卡住(`GIT_TERMINAL_PROMPT=0`、`GH_PROMPT_DISABLED=1`,并设超时)。
- 不可逆操作(push、创建 PR、回复评审)执行前,先重新读取当时的远程状态并核对,不用之前缓存的结果(对标 ThinkRail “Observed, never assumed”)。
- 评审意见处理与 Agent 自建扩展都会接触审批对话框(扩展任务负责做对话框);本任务的 Write 类工具走同一个 `IApprovalHandler`。

## 共同前提(所有五个任务)

- **底座已合入**(`19c97d8`):偏好 `AppPreferences`/`PreferencesStore`、实时文案 `Loc`、设置分区 `SettingsSections`(`src/Forge.App/Preferences/`、`Localization/`)。用法与坑以 forge-app SPEC“偏好、文案与设置分区”为准:分区在 `AppComposition.Create()` 里向 `composition.Settings` 追加并在 `App.axaml` 加模板;文案表用 `[ModuleInitializer]` 自注册;视图模型里缓存的文本订阅 `Loc.LanguageChanged`。
- **新文案**一律注册到本功能自己的表 `src/Forge.App/Localization/Strings.<功能>.cs`(中英两份),不要改 `ViewModels/Strings.cs`,也不要改别的功能的表。
- **设置项**放在本功能自己的设置分区(`SettingsSectionIds` 里预留了 id 与顺序),不要改设置页本身。
- **并行**:五个任务在不同对话里同时进行。只改本 spec“归属路径”列出的路径;需要越界时先停下问用户。共享文件(`AppComposition.cs`、`App.axaml`)只做“追加注册”这类最小改动,提交前 `git pull --rebase`。
- **本文件位置**:`docs/tasks/`(随仓库提交,新 worktree 自带);任务完成后由本任务删除它。
- **流程**:按 brainstorming 工作流进行;本 spec 的 Product 是已确认部分,“待细化”的问题在本对话里向用户问清再定 Design。分工与模型按 `AGENTS.md`。
