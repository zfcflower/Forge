---
id: forge-core
type: module-design
status: draft
title: Forge.Core
parent: forge-architecture
depends-on: [forge-architecture]
---

# Forge.Core

Agent 循环、模型提供商、提示词合成、SubAgent 调度与会话存储。只依赖 `Forge.Abstractions`;不含 UI、Git、MCP、插件加载。

## 边界

- 对外契约在 `Forge.Abstractions`;Core 的实现类通过这些接口被组合,审批弹窗、工具实现、工作区由上层提供。
- 审批:Core 只做策略求值(`IApprovalPolicy`)并在需要时调用 `IApprovalHandler`;没有处理器时按拒绝处理。
- 外部依赖仅 BCL 与 Microsoft.Data.Sqlite。

## 关键决策

### 缓存与提示词前缀

**每家厂商的缓存旋钮各按它的文档给(`ProviderCacheWiring`)。** 缓存策略不可移植:Anthropic 是显式断点 + 寿命,OpenAI 是 `prompt_cache_key` 路由 + `prompt_cache_retention`(`in_memory`/`24h`,由 `CacheHint.Ttl` 映射,`5m`→`in_memory`,其余→`24h`),xAI 只认 `x-grok-conv-id`(自动缓存加命中率),**GitHub Copilot 的缓存按 `X-Interaction-Id` 分桶**——不填这个头(或填空)的客户端在它服务的那批 Claude 模型上几乎拿不到缓存命中,所以 `CacheWiring.Copilot` 会把路由键(会话 lineage 根)发过去;它和 `Copilot-Integration-Id` 是两回事,后者决定配额桶与模型白名单(适配器本来就发)。
  - 两个缓存头都取 `CacheRoutingKey ?? SessionId`,所以交接/分叉之后仍然命同一批条目;降级后一律不发。
  - 另注意 Copilot 的 OpenAI 兼容面会把缓存写入摊进 `prompt_tokens`、只在 `prompt_tokens_details.cached_tokens` 报读取,所以那里的成本读数会把写入按普通输入价计——字段不存在,我们不做估算。
  - DeepSeek/Kimi/Meta/OpenRouter/自定义只有自动前缀缓存、没有任何请求级旋钮。
  - 于是预设携带 `CacheWiring`,只有声明了的那家才被发对应字段;DeepSeek 无论选项如何都不发(它按前缀自动缓存,且拒未知字段);用户仍可用 `provider.sendPromptCacheKey` 给接受该字段的网关打开路由键。
  - **Responses 通道与兼容通道同规则**:`OpenAiResponsesProvider` 同样按这两个选项在请求末尾写 `prompt_cache_key` 与 `prompt_cache_retention`(同一映射)、DeepSeek 不发,被 400 点名这两个字段时走同一条一次性降级(`ProviderDegradations` 键为 `Id|responses|BaseUrl`,与兼容通道的键互不影响)——两件事必须一起做,否则严格网关会让整轮 400。
  - 实测(2026-11-18):DeepSeek 的 Responses 端点接受并忽略这两个字段与任意未知字段、用量里 `input_tokens_details.cached_tokens` 能被正确读到,所以降级路径只由桩测试覆盖,未对真实的严格网关验证。
  - 被 400 拒的旋钮走与 Anthropic 同一条一次性降级路径(共享 `ProviderDegradations` 记忆:降级一次、记住这个端点、以 `MessageCompleted.Notice` 告知用户;该记忆 1 小时后过期再试完整形状,因为键里没有模型名,某个模型的拒绝不应让同网关的其它模型永久退回精简形状)。

**缓存纪律。** 请求的系统提示词只含稳定前缀(系统 → 全局 Agent → 项目 Agent → 角色,字节稳定);工具定义分三段、顺序固定且同类 Agent 一致:内置固定段按**声明顺序**(Core 自己的工具在前,宿主永远在场的工具在后:`read`、`write`、`edit`、`ls`、`find`、`grep`、`copy`、`move`、`delete`,shell 工具 `bash` 排在这些文件操作之后) → 内置条件段(按名称排序;`ask_user`/`set_title` 由宿主能力决定,`request_review`/git/PR/spec 等按会话条件决定) → 外部段(MCP/插件,按名称排序);外部工具的增减只改尾部、不动前两段字节;动态信息只作为末尾的一条临时 user 消息,正文两端带 `<forge-context>`/`</forge-context>` 标记(不落库、不进历史)。**这段头部对所有提供商是同一份字节,任何厂商适配都不许写进它**:各家缓存策略不同(Anthropic 是显式断点 + TTL,OpenAI 兼容是自动前缀缓存加路由键,再往后的厂商又是别的),所以能共享的只有头部本身;`ProviderConfig.SystemPromptPreamble`(Claude 的身份句)与 billing 块只在 `AnthropicProvider` 里加、只在 `Wire` 生效时加,不进 `ComposedPrompt.StablePrefix`,也不进 `PromptFingerprint`——否则切换开关会顺带改掉会话的缓存/回放身份。这是一条回归契约(头部不含厂商身份、指纹与开关无关、断言行数不变、别家适配器拿不到身份块)。缓存断点落在已持久化历史的倒数第二条。压缩只追加一条新的会话级摘要并把切点前的原文替换成摘要视图,切点后的近期原文(至少 keepRecent)与继承前缀逐字节不变。

### Agent 循环与工具面

**可选字符串的空白等于省略。** 每个工具的可选字符串参数里,缺省、`null` 与空白一律按“没给”读,取 schema 自己写的默认值——模型常把没用到的可选字段填成 `""` 而不是省略它,把空串当输入只让它白丢一回合,并收到一句 .NET 参数守卫的原话(点名内部形参、不给下一步)。落在各模块:`Forge.IO` 的 `path`/`workdir`/`glob`/`pattern` 走 `ToolJson.OptionalPath`,Core 的 `spawn_agent.resume_id` 与 `todo` 的 `after_id`/`title` 走 `JsonArguments.OptionalText`,`git_diff.base`/`git_fetch.remote`/`git_log.revision`/`git_sync.onto`·`remote`/`pr_create.base`·`body`/`screenshot.base`/`spec_graph.direction`·`edge` 在各自读取处归一。**例外只能本身就是有意义的空值**:`edit.new_string`、`write.content`(空串 = 删除/空文件)与 `todo.update` 的 `note`/`summary`/`commit_subject`/`verification`(空串 = 清除)——这些绝不能归一。

**循环。** 流式一轮 → 审批 → 执行 → 回写。连续只读工具并发,有副作用工具依序逐个执行;结果按调用顺序写成一条 Tool 消息。取消时每个待决调用都补一条"已取消"结果,保证历史始终合法。超长工具输出(>32,000 字符)转存 Artifact,上下文里留“头+尾”预览和 id;已自限长度的工具(`IBoundedOutputTool`:read/ls/find/grep)与 `read_artifact` 不再转存。bounded 工具的输出不经 spill,由上下文裁剪级在判定线处回收:越过触发线(或回合内的紧急线)时(`AgentLoop.CompactIfNeededAsync` 的裁剪分支)把 keepRecent 窗口之外的旧 `ToolResultBlock` 换成收据(`ContextReceipt` 一行,固定部分永不截断:`read_history <seq>` 是找回入口,只缩路径/命令段;错误结果、短于 512 字符的结果、纯收据块不再动,所以投影是定点);继承前缀一条不碰,keepRecent 窗口内不碰;收据总长与留头留尾受常量对约束(96/150/150,之和加标记余量 < 512);裁剪目标取触发线与摘要目标的中点(推导值,零新参数)。裁剪零模型成本:降到判定线下即发 `AgentContextReduced`(随后的 `AgentContextMeasured` 报判定线——回合首请求为触发线、回合内为紧急线)并复位迟滞,仍超线才走摘要。`read_history` 是该收据的唯一找回入口,所以它不设低上限:输出 200,000 字符封顶,越 32,000 字符照旧落 Artifact、按 `read_artifact` 的 `offset`/`length` 分片读回(收到 32,000 以下会让 >32k 的原文永久不可读——它只有 `from_seq`/`to_seq`,"narrow the range" 切不开单条消息);描述写明它只用于回取**已不在上下文里**的原文,近期对话直接读上下文、不要回放。

**SubAgent。** `spawn_agent` 拒绝没有 `tools` 列表的 Agent(否则它会拿到全部工具):返回错误,提示模型用 `ask_user` 让用户选角色并给出推荐清单,agent 文件改动从下个会话起生效(Agent 种类是每次建 host 的快照);内置 Scout/Worker/Plan/Reviewer 自带清单。Scout/Worker/Plan 的清单含 `web_search`/`web_fetch`(无可用搜索后端时 `web_search` 本就不注册;两者只读、免问),Reviewer 不含——它只判仓库内的变更,抓来的网页是不可信输入,不得进结论。
  - `spawn_agent` 按 `<kind>-<n>` 登记实例;同类型共享前缀,可按 id 续跑(同类型、且当前未在运行)。
  - **没给 `resume_id` 时自动复用**:先在同类型、模型自有、当前空闲、上次 run 结束不超过 `AgentOptions.SubAgentReuseWindow`(默认 1 小时)的子 Agent 里挑,按 `SubAgentAffinity` 打分——新任务与候选历史里**所有 user/assistant 文本块**共享的文件名各 3 分、词各 1 分(工具调用与工具结果不读:历史的大头是工具输出,解析工具参数不划算,所以候选只在正文里点过名的文件才算;评审的 `verdict` 行同样不计——它说的是上一次评审怎么结束,不是评审内容;拉丁词按 ≥3 字符去停用词计,汉字段按相邻两字成词计——中文没有分词符,整句当一个词就只能自己匹配自己)——取最高分、平局取更晚结束者;分数低于 `SubAgentReuseThreshold`(默认 2)就新开,无关任务绝不接到别人的对话后面。候选打分每次 spawn 现场扫一遍,不做常驻缓存(证据只有正文,十万字节量级、毫秒级、全是短命分配)。
  - 命中时回执写成 `[agent_id: X, resumed]`。
  - `reuse: false` 的调用方既不挑候选、其孩子也不进复用池(自动化 `SpawnChildAsync`:它的孩子不是任何人接下来的任务;这类 child 由调度器记名,显式 `resume_id` 仍可续);`request_review` 走默认,评审者同类型同主题即续跑(它带着自己读过的上下文与上一次结论,靠自己的提示词按本次 package 判断;`verdict:` 行不进证据表,结论也只认本次 run 的文本——续跑的 Reviewer 这一轮不写结论就是 `Unstructured`,上一次的 approve 不会顶上来);带 `model`/`provider`/`thinking` 覆盖的 spawn 与显式 `resume_id` 也不走自动复用。
  - **重启后仍可续跑**:子 Agent 的 agent id、kind、模型设置(模型/provider/思考档/输出上限)、上一轮结束时刻与 `reusable` 写进侧表 `sub_agent_state`(spawn 写一行、每个 run 结束重写;走存储的单写队列,关进程前 `DisposeAsync` 把它排空),建 host 后由 `SubAgentScheduler.RestoreAsync(rootSessionId)` 重建为实例——历史经 `SessionHistory.LoadAsync`、模型取库里那四个字段、id 计数器推到已用最大值之后,于是窗口内的 `resume_id` 与自动复用都在新进程里照样成立。窗口外、`reusable=0`、kind 已不存在、历史读不出的行一律不恢复(进历史菜单);用户驱动的会话不用此表(它自带会话行)。
  - **host 重建不丢活名单**:prompt 热重载为同一会话重建 `AgentHost`(新 catalog/layers/composer),`SessionRuntime.Build` 调 `SubAgentScheduler.TakeOverFrom(旧 scheduler)` 把 `instances/owners/finished/lastFinishedAt/notReusable/counters` 接过(仍在跑的实例留在旧 scheduler——热重载本就在有子 Agent 运行时拒绝重建),所以重建后第一次 `spawn_agent` 仍能续跑之前那个 child,新派生的 id 也不与活着的重名。
  - `spawn_agent` 另收可选 `model`/`provider`/`thinking`：`AgentHostOptions.SpawnModels`（App 实现，Core 不认模型目录）把它解析成完整的 `ModelSettings`，匹配走 Core 的 `ModelResolver`（精确原文 → `ModelIdRules.Normalize` → 快照后缀双向 → 包含；多命中一律列候选、绝不猜），`provider` 先按 id/显示名收窄，thinking 由 App 夹到 `ThinkingStepsFor(model, providerId)` 接受档并在回执注明；覆盖只作用于这一次 spawn、不写 `settings.json`、与 `resume_id` 互斥（续跑沿用实例原模型）；宿主没有该回调时工具定义不含这三个参数（字节随宿主快照固定）。
  - 活跃 Agent 计数覆盖整棵树;父 Agent 等待子结果时交还名额,否则嵌套会死锁。
  - **`spawn_agent` 一律异步**（没有同步/阻塞模式可选，也没有那个参数）：它立即返回「已启动、尚无报告，报告稍后以会话回复消息回来」并给 `agent_id`/`child session id`，子 Agent 继续跑；父回合不再阻塞（父 Agent 因此可以在同一回合继续 spawn、干别的、或直接收回合），**用户也能在这一轮之外给它新任务**。子 run **不链在发起它的那一次调用/回合的令牌上**（那正是它必须超越的东西），只能由 `subagent_stop`、会话停止（`SessionWorkspace.Cancel` → `SessionRuntime.StopBackgroundChildren` → `SubAgentScheduler.StopBackgroundRuns`：按调度器自己的 `backgroundRuns` 登记表逐个 stop，不看 App 的子 Agent 行——后台子 Agent 可能在它的 start 事件穿过帧合并器之前就已经在跑；同一调用同时关掉「之后再接受后台派生」，运行时拆除也走它）、或用完自然结束。
  - 完成时以**回执消息**进父会话：`SessionReplies.BuildChildReport`（同 `SessionReplies.Marker`，头行 `[会话回复 · 子 Agent <kind> (<agent-id>) · 状态 · 任务]`，改动行 0/0，报告 4000 字符/250 行封顶、超出转 artifact；无 artifact 时用 `SessionReplies.Cut` 截断并保留「已截断」说明，**绝不让构造失败丢掉回执**；状态行带真实的停止/失败原因，`SubAgentOutcome.Reason` → `BackgroundChildReport.Reason`）。
  - 投递由 `AgentHost.DeliverBackgroundReportAsync` 决定：root 与用户驱动的子会话有 App 接缝，走 `ISessionMessageEndpoint.DeliverReceipt`（父在跑→下一批工具边界，空闲→存历史+转录+未读点，**绝不自动起一轮**）；模型自己派生的子 Agent 没有标签，回报直接进它的实例——正在跑就 `Interject`（下一批工具边界），空闲就追加进内存历史与 store；没有 App 接缝（脚本/测试）时父实例内存与 store 照写，报告永不丢。`request_review` 仍阻塞（它需要结论）。**停与拆**：普通停止按父会话取作用域（根标签 = 整棵树，Agent 会话标签 = 它自己派生的那些；草稿还没拿到自己的会话 id 时作用域为空、不退回整棵树），不停用宿主——之后的回合照样能再派生；只有运行时拆除（`SessionRuntime.Dispose` → `CloseBackgroundChildren` → `CloseBackgroundRuns`）才关掉新派生（此后 `spawn_agent` 一律拒绝），并且同样逐个 stop。**派生与停止的竞态**：派生先记下（全局 / 按父会话）两个停止代数，`PrepareAsync` 这段 await 里发生的停止由登记时的比对拒掉（不启动、实例进不可复用名单）；已经登记但 `SubAgentRun` 还没挂上的 run 进 `backgroundStopPending`，由 `Attach` 在同一个锁里兑现停止。**残余插话**：模型所有的 run（根、模型派生的子 Agent）结束时、在释放实例 gate 之前，`PersistLeftoverInterjectionsAsync` 把循环没走到边界的插话写进存储与内存历史——根在无 App 接缝时、模型子 Agent 总是走它；有 App 接缝的根与用户驱动的子会话仍由各自标签把残余插话收回队列。  - 嵌套深度上限 2(根与其子可派生,孙不可)。
  - 子 Agent 不应超出父 Agent 的工具集,但不做强制:父为子 Agent 且声明了 `tools` 时,若子声明了父没有的工具,`spawn_agent` 向事件流发一条 `AgentNotice` 提示用户(根 Agent 的工具面此处不可知,不比较)。
  - 树内并发上限默认 4、可在设置里调到 1–16(`AgentRuntimeServices.SubAgents` → `AgentOptions.MaxParallelSubAgents`,每回合前经 `AgentHost.SetMaxParallelSubAgents` 应用到活的宿主:扩容立即唤醒排队中的 Agent,缩容不打断已在跑的一轮、只是不再放新的进来)。
  - 该工具对未超深度的所有 Agent 隐式可用,Agent 定义无法关闭它;要停止派生就在工具开关里关掉 `spawn_agent`(`agents` 分区的子 Agent 开关已删除),已在跑的子树仍可用 steer/follow_up/stop 管理。

