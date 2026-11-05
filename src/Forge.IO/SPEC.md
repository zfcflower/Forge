---
id: forge-io
type: module-design
status: draft
title: Forge.IO
parent: forge-architecture
---

# Forge.IO

底层文件写入组件,只依赖 BCL,AOT 兼容。所有“覆盖已有文件”的写入统一走这里,不再各处自写“临时文件再改名”。

## 边界

- `AtomicFile`:无锁、可用于用户工作区文件。写临时文件、落盘、改名覆盖;读取带 `FileShare.ReadWrite | Delete`。对外两组入口:整份写入 `WriteAllBytes`/`WriteAllBytesAsync`,以及只做“发布”的 `ReplaceFile`/`ReplaceFileAsync`(给已经流式写好并落盘的临时文件用)。
- `AtomicJsonStore<T>` + `FileLock`:仅用于本程序自有、多进程共享的小状态文件(设置等)。每次更新在跨进程锁内重读最新值再应用修改,避免后写覆盖先写。用户工作区文件不可加锁(其他程序不遵守)。
- 审批记录不使用本组件,改用 SQLite。

## 关键决策

- **覆盖用改名覆盖,不用 `File.Replace`。** Windows 上 `File.Move(overwrite)` 在目标有任何打开句柄时被拒绝(访问被拒绝),`File.Replace` 写者不受阻但让读者约 12% 打开失败(文件名瞬时不存在)。前者读者零失败,故选前者并重试。
- **只重试确认的占用竞争:** Windows、`UnauthorizedAccessException` 或共享/锁冲突类 `IOException`(HResult 低 16 位 32、33),且目标仍存在、不是目录、不带 `ReadOnly`。其余错误立即抛出。
- **确定性失败不重试:** 目标明明是 `ReadOnly` 文件、目标是目录、锁路径是目录或 `ReadOnly` 文件时,等待不会改变结果,因此立即原样抛出原异常,不消耗预算(改造前这些情况会占满整个 5 秒预算/锁超时)。
- **只读目标在改名前拒绝,不靠改名失败:** `ReadOnly` 不再只是失败后的重试分类条件。Unix 上目标目录可写时 `rename(2)` 能覆盖只读文件、并把权限换成临时文件的,所以发布前统一检查目标:Windows 看 `FileAttributes.ReadOnly`;Unix 另加两条(模式没有任何写位、或以写权限打开目标被拒),三条任一命中即抛 `UnauthorizedAccessException`(消息含路径),删临时文件,目标字节与属性原样保留,不重试。两个入口与异步变体共用同一段发布逻辑,因此 `WriteAllBytes`、`WriteAllBytesAsync`、`ReplaceFile`、`ReplaceFileAsync` 行为一致。
- **成功替换保留目标权限:** Unix 上发布前把目标的 `File.GetUnixFileMode` 设到临时文件,换名后文件权限(含可执行位)不变,而不是变成临时文件的 0644;非 Unix 跳过,设置失败忽略(权限不是发布的契约条件)。
- **重试节奏:** 先 `Thread.Yield()` 约 10 毫秒再 `Sleep(1)`,总预算默认 5 秒,可取消,耗尽重抛原始异常并清理临时文件。
- **取消的响应上限:** 同步与异步的重试循环都在**每次尝试之前**检查令牌,等待本身可取消(同步 `WaitHandle.WaitOne(1)`,异步 `Task.Delay(1, token)`)⇒ 从取消到抛出的延迟上限是“**一次改名尝试 + 1 毫秒**”,异步再加一次续体调度(12 个竞争任务同进程时实测 ≤ 16 毫秒)。单次尝试是一次 `File.Move` 加一次属性查询,它和临时文件的创建/写入/`fsync` 一样是文件系统调用:杀毒软件与慢盘可让单次调用停数百毫秒到数秒(托管 Windows runner 上实测过单次小文件写入 0.67 秒、整条写入 6.9 秒),产品不为这类单次调用担保固定毫秒数。写临时文件的创建/写入/`fsync` 本身不可取消,取消落在这一段时调用在其后第一次令牌检查处抛出;写完数据后、`fsync` 前再检查一次令牌,取消落在写入与落盘之间时不再付出一次落盘的代价。
- **读者永不把瞬时缺失当作“无数据”。** 读取到不存在时做有界复查;只有确认不存在才返回 null。损坏的 JSON 抛 `InvalidDataException`,绝不当作空状态(否则下一次更新会覆盖全部数据)。
- **已知局限:** 读者长时间持有句柄(如连续读取)时重试无法保证写入成功,预算耗尽会明确失败而不是静默。

## 实现约定

上面“关键决策”已定;以下是实现时不得不自行确定、契约里没写死的细节,以本文件为准。

