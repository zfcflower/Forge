# 审批队列 + Agent 扩展合入后的遗留问题(2026-11-06)

两轮安全评审(Review 角色,只读,彼此独立的会话)的结论都是“pass with fixes”,其中的实质性问题都已修复(见 `fix(core)`/`fix(extension-host)`/`fix(app)` 各提交)。以下是评审中确认、但本次有意不修或只缓解的问题。

## 1. C# 插件跨插件同名工具:回滚前有短暂窗口

- **位置**:`src/Forge.ExtensionHost/ExtensionHost.Authoring.cs`,`CommitCoreAsync` 中批准之后的重名检查。
- **现象**:C# 插件的工具名要等 Worker 装载后才知道,所以重名检查放在 `ApproveCoreAsync` 之后,发现重名就整体回滚并返回 `invalid_extension`。在这段很短的时间里,新旧两个同名工具都已对外注册,这时发出的模型请求可能调用到其中任何一个。
- **为什么接受**:两份代码都已经过用户批准,不会运行未批准的代码;回滚路径正确。工作流包的工具名在 manifest 里,检查在动磁盘之前完成,没有这个窗口。
- **建议**:让 Worker 先把工具装进暂存集合,重名检查通过后再发布;或者在 `ApplyCoreAsync` 发布工具集之前做检查。

## 2. 子 Agent 别名登记有极小的时序窗口

- **位置**:`src/Forge.App/Agents/SessionRuntime.cs`,`TrackSubAgent`。
- **现象**:子 Agent 的会话 id 在 App 消费 `AgentSubAgentStarted` 事件时才登记为根会话的别名,而不是在子 Agent 启动时同步登记。理论上,子 Agent 的首个插件调用可能早于这次登记。
- **结果**:失败即封闭——该调用被拒绝(“Unknown tool”/拒绝),不会越权。实际上首个工具调用要等一次模型往返,远晚于事件被消费。
- **建议**:如果要彻底消除,需要 Core 提供同步的子会话创建钩子。这属于 Core 的改动,本次不做。

## 3. 子 Agent 的插件调用按根的工具集解析

经路由器进来的子 Agent 调用,用的是根会话的工具集和策略,不是该子 Agent 自己的工具白名单。这类调用的变更操作仍然要过审批卡,`extension_*` 仍然不可达。这一点已写进 forge-app SPEC。

## 4. 撤卡结果以 `Invalid` 报给 Core

Core 的 `ExtensionSubmitOutcome` 只有 Approved/Rejected/Invalid 三个值,所以“撤卡(不是拒绝,草稿保留)”以 `Invalid` 加明确文本返回给 Agent。要单独区分,需要给 Core 的枚举追加 `Withdrawn`。

## 5. 集成测试在高负载下偶发失败

全量 `dotnet test Forge.slnx` 并行跑时,以及同机还有其它副本在跑测试时,每次会有不同的 1–4 个计时敏感集成用例失败(例如 `CrashRecoveryTests`、`SingleUseExecutorIsolationTests.GhostTask_*`、`EntryLifecycleTests.*`,以及钩子文件被占用导致的 `IOException`)。这些用例单独运行(连续 3 次)全部通过,本任务也没有改动它们。App 测试中 `TodoPanelTests.Add_check_remove_move_reach_the_store` 在 10 次全量运行中失败过 1 次,属于既有 headless 会话的抖动。
