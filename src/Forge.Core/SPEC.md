---
id: forge-core
type: module-design
status: draft
title: Forge.Core
parent: forge-architecture
depends-on: [forge-architecture]
---

# Forge.Core

Agent 循环、模型提供方、提示词合成、SubAgent 调度与会话存储。只依赖 `Forge.Abstractions`;不含 UI、Git、MCP、插件加载。

## 边界

- 对外契约在 `Forge.Abstractions`;Core 的实现类通过这些接口被组合,审批弹窗、工具实现、工作区由上层提供。
- 审批:Core 只做策略求值(`IApprovalPolicy`)并在需要时调用 `IApprovalHandler`;没有处理器时按拒绝处理。
- 外部依赖仅 BCL 与 Microsoft.Data.Sqlite。

## 关键决策

**缓存纪律。** 请求的系统提示词只含稳定前缀(系统 → 全局 Agent → 项目 Agent → 角色,字节稳定);工具定义按名称排序且同类 Agent 一致;动态信息只作为末尾的一条临时 user 消息。缓存断点落在已持久化历史的倒数第二条。压缩只重写最老的一层摘要,不改近期原文。

**循环。** 流式一轮 → 审批 → 执行 → 回写。连续只读工具并发,有副作用工具依序逐个执行;结果按调用顺序写成一条 Tool 消息。取消时每个待决调用都补一条"已取消"结果,保证历史始终合法。超长工具输出转存 Artifact,上下文里只留预览和 id。

**SubAgent。** `spawn_agent` 按 `<kind>-<n>` 登记实例;同类型共享前缀,可按 id 续跑(同类型、且当前未在运行)。活跃 Agent 计数覆盖整棵树;父 Agent 等待子结果时交还名额,否则嵌套会死锁。深度上限 3。该工具对未超深度的所有 Agent 隐式可用,Agent 定义无法关闭它。

**子 Agent 控制。** 每次 spawn 是一个 run,有独立 `CancellationTokenSource`(链到父 run 令牌),父 Agent 的 `subagent_steer` / `subagent_followup` / `subagent_stop` 与 UI 停止按钮共用 `AgentHost.SubAgentControl`(`ISubAgentControl`)。
- 转向在工具批次边界生效:已发出的调用跑完,本回合剩余未开始的调用补 `skipped: the agent was redirected` 错误结果(历史保持合法),指令作为下一条 user 消息注入;边界前的多条转向合并为一条消息、一个事件;批后无剩余调用时按丢弃 0 交付,指令不丢。
- 后续指令在子 Agent 本应结束(assistant 无工具调用)时注入并继续。排队期收到的转向/后续指令合入初始任务。
- 停止只取消该子 Agent:待决工具补 "Cancelled.",待决审批被取消释放;父 Agent 收到**非错误**结果 "Sub-agent '<id>' was stopped (<reason>). Partial output: …",由父决定后续。
- 生命周期 Started(父发出)→ 仅在确实等名额时 Queued → Running → 每个 run 恰一次 Finished(在调度器 `finally` 中,含异常与父取消;父取消记为 Stopped "parent cancelled")。用量为本 run 增量,耗时从 spawn 起算(含排队)。
- `runs` 只保存运行中的 run,结束即移除并释放其 CTS 与父令牌注册;已结束 id 记入紧凑集合以继续返回 `AlreadyFinished`;`instances` 保留以支持按 id 续跑。

**会话目录与恢复。** 会话元数据(项目、worktree、分支、父会话、Agent 种类、任务、归档状态、最后活动)是 `sessions` 表的列,`ISessionCatalog` 与 `ISessionStore` 由同一 `SqliteSessionStore` 实现。能力只经 `SessionCatalogs.From` 读取:`LoggingSessionStore` 总实现该接口,须读 `SupportsCatalog`,内层非目录时按无目录处理。子会话带 `ParentSessionId`/`AgentKind`/`Task` 并继承父会话的 worktree 与项目;每回合持久化后 `TouchAsync`,失败不影响循环。`ResumeRootAsync` 用与压缩器相同规则重建上下文(摘要视图 + 未摘要尾部),丢弃孤立首个工具结果与末尾无结果的工具回合。一个 `AgentHost` 对应一个主会话;`AgentScope` 为 AsyncLocal,名额池与 id 计数在实例内,多主会话可并发。

**`todo` 工具。** 按参数决定副作用(list 只读,其余写入),审批策略与只读并发分批看到单次调用的真实副作用;update/remove 先确认条目属于 `ToolContext.SessionId`。

**存储。** SQLite 存会话、消息、摘要、全文索引(FTS5);JSONL 为事件日志,供崩溃恢复;Artifact 内容存文件。所有写入走单写线程批量提交,返回 seq 时已落盘;提交后的取消不会让调用方误判为失败。摘要三层(近期原文 → 阶段摘要 → 会话级总摘要),只追加不改写,被压缩的原文仍可检索和取回。压缩不得拆开工具调用与其结果。

**forge.db 版本阶梯。** `PRAGMA user_version` 是唯一权威(0 = 无标记旧库,1 = 当前)。每步在一个 `BEGIN IMMEDIATE` 事务内执行并同事务写入版本号,事务内重读版本防止多进程重复迁移;读到更高版本立即 `InvalidDataException`,不降级不写库。旧会话回填 Active 与最后活动 = 创建时间(严格解析,失败整步回滚)。排序用 `last_active_ticks`(UTC ticks)而非 ISO 文本。`messages_fts` 与阶梯解耦(可用性取决于 SQLite 编译选项),首次建表与回填同事务,单条坏消息只跳过(派生数据)。

**工作台存储。** `projects` / `todos` / `ui_state` / `turn_checkpoints` 与会话同库:每个 store 一条长连接 + `SemaphoreSlim` 串行 + 每写一个 `BEGIN IMMEDIATE`(deferred 读后升级会静默丢更新),读用池化短连接。`ITodoStore.Changed` 在提交后、写锁外触发,订阅者异常被吞。路径唯一性靠 `root_key`(Windows 大写)唯一索引,General 项目由部分唯一索引保证单例,再次注册即改指向。TODO 序号会话内致密。检查点以 (session, turn) 为主键,已完成轮次不被再次 Begin 改写。`synchronous=NORMAL`:这些是体验数据,丢失不影响安全(审批库用 FULL)。

**压缩触发。** 阈值是用户设置的绝对 token 数,而非窗口百分比;压缩目标为阈值的一半。无会话存储时压缩不生效。

**提供方。** OpenAI 兼容与 Anthropic。重试只发生在首个事件之前;`HttpClient` 必须是无限超时,改由每次读取的空闲超时(默认 120 秒)判定停滞。`ProviderException` 的 Retry-After 暂存于 `Exception.Data`(契约缺口,未确认是否正式化)。

## 未决

- 事件通道为无界;UI 侧按帧合并尚未实现。
- `MaxTurns` 计入后续指令续跑;不响应取消的工具要等其返回才真正停止。
- 内置 Agent 的工具白名单未含 `todo`(加入会改动其可缓存工具集),目前只有根 Agent 可用。
- OpenAI 兼容适配器不合并相邻同角色消息,对严格的兼容服务可能不被接受。
- Artifact 转存阈值与三层摘要的保留量使用默认值,未经使用验证。
