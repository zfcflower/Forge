# 02 Agent 运行时

本文件说明 FORGE 的 Agent 循环、工具面、子 Agent、上下文管理、计划与评审门禁、用量与限速、自动化引擎。

## 2.1 循环

一次回合是不断重复的“流式请求 → 审批 → 执行 → 回写”：

- 模型输出流式增量（文本、思考、工具调用）经事件通道发往 UI；UI 侧按渲染帧合并，每帧最多更新一次。
- 工具调用按副作用分批：连续**只读**调用并发执行，**有副作用**的调用依序逐个执行；所有结果按调用顺序写成一条 Tool 消息。
- 审批看**本次调用的边界**加设置页 `tools` 的全局档位（「工作区授权」默认 / 「全部授权」）：只读免问；写与执行在工作区内免问，参数里点名了工作区外的路径（绝对路径、`~`、`..` 逃逸）才弹一张卡，允许后工具才真的越过边界；插件、MCP 与网络类工具按同一规则（批准即授权），只剩插件审批与删除确认仍必问。档位每次求值现读，改完从下一次调用生效。
- 取消回合时，每个待决调用都补一条“已取消”结果，历史始终合法。
- 超长工具输出转存 Artifact（`forge.db` 的 `artifacts` 表 + `<UserData>/artifacts/<会话 id>/`），上下文里只留预览与 id，模型用 `read_artifact` 取回。
- 单次工具调用可被单独取消（`AgentHost.CancelToolCall`）：取消链到回合令牌、登记在整棵树共享的取消表；工具抛出的取消转成“用户取消了该调用”的错误结果写回历史，回合继续。`bash` 等进程工具会杀整棵进程树；`spawn_agent` 没有同步模式，它一律立即返回（见工具表）。**只有需要结论的内部路径**（评审工具 `request_review`、自动化）的子 run 才阻塞并链在那次调用上，停掉调用即取消它。`spawn_agent` 的子 run 刻意不链在发起它的那次调用/回合的令牌上（见下面工具表），子 run 刻意不链在发起它的那次调用/回合的令牌上（它要活过发起它的回合），只由 `subagent_stop`、所属会话的停止（按父会话取作用域，根会话停整棵树）或运行时拆除结束——拆除之后不再接受新的派生。
- 事件通道无界、不丢事件；UI 合并由 `FrameCoalescer` 完成（`DispatcherPriority.Background`，单次 flush 上限 2048 项）。

## 2.2 工具面

工具由四类来源合成：Core 内置、App 内置（文件/git/PR/web/spec/扩展编写）、插件工具、MCP 适配器。同名以**首个注册者**为准（内置优先）；`run_code` 的脚本面里插件工具在前、同名先注册者生效。

### 内置工具清单（条件注册见各项；MCP 服务器工具另计）

