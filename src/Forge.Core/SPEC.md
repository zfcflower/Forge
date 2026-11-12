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

**缓存纪律。** 请求的系统提示词只含稳定前缀(系统 → 全局 Agent → 项目 Agent → 角色,字节稳定);工具定义按名称排序且同类 Agent 一致;动态信息只作为末尾的一条临时 user 消息。缓存断点落在已持久化历史的倒数第二条。压缩只追加一条新的会话级摘要并把切点前的原文换成它,切点后的近期原文(至少 keepRecent)与继承前缀逐字节不变。

**循环。** 流式一轮 → 审批 → 执行 → 回写。连续只读工具并发,有副作用工具依序逐个执行;结果按调用顺序写成一条 Tool 消息。取消时每个待决调用都补一条"已取消"结果,保证历史始终合法。超长工具输出转存 Artifact,上下文里只留预览和 id。

**SubAgent。** `spawn_agent` 按 `<kind>-<n>` 登记实例;同类型共享前缀,可按 id 续跑(同类型、且当前未在运行)。活跃 Agent 计数覆盖整棵树;父 Agent 等待子结果时交还名额,否则嵌套会死锁。深度上限 3。该工具对未超深度的所有 Agent 隐式可用,Agent 定义无法关闭它。

**子 Agent 控制。** 每次 spawn 是一个 run,有独立 `CancellationTokenSource`(链到父 run 令牌),父 Agent 的 `subagent_steer` / `subagent_followup` / `subagent_stop` 与 UI 停止按钮共用 `AgentHost.SubAgentControl`(`ISubAgentControl`)。
- 转向在工具批次边界生效:已发出的调用跑完,本回合剩余未开始的调用补 `skipped: the agent was redirected` 错误结果(历史保持合法),指令作为下一条 user 消息注入;边界前的多条转向合并为一条消息、一个事件;批后无剩余调用时按丢弃 0 交付,指令不丢。
- 后续指令在子 Agent 本应结束(assistant 无工具调用)时注入并继续。排队期收到的转向/后续指令合入初始任务。
- 停止只取消该子 Agent:待决工具补 "Cancelled.",待决审批被取消释放;父 Agent 收到**非错误**结果 "Sub-agent '<id>' was stopped (<reason>). Partial output: …",由父决定后续。
- 生命周期 Started(父发出)→ 仅在确实等名额时 Queued → Running → 每个 run 恰一次 Finished(在调度器 `finally` 中,含异常与父取消;父取消记为 Stopped "parent cancelled")。用量为本 run 增量,耗时从 spawn 起算(含排队)。
- `runs` 只保存运行中的 run,结束即移除并释放其 CTS 与父令牌注册;已结束 id 记入紧凑集合以继续返回 `AlreadyFinished`;`instances` 保留以支持按 id 续跑。

**会话目录与恢复。** 会话元数据(项目、worktree、分支、父会话、Agent 种类、任务、归档状态、最后活动)是 `sessions` 表的列,`ISessionCatalog` 与 `ISessionStore` 由同一 `SqliteSessionStore` 实现。能力只经 `SessionCatalogs.From` 读取:`LoggingSessionStore` 总实现该接口,须读 `SupportsCatalog`,内层非目录时按无目录处理。子会话带 `ParentSessionId`/`AgentKind`/`Task` 并继承父会话的 worktree 与项目;每回合持久化后 `TouchAsync`,失败不影响循环。`ResumeRootAsync` 用与压缩器相同规则重建上下文(摘要视图 + 未摘要尾部),丢弃孤立首个工具结果与末尾无结果的工具回合。一个 `AgentHost` 对应一个主会话;`AgentScope` 为 AsyncLocal,名额池与 id 计数在实例内,多主会话可并发。

**`todo` 工具。** 按参数决定副作用(list 与 plan_summary_get 只读,其余写入),审批策略与只读并发分批看到单次调用的真实副作用;update/remove/promote 先确认条目属于 `ToolContext.SessionId`。清单按**分组**组织:add 带 `group`(按标题找,没有就新建)把条目收进该组的步骤,不带 `group` 则进会话的**待排散项队列**;`promote` 把一条散项升成以它命名的新分组(条目移出队列、分组继承它的 id 与标题),agent 接手用户排队的任务时用它,`after_id` 只在同一层内插入。同一时刻只有一步 in_progress(存储层保证,写成 in_progress 会把会话里别的 in_progress 退回 pending);`paused` 表示在等外部输入或依赖,永不触发步骤提交。update 接受 `commit_subject`/`summary`/`verification`;状态变为 done 时经 `AgentHostOptions.StepCommitter`(`IStepCommitter`,契约在 `Forge.Abstractions`,由 App 实现,见 forge-app)提交这一步,结果(短 sha 或失败原因)写进工具结果,提交失败不回滚 done 状态。list 按分组输出(每组标题 + `done/total` 进度、组内步骤带 id/状态/note 摘要),散项列在末尾,agent 据此决定 promote 谁。`plan_summary` 操作读写会话级计划摘要(`plan_summaries` 表),供 PR 正文渲染。

