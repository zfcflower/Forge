# 04 Git、Diff 与 PR

本文件说明 FORGE 的 Git 集成、改动浏览与审阅、步骤提交，以及完整的 PR 生命周期。

## 4.1 Worktree 与分支

- 每会话一个 worktree；App 使用 `SiblingOfRepository` 布局（`<repoParent>/<repoName>.worktrees/<slug>`），兄弟目录不会被误提交。
- 创建：分支默认 `forge/<slug>`，也可显式命名（显式名不加后缀、不重名复用；目录 slug 取名字最后一段，冲突才加 `-2` 起的后缀）；显式分支已存在抛 `GitException`，非法名抛 `ArgumentException`，失败不创建任何分支或目录。
- **起点可选，新分支永不带上游**：起点可以是本地分支、远程跟踪引用或任意 revision，先解析成提交再交给 `git worktree add -b`，所以从 `origin/main` 分叉也不会让新分支跟踪它（`↑a ↓b` 要用户自己 `push -u`）。
- 并发创建：进程内按规范化仓库根串行“列分支 → 挑后缀 → add -b”，跨进程冲突重读分支取下一后缀重试（上限 20），从不删除已存在的分支、目录或 worktree。
- 检出状态读取不抛：`Branch` / `UnbornBranch` / `Detached` / `NotRepository`（裸仓库按 NotRepository，避免把文件写进仓库数据目录）。
- 分支列表保留引用身份：`Name` 是显示名，`RefName` 是完整 `refs/heads/...` 或 `refs/remotes/...`，调用方不得按名字里的 `/` 猜类型。
- 改名分支：菜单只在非默认分支、非游离、无合并/变基进行中时显示；有上游先警告；`git branch -m` + 一个事务改会话 `branch` 与 `fork_of_branch`，库写失败时把分支改回。
- 删除守卫：非强制删除在未提交改动、未合入提交、游离 HEAD 独有提交或无法确定基线分支时拒绝；移除会话走 App 的回收站流程（见 01），只有失败创建的回滚会删本次刚建的分支（`branch -d`，从不 `-D`）。

## 4.2 改动浏览

文件面板有两个标签：**全部**（文件索引的文件数）与**已改动**（当前范围内的改动文件数），各有数量徽标，0 也照常画 0，无会话时都是 0；数字同时进 `AutomationProperties.Name`。

- **范围**（自绘下拉）：“全部改动”（当前分支自所选基线共同祖先以来的已提交差异）、“未提交改动”（HEAD 到工作树，含未跟踪；干净时灰显）、“上一轮 · N 个文件”快捷项、“Agent 轮次”组、“Commit”组。
- **基线**（自绘下拉）：本地/远端跟踪分支分组（远程按远程名分组、组名不可选，`origin/HEAD` 这类不列出），默认仓库默认分支，支持搜索过滤（包含、不分大小写）；只影响“全部改动”与 commit 列表。
- **已改动**显示为目录树：单子目录链压缩、目录在前、行尾 `+x −y`、新增文件名绿色、二进制只显示状态字母（绝不显示 `+0 -0`）；标题栏刷新在“已改动”时换成全部折叠/展开。
- 游离 HEAD、无共同历史或无可比较基线时显示明确状态，不伪装成“无改动”。
- 徽标与 watcher：700 ms 一跳，补丁按“状态 + 每文件大小/修改时间”指纹缓存，无变化不重算；路径按会话目录裁剪，`scratch/…` 下也有计数。

## 4.3 Diff 查看器

