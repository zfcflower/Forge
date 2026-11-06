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

## 细化结论(2026-11-05,用户已确认)

- pull:fast-forward / rebase / merge 三选一,默认只快进;不能快进时报错并提示改用 rebase/merge 或 `git_sync`。在 `git` 设置分区可改。
- 步骤提交:默认开启,只对 TODO 条目生效;没有 TODO 的会话不自动提交(不按轮次提交)。设置分区可关。
- 截图(#7):**自动截图**(做法见 ⑥)。
- 等待模式:30 秒一次,最长 30 分钟,可取消;结果写进 Git panel 与状态栏,**并发系统通知**。
- 入口:检视区新增 Git panel;会话标题栏显示分支名;状态栏显示超前/落后数。
- 平台:只做 GitHub,但**先定义平台接口** `IPrPlatform`,工具与 UI 只依赖接口,`GitHubPlatform`(基于 `GhCli`)是唯一实现。
- 写操作审批:Agent 发起的 push、创建/修改 PR、回复/resolve 都走现有 `IApprovalHandler`;审批对话框(扩展任务负责)上线前一律拒绝。用户在 Git panel 里点按钮发起的操作不走审批,点击本身就是确认。**待对接**:对话框合入后,在 `SessionRuntime` 接上处理器并端到端验证(已列入本对话 TODO;任务结束时若仍未对接,写进 forge-app SPEC 未决)。
- 越界清单 1–8 已获同意;两处偏差(工具放 Workspace、流程指引不进 Forge.Workflows)已获同意。

## Design

### 与设计提示的偏差(须确认)

- **git/PR 工具放在 `Forge.Workspace/Git`,不放 `Forge.Core/Tools`。** Core 只依赖 Abstractions,SPEC 明写“不含 Git”;Core 引用 Workspace 会反转依赖方向。Workspace 已有先例:7 个规格工具就在 Workspace 实现 `ITool`。这个目录也在归属路径里。
- **内置工作流不进 `Forge.Workflows`。** 该项目是 JSON 声明式(L0)引擎,依赖 Plugin.Sdk,主程序不引用它;仓里也还没有 Markdown 技能的加载器。闸门既然由代码强制,流程文字只是给 Agent 的指引,所以以 C# 原始字符串常量放在 `Forge.Workspace/Git/PrWorkflowText.cs`(不需要改 csproj 嵌入资源),由只读工具 `pr_workflow` 按阶段返回(creating / syncing / body / checks / review-comments / screenshots),各工具描述里点名先读它。将来有了 Markdown 技能加载器,再把它迁成技能文件。

### 分层

| 层 | 内容 |
|---|---|
| `Forge.Abstractions`(主 Agent 写契约) | `TodoItem` 加 `CommitSubject`/`Summary`/`Verification`/`CommitId`;`ITodoStore` 加带这些字段的更新重载与会话级计划摘要读写;新增 `IStepCommitter`(步骤提交钩子)。 |
| `Forge.Core`(存储与 `todo` 工具) | `StorageSchema` v1→v2:`todos` 加 4 列、新表 `plan_summaries(session_id PK, text, updated_at)`;`todo` 工具在 update 里接受 `commit_subject`/`summary`/`verification`,新增 `plan_summary` 操作;状态变为 done 时调用 `IStepCommitter`,把提交结果写进工具结果。 |
| `Forge.Workspace/Git` | `GitCli` 新增方法(分支、上游、ahead/behind、fetch/pull/push、rebase/merge 与冲突状态、ls-remote);`IPrPlatform` 平台接口与平台无关的模型(PR、检查、合并状态、评审线程);`GhCli` 与 `GitHubPlatform`;`GhJsonContext` 源生成;Agent 工具;`PrWorkflowText`;`PrBodyRenderer`;`ScreenshotCapture`。 |
| `Forge.App` | `Git/` 新目录:`StepCommitter`(实现 `IStepCommitter`)、`GitPanelViewModel`/`GitPanelView`、`PrWatcher`(等待模式)、`SystemNotifier`、`GitSettingsSectionViewModel`;`Localization/Strings.Git.cs`。 |

### 进程与隔离

`GhCli` 与 `GitCli` 同一套:`ArgumentList`、按字节捕获、超时杀进程树、取消即杀。环境:`GH_PROMPT_DISABLED=1`、`GIT_TERMINAL_PROMPT=0`、`GH_NO_UPDATE_NOTIFIER=1`、`GH_PAGER=cat`、`NO_COLOR=1`,并清掉 `GitCli` 已清掉的那组仓库定位变量。网络命令(fetch/pull/push/gh)默认超时 2 分钟,可设置。`GhCli.GetAvailabilityAsync` 返回 `NotInstalled` / `NotAuthenticated` / `Ready`(`gh auth status`,带 host);不是 `Ready` 时 PR 工具返回明确错误,UI 显示“需要安装并登录 gh”,本地 git 功能不受影响。所有 JSON 走 `GhJsonContext`,不用反射。

失败分类(`GitRemoteFailure`):`AuthenticationFailed`、`NonFastForward`、`Conflict`、`RemoteNotFound`、`NoUpstream`、`Network`、`TimedOut`、`Other`;按 git/gh 的退出码与 stderr 关键字判定(`LC_ALL=C` 已保证英文输出),原始 stderr 原样附带。工具与 UI 都按分类给出下一步提示。

### ① 分支与远程

- `GitCli` 新增:`GetBranchStatusAsync`(当前分支、上游、ahead/behind,来自 `status --porcelain=v2 --branch`)、`CreateBranchAsync`、`SwitchBranchAsync`(工作树不干净时拒绝)、`SetUpstreamAsync`、`FetchAsync`、`PullAsync(策略)`、`PushAsync(setUpstream, forceWithLease 期望值)`、`RebaseAsync`/`MergeAsync`/`AbortAsync`/`ContinueAsync`、`GetConflictsAsync`、`LsRemoteAsync`。已有方法签名和语义都不变。
- 远程写操作执行前现读状态再核对:push 先 `ls-remote` 读远程分支当前 sha,作为 `--force-with-lease=<ref>:<sha>` 的期望值;从不使用缓存的结果。
- 删除分支沿用 `DeleteBranchAsync`;删除当前分支或某个 worktree 已检出的分支时拒绝。
- Agent 工具 `git_status`(只读:分支、上游、ahead/behind、改动、冲突)、`git_log`(只读,供判断提交风格)、`git_diff`(只读,`base...HEAD` 加工作树)、`git_fetch`(只读,只更新远端跟踪引用)、`git_branch`(Write)、`git_sync`(Write)。按副作用拆成两个工具名,而不是一个工具按参数判定:审批策略看到的是工具名固定的副作用。
- pull 策略只来自设置,`git_sync` 的参数不能覆盖——策略归用户。rebase/merge **不**先 fetch:`onto` 默认是当前分支的上游,没有上游时是 `origin/<默认分支>`,取的是最后一次 fetch 到的状态;要最新就必须先显式 fetch(理由:不要 rebase 在一个你刚刚没看过的东西上)。
- 期望失败都以 `GitRemoteResult` 返回,不抛异常,只有取消抛;失败种类由 `GitFailureClassifier` 按 git 的 stderr 判定(git 在 `LC_ALL=C` 下输出稳定的英文)。注意 git 2.56 对“落后推送”输出 `(fetch first)` 而不是经典的 `(non-fast-forward)`,两个都要认。
- 新建会话时分支名可编辑(默认 `forge/<slug>`,按 `git check-ref-format` 校验);会话标题栏显示分支名。

### ② 步骤提交与计划摘要

- 触发:TODO 条目从非 done 变成 done,不论 Agent 还是用户在 TODO panel 里勾选。会话不在 git 仓库里(常规 `scratch/`)、或设置里关掉时跳过。
- 范围:提交时工作树里**全部**未提交改动(`add -A` + `commit`)。会话 worktree 只属于这个会话,所以这些改动就是本步骤的。没有采用“按轮次检查点切分”:Agent 一个轮次里可能跨两个步骤改文件,按轮次切反而切错。检查点只用来标注:把这一步跨越的轮次范围记在条目上,供 Diff 按步骤查看。没有改动时不产生空提交,结果写“无改动”。
- 提交说明:优先用 Agent 给的 `commit_subject`,没有时退回条目标题。`todo` 工具描述要求 Agent 先用 `git_log` 读近 20 条提交,判断是否 Conventional Commits 再写。正文写入步骤的 summary;verification 不进提交说明。
- 提交失败(钩子失败、冲突中、游离 HEAD)不回滚 done 状态:工具结果与 TODO 行都显示失败原因,并提供“重试提交”。
- 审批:本地提交写的是会话自己的 worktree,而且可撤销,随 `todo` 一起放行,不单独弹审批。
- 计划摘要:会话级一段文本(Summary / Changes / Testing 三段);`PrBodyRenderer` 把它加上自动生成的 `## Plan`(每步:标题、提交短 sha、summary、verification)渲染成 PR 正文。纯函数,便于测试。

### ③ PR 创建 / 同步 / 改正文

- `pr_create`(Write)按顺序由代码检查五道闸门,任一不过就拒绝并说明原因:
  1. `git status --porcelain` 为空;
  2. 现 fetch 后,`origin/<base>` 是 HEAD 的祖先(否则让 Agent 先用 `git_sync` rebase);
  3+5. **当前 HEAD** 的 `git_diff` 已经被读过:工具按会话记下最近一次被读取的 HEAD sha,HEAD 之后有变化就必须重读。自审与“清理临时产物”都绑定到这次读取,代码能强制的只有这一点,清理是否干净仍靠 Agent 判断(诚实写进工具描述);
  4. 正文里 `## Testing` 段非空,且不是只有一句“tests pass”这类空话(最小长度加禁用词)。
  闸门通过后再现读一次状态,然后 `push -u`(带 lease)→ `gh pr create --body-file <临时文件>`,完成后删除临时文件;`--draft` 只在参数明确要求时加。
- `pr_sync`(Write):fetch,然后按 PR 已有的历史风格 rebase 或 merge(看分支里有没有 merge 提交)。有冲突时停下,返回冲突文件列表交给 Agent 解;Agent 需要读两侧 `git log -p`,工具描述里写明。解决后 `--continue`,push 用 `--force-with-lease`。冲突文件与结果在 Git panel 显示。
- `pr_edit`(Write):先读当前 title/body,执行前再读一次,有并发修改就把本次修改合并进最新正文;执行后再读一次核对是否生效,不一致就报错。
- `pr_view`(只读):PR 号、状态、链接、head/base。

### ④ 检查与等待模式

- `pr_checks`(只读):快照模式查一次 `gh pr checks --json` 与 `gh pr view --json mergeStateStatus,isDraft,reviewDecision`,如实报告;空结果报告为“未确定”,不当作没有 CI。
- `wait: true`:每 30 秒轮询,最长 30 分钟,随轮次取消;`UNKNOWN`、排队中一律继续等;只有观察到 `CLEAN` / `HAS_HOOKS`(后者附注意事项)且全部检查通过才算完成;`BEHIND`/`DIRTY`/`BLOCKED`/`UNSTABLE`/草稿按 checks.md 的语义返回给 Agent,不自己同步或修复。连续多次确认没有任何检查,才报告“仓库没有 CI”。
- `pr_ci_log`(只读):`gh run view --job <id> --log-failed`,输出过长时转存 Artifact(Core 已有)。
- App 侧 `PrWatcher`:Git panel 的“等待检查”按钮使用同样的轮询逻辑(两边共用 Workspace 里的 `PrChecksPoller`),只在后台线程运行,结果经快照回到 UI 线程;关闭会话或点取消即停止。
- 系统通知(`SystemNotifier`):等待结束(成功、失败、超时、被阻塞)时各发一条。App 不允许 P/Invoke,白名单也没有通知库,Avalonia 的 `TrayIcon` 又没有气泡通知 API,所以改为调用平台自带的命令行工具:Windows 用 `powershell` 调 WinRT `ToastNotificationManager`(发送方会显示为 Windows PowerShell,因为未打包的程序没有自己的 AppUserModelID),macOS 用 `osascript -e 'display notification …'`,Linux 用 `notify-send`。参数经 `ArgumentList` 传入并转义,超时 5 秒;工具不存在或执行失败时不报错,只记 Trace,状态栏和 Git panel 照常显示。设置分区里可以关掉。

### ⑤ 评审意见

- 读取:`gh api graphql` 拉取 `reviewThreads`(含 id、isResolved、路径、行号、全部评论,分页取完);同时 `gh pr diff` 读全量 diff。
- `pr_comments`:`list`(只读)、`reply`(Write,必须带 `verdict` = apply / push_back / clarify)、`resolve`(Write)。由代码强制:verdict 为 apply 的回复必须引用一个**已推送**的提交 sha(用 `ls-remote` 与 `merge-base --is-ancestor` 核对);resolve 只允许对已回复过、且 verdict 为 apply 或 push_back 的线程执行;clarify 不能 resolve。
- 本地评审意见(#10):`review_comments` 工具对 `ReviewStore` 做 list / resolve(只用它已有的 API,不改 `ReviewStore`)。本地意见和 PR 意见共用同一份“先判断再改”的指引(`pr_workflow` 的 review-comments 阶段)。
- UI:PR 评审线程显示在 Git panel 的“评审”分组里(见“越界”),附上每条线程的 verdict 与回复。

### ⑥ 截图(自动)

- 采集方式(用户已确认):**由项目提供截图命令**。仓库里的 `.forge/screenshots.json` 声明 `command`(把 PNG 写到 `output` 目录的命令,如 headless/e2e 测试)、`output`、`timeoutSeconds`(默认 300)。没有声明时,Agent 可以写一个一次性采集脚本,然后以参数形式把命令传给 `pr_screenshots`;两者都没有就明确报告“无法自动截图”,不猜启动方式。不做“Forge 启动程序并截取窗口”。
- 流程(`ScreenshotCapture`,工具 `pr_screenshots` 与 Git panel 按钮共用):以**会话分支上的**配置为准(base 上可能还没有配置),先在会话 worktree 里跑一次得到 after,再在 `origin/<base>` 的临时分离 worktree 里跑一次得到 before;按文件名配对(`<name>.png` → `<name>-before.png`/`<name>-after.png`),只有一侧有的图也保留,并标注另一侧缺失。命令经平台 shell 执行(Windows `cmd /c`,其余 `/bin/sh -c`),超时或取消就杀进程树;任一侧失败时把退出码与输出尾部返回,不附图。临时 worktree 放在系统临时目录,`finally` 里 `worktree remove --force` 并 prune。
- 审批:截图命令会执行仓库里的任意代码,`pr_screenshots` 的副作用是 **Execute**,推送与改正文部分另算 Write;用户在 Git panel 点按钮时按钮旁边会显示将要执行的命令。
- 附图:生成孤儿提交(`hash-object -w` → `mktree` → `commit-tree`)→ push 到 `refs/heads/assets/<分支 slug>` → 用 `pr_edit` 的“读-改-核对”流程更新正文里的 `## Screenshots` 段,链接固定到提交 sha。图片不进 PR 的文件树。这一步与采集方式无关,也支持用户手动选择本地 PNG。
- 临时产物(截图目录、before worktree、一次性采集脚本)在正文更新成功后由代码清理;如果采集脚本被提交过,删除它的提交也要推送。

### 设置分区 `git`

步骤提交开关、pull 策略、网络超时、等待间隔与上限、系统通知开关、`gh` 可执行路径。需要在 `AppPreferences` 里加字段(见“越界”)。

### 越界清单(动手前须你同意)

这些都是“追加注册”或最小改动,不改既有行为:

1. `src/Forge.Abstractions/Workbench.cs`:`TodoItem` 加字段、`ITodoStore` 加方法、新增 `IStepCommitter`。`TodoItem` 是位置记录,新字段放在末尾并给默认值,现有构造调用不受影响。
2. `src/Forge.Core/Storage/StorageSchema.cs` 与 `SqliteTodoStore.cs`:schema 升到 v2(只加列、加表,沿用版本阶梯的规则)。
3. `src/Forge.App/Preferences/AppPreferences.cs`:加 git 设置字段(与 terminal 字段同样的写法)。
4. `src/Forge.App/Agents/SessionRuntime.cs`:注册 git/PR 工具,把 `IStepCommitter` 传给 `AgentHost`,审批规则里 `git_status`/`git_log`/`git_diff`/`pr_view`/`pr_checks`/`pr_ci_log`/`pr_workflow` 按只读处理。还需要 `AgentHostOptions` 加一个 `StepCommitter` 属性(`Forge.Core/Agents/AgentHost.cs`)。
5. 工作台:在检视区工具组(TODO | 评审 | 规格树)里追加 Git 选项卡(`MainWindowViewModel.cs`/`WorkbenchLayout` 的默认选项卡列表);`MainWindow.axaml` 标题行追加分支名,状态栏追加 ahead/behind。
6. `src/Forge.App/ViewModels/Panels.cs` 的 `TodoListViewModel`/`TodoEntryViewModel`:显示提交 sha 与失败原因、“重试提交”,并在用户勾选完成时调用 `IStepCommitter`。
7. 新建会话流程(`ProjectTree.cs`、`WorktreeService.cs`):分支名可编辑。`WorktreeManager` 在 `Forge.Workspace/Worktrees/`,也需要接受调用方给的分支名。
8. `AppComposition.cs`、`App.axaml`:追加注册(这两处任务 spec 已允许)。

PR 评审线程不改 `ReviewPanelViewModel`,只显示在 Git panel 里,所以评审面板本身不在越界清单中。

### 交付与验证

- 按 ①→⑥ 分批。每批:主 Agent 先定契约与骨架并提交 → 实现 Worker 与测试 Agent 在各自副本里并行 → Review → 主 Agent 跑全量构建与测试、检查越界 → rebase 到最新 main 后提交。①③④⑤ 的 Workspace 部分彼此独立的,可以并行。
- 测试:`tests/Forge.Workspace.Tests/Git/**` 用本地裸仓库作远程(覆盖 fetch/pull 三种策略/push/lease 冲突/非快进/远程不存在/鉴权失败模拟/冲突与 abort/continue);`GhCli` 用一个假 `gh` 可执行文件(测试里生成的脚本或小程序,通过 `ExecutablePath` 注入)回放 JSON 与错误,不访问真实 GitHub;闸门、轮询(注入 `TimeProvider`)、渲染器都是确定性单测;Core 的 schema 迁移、`todo` 新字段与 done 时的提交钩子在 `Forge.Core.Tests`;App 的视图模型用 headless 测试。另在本机对真实 GitHub 测试仓库手动走一遍创建 → 检查 → 回复,结果写进最后的计划摘要,不进 CI。
- AOT:Workspace 保持 `IsAotCompatible`,分析器 0 警告;CI 已有的 AOT 发布作为门禁。

### 明确不做

GitLab 的实现(只留接口);合并 PR(`gh pr merge`)——对标清单没有,ThinkRail 也不替用户合并。

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