**扩展编写工具(`Tools/IExtensionAuthoring.cs`)。** Core 只声明窄接口 `IExtensionAuthoring`(`ListAsync`/`SubmitAsync(ExtensionDraft)`/`DisableAsync`/`DeleteAsync`),由 App 用 HostClient 实现;Core 没有提交或批准能力,人工闸门只在 App 的实现里。五个工具 `extension_list`(只读)、`extension_create`/`extension_update`(同一个类,schema 字节一致;参数 `id`、`version`、`kind`(`workflow`/`plugin`)、`files`、`permissions`、`dependencies`)、`extension_disable`、`extension_delete`:参数全走 `JsonDocument` 本地校验,不合规返回错误结果且不调 authoring;提交调用阻塞到用户决定,结果是已注册的工具名、拒绝/撤卡原因或编译诊断;`ExtensionSubmitOutcome` 用 `Approved`/`Rejected`/`Withdrawn`/`Invalid` 四个值区分——`Withdrawn` 是卡片在用户决定前消失(会话关闭、应用退出),草稿保留待定、不是拒绝、对 agent 不算错误结果。`OperationCanceledException` 透传。它们不进内置 Agent 白名单,内置 Agent 的可缓存工具集不变。

**工具热加载(`ToolSurface`)。** `AgentHost.ReplaceExternalTools` 发布不可变快照 `ToolSet(版本, 注册表)`(`Volatile` 读、只在发布时加锁、版本单调);同名时内置优先、外部同名静默丢弃。循环在每次模型请求前和每次查工具前取当前快照:版本未变复用同一 `Definitions` 数组实例(零分配,提示词缓存不变),变了才按白名单/深度重建,所以批准后的下一次模型请求(同一回合内也是)就能看到新工具;代价是该会话提示词缓存失效一次。工具开关也在这里:`AgentHost.SetDisabledTools`(或 `AgentHostOptions.DisabledTools` 初值)给 `ToolSurface` 一个禁用名集合,发布快照时内置与外部工具都按它过滤;集合与现值相同(`SetEquals`)时什么都不发布,版本不变、缓存不破;外部替换后禁用集合仍然生效,后注册的同名工具也被挡住。被禁用的工具既不出现在请求里,回合中途被关掉后模型再调用会得到错误结果。`AgentHost.AllTools` 列出全部内置与外部工具(含已禁用的),供 App 的工具目录使用。`AgentLoopServices.Tools` 的类型是 `ToolSurface`。

**扩展工具仅限根 Agent。** `extension_*` 五个工具只对 `Depth == 0` 的 Agent 可见、可调用:`ToolRegistry.Select` 对子 Agent 一律剔除(无论 `ToolNames` 为 null 还是显式列出),派发查找走同一份选择结果;工具自身在 `ToolContext.AgentDepth > 0` 时也拒绝并返回错误结果。

**审批路径比较按 OS,不探测文件系统。** `DefaultApprovalPolicy` 判断工作目录是否在受信目录内,比较规范化后的路径前缀:Windows 用 `OrdinalIgnoreCase`,其余平台用 `Ordinal`,不做大小写探测、不读文件系统。macOS 的 APFS 默认大小写不敏感,同一目录的其他大小写写法因此会被判为“不在受信目录内”,结果是 fail-closed 到 `Ask` 而不是误放行;测试按 `OperatingSystem.IsWindows()` 断言,不按卷的大小写敏感属性断言。

**存储。** SQLite 存会话、消息、摘要、全文索引(FTS5);JSONL 为事件日志,供崩溃恢复;Artifact 内容存文件。所有写入走单写线程批量提交,返回 seq 时已落盘;提交后的取消不会让调用方误判为失败。摘要行只追加不改写,被压缩的原文仍可检索和取回。每次压缩追加**一条**会话级(layer 2)摘要,范围从上一份会话摘要(或旧阶段摘要、或首条原文)的起点到最后一条被摘要的消息;视图 = 一份摘要 + 原文尾部(`SummaryLayers.Build` 取覆盖最宽的会话摘要并丢掉被它覆盖的阶段行)。旧版本写下的阶段(layer 1)行仍可读:恢复时照旧展示,下次压缩把它们的正文并入“上一份摘要”,新会话摘要覆盖后即不再出现,所以旧行无需迁移。切点由 `SummaryLayers.PlanCut` 定:只落在下标 ≥ 1 的 user 或 assistant 消息上(不落在工具结果上),所以压缩不会拆开工具调用与其结果。

**forge.db 版本阶梯。** v5 的归档与通知写入在同一个事务里:一次注入的失败(通知写入缺列)回滚到 v4、两条会话都还是 Active、`occupancy_key` 也随 DDL 一起消失,去掉障碍后同一步可重跑并爬到 v9(`StorageSchemaV5To9MigrationTests`)。 `PRAGMA user_version` 是唯一权威(0 = 无标记旧库,1 = 工作台表,2 = `todos` 加 `commit_subject`/`summary`/`verification`/`commit_id`/`commit_error` 五列 + 新表 `plan_summaries(session_id PK, text, updated_at)`;3 = 一次性规范化历史时间戳,并为每个时间戳列装写守卫;4 = 当前:`todo_groups(id PK, session_id, title, order_index, updated_at)` 与 `todos.group_id`/`todos.origin` 两列,旧行的 `group_id` 保持 NULL 即待排区、`origin` 默认 `agent`,无需回写)。每步在一个 `BEGIN IMMEDIATE` 事务内执行并同事务写入版本号,事务内重读版本防止多进程重复迁移;读到更高版本立即 `InvalidDataException`,不降级不写库。旧会话回填 Active 与最后活动 = 创建时间(严格解析,失败整步回滚)。排序用 `last_active_ticks`(UTC ticks)而非 ISO 文本。`messages_fts` 与阶梯解耦(可用性取决于 SQLite 编译选项),首次建表与回填同事务,单条坏消息只跳过(派生数据)。

