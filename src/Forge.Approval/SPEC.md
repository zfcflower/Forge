---
id: forge-approval
type: module-design
status: draft
title: Forge.Approval
parent: forge-architecture
---

# Forge.Approval

用户插件的源码哈希审批引擎。纯逻辑 + SQLite 持久化(`Microsoft.Data.Sqlite`);AOT 兼容(无反射,Legacy 导入走源生成的 `ApprovalJsonContext`)。

## 边界

- 持久化记录是权威;`Evaluate` 纯函数无 IO,`Evaluate` 的读路径来自 store 的内存快照(`PRAGMA data_version` 失效),重启恢复 = 新建 store 或 `Reload()`。
- 不负责 UI 审批交互、权限执行(属主程序)或插件发现(属 ExtensionHost)。
- `PluginRecord`、`ApprovalEngine`、`Hashing`、`ApprovalJsonContext` 的实现与语义不为存储机制改变而改动:映射只发生在 `ApprovalStore` 内部的 SQL 行上。

## 关键决策

**组合指纹。** `ApprovedHash` = `SHA256(SourceHash + DependencyHash + SdkVersion)`,三者任一漂移即批准失效;行尾归一化(CRLF/CR/LF 等价)使纯行尾修改不失效。

**状态矩阵。** `Draft` → `RequiresApproval`(可批准提升);`Disabled`/`Uninstalled` → `Blocked`(再批准不可恢复);无记录 → `RequiresApproval`。只有三个候选哈希与记录一致且指纹等于 `ApprovedHash` 才 `Allowed`。审批操作不得绕过封锁:`SetApproved` 只刷新内容哈希并保留 `Disabled`/`Uninstalled` 状态;`Enable` 只解 `Disabled`(有 `ApprovedHash` 回 `Approved`,否则回 `Draft`);`Revoke` 对 `Disabled`/`Uninstalled` 是空操作。

**数据库布局。** `<directory>/approvals.db`,一行一个 `<pluginId, version>`。主键是 `(plugin_id, version)` 的 TEXT 复合键、默认 BINARY collation:与 `StringComparer.Ordinal` 只对逐字节相同的 UTF-8 相等(大小写、尾随空格都算不同键)。`status` 存枚举名字符串;时间戳存 ISO-8601 `"O"` 文本(保留 offset 与 7 位小数,`Round_trip_*` 逐字段相等),另存 `updated_utc_ticks` 专供将来在 SQL 侧排序——不同 offset 的 ISO 文本字典序不等于时间序,`List()`/`Get(pluginId)` 的 ordinal 与“最近更新”语义因此留在 C# 的内存快照里,不在 SQL 里 `ORDER BY`。`approval_meta` 存一次性迁移标记;`PRAGMA user_version` 是 schema 阶梯(0 = 新建或 0 字节空库,1 = 当前,>1 = 由更新版本的程序写过)。表为 `WITHOUT ROWID`(单行约 250 B、按 TEXT 主键访问)。

**持久化。** 每条写操作 = 一个 `BEGIN IMMEDIATE` 事务(`BeginTransaction(deferred: false)`):事务内 `SELECT` 目标行 → 现有状态机算出新值 → 单行参数化 `INSERT ... ON CONFLICT (plugin_id, version) DO UPDATE` → `COMMIT`。SQL 只用参数,不拼字符串。提交成功后才把记录发布到内存快照,因此写失败(磁盘满、只读库、busy 耗尽、取消)时内存仍等于库里已提交的内容——persist-before-publish 不变。状态机的三个 `switch`(SetApproved 保留 `Disabled`/`Uninstalled`、Revoke 不动封锁、Enable 只解 `Disabled`)以“事务内读到的行”为输入,单进程行为与逐文件实现完全一致,多进程时也不再丢更新。

**为什么必须 `BEGIN IMMEDIATE`。** 裸 `BEGIN`(deferred)下,若另一连接在本次事务读之后提交,本连接升级为写会得到 `SQLITE_BUSY_SNAPSHOT`(SQLite 5 / extended 517),busy handler 不介入——这就是一次静默丢更新。IMMEDIATE 在事务开始即取写锁,冲突的写者按 `busy_timeout` 排队后成功。不额外做重试:IMMEDIATE 事务只含一行读 + 一行写(开发者机器上提交 p50 ≈ 2 ms,不是产品承诺:每次提交是一次 WAL 追加加一次 `fsync`,耗时由磁盘决定),busy 预算耗尽(默认 5 s)就把 `SqliteException` 原样上抛,不吞、不包装、不重试。`CancellationToken` 在事务开始前检查(与旧实现相同:不提供“提交中取消”),取消时库与内存都不变。