- 整个视图一个只读 `TextEditor`（拆分时左右两个、行数对齐、垂直滚动同步），默认只读 3 行上下文摘要；改动之间是“N unmodified lines”折叠条。
- 点开某段才按同一组 ref 把**该文件**以全上下文重读一次（`GitCli.FullContextLines`，pathspec `:(top)<路径>`，重命名带旧路径）；指纹与块数一致才换入（保留标记不丢），否则等 watcher 重载。
- 文件头：折叠箭头、状态字母、路径（中间省略）、`+x −y`、`已保留 k/K`、撤销整文件、在编辑器中打开、⋯（复制路径）、“已查看”勾选；勾选已查看即折叠该文件。
- 改动块 = 连续非上下文行；块上方“✓ 保留 ↶”：
  - **保留**只标记接受、不改文件；
  - **撤销**把该块还原为基线侧（`ChangeBlockReverter`：保留每行换行与 BOM/末尾换行；块不在预期位置时在整个文件里找新侧行、用前后最多 3 行上下文锚定，唯一匹配才撤销，多处或找不到则报“文件已变化”且不写）；
  - 整文件撤销先确认，Modified 一次原子写回全部块、Added 删除、Deleted 重建；重命名/二进制/截断禁用；
  - 撤销只在“未提交改动”“全部改动”范围提供，按轮次与按 commit 是历史，按钮禁用。
- **「已查看」「已保留」按会话存进 `reviews.db` 的 `review_marks`**：已查看按文件 diff 指纹、已保留按块内容哈希（内容完全相同的块按出现序号区分），内容一变自然失效；标记写入串行，读取期间又有切换则丢弃该次结果重读。
- `.md` / `.markdown` / `.mdx` 文件头多“预览 | Source”，默认预览：自写块级解析（不引 Markdig），改动块绿色左条、删除块红底插回原位，连续未改动块折叠成“N 个未改动块 § 最近标题”；预览里没有逐行评语，切回 Source 评。
- 图片变更并排显示旧/新（W17a），LFS 指针与解码失败回落文本 diff。
- 拆分/内联、堆叠/单文件、自动换行、上/下一个文件、底栏“已评审 a/N”进度条、“下一个未评审的 J”、“全部标记为已查看”；范围/分支选择与显示模式不写入布局快照。
- 预览组“铺满主区”按钮让它盖住整个主区，方便专注审阅。

## 4.4 文件面板顶部的两行 Git 头

面板最上面两行都是 git，先于 全部/已改动 标签（无仓库时两行一起隐藏）。

### 第 0 行：本地同步（`GitSyncViewModel`）

- 左到右：分支名（12px mono SemiBold，tooltip 写 `分支 · 推送到同名远端分支 · 上游 · HEAD`）、三个图标按钮 `git-fetch` / `git-pull` / `git-push`、“详情”链接、结果文字（上限 150 px）。
- 数字直接跟在会改状态的两个动词后面：`↓n` 只画在拉取上（警告色）、`↑n` 只画在推送上（强调色），0 不画、超 99 显示 `99+`，抓取不带数字（只读刷新）。
- `CanPull` 只要有仍存在的上游即成立，`CanPush` 只要有远程且不是分离 HEAD 即成立——ahead/behind 只反映上次抓取，拿它禁用会让“把别人的提交拉进来”不可达；无上游时推送可用（首次即 `push -u`）。
- 三个按钮都不带 force/lease；失败给按 `GitRemoteFailureKind` 选出的恢复提示。
- rebase/merge 进行中时树上方出现 `Border.git-banner`（唯一使用危险色竖条的地方）：操作名、冲突文件（可点、打开到编辑器，同样文件在“已改动”里以 `U` 出现）、继续/中止。
- 随活动会话刷新（attach、每回合结束、每次操作后；不自动 fetch）。

### 第 1 行：出货（`GitShipViewModel`，28 px）

