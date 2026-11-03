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

**原子持久化。** 单锁 + 不可变记录 + 临时文件改名覆盖状态文件,读者永不见缺失/半文件;损坏 JSON → `InvalidDataException`。写入先落盘、成功后才换内存:写入失败时内存仍等于磁盘内容;`Reload` 读成功后才整体替换内存,读失败保留原记录。存储自身的读取对并发替换造成的瞬时拒绝访问做有限重试。

## 未决

- 未引用 `Forge.Plugin.Sdk` 类型,`Dependency` 在本地声明;集成时若复用 SDK 类型只影响 `PluginRecord` 映射。
