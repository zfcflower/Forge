---
id: forge-approval
type: module-design
status: draft
title: Forge.Approval
parent: forge-architecture
---

# Forge.Approval

用户插件的源码哈希审批引擎。纯逻辑 + JSON 文件持久化,只依赖 BCL,AOT 兼容(源生成 JSON)。

## 边界

- 持久化记录是权威;`Evaluate` 纯函数无 IO,重启恢复 = 新建 store 或 `Reload()`。
- 不负责 UI 审批交互、权限执行(属主程序)或插件发现(属 ExtensionHost)。

## 关键决策

**组合指纹。** `ApprovedHash` = `SHA256(SourceHash + DependencyHash + SdkVersion)`,三者任一漂移即批准失效;行尾归一化(CRLF/CR/LF 等价)使纯行尾修改不失效。

**状态矩阵。** `Draft` → `RequiresApproval`(可批准提升);`Disabled`/`Uninstalled` → `Blocked`(再批准不可恢复);无记录 → `RequiresApproval`。只有三个候选哈希与记录一致且指纹等于 `ApprovedHash` 才 `Allowed`。审批操作不得绕过封锁:`SetApproved` 只刷新内容哈希并保留 `Disabled`/`Uninstalled` 状态;`Enable` 只解 `Disabled`(有 `ApprovedHash` 回 `Approved`,否则回 `Draft`);`Revoke` 对 `Disabled`/`Uninstalled` 是空操作。

**原子持久化。** 单锁 + 不可变记录 + 临时文件改名覆盖状态文件,读者永不见缺失/半文件;损坏 JSON → `InvalidDataException`。写入先落盘、成功后才换内存:写入失败时内存仍等于磁盘内容;`Reload` 读成功后才整体替换内存,读失败保留原记录。

**替换重试。** Windows 上 `File.Move(overwrite: true)` 只要目标文件被任意句柄打开(即使该句柄带 `FileShare.Delete`)就立即失败(win32 5 → `UnauthorizedAccessException`)。`Replace` 因此有界重试:前 10 ms `Thread.Yield()`,随后 `Thread.Sleep(1)`,总预算 5 s(构造重载可注入,默认 5 s)。只重试已确认的该类竞态:`OperatingSystem.IsWindows()` 且(`UnauthorizedAccessException`,或 `HResult & 0xFFFF` ∈ {32, 33} 的 `IOException`)、状态文件仍存在、不是目录、不带 `ReadOnly`——目标是 `ReadOnly` 文件或目录这类确定性失败等待不会改变结果,立即原样上抛、不消耗预算;其他错误(磁盘满、路径过长、文件不存在时的真实 ACL 拒绝)同样立即上抛,非 Windows 不重试。等待期间的 `CancellationToken` 取消抛 `OperationCanceledException`;预算耗尽即删除临时文件、保持内存不变、原样抛出最后一次异常(不包装、不吞)。`OpenForReading` 对同一竞态类加瞬时缺失做有限重试(8 次);文件始终缺失时按空存储处理,与既有 `File.Exists` 契约一致。

**单写者。** 重试只消除"状态文件暂时不可替换"的抖动,不提供跨实例协调:两个 `ApprovalStore` 指向同一文件时各自持有内存快照并整文件重写,结果是 last-writer-wins(测试固定该实际行为)。防止丢失更新的规则是单属主——只由 Extension Host 进程写状态文件;同一实例内的修改全部在 `lock(gate)` 下,不会丢更新。

## 未决

- 未引用 `Forge.Plugin.Sdk` 类型,`Dependency` 在本地声明;集成时若复用 SDK 类型只影响 `PluginRecord` 映射。