**forge.db 时间戳由库自己把关。** 时间戳列只存 `ToString("O")` 形状的文本(带 offset、固定 7 位小数),读取严格、不兼容其它写法。进程外工具直接改库曾写入 6 位小数的 ISO 文本,一行坏值就让整个 `forge.db` 读不出来(左栏项目树空白、添加项目报错)。因此 3 号步骤先就地规范化:凡文本自带 `Z` 或 `±hh:mm`、能解析出唯一时刻的,改写为规范形状(时刻与 offset 不变);无 offset 或无法解析的值原样保留,由严格读取指名报错——迁移不猜时间。规范化之后给 8 张表 10 个时间戳列各建 `BEFORE INSERT`/`BEFORE UPDATE` 触发器(`NEW.col IS NOT NULL AND NOT (NEW.col GLOB <规范形状>)` 时 `RAISE(ABORT)`),任何写者(本程序、外部脚本、旧构建)都写不进非规范文本,可空列(`turn_checkpoints.ended_at`)仍允许 NULL。守卫只建在**步骤执行时已存在**的表上(3 号步骤在 `todo_groups` 之前跑,否则老库升到 3 会因缺表直接失败),4 号步骤建出新表后按同一份清单把全量守卫重装一遍(`DROP TRIGGER IF EXISTS` + `CREATE`,幂等),现在是 9 张表 11 个列。新增时间戳列必须登记进 `StorageSchema.TimestampColumns`,否则既不会被规范化也没有守卫。审批库 `approvals.db` 不在此列,仍按其 SPEC 只接受规范文本。

**工作台存储。** `projects` / `todo_groups` / `todos` / `ui_state` / `turn_checkpoints` 与会话同库:每个 store 一条长连接 + `SemaphoreSlim` 串行 + 每写一个 `BEGIN IMMEDIATE`(deferred 读后升级会静默丢更新),读用池化短连接。`ITodoStore.Changed` 在提交后、写锁外触发,订阅者异常被吞,一次写入只抬一次。路径唯一性靠 `root_key`(Windows 大写)唯一索引,General 项目由部分唯一索引保证单例,再次注册即改指向。TODO 序号**层内**致密:`(session_id, group_id)` 是一层(`group_id` 为 NULL 就是待排区),插入、删除、`MoveAsync` 只重排本层,层与层互不牵连;分组顺序在会话内致密且只增不减(没有分组删除入口,面板侧也不提供)。同会话只能有一步行进中:写事务里把某条目写成 `InProgress` 时,同事务把该会话其余 `InProgress` 改回 `Pending`。检查点以 (session, turn) 为主键,已完成轮次不被再次 Begin 改写。`synchronous=NORMAL`:这些是体验数据,丢失不影响安全(审批库用 FULL)。

**会话生命周期与工作区占用(v5–v9)。** 状态 `Active`/`Archived`/`Deleted`。占用键 `occupancy_key` = `OccupancyKey.Compute(worktree_path, workspace_path)`(`Path.GetFullPath` + 去尾分隔符,Windows 再大写;与 `WorkspacePath.Normalize` 同语义),由存储层在每次写路径时计算,调用方不传;部分唯一索引 `ux_sessions_occupancy`(`status='Active' AND parent_session_id IS NULL`)保证一个工作区同一时刻一个活动根会话,冲突映射为 `WorktreeOccupiedException(HolderSessionId)`。App 内把会话置 `Active` 只走条件插入、`ActivateAsync`、`HandoverAsync`;`SetStatusAsync` 是只受索引把关的原始写入。v5 迁移逐行在 C# 回填键,按键去重(保留 `last_active_ticks` 最大者,平局按 id 大者),被归档者写进 `storage_notices`(`dedupe-archived`);v6 `deleted_at`,v7 `projects.closed_at`(软关闭,`AddAsync` 同路径复活原 id),v8 `fork_of_branch`/`fork_base_sha`,v9 `inherits_from_session_id`/`inherits_up_to_seq`/`lineage_root_id`(回填为自身)/`origin_kind`(回填 `cold`)/`prefix_fingerprint`。

**写入不连坐。** 写队列一个批次一个 `BEGIN IMMEDIATE` 事务,每个操作包在 `SAVEPOINT forge_op` 里:单个操作冲突只回滚到自己的保存点并以异常交还调用方,同批其它操作照常提交。`HandoverAsync` 单事务:先条件归档旧会话(受影响行 ≠ 1 抛 `SessionNotActiveException`),再条件插入新会话(继承 `fork_*`/`lineage_root_id`,同事务写 `InitialMessages`,按开关复制 todos / todo_groups / plan_summaries)或恢复目标会话(只改状态);任一步失败整体回滚。`SoftDeleteAsync` 级联子会话;`PurgeAsync` 只删 `Deleted` 会话及其子会话在 forge.db 内所有以 `session_id` 为键的行(含 FTS),被 `inherits_from_session_id` 指向时抛 `SessionPurgeRefusedException`。常规查询排除 `Deleted`,`ListHistoryAsync` 包含之。`HealMissingDirectoriesAsync` 把目录缺失而项目根仍在的 `Active`/`Archived` 根会话(级联)置 `Deleted`,写 `heal-deleted` 通知(只列根 id);`ReadNoticesAsync`/`AcknowledgeNoticesAsync` 供 App 启动时通知一次。