| 组 | 工具 | 副作用 | 说明 |
|---|---|---|---|
| 文件/搜索/shell | `read` | 只读 | 有界窗口读：默认 2000 行 / 256 KiB，上限 20000 行 / 1 MiB；BOM 嗅探 + 严格 UTF-8 前 8 KiB；二进制即报错 |
| | `write` | 写 | 整写走原子替换（临时文件 + fsync + 改名）；`append` 为普通追加 |
| | `edit` | 写 | 要求唯一匹配；0 次或多于 1 次且未 `replace_all` 即报错并给行号；同路径串行 |
| | `copy` / `move` | 写 | `from` 收路径/glob（`* ? [..] ** {a,b}`）/数组，单个普通路径时 `to` 是确切新路径，多源或含 glob 时 `to` 以 `/` 结尾表示目标目录；全部校验后再执行；目标已存在默认报错，`overwrite` 才替换文件/合并目录；目录树 ≥64 文件时并行复制；不复制链接与 `.git` |
| | `delete` | 写 | `path` 收路径/glob/数组；非空目录须 `recursive`；删链接不碰目标；工作区根与 `.git` 不可删；≥64 文件时并行删 |
| | `ls` | 只读 | 单层列目录，最多 10000 条；显式目录不套忽略规则，链接以 `l` 标记 |
| | `find` | 只读 | 遍历 + glob，最多 1000 条（上限 10000），深度上限 64，遵循完整 gitignore 语义 |
| | `grep` | 只读 | 托管引擎（可选 ripgrep 加速），默认 1000 条命中，单文件 8 MiB，并发 `min(核数,8)`，正则 2 秒超时 |
| | `bash` | 执行 | 先 Direct（参数向量、不经 shell），出现 shell 语法才走解释器；命令串的解析器是**平台的那个 shell**（Windows 上 `pwsh`，缺它时 `cmd.exe`；Unix 上 `bash`/`sh`），与工具名无关，多行或嵌套引号的命令应先写成文件；每流 512 KiB、默认超时 120 s（1–600）、全局并发 8；超时/取消杀整棵树；只做没有专用工具的事：与 `read/grep/find/ls/edit/write/copy/move/delete/web_fetch` 重叠的命令被拒绝并指路，`git` / `gh pr|run|workflow|api` 只对持有 `git_*` / `pr_*` 工具的调用者开放(持有任一 `pr_*` 则整个 `gh` 放行)(git 另分只读/写两档,且不含工具已覆盖的子命令);只过滤管道输入的 `grep/head/tail` 放行；非廉价命令按工作区串行（等锁不计超时） |
| 会话 | `todo` | 读/写按参数 | 7 种操作：`list`/`add`/`update`/`remove`/`promote`/`plan_summary_get`/`plan_summary_set`；写操作先确认条目属于本会话 |
| | `set_title` | 写 | 仅在会话首轮可命名；用户改名永久优先，只写 `sessions.title_source`，不动分支与目录 |
| | `ask_user` | 只读 | 仅在宿主提供问题处理器时注册；问题卡 + 选项/自由文本；支持多选 min/max |
| | `skill` | 只读 | 按名返回 `SKILL.md` 全文（上限 64000 字符）；名字只在本会话发现的四个技能根里解析 |
| Agent | `spawn_agent` | 只读 | 派生内置 Scout/Worker/Plan/Reviewer 或用户 Agent；`<kind>-<n>` 登记；深度上限 2；没有 `tools` 列表的用户 Agent 会被拒绝派生（返回错误并引导模型问用户选角色）；不能关闭子 Agent 时用它停；可选 `model`/`provider`/`thinking` 只作用于这一次派生（`models` 给确切 id，多命中列候选绝不猜），不写设置，与 `resume_id` 互斥；**一律立即返回「已启动、尚无报告」并让子 Agent 在后台继续跑**，父回合不再被堵住（同一个回合里可以继续 spawn 或干别的，回合结束后用户也能给它新任务），子 Agent 结束时以回执消息回到父会话（有 App 标签的会话：父在跑就在工具批次边界送达，空闲就存进历史+转录打未读点，绝不自动起一轮；模型派生的子 Agent 没有标签，回报直接进它自己的实例；没有 App 接缝时父实例内存与 store 照写）；后台子 run 不链在发起它的那次调用/回合上，只能由 `subagent_stop` 或父会话的停止（按调度器的登记表停，不看可能还没刷新的子 Agent 行）结束，状态行带真实停止原因 |
| | `subagent_steer` | 只读 | 在工具批次边界重定向子 Agent（已发调用跑完，未开始的补 `skipped`） |
| | `subagent_followup` | 只读 | 子 Agent 本应结束时注入后续指令并继续 |
| | `subagent_stop` | 只读 | 只取消该子 Agent，待决工具补 `Cancelled.`，父收到非错误结果 |
| | `request_review` | 只读 | 派生 Reviewer 评审变更集，返回结论行 + 原文；结论落 `reviews.db`（见 2.7） |
| | `models` | 只读 | 列已连接提供商的模型与各自接受的思考档位（隐藏模型不列，`provider` 可收窄，单次上限 400 行） |
| 检索 | `search_history` | 只读 | 全文检索历史消息（FTS5，缺失时回退表） |
| | `read_context` | 只读 | 按轮区间取回**托管上下文的 entry 原文**（折叠块的 `retrieve:` 行即用法）；上限 50 轮/次 |
| | `search_context` | 只读 | 全文检索历史轮的状态事实（task/结论/unresolved/next），命中给轮号与摘要 |
| | `read_history` | 只读 | 按 seq 区间取回**原始消息**（entry 的 `seq:` 行或折叠块的 `originals:` 行即用法）；输出 200k 字符封顶，超出转 Artifact 分片读 |
| | `read_artifact` | 只读 | 读回超长输出转存的 Artifact（不可关闭） |
| Web | `web_fetch` | 只读 | 只取 http/https；默认拒绝回环/私网/链路本地；手动跟随最多 5 跳、每跳重查；正文上限 5 MB、30 秒超时；HTML 转纯文本 |
| | `web_search` | 只读 | 后端 Exa（无密钥走公共 MCP，默认）/ Tavily / DeepSeek（可用模型提供商的密钥）/ Brave / SearXNG；无可用后端时工具不注册；后端工厂每次调用现取 |
| Git | `git_status` `git_log` `git_diff` `git_fetch` | 只读 | 仅仓库项目的会话提供（General 常规会话不提供：其目录在共享的 General 仓库内，git 工具会看到所有常规会话的改动）；远程失败以结构化结果返回而非抛异常；`git_diff` 支持 `base`、`paths`、`stat_only` |
| | `git_branch` `git_sync` | 写 | 按工具名固定划分；推送只允许 `--force-with-lease`；拉取策略来自用户设置 |
| PR | `pr_view` `pr_workflow` `pr_checks` `pr_ci_log` `pr_comments` `review_comments` | 只读 | 13 个 PR 工具中的 6 个只读；仅当会话所在仓库有远程时提供（会话启动时判定，之后只在项目树“配对远程仓库”成功时于下一回合边界重判，不监视外部变更）；见 04 |
| | `pr_create` `pr_sync` `pr_edit` `pr_comment_reply` `pr_comment_resolve` `review_comment_resolve` | 写 | 五道闸门、verdict 门禁 |
| | `pr_screenshots` | 执行 | before/after 截图 + 孤儿提交 + 更新 PR 正文 |
| 规格 | `spec_get` `spec_graph` `spec_validate` | 只读 | 读单节点与链接、走图、校验悬挂链接与重复 id；仅当编排开启或工作区已有带 `id` 的规格时注册（会话启动时一次性决定）；规格的查找与修改用 `grep`/`edit` |
| 扩展编写 | `extension_list` | 只读 | 列出已装与待决扩展 |
| | `extension_create` `extension_update` | 写 | 同一 schema；暂存草稿并提交审批卡，阻塞到用户决定 |
| | `extension_disable` `extension_delete` | 写 | 禁用 / 永久封锁（同 id+version 删除后不可恢复） |
| codemode | `run_code` | 执行 | 只列出可调用名字，不逐工具列 schema；脚本面只有白名单引用（无 `System.IO`/`System.Net`），作用面就是那些内层调用，它们逐次按边界判定；见 11 |
| MCP | `<server>__<tool>` | 默认执行，`readOnlyTools` 可标只读 | 服务器工具适配器，审批卡显示服务器段 |
| | `mcp_tool_search` | 只读 | 选面上限隐藏工具时的回取入口，命中即 pin 并当场重发工具面 |

