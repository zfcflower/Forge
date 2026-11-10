---
id: forge-workspace
type: module-design
status: draft
title: Forge.Workspace
parent: forge-architecture
---

# Forge.Workspace

Git 与 Worktree、统一 Diff、规格树(spec-graph)、评审意见存储。只依赖 `Forge.Abstractions`、`Forge.IO`(规格写入的原子替换)与 BCL + Microsoft.Data.Sqlite;不含 UI。

## 边界

- 所有文件写入(规格、评审)被限制在所属工作区内;创建/更新/删除前解析符号链接与 junction 的真实目标,越界即拒绝。
- 经 `Forge.Abstractions` 暴露 7 个规格工具:`spec_grep`/`spec_get`/`spec_graph`/`spec_validate`(只读)、`spec_create`/`spec_update`/`spec_delete`(写)。
- 另有 6 个 git 工具(实现于 `Git/`,由 App 组合根构造、经 `AgentRuntimeServices.Git` 注入,不走 `Forge.Abstractions` 契约):`git_status`/`git_log`/`git_diff`/`git_fetch` 只读,`git_branch`/`git_sync` 写;副作用按工具名固定划分(`git_fetch` 只读、`git_sync` 含写),拉取策略与网络超时仅来自用户设置,不接受工具参数。
- PR 工具与 git 工具同源(同样实现于 `Git/`、经 `AgentRuntimeServices.PullRequestTools` 注入):`pr_view`/`pr_workflow`/`pr_checks`/`pr_ci_log` 只读,`pr_create`/`pr_sync`/`pr_edit`/`pr_comment_reply`/`pr_comment_resolve`/`review_comment_resolve` 写,`pr_screenshots` 执行(`ToolSideEffect.Execute`)。工具与 UI 只依赖 `IPrPlatform` 接口;`GitHubPlatform`(基于 `GhCli`)是唯一实现,只做 GitHub。
**评审意见存储。** 评审意见存应用数据目录的 SQLite,不入仓库;`MarkSentAsync` 幂等——对已发送评论重复标记不改写原始 `sent_at`。

## 关键决策

**Git 走系统 `git` 命令行。** 用 `ProcessStartInfo.ArgumentList`(不 shell 拼接);所有接受引用/范围的类型化接口拒绝以 `-` 开头的参数并加 `--end-of-options`,防止选项注入。底层 `RunAsync` 保持不受限。所有 git 子进程先移除继承的仓库定位/发现变量(`GIT_DIR`、`GIT_WORK_TREE`、`GIT_COMMON_DIR`、`GIT_INDEX_FILE`、`GIT_OBJECT_DIRECTORY`、`GIT_ALTERNATE_OBJECT_DIRECTORIES`、`GIT_NAMESPACE`、`GIT_PREFIX`、`GIT_CEILING_DIRECTORIES`、`GIT_DISCOVERY_ACROSS_FILESYSTEM`),再应用调用方显式覆盖,仓库发现只取决于工作目录;代价是用户外层设置的发现边界对 Forge 的 git 调用不生效。

**分支列表保留引用身份。** `GitBranch.Name` 是给界面显示的短名称，`RefName` 保留 `refs/heads/...` 或 `refs/remotes/...` 完整引用，`IsRemote` 标识远端跟踪分支；调用方不得按名称中的 `/` 猜测分支类型，比较时使用完整引用以区分同名本地/远端分支。

**远程操作失败不抛、推送永不裸 `--force`。** 远程/分支操作的预期失败(非快进、远端不存在、落后/冲突、认证、超时)以结构化 `GitRemoteResult` 返回而非抛异常,仅取消抛 `OperationCanceledException`。推送只允许 `--force-with-lease`,lease 对着推送前刚读的 `ls-remote` tip;git 在“Everything up-to-date”时不校验 lease 直接退 0,故该短语触发 lease 的二次核对,空或过期的 lease 升级为非快进失败。merge/rebase/pull/continue 的冲突报告打印在 stdout 而非 stderr,分类器只扫 stderr,故这些操作失败时再查 `GetConflictStateAsync` 把结果升级为冲突。git 2.56 把落后推送的提示从 `(non-fast-forward)` 改为 `(fetch first)`,两者都识别。`GitReviewLedger` 记录代理最后一次完整读 diff 的 HEAD,后续批次的 PR 门禁以此判定发 PR 前是否审过自己的改动。

**捕获字节级且诚实。** 输出按原始字节流读取(不按行重拼),保留 CRLF 与末尾无换行;超过上限时结构化接口抛 `GitOutputTruncatedException`,不把截断数据当完整结果。统一 Diff 解析器校验 hunk 声明的行数与内容一致,损坏块标记而非猜测。stdout 与 stderr 截断方向不同:stdout 保留**前缀**(diff 的开头最有用),stderr 保留**末尾**——诊断的决定性一行最后打印,超 64 KiB 上限也不丢(hook 的最终裁决);逐行回调 `OnStandardOutputLine`/`OnStandardErrorLine` 在两种方向下都照常逐行触发。

