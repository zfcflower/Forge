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

- `AtomicFile`:无锁、可用于用户工作区文件。写临时文件、落盘、改名覆盖;读取带 `FileShare.ReadWrite | Delete`。
- `AtomicJsonStore<T>` + `FileLock`:仅用于本程序自有、多进程共享的小状态文件(设置等)。每次更新在跨进程锁内重读最新值再应用修改,避免后写覆盖先写。用户工作区文件不可加锁(其他程序不遵守)。
- 审批记录不使用本组件,改用 SQLite。

## 关键决策

- **覆盖用改名覆盖,不用 `File.Replace`。** Windows 上 `File.Move(overwrite)` 在目标有任何打开句柄时被拒绝(访问被拒绝),`File.Replace` 写者不受阻但让读者约 12% 打开失败(文件名瞬时不存在)。前者读者零失败,故选前者并重试。
- **只重试确认的占用竞争:** Windows、`UnauthorizedAccessException` 或共享/锁冲突类 `IOException`(HResult 低 16 位 32、33)、目标仍存在。其余错误立即抛出。先 `Thread.Yield()` 约 10 毫秒再 `Sleep(1)`,总预算默认 5 秒,可取消,耗尽重抛原始异常并清理临时文件。
- **读者永不把瞬时缺失当作“无数据”。** 读取到不存在时做有界复查;只有确认不存在才返回 null。损坏的 JSON 抛 `InvalidDataException`,绝不当作空状态(否则下一次更新会覆盖全部数据)。
- **已知局限:** 读者长时间持有句柄(如连续读取)时重试无法保证写入成功,预算耗尽会明确失败而不是静默。

## 实现约定

上面“关键决策”已定;以下是实现时不得不自行确定、契约里没写死的细节,以本文件为准。

- **临时文件**:与目标同目录,名字 `<文件名>.<guid:N>.tmp`,`FileMode.CreateNew` + `FileShare.None`;写完 `Flush(flushToDisk: true)` 再改名。目标目录不存在则创建。
- **首次发布走不覆盖的改名**:目标此刻不存在时用 `File.Move(tmp, path, overwrite: false)`;若因目标在此期间被别人创建而失败(`IOException`,HResult 低 16 位 183 / 80 / 17 且目标现在存在),回退到覆盖重试循环。不依赖 `File.Exists` 判断的原子性,回退由真实异常触发。
- **重试等待**:前 10 毫秒 `Thread.Yield()`;之后 `Sleep(1)`(令牌可取消时改为 `WaitHandle.WaitOne(1)`,取消在下一轮循环开头立即抛出)。预算从**改名开始时**计时,不包含写临时文件与 `fsync`。
- **异步写入**:写临时文件用异步 IO,`fsync` 与改名是同步的(BCL 无异步 fsync);重试等待为 `Task.Delay(1, token)`。取消/失败同样删除临时文件。
- **读取的缺失复查**:`FileNotFoundException` / `DirectoryNotFoundException` 最多复查 16 次(10 毫秒自旋窗口内,实测一次缺失读取约 0.15 毫秒),仍缺失才返回 `null`;耗尽预算时若最后一次仍是“缺失”也返回 `null`(异常本身就证明名字不在)。替换竞争类失败(Windows 的访问被拒绝/共享冲突且文件仍在)按同一预算重试,耗尽重抛原异常。存在的空文件返回空数组。
- **锁竞争的平台判定**:Windows 认 `IOException`(HResult 低 16 位 32、33)与 `UnauthorizedAccessException`;Unix 认 `EWOULDBLOCK`(Linux 11、macOS/BSD 35,即 .NET 把 `FileShare.None` 映射到的 advisory lock 冲突)。其他 `IOException`/权限错误不重试。退避从 5 毫秒起翻倍到 100 毫秒封顶,每次在 `[d/2, d]` 内抖动,单次等待不超过剩余预算,因此超时最多超出约一次退避。
- **锁文件**:`OpenOrCreate` + `ReadWrite` + `FileShare.None`,目录不存在则创建;由 OS 在进程死亡时释放;本类型永不删除锁文件,也不做“过期锁”清理。
- **`AtomicJsonStore` 的路径与内容**:`Path` 是 `Path.GetFullPath` 之后的值,锁文件为 `Path + ".lock"`。文件缺失或长度为 0 ⇒ `new T()`;非空内容反序列化抛 `JsonException` 或得到 `null` ⇒ `InvalidDataException`(消息含路径)。`Update` 在锁内 `Read` → `mutate` → `SerializeToUtf8Bytes` → `AtomicFile.WriteAllBytes`;`mutate` 返回 `null` 视为错误(`InvalidOperationException`),不写盘。`Stamp` 用 `FileInfo`,文件缺失(或路径是目录)返回 `null`。
- **构建与分析器**:`IsAotCompatible` 打开,AOT/裁剪分析器零告警;序列化只走调用方提供的 `JsonTypeInfo<T>`。

## 验证

- Windows 10/11(`D:/FORGE/work37/harness`,throwaway 控制台):4 读者 + 1 写者、100 KB 负载、200 次改写 ×5 轮(1000 次改写)零读者错误/零半文件/零写失败,写延迟 p50 4.0–4.9 毫秒、p95 7.1–8.0 毫秒;异步路径 100 次改写同样全零。
- 句柄长期持有:预算 300 毫秒 ⇒ 315 毫秒后抛出原始 `UnauthorizedAccessException`(HResult 0x0005),目标字节不变,无 `.tmp` 残留;取消 ⇒ 约 60 毫秒内 `OperationCanceledException`,目标不变;目标为目录 ⇒ 2 毫秒内失败不重试。
- 多进程状态文件:同进程 8 个 store 实例 ×100 次 `Update` + 1 个 `dotnet` 子进程 ×100 次 ⇒ 计数 900/900;持有锁的进程被 kill 后锁在约 4 毫秒内可用。
- Linux(Ubuntu 24.04 / WSL2,ext4)同一套 harness 全绿:`rename(2)` 语义下持有句柄的读者不阻塞写入,`EWOULDBLOCK` = 11 的锁竞争判定与超时有效。macOS 未实测(按 BSD 的 `EWOULDBLOCK` = 35 列入竞争判定)。
- Windows `PublishAot=true` + `TrimmerSingleWarn=false` + `TreatWarningsAsErrors=true` 发布零告警,原生二进制可正常读写与加锁。