另有 **`message_session`**：只给根会话与它的 Agent 会话（见 §2.3），参数 `(target, message)`，异步投递。

### 工具开关与热加载

- `settings.json` 的 `preferences.toolsDisabled` 是开关集合（去空白、去重、按序数排序，为空不写）；`read_artifact`、三个子 Agent 控制工具与 `skill` 是必需项，写了也忽略。
- `spawn_agent` 与 `request_review` 各按自己在允许清单里的条目开关；不再有“允许派生 Agent”总开关。
- 工具面是可替换快照（`ToolSurface`：版本 + 注册表，`Volatile` 读）：版本未变复用同一 `Definitions` 数组，提示词缓存不破；集合没变时不发布。批准后的下一次模型请求就能看到新工具。
- `AgentHost.AllTools` 列出全部内置与外部工具（含已禁用），供资源窗口按会话统计工具占用；设置页 `tools` 分区的目录是应用级清单（`ToolCatalog.Describe`，组合根算出来），不读宿主，也不随会话变。
- `run_code` 的脚本面只收“插件工具 + `SideEffect == ReadOnly` 的文件/web/MCP 三族”（只读判定不认名字）；`copy`/`move`/`delete`、所有 git/PR 工具（含只读）以及 `write`/`edit`/`bash`、扩展编写工具一概不进。

## 2.3 子 Agent

- 内置四种：**Scout**（只读侦察，read/grep/find/ls + `web_search`/`web_fetch`）、**Worker**（实现一个任务，加 edit/write/copy/move/delete/bash + 两个 web 工具）、**Plan**（只读产出有序计划，同样带两个 web 工具）、**Reviewer**（只读评审，多一个只读 `git_diff`，**不带** web 工具——抓来的网页是不可信输入，不进结论）。模型与思考强度可分别设置。
- 生成：`spawn_agent` 按 `<kind>-<n>` 登记实例，同类型共享稳定提示词前缀以命中缓存；同类型实例可按 id 续跑（当前未在运行）。可选 `model`/`provider`/`thinking` 只作用于这一次派生：`models` 给出确切 id 与接受档位，解析器绝不猜（多命中一律列候选），thinking 夹到模型接受档并在回执注明，覆盖不写设置、与 `resume_id` 互斥。
- 并发与深度：嵌套深度上限 2；整棵树的活跃 Agent 数默认 4，可在 `agents` 分区调到 1–16（扩容立即唤醒排队中的 Agent，缩容不打断已在跑的一轮）；父 Agent 等子结果时交还名额，避免嵌套死锁。
- 生命周期事件：`Started`（父发出）→ 仅在确实等名额时 `Queued` → `Running` → 每个 run 恰一次 `Finished`（含异常与父取消；父取消记 `Stopped "parent cancelled"`）。用量是本 run 增量，耗时从 spawn 起算（含排队）。
- 控制语义：转向只在工具批次边界生效（批次内多条转向合并成一条消息、一个事件）；后续指令在子 Agent 本应结束时注入；停止只取消该子 Agent。
- 树上的子 Agent 行有两个来源：存储里按 `parent_session_id` 推出的历史行（重启后也在），与会话加载后的实时名单（带运行状态）；状态从最后几条存储消息推断，推不出显示“已结束”，不把未知当完成。
- 结束的子 Agent 收进底部组“SubAgents 历史 (n)”，可只读重放；没有运行/排队/已结束条目时整块从布局中移除。
- **Agent 会话**：用户在活动根会话下手动增加的子会话（种类在项目树的「新建 Agent 会话」二级菜单里选定，之后固定），跑在**根会话的 `AgentHost`** 里（共用工具面、审批处理、取消表、树内并发名额与限速）；存储形态与模型派生的子 Agent 相同（`parent_session_id` = 根、深度 1、初始上下文空白），区别只在**由用户驱动**。两条隔离是机械的：用户所有的实例不进模型可见名单，`spawn_agent` 续跑与 `subagent_steer`/`subagent_followup`/`subagent_stop` 对它一律按未知 id 处理；深度同为 1，可再派生一层（深度 2 的不能再派生）。在根标签按停止**连带停**它名下的回合与已投递未开跑的消息，子会话标签的停止只停自己。
- **`message_session`**：根与它的 Agent 会话之间唯一的通道。门禁是用户亲手点名——只有从输入框 `@` 弹层“会话”组选中的**结构化提及**才算（手打的 `@名字` 文本不算），授权写入后**保留到目标会话结束**（目标归档/关闭、根离开 `Active`、用户撤销时失效；只存内存，重启即清空）。调用是**纯异步投递**：立刻返回“已投递 #n / 已排队 #n”并明说“未完成，你现在没有结果”，**不自动起轮**（否则 A 投→B 回→A 自动起轮再投递，就成了无人看管的自动循环）。**回程回执**（定稿）：对方一轮结束——完成、失败、被停止都算——向发起方写一条结构化消息：头行（回复编号、来自谁、状态、任务短标签）、改动行（`N 个文件 (+a/−b)`，取该轮检查点）、报告行（对方最终回复原文，**上限 4000 字符/250 行**，超出转 artifact 并附 `完整报告: <id>`，复用 `read_artifact`），**不带工具调用清单**。状态必填：失败/停止也发回执，否则发起方只能把“没有消息”当成功。送达时机：发起方正在跑回合时在工具批次边界送达（同一轮可见），空闲时作为未读消息落进转录。投递本身不弹审批卡，对方回合内的工具调用照常审批；目标只读时直接报错。**点名只约束“谁先开话头”**：子会话要主动向根发起须在子会话里也点名过根，回程永远自动（投递即附带回程许可），但回程不是“再问回去”的通道。
- **`write` 覆盖检查**：每个 Agent 实例一份内存读取记录（`路径 → (长度, mtime)`，由 `read` 记录），`write` 覆盖已存在文件时在路径门内比对，没读过或已被改则报错不写；`edit`/`append` 不检查，但只在“编辑前磁盘状态与本 Agent 记录一致”时才更新记录；记录不设上限、不落盘，实例重建即清空。完整规则见 `src/Forge.IO/SPEC.md`「`write` 覆盖检查」，热路径须给基准数据。

