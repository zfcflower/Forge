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
