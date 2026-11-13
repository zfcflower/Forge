# ThinkRail 缺口核查(2026-11-13)与实施计划

对照 `2026-11-12-thinkrail-gap.md` 的 14 项 + 低优先级项,逐条按当前代码(2026-11-13 工作树)重新核查。**该文档有 4 处已过时(#4、#5、#8、#12)**,本文件是它的替代版:状态以本文件为准,原文件保留作历史。

本版另加两条硬约束(用户要求):
1. **排版类需求记为待定**,不进波次(见「待定」)。
2. **波次之间零交叉**:交叉点全部提前在 W0 拆掉,W1–W9 之后按**文件级互斥**并行;任何跨波的语义依赖也在 W0 用「只接线不改行为」的方式预先满足。
3. 每一波开工前先交付该波的实现说明(方案 + 被否的备选 + 为什么这是最优),确认后才动手。
4. **凡是用户合理会想调的值,一律走设置,不写死在代码里**(见「六·二、可配置项登记」)。代码里只留**默认值常量**,取值经 `AppPreferences`;硬编码只允许用于协议常量、安全边界与内部实现细节。评审时 grep 新增的魔法数字,没有登记又不属于上述三类的按缺陷处理。

## 一、核查结果

判定口径:`已实现` = 达到 ThinkRail 对应表面的行为;`部分` = 明确列出缺的那一半;`缺失` = 无。证据都是 `src/` 下的符号(不含 `bin`/`obj`/`artifacts`)。

| # | 缺口 | 实况 | 关键证据 | 文档 vs 实况 |
|---|---|---|---|---|
| 1 | 工具返回图片 | **缺失** | `Forge.Abstractions/Tools.cs:7` `ToolResult(string Content,…)`;`Messages.cs:14` `ToolResultBlock` 单串;`AgentLoop.cs:898` 只能从字符串构造;`Mcp/McpToolAdapter.cs:48` 丢弃 `McpToolCallResult.Content` 的结构化块(`McpContentFlattener.cs:131` 把 image 降级成 `[image: …]` 文本);App 工具行只画参数与结果文本(`App.axaml:673`) | 一致 |
| 2 | 全局搜索 / 命令面板 | **缺失** | `MainWindow.axaml:83` 搜索只是图标;`SPEC.md:365`「搜索框暂无行为」;无 palette 视图/VM、无 `Ctrl+P`。现成可复用件:`Services/ComposerSuggestions.cs`、`SqliteSessionStore.SearchAsync`(FTS5)、`Forge.IO` grep 引擎、`LspClient.RequestDocumentSymbolsAsync`(`:288`) | 一致 |
| 3 | Plan 会话 | **部分** | 有:`plan_summaries`/`todo_groups`(`StorageSchema.cs:134`)、`plan_summary_get/set`(`TodoTool.cs:22`)、TODO 面板、PR 正文预填(`PrBodyRenderer.cs:37`)。缺:计划摘要界面、修订历史(单行 upsert)、check-all、下一步、导出 | 一致 |
| 4 | Ship 全流程 / checks 面板 | **部分**(比文档多) | 有:推送、开 PR(`GitSyncViewModel.cs:565`)、合并 PR + 删远端分支(`:920`、`:972`)、本地合并(`:742`)、CI 自动化等待检查(`CiChain.cs:336`)。缺:**checks 面板**(后端 `PrChecksPoller`/`pr_checks` 已在,面板复用)、`gh pr ready`、手动提交(只有 `StepCommitter` 按 TODO 步骤自动提交) | 文档写「只剩片段」已过时 |
| 5 | 编辑器 LSP UI | **部分**(比文档多) | 已接:诊断波浪线/概览/悬停(`FileDocumentView.Lsp.cs:50`)、补全(`:290`)、跳转定义(`:534`)。缺:**语义 token 叠加**(`HighlightMerger.cs`、`SemanticTokenMapper.cs` 在 App 零调用)、查找引用 UI(`LspClient.cs:392` 后端已有)、问题列表面板、code action / rename(后端也没有) | 文档与 `SPEC.md:363` 都过时 |
| 6 | 富文件查看器 | **缺失** | 预览正文唯一形态是 AvaloniaEdit 文本区(`FileDocumentView.axaml:41`),非文本走错误串 `files.binary`(`FileDocumentViewModel.Editing.cs:190`);唯一富预览是 diff 里的 Markdown。无图片/CSV/notebook/PDF/SVG/HTML、无 LFS 识别、无图片 diff | 一致 |
| 7 | MCP 管理界面 | **部分** | UI 只有只读行(`Settings/InstrumentSettingsSections.cs:178`)。后端已有而 UI 零调用:`McpServerManager.StopAsync/RestartAsync/SetEnabledAsync/RefreshToolsAsync`;配置只有 `McpConfigLoader.Load`,无写入;`StderrTail` 从不显示;无导入、无预设 | 一致 |
| 8 | MCP 审批与信任 | **部分**(比文档多) | 有:通用审批卡(工具名/副作用/工作目录/参数)、三级作用域规则(`ApprovalRules.cs:17`)、撤销入口。缺:卡片不含 server 的 command/args/env/headers、卡片上没有即时撤销、无工具检索上限 | 文档写「无」不准确 |
| 9 | MCP OAuth / resources/subscribe / progress | **缺失** | `Mcp/SPEC.md:26` 列为未决;`McpProtocol.cs:11` 只有常量;`oauth` 零命中 | 一致 |
| 10 | 扩展包仓库 | **部分** | 有:已安装列表 + 启用/禁用/删除 + 待审草稿 + 规则。缺:仓库浏览/安装/检查更新、内置/捆绑/已安装区分 | 一致 |
| 11 | 自动更新 | **缺失** | 全仓无更新检查;CI 只 `dotnet publish` + 上传 artifact,无安装包,因此也无安装日志/失败恢复 | 一致 |
| 12 | 首启引导 + 克隆/新建项目 | **部分** | 有:添加项目 = 选本地文件夹 → 非仓库问一句 `git init`(`MainWindowViewModel.cs:2789`)。缺:首启向导、远端克隆(`GitCli*.cs` 无 Clone)、新建目录 | 文档写「无」已过时 |
| 13 | 资源检查器 | **部分** | 子 Agent 有耗时 + 停止(`SubAgentView.axaml:46`)。缺:运行中命令的独立列表、耗时、停止、日志尾,**且没有进度事件**(`Agents.cs:14` 只有 started/finished) | 一致 |
| 14a | 模型可见性过滤 | **缺失** | 选型器列全部模型,只有钉住排序(`ComposerPreferences.cs:14`) | 一致 |
| 14b | 子 Agent 并发上限/开关 | **部分** | 上限硬编码 `AgentOptions.cs:19` `MaxParallelSubAgents = 4`;关掉只能禁用 `spawn_agent` 工具 | 一致 |
| 14c | 聊天显示选项 | **缺失 → 待定** | 高度/宽度写死在 `SessionView.axaml:789`、`:365` | 见「待定」 |
| 14d | 快捷键设置页 | **缺失** | 快捷键是分散的 XAML `KeyBinding`,无 keymap | 一致 |
| 14e | 工作区闲置自动归档 | **部分** | `Housekeeping.cs:24` 写死 7 天,不可配,且只归档自动化创建的根会话 | 一致 |
| 14f | 通知权限引导 | **缺失** | `notifications` 分区只有总开关;`SystemNotifier` 失败只 Trace | 一致 |
| L1 | 消息复制 | **部分** | 正文可选中,无逐条/代码块复制;聊天完全不过 Markdown 渲染(无 fenced code) | 见 W0(复制)/W4b(Markdown) |
| L2 | 评审评论线程 / 过期评论视图 | **部分** | 数据层齐全(`PrPlatformModels.cs:171` 带 `IsOutdated` 与嵌套回复),只以扁平文本给 agent,无 UI | — |
| L3 | 会话内导航(breadcrumbs) | **缺失** | `breadcrumb` 零命中 | — |
| L4 | 会话用量/耗时统计 | **部分** | token/缓存/成本在输入框上条,每回合耗时在回合标题;无会话级累计与汇总界面 | — |
| L5 | Mermaid / 可视化卡片 | **缺失** | 无 JS 通道,Markdown 渲染器无图表类型 | — |
| L6 | 终端缓冲区搜索 / 重启已退出终端 | **缺失** | SPEC 已记为推迟 | — |
| L7 | 评审包 + `request_review` 工具 | **缺失** | 工具面约 50 个无 `request_review`;Reviewer 只有 `read/grep/find/ls`,连 `git_diff` 都没有 | — |
| L8 | 布局设置对话框 | **缺失 → 待定** | 布局 = 拖拽 + 快照 v5 | 见「待定」 |

## 二、已定 / 待定

### 已定(写进波次,不再讨论)

| 项 | 结论 |
|---|---|
| PDF / HTML 预览 | **不做内部渲染**:占位页 + 「用本机默认程序打开」(`Launcher`)。理由:能交给 OS 的事不引渲染器,PDF 渲染器都是原生库或大依赖,HTML 渲染等于再塞一个浏览器引擎。 |
| Plan 面板写权限 | **只读 + 「让 agent 收尾这一步」按钮**。保持「状态只由 agent 改」与 done→`StepCommitter` 自动提交语义不变,不新增存储写路径。 |
| 富查看器范围 | 图片、CSV/TSV、`.ipynb`、LFS 指针、二进制占位 —— 全部零新依赖。 |

### 待定(由你在动手前最后定,先不进波次)

| 项 | 状态 | 定它需要什么 |
|---|---|---|
| 流程图 / Mermaid 渲染选型 | 调研已完成(见下节),**等你批准是否新增两个包** | 批准 `Mermaider` + `Svg.Skia`,或先只上 ASCII 退路 |
| 自动更新形态 | 等拍板 | 自写 zip 更新器 vs 引 Velopack(要签名与托管) |
| 排版类:聊天显示选项(消息宽度/行宽、消息顺序、输入框高度)、布局设置对话框、间距/字号层级微调 | 等口径 | 目标版式(窄栏还是铺满)、要不要预设与重置 |

### 流程图 / Mermaid:调研结论

**1. 让模型输出什么格式最不容易错。** 结论是 Mermaid 文本 DSL,理由是三条独立证据:

- **token 效率**:同一张时序图,Mermaid 约 50 token、PlantUML 约 80、Excalidraw JSON 约 500、draw.io XML 约 1200——JSON/XML 类表示比 markdown 类表示贵一个数量级,而长输出里 token 越省越不容易中途走形。
- **过程建模研究**(55 个流程描述 × 9 种表示):Mermaid 在六项建模准则上综合得分最高;BPMN 文本只在元素相似度上更好。
- **编译率 ≠ 正确率**:kUML 那套基准里 PlantUML 与 Mermaid 首轮几乎都能编译,但结构保真度只有三成多(该基准是厂商自评,只取方法论这一条)。另有专门的基准 `MermaidSeqBench`(2025-11)拿 Mermaid 时序图当任务,说明它已是事实标准。

**模型最擅长的图类型**(一行一节点/一行一边、错误可定位):`flowchart`、`sequenceDiagram`、`stateDiagram-v2`、`classDiagram`、`erDiagram`。其余 19 种建议默认关掉,让模型别用。

**2. 渲染路线。** .NET 侧已有 `AvaloniaUI/mermaider`(NuGet `Mermaider`,MIT):纯 .NET 的 Mermaid 解析 + 布局(Sugiyama,自带正交路由)+ SVG 渲染,覆盖 24 种图型,内置 SVG 白名单消毒,AOT 在 CI 三平台实测,小流程图约 95 µs / 209 KB,还能直接输出 ASCII。这就是"官方次选"最接近的东西——不引 JS、不引浏览器、不引原生二进制。

**缺口只剩显示**:Avalonia 12 没有内建 SVG 控件,需要 `Svg.Skia` 把 mermaider 的 SVG 光栅化(Avalonia 本身就建在 Skia 上)。两个包都在白名单外,需你批准。

**3. 必须配失败回路。** Mermaid 语法错误率不低(微软 GenAIScript 那篇"Mermaids Unbroken"就是专门处理它):渲染失败时**显示源码 + 错误行 + 一键回给模型修**,绝不静默空白。这一条与选型无关,是交互必需。

**4. 被否的备选。** WebView + mermaid.js(Avalonia 12 起原生 WebView 开源)→ 引入 JS 运行时,与"不含 JS 运行时依赖"冲突;`mermaid.ink` / kroki 在线渲染 → 把私有架构图发给第三方,不可接受;Graphviz `dot` 原生二进制 → 模型亲和度不如 Mermaid、多一份原生分发、样式弱;自写 Mermaid 解析器 → 重复造 mermaider。

**5. 退路(零新包)。** 用 mermaider 的 `RenderAscii` 把图渲成字符画嵌进等宽文本框,先让"能看懂"成立,样式后补。

## 三、W0:拆交叉点(串行,主 Agent 独占,必须先合入)

**W0 拆成两步,各自单独提交、单独构建全绿**:**W0a**(W0-1 契约 + W0-2 schema + 流水线接线,约 2 天)与 **W0b**(W0-3 搬运/接线/预建 + W0-4 小件,约 3 天)。W0b 只在 W0a 合入后开始。

**目标:把"多个波次会碰同一个文件"这件事在 W0 里消灭。** W0 交付后仓库照常编译、**行为不变**(除了消息复制这类无交叉的小件),之后每一波只在自己那一组文件里写。

### W0-1 契约定稿(全部在 `Forge.Abstractions`,一次改完)

- `ToolResult` 从单串改为块列表(复用 `ContentBlock`;文本仍是默认形态),`ToolResultBlock` 同步;`ToolResult` 保留 `string Content` 的便捷构造,避免调用点全炸。
- 新增 `AgentToolProgress`(工具流式输出/进度);`AgentToolStarted/Finished` 不动。
- 这些改动的**所有调用点接线都在 W0 做完**:`Core/Agents/AgentLoop.cs`、`Core/Providers/*`、会话存储序列化与回放、`McpToolAdapter`、`ViewModels/SessionWorkspace.cs` 的工具事件分支、`ViewModels/Transcript.cs` 的工具行(先按原样只取文本 → 行为不变)。W0 结束时多块能力"已在流水线上通了,只是没人渲染"。

### W0-2 存储阶梯一次升版

- `Core/Storage/StorageSchema.cs` 一次升到下一版:加计划修订表 + 工具结果多块的表示所需的列/标记(两者同一版,避免 W4、W7 各自再升一次)。
- 迁移民档 + 升级/降级测试骨架在 W0;W4 与 W7 只写各自的读写代码,不再碰 schema。

### W0-3 UI 接线与占位(交叉文件的唯一写入者)

| 文件 | W0 做什么 | 之后归属 |
|---|---|---|
| `App.axaml` | 为 palette / checks 面板 / 计划面板 / 资源检查器 / MCP 分区 / 向导 各注册一个 `DataTemplate`,指向 W0 建的 stub 视图;消息复制按钮就位并接上实现 | 冻结(各波不再改) |
| `MainWindow.axaml(.cs)` | palette 覆盖层、checks、资源检查器三处 host;搜索图标换成真的按钮 | 冻结 |
| `ViewModels/MainWindowViewModel.cs` | 预建 partial:`MainWindowViewModel.{Palette,Checks,Plan,Inspector,Onboarding}.cs`(空实现)并在构造里接线 | 主文件**冻结**(含已改后台的 `DescribePullRequest`),partial 各归对应波 |
| `ViewModels/SessionWorkspace.cs`(2360 行) | **按职责拆 partial**:`SessionWorkspace.cs`(壳/生命周期)+ `SessionWorkspace.Tools.cs`(工具事件与行更新)+ `SessionWorkspace.Activity.cs`(运行态/取消)。纯搬运,不改行为 | `.Tools.cs`+主文件归 W8;`.Activity.cs` 归 W8 |
| `ViewModels/Transcript.cs` | 拆三份:`Transcript.cs`(消息/文本/代码块)、`Transcript.Tools.cs`(工具行 VM 的运行态/日志尾)、`Transcript.ToolImages.cs`(`ToolRowViewModel` 的 partial,只放结果图块与缩略图) | 依次归 W4b、W8、W4 |
| `Localization/Loc.cs` + `KeyRegistryTests` | `KeyRegistryTests` 反射扫 `Forge.App.Localization` 命名空间并要求 `Loc.EnsureRegistered()` 登记,所以**新表的注册行是共享点**:W0 一次性为本轮所有新表(Palette/Checks/Plan/Inspector/Mcp/Onboarding/ChatMarkdown/Review)建空表并登记 | `Loc.cs` 冻结,各波只改自己的表 |
| 通知权限引导接口 | W0 在 `Notifications/` 声明 `INotificationPermissionGuide`(状态查询 + 触发引导),W1 实现,W9 首启向导只调接口 | 接口冻结,实现归 W1 |
| `ViewModels/Panels.cs`(2518 行) | 拆出 `FileDocumentViewModel` 到独立文件 `ViewModels/FileDocumentViewModel.cs`(`.Editing/.Lsp/.Review` 三片已有) | 新文件 + `.Editing` 归 W4,`.Lsp` 归 W2,`.Review` 冻结 |
| `Composition/AppComposition.cs` | 抽出 `Composition/<Feature>Composition.cs` 模块(照已有 `Automations/AutomationComposition.cs` 的样子),`Create()` 里一次性调用 | 各模块归对应波 |
| `Preferences/SettingsSection.cs` | 一次性分配全部新分区 id 与 order(models-visibility、idle-archive、notification-permission、mcp 扩写沿用旧 id) | 冻结 |
| `Preferences/AppPreferences.cs` + `PreferencesJsonContext` | 预加**第六·二节登记的全部属性**(默认值 + 范围校验 + 迁移默认),各波只读取、不再增删字段 | 冻结 |
| `Forge.Abstractions` 的进度回调 | 补 `AgentToolProgress` 的**生产侧**接口(工具执行上下文里的进度回调),并给 bash/终端类工具接上产出;否则 W8 只能显示开始/结束 | 冻结 |
| `ViewModels/Settings/InstrumentSettingsSections.cs`(注意路径) | 该文件含 Approvals/Skills/Mcp 三个分区类;W0 先把 Mcp 分区拆到 `McpSettingsSectionViewModel.cs`,W6 只改那个文件 | 拆分后 Mcp 归 W6 |
| `App.axaml` 的 `ToolRowViewModel` 模板(`:673`) | 抽成独立资源字典 `Resources/ToolRow.axaml`,W4(缩略图)与 W8(日志尾)共用同一文件的两个区段前,先由 W0 分好区段 | 归 W4,W8 只在 `Transcript.Tools.cs` 加数据 |
| `GitCli.Clone.cs` | 预建空 partial | 归 W9(W5 不碰) |
| `Resources/Icons.axaml` | 预加这轮需要的图标 | 冻结 |
| `Localization/` | 每波一张新表(palette/checks/plan/inspector/mcp/onboarding)+ 空注册;共享表(`Strings.Session`/`Inspector`/`Core`)本轮新增键也在 W0 加完 | 各波只改自己的表 |
| `src/Forge.Editor/` | 确认 W2(语义 token 快照)、W3(文档符号)所需 API 已暴露;缺的补上 | 归 W2 |

### W0-4 顺手做掉、无交叉的小件

- 消息复制(整条消息 + 复制纯文本),复用 diff/终端已有的剪贴板写法。
- 修 `DescribePullRequest()` 在 UI 线程同步打三次 sqlite(`MainWindowViewModel.cs:2421`)。
- 更新 `Forge.App/SPEC.md` 未决节的过时 LSP 描述;在原调研文档顶部加一行指向本文件。

**W0 完成判据**:`dotnet build` 全绿 + `Forge.App.Tests` 全绿(纯搬运/接线,不得有测试行为变化);`git diff` 逐文件复核只含契约/搬运/接线;交叉点矩阵(下表)里"冻结"的文件之后无人再动。

## 四、波次与文件归属矩阵

W1–W9(含 W4b、W12)之间**文件级零重叠**,可以同时派给多个 Worker;每波各自跑相关测试。W10 串行收尾。

| 波 | 内容 | 独占路径(其他人不许改) | 规模 |
|---|---|---|---|
| **W1** 设置与运行参数 | 模型可见性过滤(14a)、子 Agent 并发上限与开关(14b)、闲置归档天数与范围可配(14e)、通知权限引导(14f) | `Preferences/AppPreferences.cs` 的消费点、`Agents/**`(SessionRuntime、注入 `AgentOptions`)、`Core/Agents/AgentOptions.cs`、`Automations/Housekeeping.cs`、`Notifications/**`、`Settings/ModelsVisibility*`、`ViewModels/ComposerViewModel.cs`、`INotificationPermissionGuide` 的实现 | 2 天 |
| **W2** 编辑器 | 语义 token 叠加(可见行 + 版本号缓存,delta 只失效受影响行)、查找引用列表、问题面板 | `src/Forge.Editor/**`、`Editing/**`、`ViewModels/FileDocumentViewModel.Lsp.cs`、`Views/FileDocumentView.Lsp.cs`、`Views/ProblemsPanelView*` | 3 天 |
| **W3** 全局搜索 / 命令面板 | 面板 + 五个来源(文件/内容/会话/命令/符号)+ 预览 + `Ctrl+P`/`Ctrl+Shift+P`/`Ctrl+Shift+F` | `ViewModels/PaletteViewModel*.cs`、`Views/PaletteView*`、`Services/Palette*.cs`、`ViewModels/MainWindowViewModel.Palette.cs`、`Localization/Strings.Palette.cs` | 4–5 天 |
| **W4** 工具图片 + 富查看器 | 工具行缩略图、MCP 结构化透传、provider 降级(tool 角色不能带图 → 随后的 user 消息);图片/CSV/IPYNB/LFS/二进制占位查看器;PDF/HTML 占位 + 系统默认程序打开 | `Forge.Mcp/McpToolAdapter.cs`+`McpContentFlattener.cs`、`Core/Providers/*`、会话存储读写(不碰 schema)、`ViewModels/Transcript.ToolImages.cs`、`ViewModels/FileDocumentViewModel.cs`+`.Editing.cs`+`Views/FileDocumentView.axaml(.cs)`(**不含** `.Lsp.cs`/`.Review.cs`;W2 若需改 axaml,在 W0 预留 overlay 插槽)、`Resources/ToolRow.axaml`、`Views/Preview/**` | 4 天 |
| **W4b** 聊天 Markdown 渲染 | 聊天消息走 Markdown(标题/列表/表格/链接/fenced code,代码块带复制与语言标签);复用 `MarkdownRenderer`,流式增量不整段重排 | `Changes/Markdown/**`、`ViewModels/Transcript.cs`、`Views/ChatMarkdown*`、`Localization/Strings.ChatMarkdown.cs` | 4 天 |
| **W5** PR checks 与收尾 | checks 面板(逐项状态/耗时/链接、等待、失败日志尾、让 agent 修)、`gh pr ready`、ship 补「合并后收尾」 | `Forge.Workspace/Git/IPrPlatform.cs`+`GitHubPlatform.cs`+`PrCheck*.cs`+`PrChecksPoller.cs`(**不含** `GitCli.Clone.cs`)、`Views/ChecksPanelView*`、`ViewModels/MainWindowViewModel.Checks.cs` | 3 天 |
| **W6** MCP 管理面 | 配置写入(原子写 + 同款互斥)、增删改/启停/测试/重连/工具清单/错误与 stderr 尾、从 Claude/Cursor 导入、预设;审批卡补 command/args/cwd/env/headers 键名与即时撤销 | `Forge.Mcp/McpConfig*.cs`、`McpServerManager.cs`、`McpServerState.cs`、`ViewModels/Settings/McpSettingsSectionViewModel.cs`(W0 从 `InstrumentSettingsSections.cs` 拆出)、`Views/McpSettingsView*`、`Approval/**` | 4–5 天 |
| **W7** Plan 会话 | 摘要面板 + 步骤与验证状态、修订历史、下一步、导出 Markdown、从计划开 PR | `Core/Storage/SqliteTodoStore.cs`(修订读写)、`ViewModels/PlanPanelViewModel*`、`Views/PlanPanelView*`、`ViewModels/MainWindowViewModel.Plan.cs` | 4–6 天 |
| **W8** 资源检查器 | 运行中工具/命令与子 Agent 统一列表(耗时、日志尾、停止)、消费 `AgentToolProgress` | `ViewModels/SessionWorkspace*.cs`、`ViewModels/Transcript.Tools.cs`、`ViewModels/SessionInstrumentsViewModel.cs`、`Views/ResourceInspectorView*`、`ViewModels/MainWindowViewModel.Inspector.cs` | 3 天 |
| **W9** 首启与项目创建 | 首启向导(语言/提供商/通知权限,权限一步只调 `INotificationPermissionGuide`,不碰 `Notifications/**`)、克隆仓库(带进度与取消)、新建项目目录 + `git init` | `Views/Onboarding*`、`ViewModels/Onboarding*`、`ViewModels/MainWindowViewModel.Onboarding.cs`、`Forge.Workspace/Git/GitCli.Clone.cs`(W0 预建)、`Services/ProjectCreation*` | 3 天 |
| **W12** 评审包 + `request_review` | 给 Reviewer 补 `git_diff` 与变更集包;`request_review` 工具(主 Agent 发起评审、结果回到会话);评审是产品核心目标,放在并行批里 | `Forge.Core/Prompts/BuiltInAgents.cs`、新建 `Forge.Core/Tools/RequestReview*.cs` + `ReviewPackage*.cs`、`Localization/Strings.Review.cs` | 4 天 |
| **W11** 流程图 / Mermaid(**等拍板,且串在 W4b 之后**) | 聊天与 diff 预览里的 ```mermaid 代码块本地渲染、图类型白名单(可配置)、失败回路(源码 + 错误行 + 回给模型修)、主题跟随 | `Changes/Markdown/**`(W4b 先落地代码块渲染)、新建 `Views/Mermaid*` + `Services/MermaidRender*` | 3 天 |

**跨波语义依赖(全部在 W0 满足,不构成串行边)**:W4/W8 依赖 W0 的契约、进度生产侧与流水线接线;W7 依赖 W0 的 schema 升版;W2/W3 依赖 W0 的 `Forge.Editor` API 确认;W5/W6/W9 依赖 W0 的 host/模板/模块骨架;W9 依赖 W0 的 `INotificationPermissionGuide` 接口;全部新文案表依赖 W0 的 `Loc.cs` 登记。**W1–W9(含 W4b、W12)之间没有任何一条"先做 A 才能做 B"的边。**

**唯一的串行边**:W11(Mermaid)在 W4b 之后、且要你先批准两个包——它要往 W4b 刚建的聊天代码块渲染器里插一层,同一批文件,所以不并行。

### W10:需要先定契约或要拍板的大件(串行)

| 项 | 前置 |
|---|---|
| 扩展包仓库(10) | 先定包格式(zip + manifest + hash)、安装/更新/卸载契约,`PluginRecord` 加"内置/捆绑/已装"维度 |
| 自动更新(11) | 需要你拍板:`自写 下载→校验→替换→重启` 还是引 Velopack(白名单外,要签名与托管) |
| MCP OAuth / `resources/subscribe` / progress(9) | 先改 `Mcp/SPEC.md` 未决;OAuth 要动态注册 + PKCE,token 进加密库 |
| 快捷键页(14d) | 先把分散的 XAML `KeyBinding` 收敛成 keymap 表 |
| 评审包 + `request_review`(L7) | 已升为并行波次 W12(见上) |
| L2/L3/L4/L6 | 按需(PR 评论线程 UI、breadcrumbs、用量统计、终端搜索与重启) |
| L5 Mermaid / 流程图 | 已升为独立波次 W11(见上,等拍板) |
| L8 布局设置对话框 | 待定(排版类) |

## 五、每一波的"实现说明"交付规范

动手前我给出,等确认后再写代码:

1. **方案**:要改的每个文件与关键方法、数据结构、线程与取消模型、失败路径。
2. **被否的备选**:至少一个,以及它为什么不选(不是"没试",是判据上输)。
3. **为什么最优**,按固定四条判据逐条回答:
   - **性能**(`forge-goal` 第一约束):UI 线程做什么、分配在哪、列表是否虚拟化、热路径加不加锁;
   - **依赖与 AOT**:有没有新包/新 P-Invoke/Roslyn;AOT 分析器是否仍 0 警告;
   - **契约与可测性**:改的是哪个契约、谁是唯一所有者、测试怎么写才算证明;
   - **回滚成本**:这一波单独 revert 是否干净。
4. **验收口径**:跑哪几个测试项目/过滤,以及手测项。

## 六、执行口径(按 `AGENTS.md`)

- 先改拥有该行为的 SPEC(`Forge.App`/`Forge.Core`/`Forge.Mcp`/`Forge.Workspace`/`Forge.Editor`),`Forge.Abstractions` 的契约定稿只在 W0 一次。
- W1–W9、W4b、W12 可并行派 Worker:各自仓库副本、只拷回上表归属路径、不碰 `*.csproj`/`Directory.Build.props`/`Forge.slnx`、不执行 git 命令;跑完相关测试全绿再拷回。
- **每波三个独立会话**:实现 Worker 写实现;**测试 Agent**(另开会话)只写测试,必须覆盖关键路径、边界、失败/取消/并发,Skip 只许记录真实缺陷;**Review Agent**(再开一个独立会话)只读,按「架构约束 > 测试是否足以证明 > 代码质量」排序输出。主 Agent 复核时构建 + 跑相关测试 + 检查是否越界改动,再采纳。三者的派活文本都带上对应 `.github/skills/...` 路径。
- 每波合入前再过一次:本波新增数值是否都在第六·二节登记(未登记又非协议常量/安全边界/内部细节的按缺陷处理)。
- 验证只跑相关范围:改了 `Workbench/`、`Resources/` 或视图 → `Forge.App.Tests.Workbench`;改了 `.cs`/`.axaml` → 再加 `Localization` 与 `MinorRegressionTests`;只改文档/spec 不跑测试;`Forge.App` 开着时用 `-p:BaseOutputPath=artifacts/<name>/` 且留在仓库内。
- 新公共逻辑先 `find-similar`(Deslop),避免再造一份 diff 解析、裁剪或搜索逻辑。

### 六·二、可配置项登记(W0 一次性进 `AppPreferences`,各波只读)

| 项 | 所属波 | 默认 | 设置页分区 |
|---|---|---|---|
| 隐藏的模型集合 | W1 | 空 | models-visibility |
| 子 Agent 并发上限、子 Agent 开关 | W1 | 4(今 `AgentOptions.cs:19`)、开 | agents |
| 闲置自动归档:天数、是否启用、范围(仅自动化会话/全部根会话) | W1 | 7 天(今 `Housekeeping.cs:24`)、开、仅自动化 | idle-archive |
| 通知权限引导是否再提示 | W1 | 提示 | notification-permission |
| Palette:每来源结果上限、内容搜索排除目录 | W3 | 50、沿用 `.gitignore` | palette |
| 工具图片缩略图最大边、是否回传给模型 | W4 | 256px、是 | chat |
| CSV/TSV 预览最大行数、`.ipynb` 输出截断、图片预览大小上限 | W4 | 1000 行、20 KB、20 MB | files |
| Checks 轮询间隔、最长等待、「无 checks」确认次数(今 `PrChecksPollerOptions`)、失败日志尾行数 | W5 | 30 s、30 min、3 次、200 行 | git |
| MCP 默认请求超时(今 `McpModels.cs:80`;单 server 的 `timeout` 覆盖保留)、重连初始延迟(今 `McpServerState.cs:27`)、工具检索上限、stderr 尾显示行数 | W6 | 60 s、1 s、0(=不限,与现状一致)、200 行 | mcp |
| 资源检查器日志尾行数、列表刷新间隔 | W8 | 200 行、1 s | inspector |
| Mermaid 启用的图类型白名单、渲染超时 | W11 | 5 种(flowchart/sequence/state/class/er)、2 s | chat |
| 克隆超时、默认克隆目录 | W9 | 无超时、项目父目录 | projects |
| 更新检查频率、更新通道 | W10 | 每日、稳定 | updates |

规则:新增项**先登记再写代码**;值只在 `AppPreferences` 读取,工具/服务通过构造参数或 options 注入,不在热路径里反复读文件;改动热加载(沿用 `IReloadableSettingsSection`)。登记表之外的新增可配置项,在该波的实现说明里补一行并说明为什么不是内部细节。

## 七、明确不做

隐私统计/问卷/反馈、JetBrains Central;不引新包前提下的语法高亮替换、tree-sitter;PDF/HTML 预览在拍板前不做(占位页)。
