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

**存储。** SQLite 存会话、消息、摘要、全文索引(FTS5);JSONL 为事件日志,供崩溃恢复;Artifact 内容存文件。所有写入走单写线程批量提交,返回 seq 时已落盘;提交后的取消不会让调用方误判为失败。摘要三层(近期原文 → 阶段摘要 → 会话级总摘要),只追加不改写,被压缩的原文仍可检索和取回。压缩不得拆开工具调用与其结果。

**压缩触发。** 阈值是用户设置的绝对 token 数,而非窗口百分比;压缩目标为阈值的一半。无会话存储时压缩不生效。

**提供方。** OpenAI 兼容与 Anthropic。重试只发生在首个事件之前;`HttpClient` 必须是无限超时,改由每次读取的空闲超时(默认 120 秒)判定停滞。`ProviderException` 的 Retry-After 暂存于 `Exception.Data`(契约缺口,未确认是否正式化)。

## 未决

- 事件通道为无界;UI 侧按帧合并尚未实现。
- OpenAI 兼容适配器不合并相邻同角色消息,对严格的兼容服务可能不被接受。
- Artifact 转存阈值与三层摘要的保留量使用默认值,未经使用验证。