**完整继承与提供商缓存。** 完整继承是父指针而非复制:`SessionHistory.LoadAsync` 先递归装入父会话截至 `inherits_up_to_seq` 的“摘要视图 + 尾部”(与 `ResumeRootAsync` 同一裁剪函数,标记 `AgentMessage.Inherited`),再接自己的消息;首个请求的消息前缀与父会话最后一次请求逐字节相同(测试断言)。压缩时继承部分作为整体前缀原样保留,永不进入摘要请求,只摘要本会话自己的消息,预算扣除继承部分(不清父指针)。`HandoverSummary.GenerateAsync` 是交接专用摘要入口(专用提示词,不复用压缩器),`InheritanceNote` 构造继承说明消息(`Build` 用于接手,`BuildFork` 用于并行分叉:写明源分支、分叉分支、起点 sha 与可能的先提交 sha,并说明源会话另行继续)。每次合成后计算 system + tools 的 `PromptFingerprint`,仅变化时写回 `prefix_fingerprint`。`ChatRequest.CacheRoutingKey` = `lineage_root_id`:OpenAI 兼容按 `ProviderOptions.SendPromptCacheKey`(`settings.json` 的 `provider.sendPromptCacheKey`,默认关)发 `prompt_cache_key`,DeepSeek 一律不发;DeepSeek 用量 `prompt_cache_hit_tokens` 计缓存读取(与 `cached_tokens` 同时出现时以后者为准)。每会话首个请求的缓存用量连同 `origin_kind`、提供商、模型写一条诊断日志。`AgentOptions.MessageCacheTtl`(`provider.promptCacheTtl`)填进 `CacheHint.MessageTtl`,Anthropic 适配器目前忽略它(见未决)。

**压缩触发(`CompactionPolicy`)。** 以实例自身模型的上下文窗口 W 为基准,不再用绝对 token 阈值。预留输出 R = min(maxOut, ⌊0.2W⌋)(maxOut 未知时取 ⌊0.2W⌋),再 R = max(R, min(4096, W/2));可用预算 U = W − R。触发线 ⌊U·TriggerRatio⌋(默认 0.85),目标 max(1, ⌊U·TargetRatio⌋)(默认 0.5),再生长量 ⌊0.1U⌋。maxOut 取 `model.MaxOutputTokens`,否则取解析器;窗口与 maxOut 都由 `CompactionPolicy` 的解析器给出(App 接模型目录),解析器抛异常按未知处理。当前上下文 C:模型 key 与历史版本都与上次请求一致时,C = 上次真实 `InputTokens`(各适配器已含缓存读写,不再重复相加)+ 此后新增消息的估算;否则 C = 历史全量估算 + 稳定前缀。迟滞:一次压缩后 C 仍不低于触发线即为“无效尝试”,下一次要等实例自有(非继承)内容再长 0.1U;连续两次无效后标记 Stalled 并发一次 `AgentCompactionStalled`,直到手动压缩或换模型。策略关闭、压缩器为 `NoopCompactor`、Stalled、W 未知时都不自动压缩;子 Agent 各按自己的模型窗口计算。保留量 keepRecent = max(1, min(20000, ⌊0.2U⌋)),经 `CompactionRequest` 传给压缩器;自动与溢出路径再把它夹到不超过目标的一半,保证仍能压到目标。切分规则:目标原文量 goal = max(keepRecent, 目标 − 继承前缀 − 现有摘要),保留不超过 goal 的最长合法后缀;一条都放不下时只留最新的合法切点之后的部分。消息按整条保留,所以单条消息跨越边界时实际保留量可能低于 keepRecent。`AgentHost` 构造时 `Compaction.Validate()` 拒绝非法比例。

**上下文溢出兜底。** `ProviderErrors.IsContextOverflow` 判定溢出(显式 Data 标记或 413;否则按短语匹配,但可重试错误与 429 一律不算)。仅当流里尚未收到任何内容时:强制压缩一次(目标 min(C·TargetRatio, 预算目标)),再重发一次(keepRecent 按预算计算,W 未知时取 20000,同样夹到目标一半以内);第二次溢出或压缩没腾出空间时以原错误失败。W 未知时也生效;策略关闭或 Noop 压缩器时不生效。

**手动压缩。** `AgentHost.CompactAsync(instance, sink, [instructions,] ct)` 以目标 1 压缩、无视触发线与 `Enabled` 开关,并重置迟滞;保留 keepRecent(W 未知时 20000)的近期原文,不再只留最新一条。`instructions` 非空时作为 `

Additional focus: <text>` 追加到每个摘要请求的指令末尾。摘要被拒时抛 `CompactionFailedException`,由调用方显示失败。调用方须保证同一实例没有回合在跑。无会话存储时压缩不生效。