**路径身份与符号链接。** `WorkspacePaths.Normalize` 只做 `GetFullPath` 加去尾分隔符;`WorkspacePaths.Equals` 先比规范化串(Windows 不区分大小写、其余平台 Ordinal),不同时再解析两侧已存在组件的符号链接后比较。依据是 git 一律回报解析后的真实路径,而调用方可以经符号链接传入路径(macOS 的 `/tmp`、`/var` 都是链接,仓库在 `/var` 下时 git 报 `/private/var`)。因此 `AddWorktreeAsync`/`AttachWorktreeAsync` 在路径拼写不同时仍返回带分支、带 HEAD 的真实 worktree,而不是空分支的占位对象。测试夹具同样先把临时沙箱解析成真实路径,再与 git 输出逐字比较。

**Worktree 安全。** 每会话一个 worktree:分支默认 `forge/<slug>`,也可显式命名(显式名不加后缀、不重名复用;目录 slug 取名字最后一段,目录冲突才加 `-2` 起的后缀;显式分支已存在抛 `GitException`、非法名抛 `ArgumentException`,失败不创建任何分支或目录);非强制删除在有未提交改动、未合入提交、游离 HEAD 独有提交、或无法确定基线分支时拒绝。会话结束后 worktree 保留,由用户手动清理。`WorktreeOptions.Layout` 默认 `NestedByRepository`;App 使用 `SiblingOfRepository`(`<repoParent>/<repoName>.worktrees/<slug>`),兄弟目录不会被误提交,也不混用多个仓库的命名空间。

**起点可选,新分支永不带上游。** `CreateAsync` 除默认从 HEAD 分叉外,还有带 `startPoint` 的重载:起点可以是本地分支、远程跟踪引用(如 `origin/main`)或任意 revision,先解析成它的提交再交给 `git worktree add -b`,因此从 `origin/main` 分叉也不会让新分支跟踪 `origin/main`(否则推送会指向主线);起点解析不出来抛 `GitException`。

**检出状态可读且不抛。** `GetHeadAsync` 回答“这个目录检出在哪”:`Branch`(有提交)、`UnbornBranch`(刚 `git init`、HEAD 指向的名字)、`Detached`、`NotRepository`。裸仓库按 `NotRepository` 回报——它没有工作树,把它当成 checkout 会让会话把文件写进仓库自己的数据目录。这条读取是给界面标签用的,所以除取消除外一律返回枚举而不抛异常;实现上先问 `rev-parse --show-toplevel`(一次调用同时排除非仓库与裸仓库),再 `symbolic-ref`/`rev-parse HEAD`。

**Worktree 并发创建。** slug/分支/路径分配是检查后动作:进程内按规范化仓库根(OS 比较器)取引用计数门串行“列分支 → 挑后缀 → `worktree add -b`”,最后一个持有者/等待者释放时移除;跨进程冲突(`already exists`/`cannot lock ref` 或目标路径已存在)重读分支取下一后缀重试,上限 20,耗尽时抛带最后一次 git 失败详情的 `GitException`。从不删除已存在的分支、目录或 worktree。

**按轮次改动。** `SnapshotTreeAsync` 把本 worktree 真实索引复制到 `<系统临时目录>/forge-snapshot-<pid>-<guid>.index`,以 `GIT_INDEX_FILE` + `GIT_OPTIONAL_LOCKS=0` 执行 `add --all` + `write-tree`,`finally` 删除副本:用户索引、HEAD、分支、refs、stash 不变,唯一副作用是未引用对象(gc 回收);同内容同哈希。文件名带进程号,因为 `dotnet test` 会并行跑多个测试程序集并共用同一个系统临时目录,按进程归属才能断言本进程没有残留,而不会把别的进程(例如 App 测试里 `TurnCheckpointService` 的在途快照)算成泄漏。本仓约 70 ms,一万文件约 80–120 ms。轮次改动 = 相邻两棵树的 diff(`GetTreeDiffAsync`/`GetTreeDiffTextAsync`/`GetTreeChangesAsync`)。

**规格写入原子。** `SpecGraph` 更新 frontmatter 时把整份 markdown(UTF-8 无 BOM、沿用文件原有行尾)交给 `AtomicFile.WriteAllBytesAsync`:同目录临时文件 + `fsync` + 改名覆盖,读者不会看到半文件;目标是 `ReadOnly` 文件或目录时立即失败且原文件逐字节不变,临时文件被删除。新建规格仍是 `FileMode.CreateNew` 的独占创建(已存在即拒绝)。

**规格 frontmatter 往返无损。** 列表解析与序列化互逆(引号、逗号、转义);解析器不支持的语法保留 `RawText` 原样回写,不静默重写。

**AOT 兼容。** 工具 JSON 输出走反射无关的 `JsonPayload`;持久化的 `DiffAnchor` 走 `WorkspaceJsonContext` 源生成。五个主程序项目 `IsAotCompatible=true`,分析器 0 警告。

