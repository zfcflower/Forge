# 12 存储与数据

本文件说明 FORGE 的数据目录、数据库 schema、设置文件与密钥库。所有写入都假定“进程可能在任意时刻被杀”，因此要么原子替换、要么事务。

## 12.1 数据根与目录布局

- 数据根默认 `%LOCALAPPDATA%/Forge`，`FORGE_DATA_DIR` 覆盖。
- 常规工作区默认 `文档/Forge/General`，`FORGE_GENERAL_WORKSPACE` 覆盖。
- 开发验证一律设置这两个变量，不写真实用户目录。

```
<数据根>/
├── forge.db                 # 会话、消息、摘要、TODO、项目、ui_state、检查点、自动化、artifact 索引
├── reviews.db               # 评审评论、标记、结论
├── approvals.db             # 插件审批记录（Host 所有，App 只读）
├── settings.json            # 偏好、提供商、Agent 默认、web/network/compaction/modelCatalog/usageLog/claudeWire
├── settings.json.bak        # 首次写入损坏的 settings.json 前的备份（只备份一次）
├── provider-secrets.json    # 提供商密钥库（AES-GCM）
├── provider-secrets.key     # 密钥库旁钥（32 字节，非 Windows 上 0600）
├── provider-models.json     # 各提供商 /models 上报的窗口与价格
├── model-catalog.json       # models.dev 紧凑快照
├── mcp-secrets.json         # MCP OAuth token（同一套加密旁车件）
├── approval-rules.json      # 记住的审批规则（项目级与永久）
├── automations.json         # 自动化定义
├── forge.instance.lock      # 单实例锁（独占打开，进程死亡由 OS 释放）
├── builtin-skills/<hash>/   # 产品内置技能的物化目录
├── artifacts/<会话 id>/      # 超长工具输出
├── logs/forge-YYYYMMDD_N.log
├── usage/usage-YYYYMMDD.jsonl
├── updates/<version>/       # 下载暂存、app/、install.log、ok-<version>
└── purge-pending.txt        # 彻底删除后待重试的会话 id
```

## 12.2 `forge.db`

- `PRAGMA user_version` 是 schema 阶梯，当前 **12**。每步在一个 `BEGIN IMMEDIATE` 事务内执行并同事务写版本号，事务内重读版本防止多进程重复迁移；读到更高版本立即 `InvalidDataException`，不降级不写库。
- 阶梯要点：
  - 1：工作台表。
  - 2：`todos` 加 `commit_subject`/`summary`/`verification`/`commit_id`/`commit_error` 五列 + `plan_summaries(session_id PK, text, updated_at)`。
  - 3：一次性规范化历史时间戳并为每个时间戳列装写守卫。
  - 4：`todo_groups(id PK, session_id, title, order_index, updated_at)` 与 `todos.group_id`/`todos.origin`（旧行 `group_id` 保持 NULL 即待排区、`origin` 默认 `agent`）。
  - 5：会话占用键回填 + `ux_sessions_occupancy`（`status='Active' AND parent_session_id IS NULL`）唯一索引；按键去重保留 `last_active_ticks` 最大者，被归档者写 `storage_notices`（`dedupe-archived`）。
  - 6：`deleted_at`。7：`projects.closed_at`（软关闭，同路径再添加复活原 id）。8：`fork_of_branch`/`fork_base_sha`。9：`inherits_from_session_id`/`inherits_up_to_seq`/`lineage_root_id`（回填自身）/`origin_kind`（回填 `cold`）/`prefix_fingerprint`。
  - 10：`automation_runs`、`automation_state`、`sessions.automation_id`/`automation_run_id`。
  - 11：`plan_summary_revisions(id 自增 PK, session_id, text, replaced_at)` + `ix_plan_summary_revisions_session(session_id, id DESC)`。
  - 12：`todos.baseline_tree`（步骤首次转 `in_progress` 时的工作树内容指纹，旧行 NULL，不回填）。