**压缩内容(`SummaryCompactor`)。** 系统提示词是 `compaction.summarizer` 槽位(默认 `SummarizerPrompt`),规定结构化检查点格式:Goal / Constraints & Preferences / Progress(Done / In Progress / Blocked)/ Key Decisions / Next Steps / Critical Context。待摘要的消息序列化成一条 user 消息:`<conversation>` 内逐条写 `[User]:`、`[Assistant]:`、`[Assistant tool calls]: name(k=v, …)`、`[Tool result]:`(错误结果标 `(error)`),thinking 块不写,工具结果截到 2000 字符并加 `[… N chars truncated]`,图片与文件只写占位。首次用初始指令;已有摘要时用 `<previous-summary>` 包住上一份摘要正文(会话摘要在前、旧阶段摘要随后,文件标签已剥离)并用 UPDATE 指令增量更新。文件追踪:从 `read`(读)与 `edit`/`write`(改)调用的 `path` 参数收集,与上一份摘要里的 `<read-files>`/`<modified-files>` 合并,既读又改的只列在 modified 下,排序后追加在存储的摘要末尾。拆分回合:切点落在一个仍在进行的长回合内部的 assistant 消息上时,该回合切点前的前缀单独用 TURN-PREFIX 指令摘要(Original Request / Progress So Far / Context Needed to Continue),与历史摘要并行请求,合并为 `<历史摘要>

---

**Turn Context (split turn):**

<前缀摘要>`(无更早历史时只有后半)。摘要请求不带工具、Thinking 关闭、带 SessionId,输出上限取模型自身值与 8192(前缀 4096)中较小者。回复以 MaxTokens 截断、发起工具调用、提供商报错、没有完成消息或正文为空时拒绝该摘要,抛 `CompactionFailedException`,什么也不写;取消照常传播。自动与溢出路径把拒绝当作“没腾出空间”(计入无效尝试,不让回合失败)。

**图片输入。** `ImageBlock(MediaType, Data)` 是用户消息的内容块(base64),`AgentHost.RunAsync(instance, ChatMessage, ct)` 接收带图片的用户消息。三个适配器按各自协议编码(Anthropic `image` source、OpenAI 兼容 `image_url` data URL、Responses `input_image`);`MessageJson` 原样持久化。`TokenCounter` 每张图按固定 1600 token 估算。压缩器给摘要模型的永远只是占位文本 `[image: <type>, <n> KB]`(文件为 `[file: <name>, <type>, <n> KB]`),原图与文件内容不进摘要请求。

**文件附件。** `FileBlock(MediaType, Data, Name?)` 承载 PDF/音频/视频(base64)。各协议能承载的种类是静态表:`AnthropicProvider.WireInputs`/`OpenAiCompatibleProvider.WireInputs`/`OpenAiResponsesProvider.WireInputs`,按媒体类型细判用 `ProviderAttachments.CanCarry(kind|protocol, mediaType)`。编码:Anthropic PDF → `document` base64 块;OpenAI 兼容 PDF → `{"type":"file","file":{filename,file_data:data URL}}`、wav/mp3 → `input_audio`;Responses PDF → `input_file`(filename + file_data)。线路形状不存在的组合(Anthropic/Responses 的音视频、Chat Completions 的其他音频与视频)在**构造请求体时**抛不可重试的 `ProviderException`,请求不会发出,绝不静默丢弃。`MessageJson` 以 `"type":"file"`(media_type、data、可选 name)持久化;搜索/全文/压缩输入里只出现占位 `[file: <名称或类型>, <n> KB]`;`TokenCounter` 按每个已开始的 KB 250 token 估算(PDF 经验值,音视频同用)。文本文件不是 `FileBlock`:由 `TextAttachment` 内联成带 `File: <名称>` 头和自适应反引号围栏的 `TextBlock`。

**带附件的插话。** `AgentInstance.Interject(IReadOnlyList<ContentBlock>)` 与 `Interject(string)` 并存;`TakeInterjections()` 返回 `Interjection`(`Content`、键入文本 `Text`、附件 `Attachments`)。送达时所有待送插话合并为**一条**用户消息:键入文本以空行(`"\n\n"`)连接成一个文本块在前,随后按顺序是全部附件;`AgentInterjected` 事件只带键入文本。

**`ask_user`。** `AskUserTool` 仅在 `AgentHostOptions.QuestionHandler` 存在时注册:模型给出问题与可选项,回合挂起等待处理器返回答案(选项或自由文本),取消即返回取消结果。它是只读工具,默认策略放行。

**Web 工具(`Tools/Web/`)。** `WebFetchTool` 只取 http/https,默认拒绝回环、私网、链路本地地址(`AllowPrivateNetwork` 每次调用现读),重定向手动跟随且每跳重查(最多 5 跳),正文上限 5 MB,超时 30 秒,HTML 经 `HtmlToText` 转成纯文本后按长度截断。私网判定以连接时为准:App 给它的 `HttpClient` 来自 `NetworkStack.CreateHandler`,`AddressFilter` 在 `ConnectCallback` 里检查实际拨号的地址,拒绝时抛 `NetworkAddressRefusedException`,工具把它报成与预检相同的私网拒绝,因此 DNS rebinding 无效;请求前的解析预检只在请求走代理时保留(经 `ConnectsDirectly` 判断,代理连接看不到目标地址),直连时跳过以免重复解析。`WebSearchTool` 走可替换的 `IWebSearchBackend`(Brave、Tavily、SearXNG),始终注册,每次调用经工厂现取后端,未配置时返回“请让用户在 Web 工具设置里配置”的失败结果;设置改动对已开会话即时生效。两者由 App 作为外部工具注入,默认策略为“询问”。