**连接与 pragma。** 每个 store 一条长连接、`Pooling=false`,在 `lock(gate)` 下串行使用;pragma 顺序固定为 `busy_timeout` → `journal_mode` → `synchronous`(`journal_mode=WAL` 本身要取锁,若 busy 预算还没生效,并发打开会直接 `SQLITE_BUSY`)。`busy_timeout` 与连接串 `DefaultTimeout` 取同一个值(默认 5000 ms):两者会叠加,取同一值让最坏等待就是那个值。`synchronous=FULL`:审批/撤销是安全相关操作,`SetApproved` 返回后 IPC 已回 “ok”,掉电丢掉一条已确认的撤销就是安全回退;WAL 下 FULL 只 fsync 一次 WAL。`Dispose` 先 `PRAGMA wal_checkpoint(TRUNCATE)` 再关连接并 `SqliteConnection.ClearAllPools()`,所以关闭后 `approvals.db`/`-wal`/`-shm` 可被删除或移动(测试必须 `Dispose` store 才删得掉目录)。

**WAL 回退规则。** `PRAGMA journal_mode = WAL` 会返回 SQLite 实际选中的模式,代码读返回值而不是假定成功:返回 `wal` 即用 WAL;返回其他值(如共享内存不可用的网络/重定向目录)则按返回的回滚日志模式继续,并在 stderr 写一行 `forge-approval: '<db>' cannot run in WAL mode (...)`,store 的 `JournalMode` 属性给出实际值。两种模式都正确,差别只在并发读的阻塞;测试不得断言 `-wal`/`-shm` 文件存在。

**读路径与跨进程可见性。** `Count`/`Get`/`List` 从内存快照作答,先读 `PRAGMA data_version`(约 3 µs)再决定是否重读全表:值未变就命中字典;变了就整表重读并换快照(200 行约 400 µs)。`data_version` 先读、行后读:若另一进程的提交落在这两次读之间,本次快照比记录的版本更新,下次只会多重载一次而不会漏掉一次提交。构造期、`Reload()` 与失效重读三条快照路径共用这一顺序,构造期不得反过来:先读行再读版本时,两次读之间落地的外部 `Revoke` 会让版本追上陈旧行,该快照此后永久被当作当前。三条路径都在整表读成功后才同时替换快照与版本;读失败时两者都不动。`Reload()` 是无条件重读全表、成功才换快照(失败保留旧快照),语义仍是“重启会看到的内容”。

**一次性 legacy 导入。** 旧版本写 `<directory>/approvals.json`。打开时若 `user_version = 0`,在**同一个 IMMEDIATE 事务**里建表、导入、写 `approval_meta.legacy_import=<utc>` 并把 `user_version` 置 1:导入条件是无标记、`plugin_approval` 为空、且 JSON 存在;空 `pluginId`/`version` 的行跳过(与旧 `ToMap` 一致);同键后者胜。提交成功后仅尽力把文件改名为 `approvals.json.migrated`(改名失败不影响正确性)。**标记是权威**:导入成功后即使旧 JSON 被旧版本程序或备份还原写回,也不会再被导入,已撤销的审批不会复活——用“表空就导入”的规则会整批复活它们,所以不用。导入只发生在 `user_version` 0→1 的那一次事务里,所以已经是版本 1 的库永远不会再读 JSON(手工拷贝进来的、没有标记的库因此只会补写标记、视作已导入)。`ApprovalStoreOptions.ImportLegacyJson = false` 时跳过读取但同样写标记,即对该库永久放弃导入。

**写延迟没有绝对预算,只有“不比一次 fsync 慢太多”的相对预算。** 一次写 = 一个 `BEGIN IMMEDIATE` + 一行读 + 一行写 + 一次 WAL `fsync`(`synchronous=FULL`),所以单次写的耗时基本就是本机一次 `fsync` 的耗时:开发者 NVMe 上约 2 ms,受争用的 CI 盘上中位数几十毫秒(hosted Windows runner 实测 74 ms),这既不是缺陷,也不构成产品承诺;审批写入的时延承诺(IPC 返回 “ok” 之前那条记录已经落盘)由 `synchronous=FULL` 保证,不由毫秒数保证。因此 `Two_hundred_writes_stay_within_the_latency_budget` 不用固定上限:它先排除首次写(创建 WAL、JIT、首次走写路径),再在同一个目录里用“单字节写 + `Flush(flushToDisk: true)`”测出本机一次持久写的耗时,预算 = 该中位数 × 10 并夹在 [50 ms, 1 s]:快盘上等于历史值 50 ms,慢盘上随盘放大,而上限 1 s 保住了“写入不会退化到秒级”这一意图。写路径真的退化(每次写多次 `fsync`、整表重写、每次写重开库/重读全表)仍会超出 10 倍而被抓住。