- 关键表：`sessions`、`messages`、`summaries`、`messages_fts`（或回退 `messages_text`）、`artifacts`、`projects`、`todo_groups`、`todos`、`plan_summaries`、`plan_summary_revisions`、`ui_state`、`turn_checkpoints`、`storage_notices`、`automation_runs`、`automation_state`。
- 路径唯一性：`projects.root_key`（Windows 大写）唯一索引；General 项目由部分唯一索引保证单例，再次注册即改指向。
- 时间戳只存 `ToString("O")` 形状（带 offset、固定 7 位小数）的文本，读取严格；3 号步骤先就地规范化（有 Z 或 ±hh:mm 且能解析出唯一时刻的改写为规范形状，时刻与 offset 不变；无 offset/不可解析原样保留，由严格读取指名报错）。规范化后给 13 张表 20 个时间戳列建 `BEFORE INSERT`/`BEFORE UPDATE` 触发器，任何写者都写不进非规范文本（可空列仍允许 NULL）。新增时间戳列必须登记进 `StorageSchema.TimestampColumns`，否则既不会被规范化也没有守卫。`artifacts` 由 owner 在阶梯外自建，其 `at` 列经 `EnsureOwnedTimestampsAsync` 装同一份守卫。
- 索引刷新：`Artifact` 内容存文件、`forge.db` 只存行；`FileArtifactStore` 按会话删除目录。
- 全文索引：`messages_fts`（FTS5）与阶梯解耦（可用性取决于 SQLite 编译选项），首次建表与回填同事务；建过 FTS5 的库被无 FTS5 的构建打开时补建 `messages_text` 回退表并回填，写与搜索都落到回退表；换回能读 FTS5 的构建时反向回填缺失行，并按 `messages` 修剪降级期留下的孤儿索引行。
- 写模型：`SqliteSessionStore` 一个写队列，一个批次一个 `BEGIN IMMEDIATE`；可能业务冲突的操作（`UsesSavepoint`，如占用冲突）包在 `SAVEPOINT forge_op` 里，单个冲突只回滚到自己的保存点并以异常交还调用方，同批其它操作照常提交；其余操作失败仍连坐整批。返回 seq 时已落盘，提交后的取消不会让调用方误判失败。
- 读写连接：工作台各 store 每个一条长连接 + `SemaphoreSlim` 串行 + 每写一个 `BEGIN IMMEDIATE`（deferred 读后升级会静默丢更新），读用池化短连接；`synchronous=NORMAL`（体验数据）。
- 计划摘要修订：替换时在同一事务记被替换的旧文本（相同则不记），每会话保留最新 50 条；`ListPlanRevisionsAsync` 按 id 倒序、limit 夹到 1..50；复制会话一并复制、清理会话一并删除。
- 会话生命周期与写入不连坐、`HandoverAsync` 单事务、`SoftDeleteAsync` 级联子会话、`PurgeAsync` 只删 `Deleted` 及其子会话在 forge.db 内的所有 `session_id` 行（见 01）。`HealMissingDirectoriesAsync` 把目录缺失而项目根仍在的根会话置 `Deleted` 并写 `heal-deleted` 通知，启动时通知一次。
- `ui_state`：`layout`（v5）、`open-tabs`（v2）、`onboarding`（schema 版本 1）、`updates.lastCheck`。
- 自动化：`automation_runs` 按定义保留 200 条、总 2000 条修剪；`automation_state` 存心跳、尝试预算与各 runner 的持久笔记。

## 12.3 `reviews.db`

- 三张表：`review_comments`、`review_marks`、`review_conclusions`。
- **schema 没有版本号**：`EnsureInitializedAsync` 跑一份幂等脚本（`PRAGMA journal_mode=WAL` + `CREATE TABLE/INDEX IF NOT EXISTS`），后续波次加表只是往这份脚本里追加，已有库下次打开即补齐，任何版本都不 drop 或改写列。
- `review_comments`：行/块评论、草稿与已发送；`sent_at` 幂等（重复标记不改写原始时间）。
- `review_marks`：已查看按文件 diff 指纹、已保留按块内容哈希（内容相同的块按出现序号区分）。
- `review_conclusions`：一次 `request_review` 一行——session、工作目录、`step_id` 或 `base`、HEAD、`outcome` 整数（`0 Unstructured / 1 Approve / 2 RequestChanges / 3 Stopped / 4 Failed`，只允许在末尾追加新值）、Reviewer 原文、时间。
- 三张表都按 session 归属：`CopySessionAsync`（接管/分叉）复制且只换 `session_id`；`DeleteSessionAsync`（彻底删除）一起删。
- 不可释放：池化句柄靠 `SqliteSessionStore` 拆除时进程级 `ClearAllPools` 才释放（见 14）。

