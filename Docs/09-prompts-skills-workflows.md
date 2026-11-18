# 09 提示词、技能与工作流

本文件说明 Agent 行为如何被塑造：三层提示词、三类可覆盖模板、四层技能、默认编排的注入方式，以及两种“工作流”形态。

## 9.1 提示词三层

稳定前缀由四层依次叠加：**系统提示词 → 全局 Agent → 项目 Agent → 角色**。前三层按文件作用域合成，角色层是内置或用户 Agent 的正文；动态信息（时间、工作区状态）只作为请求末尾的一条临时 user 消息，不进历史与存储。

- 系统层是可覆盖的槽位 `system`；`PromptComposer` 把 `PromptLayers.System` 当作完整系统层，空时回落 `BuiltInPrompts.System`。
- 前缀字节稳定才命中提供商缓存：工具定义按名称排序、同类 Agent 一致；厂商身份（Claude 的身份块与 billing 块）只在 `AnthropicProvider` 里加，不进 `ComposedPrompt.StablePrefix`，也不进 `PromptFingerprint`。

## 9.2 三类模板（命令 / Agent / 槽位）

| 种类 | 触发 | frontmatter | 正文语义 |
|---|---|---|---|
| **命令（Command）** | 输入框 `/名称 参数`，展开后作为消息发出 | `description`、`argument-hint`、`mode` | `$ARGUMENTS`/`$@` 是整串参数，`$1`..`$9` 是第 n 个参数（空白分隔、引号成组）；都没用到而又给了参数时参数接在正文后空一行；单次从左到右替换（参数里的 `$1` 不再展开） |
| **Agent** | `spawn_agent` 或输入框 Agent 选择 | `name`、`description`、`model`、`thinking`（off/minimal/low/medium/high/xhigh/max）、`tools`、`mode` | 角色提示词；覆盖时没写的元数据（尤其 `tools` 白名单）沿用内置值，改提示词不会悄悄放开只读 Agent 的写权限 |
| **槽位（Slot）** | 由代码在固定位置取用 | 无 | 固定 id 的内置提示词，只能覆盖不能新增 |

- 内置命令 4 个：`review`、`explain`、`tests`、`fix`。
- 内置 Agent 4 个：`scout`、`worker`、`plan`、`reviewer`（名称是身份：内置 Agent 的覆盖文件不得改名；用户 Agent 不得占用内置名或内置 id；空 `tools` 列表无效，不当作“全部工具”）。
- Core 槽位：`system`、`compaction.summarizer`、`handover.summary`。
- App 槽位：`pr.workflow.<阶段>`（六段 PR 流程文本）、`automations.quota.continue`、`automations.ci.scout`（占位符 `{pr}` `{branch}` `{failures}`）、`automations.ci.fix`（`{pr}` `{analysis}`）。这三条自动化提示词是固定英文（模型输入，不随界面语言变）。
- 占位符是单花括号小写标识符、单次替换；声明了占位符的槽位里出现未声明的 `{名字}` 判为无效；未声明占位符的槽位不做替换与检查（PR 文本里的 JSON/URL 花括号原样保留）。
- 新建 Agent 时正文、模型与思考强度来自模板；`settings.json` 的 `agents` 段键是模板 id（文件名），不从名称推导。

## 9.3 作用域、优先级与修订

- 每个模板一个 `<id>.md`，id 即文件名（1–64 位 `a-z 0-9 . _ -`，字母或数字开头，不以点结尾，不是 Windows 设备名）。
- 作用域按优先级升序：**内置（编译期常量，只读）→ 全局 `~/.forge/{commands,agents,prompts}/` → 项目 `<工作区>/.forge/{commands,agents,prompts}/`**。
- 同种类同 id：高层默认**替换**低层；`mode: append` 时高层正文接在低层合成结果之后（空一行），没写的元数据继承。合成从链上最后一个 replace 成员开始。
- 无效文件不参与合成但照样列出并带问题码（`PromptTemplateProblemCode`），不静默跳过；“恢复默认”就是删掉自己的那份文件。
- 保存带读取时的修订号（文件字节 SHA-256 前 12 字节）：文件在外面被改过即 `Conflict`、不覆盖；新建传 `null` 修订号，文件已存在同样是冲突；写入走临时文件 + 改名。
- 读与列表拒绝超过 256 KiB 或不是合法 UTF-8 的文件（抛 `IOException`），编辑器不会打开它们，以免卡住文本框或覆盖看不见的字节。
- 有效时机：会话在建 `AgentHost` 时对工作区 `Resolve` 一次快照，稳定前缀在宿主生命周期内不变；改动从下一次建宿主起生效；已开的会话点标题栏搜索图标右侧的“重载提示词”按钮即热重载（下一条消息起换新宿主、根 Agent 从存储恢复，提示缓存失效一次；点击后提示成功并建议手动新开会话，不弹选项，是否重载由用户决定）。监视文件系统通知而不是每回合轮询：模板文件夹、技能文件夹与 `AGENTS.md` 在快照后变化时，按钮出现圆点并弹一次 toast。`pr_workflow` 每次调用按调用的工作目录现取覆盖。