**子 Agent 控制。** 每次 spawn 是一个 run,有独立 `CancellationTokenSource`(链到父 run 令牌),父 Agent 的 `subagent_steer` / `subagent_followup` / `subagent_stop` 与 UI 停止按钮共用 `AgentHost.SubAgentControl`(`ISubAgentControl`)。
- 转向在工具批次边界生效:已发出的调用跑完,本回合剩余未开始的调用补 `skipped: the agent was redirected` 错误结果(历史保持合法),指令作为下一条 user 消息注入;边界前的多条转向合并为一条消息、一个事件;批后无剩余调用时按丢弃 0 交付,指令不丢。
- 后续指令在子 Agent 本应结束(assistant 无工具调用)时注入并继续。排队期收到的转向/后续指令合入初始任务。
- 停止只取消该子 Agent:待决工具补 "Cancelled.",待决审批被取消释放;父 Agent 收到**非错误**结果 "Sub-agent '<id>' was stopped (<reason>). Partial output: …",由父决定后续。
- 生命周期 Started(父发出)→ 仅在确实等名额时 Queued → Running → 每个 run 恰一次 Finished(在调度器 `finally` 中,含异常与父取消;父取消记为 Stopped "parent cancelled")。用量为本 run 增量,耗时从 spawn 起算(含排队)。
- `runs` 只保存运行中的 run,结束即移除并释放其 CTS 与父令牌注册;已结束 id 记入紧凑集合以继续返回 `AlreadyFinished`;`instances` 保留以支持按 id 续跑。

### Agent 会话

**Agent 会话(用户驱动的子会话;已实现,设计记录 2026-11-19)。** 用户在活动根会话下手动增加的子会话:存储形态与模型派生的子 Agent 相同(`parent_session_id` = 根、继承根的项目与 worktree、深度 1),区别只在**由谁驱动**。不变量:
- 不参与占用索引(`ux_sessions_occupancy` 只约束 `parent_session_id IS NULL`,不改索引);一个工作区仍只有一个活动根会话。
- 实例跑在**根会话的 `AgentHost`** 里,共用工具面、审批处理、取消表、树内并发名额与限速;每条用户消息是一轮,占一个树内名额,名额满时排队。深度 1,可再派生一层(深度 2 的 Agent 由 `ToolRegistry.Select` 剔除派生与控制工具)。
- 宿主入口(Core 侧已实现):`AgentHost.StartUserChildAsync` 建会话行(`child_driver='user'`、parent = 根会话)并跑首轮;`RunUserChildTurnAsync` 对已有实例跑一轮;`ResumeUserChildAsync` 从存储重建(幂等,复用 `SessionHistory.LoadAsync`);`StopUserChildren` 停掉名下所有用户会话的 run(排队/运行)。四者内部与 `SpawnAsync` 共用抽出的 `DriveAsync`(attach 进取消表 → `AgentSubAgentStarted`(带子代理模型的 `ProviderId`,App 侧子代理标签按它读自己账号的额度并按它自己的模型定价) → `Runner` → 恰一次 `AgentSubAgentFinished` 带本 run 的 `Usage`/`Elapsed`);分叉点只有:会话行写 `child_driver`、id 用 `session-<n>`、**不查 catalog/不做工具白名单与深度检查**(种类筛选由 App 负责,App 必须自己排除主 Agent、没有 `tools` 列表与含 git 写/`pr_*` 的定义)、`RunUserTurnAsync` 只接受本调度器登记为用户的同一实例。模型路径的 Started/Finished 语义未变。
- **与模型派生子 Agent 隔离**:调度器登记表按所有者区分,用户所有的实例不进模型可见名单,id 用独立命名空间;`spawn_agent` 按 id 续跑与 `subagent_steer`/`subagent_followup`/`subagent_stop` 对它一律按未知 id 处理。模型与它的唯一通道是下文的授权通信。
- 可选 Agent 种类:除主 Agent 外的内置 Agent 与带 `tools` 白名单的用户 Agent;白名单含 `git_branch`/`git_sync`/任一 `pr_*` 的不可选(只有根会话改 git)。初始上下文空白。
- 存储:`sessions` 新增 `child_driver TEXT NULL`(NULL = 模型派生,`'user'` = Agent 会话),直接写进建表脚本、**不做迁移**(产品未发布,旧库删库重建)。检查点与每轮指标按**子会话 id** 写,轮次独立从 1 编号;根与 Agent 会话回合时间重叠时双方的轮次 diff 互含(整树快照),是已知限制。
- 回合前校验与根一致(根仍 `Active`、无合并冲突、无工作区操作中);根的忙判定包含 Agent 会话的回合、排队消息与未送达插话。**在根标签按停止时连带停**:根回合、它名下正在跑的回合与已投递未开跑的消息一并取消(子会话标签自己的停止只停它自己)。根被接手、归档后:旧 root 降级为接手/继任会话名下的归档 Agent 会话,其 Agent 家族(仍 `Active` 的保持可用、归档/已移除的原样跟随)一并改挂——与「归档即换手」同一条规则(2026-02 用户确认);软删/移除会话沿用子会话级联。
- **归档即换手(`ISessionCatalog.ArchiveAndPromoteAsync(rootSessionId, promoteSessionId?, ct)`,一个 savepoint 事务,2026-11-20)**:归档 root 时把工作区交给一个 `child_driver='user'` 的 Active 子会话——指定的 `promoteSessionId`(必须是该 root 的 Active Agent 会话,否则 `InvalidOperationException`)或按 `last_active_ticks DESC, id` 最近的那个;root 先转 `Archived` 并改挂到继任者下、`child_driver='user'`(降级成归档的 Agent 会话),继任者清空 `parent_session_id`/`child_driver` 成为 root(状态保持 `Active`、仍占同一个 `occupancy_key`),root 其余 Agent 会话改挂到继任者下——Active 与归档的都要改挂:树只展开 root 的 Agent 会话行,留在降级行下的归档 Agent 会话会从树上消失(2026-11 用户复报);没有继任者时只归档 root 并返回 null。
  - root 不是 `Active` 时抛 `SessionNotActiveException`,子会话不是继任者时抛 `InvalidOperationException`,失败整体回滚。
  - **恢复即归位(`ActivateAsync`)**:`Archived`/`Deleted` → `Active`、清 `deleted_at`,先把自己名下的 Deleted 后代转 `Archived`、再做任何改挂(先后代恢复、后改挂,否则被改挂带走的已移除子会话会逃过恢复);**恢复永不因占用失败**——root 与 Agent 会话同规则,同键有 Active root 就改挂到它名下再激活(降级语义、`child_driver='user'`),没有就成为该分支的 root,谁成为 root 谁就把该分支旧库残留的非 Active root 行降级到自己名下(一根规则,`AdoptPriorRoots`,降级统一写 `status='Archived'`、清 `deleted_at`,家族含归档/已移除一并改挂);Agent 会话先找同键的 Active root——有就改挂到它名下再激活,没有就成为该分支的 root:它原来的父会话(是 root 时)改成它名下的 `child_driver='user'` 归档 Agent 会话(统一降级语义:Deleted 落 `Archived`、清 `deleted_at`——目录刚被恢复重建,“已移除”描述的是已不存在的状态),父会话的其它 Agent 会话(含归档与已移除)一并改挂到它名下;本身已是 root 时只激活、不改任何关系。`SetStatusAsync` 仍是原始状态写,不隐式换手。
- 用量按**实例**:Agent 会话每一轮的 token 取该轮 `AgentSubAgentFinished.Usage`(事件本就带每 run 的用量增量);**流式时长取该实例 `StreamElapsed` 的增量**,`Finished.Elapsed` 是整轮墙钟(含工具时间)不能当 `stream_ms`。不要复用只读根实例的 `ReadRootCounters`——否则会把根的 token 再记一遍。UI 一轮之外的实时读数经按实例的访问器;轮次指标不依赖它。
- 计划与步骤:`todo` 按会话 id,Agent 会话自然有独立清单;其步骤标完成不提交、门禁 `NotApplicable`(现有“有父会话即子 Agent”规则),改动由根会话的下一次步骤提交带走并由根的门禁把关;不参与 `pr_create` 的步骤闸门。

