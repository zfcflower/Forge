# Forge.Workflows

L0 声明式工作流引擎（`Forge.Workflows`）。一个工作流是一份 JSON 文档：若干步骤，每个步骤调用一个已注册工具；
步骤之间的先后关系只由 `after` 表达，没有先后关系的步骤并行执行；后一步的 `args` 可以用 JSON 路径从
前面步骤的输出里取值。工具调用一律经 `Forge.Plugin.Sdk.IHostFacade.CallToolAsync`，引擎自身不持工具注册表、
不做审批、不落盘。

本阶段（第一阶段）范围：**顺序 + 并行 + JSON 路径映射**。循环、条件表达式、脚本表达式与通用 DSL 明确不在范围内；
格式按可向前兼容的方式设计（未知属性忽略、`version` 守卫、`$` 前缀指令命名空间），后续加入 `when`/`foreach`/
`{"$expr": ...}` 时不破坏既有文档（见“未决”）。

## 文档形状

```jsonc
{
  "name": "可选名字",          // 不参与调度
  "version": 1,                // 省略视为 1；其他值 → UnsupportedVersion 诊断，拒绝运行
  "steps": [                   // 步骤数组；允许为空（直接 Completed）
    {
      "id": "fetch",           // 稳定 id，全文档唯一，被 after 与 $ref 引用
      "tool": "read_file",     // IHostFacade 的工具名
      "args": { },             // 字面量 JSON + 内嵌 {"$ref": ...} 绑定；省略或 null 视为 {}
      "after": ["prepare"],    // 省略 = 依赖紧邻的上一歩；[] = 无依赖；显式列出则只依赖列出的步骤
      "onError": "fail"        // 目前只有 fail（失败即传播）；未知值 → WorkflowFormatException
    }
  ]
}
```

联调要点：`after` 省略与 `after: []` 语义不同，前者是隐式串行链，后者是并行起点。隐式链与显式前向引用会成环
（例如第 2 步 `after: ["first"]`、而 `first` 在第 3 步且未写 `after`），这类情况由校验器报 `DependencyCycle`。

## JSON 路径子集

绑定写作单键对象 `{"$ref": "<path>"}`；`args` 里除 `$ref`/`$literal` 外的单键 `$` 对象都是错误（校验诊断）。
多键对象一律按字面量处理，嵌套层里继续识别指令。

路径有两个根：

- `steps`：前面步骤的输出，写作 `$.steps.<id>.<field>...`，省略 `$` 时写作 `steps.<id>.<field>...`。
- `$input`：本次运行的入参（`RunOptions.Input`），写作 `$input`、`$input.a.b` 或 `$input.items[0].name`。

```
path      := (stepsPath | inputPath) '?'?
stepsPath := ('$.')? 'steps' '.' stepId '.' field segment*
inputPath := '$input' segment*
segment   := '.' name | '[' 整数 ']' | '[' 引号字符串 ']'
```

- 步骤根的第二段是产出步骤的 `id`（普通名或 `['...']` 引号形式），第三段必须是字段之一：
  `content`（原始文本）、`isError`（布尔，工具是否报错）、`status`
  （`succeeded`/`failed`/`cancelled`/`skipped`/`notRun`）、`error`（失败说明；成功步骤上没有该字段）、
  `json`（`content` 解析后的 JSON）。
- 其后是对象属性与数组下标；名字含 `.`、`[`、`]`、空白或引号时用 `['...']`，引号内 `\\`、`\'`、`\"` 转义。
- `$input` 根必须带 `$`：裸写 `input.x` 会被当作步骤名，而 `$input` 不可能是步骤 id，所以步骤绑定与入参绑定不会混淆。
- `$input` 单独使用（`{"$ref": "$input"}`）得到整个入参；带路径时按同一套段语法逐层取值。
- `$input` 不是步骤：校验器不要求它出现在 `after` 里，也不会报 `UnknownBindingStep`/`BindingOnNonDependency`。
- 末尾 `?` 表示可选：解析失败时**在对象里省略该属性、在数组里写 `null`**；不带 `?` 的绑定解析失败会让该步骤
  直接失败（不调用工具），错误文本点名步骤与路径。不会静默注入 null。`RunOptions.Input` 为 null 时所有不带 `?`
  的 `$input` 绑定都按解析失败处理。

示例（`$ref` 值）：

| 路径                                   | 含义                                     |
| -------------------------------------- | ---------------------------------------- |
| `$.steps.fetch.content`                | 步骤 `fetch` 的原始文本                  |
| `$.steps.fetch.json.items[2].name`     | 其 JSON 输出里第 3 个元素的 `name`        |
| `$.steps['my step'].json.obj.nested[0]` | 引号形式的步骤 id，再逐层取值             |
| `$.steps.fetch.json.optional.x?`       | 缺失时省略属性（数组元素为 `null`）       |
| `$.steps.fetch.status`                 | 该步骤状态字符串                          |
| `$.steps.fetch.json`                   | 整段 JSON，可整体作为 `args`              |
| `$input`                               | 整个运行入参（`RunOptions.Input`）        |
| `$input.branch`                        | 入参对象的 `branch` 属性                  |
| `$input.items[0].name`                 | 入参数组的第 1 个元素的 `name`            |