**PR 平台:经 `gh` CLI、接口隔离。** 工具与 UI 只依赖 `IPrPlatform`,`GitHubPlatform` 是唯一实现,基于 `GhCli`(与 `GitCli` 同一套 `ArgumentList`/字节捕获/超时杀树纪律;环境 `GH_PROMPT_DISABLED=1`、`GH_NO_UPDATE_NOTIFIER=1`、`GH_PAGER=cat`、`NO_COLOR=1` 并清掉仓库定位变量;JSON 走 `GhJsonContext` 源生成,不反射)。`GetAvailabilityAsync` 返回 `NotInstalled`/`NotAuthenticated`/`Ready`(`gh auth status`);不是 `Ready` 时 PR 工具报明确错误,本地 git 不受影响。

**`pr_create` 五道闸门由代码强制,推送钉死被审阅的提交。** 闸门按序:(1) 工作树干净(未跟踪计入);(2) 现 fetch 后 `origin/<base>` 是 HEAD 祖先;(3) 本会话对**这个确切 HEAD** 读过完整 `git_diff`(`GitReviewLedger` 记下真正 diff 的那个 HEAD;代码只能强制“重读”,清理是否彻底是 Agent 的判断,工具描述诚实写明);(4) 正文 `## Testing` 非空且非空话(最小长度 + 禁用词);(5) 与 3 同源。闸门通过后重读 HEAD 与工作树,再把推送钉死到被审阅的提交:`GitPushOptions.SourceCommit` 把 refspec 写成 `<sha>:refs/heads/<branch>` 并在 push 前再核一次 HEAD,闸门之后落到 HEAD 的新提交不可能被发布——即 gate-to-push 竞态被拒绝而非静默发布;`-u` 对 sha refspec 不生效,由 `WriteUpstreamAsync` 成功后补写 `branch.<n>.remote`/`branch.<n>.merge`。`pr_sync` 拒绝没有 PR 的分支,按分支既有历史选 rebase 或 merge,冲突停下交 Agent 解,成功后带 lease 推送。`pr_edit` 读-改-核对。`pr_view`/`pr_workflow` 只读。

**评审意见:先判断再改,verdict 由代码门禁。** 评审线程经 GraphQL 分页拉全(`reviewThreads` 与每条线程的 `comments` 都翻页,上限 100/50 页;超限或 cursor 空/不前进时拒绝返回残缺列表)。`pr_comment_reply` 必须带 verdict(`apply`/`push_back`/`clarify`);`apply` 引用的提交按 **PR 实际 head**(平台返回的 `headRefOid`)核对(`merge-base --is-ancestor`),不再按同名分支 ref;发回复超时后回读线程比对,不重复发。`pr_comment_resolve` 只对已回复且 verdict 为 apply/push_back 的线程生效,且 verdict 之后**任何**新评论(含同一 gh 登录)都阻塞 resolve;clarify 不能 resolve。本地 `review_comments`/`review_comment_resolve` 与 PR 意见共用同一“先判断再改”指引,草稿(未 Sent)意见不可 resolve。

**自动截图:项目命令 + before/after + 孤儿提交。** 截图命令由仓库 `.forge/screenshots.json` 声明(`command`/`output`/`timeoutSeconds`),没有声明时可传一次性命令。`pr_screenshots`(副作用 Execute)在会话 worktree 跑 after、在 `origin/<base>` 的临时分离 worktree 跑 before,按文件名配对,仅一侧有的图保留并标注;任一侧失败返回退出码 + 输出尾部,不附图;临时 worktree 在 `finally` 移除。图片经孤儿提交(`hash-object -w`/`mktree`/`commit-tree`)push 到 `refs/heads/assets/<分支 slug>`,再用 `pr_edit` 的读-改-核对更新正文 `## Screenshots` 段,图片不进 PR 文件树;采集脚本若被提交过,删除它的提交也一并推送。`ScreenshotCapture`/`ScreenshotRunner` 与 App 的 Git panel 按钮共用同一编排。

**PR 正文的计划按分组渲染。** `PrBodyRenderer.Render(planSummary, steps, groups)` 是纯函数:用户(Agent)写的 `## Summary/Changes/Testing` 正文照原样保留,其后追一个生成的 `## Plan`。计划按分组组织:每个**有步骤**的分组一个小节 `### <分组标题>`(组内步骤按 `TodoItem.Order` 从 1 编号),不属于任何分组(或 `GroupId` 指向已不存在的分组)的散项收进末尾的 `### Other steps`(重新从 1 编号,无散项时不出现);**空分组不出小节**——刚 promote 出来的分组本来就可能一条步骤都没有,裸 `###` 是噪音。每步仍展示标题、7 位提交 sha、summary 与 `Verification:`;未完成的状态标记为 `_(pending)_`/`_(in progress)_`/`_(paused)_`,done 不带标记。分组标题与步骤标题走 `OneLine` 压平换行(正文是 markdown,不许注入标题层级);标题与 id 都空时用 `Group` 兜底。`PrCreateTool` 因此要两读:`ListAsync` 与 `ListGroupsAsync`(不合并成新接口,契约不动);没分组支持的存储(`ListGroupsAsync` 默认空表)退化成全部步骤进 `### Other steps`。

## 未决

- 索引刷新默认信任长度+mtime;外部工具保留时间戳的同长度修改需 `force` 刷新才可见(已提供强制刷新路径)。