## 12.4 `approvals.db`

详见 [11-extensions-and-codemode.md](11-extensions-and-codemode.md) 11.4。要点：`user_version` 阶梯（0/1/2）、`WITHOUT ROWID`、`BEGIN IMMEDIATE` 每写、`synchronous=FULL`、WAL 不可用回退回滚日志、构造期 fail-closed、App 只读。

## 12.5 `settings.json` 与三个写者

- 段：`preferences`、`providers`、`agents`、`web`、`network`、`compaction`、`modelCatalog`、`usageLog`、`claudeWire`（以及迁移期只读的旧 `provider` 段）。
- 三个写者（`preferences`、`providers`、`agents`）都会把整份文件读—合并—原子写；`SettingsFileMutex` 按路径互斥，否则后写者会把先写者的分区合并丢。`AtomicFile` 只保证文件完整、不保证合并正确，所以互斥是必需的。
- 文件根不是对象或 JSON 无效时拒绝保存，原文件保持不动；未知段与未知字段原样保留。
- 偏好加载：文件损坏或 `preferences` 段无效时用默认值并填 `LoadProblem`，绝不因加载覆盖文件；第一次写入前先把损坏原文件复制成 `settings.json.bak`（只备份一次）。源生成 `PreferencesJsonContext`。
- 设置页保存即写；偏好修改有 500 ms 防抖，`DisposeAsync` 强制落盘。
- 提供商的旧单 `provider` 段只在第一次读到时迁移成一条并按协议推断预设，第一次保存写 `providers` 并删旧段。

## 12.6 其他文件

| 文件 | 内容 | 写入纪律 |
|---|---|---|
| `provider-secrets.json` + `.key` | 提供商密钥与设备码 token bundle | AES-GCM，原子写；损坏明确报 `Problem`，不当“没有密钥”覆盖 |
| `mcp-secrets.json` | MCP OAuth token | 与提供商同一套加密旁车件，文件名与引用空间不同 |
| `approval-rules.json` | 记住的审批规则（项目/全局） | 源生成 JSON、`AtomicFile`、后台线程、写入串行；损坏当作无规则并备份 `.bak`；`rules: null` 视为损坏 |
| `automations.json` | 自动化定义 | 校验错误带本地化键与参数；定义行按当前语言显示 |
| `provider-models.json` | 各提供商 `/models` 的窗口与价格 | 一次刷新只写一次（在池上写，写完才算完成） |
| `model-catalog.json` | models.dev 紧凑快照 | 损坏或版本不同即当作没有快照；304 只更新 `checkedAt` |
| `approvals.db` 的旧 `approvals.json` | 旧版审批记录 | 仅一次导入，导入后改名 `.migrated`（失败不影响正确性） |
| `purge-pending.txt` | 彻底删除后待清理的会话 id | 整文件临时替换写入；只接受合法 id |
| `builtin-skills/<hash>/<name>/SKILL.md` | 四份内置编排技能 | 启动时物化、幂等、best-effort 清旧版本；写不出只记日志 |
| `logs/forge-YYYYMMDD_N.log` | 诊断日志 | 见 13.2 |
| `usage/usage-YYYYMMDD.jsonl` | 用量日志（默认关） | best-effort，绝不因日志让回合失败 |
| `forge.instance.lock` | 单实例锁 | 独占打开、不删文件；进程崩溃由 OS 释放 |

相关文档：[01-sessions-and-workspaces.md](01-sessions-and-workspaces.md)（会话状态与事务）、[11-extensions-and-codemode.md](11-extensions-and-codemode.md)（审批库）、[13-operations-and-tooling.md](13-operations-and-tooling.md)（日志与更新）。