`$input` 的入参由宿主在运行前决定（`RunOptions.Input`），工作流插件用它把工具调用参数传进各步骤：

```jsonc
{
  "name": "greet",
  "steps": [
    { "id": "fetch", "tool": "read_file", "args": { "path": { "$ref": "$input.path" } } },
    { "id": "echo", "tool": "echo", "args": { "text": { "$ref": "$.steps.fetch.content" }, "label": { "$ref": "$input.label?" } } }
  ]
}
```

解析失败的错误文本形如
`step 'use': binding '$.steps.fetch.json.items[5]' could not be resolved: index 5 is out of range at '$.steps.fetch.json.items[5]' (length 2)`。

`{"$literal": <任意 JSON>}` 原样注入，用于字面量本身形如指令对象（例如工具需要 `{"$ref": "..."}` 字面量）的场合。

`args` 中不含任何指令时按**原始 JSON 元素**直接交给工具（不重新序列化）；含指令时先解析再序列化一次。

## 调度与失败语义

- 就绪队列按定义顺序取最小下标，因此同一份文档的可复现调度顺序稳定；并发上限 `RunOptions.MaxConcurrency`（默认 8）。
- 步骤完成即递减后继的未完成依赖数，归零即可启动（不是按层推进）。
- 工具返回 `HostToolResult.IsError`、抛异常、绑定解析失败都记为该步骤 `Failed`，**不抛出**。失败的步骤不会让后继
  变就绪，后继及其传递后继标记 `Skipped`；与失败无依赖关系的分支继续跑完。`onError: fail` 的“传播”就是这一点。
- 取消（调用方令牌）与超时（`RunOptions.Timeout`）都停止**新步骤**的调度，在飞步骤收到令牌；已启动但没有结果的步骤
  视情况标 `Cancelled`（在飞）或 `NotRun`（从未启动）。先等 `CancellationDrainTimeout`（默认 5 秒）让在飞步骤收尾，
  超时后直接返回并把这些步骤标 `Cancelled`（错误文本说明 drain 超时）；不合作的工具无法从引擎内终止。
- 运行结束状态：`Completed` / `Failed` / `Cancelled` / `TimedOut` / `Invalid`（校验有错，一个步骤都不跑）。
- `RunOptions.TimeProvider` 同时用于时间戳、步骤耗时与超时计时（`CancellationTokenSource(delay, TimeProvider)`），
  测试注入假时钟即可控制全部时间。

## 校验

`WorkflowValidator.Validate(definition)` 返回 `IReadOnlyList<WorkflowDiagnostic>`，永不因定义错误抛出；顺序确定：
文档级 → 按步骤（id、tool、after、args 绑定）→ 依赖环。错误码：
`MissingStepId`、`DuplicateStepId`、`MissingTool`、`InvalidAfterEntry`、`UnknownDependency`、`SelfDependency`、
`DependencyCycle`（附环路径，如 `a -> b -> a`）、`InvalidBindingPath`、`InvalidBindingDirective`、
`UnknownBindingStep`、`BindingOnNonDependency`、`UnsupportedVersion`。

`BindingOnNonDependency` 是刻意收紧的：只能绑定自己（传递）依赖的步骤，避免与并行步骤抢数据。环检测用 Kahn 剥点找出
环上节点，再按最小下标各报一次并给出路径。

## JSON 读取

`WorkflowJson.Parse(string | ReadOnlySpan<byte>)` 走源生成 `WorkflowJsonContext`（无反射、AOT 可用，
`IsAotCompatible=true` 分析器 0 警告，并以 Native AOT 发布二进制实测）。允许 `//` 注释与尾随逗号；属性名严格 camelCase。
JSON 语法错误、类型不符、`steps` 缺失、`after` 元素为 null、`onError` 取值未知等一律 `WorkflowFormatException`，
带上 STJ 给出的 `$.steps[1].tool` 路径与行列位置。

注意：源生成反序列化对**文档中缺失的属性写 default(T)**，不会保留属性初始化器，因此解析后会归一化：
`version` 缺失（0）视为 1，`steps` 缺失直接报格式错误。手写 `new WorkflowDefinition { ... }` 仍按属性初始化器取默认值。

## 结果

`WorkflowRunResult` 给出 `Status`、`Error`、`Steps`（恒定按定义顺序，含 `Index`/`Id`/`Tool`/`Status`/`Content`/
`IsError`/`Error`/`StartedAt`/`CompletedAt`/`Duration`/`DependsOn`）、`NeverRanStepIds`、`Diagnostics` 与整体耗时。
`StepResult.ParseContent()` 按需把 `Content` 解析为 `JsonDocument`（调用方负责释放）。
