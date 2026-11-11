---
id: forge-workflows
type: module-design
status: draft
title: Forge.Workflows
parent: forge-architecture
depends-on: [forge-architecture]
tags: [workflow, declarative, l0]
---

# Forge.Workflows

声明式工作流引擎（L0 扩展形态）。一份 JSON 文档描述若干步骤，每步调用一个已注册工具；先后关系只由 `after` 表达，
无关系的步骤并行；后一步的 `args` 可用 JSON 路径引用前面步骤的输出。只依赖 BCL 与 `Forge.Plugin.Sdk`；工具调用一律经
`IHostFacade.CallToolAsync`，引擎不持工具注册表、不做审批、不落盘。

## 边界

- 第一阶段范围：顺序 + 并行 + JSON 路径映射。循环、条件表达式、脚本表达式、通用 DSL 不在范围内。
- 对外入口：`WorkflowEngine.RunAsync`、`WorkflowJson.Parse`、`WorkflowValidator.Validate`、`RunOptions`、
  `WorkflowRunResult`/`StepResult`、`WorkflowFormatException`、`WorkflowDiagnostic`。格式与路径子集的完整说明见
  `src/Forge.Workflows/README.md`。
- 格式与路径子集属于契约：`after` 省略 = 依赖紧邻上一歩，`after: []` = 无依赖；绑定只能指向自己的传递依赖；
  指令只有 `$ref` 与 `$literal`，路径字段只有 `content`/`isError`/`status`/`error`/`json`。
- 绑定有两个根：`steps`（前面步骤的输出）与 `$input`（本次运行的 `RunOptions.Input`）。`$input` 不是步骤：
  校验器不要求它进 `after`，不产生 `UnknownBindingStep`/`BindingOnNonDependency`；`Input` 为 null 或路径不存在时
  该步按绑定解析失败处理（`Failed`，不调工具），带 `?` 时按可选语义省略。工作流插件（L0）就是靠这个根把工具
  调用参数传进各步骤。

## 关键决策

**调度按就绪集而非分层。** 就绪队列取最小下标启动，故同一文档调度顺序稳定；步骤完成即递减后继未完成依赖数，归零即启动。
并发上限 `RunOptions.MaxConcurrency`（默认 8）。完成事件走无界 `Channel<int>`，取消用写哨兵唤醒而不是异常驱动。

**失败只传播到依赖方。** 工具报错、抛异常、绑定解析失败都记为该步 `Failed` 且不抛出；失败的步骤永不递减后继计数，
因此后继及其传递后继停在 `Skipped`，无关分支继续跑完，运行状态 `Failed`。`onError` 目前只有 `fail`。

**取消/超时停调度、不强行终止。** 调用方令牌与 `RunOptions.Timeout` 都只停止新步骤；在飞步骤收到令牌，等
`CancellationDrainTimeout`（默认 5 秒）后仍未结束的标 `Cancelled` 并立即返回。不合作的工具无法从引擎内终止，
被放弃步骤持有的 `RunState`（含已解析的 `JsonDocument`）延迟到其任务结束才释放，避免读到已释放文档。

**时间统一走 `TimeProvider`。** 时间戳、步骤耗时与超时计时（`CancellationTokenSource(delay, TimeProvider)`）都取自
`RunOptions.TimeProvider`，测试注入假时钟即可控制全部时间而不改代码。

**AOT 与零反射。** JSON 走源生成 `WorkflowJsonContext`（`IsAotCompatible=true`，分析器 0 警告；以 Native AOT 发布二进制
实测通过）。`args` 无指令时按原始 `JsonElement` 原样传给工具，不重新序列化；有指令时用 `Utf8JsonWriter` 写一次再解析。

**格式错误的判定不依赖 STJ 对缺失属性的处理。** `steps` 是否出现由对同一份 UTF-8 字节的一次 `Utf8JsonReader` 扫描决定
（注释与尾随逗号选项与 `WorkflowJsonContext` 一致），不靠 `Steps is null`：.NET 11 起源生成反序列化保留属性初始化器，
缺失属性不再写成 `default(T)`，`{}` 会得到空 `steps`。契约不变——`steps` 缺失仍是格式错误（`$.steps`）——但实现不能再
依赖该默认值行为。

**校验不抛异常。** `WorkflowValidator.Validate` 返回确定顺序的诊断列表；环检测用 Kahn 剥点定位环上节点，再按最小下标
各报一次并给出路径。`BindingOnNonDependency` 刻意收紧为“只能绑定传递依赖”，避免与并行步骤抢数据。

**`$input` 绑定根。** 写法 `{"$ref": "$input"}`（整个入参）或 `{"$ref": "$input.a.b"}` / `$input[0]`，段语法与步骤
根相同（含 `?` 可选后缀）。`$` 前缀是必需的：裸写 `input.x` 会被当作步骤名。解析在 `RunState` 内直接面对
`RunOptions.Input`，不经过步骤结果表；入参为 null 时错误文本明确“run 没有 $input”，不会注入 null 或空对象。

## 未决

- ~~源生成反序列化对缺失属性写 `default(T)`~~：已在 .NET 11 上回归并解决。该版本起缺失属性保留属性初始化器，
  `steps` 缺失改由文档扫描判定（见“关键决策”）；其余缺失属性现在取初始化器默认值，不再需要解析后归一化。
- `onError` 的 `retry`/`continue`、步骤级超时、`when`/`foreach`、`{"$expr": ...}` 表达式均未实现；格式按可向前兼容
  设计（未知属性忽略、`version` 守卫、`$` 前缀指令命名空间），加入时需扩展 README 与校验错误码。
- 每步只保留 `Content` 文本，未记录解析后的 `args`；排查绑定问题目前靠错误文本，必要时再加可选的 `CaptureResolvedArgs`。
- 并发上限是每次运行的上限，不是全局上限；多工作流并行时总并发为运行数乘该值。