- **临时文件**:与目标同目录,名字 `<文件名>.<guid:N>.tmp`,`FileMode.CreateNew` + `FileShare.None`;写完 `Flush(flushToDisk: true)` 再改名。目标目录不存在则创建。
- **发布已写好的临时文件**:`ReplaceFile(tempPath, destinationPath, options, token)` / `ReplaceFileAsync` 由调用方提供已写完并已 `Flush(flushToDisk: true)` 的临时文件,其余契约与 `WriteAllBytes` 完全一致——预算解析与校验(非正预算在动任何文件前抛 `ArgumentOutOfRangeException`)、同一重试策略、失败或取消时删除 `tempPath`、目标保持原内容、目标目录不存在则创建。`WriteAllBytes`/`WriteAllBytesAsync` 写临时文件成功后走的就是这条路径;流式写入者(编辑器保存)因此不必自己 `File.Move`。
- **只读目标的探测细节**:发布函数开头、估算预算计时之前检查目标是否存在(目标不存在或本身是目录时跳过,目录失败仍由改名自己抛)。判定顺序:(1) `File.GetAttributes` 带 `ReadOnly`;(2) 仅 Unix: `File.GetUnixFileMode` 的 `UserWrite|GroupWrite|OtherWrite` 全为 0;(3) 仅 Unix: `new FileStream(dest, Open, Write, FileShare.ReadWrite|Delete)` 立刻 dispose,抛 `UnauthorizedAccessException` 则算只读。第(2)(3)条合起来覆盖 `File.SetAttributes(ReadOnly)` 的 chmod 语义、以 root 运行以及 ACL 拒绝,并保持与 `File.OpenWrite` 会拒绝的集合一致;(3) 抛其他 `IOException`(如另一进程正持有)不当只读,交给改名与既有重试策略。命中后抛新建的 `UnauthorizedAccessException`(消息含路径),临时文件由既有 `catch` 删除:同步与异步、`WriteAllBytes` 与 `ReplaceFile` 一致。
- **权限保留的实现**:同一次发布前的检查里,目标存在且非只读时先 `File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination))` 再改名;读取或设置失败(`IOException`/`UnauthorizedAccessException`/`NotSupportedException`)忽略,Windows 直接跳过。
- **首次发布走不覆盖的改名**:目标此刻不存在时用 `File.Move(tmp, path, overwrite: false)`;若因目标在此期间被别人创建而失败(`IOException`,HResult 低 16 位 183 / 80 / 17 且目标现在存在),回退到覆盖重试循环。不依赖 `File.Exists` 判断的原子性,回退由真实异常触发。
- **重试等待**:前 10 毫秒 `Thread.Yield()`;之后 `Sleep(1)`(令牌可取消时改为 `WaitHandle.WaitOne(1)`,取消在下一轮循环开头立即抛出)。预算从**改名开始时**计时,不包含写临时文件与 `fsync`。写临时文件的 `Write`/`WriteAsync` 之后、`Flush(flushToDisk: true)` 之前检查一次令牌,使取消不必再付一次落盘。
- **异步写入**:写临时文件用异步 IO,`fsync` 与改名是同步的(BCL 无异步 fsync);重试等待为 `Task.Delay(1, token)`。取消/失败同样删除临时文件。
- **读取的缺失复查**:`FileNotFoundException` / `DirectoryNotFoundException` 最多复查 16 次(10 毫秒自旋窗口内,实测一次缺失读取约 0.15 毫秒),仍缺失才返回 `null`;耗尽预算时若最后一次仍是“缺失”也返回 `null`(异常本身就证明名字不在)。替换竞争类失败(Windows 的访问被拒绝/共享冲突,且文件仍在、不是目录、不带 `ReadOnly`)按同一预算重试,耗尽重抛原异常。存在的空文件返回空数组。
- **锁竞争的平台判定**:Windows 认 `IOException`(HResult 低 16 位 32、33)与 `UnauthorizedAccessException`;Unix 认 `EWOULDBLOCK`(Linux 11、macOS/BSD 35,即 .NET 把 `FileShare.None` 映射到的 advisory lock 冲突)。其他 `IOException`/权限错误不重试;锁路径是目录或带 `ReadOnly` 的文件时同样不重试,开锁失败的原始 `UnauthorizedAccessException` 立即上抛(而不是等超时后包成 `TimeoutException`)。退避从 5 毫秒起翻倍到 100 毫秒封顶,每次在 `[d/2, d]` 内抖动,单次等待不超过剩余预算,因此超时最多超出约一次退避。
- **锁文件**:`OpenOrCreate` + `ReadWrite` + `FileShare.None`,目录不存在则创建;由 OS 在进程死亡时释放;本类型永不删除锁文件,也不做“过期锁”清理。
- **`AtomicJsonStore` 的路径与内容**:`Path` 是 `Path.GetFullPath` 之后的值,锁文件为 `Path + ".lock"`。文件缺失或长度为 0 ⇒ `new T()`;非空内容反序列化抛 `JsonException` 或得到 `null` ⇒ `InvalidDataException`(消息含路径)。`Update` 在锁内 `Read` → `mutate` → `SerializeToUtf8Bytes` → `AtomicFile.WriteAllBytes`;`mutate` 返回 `null` 视为错误(`InvalidOperationException`),不写盘。`Stamp` 用 `FileInfo`,文件缺失(或路径是目录)返回 `null`。
- **构建与分析器**:`IsAotCompatible` 打开,AOT/裁剪分析器零告警;序列化只走调用方提供的 `JsonTypeInfo<T>`。