## 2.4 插话、排队、转弯、停止

- **插话**：消息在当前工具批次之后送达正在运行的 Agent；若回合先结束则成为下一回合。文字 + 附件合并成一条用户消息（键入文本以空行连接成一个文本块在前，附件随后），UI 为每条插话留一行提示（`插话：文字` / `插话：2 个附件（图片、report.pdf）`），这一行按用户自己的话显示——13px、主文字色（`TextPrimaryBrush`），与用户消息气泡同档，不像其它提示那样是 11px 的次要色 caption。
- **排队**：显示为小片，回合结束自动发送；运行中 Enter / Ctrl+Enter 默认排队。
- **转弯**：取消当前合并立即发送。
- **停止**：取消当前回合并暂停队列；空草稿发送即恢复队列。
- 附件随插话一起送达；未送达的插话连同附件回到队首。
- **等待期可撤回编辑**：排队小片与尚未送达的插话小片都带「撤回并编辑」按钮——从队列/待送达列表里取掉，文字接在已有草稿之后、附件放回输入框，绝不覆盖正在写的内容。插话一旦被循环在工具批次边界取走就不可撤回（`AgentInstance.Withdraw` 返回 false），小片随 `AgentInterjected` 自行消失并提示“已送达模型”；排队消息撤回收到了评审面板交办的那种消息时同时通知来源方“已撤回”。

## 2.5 托管上下文与确定性折叠

模型的历史区不是原始对话流水，而是**每轮一条 entry** 的累加，一条 entry 是三消息：任务发 `user`、回复发 `assistant`（原样，不加标题）、收据发 `tool`（一个 synthetic 的 `evidence_receipt` 调用 + 它的结果 `{"turn":…,"seq":"a-b","reads":[…],"writes":[…]}`）。每个回合开始时由 `turn_ledger` 重建一次历史区（折叠块 + 未折叠轮的 entry）；**回合内不受影响**——本回合的每一次请求都带着本回合的真实交换（工具调用与结果全在），不裁剪、不收据、不摘要。完整算法与找回路径见 [16-context-compaction.md](16-context-compaction.md)。

- 缓存:兼容/Responses 协议的请求默认带 `prompt_cache_key`(lineage 根)与 `prompt_cache_retention`;拒绝这两个字段的端点由适配器一次性降级记住,之后不再发。
- 预算 `U = W − R`（窗口减输出预留）：回合首请求用触发线（默认 85%），回合内用紧急线（默认 95%）。**没有目标线**——折一次就折到底。
- 过线时把除最新 N 轮以外的**已结算**轮全部折进指针块（N 默认 3，设置里可调 1–10）。折叠不花模型调用，一次折到底与折一半代价相同，而折得越深下一次折叠越晚——所以不设“折到百分之几”。折叠 = 一次单调 `folded` 标记 + 一次重渲染，**零模型调用、零摘要**。
- 指针块固定三行：`[compacted: turns a–b · seq x–y · n turns]` / `retrieve: read_context(from_turn, to_turn)` / `originals: read_history(from_seq, to_seq)`。它**不聚合任何内容**——聚合“做完了什么”会变成变相摘要，而它过去聚合的 `unresolved` 已随状态节一起删除。
- 回合内撞窗口：再折一次后重发一遍；仍不够（本回合自身超窗口）按原错误失败。没有第二级压缩。
- 手动 `/compact`：折叠到只剩最新 N 轮，忽略阈值与开关，不再接受“关注点”（折叠是确定性的，没有可steer的余地）。
- 找回：entry 的 `seq:` 与折叠块的 `originals:` 指向 `read_history`；超长工具输出（>32k）仍在 artifact 里，用 `read_artifact`。`read_context` 取回被折叠轮的 entry，`search_context` 查 `turn_ledger` 的全文索引。
- 旧会话（无 ledger 行，或宿主没配 ledger）继续走 `summaries` 摘要视图 + 原始尾部；新会话不再写摘要。摘要级与收据级的代码、`compaction.summarizer` 槽位都随本变更删除。
- 计划摘要（`plan_summaries`）与此无关，另存：每次替换把旧文本记一条修订，每会话保留最新 50 条。