**失败即封闭。** 构造期把不可用的状态统一包成 `InvalidDataException`(消息含路径),绝不返回一个空 store:数据库文件是垃圾字节/被截断(`SQLite 26 NOTADB`)、路径是目录(`14/526 CANTOPEN`)、schema 版本高于本程序、`status` 文本无法识别、legacy JSON 不是合法审批列表(inner `JsonException`)。行值一律按“坏数据”归类而不是泄漏底层异常:时间戳列只接受 `"O"` 写出的规范文本——带 offset、固定 7 位小数、且与 `ToString("O")` 逐字符相等;无 offset(会被当成本机时区)、`Z`、`-00:00`、精度不足、空值、非文本值都抛 `InvalidDataException`,消息指名库路径、行(`pluginId version`)与列名,不再从 `DateTimeOffset.Parse` 泄漏 `FormatException`。`status` 与文本列不可读同此归类。构造期因此 fail-closed;`Reload()` 与读路径(失效重读)遇到同一失败时抛同一异常且不换快照,即库修好前 store 保持不可用,而不是回退到旧数据;写路径在事务内读到的行损坏时同样上抛,事务回滚,库与内存不变。legacy 导入失败时事务回滚:库仍 `user_version = 0`、无表、JSON 原样保留,下次打开重试(修复 JSON 或删掉它即可)。`ExtensionHost` 的 `Program` 已把 `InvalidDataException`/`IOException`/`UnauthorizedAccessException` 映射为“state … is unusable”并 exit 1。运行期失败(写时只读、磁盘满、busy 超时)以 `SqliteException`(8/13/5)上抛,内存不变。

**单属主是被期望、不是必需。** 逐行事务使两个 `ApprovalStore`(两个 ExtensionHost 用同一个 `--data`)在存储层是安全的:各自只读改自己那一行、提交后另一方的 `data_version` 失效并重读到新值,旧的“整文件 last-writer-wins”不再存在。约定仍然是一个数据目录只有一个 ExtensionHost 写审批记录:多实例同时跑会在语义层(谁的 approve 先到)竞争,而且每次跨进程碰撞要多等一个定时器 tick(Windows 约 15 ms)。同一实例内的修改全部在 `lock(gate)` 下,不会丢更新。

**读不到文件这条路径不存在。** 旧实现里 `Reload` 要打开文件,于是有一个“另一个句柄以 `FileShare.None` 持有状态文件时该抛还是该成功”的平台分叉测试(Linux 上 .NET 用 `flock` 强制 `FileShare.None`,该用例期望的“读仍成功”不成立)。SQLite 版读的是已经打开的长连接上的表:WAL 快照读不需要写锁,别的进程持写锁时读也不会失败;真正不可访问的库在构造期就 fail-closed。因此不再有任何 `OperatingSystem.IsWindows()` 分支,该测试的前提随旧契约一起移除。

**破坏面。** `ApprovalStore(string, TimeSpan replaceBudget)` → `ApprovalStore(string, ApprovalStoreOptions?)`;`FilePath` → `DatabasePath`,`FileName` 值改为 `approvals.db`(新增 `LegacyFileName`、`MigratedSuffix`);新增 `IDisposable`(调用方必须 `Dispose`/`using`)。`Upsert`/`SetApproved`/`Revoke`/`Disable`/`Enable`/`Get`/`List`/`Count`/`Reload` 的签名与语义不变。

**测试接缝(仅 Debug)。** 让并发与失效路径的测试不依赖机器计时:`ApprovalStoreOptions` 在 `DEBUG` 构建里多两个 `internal` 钩子——`AfterInitialSnapshotRead`(构造期整表读之后、快照与版本赋值之前调用,与版本/行读取顺序无关)与 `SnapshotRead`(每次真实整表读后调用一次:构造、`Reload()`、失效重读)。两者经 `src/Forge.Approval/AssemblyInfo.cs` 的 `InternalsVisibleTo("Forge.Approval.Tests")`(同样只在 `DEBUG`)对测试可见;`Release` 构建里既无属性也无 `InternalsVisibleTo`,不引入环境变量、静态可变全局状态或公共 API。钩子抛异常等同该路径失败:构造期释放连接并上抛,`Reload()`/读路径不换快照。用它们可以“在初始快照加载后立刻外部 Revoke”,并数出真实整表读次数。跨进程压力测试的子进程把 `BusyTimeout` 提到 60 秒:默认 5 秒是产品策略,慢盘或杀毒扫描使一次写锁的持有超过 5 秒是已文档化的合法结果(`SqliteException 5`),不应被压测读成丢更新;真正的死锁仍会在父进程 3 分钟的等待里失败。

## 未决

- 未引用 `Forge.Plugin.Sdk` 类型,`Dependency` 在本地声明;集成时若复用 SDK 类型只影响 `PluginRecord` 映射。
- 前向迁移只留了 `user_version` 阶梯(`ApprovalMigration.EnsureSchema` 按版本追加一步),目前只有版本 1;跨平台(尤其是 macOS 与 SMB 上的 WAL)行为仍由 CI 冒烟,不在本机覆盖。