**网络(`Net/`)。** `NetworkStack` 实现 `INetworkStack`,是全部出站 HTTP 的唯一来源(提供商、模型列表、models.dev、MCP HTTP 传输、Web 工具;`HostClient` 的本机 IPC 不经过它)。`Apply` 原子替换设置并触发 `SettingsChanged`,之后新建的连接即按新设置走;`CreateHandler` 产出的 `SocketsHttpHandler` 的 `Proxy` 与 `ConnectCallback` 都在每次连接时读当前设置,所以已建的客户端无需重建,但连接池里的旧连接最长保留 `PooledConnectionLifetime`(默认 5 分钟)。代理:`system` 用 `HttpClient.DefaultProxy`,`none` 直连,`manual` 用 `proxyUrl`(http/https/socks4/socks4a/socks5,userinfo 转为凭据并从 URL 去掉),`proxyBypass` 支持 `*`、`*.域名`/`.域名`(含自身)、精确主机与 IP,回环与 `localhost` 恒直连;manual URL 无效时回退到 system。DNS:`system` 走 `Dns.GetHostAddressesAsync`;`servers` 是手写的 RFC 1035 客户端(`DnsWire` 编解码,A 与 AAAA 并发查询,UDP 先发、TC 置位改 TCP,按服务器顺序逐个尝试,超时或 SERVFAIL/REFUSED 换下一台,NXDOMAIN 立即失败;校验事务 id 与问题段,忽略伪造或错位的报文);`doh` 按 RFC 8484 POST `application/dns-message`(id 为 0,只允许 https,回复上限 64 KB)。两者共用按 TTL 过期的 `DnsCache`(TTL 0 不缓存,失败不缓存,容量满时先清过期项再逐出最早到期项)。解析失败抛 `DnsResolutionException`(`SocketException` 子类,带主机名)。连接只在直连时解析并过滤:`ConnectCallback` 的端点等于请求 URI 的主机与端口且不是 CONNECT 时视为直连,先解析(IP 字面量不解析),逐个地址过 `NetworkHandlerOptions.AddressFilter`,全部被拒抛 `NetworkAddressRefusedException`(`Find` 沿内层异常链查找);走代理时端点是代理本身,目标由代理解析,不做过滤。`CreateResolver(candidate)` 按候选设置造一次性解析器,供设置页“测试”使用而不应用。`NetworkSettingsRules` 是设置校验的唯一实现(服务器、DoH URL、代理 URL、绕过模式、`Problems`),App 的存储与设置页都调用它。

**提供商。** OpenAI 兼容、Anthropic 与 OpenAI Responses(`POST {baseUrl}/responses`,只在提供商模型只走 Responses 时使用:Meta 与 ChatGPT 订阅;请求用 `instructions` 承载系统提示、`input` 承载消息与工具调用、`reasoning.effort` 承载推理档、`store:false`;流按 `output_text.delta`/`function_call_arguments.delta`/`response.completed` 解析,七档里 `Max` 与 OpenAI 兼容一样夹到 `xhigh`)。重试只发生在首个事件之前;`HttpClient` 必须是无限超时,改由每次读取的空闲超时(默认 120 秒)判定停滞。`ProviderConfig.Headers` 经 `ProviderHeaders.Apply` 写入每个请求(覆盖同名头),值中的 `{session}` 取 `ChatRequest.SessionId`(`AgentLoop` 填本会话 id,不是 lineage 根);无会话时该头不发,模型列表请求同样不发。`ProviderException` 的 Retry-After 暂存于 `Exception.Data`(契约缺口,未确认是否正式化)。OpenAI 兼容适配器在写 wire 消息时合并相邻的同角色 `user`/`assistant` 消息:文本按序拼接、段落间空一行,`tool_calls` 保持顺序;`tool` 角色消息不合并,`system` 保持现状(系统提示词在最前,历史中的 system 消息按原位置逐条写出)。内部历史不变,序列化结果对同一历史字节稳定。

**提供商认证与端点。** `ProviderConfig.AuthStyle` 决定凭据怎么走:`Default`(各协议自己的约定:Anthropic `x-api-key`,OpenAI 兼容 bearer)、`BearerOnly`(OAuth 与 Copilot 用的 `Authorization: Bearer`,Anthropic 走这条时另加一个逗号连接的 `anthropic-beta: claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14`)、`None`(不写凭据头,凭据全部由调用方放进 `Headers`)。`Headers` 在 `ApplyAuth` 之后逐个覆盖同名头,所以网关能用自定义方案而不是被内置方案绑死。`SystemPromptPreamble` 非空时 Anthropic 的 `system` 数组**第一块**必须是它(单独一块文本),cache 断点仍在最后一块——非 Haiku 的订阅请求缺这一块会被服务端以 400 拒绝。