## 9.4 设置页 `prompts`

- 左侧按 命令 / Agent / 内置提示词 分组，每行左侧种类图标（斜杠 / Agent / 齿轮），右侧来源徽标：`内置`（锁、灰）、`全局`、`项目`，另有 `覆盖内置` / `追加` / `已被覆盖`（行变暗）/ `无效`（错误色，悬停看问题）；顶部过滤 全部 / 内置 / 自定义；悬停显示文件路径。
- 右侧是表单 + Markdown 编辑器；内置项只读，按钮“复制到全局”/“复制到项目”；自定义项可保存、还原、删除（确认）；新建时选种类与作用域；槽位不能新建。
- 项目作用域就是前台会话的工作目录（worktree 或 scratch），与会话运行时、输入框读取的 `<工作区>/.forge` 同一处；没有前台会话时只能写全局。
- 有未保存修改时，点别的行或“新建”只给警告，再点同一目标才放弃修改；追加模式的勾选文案写明“接在下层同名模板之后”。

## 9.5 技能（Skills）

- 目录形态：`SKILL.md`（frontmatter `name`/`description`，可选 `workflow: true`，见 9.6）+ 可选的 `scripts/` 等；语义是“模型按描述自取”，与用户触发的命令不同。
- **四层发现，先到先得**：
  1. 工作区 `.github/skills`（徽标 `内置`）
  2. 工作区 `.forge/skills`（`项目`）
  3. `~/.forge/skills`（`全局`）
  4. 产品内置（`产品内置`，优先级最低）
- 产品内置四份编排技能随产品发布：`choosing-a-workflow`、`brainstorming`、`todos`、`shipping-a-pr`，作为 `Forge.App` 的嵌入资源，启动时物化到 `<UserData>/builtin-skills/<内容哈希>/<name>/SKILL.md`（幂等、best-effort 清旧版本、写不出只记日志）；资源名里的连字符会变成下划线，所以技能名一律取 front matter 的 `name`。
- 用户与项目用同名技能即可覆盖默认编排，不需要第二套覆盖机制；技能发现顺序即优先级。
- `skill` 工具：只读、无审批，`{"name": "…"}` 返回该技能 `SKILL.md` 全文（上限 64000 字符，超出截断并说明）；名字只在本会话发现到的四个根里解析（大小写不敏感），模型给不出路径；未知名返回可用名列表。它是技能正文到达模型的**唯一**通道（物化目录在工作区之外，`read` 的工作区路径校验会拒绝）。
- `skills` 分区（只读）：名称、描述、`SKILL.md` 路径、来源徽标。
- 输入框 `/` 弹层把命令与技能合并列出，命令在前、同名时命令胜出；插件的 `WorkflowDefinition` 以后按“来源 = 插件”的技能接入同一目录；技能的 `scripts/` 只能经现有 shell/代码执行工具运行，走原有审批。

## 9.6 默认编排的注入

- `OrchestrationPrompt.Build` 是纯函数：列出技能名 + 描述（按目录序数排序），加一段 per-task 路由规则（新工作先取 `choosing-a-workflow` 并照它路由；功能/行为/Bug → `brainstorming`；交付 PR → `shipping-a-pr`；问答/检查/不改行为的编辑 → 不路由）。没有技能或没有路由技能时返回空串。
- 只追加到**根 Agent 的 role 层**（不写回根定义，`SameDefinition` 仍比较用户所选，不会每回合重建根）；**不进 system 层**——system 层整棵 Agent 树共享，注入它会让被派活的子 Agent 也去选工作流、访谈用户。
- 用户选定的 Agent 种类若带显式工具白名单且不含 `skill`，不注入；索引与路由是每次建宿主的快照。
- 开关 `preferences.orchestrationEnabled`（默认开）：关掉即不注入；四份内置技能是模型输入，英文、不本地化。
- 编排的约束力是混合的：路由、头脑风暴、计划都是**提示引导**（Agent 可以不遵守）；两个节点由代码强制——改了代码的 TODO 步骤标 done 要过步骤完成门禁（02.7），`pr_create` 要求本会话计划的分组步骤全部 done（04.7）。步骤门禁与自动修复的开关见 02.7（`reviewToolEnabled`/`stepReviewRequired`/`reviewAutoFix`）；`pr_create` 的闸门不可关。

### 自定义编排

要加一套自己的工作流（例如“紧急修复”：不访谈、直接定位、派 Worker 改、Reviewer 审、再提 PR），路径如下：