- 左侧远程读数是文本不是按钮：`没有远程` / `未发布` / `{n} 个提交待推送` / `与 origin 同步` / `落后 origin {n} 个提交`；动词在上一行，这格不会因误点而推送。
- `发布` 药丸 + `提交目标 ▾` 裂分按钮，按状态列出：打开 PR（对话框填标题/正文/草稿 → `IPrPlatform.CreatePullRequestAsync`）、在本地合并到默认分支、合并 PR（`gh pr merge --squash|--merge|--rebase [-d]`，策略默认压缩、合并后删远端分支默认关）、标记可评审（`gh pr ready`，仅草稿可用，不必确认；已可评审时按成功处理）、PR 检查…、在浏览器打开、删除远程分支（仅 PR 已合并或已关闭时可用，必确认）。
- **打开 PR 的正文预填**为 `PrBodyRenderer.Render(计划摘要, 步骤, 分组)` 的结果——与 agent 的 `pr_create` 默认正文同一个来源，不需要再调一次模型；用户改完才创建。
- gh 缺失/未登录时菜单有“在浏览器打开 compare 页面”（只对 github.com 远端生成，按解析出的 host 精确判定），可用条件是“远端在 GitHub、读得到默认分支、平台没有报出该分支的 PR 状态”。
- **合并队列**：`gh pr merge` 成功返回但 PR 仍是 OPEN 时，行里报“已加入合并队列 PR #n”，不谎报“已合并”。
- **推进共享分支前必须确认**：推送目标就是默认分支（base）且确实有东西要推时，先弹“推送到 {base}？”；推功能分支不问，“everything up to date”的空推送不问。
- 合并后自动收尾：根目录 `fetch --prune`，再当根检出正停在 PR 自己的 base 分支且工作树干净时**仅快进**拉动它（不经确认；base 上有本地提交时拒绝而不是 rebase/merge）。
- 某个出货动作失败只写在这一行（错误底 + 文字转红 + stderr 的“详情”），不发系统通知。
- 五个写动作（本地合并、合并 PR + 收尾、打开 PR、标记可评审、删除远程分支）走同一条写纪律：工作区标 writing、busy 置位、第二次点击 no-op、刷新排队、结束时释放并补跑排队中的刷新；只有成功才重读。

## 4.5 步骤提交

- TODO 条目从非 done 变 done 时（只有 Agent 能改状态），把会话 worktree 里全部未提交改动提交到会话分支；开关 `gitStepCommits`（默认开）。
- 范围 `add -A` + `commit`（会话 worktree 专属）；提交说明优先 `commit_subject`、否则条目标题，正文是 summary，verification 不进提交说明。
- 失败（钩子拒绝、冲突中、游离 HEAD）不回滚 done：工具结果与 TODO 行显示失败原因并提供“重试提交”；失败后把 index 复位到 HEAD（mixed reset，不动工作区），被拒改动回到未暂存。
- 晚取消（提交对象与 ref 已存在、只差慢 `post-commit` 钩子）时重读 HEAD，前进了就把新 sha 记到该步骤并报已提交。
- 同一 worktree 的提交按路径门串行，不同 worktree 并行。
- 提交成功后若开了“步骤提交后自动评审”且评审方式是 subagent，把带 `step_id` 的 `request_review` 请求交给该会话（不是第二步提交，用户不会看到第二个回合抢占）。

## 4.6 PR 工具（13 个）

| 工具 | 副作用 | 说明 |
|---|---|---|
| `pr_view` | 只读 | PR 概览、分支、状态、评审、检查摘要 |
| `pr_workflow` | 只读 | 六段 `pr.workflow.<阶段>` 文本（槽位可覆盖） |
| `pr_checks` | 只读 | 一次读取检查并给判决（与等待模式同一规则） |
| `pr_ci_log` | 只读 | 失败作业日志尾部 |
| `pr_comments` | 只读 | PR 评审线程（GraphQL 分页，上限 100/50 页） |
| `review_comments` | 只读 | 本地行评审草稿与评论 |
| `pr_create` | 写 | 五道闸门；默认正文来自 `PrBodyRenderer` |
| `pr_sync` | 写 | 拒绝没有 PR 的分支；按既有历史选 rebase 或 merge；冲突停下交 Agent；成功后带 lease 推送 |
| `pr_edit` | 写 | 读-改-核对 |
| `pr_comment_reply` | 写 | 必须带 verdict（`apply`/`push_back`/`clarify`）；`apply` 引用的提交按 PR 实际 head 核对 |
| `pr_comment_resolve` | 写 | 只对已回复且 verdict 为 apply/push_back 的线程生效；verdict 之后任何新评论都阻塞 |
| `review_comment_resolve` | 写 | 本地评论；草稿（未 Sent）不可 resolve |
| `pr_screenshots` | 执行 | 在会话 worktree 跑 after、在 `origin/<base>` 临时分离 worktree 跑 before，按文件名配对；图片经孤儿提交推到 `refs/heads/assets/<分支 slug>` 并更新正文 `## Screenshots` 段；返回文本报告 + 已发布图片块 |