- **Claude 订阅凭据(`ClaudeSubscriptionCredentials`)。** 读本机 `~/.claude/.credentials.json` 的 `claudeAiOauth`;距 `expiresAt` 5 分钟内(或过期、或到期时间缺失/单位不可判)用 refresh token 向 `https://platform.claude.com/v1/oauth/token` 换新(client_id `9d1c250a-e61b-44d9-88ed-5944d1962f5e`,请求 UA 用 `axios/…`,因为带 `claude-code/` 前缀的 UA 会被限流),成功后原子写回原文件并保留全部外来字段;写回失败只记一行 Trace,不让本轮失败。无文件/无该节点/空 token 返回 `None`(不是异常);JSON 坏、刷新失败抛不可重试的 `ProviderException`,消息里永远不含 token。
- **GitHub Copilot(`CopilotAuthentication`)。** 设备码 `POST https://github.com/login/device/code`(client_id `Iv1.b507a08c87ecfe98`,scope `read:user`)→ 轮询 `POST /login/oauth/access_token`(pending/slow_down 返回 null,其余错误抛)→ `GET https://api.github.com/copilot_internal/v2/token` 换会话 token(`Authorization: token <ghu_…>`,带 `Copilot-Integration-Id: copilot-chat` 与 `Editor-Version`/`Editor-Plugin-Version`),会话 token 约半小时有效、`IsValidAt` 留 60 秒余量。这是非官方客户端用法,界面必须如实标注。
- **模型列表(`ProviderModelListing`)。** 原先在 App 的 `/models` 解析搬到这里:`GET {baseUrl}/models`,调用方给头,20 秒链接超时(超时抛 `TimeoutException`),按服务端顺序去重;上下文窗口认 `context_length`/`max_context_length`/`top_provider.context_length`,价格认 `pricing.prompt|completion|input_cache_read|input_cache_write` 的**每 token** 值并换算成每百万 token。
- **内化落点(`Providers/Internal/ChatProviderPipeline`)。** 参照 microsoft/agent-framework(`Microsoft.Extensions.AI` 的 `ChatClientBuilder.Use`)的**中间件管道形状**:端点只管协议,重试作为最外层中间件(`RetryMiddleware` 就是 `RetryingChatProvider` 的管道包装)。没有逐字拷贝上游文件,也没有引入它的 OpenAI/Anthropic SDK 客户端——本适配器直接讲 wire 协议;provenance 记在 `src/Forge.Core/THIRD_PARTY_NOTICES.md`。
- **预设(`ProviderPresets`)。** 纯数据:协议、base URL、环境变量、文档链接、种子模型、是否支持 `/models`。它是"便捷接入"的唯一来源,不参与运行时判断;某个预设被删后旧配置仍按自己的 URL 工作(降级为自定义)。

**模型元数据与用量换算(`Providers/`)。** 没有内置的价格表:链是 **目录快照 → provider 上报的元数据 → 用户 `settings.json` 的 `providers[].modelMeta`**(`ModelMetadataResolver.Resolve`),逐字段合并(`ModelMetadata.With`),任何一半未知就保持未知,绝不补 0——离线时跑不了 agent,需要价格的时刻一定有网,而编译期的旧价只会悄悄错。目录快照来自 models.dev 的 `api.json`:`ModelCatalogParser` 把它读成紧凑的 `ModelCatalogSnapshot`(`limit.context`/`limit.output`、四个价格、`modalities.input`、`reasoning` 与其 `effort` 档位),`ModelCatalogFetcher` 用存下的 `ETag` 发条件请求(304 即“无需更新”)并在超时/状态/形状失败时返回消息而不是异常,`ModelCatalogJson` 负责 `model-catalog.json` 的读写(损坏或版本不同即当作没有快照)。`ModelCatalogView` 负责查:`ModelIdRules.Normalize` 归一化 id(小写、去掉 `vendor/` 前缀与 `:free`/`-latest`),先按“预设 → 目录提供商”映射取该提供商自己的条目(同一模型各网关价格不同),映射不到的自定义提供商按模型 id 全局兜底(一方优先序,首方优先),id 匹配是“精确 → 最长已知 id + 快照后缀(`-yyyyMMdd`/`-vN`/`-latest`)→ 包含”,版本号递增(`gpt-4o` → `gpt-5`)不继承。`SessionUsageMath` 全是纯算术:缓存命中率以 `InputTokens` 为分母(OpenAI 兼容的 `prompt_tokens` 与 Anthropic 的 `input + cacheRead + cacheWrite` 都把缓存折进输入)、计费输入先减去缓存、每个 token 数夹到 ≥ 0 后按每百万单价计费、窗口占比夹到 0..1。展示层(百分比、`9% / 1.0M`、`$0.041`)与它无关,见 forge-app 的会话输入框一节。