1. **写工作流技能**：放进 `.forge/skills/<name>/SKILL.md`（项目）或 `~/.forge/skills/<name>/SKILL.md`（全局），frontmatter 写 `workflow: true`。带这个标记的技能（产品内置的 `brainstorming`、`shipping-a-pr` 也带）会出现在输入区“编排”选择器里，可为下一条消息单独选用；选中后该消息头部带指令“跳过路由，用 `skill` 工具读它并照做”。没有标记的技能照常进技能索引，只是不进选择器；路由技能和 `todos` 不是工作流，不要标。
2. **让自动路由认识它**：路由规则写在 `choosing-a-workflow` 里，目前没有登记表；要让 Auto 模式也选它，需用同名技能整体覆盖 `choosing-a-workflow`（在决策表里加一行）。只靠选择器手选则不用覆盖。
3. **谁执行**：工作流正文自己写。主会话默认自己执行；要委派，在正文里写“用 `spawn_agent` 派 Worker 做某步、`request_review` 让 Reviewer 评”。子 Agent 能用哪些工具由它的 Agent 文件（`tools` 白名单）决定，技能里不能声明；子 Agent 不会收到编排，也不会再去路由。Reviewer 同名 Agent 文件可覆盖其提示词与白名单。
4. **门禁**：步骤完成门禁（改了代码的步骤标 done 要过 `request_review`）与 `pr_create` 的闸门是代码强制的，工作流技能不能新增或放宽；步骤门禁按 02.7 的三个设置分别开关，`pr_create` 闸门不可关。

## 9.7 两种“工作流”形态

### 动态工作流（Markdown 技能）

- 工作流以 Markdown 技能文件定义（frontmatter + 步骤文本，同 SKILL.md 形态），由 Agent 阅读并执行；用户与插件均可提供，由路由工作流按任务类型选择（参考 ThinkRail 的 choosing-a-workflow）。
- 不引入新语法，也不提供 C# 状态机形态。

### 声明式工作流（L0，JSON）

- 一份 JSON 文档描述若干步骤，每步调用一个已注册工具；先后关系只由 `after` 表达，无关系的步骤并行；后一步的 `args` 可用 JSON 路径引用前面步骤的输出。
- 格式契约：
  - `after` 省略 = 依赖紧邻上一步；`after: []` = 无依赖。
  - 绑定只能指向自己的**传递依赖**（`BindingOnNonDependency` 收紧，避免与并行步骤抢数据）。
  - 指令只有 `$ref` 与 `$literal`；路径字段只有 `content`/`isError`/`status`/`error`/`json`。
  - 两个绑定根：`steps`（前面步骤的输出）与 `$input`（本次运行的 `RunOptions.Input`）；`$input` 不是步骤，校验器不要求它进 `after`；`$` 前缀必需（裸写 `input.x` 会被当成步骤名）。
- 调度：就绪集取最小下标启动（同一文档调度顺序稳定），步骤完成即递减后继未完成依赖数、归零即启动；并发上限 `MaxConcurrency` 默认 8。
- 失败只传播到依赖方：工具报错、抛异常、绑定解析失败都记为该步 `Failed` 且不抛出；后继及其传递后继停在 `Skipped`，无关分支继续跑完，运行状态 `Failed`。`onError` 目前只有 `fail`。
- 取消/超时只停调度、不强行终止：在飞步骤收到令牌，等 `CancellationDrainTimeout`（默认 5 秒）后仍未结束的标 `Cancelled` 并立即返回；时间统一走 `TimeProvider`（测试可注入假时钟）。
- AOT 与零反射：JSON 走源生成 `WorkflowJsonContext`；`args` 无指令时按原始 `JsonElement` 原样传给工具；有指令时用 `Utf8JsonWriter` 写一次再解析。
- 校验不抛异常：`WorkflowValidator.Validate` 返回确定顺序的诊断；环检测用 Kahn 剥点定位环上节点；`steps` 缺失是格式错误（实现上由一次 `Utf8JsonReader` 扫描判定，不依赖 STJ 对缺失属性的处理）。
- 工具调用一律经 `IHostFacade.CallToolAsync`：引擎不持工具注册表、不做审批、不落盘；审批在主程序（`host.tool_call`）。

### 插件工作流包

- 插件 manifest 的 `kind: workflow` + `workflow`（单个安全文件名，默认 `workflow.json`）+ `tool:{name,description,inputSchema}`：发现时不编译程序集，`sourceHash` 是行尾归一化后的 `workflow.json` 哈希，文件域只放它，`sdkVersion` 用 `forge.workflows/1`。
- 审批通过后发布一个工具（副作用 `Execute`）；`plugin.execute` 命中工作流包时在 Host 内用 `WorkflowEngine.RunAsync` 运行，不启动 Worker；`RunOptions.Input` = 调用参数，`Timeout` = 剩余预算。
- 步骤的工具调用经 `host.tool_call` 使用与插件相同的 run 绑定与逐步审批；成功返回无后继步骤中最后完成那一步的内容，失败返回失败步骤与原因并置 `isError`。

相关文档：[02-agent-runtime.md](02-agent-runtime.md)（Agent 行为与门禁）、[11-extensions-and-codemode.md](11-extensions-and-codemode.md)（插件与工作流包）、[15-appendix.md](15-appendix.md)（槽位与常量）。