- 没有工具会**合并 PR** 或**标记可评审**：这两个只有 UI 在用户点击后调用；接口默认实现直接拒绝，平台不支持时调用方拿到明确答复。
- PR 工具与 git 工具同源（`Git/`，经 `AgentRuntimeServices.PullRequestTools` 注入），只依赖 `IPrPlatform`；`GitHubPlatform`（基于 `GhCli`）是唯一实现，只支持 GitHub；`gh` 不是 Ready 时 PR 工具报明确错误，本地 git 不受影响。
- 截图命令由仓库 `.forge/screenshots.json` 声明（`command`/`output`/`timeoutSeconds`），也可传一次性命令；任一侧失败返回退出码 + 输出尾部，不附图；临时 worktree 在 `finally` 移除。

## 4.7 `pr_create` 的五道闸门

按序执行，由代码强制：

1. 工作树干净（未跟踪计入）。
2. 现 fetch 后 `origin/<base>` 是 HEAD 祖先。
3. 本会话对**这个确切 HEAD** 读过完整 `git_diff`（`GitReviewLedger` 记下真正 diff 的那个 HEAD；代码只能强制“重读”，清理是否彻底是 Agent 的判断）。
4. 正文 `## Testing` 非空且非空话（最小长度 + 禁用词）。
5. 本会话**计划的分组步骤全部 done**（只算 `GroupId` 非空的条目；待排散项不拦；拒绝时列出未完成步骤的 id 与标题，最多 10 条，超出写 `... and N more`，标题压平换行并截到 120 字符）。第五道由 `preferences.workflowGatesEnabled`（默认开）控制，前四道不受影响。

闸门通过后重读 HEAD 与工作树，再把推送钉死到被审阅的提交：refspec 写成 `<sha>:refs/heads/<branch>`，push 前再核一次 HEAD，所以闸门之后落到 HEAD 的新提交不可能被发布（gate-to-push 竞态被拒绝而非静默发布）；`-u` 对 sha refspec 不生效，成功后补写 `branch.<n>.remote` / `branch.<n>.merge`。

## 4.8 PR 检查面板

- 覆盖层从“提交目标 ▾ → PR 检查…”打开，读当前会话分支的 PR：标题行“PR 检查 #N” + 关闭；判决行（通过/失败/等待/behind/dirty/blocked/draft/no CI/已关闭）+ 各桶计数；读失败时说明原因并**保留上一次读到的检查**（网络失败不等于检查变了）。
- 检查列表按桶排序（失败 → 取消 → 运行中 → 跳过 → 通过），每行显示桶、名称、工作流、平台原始 state、描述（最多两行）、耗时，以及“详情”（打开平台页面）与“失败日志”（`gh run view --job --log-failed` 的尾部，行数来自 `GitChecksFailureLogTailLines`，默认 200）两个按钮。
- 底部“在浏览器打开”“让 agent 修”“标记可评审”（仅草稿）、“刷新”与最近一次动作结果与读取时间。
- 轮询：间隔取 `git` 分区的轮询设置（默认 30 秒，10–300），判决出现即停；等待超过“最长等待”（默认 30 分钟，1–180）也停下并提示刷新；连续失败达上限或 gh 缺失时同样停下；面板关闭、切换会话或再次打开时取消轮询；每次轮询带世代号，旧结果丢弃。
- “让 agent 修”：把失败检查 digest（判决 + 每条检查的 run/job id）+ 每个失败作业日志尾（每份 ≤4000 字符、合计 ≤20000，超出点名省略）+ 一句“改因、提交、推到当前分支上游；不要推 base、不要合并、不要无改动重跑”拼成英文消息，交给当前会话：空闲立即跑成一轮（转写带「PR 检查」来源行），忙时接在草稿之后（空一行），从不中断正在跑的回合，也不排队第二次运行。
- 检查的判决、排序与文本只有一份（`PrChecksPoller.Verdict` / `PrCheckText`）：等待模式、工具返回文本、面板行顺序、交给 agent 的修复消息共用同一套判决词汇；“没有 CI”只由连续多次成功空读达到“无检查确认次数”（默认 3，1–10）得出。