**自动化(`Automations/`)。** 契约在 `Forge.Abstractions/Automations.cs`,引擎与调度在这里,参考 CodeAlta.Automations(BSD-2,见 `THIRD_PARTY_NOTICES.md`)的调度形状,未逐字拷贝。
- `AutomationSchedule`:间隔(每 N 小时的第 M 分)、每日若干时刻、每周若干天的若干时刻、5 段 cron;按本地时区算下一次,DST 跳过/重复的时刻各只触发一次。`TryCreate` 给出校验错误,设置页的“接下来 5 次”预览直接用它。
- `AutomationEngine`:单个后台循环,最长睡 30 秒;按定义的下次触发时间启动运行。忙键 =(定义 id, 目标会话 id),同键已有活动运行时新请求返回 `Busy`;同一 `Occurrence` 只跑一次(`Duplicate`)。链条深度上限 `MaxChainDepth = 5`(运行由另一次运行引起时深度 +1),运行不能触发自己(`SelfTriggered`);带 `TargetKey` 的请求计入尝试预算(`budget:<key>`,到额即 `BudgetExhausted`,由调用方 `ResetTargetBudgetAsync` 清零)。每分钟写一次心跳 `engine.alive_at`;启动时把上次未结束的运行标为中断,`CatchUp` 定义在停机期间错过的触发补跑一次。运行状态更新按运行串行(`RunContext.UpdateAsync` 持每运行一把锁)。`ResumableWhileIdle`(目前只有额度恢复)的运行在关闭时若停在 `WaitingIdle`,启动时改回 `WaitingQuota`(无恢复时刻)续跑,其余 `Running`/`WaitingIdle` 标为中断。`StartAsync` 不检查定义启用/暂停——事件启动的运行由调用方先判(App 的 `AutomationService.IsEnabled`)。
- 存储:schema v10 新增 `automation_runs`(运行历史,`TrimAsync` 按定义保留条数与总条数修剪)、`automation_state`(键值:心跳、预算、各 runner 的持久笔记)与 `sessions.automation_id`/`automation_run_id`(会话由哪次运行创建)。
- 额度用尽:`ProviderQuota.Classify` 把 402、带额度标记或长 Retry-After 的 429、带额度标记的 400/403 归为 `ProviderQuotaExhausted`。额度标记分三级:确定标记(`insufficient_quota`、`usage limit reached`、`billing_hard_limit_reached`、`credit balance is too low`、“余额不足”等)直接算额度;智谱额度错误码次之;文本里出现速率措辞(`rate limit`、`per minute`、RPM/TPM、“请求过于频繁”“频率”“并发”等)时一票否决,其后才看措辞型标记(`quota exceeded`、`usage limit`、“使用上限”)与周期限额/中文额度句式,避免把限流当成额度(含 provider、模型、可解析出的重置时间;未知错误从不算额度),重试层对它不重试,`AgentHost` 以 `AgentQuotaExhausted` 事件上报且不重试;`QuotaProbeSchedule` 在没有重置时间时按退避间隔用最小请求探测额度是否恢复(用户已接受探测的额外消耗)。
- `AgentHost` 可在回合之外启动子 Agent(自动化的 Scout 用它),结果与普通子 Agent 相同。

- **提示词模板(`Prompts/Templates/`)。** `PromptTemplateRegistry` 收编译期内置模板(Core 的四个 Agent、四个命令、`system`/`compaction.summarizer`/`handover.summary` 三个槽位;组合根可 `With` 追加自己的槽位),`PromptTemplateStore` 按 内置 → `~/.forge` → `<工作区>/.forge` 分层读写 `<种类目录>/<id>.md`,`Resolve` 得到不可变的 `PromptTemplateSet`(命令、Agent 定义、槽位文本;`FormatSlot` 单次替换 `{名字}`,`TryExpandCommand` 展开 `/名称 参数`)。frontmatter 由手写的 `PromptFrontmatter` 解析(无 YAML 库,AOT 安全)。`AgentHostOptions.Templates` 给默认压缩器提供 `compaction.summarizer`,`HandoverSummary.GenerateAsync` 可传入 `handover.summary`;`PromptComposer` 把 `PromptLayers.System` 当作完整系统层(即生效的 `system` 槽位),空时回落 `BuiltInPrompts.System`——此前它总把内置文本再拼在前面,调用方又传了同一段内置文本,系统层重复了两次。Agent 以名称派生,名称即身份:内置 Agent 的覆盖文件不得改名,用户 Agent 不得占用内置名或内置 id,空 `tools` 列表无效;`PromptTemplateSet.ResolvedAgents` 给出每个 Agent 的模板 id(设置键)与 `thinking` 是否由文件显式给出。`Read` 对超限或非 UTF-8 文件抛 `IOException`。
## 未决

- 事件通道保持无界(决定):`AgentHost.RunAsync` 的 `Channel.CreateUnbounded<AgentEvent>` 不加背压、不丢事件;UI 合并在 App 侧由 `FrameCoalescer` 实现,见 forge-app。
- `MaxTurns` 计入后续指令续跑;不响应取消的工具要等其返回才真正停止。
- 内置 Agent 的工具白名单未含 `todo`(加入会改动其可缓存工具集),目前只有根 Agent 可用。
- Artifact 转存阈值与 keepRecent(20000 / 0.2U)使用默认值,未经使用验证。
- 摘要请求的对话文本上限 400000 字符,更长时截掉其后的消息;多次拆分回合会让多段 Turn Context 叠在更新后的摘要里,由 UPDATE 指令合并;文件追踪只认 `read`/`edit`/`write` 的 `path` 参数。
- Anthropic `promptCacheTtl: "1h"` 未接线:官方文档要求 1 小时缓存条目排在任何 5 分钟条目之前,与“只给最后一个消息断点加 1h、工具与 system 保持 5 分钟”的设计冲突,待定(三个断点都 1h,或只留消息断点)。
- 完整继承跨不同前缀(换模型 / 推理档位)时 `ThinkingBlock` 可能被拒;`search_history`/`read_history` 不沿继承链回溯。
- 完整继承且继承前缀本身已超过触发线时,迟滞与 Stalled 已把重复压缩限制为最多两次无效尝试,但根因未除:长期解法仍是持久化压缩后的表示或历史边界(新的存储契约,归 T0),本阶段不做。压缩估算不计工具定义。
- Tavily 的 Bearer 认证方式未经实测。
- 网络栈:socks4(非 4a)代理由 .NET 在本机用系统 DNS 解析目标;DoH 服务器自身的主机名用系统解析器解析(写 IP 形式的 URL 可避免);代理服务器地址本身不过地址过滤;连接池旧连接最长 5 分钟内仍走旧路由。
- 多个 `ask_user` 并行调用会同时出现多张问题卡,未串行化。
