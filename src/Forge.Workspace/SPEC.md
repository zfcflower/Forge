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
**评审意见存储。** 评审意见存应用数据目录的 SQLite,不入仓库;`MarkSentAsync` 幂等——对已发送评论重复标记不改写原始 `sent_at`。

## 关键决策

**Git 走系统 `git` 命令行。** 用 `ProcessStartInfo.ArgumentList`(不 shell 拼接);所有接受引用/范围的类型化接口拒绝以 `-` 开头的参数并加 `--end-of-options`,防止选项注入。底层 `RunAsync` 保持不受限。所有 git 子进程先移除继承的仓库定位/发现变量(`GIT_DIR`、`GIT_WORK_TREE`、`GIT_COMMON_DIR`、`GIT_INDEX_FILE`、`GIT_OBJECT_DIRECTORY`、`GIT_ALTERNATE_OBJECT_DIRECTORIES`、`GIT_NAMESPACE`、`GIT_PREFIX`、`GIT_CEILING_DIRECTORIES`、`GIT_DISCOVERY_ACROSS_FILESYSTEM`),再应用调用方显式覆盖,仓库发现只取决于工作目录;代价是用户外层设置的发现边界对 Forge 的 git 调用不生效。

**远程操作失败不抛、推送永不裸 `--force`。** 远程/分支操作的预期失败(非快进、远端不存在、落后/冲突、认证、超时)以结构化 `GitRemoteResult` 返回而非抛异常,仅取消抛 `OperationCanceledException`。推送只允许 `--force-with-lease`,lease 对着推送前刚读的 `ls-remote` tip;git 在“Everything up-to-date”时不校验 lease 直接退 0,故该短语触发 lease 的二次核对,空或过期的 lease 升级为非快进失败。merge/rebase/pull/continue 的冲突报告打印在 stdout 而非 stderr,分类器只扫 stderr,故这些操作失败时再查 `GetConflictStateAsync` 把结果升级为冲突。git 2.56 把落后推送的提示从 `(non-fast-forward)` 改为 `(fetch first)`,两者都识别。`GitReviewLedger` 记录代理最后一次完整读 diff 的 HEAD,后续批次的 PR 门禁以此判定发 PR 前是否审过自己的改动。

**捕获字节级且诚实。** 输出按原始字节流读取(不按行重拼),保留 CRLF 与末尾无换行;超过上限时结构化接口抛 `GitOutputTruncatedException`,不把截断数据当完整结果。统一 Diff 解析器校验 hunk 声明的行数与内容一致,损坏块标记而非猜测。

**路径身份与符号链接。** `WorkspacePaths.Normalize` 只做 `GetFullPath` 加去尾分隔符;`WorkspacePaths.Equals` 先比规范化串(Windows 不区分大小写、其余平台 Ordinal),不同时再解析两侧已存在组件的符号链接后比较。依据是 git 一律回报解析后的真实路径,而调用方可以经符号链接传入路径(macOS 的 `/tmp`、`/var` 都是链接,仓库在 `/var` 下时 git 报 `/private/var`)。因此 `AddWorktreeAsync`/`AttachWorktreeAsync` 在路径拼写不同时仍返回带分支、带 HEAD 的真实 worktree,而不是空分支的占位对象。测试夹具同样先把临时沙箱解析成真实路径,再与 git 输出逐字比较。

**Worktree 安全。** 每会话一个 worktree:分支默认 `forge/<slug>`,也可显式命名(显式名不加后缀、不重名复用;目录 slug 取名字最后一段,目录冲突才加 `-2` 起的后缀;显式分支已存在抛 `GitException`、非法名抛 `ArgumentException`,失败不创建任何分支或目录);非强制删除在有未提交改动、未合入提交、游离 HEAD 独有提交、或无法确定基线分支时拒绝。会话结束后 worktree 保留,由用户手动清理。`WorktreeOptions.Layout` 默认 `NestedByRepository`;App 使用 `SiblingOfRepository`(`<repoParent>/<repoName>.worktrees/<slug>`),兄弟目录不会被误提交,也不混用多个仓库的命名空间。

**Worktree 并发创建。** slug/分支/路径分配是检查后动作:进程内按规范化仓库根(OS 比较器)取引用计数门串行“列分支 → 挑后缀 → `worktree add -b`”,最后一个持有者/等待者释放时移除;跨进程冲突(`already exists`/`cannot lock ref` 或目标路径已存在)重读分支取下一后缀重试,上限 20,耗尽时抛带最后一次 git 失败详情的 `GitException`。从不删除已存在的分支、目录或 worktree。

**按轮次改动。** `SnapshotTreeAsync` 把本 worktree 真实索引复制到 `<系统临时目录>/forge-snapshot-<pid>-<guid>.index`,以 `GIT_INDEX_FILE` + `GIT_OPTIONAL_LOCKS=0` 执行 `add --all` + `write-tree`,`finally` 删除副本:用户索引、HEAD、分支、refs、stash 不变,唯一副作用是未引用对象(gc 回收);同内容同哈希。文件名带进程号,因为 `dotnet test` 会并行跑多个测试程序集并共用同一个系统临时目录,按进程归属才能断言本进程没有残留,而不会把别的进程(例如 App 测试里 `TurnCheckpointService` 的在途快照)算成泄漏。本仓约 70 ms,一万文件约 80–120 ms。轮次改动 = 相邻两棵树的 diff(`GetTreeDiffAsync`/`GetTreeDiffTextAsync`/`GetTreeChangesAsync`)。

**规格写入原子。** `SpecGraph` 更新 frontmatter 时把整份 markdown(UTF-8 无 BOM、沿用文件原有行尾)交给 `AtomicFile.WriteAllBytesAsync`:同目录临时文件 + `fsync` + 改名覆盖,读者不会看到半文件;目标是 `ReadOnly` 文件或目录时立即失败且原文件逐字节不变,临时文件被删除。新建规格仍是 `FileMode.CreateNew` 的独占创建(已存在即拒绝)。

**规格 frontmatter 往返无损。** 列表解析与序列化互逆(引号、逗号、转义);解析器不支持的语法保留 `RawText` 原样回写,不静默重写。

**AOT 兼容。** 工具 JSON 输出走反射无关的 `JsonPayload`;持久化的 `DiffAnchor` 走 `WorkspaceJsonContext` 源生成。五个主程序项目 `IsAotCompatible=true`,分析器 0 警告。

## 未决

- 索引刷新默认信任长度+mtime;外部工具保留时间戳的同长度修改需 `force` 刷新才可见(已提供强制刷新路径)。