## 2.6 计划与 TODO

- 清单按**分组**组织：`todo add` 带 `group`（按标题找，没有就新建）把条目收进该组步骤；不带 `group` 进**待排散项队列**。`promote` 把一条散项升成以它命名的新分组，继承其 id 与标题。
- 同一时刻只有一步 `in_progress`（存储层保证：写 `in_progress` 会把同会话其它 `in_progress` 退回 `pending`）；`paused` 表示等外部输入或依赖，永不触发步骤提交。
- 序号层内致密：`(session_id, group_id)` 是一层（`group_id` 为 NULL 即待排区），插入/删除/移动只重排本层；分组顺序只增不减（没有分组删除入口）。
- `update` 接受 `commit_subject` / `summary` / `verification`；状态变 `done` 先过**步骤完成门禁**（2.7），通过后才提交这一步，提交结果（短 sha 或失败原因）写进工具结果；提交失败不回滚 done。
- `plan_summary_set` 写入计划摘要（**Summary / Changes / Testing** 三段），成为 PR 正文的来源。
- 面板：置顶排队输入行 → 未完成分组（组行 + 步骤，未完成组始终展开）→ 待排散项 → 完成区（已完成分组收成一行、可展开）→ 历史压底。面板**只读**：没有勾选框、不能改状态，只支持新建、删除、同层拖拽调序、失败步骤重试提交。

## 2.7 步骤完成门禁与评审

### 完成门禁

**改了代码的步骤要标 done，必须先有 `request_review` 的 approve 结论与非空、且不是 `not verified` 的 verification；没改代码的步骤直接放行。**

- 证据来源是窄接口 `IStepChangeProbe.ProbeAsync(sessionId, workingDirectory, ct)`，返回 `NotApplicable` / `Clean` / `Dirty` / `Unknown`：
  - 无 probe、`NotApplicable` → 放行；`Unknown` → **失败关闭**并写出 git 的原因；`Clean` 且该步骤没有自己的 `CommitId`、也没有基线 → 放行；步骤基线（首次转 `in_progress` 时记下的工作树内容指纹）与当前相等 → 放行；其余要 approve + 有效 verification，且评审结论记录的工作树指纹必须等于当前指纹（评审后内容变过即过期，需重新评审；`Clean` 但内容与基线不同表示代码在步骤自己的提交之外被提交了，同样要评审）。
- 门禁在写状态**之前**判定：拒绝时条目状态不变、不提交；只上抛取消，probe 其它异常按 `Unknown`。
- 结论落 per-host `StepReviewLedger`（按 session + step），App 经 `StepReviewLedgerStore` 持久化到 `reviews.db.step_review_verdicts`：重启后恢复仍与当前工作树指纹匹配的结论；未配置 store 的宿主重启后需重新评审。步骤离开 done 时 `Forget` 并删除台账行。
- 三个相互独立的开关（均在 `settings.json`）：`reviewToolEnabled`（默认开）——Agent 能否自行发起评审，即是否提供 `request_review`，建宿主时读取；关掉则没有证据来源，步骤门禁随之不存在；`stepReviewRequired`（默认开）——改了代码的步骤标 done 是否要过本节门禁，每次尝试读取；`reviewAutoFix`（默认开）+ `reviewAutoFixMaxRounds`（默认 3，1–10）——`request changes` 后 Agent 自己修复并再评审，同一步骤已完成的非 approve 评审达到上限时停止并用 `ask_user` 问用户，一次评审的发现要合并在一轮里全部修；关掉自动修复则只向用户汇报并等待。旧总开关 `workflowGatesEnabled` 已删除，仅在加载旧文件时把 `false` 迁移为 `stepReviewRequired=false`。`ledger.Rounds` 在 approve 或步骤离开 done 时清零。非空 verification 与 `pr_create` 的五道闸门是固定行为，不可关。

### `request_review`