## 验证

- Windows 10/11(`D:/FORGE/work37/harness`,throwaway 控制台):4 读者 + 1 写者、100 KB 负载、200 次改写 ×5 轮(1000 次改写)零读者错误/零半文件/零写失败,写延迟 p50 4.0–4.9 毫秒、p95 7.1–8.0 毫秒;异步路径 100 次改写同样全零。
- 并发读者压测(`AtomicFileWriteTests`,在 2 核 CPU 限制下复现过写失败):读者在两次读取之间让出 1 毫秒。零间隙地连续重开同一路径会让 Windows 的“改名覆盖”在整个预算内都拿不到目标——那是“已知局限”里的场景,不是本测试要固定的契约;让出后仍能验证真正的不变量:改写进行中读者永远读不到缺失、半文件或混合内容。
- 句柄长期持有:预算 300 毫秒 ⇒ 315 毫秒后抛出原始 `UnauthorizedAccessException`(HResult 0x0005),目标字节不变,无 `.tmp` 残留;目标为目录 ⇒ 2 毫秒内失败不重试。
- 取消(100 毫秒后触发,读者全程持有目标):只计重试循环(`ReplaceFile`/`ReplaceFileAsync`,临时文件事先写好并落盘)时,从触发取消到 `OperationCanceledException` 的净延迟 ≤ 41 毫秒(20 次;Windows 11,2 核 affinity + 4 个并发 `Flush(true)` 写线程 + 2 个自旋线程)。同一条件下整条 `WriteAllBytes` 的取消延迟还包含不可取消的临时文件创建/写入/`fsync`,因此测试只对重试循环设毫秒级上限(5 秒,单次调用被杀毒软件卡住时不误报),对整条路径只断言“在预算之内抛出、且不是预算耗尽”。
- 确定性失败(Windows 11,`dotnet test` 实测):目标为 `ReadOnly` 文件 ⇒ 6 毫秒(异步 98 毫秒)抛原 `UnauthorizedAccessException`,改造前 5007 毫秒;锁路径为目录 ⇒ 约 1 毫秒抛原 `UnauthorizedAccessException`,改造前 2012 毫秒后抛 `TimeoutException`;锁路径为 `ReadOnly` 文件 ⇒ 约 1 毫秒,改造前 2005 毫秒;目标为目录 ⇒ 约 6 毫秒抛 `IOException`(0xB7),原本就立即。
- 只读目标不再被覆盖(两个平台同一断言):`File.SetAttributes(ReadOnly)`(Unix 另设模式 0444)后 `WriteAllBytes`/`WriteAllBytesAsync`/`ReplaceFile`/`ReplaceFileAsync` 都抛 `UnauthorizedAccessException`,耗时 < 预算的一半,目标字节、属性、Unix 模式不变,无 `*.tmp` 残留;`SpecGraph.UpdateFrontmatterAsync` 对只读 spec 文件同样失败且文件字节不变。成功替换后目标 Unix 模式(含 `0640` 这类非默认模式)保持不变。
- 多进程状态文件:同进程 8 个 store 实例 ×100 次 `Update` + 1 个 `dotnet` 子进程 ×100 次 ⇒ 计数 900/900;持有锁的进程被 kill 后锁在约 4 毫秒内可用。
- 更新序列的等待上限按本机实测成本标定,不写死 30 秒:先在同一目录用同一 store 串行跑 6 次 `Update` 取中位数作为单次持久更新成本,上限 = `clamp(次数 × 成本 × 8 + 子进程启动余量, 30 秒, 2 分钟)`。依据:更新被跨进程锁串行,每次更新 = 一次锁开/关 + 一次读 + 一次临时文件创建/写入/`fsync` + 一次改名,总时长就是次数乘单次成本;托管 Windows runner 上 800 次实测 32.8 秒(同一次运行里 900 次那条只用 17.0 秒),固定 30 秒必然偶发超时。
- 退避不构成锁护送:本机 2 核 affinity 下 8 线程 ×100 次 `Update` 为 3.70–5.27 秒;把退避换成“每 1 毫秒轮询同一把锁”的等效实现为 3.57–4.48 秒,不睡眠的自旋版本反而更慢(5.63–5.88 秒)。时间由串行的持久写成本决定,不是握手延迟。
- Linux(Ubuntu 24.04 / WSL2,ext4)同一套 harness 全绿:`rename(2)` 语义下持有句柄的读者不阻塞写入,`EWOULDBLOCK` = 11 的锁竞争判定与超时有效。macOS 未实测(按 BSD 的 `EWOULDBLOCK` = 35 列入竞争判定)。
- Windows `PublishAot=true` + `TrimmerSingleWarn=false` + `TreatWarningsAsErrors=true` 发布零告警,原生二进制可正常读写与加锁。