**授权通信(`message_session`)。** 根会话与其 Agent 会话之间唯一的通道,门禁是**用户亲手点名**:只有从输入框 `@` 弹层“会话”组选中的结构化提及才算授权(手打 `@名字` 文本与模型输出都不算),据此写入“发起会话 → 目标会话”这一对授权。授权**保留到目标会话结束**:目标归档/关闭、根离开 `Active` 或用户撤销时失效;只存在内存里,重启即清空;输入区有可撤销的授权标记。`message_session(target, message)` 是**纯异步投递**:经邮箱投递(对方忙则排队,立刻返回“已投递 #n”/“已排队 #n”并明说“未完成,你现在没有结果”),对方跑一轮;**不自动起轮**——否则 A 投递、B 回复、A 自动起轮再投递会形成无人看管的自动循环。投递立刻返回、不借名额;工具常驻工具集(缓存稳定),无授权时返回错误结果;目标只读时直接报错。目标只要有 `Active` 行就能收:**不要求本进程已经跑过它的回合**——App 接缝会在目标自己的标签里跑(必要时先打开标签),打不开它的标签、调度器里也没有它的实例时才以失败回执结束(`the session is no longer loaded`)——不在投递前拦下;投递时就没有 App 接缝则直接报错,不产生回执。排队等目标空闲有上限(30 分钟),超限按 `已停止(目标长时间忙)` 发回执,不让发起方无限等;目标空闲后、开跑前 Core 再读一次根与目标仍 `Active`,不再活动则不跑,按 `失败(会话已不再活动)` 发回执并撤销该授权。

**回程回执(定稿)。** 对方一轮结束——完成、失败、被停止或取消都算——向发起方写一条结构化回执:头行 `[会话回复 #n · 来自 <对方> · 状态:完成|失败(原因)|已停止(原因) · 任务:<发送时截取的短标签>]`;第二行 `改动: N 个文件 (+a/−b)`(取该轮检查点);第三行 `报告:` 对方最终回复原文,**上限 4000 字符 / 250 行**,超出转 artifact 并附 `完整报告: <id>`(复用 `read_artifact`)。**不带工具调用清单**(要细节时发起方再投递一次问,或用户点开子会话)。状态是必填:失败/停止也发回执,否则发起方只能把“没有消息”当成功。送达时机:发起方正在跑回合时于**工具批次边界**送达(同一轮可见);空闲时作为一条**未读**消息落进转录,不自动起轮。

**点名只约束“谁先开话头”**:子会话要主动发起,须在子会话里也点名过根;回程**永远自动**(投递即附带一次回程许可),不需要二次点名,但回程不是“再问回去”的通道。深度 2 不继承授权。投递本身不弹审批卡,对方回合内的工具调用照常审批。可通信对象只有根 ↔ 它的 Agent 会话。

**`write` 覆盖检查的读取记录。** 每个 `AgentInstance` 持有一份内存读取记录(`路径 → (长度, LastWriteTimeUtc)`,路径按 `WorkspacePath.Normalize` 归一,线程安全),`AgentLoop` 构造 `ToolContext` 时经新属性 `ToolContext.FileVersions`(`Forge.Abstractions` 的 `IFileVersionLedger`;null 时工具不检查,如脚本、插件桥)交给工具;规则见 forge-io「`write` 覆盖检查」。按实例而非会话 id:记录的是“这个 Agent 看到过哪个版本”,同名 Agent 重建后看到的历史可能已被压缩,不能沿用。记录不落盘,实例重建(重启、切换 Agent/模型)即清空。**不设上限、不淘汰**:只增的代价是内存按“读过多少文件”线性增长,而淘汰会让读过的文件突然要求重读;量级由 `tools/Forge.Bench` 实测给出。

### 会话、TODO 与门禁

**会话目录与恢复。** 会话元数据(项目、worktree、分支、父会话、Agent 种类、任务、归档状态、最后活动)是 `sessions` 表的列,`ISessionCatalog` 与 `ISessionStore` 由同一 `SqliteSessionStore` 实现。能力只经 `SessionCatalogs.From` 读取:`LoggingSessionStore` 总实现该接口,须读 `SupportsCatalog`,内层非目录时按无目录处理。子会话带 `ParentSessionId`/`AgentKind`/`Task` 并继承父会话的 worktree 与项目;每回合持久化后 `TouchAsync`,失败不影响循环。`ResumeRootAsync` 用与压缩器相同规则重建上下文(摘要视图 + 未摘要尾部),丢弃孤立首个工具结果与末尾无结果的工具回合。一个 `AgentHost` 对应一个主会话;`AgentScope` 为 AsyncLocal,名额池与 id 计数在实例内,多主会话可并发。

**`todo` 工具。** 按参数决定副作用(list 与 plan_summary_get 只读,其余写入),审批策略与只读并发分批看到单次调用的真实副作用;update/remove/promote 先确认条目属于 `ToolContext.SessionId`。清单按**分组**组织:add 带 `group`(按标题找,没有就新建)把条目收进该组的步骤,不带 `group` 则进会话的**待排散项队列**;`promote` 把一条散项升成以它命名的新分组(条目移出队列、分组继承它的 id 与标题),agent 接手用户排队的任务时用它,`after_id` 只在同一层内插入。同一时刻只有一步 in_progress(存储层保证,写成 in_progress 会把会话里别的 in_progress 退回 pending);`paused` 表示在等外部输入或依赖,永不触发步骤提交。update 接受 `commit_subject`/`summary`/`verification`;**状态变为 done 先过步骤完成门禁**(见下),通过后才经 `AgentHostOptions.StepCommitter`(`IStepCommitter`,契约在 `Forge.Abstractions`,由 App 实现,见 forge-app)提交这一步,结果(短 sha 或失败原因)写进工具结果,提交失败不回滚 done 状态。list 按分组输出(每组标题 + `done/total` 进度、组内步骤带 id/状态/note 摘要),散项列在末尾,agent 据此决定 promote 谁。`plan_summary` 操作读写会话级计划摘要(`plan_summaries` 表),供 PR 正文渲染。

**扩展编写工具(`Tools/IExtensionAuthoring.cs`)。** Core 只声明窄接口 `IExtensionAuthoring`(`ListAsync`/`SubmitAsync(ExtensionDraft)`/`DisableAsync`/`DeleteAsync`),由 App 用 HostClient 实现;Core 没有提交或批准能力,人工闸门只在 App 的实现里。五个工具 `extension_list`(只读)、`extension_create`/`extension_update`(同一个类,schema 字节一致;参数 `id`、`version`、`kind`(`workflow`/`plugin`)、`files`、`permissions`、`dependencies`)、`extension_disable`、`extension_delete`:参数全走 `JsonDocument` 本地校验,不合规返回错误结果且不调 authoring;提交调用阻塞到用户决定,结果是已注册的工具名、拒绝/撤卡原因或编译诊断;`ExtensionSubmitOutcome` 用 `Approved`/`Rejected`/`Withdrawn`/`Invalid` 四个值区分——`Withdrawn` 是卡片在用户决定前消失(会话关闭、应用退出),草稿保留待定、不是拒绝、对 agent 不算错误结果。`OperationCanceledException` 透传。它们不进内置 Agent 白名单,内置 Agent 的可缓存工具集不变。

**步骤完成门禁(`Tools/StepGate.cs`)。** 编排里唯一两个机械节点之一(另一个是 `pr_create` 全步骤 done,见 forge-workspace),其余编排环节都是提示引导。规则:**改了代码的步骤要标 done,必须先有 `request_review` 的 approve 结论与非空、且不是 `not verified` 的 verification;没改代码的步骤直接放行。**
- **证据来源是 Core 声明的窄接口**(照 `IReviewChangeSetSource`/`IExtensionAuthoring` 的形状,不进 `Forge.Abstractions`):`IStepChangeProbe.ProbeAsync(sessionId, workingDirectory, ct)` 返回 `StepChangeProbe(State, Detail, Tree)`,状态四值——`NotApplicable`(一般 scratch 会话、子 Agent 会话、目录已消失、非仓库:这些会话本来就不提交步骤)、`Clean`(含未跟踪的 `git status` 为空)、`Dirty`、`Unknown`(resolve 或 git 失败)。`Clean`/`Dirty` 另带 `Tree`:整个工作树(含未跟踪,遵守 ignore)的内容指纹,App 用 `GitCli.SnapshotTreeAsync`(临时 index + `write-tree`,不碰用户的 index/HEAD/refs)取得。App 用 `GitStepChangeProbe` 实现,复用 `StepCommitter.WorktreeOf` 的会话→worktree 解析,不写第二份。
- **规则按序**:`enabled` 返回 false 或没有 probe → 放行(没有 git 证据来源的宿主没有门禁);`NotApplicable` → 放行;`Unknown` → **失败关闭**并写出 git 的原因;**步骤有基线(`TodoItem.BaselineTree`)且当前 `Tree` 与之相等 → 放行**(工作树与步骤开始时完全一样,无论 git status 如何);`Clean` 且该步骤没有自己的 `CommitId` 且没有基线 → 放行(纯查证/分析步骤);其余(`Dirty`,`Clean` 但步骤已有提交,或 `Clean` 但内容与基线不同——代码在步骤自己的提交之外被提交了)要 `StepReviewLedger` 里该步骤的 `Approve`,**且该结论记录的 `Tree` 等于当前 `Tree`**(评审之后内容变过、或评审没取到指纹都判过期,要求对同一 step_id 重新 `request_review`),且有效 verification 非空、不是 `not verified`(大小写不敏感、容忍结尾句点)。有效 verification = 本次 update 传的值,没传则取已存的值(与存储层"null 保留旧值"同义)。拒绝是 `StepGateDecision`(Reason + NextStep,模型可执行英文),不抛异常;只上抛取消,probe 抛其它异常按 `Unknown` 处理。
- **门禁在写状态之前判**:`TodoTool.UpdateAsync` 在 `store.UpdateAsync` 之前调 `CheckAsync`,拒绝时条目状态不变、不提交。判据 `finishes`(status=done 且「原本不是 done 或上次提交失败」)与步骤提交条件同一个,所以重试提交也重新过门禁。
- **结论落 per-host ledger**:`StepReviewLedger` 按 (session, step) 记 `request_review` 解析出的 verdict、该次评审的 commit 与**评审开始前取得的工作树指纹**(`RequestReviewTool` 持 `IStepChangeProbe`,在拉起 Reviewer 之前取,所以结论绑定的是 Reviewer 看到的内容);只在带 `step_id` 的评审记录,被 stop、启动失败两条早退不记(它们没有结论);变更集为空时不拉起 Reviewer,但取到了指纹就记一条 Approve(工作树内容与基线不同而变更集为空——改了又改回——否则门禁永远要评审、步骤永远做不完)。`AgentHost` 在 `Todos` 非 null 时建一个,`TodoTool` 与 `RequestReviewTool` 共享同一实例。
  - **持久化**:内存是运行时权威;`AgentHostOptions.StepReviewStore`(`IStepReviewStore`,App 用 `StepReviewLedgerStore` 落 `reviews.db` 的 `step_review_verdicts`)非空时,每次 `Record`/`Forget` 经单一串行队列后写,`EnsureLoadedAsync(session)` 在门禁检查、`request_review` 记录与 `TodoTool` 的 `Forget` 之前把该会话的已存条目装入内存(本宿主已记/已忘的优先,装载排在已入队的写之后所以不会读到将被删的行;装载失败按“无条目”处理并在下次重试,写失败只意味着重启后重评)。无 store 时重启后不知道任何结论,每个步骤要重新评审一次(安全侧)。步骤离开 done 时 `Forget`,所以修复必须重新评审。
  - **基线**:`TodoTool` 在步骤首次转 `in_progress` 且尚无基线时调 `StepGate.CaptureBaselineAsync` 取一次指纹写进 `todos.baseline_tree`(`TodoStepFields.BaselineTree`);门禁关闭、无 probe、`NotApplicable`/`Unknown` 时不取,该步骤按无基线的旧规则判;重开的步骤保留原基线;换手复制 todos 时不带基线。剩余限制:没走过 `in_progress` 的步骤没有基线;评审之后构建/测试往工作树写入未被 ignore 的文件也会改变指纹使结论过期。
- **自动修复循环由 `ReviewFixPolicy(AutoFix, MaxRounds)` 约束**(`AgentHostOptions.ReviewFix`,每次评审结果读取):ledger 另记每步“已完成的非 approve 评审”轮数(`Rounds`,approve 或 `Forget` 清零);`request_review` 的结果据此给下一步——自动修复开且未到上限:把本次全部发现合并一轮修完再评审;到上限:停止修复、不再评审、用 `ask_user` 问用户;自动修复关:只向用户汇报并等待。不带 `step_id` 的评审不计轮。`AgentHostOptions.Reviews` 为空即无 `request_review`,也就没有步骤门禁。
- **没有评审工具就没有门禁**:`AgentHostOptions.Reviews` 为 null 时 gate 不带 probe——那种宿主拿不到 approve,装门禁只会让改代码的步骤永远做不完。`request_review` 只由允许清单决定是否提供,与 `spawn_agent` 的开关无关。
- **剩余限制,不掩盖**:① 没走过 `in_progress` 的步骤没有基线,仍按 git status 与自己的提交判(`bash` 里自己 `git commit` 的代码会被当成「没改代码」);② 内容指纹来自 `git add --all` 到临时 index 后的 `write-tree`:子模块只记 gitlink,子模块内的未提交改动不进指纹;指纹取不到(锁住或超长路径的未跟踪文件)时探针仍答 Clean/Dirty 但 `Tree` 为空——不需要指纹的步骤照常放行,有改动的步骤要的 approve 因无指纹不被接受;③ `request_review` 带 `paths`(只看部分内容)时不记指纹,该结论不被门禁接受;④ 步骤交错进行时(A 开始→B 开始并提交→A 回来未改就 done),A 的旧基线与当前不同,会要求一次多余的评审;⑤ `Dirty` 的工作树即使与基线相同也要评审(基线之前就有的未提交改动会被该步骤的提交带走);⑥ 评审之后构建/测试写入未被 ignore 的文件会使结论过期,需重新评审;⑦ 台账写是后台串行写,进程在写落库前被杀会丢最后一条,表现为重启后多评审一次。

**评审工具(`request_review`,`Tools/ReviewPackage.cs` + `Tools/ReviewPackageText.cs` + `Tools/RequestReviewTool.cs` + `Tools/RequestReviewVerdict.cs` + `Tools/ReviewConclusion.cs`)。** Core 不引 git,变更集的 git 侧由 App 实现窄接口 `IReviewChangeSetSource`(照 `IExtensionAuthoring` 的形状),经 `AgentHostOptions.Reviews` 交给宿主;没有实现就没有这个工具(与没有 `ITodoStore` 就没有 `todo` 同理)。结论的存放同理由 App 实现窄接口 `IReviewConclusionStore`(`AddAsync`/`ListAsync`),经 `AgentHostOptions.ReviewConclusions` 交给宿主;没有实现就没有落库,工具结果逐字不变。契约:`ReviewChangeSetRequest`(worktree、`Base`、`Commit`、`Paths`)、`ReviewChangedFile`/`ReviewCommit`、`ReviewChangeSet`(分支、HEAD、基准、文件、提交、diff 文本、是否被截断)、`ReviewChangeSetResult`;预期失败是模型可执行的文本(`Failed`),只有取消才抛。
- **两种目标。** 带 `step_id` 时变更集是该步骤自己的那次提交(`commit^..commit`,根提交对空树),意图取步骤的标题/摘要/验证;**步骤没有 CommitId(如提交失败但已 done)时回退为默认分支加未提交与未跟踪的树,并在意图里自述「this step has no commit of its own, so the change set is the branch and the uncommitted tree」**(`RequestReviewTool.WithCommitNote`),不静默冒充提交评审;不带 `step_id` 时是 `base`(默认仓库默认分支)加未提交与未跟踪的树,此时 `intent` 必填。`step_id` 与 `base` 同时给是错误——一个是提交评审、一个是范围评审,静默忽略其中一个会让调用方以为审了别的东西;已知参数拿到字符串以外的 JSON 类型也是错误,不回落默认(否则 `{"step_id": 5}` 会变成一次看起来像步骤评审的分支评审)。
- **包(`ReviewPackageText.Render`)** 一次 `StringBuilder` 渲染:意图、步骤记录(或“不是某一个步骤”一句)、分支/HEAD/基准/计数/提交清单/文件清单、diff、焦点,最后只给一句“按你的指令报告并以结论行收尾”的指针——报告规则只写在 Reviewer 提示词里,两处各写一份必然漂移。上限由 `ReviewPackageLimits` 给出(`Render` 的可选参数,缺省为 `Default`;`AgentHostOptions.ReviewLimits` 是 `Func<ReviewPackageLimits>`,`RequestReviewTool` 每次评审调用一次,所以设置改动对下一次评审生效、无需重启;Core 不依赖 App):diff 字符(默认 48000)、**提交清单行数(默认 50)与文件清单行数(默认 200)各自独立**(文件才是评审的对象,长提交历史不能把它们挤掉,两者各自声明还剩下多少)、每个文本字段(意图/步骤文字/焦点/提交标题/限定路径列表)字符数(默认 2000);App 侧由 `git` 设置分区调这四项(见 App SPEC)。Reviewer 原文 8000 字符与未跟踪文件 8000 字节探测仍是内部常量(后者与 git 自己的二进制判定窗口一致);被截断时写明按 `git_diff`(paths、stat_only)读其余。`Render` 同时返回“diff 不完整”标志,供工具在结果头部提示调用方。包固定写明“diff 是 git 对已跟踪文件的输出,列成 added 却没有自己 diff 段落的文件是未跟踪的,读它再判断”:分支模式的 `git diff` 文本不含未跟踪文件内容,文件清单里却有它们。**收窄过的变更集必须自述**:`ReviewChangeSet.Paths` 非空时包里写 `narrowed to: a, b (only these paths were read; …)`——Reviewer 分不出“别处没改”与“只让我看这些路径”,否则会把被过滤掉的调用点/测试报成“漏改”。同理 `CommitsTruncated` 为真时计数写成 `at least N commits`,不把源的上限当成总数。
- **结论(`RequestReviewVerdict`)** 只认 Reviewer 文本里最后一条 `verdict:` 行:大小写不敏感,容忍 `_`、Markdown 强调、列表/标题标记与行尾标点;没有结论行就是 `Unstructured`,**绝不从缺失推断通过**。工具结果固定为:结论行、`reviewed: <短 sha> · <n 个文件>[ · base <短 sha>]`(HEAD 未知时写 `reviewed: unknown HEAD`)、diff 被截断时一行提示、下一步动作、`---`、Reviewer 原文(上限 8000 字符)、`[agent_id: …]`。变更集为空(无提交、无文件、无 diff)时不启动 Reviewer,返回非错误结果说明没有可评审的内容——把空评审当通过是最坏的失败形态。
- **被停止的评审没有结论。** `SubAgentOutcome.Stopped` 为真(用户或父 Agent 用 `sub_agent_stop` 停了 Reviewer)时工具返回错误结果“评审在完成前被停止,没有结论,不是通过”,后面附部分原文与 `[agent_id: …]`,**不解析结论行**——Reviewer 可能在读完 diff 前就写过一行 `verdict: approve`。父回合被取消时 Reviewer 一起取消,取消照常上抛。
- **结论会落库(W17b)。** 评审结束经 `IReviewConclusionStore` 记一行:sessionId、工作目录、`step_id`(步骤评审,base 落该提交的第一父提交,根提交为 null)或范围评审的 `base`(merge-base)、HEAD、结论态与 Reviewer 原文(按 8000 字符截断)。结论态是 `Unstructured`/`Approve`/`RequestChanges`/`Stopped`/`Failed` 五个值(数值固定,见下),`Stopped` 与 `Failed` 都**不是通过**:前者是评审被打断、后者是评审没能开始或 Reviewer 一个字都没回。只记真正走到 Reviewer 的那次请求——参数错误、变更集读不出、变更集为空(没起 Reviewer)都不留行,因为那不算一次评审,留行会让历史读起来像评审过。写库失败只吞掉、不改变工具结果:已经跑完的评审必须交给调用方。Core 没有日志设施,故这条失败是静默的(宿主侧的实现可以自己记日志)。
- **结论态的数值不可改。** `ReviewConclusionOutcome` 的 `0..4` 是 `reviews.db` 的 `outcome` 列,旧版本写下的行由新版本按数值读回;增删必须追加在末尾,重排会让历史被静默曲解(两侧都有钉住数值的测试)。
- **`git_diff` 的退路必须带基准。** 裸 `git_diff` 比较的是仓库默认分支,与自定义 `base` 或单个提交的评审不是同一个变更集;所以包里截断提示与文件清单尾注写成 `git_diff {"base": "<sha>"[, "stat_only": true]}`(sha 经 JSON 转义)。`ReviewChangeSet.SingleCommit` 为真时补一句“这个范围还含此提交之后的所有提交与未提交树,只判断本提交改了什么”;根提交(无基准)`git_diff` 无法单独取出,改为指向 `read`。`narrowed to:` 的路径列表同样按 2000 字符字段上限截断;无换行的 diff 截断不劈开代理对。
- **它不阻断任何东西,也不替代自评。** `pr_create` 的第三道闸门仍只认本会话在这个 HEAD 上读过完整 `git_diff`;Reviewer 的 `git_diff` 记在它自己的子会话上(`GitReviewLedger` 按 sessionId + worktree 分键),所以一次评审既不会让闸门通过,也不会被当成作者自评。结论不进计划面板;一次评审的结论按 W17b 落结论库(有 `IReviewConclusionStore` 时),带 `step_id` 的还会记进宿主的 `StepReviewLedger` 供步骤完成门禁读取(见上)。副作用是 `ReadOnly`(只读 git + 只读子 Agent),与 `spawn_agent` 同性质;等子 Agent 时用 `AgentScope.Current.Permit.Detach(ct)` 交还名额(与 `SpawnAgentTool` 同一写法),否则树内并发上限填满时会死锁。`BuiltInAgents.Reviewer` 因此多一个只读 `git_diff`(白名单只是名字,Core 不因此依赖 git),仍无任何写工具,可缓存工具集只对 Reviewer 自己变化。
- **`ToolContext.Tools` 与 `ToolContext.YieldSlot`(`AgentLoop.ExecuteToolsAsync` 每批工具调用填一次)。** `Tools` 是调用者此刻实际持有的工具(白名单叠加用户开关),取自批次开始时的快照;`bash` 与 git 的门禁按它判断,不看角色。`YieldSlot` 让会等别的 Agent 的工具(`bash` 排工作区锁)交还并行名额,释放时用**整轮令牌**取回(与 `Detach(ct)` 的调用令牌不同:调用被单独取消时,取回不会因令牌已取消而失败,Agent 不会永久少占名额)。`ExtensionHostClient` 的 `host.tool_call` 由所属 run 的上下文派生,嵌套调用同样带着调用者的工具面。

### 工具热加载与审批路径

**工具热加载(`ToolSurface`)。** `AgentHost.ReplaceExternalTools` 发布不可变快照 `ToolSet(版本, 注册表)`(`Volatile` 读、只在发布时加锁、版本单调);同名时内置优先、外部同名静默丢弃。循环在每次模型请求前和每次查工具前取当前快照:版本未变复用同一 `Definitions` 数组实例(零分配,提示词缓存不变),变了才按白名单/深度重建,所以批准后的下一次模型请求(同一回合内也是)就能看到新工具;代价是该会话提示词缓存失效一次。工具开关也在这里:`AgentHost.SetDisabledTools`(或 `AgentHostOptions.DisabledTools` 初值)给 `ToolSurface` 一个禁用名集合,发布快照时内置与外部工具都按它过滤;集合与现值相同(`SetEquals`)时什么都不发布,版本不变、缓存不破;外部替换后禁用集合仍然生效,后注册的同名工具也被挡住。被禁用的工具既不出现在请求里,回合中途被关掉后模型再调用会得到错误结果。`AgentHost.AllTools` 列出全部内置与外部工具(含已禁用的),供 App 的工具目录使用。`AgentLoopServices.Tools` 的类型是 `ToolSurface`。

**扩展工具仅限根 Agent。** `extension_*` 五个工具只对 `Depth == 0` 的 Agent 可见、可调用:`ToolRegistry.Select` 对子 Agent 一律剔除(无论 `ToolNames` 为 null 还是显式列出),派发查找走同一份选择结果;工具自身在 `ToolContext.AgentDepth > 0` 时也拒绝并返回错误结果。

**审批:策略只求值,边界由调用方算。** `DefaultApprovalPolicy` 按序判定:精确工具名规则 → `SideEffect == ReadOnly` 免问 → 全局档位 `All` 免问 → `ApprovalRequest.Scope == InsideWorkspace` 免问 → 否则 `Ask`。档位是 `Func<ToolPermissionMode>`(每次求值现读,改设置不用重启)。`Scope` 不是策略算的:调用方(`AgentLoop`、`ExtensionHostClient` 的 `host.tool_call`)用 `ToolScopes.Of` 写好——工具可实现 `IToolCallScope` 给出精确答案,否则退回参数扫描;`ApprovalRequest.Scope` 是必填参数(无默认值),漏算一处编译不过,而不是静默按域内放行。路径比较按 OS 大小写规则、不探测文件系统(macOS 大小写不敏感的写法会被判域外 → fail-closed 到 `Ask`)。被允许的域外调用由调用方在 `ToolContext.OutOfWorkspaceAllowed` 上标记,工具层才可越过工作区。

### 存储与迁移

**存储。** SQLite 存会话、消息、摘要、全文索引(FTS5);JSONL 为事件日志,供崩溃恢复;Artifact 内容存文件。会话消息/摘要写入走单写线程批量提交,返回 seq 时已落盘;提交后的取消不会让调用方误判为失败(工作台各 store 另用每 store 连接 + 信号量串行,见「工作台存储」)。摘要行只追加不改写,被压缩的原文仍可检索和取回。每次压缩追加**一条**会话级(layer 2)摘要,范围从上一份会话摘要(或旧阶段摘要、或首条原文)的起点到最后一条被摘要的消息;视图 = 覆盖 `to_seq` 最宽的会话摘要 + 未被它覆盖的旧阶段摘要 + 原文尾部(`SummaryLayers.Build`,同 `to_seq` 时取较新 `at`)。旧版本写下的阶段(layer 1)行仍可读:恢复时照旧展示,下次压缩把它们的正文并入“上一份摘要”,新会话摘要覆盖后即不再出现,所以旧行无需迁移。切点由 `SummaryLayers.PlanCut` 定:只落在下标 ≥ 1 的 user 或 assistant 消息上(不落在工具结果上),所以压缩不会拆开工具调用与其结果。

**forge.db 版本阶梯。** v5 的归档与通知写入在同一个事务里:一次注入的失败(通知写入缺列)回滚到 v4、两条会话都还是 Active、`occupancy_key` 也随 DDL 一起消失,去掉障碍后同一步可重跑并爬到 v9。
  - `PRAGMA user_version` 是唯一权威(0 = 无标记旧库,1 = 工作台表,2 = `todos` 加 `commit_subject`/`summary`/`verification`/`commit_id`/`commit_error` 五列 + 新表 `plan_summaries(session_id PK, text, updated_at)`;3 = 一次性规范化历史时间戳,并为每个时间戳列装写守卫;4 = `todo_groups(id PK, session_id, title, order_index, updated_at)` 与 `todos.group_id`/`todos.origin` 两列,旧行的 `group_id` 保持 NULL 即待排区、`origin` 默认 `agent`,无需回写;5–10 见下两段;11 = `plan_summary_revisions(id 自增 PK, session_id, text, replaced_at)` + `ix_plan_summary_revisions_session(session_id, id DESC)` 索引,无回填,写守卫覆盖新表的 `replaced_at`;12 = `sessions.title_source TEXT`,无回填——NULL 就是“仍是创建默认标题”,见「会话标题来源」;13 = `turn_metrics.effort TEXT NULL`,每轮跑的推理档(小写 `off`…`max`,与 `settings.json`/`composer.json` 同一套名字),旧行 NULL 即未知、不回填;14 = 版本号直跳、**无结构步骤**:`last_seq INTEGER NULL` 直接改入 V1 建表脚本(转录重放的精确配对键,见 forge-app),case 13 只是把版本号爬到 14(fresh 库逐级爬梯必经此级;未投产不设 ALTER——旧 13 号开发库无声跳到 14 后缺列,由记录器形状探针把特性旁路,重建库即恢复全部功能)。
  - `turn_metrics` 的表由 V1 建表脚本直带、表本身不进阶梯;新列 13 号走步骤只在表存在时 `ALTER`(表不存在就什么都不做,整条特性仍旁路,见「工作台存储」),14 号起未投产即不再积累升级步骤——新列直接改建表脚本、版本号照跳。侧表 `sub_agent_state`(子 Agent 重启续跑用)不进阶梯也不改版本号:它的 `CREATE TABLE IF NOT EXISTS` 在每次打开库时幂等执行(`StorageSchema.EnsureSubAgentStateAsync`),旧开发库无声得到该表;`finished_at` 登记进 `TimestampColumns` 并由这个创建点自己装规范化与写守卫(`EnsureOwnedTimestampsAsync`,与 `artifacts` 同一做法——阶梯装守卫时它还不存在)。
  - 每步在一个 `BEGIN IMMEDIATE` 事务内执行并同事务写入版本号,事务内重读版本防止多进程重复迁移;读到更高版本立即 `InvalidDataException`,不降级不写库。
  - 旧会话回填 Active 与最后活动 = 创建时间(严格解析,失败整步回滚)。
  - 排序用 `last_active_ticks`(UTC ticks)而非 ISO 文本。
  - `messages_fts` 与阶梯解耦(可用性取决于 SQLite 编译选项),首次建表与回填同事务,单条坏消息只跳过(派生数据)。
  - 建过 `messages_fts` 的库被无 FTS5 的构建打开时,同事务补建 `messages_text` 回退表并回填(已存在则只补缺失行),写与搜索都落到回退表。
  - 换回能读 `messages_fts` 的构建时,把降级期写入 `messages` 但缺于索引的行回填进 `messages_fts`(`NOT EXISTS` 去重,以 `messages_text` 存在为门槛,无缺失即跳过),使降级期写入的消息恢复可搜;降级 purge 留下的孤儿索引行则按 `messages` 反向修剪(见「写入不连坐」)。

**会话标题来源。** `sessions.title_source`(`derived`/`agent`/`user`,NULL = 仍是创建默认标题)记录“谁起的这个名字”,决定它还能不能被改。`ISessionCatalog.TryTitleAsync(sessionId, title, source)` 是唯一的替换入口,优先级写在一条条件 UPDATE 里而不是读-改-写:`derived` 只覆盖 NULL,`agent` 覆盖 NULL 或 `derived`,两个人起的名字(对话框改名的 `RenameAsync`,它恒写 `user`)永不被覆盖——所以模型和派生互不打架、用户改名永远赢,且并发的“还以为没命名”不可能盖掉刚落下的名字。返回 false 表示一行没写(标题仍是别人定的),调用方据此决定回什么话。`set_title` 工具(`Agents/SetTitleTool.cs`)只改这一列:名字由宿主经 `AgentHostOptions.RenameSession` 写(没有该委派时工具根本不出现,工具段因此仍是逐字节稳定的),工具本身不碰文件、不碰 git——worktree 分支与目录用的是创建时的标题,改名绝不让 checkout 从评审或提交脚下挪走。何时写得进去由宿主决定,不由工具定:App 只在一个会话的第一轮(创建它的那条用户消息开始的那一轮)打开写入面,此后(包括关闭后重开的会话)一律拒绝,所以一个会话的名字只会被模型写一次。空名或没有字母数字的名字返回错误结果;长度不归工具管——`RenameSession` 回的是宿主规整后真正落库的名字(App 的 `SessionTitles` 把它截成一个标签长度),工具用这个名字回话。工具声明(名字、描述、schema)是静态常量,留在固定工具段里,不随会话或轮次变化。它不写工作区,所以 App 的会话策略放行(见 Forge.App「会话标题」)。

**计划摘要修订。** `SetPlanSummaryAsync` 在同一个事务里把被替换掉的旧文本记成一条修订(新文本与旧文本相同则不记),每会话只留最新 `PlanRevisionLimit`(50)条;`ListPlanRevisionsAsync(sessionId, limit)` 按 id 倒序返回(limit 夹到 1..50);复制会话时一并复制,清理会话时一并删除;面板只读历史,恢复旧版本不走 UI 写路径。

**forge.db 时间戳由库自己把关。** 时间戳列只存 `ToString("O")` 形状的文本(带 offset、固定 7 位小数),读取严格、不兼容其它写法。进程外工具直接改库曾写入 6 位小数的 ISO 文本,一行坏值就让整个 `forge.db` 读不出来(左栏项目树空白、添加项目报错)。因此 3 号步骤先就地规范化:凡文本自带 `Z` 或 `±hh:mm`、能解析出唯一时刻的,改写为规范形状(时刻与 offset 不变);无 offset 或无法解析的值原样保留,由严格读取指名报错——迁移不猜时间。规范化之后给 8 张表 10 个时间戳列各建 `BEFORE INSERT`/`BEFORE UPDATE` 触发器(`NEW.col IS NOT NULL AND NOT (NEW.col GLOB <规范形状>)` 时 `RAISE(ABORT)`),任何写者(本程序、外部脚本、旧构建)都写不进非规范文本,可空列(`turn_checkpoints.ended_at`)仍允许 NULL。守卫只建在**步骤执行时已存在**的表上(3 号步骤在 `todo_groups` 之前跑,否则老库升到 3 会因缺表直接失败),4 号步骤建出新表后按同一份清单把全量守卫重装一遍(`DROP TRIGGER IF EXISTS` + `CREATE`,幂等),现在是 15 张表 23 个列(`turn_metrics.started_at`/`ended_at` 随 V1 建表 + `TimestampColumns` 登记,新库在 3 号步骤装上守卫)。新增时间戳列必须登记进 `StorageSchema.TimestampColumns`,否则既不会被规范化也没有守卫。`artifacts` 由 `FileArtifactStore` 在阶梯外自建,其 `at` 列的守卫与规范化由 owner 在初始化时经 `StorageSchema.EnsureOwnedTimestampsAsync` 装(同一份登记、同一套规范化,不新增版本步)。审批库 `approvals.db` 不在此列,仍按其 SPEC 只接受规范文本。

**工作台存储。** `projects` / `todo_groups` / `todos` / `ui_state` / `turn_checkpoints` / `turn_metrics` 与会话同库:每个 store 一条长连接 + `SemaphoreSlim` 串行 + 每写一个 `BEGIN IMMEDIATE`(deferred 读后升级会静默丢更新),读用池化短连接。
  - **关闭必须先让在飞的写收尾**:关闭只置位并不阻塞地试取写闸,拿到才关那条长连接,拿不到就交给最后一次写在自己的 `finally` 里关(闸本身就故意不 `Dispose`)。
  - 不这样做的代价是真实的:关闭线程关连接时会把写线程正持有的 `BEGIN IMMEDIATE` 事务在它脚下回滚,Microsoft.Data.Sqlite 里同一个事务被两个线程各完成一次——抛 `SqliteTransaction.Complete` 的 `NullReferenceException`(一次全量测试里的偶发失败:后台保存比分它写进的 store 活得久);顺带把“关 store 要等正在写的那个”这种阻塞留给调用方线程的问题也去掉了。
  - `turn_metrics`(每轮指标,PK `(session_id, turn)`,失败轮 token 列全 NULL、未上报不补 0;`stream_ms INTEGER NOT NULL` 记模型流式生成时长——首个输出增量→流结束、回合内多次调用累加,零输出为 0,**绝不回退墙钟**,写入前四舍五入到毫秒、负值钳 0,失败轮也必带;`effort TEXT NULL` 记该轮实际跑的推理档,根轮取回合开始时的快照、Agent 会话轮取该轮 definition,子 Agent 轮与加列之前的旧行留 NULL 即未知;`last_seq INTEGER NULL` 记该轮末一条消息的 seq——重放配对的唯一精确键,根/子/delivery 三路都在轮末源处取实例读数且只有同实例且有新写入才名位置(轮中重建实例从库载入旧历史,不名位置),绝不用事后 `MAX(seq)`;零写入轮 NULL 即无位置、不配对不猜)的表由 V1 建表脚本直带(与 `turn_checkpoints` 同款先例),表本身不进阶梯,新列 13 号走步骤补;14 号起未投产即直接改建表脚本,阶梯只留空级跳版本号(缺列旧开发库由探针旁路,重建库即恢复);App 侧记录器一次性表存在性探测**以 `stream_ms`、`effort`、`last_seq` 三列齐全为形状标记**,探测不到就整条特性旁路为 no-op(读作 false/空/0,不是错误)——缺列旧表只会被旁路,不会让每次写入失败;`PurgeAsync` 对它先做表存在性检查再删,缺表跳过(不能放进无条件删表清单,否则旧库删会话直接崩)。
  - 指标行由 App 级后台记录器 best-effort 写入(有界通道 + 单消费者 + 短 busy timeout,永不抛、可丢行),绝不进会话历史批队列;换手/分叉不复制(指标属于原会话)。
  - `ITodoStore.Changed` 在提交后、写锁外触发,订阅者异常被吞,一次写入只抬一次。
  - 路径唯一性靠 `root_key`(Windows 大写)唯一索引,General 项目由部分唯一索引保证单例,再次注册即改指向。
  - TODO 序号**层内**致密:`(session_id, group_id)` 是一层(`group_id` 为 NULL 就是待排区),插入、删除、`MoveAsync` 只重排本层,层与层互不牵连;分组顺序在会话内致密且只增不减(没有分组删除入口,面板侧也不提供)。
  - 同会话只能有一步行进中:写事务里把某条目写成 `InProgress` 时,同事务把该会话其余 `InProgress` 改回 `Pending`。
  - 检查点以 (session, turn) 为主键,已完成轮次不被再次 Begin 改写。
  - `synchronous=NORMAL`:这些是体验数据,丢失不影响安全(审批库用 FULL)。

**会话生命周期与工作区占用(v5–v9)。** 状态 `Active`/`Archived`/`Deleted`。占用键 `occupancy_key` = `OccupancyKey.Compute(worktree_path, workspace_path)`(`Path.GetFullPath` + 去尾分隔符,Windows 再大写;与 `WorkspacePath.Normalize` 同语义),由存储层在每次写路径时计算,调用方不传;部分唯一索引 `ux_sessions_occupancy`(`status='Active' AND parent_session_id IS NULL`)保证一个工作区同一时刻一个活动根会话,冲突映射为 `WorktreeOccupiedException(HolderSessionId)`。App 内把会话置 `Active` 只走条件插入、`ActivateAsync`、`HandoverAsync`;`SetStatusAsync` 是只受索引把关的原始写入。v5 迁移逐行在 C# 回填键,按键去重(保留 `last_active_ticks` 最大者,平局按 id 大者),被归档者写进 `storage_notices`(`dedupe-archived`);v6 `deleted_at`,v7 `projects.closed_at`(软关闭,`AddAsync` 同路径复活原 id),v8 `fork_of_branch`/`fork_base_sha`,v9 `lineage_root_id`(回填为自身)/`origin_kind`(回填 `cold`)/`prefix_fingerprint`(继承两列曾在这一步加入,v15 删掉);v15 删 `inherits_from_session_id`/`inherits_up_to_seq` 与 `ix_sessions_inherits`,并把 `origin_kind='full'` 折回 `cold`——完整继承整个能力取消。当前版本 15。

**写入不连坐。** 会话写队列一个批次一个 `BEGIN IMMEDIATE` 事务,**可能业务冲突的操作**(`UsesSavepoint`,如占用冲突)包在 `SAVEPOINT forge_op` 里:单个操作冲突只回滚到自己的保存点并以异常交还调用方,同批其它操作照常提交;其余操作(Append/PutSummary 等)不包保存点,失败仍连坐整批。
  - `HandoverAsync` 单事务:先条件归档旧会话(受影响行 ≠ 1 抛 `SessionNotActiveException`),再条件插入新会话(继承 `fork_*`/`lineage_root_id`,同事务写 `InitialMessages`,按开关复制 todos / todo_groups / plan_summaries)或恢复目标会话(只改状态),接手会话成为 root 后,旧 root 与该分支旧库残留的非 Active root 行一律降级为它名下的归档 Agent 会话、其 Agent 家族(含归档/已移除)一并改挂(新建路径 `AdoptPriorRoots` 直接扫,恢复路径随 `ActivateAsync` 同规则)——旧 root 留着 root 行会让「历史」组列出多个 root 会话,违背分支一根规则(2026-02 用户复报);任一步失败整体回滚。
  - `SoftDeleteAsync` 级联子会话;`PurgeAsync` 删 `Archived` / `Deleted` 会话(拒绝 `Active`,先归档)及其子会话在 forge.db 内所有以 `session_id` 为键的行(两张搜索表 `messages_fts`/`messages_text` 各按存在性清理;无 FTS5 模块的构建只清 `messages_text`,它清不到的 `messages_fts` 残留行由下一个能读该索引的构建按 `messages` 反向修剪),没有任何继承守卫(完整继承取消后不存在继承者)。常规查询排除 `Deleted`,`ListHistoryAsync` 包含之。
  - `HealMissingDirectoriesAsync` 把目录缺失而项目根仍在的 `Active`/`Archived` 根会话(级联)置 `Deleted`,写 `heal-deleted` 通知(只列根 id);`ReadNoticesAsync`/`AcknowledgeNoticesAsync` 供 App 启动时通知一次。

**会话上下文与提供商缓存。** `SessionHistory.LoadAsync` 只读本会话自己的“摘要视图 + 尾部”(与 `ResumeRootAsync` 同一裁剪函数);上下文没有跨会话引用——接手/分叉只有摘要或空白两种起步,摘要经 `HandoverSummary.GenerateAsync`(交接专用提示词,不复用压缩器)生成一条用户消息。压缩只摘要本会话自己的消息。每次合成后计算 system + tools 的 `PromptFingerprint`,仅变化时写回 `prefix_fingerprint`。`ChatRequest.CacheRoutingKey` = `lineage_root_id`:OpenAI 兼容按 `ProviderOptions.SendPromptCacheKey`(`settings.json` 的 `provider.sendPromptCacheKey`,默认关)发 `prompt_cache_key`,DeepSeek 一律不发;DeepSeek 用量 `prompt_cache_hit_tokens` 计缓存读取(与 `cached_tokens` 同时出现时以后者为准)。每会话首个请求的缓存用量连同 `origin_kind`、提供商、模型写一条诊断日志。`AgentOptions.CacheTtl`(`provider.promptCacheTtl`)经 `AgentLoop.ResolveCacheTtl` 填进 `CacheHint.Ttl`,Anthropic 适配器遵循它(`5m`/`1h` 直用、`auto`/null 按 `CacheTtlPromotionGap` 自动升档,见「官方身份」)。

### 上下文压缩

**压缩触发(`CompactionPolicy`)。** 以实例自身模型的上下文窗口 W 为基准,不再用绝对 token 阈值。
  - 预留输出 R 取请求真正携带的输出上限:`WireMaxOutputTokens` 有值时就取它(不夹 0.2W——夹了正是让触发线越过真实输入上限的原因,`0.85·(W−R)` 必须始终低于 `W−wireMax`),否则取 min(maxOut, ⌊0.2W⌋)(maxOut 未知时取 ⌊0.2W⌋),再一律抬到至少 min(4096, W/2);可用预算 U = W − R,`R ≥ W`(输出上限填满整个窗口)时没有可用预算,不自动压缩(溢出兜底仍在)。
  - 触发线 ⌊U·TriggerRatio⌋(默认 0.85),同回合内的紧急线 ⌊U·EmergencyRatio⌋(默认 0.95),目标 max(1, ⌊U·TargetRatio⌋)(默认 0.5),再生长量 ⌊0.1U⌋。
  - 两条线的分工:回合的**第一个**模型请求前用触发线判断(此时上下文是上一回合留下的,压掉不影响本回合证据);同一回合后续请求只用紧急线,除非接近窗口否则不压——所以一个回合不会把自己正在收集的工具结果压掉。
  - `Compaction.Validate()` 要求 0 < target < trigger < emergency ≤ 1。
  - maxOut 取 `model.MaxOutputTokens`,否则取解析器(解析器在 App 侧按「模型目录 `limit.output` → provider 上报 → `modelMeta[模型].maxOutputTokens` → 提供商级 `providers[].maxOutputTokens`」解析;全未知则 R = ⌊0.2W⌋);窗口、maxOut 与线上的真实输出上限都由 `CompactionPolicy` 的解析器给出(App 接模型目录),解析器抛异常按未知处理。
  - `WireMaxOutputTokens` 回答的是「这个请求实际会写进 `max_tokens` 的数字」:只有 Anthropic 协议总是带一个(它自己的 8192 默认,或 thinking 预算达到它时抬到的 `预算 + 4096`,见 `Providers/OutputCeiling`),兼容与 Responses 协议不 pin 就不发,所以那里没有图数、按 maxOut 那条走。
  - 适配器与预算共用 `OutputCeiling.Wire` 这一份规则,任何一方改动都必须让另一方跟上——否则预留与实际参数会再次分岔。
  - 当前上下文 C:模型 key 与历史版本都与上次请求一致时,C = 上次真实 `InputTokens`(各适配器已含缓存读写,不再重复相加)+ 此后新增消息的估算;否则 C = 历史全量估算 + 稳定前缀 + 本实例可见工具定义的估算(每个定义按名称、描述、参数 schema 计数另加 8 token;按工具集发布版本缓存在实例上,版本不变时不重算、不分配)。
  - 调摘要器之前先算不可压缩下界 `floor = 继承前缀 + 稳定前缀 + 工具定义 + min(keepRecent, 自有消息)`(摘要按 0 计:它总能被重写得更小,把它算成下界会让本可奏效的实例提前放弃);`floor ≥ 本次实际使用的线`(新回合 τ、同回合 ε)时不调摘要器,直接标记 Stalled 并发一次 `AgentCompactionStalled(Reason=Irreducible)`——那种情况下摘要不可能把 C 降到线下。
  - `floor > 目标` 但低于线时照常压缩,目标达不到不是放弃的理由。
  - 迟滞:一次摘要后 C 仍不低于触发线即为“无效尝试”,下一次要等实例自有(非继承)内容再长 0.1U;连续两次无效后标记 Stalled 并发一次 `AgentCompactionStalled(Reason=Ineffective)`,直到手动压缩、换模型或一次把 C 带回线下的裁剪。
  - 两个原因分开:Irreducible 与 Ineffective 都能由换模型、手动压缩或一次把 C 带回线下的裁剪解除;Irreducible 另可在记录的判定线抬高或工具定义 token 变小时自动重试,Ineffective 不因这两项数值变化自动重试。
  - 阻断时的线与工具定义 token 记在实例上,只有它们真的变了才重算,所以被阻断的实例不会每轮重发通知。
  - 策略关闭、压缩器为 `NoopCompactor`、W 未知时两级都不动;Stalled 只停摘要级,裁剪级照跑(见「循环」段的裁剪级:先裁后摘要,仍超线才调摘要器);子 Agent 各按自己的模型窗口计算。
  - 保留量 keepRecent 默认 max(1, min(20000, ⌊0.2U⌋));策略上的 `KeepRecentTokens` 有值时取它(夹到 [min(1000, U), U/2]),经 `CompactionRequest` 传给压缩器;自动与溢出路径再把它夹到不超过目标的一半,保证仍能压到目标。
  - 切分规则:目标原文量 goal = max(keepRecent, 目标 − 继承前缀 − 现有摘要),保留不超过 goal 的最长合法后缀;一条都放不下时只留最新的合法切点之后的部分。
  - 消息按整条保留,所以单条消息跨越边界时实际保留量可能低于 keepRecent。
  - `AgentHost` 构造时 `Compaction.Validate()` 拒绝非法比例。
  - `AgentOptions.Compaction` 只是初始策略:`AgentHost.SetCompactionPolicy(policy)` 先校验(非法抛异常、原策略保留),再以 volatile 写替换生效策略(`AgentHost.CompactionPolicy` 读出),根与全部子 Agent 共用;循环在每次模型请求前、溢出兜底与手动压缩时各读一次,所以正在跑的回合从下一次请求起按新策略判断。
  - 替换不改请求内容、不重置迟滞与上次提示词大小,提示词缓存不受影响。
  - **上下文读数**:每次模型请求落盘后、每次自动/溢出/手动压缩(`AgentCompacted` 之后)各发一条 `AgentContextMeasured(AgentId, ContextTokens, UsableTokens, TriggerTokens)`;同一条事件还带 `Usage` = 该实例此刻的累计用量(在该请求自身的用量加到计数器之后取值;裁剪/压缩这类不新增开销的测量报当时已有的数,读者差分即为零),外壳据此按自己的基线差分,所以用量统计能在一个回合内逐请求更新而不是等回合结束;`AgentTurnCompleted.Usage` 仍是同一计数器的轮末读数,两者按差分衔接、不重复计——C 即上文的当前上下文,U 与触发线按当时生效的策略计算,W 未知时两者为 null;手动压缩只在传了 sink 且确有压缩时发。

**上下文溢出兜底。** `ProviderErrors.IsContextOverflow` 判定溢出(显式 Data 标记或 413;否则按短语匹配,但可重试错误与 429 一律不算)。仅当流里尚未收到任何内容时:强制压缩一次(目标 min(C·TargetRatio, 预算目标)),再重发一次(keepRecent 按预算计算,W 未知时取 20000,同样夹到目标一半以内);第二次溢出或压缩没腾出空间时以原错误失败。W 未知时也生效;策略关闭或 Noop 压缩器时不生效。

**手动压缩。** `AgentHost.CompactAsync(instance, sink, [instructions,] ct)` 以目标 1 压缩、无视触发线与 `Enabled` 开关,并重置迟滞;保留 keepRecent(W 未知时 20000)的近期原文,不再只留最新一条。`instructions` 非空时作为 `

Additional focus: <text>` 追加到每个摘要请求的指令末尾。摘要被拒时抛 `CompactionFailedException`,由调用方显示失败。调用方须保证同一实例没有回合在跑。无会话存储时压缩不生效。

**压缩内容(`SummaryCompactor`)。** 系统提示词是 `compaction.summarizer` 槽位(默认 `SummarizerPrompt`),规定结构化检查点格式:Goal / Constraints & Preferences / Progress(Done / In Progress / Blocked)/ Key Decisions / Next Steps / Critical Context。
  - 待摘要的消息序列化成一条 user 消息:`<conversation>` 内逐条写 `[User]:`、`[Assistant]:`、`[Assistant tool calls]: name(k=v, …)`、`[Tool result]:`(错误结果标 `(error)`),thinking 块不写,工具结果截到 2000 字符并加 `[… N chars truncated]`,图片与文件只写占位。
  - 首次用初始指令;已有摘要时用 `<previous-summary>` 包住上一份摘要正文(会话摘要在前、旧阶段摘要随后,文件标签已剥离)并用 UPDATE 指令增量更新。
  - 待摘要范围分批:每个请求的对话文本预算 = (W − min(摘要输出上限, W/2))·0.6 个 token 按 4 字符/token 折算,再扣系统提示词、上一份摘要、指令与焦点的长度,夹在 [4000, 400000] 字符之间(W 未知时取 400000);以“一条 user/assistant 消息 + 其后的工具结果”为不可拆单元贪心装批,工具调用与其结果绝不分开;单个单元超过预算时从中间截断并插入 `[… N chars omitted …]`。
  - 第一批用初始(或已有摘要时的 UPDATE)指令,之后每批都用 UPDATE 指令、以上一批的输出作 `<previous-summary>`,最后一批的输出即历史摘要;不再有静默丢弃。
  - 文件追踪(`CompactionFiles`):`read` 记路径**与它实际显示的行范围**——取回答里 `Shown lines X–Y`(被截断)或首行 `path: N lines`(从 `offset` 起算),**不取请求的 limit**(工具还会按字节截);回答失败时这条不记,没有回答时退回请求的窗口。
  - 同一文件多次读的行范围相交或相邻就合并(`1-420` + `421-840` → `1-840`,`offset` 无 `limit` 记 `500-`),整文件读(`path` 无 `offset`/`limit`)吞掉所有范围并渲染成裸路径。
  - 修改类(`edit`/`write`/`delete`/`copy`/`move`)只记路径,读过的文件被改后只出现在 modified 下。
  - 两份清单都与上一份摘要里的同名清单合并(区间累加,不重复),再按字符硬预算渲染后追加在存储的摘要末尾(读取 4000、修改 8000,标签与换行算在内):本次压缩新增的路径排在前面、其余按 Ordinal(没有时间戳可依:清单只有渲染结果能活过一轮,「最新」只能表示「本轮看见」),超出预算时保留放得下的整行并补一行 `… N more`,单条路径比整块还长时中间省略(保住文件名与行范围);从存储重建时,提示行与含 `…` 的行不再当作路径,否则清单会比它来源的那一份更长;`AddEntry` 按“最后一个冒号后是纯区间列表”才把它当范围,所以 `D:\…` 的盘符不被误拆。
  - 拆分回合:切点落在一个仍在进行的长回合内部的 assistant 消息上时,该回合切点前的前缀单独用 TURN-PREFIX 指令摘要(Original Request / Progress So Far / Context Needed to Continue),与历史摘要并行请求,合并为 `<历史摘要>

---

**Turn Context (split turn):**

<前缀摘要>`(无更早历史时只有后半)。前缀超出预算时同样分批,后续批用 TURN-PREFIX 的续写指令(同样三段格式、带 `<previous-summary>`)。新摘要至多含一段 Turn Context:上一份摘要带旧的 Turn Context 段时,UPDATE 指令追加"把该段并入常规各节、不再复现标题与分隔线"的要求;没有新历史但出现新拆分时,单独发一次 `(no new messages)` 的 UPDATE 请求完成合并;模型仍复现旧段时,代码把旧标题降级为 `**Earlier turn context:**`、去掉其前的 `---`,只有本次前缀带正式标题。任何一批失败即整次压缩失败(另一条链经联动取消立即停止),只在全部成功后写入唯一一条会话层摘要。摘要请求不带工具、Thinking 关闭、带 SessionId,输出上限取模型自身值(pin 未知时取策略解析的能力值)与 8192(前缀 4096)中较小者。回复以 MaxTokens 截断、发起工具调用、提供商报错、没有完成消息或正文为空时拒绝该摘要,抛 `CompactionFailedException`,什么也不写;取消照常传播。自动与溢出路径把拒绝当作"没腾出空间"(计入无效尝试,不让回合失败)。

### 工具结果、取消、图片与附件

**工具结果多块。** `ToolResult`/`ToolResultBlock` 持有 `Blocks`(只允许 `TextBlock`/`ImageBlock`,构造与 `with` 都校验;传入后视为不可变),`(string, IsError)` 便捷构造保留;`Content` 是拉平文本(单文本块即原串,多块按行拼接,图片为占位文案),相等按元素比较。持久化:`tool_result` 恒写拉平后的 `content`,仅当块不是“恰好一个文本块”时多写 `blocks` 数组;旧行与旧读者照常工作,不升 schema。`AgentLoop` 的溢出归档只处理文本、图片块原样保留;`TokenCounter` 对工具结果里的每张图按附件图同一口径(1600 token)估算。**适配器已按各协议发送工具结果图片**:`IncludeToolImages` 为真且结果含图片时,Anthropic 与 Responses 写图片块数组,Chat Completions 在连续 tool 消息之后追加一条携图 user 消息;`pr_screenshots` 与 MCP 工具会产出图片块。关闭“把工具结果图片发给模型”或模型输入能力不匹配时只写 `Content` 占位文本,存储与界面显示不变。

**工具进度。** `ToolContext.Progress`(`Action<string>?`)收到的是长任务实时输出的**最新尾部快照**(不是增量);`AgentLoop` 为每次调用派生独立 context,把它转成 `AgentToolProgress(AgentId, Call, Tail)`,夹在 `AgentToolStarted`/`Finished` 之间,不落库。监听者必须不阻塞(生产者在持锁时调用它以保证有序)。目前只有 `bash` 产出。**App 侧已消费(wave W8)**:`SessionWorkspace` 在工具执行线程上直接把快照写进该调用的实时条目(不派发、不落库),工具行的实时尾部与资源检查器在 UI 线程按自己的 tick 读最新快照;开始/结束仍走 coalescer,详见 `Forge.App/SPEC.md`「资源检查器」。

**单个工具调用的取消(W8 follow-up)。** `AgentLoop` 给每次工具调用派生一个链到回合令牌的 `CancellationTokenSource`,登记在 `ToolCallCancellations`(`ConcurrentDictionary<(AgentId, CallId), CTS>`,整棵树——根与所有子 Agent——共用一份,因为它们共用一个 `AgentLoop`),在 `AgentToolStarted` 发出**之前**登记、调用返回后在 `finally` 里注销并释放(`TryRemove(KeyValuePair)`:只摘掉自己那一项,重复 id 的另一条调用不受影响)。
  - `AgentHost.CancelToolCall(agentId, callId)` 取消且只取消该调用:取消走 `CancelAsync`(回调在线程池上跑,UI 线程不被进程树终止阻塞),调用已结束、id 未知或已请求过取消都返回 `false`,与调用自身返回竞争时吞掉 `ObjectDisposedException` 并返回 `false`。
  - 工具因此抛出的 `OperationCanceledException`(回合令牌未取消)被循环转成错误结果 `AgentLoop.CancelledByUserResult`("Cancelled by the user: this tool call was stopped before it finished."),照常写回历史,模型继续;回合取消仍取消全部调用并照旧传播。
  - 不理会令牌、照常返回的工具保留它的真实结果。
  - **同一批并发执行的只读调用各算各的**：`RunCallsAsync` 仍然是全员并发,每条任务一完成就把结果写进该调用自己的槽位(取消 → `CancelledByUserResult`,异常 → 它自己的错误结果),`Task.WhenAll` 只当汇合点、不是失败通道,所以不存在「一条取消把其余调用已产出的结果一起丢掉」;整轮取消由调用方在全部结果落位之后 `ThrowIfCancellationRequested()` 结束,落盘的工具消息因此是一条条真实结局而不是四个「已取消」。
  - 不用 `Task.WhenAny`/`IAsyncEnumerable`:批次必须**全部**有结果才能拼一条工具消息(循环的契约是一次 assistant 消息只配一条工具消息),边完成边消费换不来可观察的东西,却要多一份按完成顺序对回调用下标的记账。
  - `bash` 等命令工具经 `ProcessRunner` 在令牌触发时 `Kill(entireProcessTree: true)`;`spawn_agent` 的子 Agent run 链到该调用的令牌,所以停掉这次调用即取消子 Agent(状态 Stopped "parent cancelled")。
  - 超长结果的转存仍用回合令牌。

**图片输入。** `ImageBlock(MediaType, Data)` 是用户消息的内容块(base64),`AgentHost.RunAsync(instance, ChatMessage, ct)` 接收带图片的用户消息。三个适配器按各自协议编码(Anthropic `image` source、OpenAI 兼容 `image_url` data URL、Responses `input_image`);`MessageJson` 原样持久化。`TokenCounter` 每张图按固定 1600 token 估算。压缩器给摘要模型的永远只是占位文本 `[image: <type>, <n> KB]`(文件为 `[file: <name>, <type>, <n> KB]`),原图与文件内容不进摘要请求。

**工具结果的图片。** `ToolResult`/`ToolResultBlock` 是块列表,不只是字符串(契约的一半由 W0 定:`Blocks` 只允许 `TextBlock`/`ImageBlock` 并在 `init` 与 `with` 时校验,`Content` 是整份结果的文本视图、非文本块各占一行占位如 `[image: image/png, 12 KB]`,`Equals`/`GetHashCode` 手写为逐块比较)。本波在它上面补了两件:`Text` 是工具自己的话(只含 `TextBlock`,逐行,没有占位)给**自己把图片送上线路**的适配器——后者若还用 `Content` 就把同一张图说了两遍;`HasImages` 判断结果里有没有图而不建列表。只有一层嵌套:工具结果里放 `TextBlock`/`ImageBlock`,不嵌套工具结果。

三个适配器各自的形状,以及谁来决定发不发:`ChatRequest.IncludeToolImages`(默认 true)由 `AgentLoop.BuildRequest` 每请求算一次——`AgentOptions.SendToolImagesToModel`(一个 `Func<bool>`,每次请求现问,所以设置开关下一次回合就生效,不必重建宿主)与 `AgentOptions.ModelInputs`(目录里该模型的输入种类;未知视为可收图,与输入框对用户附件的规则一致)。为 false 时**所有适配器都只写 `Content`**,图片仍在存下的消息里,界面照常显示,改回开关只影响下一次请求。

- **Anthropic**:`tool_result` 的 `content` 本来就收块数组。有图且有允许时写数组(`text`(取 `Text`)在前,随后每张 `image`),否则写字符串 `Content`——纯文本结果的线路字节与以前完全一致,提示词缓存的前缀不受影响。
- **Responses**:`function_call_output.output` 收 `input_text`/`input_image` 数组。同上,有图写数组(文本取 `Text`),否则写字符串。
- **Chat Completions**:`tool` 角色的 `content` 只能是文本(协议如此,不是选择)。所以结果文本(带占位,`Content`)照常写进 `tool` 消息,图片随后作为**一条用户消息**发出:`content` 数组 = 一行说明 `Images returned by the tool calls above.` + 每张 `image_url` data URL。多张图只发一条,顺序与 `ToolResultBlock` 的次序一致。协议要求一次 assistant 调用的所有 `tool` 消息紧跟其后,所以连续的 `tool` 消息按一组写:整组写完之后才追加这一条携图用户消息,绝不插在两条 `tool` 消息之间。

溢出与计费:文本超阈值移入 Artifact 时只搬文本,图片跟着结果留下;`TokenCounter` 把工具结果里的图片按与顶层 `ImageBlock` 相同的 1600 token 估算;压缩器给摘要模型的仍是占位文本。

**文件附件。** `FileBlock(MediaType, Data, Name?)` 承载 PDF/音频/视频(base64)。各协议能承载的种类是静态表:`AnthropicProvider.WireInputs`/`OpenAiCompatibleProvider.WireInputs`/`OpenAiResponsesProvider.WireInputs`,按媒体类型细判用 `ProviderAttachments.CanCarry(kind|protocol, mediaType)`。编码:Anthropic PDF → `document` base64 块;OpenAI 兼容 PDF → `{"type":"file","file":{filename,file_data:data URL}}`、wav/mp3 → `input_audio`;Responses PDF → `input_file`(filename + file_data)。线路形状不存在的组合(Anthropic/Responses 的音视频、Chat Completions 的其他音频与视频)在**构造请求体时**抛不可重试的 `ProviderException`,请求不会发出,绝不静默丢弃——此句约束**用户附件的构造期**。**助手历史消息中的 FileBlock** 属用户历史行为,适配器无法承载时允许丢弃,但必须保留语义占位且不影响轮次执行(决策 4)。`MessageJson` 以 `"type":"file"`(media_type、data、可选 name)持久化;搜索/全文/压缩输入里只出现占位 `[file: <名称或类型>, <n> KB]`;`TokenCounter` 按每个已开始的 KB 250 token 估算(PDF 经验值,音视频同用)。文本文件不是 `FileBlock`:由 `TextAttachment` 内联成带 `File: <名称>` 头和自适应反引号围栏的 `TextBlock`。

**带附件的插话。** `AgentInstance.Interject(IReadOnlyList<ContentBlock>)` 与 `Interject(string)` 并存;`TakeInterjections()` 返回 `Interjection`(`Content`、键入文本 `Text`、附件 `Attachments`)。送达时所有待送插话合并为**一条**用户消息:键入文本以空行(`"\n\n"`)连接成一个文本块在前,随后按顺序是全部附件;每条插话各发一个 `AgentInterjected(AgentId, Text, At, Attachments)`:`Text` 是键入文本(纯附件时为空),`Attachments` 是该插话的附件列表(无附件时为空列表),元素就是调用方交进来的同一批块实例,UI 据此描述纯附件插话并按实例对上自己的小片。

### 交互、Web 与网络

**`ask_user`。** `AskUserTool` 仅在 `AgentHostOptions.QuestionHandler` 存在时注册:模型给出问题与可选项,回合挂起等待处理器返回答案(选项或自由文本),取消即返回取消结果。它是只读工具,默认策略放行。多选题可带 `minSelect`/`maxSelect`(整数,1 ≤ min ≤ max ≤ 选项数,允许自由输入时再加 1,因自由文本算一次选择;单选题带这两个参数会报错,提示需要 `multiSelect`)。用户可跳过单个问题,结果里该题输出 `<header>: (skipped)`;全部跳过时 UI 不提交,而是用“忽略”(Declined)。用户也可对单题发起“讨论”(原因可选:不够清楚/与问题无关/需要更多上下文,另可附说明):结果里该题输出 `<header>: (discussion requested, reason: …) <说明>`,并在末尾追加指令——模型须先用普通文本与用户讨论、澄清,同一条消息里不得再调用 `ask_user`,之后只对未解决的问题再次调用 `ask_user`(已有答案沿用)。

**Web 工具(`Tools/Web/`)。** `WebFetchTool` 只取 http/https,默认拒绝回环、私网、链路本地地址(`AllowPrivateNetwork` 每次调用现读),重定向手动跟随且每跳重查(最多 5 跳),正文上限 5 MB,超时 30 秒,HTML 经 `HtmlToText` 转成纯文本后按长度截断。
  - 私网判定以连接时为准:App 给它的 `HttpClient` 来自 `NetworkStack.CreateHandler`,`AddressFilter` 在 `ConnectCallback` 里检查实际拨号的地址,拒绝时抛 `NetworkAddressRefusedException`,工具把它报成与预检相同的私网拒绝,因此 DNS rebinding 无效;请求前的解析预检只在请求走代理时保留(经 `ConnectsDirectly` 判断,代理连接看不到目标地址),直连时跳过以免重复解析。
  - `WebSearchTool` 走可替换的 `IWebSearchBackend`(Exa、Tavily、DeepSeek、Brave、SearXNG),每次调用经工厂现取后端,未配置时返回“请让用户在 Web 工具设置里配置”的失败结果;App 只在有可用后端时注册它,并在各会话回合开始时按设置增删(见 forge-app 的 Web 工具设置),工厂现取后端是回合中途后端被移除时的兜底。
  - Exa 是唯一无需任何密钥就能用的后端,有两条路径:配置了密钥时 POST `https://api.exa.ai/search`(`x-api-key`,结构化 JSON,`contents.text.maxCharacters` 取正文);无密钥时 POST Exa 公共 MCP 端点 `https://mcp.exa.ai/mcp`,单次 JSON-RPC `tools/call`(`web_search_exa`,`objective` 由查询合成),免 `initialize`、免 session,回复是 SSE,正文是 `Title:/URL:/Published:/Author:/Highlights:` 文本区块,按 `---` 分行取标题、URL 与 highlights(每条截断 600 字符)——这个无密钥路径是“开箱可用”的唯一实现,依赖对方文本格式,格式变更时只会返回无结果而非静默错误。
  - DeepSeek 走 `POST https://api.deepseek.com/v1/tools/search`(Bearer,`need_content` 恒 false),结果取长 `summary`、空时回退 `snippet`。两者由 App 作为外部工具注入,默认策略为“询问”。

**网络(`Net/`)。** `NetworkStack` 实现 `INetworkStack`,是全部出站 HTTP 的唯一来源(提供商、模型列表、models.dev、MCP HTTP 传输、Web 工具;`HostClient` 的本机 IPC 不经过它)。
  - `Apply` 原子替换设置并触发 `SettingsChanged`,之后新请求即按新设置走;`CreateHandler` 返回 `SwappableNetworkHandler`(包一层可替换的内层 `SocketsHttpHandler`,`Proxy` 与 `ConnectCallback` 仍在每次连接时读当前设置):`Apply` 只在设置确实不同(逐字段比较,列表按元素)时递增路由版本,各包装器在 `SettingsChanged` 上换一个新内层(同一份 `NetworkHandlerOptions`,`AddressFilter` 等原样带上),所以已建的客户端无需重建,旧连接池不会被新请求复用,设置相同的 `Apply` 不换。
  - 请求路径无锁:volatile 读当前代、CAS 递增在途计数、经 `HttpMessageInvoker.SendAsync` 发送、返回(收到响应头)时递减;换代在锁内先发布新代再退役旧代,旧代在途计数归零时由最后一个请求(没有在途请求时由退役方)恰好释放一次,不用定时器也不设宽限期,在途请求不会遇到已释放的内层,退役的内层不泄漏;已收到响应头、仍在读取的响应体不计入在途(释放 `SocketsHttpHandler` 只关空闲连接,使用中的连接用完才关)。
  - 包装器释放时释放当前内层并取消订阅 `SettingsChanged`(事件不持有已释放的包装器),之后的请求抛 `ObjectDisposedException`。
  - `PooledConnectionLifetime` 仍为 5 分钟,只用于刷新 DNS TTL。
  - 代理:`system` 用 `HttpClient.DefaultProxy`,`none` 直连,`manual` 用 `proxyUrl`(http/https/socks4/socks4a/socks5,userinfo 转为凭据并从 URL 去掉),`proxyBypass` 支持 `*`、`*.域名`/`.域名`(含自身)、精确主机与 IP,回环与 `localhost` 恒直连;manual URL 无效时回退到 system。
  - DNS:`system` 走 `Dns.GetHostAddressesAsync`;`servers` 是手写的 RFC 1035 客户端(`DnsWire` 编解码,A 与 AAAA 并发查询,UDP 先发、TC 置位改 TCP,按服务器顺序逐个尝试,超时或 SERVFAIL/REFUSED 换下一台,NXDOMAIN 立即失败;校验事务 id 与问题段,忽略伪造或错位的报文);`doh` 按 RFC 8484 POST `application/dns-message`(id 为 0,只允许 https,回复上限 64 KB)。
  - 两者共用按 TTL 过期的 `DnsCache`(默认容量 256、maxTtl 1 小时;TTL 0 不缓存,失败不缓存,容量满时先清过期项再逐出最早到期项)。
  - 解析失败抛 `DnsResolutionException`(`SocketException` 子类,带主机名)。
  - 连接只在直连时解析并过滤:`ConnectCallback` 的端点等于请求 URI 的主机与端口且不是 CONNECT 时视为直连,先解析(IP 字面量不解析),逐个地址过 `NetworkHandlerOptions.AddressFilter`,全部被拒抛 `NetworkAddressRefusedException`(`Find` 沿内层异常链查找);走代理时端点是代理本身,目标由代理解析,不做过滤。
  - `CreateResolver(candidate)` 按候选设置造一次性解析器,供设置页“测试”使用而不应用。
  - `NetworkSettingsRules` 是设置校验的唯一实现(服务器、DoH URL、代理 URL、绕过模式、`Problems`),App 的存储与设置页都调用它。

### 提供商、限速与元数据

**提供商。** OpenAI 兼容、Anthropic 与 OpenAI Responses(`POST {baseUrl}/responses`,只在提供商模型只走 Responses 时使用:Meta 与 ChatGPT 订阅;请求用 `instructions` 承载系统提示、`input` 承载消息与工具调用、`reasoning.effort` 承载推理档、`store:false`;流按 `output_text.delta`/`function_call_arguments.delta`/`response.completed` 解析,七档里 `Max` 与 OpenAI 兼容一样夹到 `xhigh`)。
  - 重试只发生在首个事件之前,阶梯与可见性见 W15 一段;`HttpClient` 必须是无限超时,改由每次读取的空闲超时(默认 120 秒)判定停滞。
  - 每次模型调用的 `MessageCompleted` 必带 `StreamElapsed`(首个输出增量→流结束,适配器读循环里以 `Stopwatch.GetTimestamp` 对采集,零分配;零输出为 0;一次性降级重发同样产出——它仍走流式路径,被拒的那次没有任何增量也不产出完成;缺失即适配层缺陷,不存在「未知」,绝不回退墙钟)。
  - `AgentLoop` 把它逐次累加进 `AgentInstance.StreamElapsed`(与 `Usage` 同型实例累计,`AgentTurnCompleted` 带累计值,轮份额 = 起止快照差分),并把该轮的墙钟 `StartedAt`(取得运行槽之后起表,排队等待不进任何一轮的耗时)/`EndedAt` 一并带上——App 侧**子代理**每轮分隔行的耗时与子会话 `turn_metrics` 行的起止列来自它们(根仍用自己的发送墙钟);存储层落 `turn_metrics.stream_ms`。
  - 事件另带 **`LastSeq`**(该轮末一条消息的 seq,`PersistAsync` 刚 await 完的落库值,无存储为 0)——它是转录重放的精确配对键(见 forge-app),不是事后查库的 `MAX(seq)`。
  - `ProviderConfig.Headers` 经 `ProviderHeaders.Apply` 写入每个请求(覆盖同名头),值中的 `{session}` 取 `ChatRequest.SessionId`(`AgentLoop` 填本会话 id,不是 lineage 根);无会话时该头不发,模型列表请求同样不发。
  - `ProviderException` 的 Retry-After 暂存于 `Exception.Data`(契约缺口,未确认是否正式化)。
  - 非 2xx 的响应体由 `ProviderErrors.FromStatus` 经 `ProviderErrorBody.Describe` 读成一句人话(`error.message` → `message` → 字符串 `error` → 折成一行的原文,再补 `(param: …)`/`(use: …)`/非泛型 `(type: …)`),原始体存 `Exception.Data[ProviderErrors.ErrorBodyKey]`;短语规则(上下文溢出、两处一次性降级)一律用 `ProviderErrors.MatchText`(可读句 + 原始体)匹配,因为字段名常常只出现在 `error.param` 里。
  - OpenAI 兼容适配器在写 wire 消息时合并相邻的同角色 `user`/`assistant` 消息:文本按序拼接、段落间空一行,`tool_calls` 保持顺序;`tool` 角色消息不合并,`system` 保持现状(系统提示词在最前,历史中的 system 消息按原位置逐条写出)。
  - 内部历史不变,序列化结果对同一历史字节稳定。

**按提供商限速(`Providers/RateLimit*`,W15)。** 每个请求在离开之前先从一个**按提供商**的本地预算里取额度,取不到就等,而不是让服务端用 429 告诉你。预算有两个维度(请求/分钟、令牌/分钟)加一个自动化恢复间隔;数值在一份进程级策略里(`ProviderRateLimitPolicy`,由外壳经 `ProviderRateLimiter.Shared.Publish` 在启动、偏好变更与读盘后发布),桶按 `IChatProvider.Id` 分开——两家提供商是两个账号、两个窗口,共用一个桶会让慢的那家拖住无关的那家)。默认宽松(600 请求/分钟、4,000,000 令牌/分钟;某一维为 0 表示不限),它的职责不是节流,而是防跑飞:子 Agent 扇出、自动化重试、另一个会话同时恢复,都能在一秒内烧掉一整分钟的额度。全局数字之外,策略还带**按提供商 id 的覆盖**(`Overrides`/`ProviderRateLimitLimits`,每一维可单独留空继承全局、填 0 表示这家不限):它不是“每家都要填一张表”,而是给全局数字表达不了的那一种情况——自建网关、小额度套餐这类只有运营者自己知道更紧的端点;报得出自己限额的提供商不需要条目;**生效上限取“手填/全局”与“响应头所报”两者的较小者**(0 = 该侧不设限):响应头只会收紧、永不放宽手填的更严上限,所报数字存在桶旁(`ProviderBuckets.ReportedRequests/ReportedTokens`),`Publish` 重配桶时按同一条取小规则重算,不会被覆盖的 50 冲掉已报的 20。覆盖会随 `Publish` 就地重配已存在的桶,所以加了、改了、删了都在下一次请求生效。

实现要同时成立三件事:热路径零分配、不读时钟、只用一次原子操作。因此 `RateLimitBucket` 的发布状态不是“每次授权分配一个不可变对象再 CAS”,而是两个 64 位字(固定点余额 + 上次补充时间戳):补充协议是“先 CAS 时间戳赢下一个窗口,再按窗口加信用;输者按新时间戳重算自己更晚的窗口”,信用夹到容量上限,长时间空转只是把桶填满;余额在低水位以上时不读时钟(信用只与窗口和余额有关,推迟不会算错,低水位以下才补),成功路径是一次 CAS(`Take` 是 CAS 循环而不是“先减后回补”:后者在被拒的瞬间余额为负,并发的另一个请求会因为一笔只是借来的信用被拒,`Adjust` 的夹取还会多退)。`ExceedsCapacity`(单请求大于整个预算,等待也装不下,典型是长上下文)与 `NeverRefills`(空了且没有补充率或没有重置时间)一律放行;`ExceedsCapacity` 同时把桶清空(请求会被发出、提供商会计它,桶不能继续看起来是满的),且不计入“已收取”(见下),`Wait` 连同 retryTime 交回调用方。这三条由 `tools/Forge.Bench` 的 `ratelimit.acquire.*` 钉住:0 字节/次、无争用 p50 < 100 ns、8 线程共享一个桶时每次都授权,并与“同一语义、一把锁”的基线在同一循环里互测。

获取点在 `RetryingChatProvider`:它是最外层中间件,看得见每一次尝试,所以重试也计费。
  - 估算用 `ITokenCounter`(系统提示 + 消息 + 工具定义),应答回来后 `Settle` 用 `InputTokens + OutputTokens` 校正(TPM 口径计两侧,2026-11-15 修正:估算仍在请求前、只算输入——应答之前输出不存在——应答之后补扣输出;`OutputTokens` 缺失为 0 时退回只按输入,与旧版一致)。
  - `InputTokens` 仍是**完整计量输入**,三个适配器都把缓存读写含在里面(Anthropic 求和,OpenAI 形状直接传 `prompt_tokens`/`input_tokens`),`SessionUsageMath.BilledInputTokens` 正是因为这一点才减缓存得计费部分;把缓存再加一遍会让长缓存会话按两倍速吃预算。
  - `Acquire` 的重载经 `out chargedTokens` 报告实际从令牌桶收取的数(被拒、`ExceedsCapacity`、关闭都是 0),`Settle(chargedTokens, actualTokens)` 只对收取过的部分校正——没付过的估算不会在 Settle 里被退成凭空的信用。
  - 估算本身在限速关闭(或没有限流器)时整个跳过,不白数一遍提示。
  - `Observe` 用响应头校准:`ProviderRateLimit.Throttle` 给出上限、剩余与重置,桶直接被改成服务端的数字。
  - 读两类头的地方有两个:`AnthropicRateLimits`(`anthropic-ratelimit-requests-*`/`-tokens-*`,已存在)与新加的 `RateLimitHeaders`(通用 `x-ratelimit-limit|remaining|reset-{requests,tokens}`,OpenAI 和大多数兼容服务器每个响应都发,`-reset-*` 的时长方言 `1s`/`6m0s`/`1h2m3s`/`250ms` 与裸秒数都收);`HttpChatProvider.ReadRateLimit` 的缺省实现就是后者,所以 OpenAI 兼容提供商不再只能靠被拒绝来学习自己的限额,`AnthropicProvider` 覆盖时先读自己的家族、读不到再回落通用家族(OpenAI 形状的代理会转发 Anthropic 正文,却发通用头)。
  - 校准把服务端的数字写成桶的余额,并且**同时推进补充时间戳**:服务端的 `remaining` 已经包含它自己那个窗口到应答时刻的补充,不推进时间戳的话,随后一次低水位补充会把这段窗口再叠一遍、夹到容量,校准立刻被抹掉。
  - 剩余为 0 且有重置时间就 hold 到那一刻(逐次补充只会再被拒一次);被拒的用量窗(`IsRejected`)同样给 hold,因为它一次覆盖请求与令牌两个维度;一次读数里两个维度分别算出 hold 后才统一应用(有余额的维度不该取消另一维度要的 hold)。
  - **hold 有出口**:单次 hold 上限 1 小时(`Hold.MaxHold`),任何一次 `Publish`(含把开关关了再开)都清掉它;裸数字的重置值按时长处理并封顶 365 天,大于 1e9 的当 unix 时间戳(必须在未来且不超过一年),所以一个畸形的头不能把提供商永久锁死。
  - **被拒的读数一律在抛出之前上报**:`RetryingChatProvider` 在判断“重试/不可重试/额度”之前先 `Observe` 失败应答的读数(最后一次尝试、不可重试的错误、已判成额度的 429 也一样),否则下一个请求不知道窗口已被拒;反过来,**成功的应答不产生 `IsRejected` 的 hold**(`Observe(..., answered: true)`:应答本身证明请求被接受,超额通道可以让账号越过已花完的窗口)。
  - 等待放弃时抛出的 429 带的 `Retry-After` 是**还差多久**(剩余等待),不是已经睡过的加上还要的。
  - 等待超过 `_maxRetryAfter`(默认 5 分钟)不睡过去,而是抛一个带 `Retry-After` 的 429——`ProviderQuota` 于是把它判成额度、`QuotaResume` 按既有语义等重置,限速层与服务端的结论走同一条路径。
  - **重试阶梯按原因分(`RetryingChatProvider`,2026-11-24)。** 传输层失败与 5xx 保留短阶梯(3 次尝试,0.5s 起倍增、封顶 30s);**429 走单独的过载阶梯**(`OverloadDelays`:1s/3s/8s/20s,共 5 次尝试),因为网关掉负载的恢复以十秒计,半秒退避只是把尝试烧在同一波里。
  - 抖动一律 full jitter(`[0, ceiling]` 均匀),两次同时被拒的会话不会同时回来;单请求累计等待上限 60 秒(`MaxRetryBudget`),到顶不睡、带服务端原值抛出。
  - `Retry-After` 有值时以它为准,但仍受那 60 秒约束(要 200 秒就不是抖动而是已花完的窗口;>5 分钟仍由 `ProviderQuota` 判成额度、走 `QuotaResume`)。
  - `RetryReason` 把 429 分成 `Overloaded`(无速率措辞的服务端过载)与 `RateLimited`(`ProviderQuota.IsRateWording` 命中,即分钟级限额),5xx 为 `Server`,无状态码的连接/超时异常为 `Transport`。
  - 判额度(`IsQuota`)仍在重试之前,额度类 429 一次尝试就抛出(不变)。

**等待可见(2026-11-15 用户拍板)**:预算等待入睡之前(预计等待 ≥3 秒、每个 attempt 至多一次)先 yield 一条 `BudgetWait(need)` 流事件,`AgentLoop` 转成 `AgentBudgetWait(AgentId, ProviderId, Wait)`——不计入「收到内容」的守卫,不影响溢出重试判定——外壳把它画成运行中轮次的一行非错误提示(`rateLimit.waiting`,含 provider 与秒数),不 toast、不落库;短于 3 秒的簿记性等待不报。**重试也可见(2026-11-24)**:每次重试入睡之前 yield 一条 `RetryWait(Delay, Attempt, Total, Reason)`(没有 3 秒门槛——重试正是“看起来卡住”的那个形状),`AgentLoop` 转成 `AgentRetryWait` 并像 `BudgetWait` 一样不计入「收到内容」的守卫;外壳画成运行中轮次的一行(`rateLimit.retrying`:provider、第 n/N 次、秒数、原因),成功也不撤这行。**自动化恢复间隔不再是独立组件**:`QuotaResume` 里那把锁加字典的 `ResumeStagger` 已删除,改调 `ProviderRateLimiter.ReserveAutomationSlot`(每个 provider 一个 CAS 时间戳的预留:三次同一瞬间的预留得到 0、gap、2×gap),间隔进策略(`AutomationGap`,默认 10 秒),所以同一提供商的自动恢复与用户回合共享一个预算(限速关闭时 `ReserveAutomationSlot` 返回 0:那个开关同时关掉自动化间隔)。**`RetryMiddleware` 缺省(`null`)= 不限速**,不再悄悄回落到 `ProviderRateLimiter.Shared`;`ProviderCatalog` 显式传 `Shared`,所以只有外壳建出的提供商受预算约束,测试里随手 new 的中间件不会碰进程级状态。限流器内的提供商 id 区分大小写(序数比较),覆盖表的 id 从偏好到策略到桶全程保持原写法。
**提供商认证与端点。** `ProviderConfig.AuthStyle` 决定凭据怎么走:`Default`(各协议自己的约定:Anthropic `x-api-key`,OpenAI 兼容 bearer)、`BearerOnly`(OAuth 与 Copilot 用的 `Authorization: Bearer`,Anthropic 走这条时另加一个逗号连接的 `anthropic-beta: claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14`)、`None`(不写凭据头,凭据全部由调用方放进 `Headers`)。`Headers` 在 `ApplyAuth` 之后逐个覆盖同名头,所以网关能用自定义方案而不是被内置方案绑死。`SystemPromptPreamble`(Claude 身份句)非空时作为 Anthropic `system` 数组里单独一块文本;声称官方身份(Wire 生效)时 billing 块在前、身份块在后(见「官方身份」),cache 断点仍在最后一块——非 Haiku 的订阅请求缺身份块会被服务端以 400 拒绝。

- **Claude 订阅凭据(`ClaudeSubscriptionCredentials`)。** 读本机 `~/.claude/.credentials.json` 的 `claudeAiOauth`;距 `expiresAt` 5 分钟内(或过期、或到期时间缺失/单位不可判)用 refresh token 向 `https://platform.claude.com/v1/oauth/token` 换新(client_id `9d1c250a-e61b-44d9-88ed-5944d1962f5e`,请求 UA 用 `axios/…`,因为带 `claude-code/` 前缀的 UA 会被限流),成功后原子写回原文件并保留全部外来字段;写回失败只记一行 Trace,不让本轮失败。**并发刷新按凭据文件全路径串行**(Windows 忽略大小写):同一文件的刷新经 per-path 信号量门控,门内重读双检避免重复刷新与写回竞态,不同文件互不阻塞(与设备码 bundle 同款串行模式)。无文件/无该节点/空 token 返回 `None`(不是异常);JSON 坏、刷新失败抛不可重试的 `ProviderException`,消息里永远不含 token。
- **额度读数(`AnthropicRateLimits`)。** Anthropic 在每个响应里报告账号用量窗(`anthropic-ratelimit-unified-{5h,7d}-{utilization,status,reset}`、`-representative-claim`、`-overage-status`),这是窗口**花完之前**唯一能看见它的地方:满了以后只有一条 429,而那句错误文本既不说明是哪个窗口也不给重置时间。解析宽容:缺头/非数字/负利用率都是 null,全部缺失返回 null(没有量表的提供商不能看起来像空窗口);`reset` 是 unix 秒,不像秒数的值丢弃而不重新解释——错的重置时间会把自动化挂到永远不会到的时刻。
  - 承载有两处:`MessageCompleted.RateLimit`(成功轮)与 `ProviderException.RateLimit`(失败轮,429 的证据只在响应头里),都是可选 init 属性,注入点是 `HttpChatProvider` 的虚钩 `ReadRateLimit(HttpResponseMessage)`;基类读通用 `x-ratelimit-*` 家族(见 W15 一段),自带量表的协议覆盖它,所以 OpenAI/Responses 适配器本身零改动。
  - 读数分两类,**语义不许混**:`anthropic-ratelimit-unified-*` 是订阅的用量窗(5 小时/周),`anthropic-ratelimit-requests-*`/`-tokens-*` 是 Console 的**分钟级限流**(API 密钥用),后者落在 `ProviderRateLimit.Throttle`(`ProviderThrottle`),不参与 `IsRejected`/`ResetAt`——撞上它是瞬时的,该由重试层处理,而不是被判成额度耗尽。两类重置时间的格式不同(unified 是 unix 秒,限流是 RFC 3339),解析器都接受。`ProviderQuota.Classify` **先**看读数:任一窗口 `rejected` 即配额耗尽,`ResetAt` 取该窗口的 `reset`(已过期则为 null);之后才退回文本标记与长 `Retry-After` 的推断。
  - 实测(2026-11-13)那次 429 的正文不含任何标记短语,旧逻辑只靠 `retry-after: 3240` 兜住——若某次 429 不带 `Retry-After`,旧逻辑会把它当瞬时限流重试。
- **官方身份(`AnthropicWire`、`ProviderConfig.Wire`)。** 订阅请求默认声称自己就是官方客户端,四件事一起做才自洽:`system[0]` 是 `x-anthropic-billing-header: cc_version=<版本>.<构建>; cc_entrypoint=<入口>;`(无 cache 断点)、`system[1]` 是官方身份句 `You are a Claude agent, built on Anthropic's Claude Agent SDK.`、`user-agent: claude-cli/<版本> (external, <入口>)`、`x-app: cli` 加 SDK 传输头(`x-stainless-*`、`anthropic-dangerous-direct-browser-access`、`x-claude-code-session-id`)与 `?beta=true`;`anthropic-beta` 用官方的 17 项列表而不是 OAuth 必需的 3 项。
  - **入口值同时出现在 billing 块与 UA 里**,只给一处是自相矛盾的指纹。
  - 缓存形状也对齐实测:identity 块与主 system 块各带一个 **`ttl: "1h"`** 的断点(官方就是 1 小时;5 分钟的断点会在一轮对话的两次请求之间过期,等于把同一前缀的写入付两遍),**工具数组不带断点**(官方不在那里打点,它在两个 system 块上打),消息断点仍是最后一条;billing 块永远无断点。
  - `anthropic-beta` 取**本机 CLI 自己报的那一份**(见下),因为这份表本身随版本与特性开关变——同一台机器连跑三次,官方自己报出 12/13/14 项不等,冻结一份必然会与我们声称的版本不一致;`accept: application/json` 也对齐。
  - **缓存形状只有一套,与凭据无关**:两个 system 块 1 小时、工具数组不带断点(断点即前缀,最后那个 system 断点已经把工具一起缓存了,官方也不在那里打点)、消息末块 1 小时;API 密钥请求为此声明 `extended-cache-ttl-2025-04-11` 这一个 beta(它不声称客户端身份,但同样要声明自己真正用到的长缓存);**寿命由 `CacheHint.Ttl` 一处决定**(`null`/`"1h"` → 1 小时,`"5m"` → 不写该字段、用服务端默认),system 块与消息块永远取同一个值——两边取不同值只会让同一段前缀按两种价格各写一次。
  - 策略在 `AgentLoop.ResolveCacheTtl`:`"5m"`/`"1h"` 直用;`"auto"`/null 先 5 分钟,一旦本实例两次请求的间隔 ≥ `AgentOptions.CacheTtlPromotionGap`(默认 5 分钟,即短寿命自身的过期点)就在**下一次写入**时永久升到 1 小时。
  - 判据是价格:1 小时写入 2×、5 分钟 1.25×、读取都是 0.1× 且读一次就续命,所以不出现 5 分钟以上停顿的会话(无人值守、连续回合)用 5 分钟严格更省,出现一次 5–60 分钟停顿后 1 小时就开始省;升档只发生在缓存本来已过期的那一刻,所以不多付一次写入,降档永不在会话中途发生(那会强制一次写入)。
  - 生效值经 `AgentContextMeasured.CacheTtl` 上报给外壳。
  - 身份声称(billing 块、UA、`x-app`、SDK 头、`?beta=true`)仍然只在 `BearerOnly` + `Wire` 下发生——一个 API 密钥没有可声称的客户端身份。
  - 这样两套代码只有"是否加身份块",不再有两套缓存配置。
  - 入口的**默认值由实测定**:2026-11-13 在本机 Pro 账号、opus-5.5 上,形状完全一致只换标签,`sdk-cli` 每推动 5 小时窗口 0.01 只需 31–33k token,而 `cli`、`claude-vscode`、`claude-desktop-3p` 需要 62–68k(两趟反序复现),所以默认是 `sdk-cli`。
  - 这与公开报告的方向相反(报告称 SDK 标签更贵),因此这条实测、连同 haiku 上三臂无差别的结果,都写在这里作为默认值的证据,而不是把报告当成结论。
  - **分类不是加权轴**:`cli`(`claude_code_cli`)与 `claude-vscode`(`claude_code_vscode`)分类不同却成本相同,说明起作用的是标签取值本身;`cc_entrypoint` 是唯一发出去的标签,`claude_code_*` 是官方客户端内部映射的分类,任何客户端都不会把它写在线上。
  - **此处修正本 spec 先前的一句判断**:身份块并**不是**必须在 `system` 的第一块——真实顺序是 billing 块在前、身份块在后,实测带着 billing 首块的请求被正常受理(429 而不是 400)。
  - **身份与提示词必须说同一件事(`BuiltInPrompts`)。** 索取官方身份时,系统提示用 `BuiltInPrompts.System`(首句 `You are a coding agent…`,不自报产品名);用户在 `claudeWire` 里关掉后改用 `SystemNamed`(首句 `You are Forge, a coding agent…`),因为那时自称 Forge 才是诚实的而不是自相矛盾的。
  - 两份文本**除首句那个名字外逐字符相同**(由测试钉住),否则切换开关会顺带改掉 Agent 的规则。
  - 哪一份生效由 `AgentRuntimeServices.ClaimsOfficialIdentity` 给出(App 从 `ProviderCatalog.ClaimsOfficialIdentity` 取:**`claudeWire` 生效且确实连着一个 `claude-subscription` 提供商**,两条件同时成立才声称;只有 API 密钥的安装不声称);用户自己给了 system 模板文本时两者都不参与。
  - **beta 被拒时降级一次,不失败**(`HttpChatProvider.CanDegrade`/`Degrade`)。
  - 服务端或网关可能拒绝我们声明的某个 beta(典型是 `anthropic-beta: unknown value …` 的 400):那说明请求要的比账号给的多,不是请求本身错,所以 `StreamAsync` 用同一份 `ChatRequest` 重建一次 body 再发——beta 缩到订阅必需的 3 项、API 密钥则一个都不发、`ttl` 不写(退回服务端默认 5 分钟),身份块与缓存断点位置不动。
  - **只降级一次**:第二次 400 是真正的错误。
  - 降级成功后按 `Id|AuthStyle|入口` 记进进程级备忘,后续实例不再多付一个往返;`AnthropicProvider.ForgetDegradations()` 供账号或设置变更时清空。
  - 降级必须可见:`MessageCompleted.Notice` 带上解释(丢了什么、代价是什么),`AgentLoop` 把它转成 `AgentNotice` 事件,外壳以 toast 呈现——静默降级等于把成本变化藏起来。
  - `ProviderConfig.Wire` 为 null 时请求与旧版字节完全一致(无 UA、无 billing 块、OAuth 三项 beta、无查询串),该不变量由测试钉住。
  - **`cc_version` 的两段来自不同地方(`ClaudeCliProbe`)。** 版本是稳定量:读本机 CLI(`claude --version`,实测 0.19 秒;按 PATH → WinGet 链接 → `~/.claude/local` → VS Code 扩展内置二进制的顺序找),在保存提供商时写进 `claudeWire.version`。
  - 后缀**不是构建号**,不能存进配置:实测同一二进制、同一凭据(credentials 文件的 mtime 未变)先后报出 `68a`、`d83`、`da9`,随目的地/时间变化,且不在二进制里(不是文件 sha256、不是 git sha/build time/版本串/token 的哈希,也没有字面量),所以它只能在发请求时现问一次:`TryReadBuild` 起一个 127.0.0.1 监听口,把 `ANTHROPIC_BASE_URL` 指向它跑一次 CLI,从它自己构造的请求里读出 billing 块就立刻杀进程(不杀的话实测会挂到 91 秒)。
  - `Lazy` 每进程只问一次、失败即空;settings 里的 `build` 只是可选的钉死值,钉了就不再问。
  - 那条 loopback 请求的头部带着用户的 OAuth token——探针只从**请求体**里取 billing 块,头部一律不复制、不记录、不落盘;读不到就是 `cc_version=<版本>;`(省略点号段),而不是拿常量冒充。
- **GitHub Copilot(`CopilotAuthentication`)。** 设备码 `POST https://github.com/login/device/code`(client_id `Iv1.b507a08c87ecfe98`,scope `read:user`)→ 轮询 `POST /login/oauth/access_token`(pending/slow_down 返回 null,其余错误抛)→ `GET https://api.github.com/copilot_internal/v2/token` 换会话 token(`Authorization: token <ghu_…>`,带 `Copilot-Integration-Id: copilot-chat` 与 `Editor-Version`/`Editor-Plugin-Version`),会话 token 约半小时有效、`IsValidAt` 留 60 秒余量。这是非官方客户端用法,界面必须如实标注。
- **模型列表(`ProviderModelListing`)。** 原先在 App 的 `/models` 解析搬到这里。地址由协议决定(`ListRequest`):Anthropic 的消息路径带版本段(`{base}/v1/messages`),所以它的列表在 `{base}/v1/models` 并显式要 `limit=1000`(该端点默认每页 20 条,不显式要全量会在模型变多后静默截断;实测该值被接受),其余协议就是 `{base}/models` 且不带查询串。订阅登录的凭据与 API 密钥列同一端点(密钥路径按 Anthropic 公开文档与同一段头构建代码,未在本机实测);调用方给头,20 秒链接超时(超时抛 `TimeoutException`),按服务端顺序去重;上下文窗口认 `context_length`/`max_context_length`/`top_provider.context_length`,价格认 `pricing.prompt|completion|input_cache_read|input_cache_write` 的**每 token** 值并换算成每百万 token。
- **内化落点(`Providers/Internal/ChatProviderPipeline`)。** 参照 microsoft/agent-framework(`Microsoft.Extensions.AI` 的 `ChatClientBuilder.Use`)的**中间件管道形状**:端点只管协议,重试作为最外层中间件(`RetryMiddleware` 就是 `RetryingChatProvider` 的管道包装)。没有逐字拷贝上游文件,也没有引入它的 OpenAI/Anthropic SDK 客户端——本适配器直接讲 wire 协议;provenance 记在 `src/Forge.Core/THIRD_PARTY_NOTICES.md`。
- **预设(`ProviderPresets`)。** 纯数据:协议、base URL、环境变量、文档链接、种子模型、是否支持 `/models`。它是"便捷接入"的唯一来源,不参与运行时判断;某个预设被删后旧配置仍按自己的 URL 工作(降级为自定义)。

**模型元数据与用量换算(`Providers/`)。** 没有内置的价格表:链是 **目录快照 → provider 上报的元数据 → 用户 `settings.json` 的 `providers[].modelMeta`**(`ModelMetadataResolver.Resolve`),逐字段合并(`ModelMetadata.With`),任何一半未知就保持未知,绝不补 0--离线时跑不了 agent,需要价格的时刻一定有网,而编译期的旧价只会悄悄错。
  - 目录快照来自 models.dev 的 `api.json`:`ModelCatalogParser` 把它读成紧凑的 `ModelCatalogSnapshot`(`limit.context`/`limit.output`、四个价格、`modalities.input`、`reasoning` 与其 `effort` 档位),`ModelCatalogFetcher` 用存下的 `ETag` 发条件请求(304 即"无需更新")并在超时/状态/形状失败时返回消息而不是异常,`ModelCatalogJson` 负责 `model-catalog.json` 的读写(损坏或版本不同即当作没有快照)。
  - `ModelCatalogView` 负责查:`ModelIdRules.Normalize` 归一化 id(小写、去掉 `vendor/` 前缀与 `:free`/`-latest`),先按"预设 → 目录提供商"映射取该提供商自己的条目(同一模型各网关价格不同),映射不到预设的先试提供商**自己的名字**:id 或显示名 trim+小写后与某个目录提供商键相同的,直接用该家的条目——名字是用户对"我在跟哪家网关说话"的声明,聚合商转列的同名模型(推算值,如 openrouter 给 glm-5.3 的 943718)争不过网关自己上报的条目;名字也无人认领的自定义提供商才按模型 id 全局兜底(一方优先序,首方优先、网关先于聚合商),id 匹配是"精确 → 最长已知 id + 快照后缀(`-yyyyMMdd`/`-vN`/`-latest`)→ 包含",版本号递增(`gpt-4o` → `gpt-5`)不继承。
  - `SessionUsageMath` 全是纯算术:缓存命中率以 `InputTokens` 为分母(OpenAI 兼容的 `prompt_tokens` 与 Anthropic 的 `input + cacheRead + cacheWrite` 都把缓存折进输入)、计费输入先减去缓存、每个 token 数夹到 ≥ 0 后按每百万单价计费、窗口占比夹到 0..1。
  - 展示层(百分比、`9% / 1.0M`、`$0.041`)与它无关,见 forge-app 的会话输入框一节。
  - **1h 缓存写计价(2026-11-18 定案)**:`CacheWrite1hTokens` 是 `CacheWriteTokens` 的子集(Anthropic 回包 `cache_creation.ephemeral_1h_input_tokens` 是 `cache_creation_input_tokens` 的分解,契约见 `Forge.Abstractions/Messages.cs`),计价时 5m 余额 = 总量-1h 按缓存写价(1.25×),1h 部分按 `2 × 输入价` 单列计(命名常数 `OneHourCacheWriteInputMultiple`,依据是 AgentOptions 缓存升档论证与用户可见悬停文案的既有知识);声明多于总量的 1h 被夹回总量。
  - `MessageJson` 补 `cache_write_1h`(仅 >0 写、缺键读 0)不升 schema;重放求和含第五字段。
  - **剩余限制**:`last_seq` 列出现之前的旧行无位置、不配对(各段读「—」),不猜不回填;两种 v11 库变体(有/无 turn_metrics 表)以表存在性区分;失败轮中已落库消息的 usage 在重放时计入而实时条带不计(微小正向漂移,与失败轮 NULL 口径并存)。

### 自动化

**自动化(`Automations/`)。** 契约在 `Forge.Abstractions/Automations.cs`,引擎与调度在这里,参考 CodeAlta.Automations(BSD-2,见 `THIRD_PARTY_NOTICES.md`)的调度形状,未逐字拷贝。
- `AutomationSchedule`:间隔(每 N 小时的第 M 分)、每日若干时刻、每周若干天的若干时刻、5 段 cron;按本地时区算下一次,DST 跳过/重复的时刻各只触发一次。`TryCreate` 给出校验错误,设置页的“接下来 5 次”预览直接用它。
- `AutomationEngine`:单个后台循环,最长睡 30 秒;按定义的下次触发时间启动运行。忙键 =(定义 id, 目标会话 id),同键已有活动运行时新请求返回 `Busy`;同一 `Occurrence` 只跑一次(`Duplicate`)。链条深度上限 `MaxChainDepth = 5`(运行由另一次运行引起时深度 +1),运行不能触发自己(`SelfTriggered`);带 `TargetKey` 的请求计入尝试预算(`budget:<key>`,到额即 `BudgetExhausted`,由调用方 `ResetTargetBudgetAsync` 清零)。每分钟写一次心跳 `engine.alive_at`;启动时把上次未结束的运行标为中断,`CatchUp` 定义在停机期间错过的触发补跑一次。运行状态更新按运行串行(`RunContext.UpdateAsync` 持每运行一把锁)。`ResumableWhileIdle`(目前只有额度恢复)的运行在关闭时若停在 `WaitingIdle`,启动时改回 `WaitingQuota`(无恢复时刻)续跑,其余 `Running`/`WaitingIdle` 标为中断。`StartAsync` 不检查定义启用/暂停——事件启动的运行由调用方先判(App 的 `AutomationService.IsEnabled`)。
- 存储:schema v10 新增 `automation_runs`(运行历史,`TrimAsync` 按定义保留条数与总条数修剪)、`automation_state`(键值:心跳、预算、各 runner 的持久笔记)与 `sessions.automation_id`/`automation_run_id`(会话由哪次运行创建)。
- 额度用尽:`ProviderQuota.Classify` 把 402、带额度标记或长 Retry-After 的 429、带额度标记的 400/403 归为 `ProviderQuotaExhausted`。额度标记分三级:确定标记(`insufficient_quota`、`usage limit reached`、`billing_hard_limit_reached`、`credit balance is too low`、“余额不足”等)直接算额度;智谱额度错误码次之;文本里出现速率措辞(`rate limit`、`per minute`、RPM/TPM、“请求过于频繁”“频率”“并发”等)时一票否决,其后才看措辞型标记(`quota exceeded`、`usage limit`、“使用上限”)与周期限额/中文额度句式,避免把限流当成额度(含 provider、模型、可解析出的重置时间;未知错误从不算额度),重试层对它不重试,`AgentHost` 以 `AgentQuotaExhausted` 事件上报且不重试;`QuotaProbeSchedule` 在没有重置时间时按退避间隔用最小请求探测额度是否恢复(用户已接受探测的额外消耗)。
- `AgentHost` 可在回合之外启动子 Agent(自动化的 Scout 用它),结果与普通子 Agent 相同。


### 提示词模板

- **提示词模板(`Prompts/Templates/`)。** `PromptTemplateRegistry` 收编译期内置模板(Core 的四个 Agent、四个命令、`system`/`compaction.summarizer`/`handover.summary` 三个槽位;组合根可 `With` 追加自己的槽位),`PromptTemplateStore` 按 内置 → `~/.forge` → `<工作区>/.forge` 分层读写 `<种类目录>/<id>.md`,`Resolve` 得到不可变的 `PromptTemplateSet`(命令、Agent 定义、槽位文本;`FormatSlot` 单次替换 `{名字}`,`TryExpandCommand` 展开 `/名称 参数`)。frontmatter 由手写的 `PromptFrontmatter` 解析(无 YAML 库,AOT 安全)。`AgentHostOptions.Templates` 给默认压缩器提供 `compaction.summarizer`,`HandoverSummary.GenerateAsync` 可传入 `handover.summary`;`PromptComposer` 把 `PromptLayers.System` 当作完整系统层(即生效的 `system` 槽位),空时回落 `BuiltInPrompts.System`——此前它总把内置文本再拼在前面,调用方又传了同一段内置文本,系统层重复了两次。Agent 以名称派生,名称即身份:内置 Agent 的覆盖文件不得改名,用户 Agent 不得占用内置名或内置 id,空 `tools` 列表无效;`PromptTemplateSet.ResolvedAgents` 给出每个 Agent 的模板 id(设置键)与 `thinking` 是否由文件显式给出。`Read` 对超限或非 UTF-8 文件抛 `IOException`。

### 模型名解析

- **模型名解析（`Providers/ModelResolver.cs`）。** 四档匹配：精确原样 → `ModelIdRules.Normalize` 归一 → snapshot 双向 → contains 双向；一档出多个候选就停并全部返回，不猜。都不中时 `Suggest` 按字母数字词共享数（再按长度差）给至多 5 个最接近的 id，只作提示、不参与解析——生产中 `luna-6.1` 猜了四次才拿到 `gpt-6-luna`。

## 未决

- 请求的令牌估算用 `ApproximateTokenCounter`(按字符类别加权:拉丁/代码/空白每字符 2 单位、CJK 汉字/假名/谚文/全角每字符 5 单位,8 单位 = 1 token,即拉丁 4 字符/token、CJK 约 1.6 字符/token),与分批预算同一口径;真实用量在应答回来后经 `Settle` 校正,所以估算偏差只影响一两个请求的提前等待,不会累积。

- 事件通道保持无界(决定):`AgentHost.RunAsync` 的 `Channel.CreateUnbounded<AgentEvent>` 不加背压、不丢事件;UI 合并在 App 侧由 `FrameCoalescer` 实现,见 forge-app。
- `MaxTurns` 计入后续指令续跑;不响应取消的工具要等其返回才真正停止。
- **无进展收尾先于轮次上限。** 连续 `NoEvidenceTurnLimit`(默认 3,0 关闭)轮“没有新证据”即正常结束并返回已产出文本(尾部一行停止原因,同时作为一条 assistant 消息落盘并流式发给 UI),不再撞 `MaxTurns` 抛 `AgentTurnLimitException`。“没有新证据”只两种形状:本轮每个调用都是 `grep`/`find`/`ls` 且每个结果都是“没有命中”(`no matches…` 或 `(empty)`),或本轮调用批次与上一轮逐字相同(`name` + `arguments`)。所以 shell/编辑/构建的失败重试不计入,任何一次命中或成功调用都会清零;steering 落地归零。`AgentTurnLimitException` 保留为兜底路径。
- 内置 Agent 的工具白名单未含 `todo`(加入会改动其可缓存工具集),目前只有根 Agent 可用。
- 评审方式已接线（`git` 分区）：`reviewMode` = `subagent`（默认）/ `manual`；`reviewAfterStepCommit`（默认关）在步骤提交成功且模式为 subagent 时，把一条带 `step_id` 的 `request_review` 请求交给该会话（空闲起一回合、忙则进草稿，绝不背着用户起第二回合）。评审结论被步骤完成门禁读取并持久化到 `step_review_verdicts`，但计划面板不显示，`pr_create` 也不读取它。
- 步骤完成门禁的两个缺口:(已由内容指纹与步骤基线解决,保留作历史)评审之后的工作树改动无法察觉;Agent 自行 `git commit` 后工作树干净且步骤无提交,会被判成「没改代码」而放行。两者都需要新的证据源(步骤基线 HEAD / 树指纹),未实现。
- Artifact 转存阈值、keepRecent 默认值与摘要文件清单的两个字符预算(4000/8000)都未经使用验证(前两项现分别由 `preferences.chatArtifactSpillChars`/`ChatArtifactPreviewChars` 与 `compaction.keepRecentTokens` 覆盖);清单预算没有设置项,按长会话实测再调。
- 文件追踪只认 `read`/`edit`/`write`/`delete`/`copy`/`move` 的工具参数；`grep`/`ls`/`find` 与插件工具不产生条目。
- 分批预算按 4 字符/token 折算,对中文等高密度文本偏乐观,0.6 的窗口份额是余量;旧 Turn Context 的合并依赖摘要模型遵从指令,代码只保证正式标题不叠加。
- `search_history`/`read_history` 只看本会话自己的消息。`read_history` 的单条消息超过 200,000 字符时被截断,而提示的 "narrow the range" 切不开单条消息——越出的尾部只能靠 Spill 后的 Artifact 分片读,(未修)。
- Tavily 的 Bearer 认证方式未经实测。
- Exa 公共 MCP 的匿名限额与可用性未量化实测(2026-11-21 实调成功);其文本区块格式与 DeepSeek Search 的字段映射未经长期验证,DeepSeek 的 Bearer 鉴权也未实测(无密钥)。
- 网络栈:socks4(非 4a)代理由 .NET 在本机用系统 DNS 解析目标;DoH 服务器自身的主机名用系统解析器解析(写 IP 形式的 URL 可避免)——两者是平台/协议限制,不在 Core 修,App 的网络设置页对这两种输入给出内联提示;代理服务器地址本身不过地址过滤。
- `web_search` 的有无在 App 的会话回合开始时同步;正在运行的回合直到结束都保留开始时的工具集,其间后端被移除由工具每次调用的后端读取兜底(返回“未配置”)。
- 多个 `ask_user` 并行调用由 App 层按会话串行展示(见 `Forge.App/SPEC.md`),Core 不感知。
- W15 限速的未决项:(1) hold 的出口目前只有 1 小时封顶与“发布一次策略”,没有 UI 上的显式“忽略服务端的 hold”;(2) `Calibrate` 假定 `x-ratelimit-limit-requests` 是**每分钟**上限——OpenAI 的头确是 RPM,但某些兼容服务发的是日/小时限额,会被当成分钟桶(偏松不会误锁,但偏紧的口径错误无法识别;2026-11-15 定为 P3:后果是多余的 429、由 `Retry-After` 与额度流兑底,不改代码,后续如修,方向是 reset 时长 ≤2 分钟才把 limit 入补充率、否则只取 remaining/reset 做 hold)。
- 额度读数目前只覆盖 Anthropic:非 Anthropic 提供商的订阅额度读数(Codex 用量端点;OpenAI 通用 `x-ratelimit-*` 已作为分钟级限流读数接入,见 W15)、设置页“已连接”行的额度摘要、额度历史曲线与跨会话持久化均未做;配额横幅文案改造(说清 5h/周/超额)待用户选择。