- 两种目标：带 `step_id` 评该步骤自己的提交（`commit^..commit`，根提交对空树）；不带 `step_id` 评 `base`（默认仓库默认分支的 merge-base）+ 未提交与未跟踪的树，`intent` 必填。两者同时给是错误；已知参数的 JSON 类型不对也是错误。
- 评审包一次渲染：意图、步骤记录、分支/HEAD/基准/计数/提交清单/文件清单、diff、焦点、最后一句指针。上限默认 diff 48000 字符、提交清单 50 行、文件清单 200 行、每个文本字段 2000 字符，可在 `git` 分区调（1000–500000 / 5–500 / 20–2000 / 200–20000）；截断时提示按 `git_diff {"base": …}` 读其余，收窄过的变更集自述“只看这些路径”。
- 结论只认 Reviewer 文本里最后一条 `verdict:` 行（大小写不敏感，容忍 `_`、Markdown 强调、列表/标题标记与行尾标点）；没有结论行就是 `Unstructured`，绝不从缺失推断通过。
- 变更集为空时不启动 Reviewer，返回“没有可评审的内容”；评审被停止时返回错误且**不解析**结论行。
- 结论落 `reviews.db` 的 `review_conclusions`：session、工作目录、`step_id` 或 `base`、HEAD、结论态、Reviewer 原文（截 8000 字符）。结论态五值固定：`0 Unstructured / 1 Approve / 2 RequestChanges / 3 Stopped / 4 Failed`，`Stopped` 与 `Failed` 都不是通过。
- 评审方式可配置（`git` 分区）：`reviewMode` = `subagent`（默认）/ `manual`；`reviewAfterStepCommit`（默认关）在步骤提交成功且模式为 subagent 时，把一条带 `step_id` 的 `request_review` 请求交给该会话（空闲起一回合、忙则进草稿，绝不背着用户起第二回合）。
- 评审与作者自评互不影响：Reviewer 的 `git_diff` 记在它自己的子会话上（`GitReviewLedger` 按 session + worktree 分键），所以一次评审既不会让 `pr_create` 的闸门通过，也不会被当成作者自评。
- 评审者与其他子 Agent 同规则复用：同一会话里上一次评审已结束（1 小时内）、类型相同且任务文本与它读过的变更集足够相似时，`request_review` 续跑同一个 Reviewer 而不是新开一个。它自己的提示词要求按本次 package 判断，所以上一次的 `verdict:` 行不是这一次的结论；不变量只剩“结论只认它本次最后一条 `verdict:` 行”。

## 2.8 提问卡（`ask_user`）

- 模型给问题与可选项，回合挂起等待；选项或自由文本作答、取消即返回取消结果。
- 多选题可带 `minSelect`/`maxSelect`（整数，1 ≤ min ≤ max ≤ 选项数；允许自由输入时再加 1，因自由文本算一次选择）；单选题带这两个参数会报错。
- 每题有“跳过本题”开关；全部跳过时提交禁用，改用“忽略”（Declined）。当前 UI 已删除逐题“跳过本题”，只保留底部“忽略”；`UserQuestionAnswer.Skipped` 仍作为公共契约保留。
- 同一会话（含子 Agent，共用会话 id）同时多个提问时，审批队列每会话只把一张卡放进列表，其余 FIFO 暂存；当前卡解决后下一张出现，可见卡显示“还有 N 个问题等待”。不同会话互不阻塞，问题不阻塞审批卡。

## 2.9 附件输入

- 三条入口（粘贴、拖放、选取）走同一个分类器：扩展名决定“声称是什么”（超限文件读之前就拒），前导字节嗅探决定“实际是什么”，其余严格 UTF-8 解码（含 NUL 或非法 UTF-8 即拒并弹 Warning）。
- 每类上限：PDF 32 MB、音频/视频 20 MB、文本 256 KB、非图片附件每条最多 10 个（图片另有数量限制）。
- 图片成为 `ImageBlock`（走缩放路径）；PDF/音频/视频成为 `FileBlock`（带文件名）；文本文件内联成带 `File: <名称>` 头与自适应反引号围栏的 `TextBlock`。
- 门控：模型声明的 `ModelInputs` 缺该类即拒绝；协议没有该类的线路形状（如 Anthropic 无音视频、Chat Completions 只认 wav/mp3）同样拒绝并通知；文本文件不受模型门控。
- 换模型后已有附件不再被接受时：弹一次 Warning、输入框下方常驻原因、发送/排队/插话/转弯全部禁用，附件**绝不静默丢弃**；移除或换回模型即解除。
- 工具结果里的图片条数/大小按协议发送；界面显示不受“发给模型”开关影响——开关只决定模型收不收。
- 点击已发送消息里的附件：图片与文本/源码进 FORGE 自己的预览（图片预览标签页 / 编辑器），PDF、音频、视频交本机默认应用打开；内联字节先写成 `%TEMP%/forge-attachments/` 下的文件（名称 = 内容哈希 + 原名，同一附件只写一次）。

## 2.10 用量、成本、缓存与上下文读数