## 4.9 PR 评论面板

- 列出 PR 评审线程与本地行评审，草稿可批量发送或单条发送；`verdict` 之后任何新评论都阻塞 resolve。
- 本地评论与 PR 意见共用“先判断再改”的指引；草稿（未 Sent）不可 resolve。
- 发送目标固定为文件所属会话，忙时沿既有队列排队；消息与评论按 `ReviewStore` 串行化（线程、草稿、标记、结论都在 `reviews.db`）。

## 4.10 行评审（Diff 与文件）

- 选中文本或点击行号后出现评论图标（挂在选中终点或行首的右下角，放不下翻到左下，始终夹在可视区内）；点击展开浮动评论框（行范围 + 保存草稿 / 立即发送 / 取消）。
- 浮层不推开后续代码、不向源文档插入占位文本，自身输入与复制不被编辑器截获。
- 草稿复用 `ReviewStore` 持久化；单条发送只包含该评论，批量发送把当前文件草稿合成一次消息；队列接受成功后才标记已发送。
- 发送前保存并验证锚点与内容仍一致；草稿可基于未保存代码，但发送前必须保存成功；锚点随编辑迁移，无法重定位时明确显示失效，不把错误行号当有效评审。
- diff 里的选区按行映射到（文件, 新侧行号），跨文件或只落在删除/占位行上不出评论图标；拆分模式只在右侧（新）编辑器评；锚点用该文件的真实 `FileDiff`，发送不保存文件；草稿按新文件坐标存，被折叠时显示在折叠条上、文件折叠时显示在文件头上。
- 已交给运行时的消息不恢复；应用退出时异步恢复可能尚未完成（边界见 14）。

## 4.11 克隆与项目创建

- `git clone --progress -- <url> <destination>`：URL 去首尾空白后不得以 `-` 开头；目标必须是绝对路径、不存在或空目录，父目录不存在则创建。
- 进度从 stderr 解析：阶段 `Connecting/Enumerating/Counting/Compressing/Receiving/Resolving/Updating`，`Percent` 是**该阶段自身**百分比（不合成假总进度），`Detail` 截 120 字符；`git clone --progress` 用 `\r` 原地重写，所以逐行回调也按裸 CR 断行。
- 失败/超时/取消删除**本次调用创建**的目标目录（原本存在的空目录保留但清空），中断的克隆不留半截仓库。
- 安全：克隆进程带 `GIT_ALLOW_PROTOCOL=https:http:ssh:git:file`，压过用户配置里的 `protocol.*.allow`；`ext::`/`fd::` 一律被 git 拒绝；错误消息、参数与 stderr 经 `RedactCredentials` 去掉每个 `scheme://` 地址的 user-info。
- 对话框规则：地址只收 `https`/`http`/`ssh`/`git+ssh`/`ssh+git`/`git`/`file`、scp 形式与本地路径；拒绝 `-` 开头、`transport::address`（`ext::` 会执行命令）与控制字符；**带密码的地址**单独提示并拒绝（会出现在命令行与报错里），只带用户名的照常放行。父目录必须是完整绝对路径。
- 克隆超时（`projects` 分区，0 = 不限）：“0 = 不限”翻成 `Timeout.InfiniteTimeSpan`，传给 git 层的 `null` 是“用它自己的 2 分钟默认值”。
- 三条项目创建分支（已有文件夹 / 克隆仓库 / 新建项目）共用同一个注册尾；已有文件夹不是仓库时问一句 `git init`。

相关文档：[01-sessions-and-workspaces.md](01-sessions-and-workspaces.md)（worktree 生命周期与移除）、[02-agent-runtime.md](02-agent-runtime.md)（步骤完成门禁与 `request_review`）、[07-workbench-ui.md](07-workbench-ui.md)（文件面板与预览列）、[12-storage-and-data.md](12-storage-and-data.md)（`reviews.db`）。