- 用量读数是根 Agent 的：输入、输出、缓存读、缓存写、成本、缓存命中率。
- 计费输入 = `InputTokens − CacheRead − CacheWrite`（`InputTokens` 已含缓存），逐项 `max(0, tokens) × 单价 / 1e6`；缓存命中率 = `CacheRead / InputTokens`；窗口占比夹到 0..1。
- 元数据链：目录快照 → 提供商 `/models` 上报 → `settings.json` 的 `providers[].modelMeta`，逐字段合并，未知保持未知，绝不补 0。
- composer 条带：`RC 98.5%` + 五段量规 + `62k/170k · 36%`（C / U · 百分比）；窗口未知时只写 C 的 token 数。量规三态：常态、C ≥ 触发线 − 10 个百分点为警告色、C ≥ 触发线为超限色。
- 账号额度读数（`strip-quota`）：订阅窗口（5 小时 / 周）与 API 密钥的分钟级限流分两套标签；只在提供商确实报告时出现，倒计时 ≤90 秒用秒、≤5 秒写“即将”；颜色只跟服务端状态（`allowed_warning` 警告色、`rejected` 超限色）。
- Anthropic 响应头 `anthropic-ratelimit-unified-{5h,7d}-*` 解析成订阅窗口；通用 `x-ratelimit-*` 解析成分钟级限流（不进用量日志的 `rateLimit` 节点）。
- **Agent 会话的用量取每轮事件**：轮次指标直接取该轮 `AgentSubAgentFinished` 上的 `Usage`/`Elapsed`（事件本就带每 run 增量，底部子 Agent 区已在用），不要复用只读根实例的 `ReadRootCounters`（那会让子会话那一轮把根的 token 再记一遍）。需要在一轮之外显示实时读数时再按实例取数。

## 2.11 按提供商限速与额度恢复

- 每个请求离开前先从**按提供商**的本地预算取额度，取不到就等（不靠服务端 429）：默认 600 请求/分钟、4,000,000 令牌/分钟（某维为 0 = 不限），自动化恢复间隔默认 10 秒。
- `rate-limit` 分区可改全局值（请求 1–100000、令牌 1000–1000000000、间隔 0–600 秒）与每提供商覆盖（留空继承、填 0 表示这家不限）；生效上限取“手填/全局”与“响应头所报”的较小者，响应头只会收紧。
- 获取点在重试中间件最外层，所以重试也计费；估算按字符类别加权（拉丁 4 字符/token，CJK 约 1.6 字符/token），应答后按 `InputTokens + OutputTokens` 校正。
- 响应头校准同时推进补充时间戳；剩余为 0 且有重置时间就 hold 到那一刻，单次 hold 上限 1 小时，任何一次策略发布都会清掉它。
- 预算等待 ≥3 秒且每个 attempt 至多一次，先发 `AgentBudgetWait`，界面画成运行中轮次的一行非错误提示（含 provider 与秒数），不 toast、不落库；短于 3 秒的簿记性等待不报。
- 重试：传输层失败与 5xx 保留三次（0.5s 起倍增、封顶 30s）；429 走过载阶梯五次（1s/3s/8s/20s，full jitter），单请求累计等待 ≤60 秒，`Retry-After` 有值时以它为准；判额度仍在重试之前，额度类 429 一次尝试就抛。每次重试在运行中轮次画一行「重试中：provider · 第 n/N 次 · 约 X 秒后重发（原因）」，不 toast、不落库。
- 过载自动续跑：一轮因 `Overloaded`/`RateLimited` 的 429 耗尽阶梯后，等 60 秒经普通回合入口自动发一条「继续」，一条失败链最多 3 次（跨度约 3 分钟）；横幅显示倒计时并可取消，用户发消息即取消，额度类与未被重试过的 429 不走这条路。
- 失败轮的脚注是服务端原句，重试过则加「（已重试 N 次，共等待 X 秒）」；跑过工具的轮给「继续」（发一条继续指令，不重放工具），没跑过工具的轮给「重试」（整轮重放），额度在等的轮两颗都不给。
- 额度耗尽（402、带额度标记或长 Retry-After 的 429、带额度标记的 400/403）：重试层不重试，`AgentHost` 以 `AgentQuotaExhausted` 上报；没有重置时间时按退避用最小请求探测恢复。

## 2.12 自动化引擎

- 定义在 `settings.json` 旁的 `automations.json`，运行与状态在 `forge.db`（`automation_runs`、`automation_state`、`sessions.automation_id`）。
- 调度：间隔（每 N 小时的第 M 分）、每日若干时刻、每周若干天的若干时刻、5 段 cron；按本地时区算下一次，DST 跳过/重复的时刻各只触发一次；设置页给“接下来 5 次”预览。
- 引擎单个后台循环（最长睡 30 秒）；忙键 =（定义 id, 目标会话 id）；同一 occurrence 只跑一次；链条深度上限 5、运行不能触发自己；带目标键的请求计入尝试预算；每分钟写心跳；启动时把上次未结束的运行标为中断，`CatchUp` 定义补跑一次。
- **投递只走邮箱**：会话忙（回合、子 Agent、排队消息、交接）就等它空闲（`WaitingIdle`），从不打断；会话被接手/归档/移除/关闭即撤回；被回合闸拒绝时报告“撤回”，不落进输入框。
- 三种放置：`Scratch`（每次在 General 建暂存会话）、`FixedFork`（复用该自动化自己的 `forge/` 分支 worktree 会话）、`Scout`（在指定活动根会话下起只读 Scout 子 Agent）；`ReadOnlyTools` 限为 read/grep/find/ls；可单独指定模型。
- `Remind` 动作只发通知。
- **五个内置项**（可在设置页逐项停用，全局可暂停）：
  - `builtin.quota-resume`：额度用尽后按重置时间 + 0–60 秒抖动等待，经邮箱发“继续”，最多 5 轮；用户自己发消息即取消等待；关闭应用造成的取消保留状态供重启续跑。
  - `builtin.ci-chain`：每 5 分钟查有打开 PR 的分支会话检查结果，新失败先起 Scout 只读分析日志（每 job 200 行/12k 字符，总 40k），再把结论交给主 Agent 修复；修复后只推 PR head 分支、永不推 base，推送前逐项核对 head/游离/分支名/上游/后代关系，固定 `HEAD:refs/heads/<head>`、从不强推；跨仓库 PR 只提交不推送；每 PR 尝试预算 3 次。
  - `builtin.session-watch`：每分钟看已打开会话：回合 15 分钟无事件记“停滞”（有外部命令在跑时改按 60 分钟记“命令运行时间很长”）、审批卡等待 10 分钟记“待审批”、窗口在后台时用户回合结束记“已完成”；只通知。
  - `builtin.housekeeping`：每日 04:00 prune worktree 记录、删空的未使用暂存目录（不足 1 小时不动）、修剪运行历史（每定义 200 / 总 2000）、归档闲置超过窗口且未打开的根会话（默认 7 天、默认只归档自动化创建的会话，可改为全部根会话或整体关掉）。
  - `builtin.model-catalog-refresh`：每日 05:00 抓模型目录快照并刷新每个已连接、凭据就绪且支持 `/models` 的提供商列表（单飞服务；304 只记“已是最新”）。
- 界面：会话视图输入框上方显示附着运行横幅（等待额度并倒计时 / 等待空闲 / 执行中）与“取消”；自动化创建的会话行带闪电标记；设置页列出定义（启停、立即运行、编辑、删除需确认）与运行历史（点运行可打开其会话）。

相关文档：[07-workbench-ui.md](07-workbench-ui.md)（composer、转录、面板）、[03-providers-and-network.md](03-providers-and-network.md)（提供商与限速设置）、[04-git-diff-pr.md](04-git-diff-pr.md)（步骤提交与 PR）、[15-appendix.md](15-appendix.md)（工具与常量速查）。

## 2.13 Turn Ledger

**回复是自由格式**：没有任何模板要模型遵守，答案就是它写的那段话；账本只记录这一轮的任务、回复、证据与 seq，从不评判回复的形状。不声明终结工具——工具面字节不变、无 JSON 参数开销，轮末判定用的就是既有的“assistant 消息无工具调用”。

- **没有契约节**：模型回复是自由格式，稳定前缀里只有 `# System` → 全局/项目 Agent → `# Role`，账本不再往前缀里追加任何模板。
- **答案不被改写**：模板只决定 ledger 行的状态。模板缺失或不合法时答案照原样交付、不回退重生成；ledger 行分别标 `missing`/`invalid`，不晋升任何状态。
- **证据由 harness 生成**：`files` 从本轮文件类工具的 `path` 参数抽取，`spec` 从 `spec_get` 参数与读过带规格 frontmatter 的文件里取，指针是轮首 user 消息 seq → 轮末 assistant 消息 seq，展示为 `seq: <a>-<b>`（与 `read_history` 的 `from_seq`/`to_seq` 同源，不用模型去猜它是什么）。模型看不到也写不了 seq，所以证据节整节由代码生成；不写行号（seq 不变，行号会漂）。
- **账本不存判定**：进度状态的权威源是 TODO 面板的 `todos` 表；账本的状态只有"轮在跑/轮已结束"，再存一份进度或一个自评词只会与面板分叉。
- **一轮一行**：轮开始写 `pending`，轮末原地 settle；`(session_id, turn_sequence)` 是主键。异常轮（取消、失败、无进展收尾、轮次上限）由 harness 按轮结局写终态，不要求模型配合。
- **台账即上下文**：ledger 不再是旁路记录——模型的历史区就是它的渲染结果（见 2.5）。异常轮（`missing`/`invalid`）照旧交付答案、不晋升状态，但它们的 entry 仍进上下文，那是模型那一轮实际说过的话。遵从率按（提供商 × 模型 × Agent 种类 × 任务类型）计数，是 ledger 字段的派生查询。
- **动态尾**：`<forge-context>` 每请求临时（不落库、不入历史），今天只载会话命名提示；它前面永远压一条固定的 assistant 承载消息让它独占一块——否则它会和开场提问并成同一块，下一次请求就在第一块断开、整段提示词重读。
- **一个观察窗**：输入区上条“资源”菜单里的“托管会话记录”弹出整个会话的 ledger 时间线——每轮一个节点「第 N 轮 — 用户输入」+ 一张与回复卡同款的卡片（结论、证据），分页异步读（见 [07-workbench-ui.md](07-workbench-ui.md)）；转录区的回复卡不再挂轮级 entry。
- **子 Agent 回执**：回执的“报告”行取该子 Agent 的「结论」节，头行、改动行、状态行形状不变。

相关文档：[07-workbench-ui.md](07-workbench-ui.md)（观察窗）、[12-storage-and-data.md](12-storage-and-data.md)（`turn_ledger` 表与索引）、[16-context-compaction.md](16-context-compaction.md)（压缩，本期未改）。
