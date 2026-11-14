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
| 流程图 / Mermaid(2026-11-14 批准) | **有条件批准新增 `Mermaider` + `Svg.Skia`**,已写入 `architecture.md` NuGet 白名单(仅 `Forge.App` 渲染层) | W11 先上 ASCII 输出(`Mermaider` 的 `RenderAscii`),再接 SVG 栅格化。条件:`Svg.Skia` 的三平台 Native AOT 发布冒烟通过才引入,否则只保留 ASCII、不引 `Svg.Skia`。图类型白名单默认 flowchart/sequence/state/class/er,做成设置。失败回路不变(源码 + 错误行 + 回给模型修)。版本在 W11 落地时钉在引用处。 |
| 自动更新(2026-11-14 批准) | **自写,不引 Velopack,分两阶段** | 阶段 A:检查更新 → 对照版本 → 下载并校验 SHA-256 → 展示更新说明 → 用户手动安装;更新源 manifest URL、检查间隔、渠道(stable/preview)、是否自动检查(默认关)全部做成设置,零新依赖、零签名要求。阶段 B(单独立项):替换并重启(含 Windows 替换运行中文件的方案)、签名策略,与 W10 扩展包的包格式契约(zip + manifest + hash)合并设计。 |
| 聊天版式(2026-11-14 批准) | **铺满窗口** | 聊天区默认铺满窗口宽度,不做窄栏居中;不增设「最大行宽」选项。**窗口内边距一致**:聊天区(消息、代码块、表格、输入框、工具行)与窗口边缘的左右内边距使用同一个值,来自同一个资源/样式(不在各视图里各写一份数字),上下同理。W4b 已落地的聊天渲染不得再引入固定最大宽度;若有,按铺满修正;若某个块与其它块的内边距不一致,按统一值修正。 |
| 聊天时间轴版式(2026-11-14 批准) | **与 Claude VS Code 扩展一致的时间轴排版,轴线上的节点用图标代替圆点,图标取自已定的 Fluent System Icons(24px 系列,几何集中在 `Resources/Icons.axaml`,不新增图标包)** | 左侧一条贯穿的细竖线(轴),每个事件一个节点(图标)落在轴上,内容在节点右侧,铺满窗口且内边距一致;相邻事件被轴连起来,最后一个节点下面轴线截止;运行中的节点有轻量动画(可被「减少动画」关掉)。节点类型与图标(Regular=空心、Filled=实心,按状态切换):用户消息、助手文本、思考、读文件、搜索、编辑/写文件、命令、网络、MCP 工具、子 Agent、计划/待办、提示、错误、审批;每一类一个图标,同一类的成功/失败/运行中用颜色与图标状态区分。缺的图标几何补进 `Icons.axaml`(FIS 24px,按 12–15px 渲染可读为准)。节点图标大小、轴线粗细与颜色、节点间距走同一组样式资源,不在各视图写数字。 |
| PDF / HTML 预览(2026-11-13 修订) | PDF 按上文占位; **HTML 不设占位**:在编辑器里就是可编辑源码,工具条的打开按钮对 `.html/.htm` 显示「在浏览器中打开」并交给系统浏览器。理由:不渲染网页的决定不依赖藏起源码,占位卡只会让每次看/改页面多一次点击。 |

### 待定(由你在动手前最后定,先不进波次)

| 项 | 状态 | 定它需要什么 |
|---|---|---|
| 布局设置对话框、间距/字号层级微调、消息顺序 | 待定 | 用过一阵再定;聊天宽度口径已定(见上表),不在此列 |

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

**W0 拆成两步,各自单独提交、单独构建全绿**:**W0a**(W0-1 契约 + 流水线接线 + W0-2 进度生产侧,约 2 天)与 **W0b**(W0-3 搬运/接线/预建 + W0-4 小件,约 3 天)。W0b 只在 W0a 合入后开始。

**目标:把"多个波次会碰同一个文件"这件事在 W0 里消灭。** W0 交付后仓库照常编译、**行为不变**(除了消息复制这类无交叉的小件),之后每一波只在自己那一组文件里写。

### W0-1 契约定稿(`Forge.Abstractions`,已按此实施)

- `ToolResult`/`ToolResultBlock` 改为块列表,只允许 `TextBlock` 与 `ImageBlock`(其余类型构造时抛 `ArgumentException`);保留 `(string, IsError)` 便捷构造,`Content` 是拉平文本(单个文本块即原串零拷贝,多块按行拼接,图片用占位文案);相等按元素比较(否则两个相等的结果因列表引用不同而不等)。
- 占位文案与 `DecodedLength` 从 `MessageJson` 搬到 `Forge.Abstractions/ContentPlaceholders.cs`,Storage 与 Compaction 改引新位置,不留旧副本。
- **存储不升版**:消息表是 `content_json` 文本列,`tool_result` 恒写拉平后的 `content`(旧读者与全文检索只读它),仅当块不是"恰好一个文本块"时多写一个 `blocks` 数组。旧行照常读,旧版本读新行也只丢图。原计划的"工具结果多块需要升 schema"不成立;计划修订表只有 W7 一家要,**W7 独占 schema 升版**,不构成交叉。
- W0 触及的调用点:`AgentLoop`(带块转发;溢出归档只处理文本、图片原样保留)、`MessageJson`(读写 `blocks`)、`TokenCounter`(工具结果里每张图按附件图同一口径计)、`TranscriptReplay`(回放带块)。**`McpToolAdapter`/`McpContentFlattener`/`Core/Providers/*` 不在 W0**,归 W4(它们仍读拉平后的 `Content`,行为不变)。

### W0-2 进度生产侧(已按此实施)

- `ToolContext.Progress`(`Action<string>?`):每次调用传**最新输出尾部的快照**(不是增量,监听者可丢事件而不丢状态);事件 `AgentToolProgress(AgentId, Call, Tail)`,夹在 `AgentToolStarted`/`Finished` 之间,不落库。
- 生产者节流在 `Forge.IO`:`ProcessRequest.OnOutput/OutputInterval(默认 250 ms)/OutputTailChars(默认 16 KiB)`,`ProcessOutputTail` 合并 stdout/stderr、每路一个 UTF-8 `Decoder`、间隔内到达的块武装一次性定时器保证"静默前最后几行"也能报出;`OnOutput` 为空时零开销;监听者抛异常不影响 pump。
- `BashTool` 把 `context.Progress` 接到 `OnOutput`;`AgentLoop` 为每个调用派生独立 context(并行调用互不串)。`SessionWorkspace` 暂时显式忽略该事件,**W8 接手消费**。
- 注:16 KiB 是生产者的上限,W8 的"日志尾行数"设置只能在此之内取。

### W0-3 UI 接线与占位(交叉文件的唯一写入者)

| 文件 | W0 做什么 | 之后归属 |
|---|---|---|
| `App.axaml` | **已实施**:为 Palette/Checks/Plan/ResourceInspector/Onboarding 五个占位 VM 各注册 `DataTemplate`(指向 `Views/*View` 占位);`ToolRowViewModel`、`TurnTextViewModel` 的模板改成指向 `Views/ToolRowView`、`Views/TurnTextView`(两个 UserControl),此后波次只改自己的子视图,不再动 `App.axaml`。MCP 分区沿用旧模板;消息复制按钮已接上(`CopyTextButton`) | 冻结(各波不再改) |
| `MainWindow.axaml(.cs)` | **已实施**(用户已定:全部做成窗口内覆盖层):五个 `ContentControl` 覆盖层绑 `MainWindowViewModel.{Palette,Checks,Plan,Inspector,Onboarding}`(`object?`,null 不显示);标题栏搜索图标换成按钮 `OpenPaletteCommand`(W3 之前禁用)。Checks/Plan/Inspector 因此是浮层,不是可停靠面板 | 冻结 |
| `ViewModels/MainWindowViewModel.cs` | **已实施**:五个 partial 已建,各带 `object?` 槽属性(私有 setter,由各波的 partial 赋值);主文件无需再接线。`DescribePullRequest` 已改 `DescribePullRequestAsync`(三次读库不再同步阻塞 UI 线程) | 主文件**冻结**,partial 各归对应波 |
| `ViewModels/SessionWorkspace.cs`(2360 行) | **按职责拆 partial**:`SessionWorkspace.cs`(壳/生命周期)+ `SessionWorkspace.Tools.cs`(工具事件与行更新)+ `SessionWorkspace.Activity.cs`(运行态/取消)。纯搬运,不改行为 **【实施时不做,偏差】** 只有 W8 改这个文件(W8 的独占路径是 `SessionWorkspace*.cs`),没有跨波重叠,拆分是纯搬运风险而无收益;W8 若觉得 2360 行难改,自行拆。 | `.Tools.cs`+主文件归 W8;`.Activity.cs` 归 W8 |
| `ViewModels/Transcript.cs` | 拆三份:`Transcript.cs`(消息/文本/代码块)、`Transcript.Tools.cs`(工具行 VM 的运行态/日志尾)、`Transcript.ToolImages.cs`(`ToolRowViewModel` 的 partial,只放结果图块与缩略图) **已实施**:`ToolRowViewModel` 整体移入 `Transcript.Tools.cs`(partial),`Transcript.ToolImages.cs` 为空 partial;`Complete(ToolResult)` 调用 `partial void CaptureImages(blocks)`,W4 在 ToolImages 里实现即可,不碰 Tools。 | 依次归 W4b、W8、W4 |
| `Localization/Loc.cs` + `KeyRegistryTests` | **更正(实施时核实)**:每张 Strings 表用 `[ModuleInitializer]` 自注册,`Loc.EnsureRegistered()` 只触碰核心表,`KeyRegistryTests` 反射扫的是各表自己的常量——**没有共享注册点**,新表不碰 `Loc.cs`。W0 因此不建空表;各波新建自己的 `Strings.<X>.cs` 即可。共享键 `turn.copyMessage` 已进 `Strings.Session.cs` | 无冻结需求 |
| 通知权限引导接口 | W0 在 `Notifications/` 声明 `INotificationPermissionGuide`(状态查询 + 触发引导),W1 实现,W9 首启向导只调接口 **已实施**:`Notifications/INotificationPermissionGuide.cs`(`GetStateAsync`、`OpenSettingsAsync`、`NotificationPermissionState`)。 | 接口冻结,实现归 W1;`AppComposition.NotificationPermissionGuide` 槽位已建,W1 赋值、W9 读取 |
| `ViewModels/Panels.cs`(2518 行) | 拆出 `FileDocumentViewModel` 到独立文件 `ViewModels/FileDocumentViewModel.cs`(`.Editing/.Lsp/.Review` 三片已有) **已实施**:`FileDocumentViewModel` 已移到 `ViewModels/FileDocumentViewModel.cs`。 | 新文件 + `.Editing` 归 W4,`.Lsp` 归 W2,`.Review` 冻结 |
| `Composition/AppComposition.cs` | 抽出 `Composition/<Feature>Composition.cs` 模块(照已有 `Automations/AutomationComposition.cs` 的样子),`Create()` 里一次性调用 **已实施(形态调整)**:`Create()` 末尾一次调用 `FeatureComposition.RegisterAll`,下挂 `RuntimeSettings`(W1)/`Palette`(W3)/`ViewerSettings`(W4)/`Inspector`(W8)/`Projects`(W9) 五个空 `*Composition.Register(composition, settings)` 模块,各波只改自己的文件;`FeatureComposition.cs` 冻结。W6 的 MCP 分区原本就在 `Create()` 里且只读 `composition`,不用搬。 | 各模块归对应波 |
| `Preferences/SettingsSection.cs` | 一次性分配全部新分区 id 与 order(models-visibility、idle-archive、notification-permission、mcp 扩写沿用旧 id) **已实施**:`SettingsSectionIds` 里新增 10 个 id 与 order。 | 冻结 |
| `Preferences/AppPreferences.cs` + `PreferencesJsonContext` | 预加**第六·二节登记的全部属性**(默认值 + 范围校验 + 迁移默认),各波只读取、不再增删字段 **已实施**:六·二登记的全部项已进 `AppPreferences`/`PreferencesSection`(常量 Min/Max/Default、钳制或令牌校验、旧文件缺键回默认),测试由独立测试 Agent 编写。 | 冻结 |
| `Forge.Abstractions` 的进度回调 | 补 `AgentToolProgress` 的**生产侧**接口(工具执行上下文里的进度回调),并给 bash/终端类工具接上产出;否则 W8 只能显示开始/结束 | 冻结 |
| `ViewModels/Settings/InstrumentSettingsSections.cs`(注意路径) | 该文件含 Approvals/Skills/Mcp 三个分区类;W0 先把 Mcp 分区拆到 `McpSettingsSectionViewModel.cs`,W6 只改那个文件 **已实施**。 | 拆分后 Mcp 归 W6 |
| `App.axaml` 的 `ToolRowViewModel` 模板(`:673`) | **已实施,与原方案不同**:抽成 `Views/ToolRowView.axaml`(UserControl,冻结),内含两个子视图占位 `Views/ToolRowImagesView`(W4)与 `Views/ToolRowTailView`(W8),各波只改自己的子视图,无共享文件。`Resources/ToolRow.axaml` 不再存在(模板放进资源字典不会被 DataType 自动匹配,UserControl 更直接) | 归 W4,W8 只在 `Transcript.Tools.cs` 加数据 |
| `GitCli.Clone.cs` | 预建空 partial **已实施**(空 partial)。 | 归 W9(W5 不碰) |
| `Resources/Icons.axaml` | **未预加**:SPEC 规定图标只能把 Fluent System Icons 的 `<path d>` 抄进此文件,仓库里没有 FIS 源,凭空写路径就是猜测。已有 `IconCopy/IconCheck/IconSearch/IconRefresh/IconStop/IconBell/IconEye/IconFolder/IconProjectNew/IconBook` 可复用;确实缺的由对应波按 SPEC 规则**追加**(只追加、各加各的键,合并无冲突) | 追加式 |
| `Localization/` | 每波一张新表(palette/checks/plan/inspector/mcp/onboarding)+ 空注册;共享表(`Strings.Session`/`Inspector`/`Core`)本轮新增键也在 W0 加完 | 各波只改自己的表 |
| `src/Forge.Editor/` | 确认 W2(语义 token 快照)、W3(文档符号)所需 API 已暴露;缺的补上 **已核实,无需补**:`LanguageSession.RequestSemanticTokensAsync`、`RequestDocumentSymbolsAsync`、`RequestReferencesAsync` 与 `SemanticTokenMapper`/`HighlightMerger` 均已公开;W2 在此之上做可见行缓存。 | 归 W2 |

### W0-4 顺手做掉、无交叉的小件

- 消息复制(整条消息 + 复制纯文本),复用 diff/终端已有的剪贴板写法。
- 修 `DescribePullRequest()` 在 UI 线程同步打三次 sqlite(`MainWindowViewModel.cs:2421`)。
- 更新 `Forge.App/SPEC.md` 未决节的过时 LSP 描述;在原调研文档顶部加一行指向本文件。

**W0 完成判据**:`dotnet build` 全绿 + `Forge.App.Tests` 全绿(纯搬运/接线,不得有测试行为变化);`git diff` 逐文件复核只含契约/搬运/接线;交叉点矩阵(下表)里"冻结"的文件之后无人再动。

## 四、波次与文件归属矩阵

W1–W9(含 W4b、W12)之间**文件级零重叠**,可以同时派给多个 Worker;每波各自跑相关测试。W10 串行收尾。

| 波 | 内容 | 独占路径(其他人不许改) | 规模 |
|---|---|---|---|
| **W1** 设置与运行参数 | 模型可见性过滤(14a)、子 Agent 并发上限与开关(14b)、闲置归档天数与范围可配(14e)、通知权限引导(14f) | `Preferences/AppPreferences.cs` 的消费点、`Agents/**`(SessionRuntime、注入 `AgentOptions`)、`Core/Agents/AgentOptions.cs`、`Automations/Housekeeping.cs`、`Notifications/**`、`Settings/ModelsVisibility*`、`ViewModels/ComposerViewModel.cs`、`INotificationPermissionGuide` 的实现(赋给 `AppComposition.NotificationPermissionGuide`,由 W9 读取);**实际执行时的越界(已记录)**:`Core/Agents/AgentSlots.cs`(池支持 `SetLimit`)、`Core/Agents/AgentLoop.cs` + `AgentHost.cs`(上限透传,二者当时已随 W0 合入、W0 之后无人再动)、`Core/AssemblyInfo.cs`(测试可见性)、`Settings/AgentRuntimeSectionsViewModel.cs`、`Automations/IdleArchiveSettings.cs`、`Composition/RuntimeSettingsComposition.cs`、`Views/*SettingsSectionView.axaml`、`Icons.axaml`;以及 `AppComposition.Create()` 里的三行运行时接线(`ProvidersSettingsViewModel` 的偏好参数、`runtimeServices.SubAgents`、`AutomationComposition.Create(idleArchive:)`)与 `MainWindowViewModel` 的一行偏好透传——`FeatureComposition.RegisterAll` 只交出已建好的 `AppComposition`,拿不到 `AgentRuntimeServices` 与 `AutomationService` 的构造实参,所以分区注册与视图走了模块,运行期接线只能留在 `Create()` | 2 天 |
| **W2** 编辑器 | 语义 token 叠加(可见行 + 版本号缓存,delta 只失效受影响行)、查找引用列表、问题面板 | `src/Forge.Editor/**`、`Editing/**`、`ViewModels/FileDocumentViewModel.Lsp.cs`、`Views/FileDocumentView.Lsp.cs`、`Views/ProblemsPanelView*` | 3 天 |

> **W2 已交付,且它破了下面的零重叠约束(已记录,勿再重复踩)。** `src/Forge.Editor/**`:新增 `Highlighting/TokenCategoryGroup.cs`、`Highlighting/SemanticLineIndex.cs`、`Lsp/SemanticTokenMap.cs`、`Lsp/WorkspaceDiagnostics.cs`(含 `IWorkspaceDiagnosticSource`);`Lsp/LspCapabilities.cs` 的 `ApplyEdits` 重写为两趟单次分配;`Lsp/LanguageSession.cs` 加 `SemanticTokensRefreshRequested` 与 `PositionEncoding`;`Languages/LanguageSessionPool.cs` 加 `SessionStarted`/`SessionClosed`(在启动门释放之后触发,订阅者可在处理函数里再开另一个会话)。App 侧:新增 `Editing/SemanticTokenColorizer.cs`、`ViewModels/ProblemsPanelViewModel.cs`、`ViewModels/ReferenceListViewModel.cs`、`ViewModels/MainWindowViewModel.Problems.cs`、`Views/ProblemsPanelView*`、`Views/ReferenceListView*`;`Workbench/Controls/SyntaxTokenTextBlock.cs` 改为使用新的 `TokenCategoryGroups` 表(原先那套分类重复了两份)。**破了 W0 独占的文件**:`ViewModels/MainWindowViewModel.cs`(多一个 `Problems` 属性、一行构造参数、`OpenFileAsync` 新增 `hasWorkspaceOverride` 参数并改了一行工作区取值、`Dispose` 多两行释放)、`Workbench/WorkbenchLayout.cs`、`Workbench/WorkbenchTypes.cs`、`App.axaml`、`Localization/Strings.Shell.cs` —— 因为本分支上 W0 的 host/模板未落地,而问题面板必须有宿主。`WorkbenchLayout` 的新参数是**可空追加参数**(`object? problems = null`),W5/W7/W8 再加面板时请把它扩成一组「额外 inspector 标签」列表,而不是再加一个参数。其余归各波的路径未动。**已与 W0 合并**(合并提交 `c20834e`):实际只有 4 个文件重叠、仅 `Forge.App/SPEC.md` 一处文本冲突,细节与 W0 落地后需更正的判断见 `2026-11-13-w2-after-w0-merge.md`。
| **W3** 全局搜索 / 命令面板 | 面板 + 五个来源(文件/内容/会话/命令/符号)+ 预览 + `Ctrl+P`/`Ctrl+Shift+P`/`Ctrl+Shift+F` | `ViewModels/PaletteViewModel*.cs`、`Views/PaletteView*`、`Services/Palette*.cs`、`ViewModels/MainWindowViewModel.Palette.cs`、`Localization/Strings.Palette.cs` | 4–5 天 |
| **W4** 工具图片 + 富查看器 | 工具行缩略图、MCP 结构化透传、provider 降级(tool 角色不能带图 → 随后的 user 消息);图片/CSV/IPYNB/LFS/二进制占位查看器;PDF 占位 + 系统默认程序打开,HTML 可编辑源码 + 在浏览器中打开 | `Forge.Mcp/McpToolAdapter.cs`+`McpContentFlattener.cs`、`Core/Providers/*`、`ViewModels/Transcript.ToolImages.cs`、`ViewModels/FileDocumentViewModel.cs`+`.Editing.cs`+`Views/FileDocumentView.axaml(.cs)`(**不含** `.Lsp.cs`/`.Review.cs`;W2 若需改 axaml,在 W0 预留 overlay 插槽)、`Views/ToolRowImagesView*`、`Composition/ViewerSettingsComposition.cs`、`Views/Preview/**` | 4 天 |
| **W4b** 聊天 Markdown 渲染 | 聊天消息走 Markdown(标题/列表/表格/链接/fenced code,代码块带复制与语言标签);复用 `MarkdownRenderer`,流式增量不整段重排 | `Changes/Markdown/**`、`ViewModels/Transcript.cs`、`Views/ChatMarkdown*`、`Views/TurnTextView.axaml(.cs)`(W0 从 `App.axaml` 抽出,含复制按钮)、`Localization/Strings.ChatMarkdown.cs` | 4 天 |
| **W5** PR checks 与收尾 | checks 面板(逐项状态/耗时/链接、等待、失败日志尾、让 agent 修)、`gh pr ready`、ship 补「合并后收尾」 | `Forge.Workspace/Git/IPrPlatform.cs`+`GitHubPlatform.cs`+`PrCheck*.cs`+`PrChecksPoller.cs`(**不含** `GitCli.Clone.cs`)、`Views/ChecksPanelView*`、`ViewModels/MainWindowViewModel.Checks.cs` | 3 天 |
| **W6** MCP 管理面 | 配置写入(原子写 + 同款互斥)、增删改/启停/测试/重连/工具清单/错误与 stderr 尾、从 Claude/Cursor 导入、预设;审批卡补 command/args/cwd/env/headers 键名与即时撤销 | `Forge.Mcp/McpConfig*.cs`、`McpServerManager.cs`、`McpServerState.cs`、`ViewModels/Settings/McpSettingsSectionViewModel.cs`(W0 已从 `InstrumentSettingsSections.cs` 拆出)、`Views/McpSettingsView*`、`Approval/**` | 4–5 天 |
| **W7** Plan 会话 | 摘要面板 + 步骤与验证状态、修订历史、下一步、导出 Markdown、从计划开 PR | `Core/Storage/SqliteTodoStore.cs`(修订读写)、`Core/Storage/StorageSchema.cs`(v10→v11,W7 独占)、`ViewModels/PlanPanelViewModel*`、`Views/PlanPanelView*`、`ViewModels/MainWindowViewModel.Plan.cs` | 4–6 天 |
| **W8** 资源检查器 | 运行中工具/命令与子 Agent 统一列表(耗时、日志尾、停止)、消费 `AgentToolProgress` | `ViewModels/SessionWorkspace*.cs`、`ViewModels/Transcript.Tools.cs`、`ViewModels/SessionInstrumentsViewModel.cs`、`Views/ResourceInspectorView*`、`ViewModels/MainWindowViewModel.Inspector.cs` | 3 天 |
> **W8 已交付。** 实现说明见 `2026-11-13-w8-resource-inspector.md`。入口按用户拍板做在**输入条「资源 ▾」菜单的第五行**（不是标题栏按钮），范围起初是**活动会话**（换会话即关），W8 follow-up 按用户要求改为**所有打开会话、按会话分组**（活动会话排前，换会话只重排），工具行的停止改为只停该调用（Core 新增单调用取消，见实现说明第七节）。新增：`ViewModels/SessionWorkspace.Tools.cs`、`ViewModels/ResourceInspectorViewModel.cs`（占位替换）、`Settings/ResourceInspectorSettingsViewModel.cs`、`Views/ResourceInspectorSettingsSectionView*`、`Localization/Strings.Resources.cs`、`Services/TextTail.cs`；改动：`ViewModels/SessionWorkspace.cs`（partial + 进度消费接线）、`ViewModels/Transcript.Tools.cs`（`LiveToolViewModel`）、`ViewModels/SessionInstrumentsViewModel.cs`、`ViewModels/MainWindowViewModel.Inspector.cs`、`Views/ResourceInspectorView*`、`Views/ToolRowTailView.axaml`、`Views/ResourceInspectorSettingsSectionView*`、`Composition/InspectorComposition.cs`、`Forge.App/SPEC.md`。**破格改动的共享文件（已记录，勿重复）：** `Views/SessionView.axaml`（一行菜单项，入口必须挂在「资源」按钮上）、`ViewModels/Transcript.cs`（`AddTool` 返回刚建的行，2 行）、`Automations/CiChain.cs`（`Tail` 委托给 `TextTail.Keep`，与 W5 分支同一处改动）、`ViewModels/MainWindowViewModel.cs`（`InitializeInspector()`、会话构造多一个 `openInspector:` 实参、`Dispose` 里一行 `CloseInspector()`）、`Views/SubAgentView.axaml`（停止按钮改绑 `CanStop`,排队中的子 Agent 不再显示一枚点不动的按钮）、测试侧 `StringTableTests`（前缀表加 `resources`）与 `SettingsPanelTests`（`KnownSectionIds` 补齐漏掉的 `SettingsSectionIds.Inspector`）各一行。**跨波提示：** `Services/TextTail.cs` 与 W5 分支为同一文件同一内容（add/add 同文本，合并取任一）。

| **W9** 首启与项目创建 | 首启向导(语言/提供商/通知权限,权限一步只调 `INotificationPermissionGuide`,不碰 `Notifications/**`)、克隆仓库(带进度与取消)、新建项目目录 + `git init` | `Views/Onboarding*`、`ViewModels/Onboarding*`、`ViewModels/MainWindowViewModel.Onboarding.cs`、`Forge.Workspace/Git/GitCli.Clone.cs`(W0 预建)、`Services/ProjectCreation*` | 3 天 |

> **W9 已交付,并在三处碰了清单外的文件(已记录,勿重复踩)。**
> **交付面。** ①首启向导 `ViewModels/OnboardingViewModel.cs` + `Views/OnboardingView.axaml`:语言(写 `AppPreferences.Language`,当场切换、跳过也生效)、提供商(只显示 `ProviderServices.Catalog` 的状态并深链到 `provider` 分区,**不重写** `ProviderEditorWindow` 的校验/测试/保存链)、通知权限(只经冻结的 `INotificationPermissionGuide` 读状态/开设置页,`Notifications/**` 一行未改)。是否显示由 `Services/OnboardingState.cs` 的一条 `ui_state`(`onboarding`,版本 1)决定;判定是“该行不存在**且**没有仓库项目”——不能只数行数,因为每个安装启动时都会注册常规工作区(`ProjectKind.General`),按行数会让全新安装永远看不到向导(测试会话发现并钉住了这个真实缺陷)。触发点在窗口且由 `App` 置 `MainWindowViewModel.ShowFirstRunWizardOnStartup = true`,所以自带窗口的 headless 宿主不会突然被浮层挡住。②添加项目 `ViewModels/MainWindowViewModel.Projects.cs`:`PickAsync` 选 已有文件夹 / 克隆仓库 / 新建项目,三条分支共用“注册 + 刷新项目缓存 + 刷新两棵树”收尾,注册抛错发一条错误通知而不是逃进调度器。③`Services/ProjectCreationService.cs`(名字规则、URL 建议名、目标目录、`git init`、克隆)+ `ViewModels/ProjectCreationViewModel.cs` + `Views/ProjectCreationDialog.axaml`(进度、停止、失败留在窗口里);名字规则的**磁盘存在性检查**在 200 ms 防抖后跑在线程池上(`ValidateNameShape` 是零 IO 的那一半),UI 线程不碰文件。④`projects` 设置分区(`Settings/ProjectsSettingsSectionViewModel.cs` + `Views/ProjectsSettingsSectionView.axaml`,克隆超时与默认目录,读的是 §6.2 登记的两项)。⑤`Forge.Workspace/Git/GitCli.Clone.cs`:+ 共用的 `GitRunOptions.CarriageReturnEndsLine`。
> **破了清单的文件(3 个产品文件 + 2 个 spec)。** `src/Forge.Workspace/Git/GitExecution.cs`(`GitRunOptions` 的 record 不能拆 partial,新选项只能加在那里)与 `src/Forge.Workspace/Git/GitCli.cs`(`PumpAsync`/`Consume` 多一个 `carriageReturnEndsLine` 参数,才能把 `git clone --progress` 的 `\r` 原地重绘变成逐条更新;开关默认关,关着时逐字节不变,`StreamLinesAsync`/`StreamRecordsAsync` 不受影响)——这两个文件没有任何波次占用,而“带进度”离开它就是假的。`App.axaml.cs` 加一行 `ShowFirstRunWizardOnStartup = true`;`MainWindow.axaml.cs` 加 `ShellDialogs.RunProjectCreationAsync`(对话框宿主)与 `InitializeAsync` 里对向导的那次询问;`ViewModels/MainWindowViewModel.cs` 只把 `AddProjectAsync` 的方法体换成一行转到 `AddProjectFlowAsync`,并在 `IDialogService` 上加一个默认返回 null 的 `RunProjectCreationAsync`(五个测试双因此不用改)。**未做**:向导不内嵌提供商编辑器(会复制一条写入口)、不做浅克隆/子模块/`--filter`。
> **测试**:`tests/Forge.Workspace.Tests/Git/GitCliCloneTests.cs`(33:进度解析表、真仓库克隆、非空/空目标、`-` 开头与相对目标被拒、失败与取消的清理、坏回调、用一个假可执行文件确定性地钉住 `\r` 与跨 chunk CRLF),`tests/Forge.App.Tests` 的 146 个 W9 用例(`Services/ProjectCreationServiceTests`、`Services/OnboardingStateTests`、`ViewModels/{ProjectCreation,Onboarding}ViewModelTests`、`Settings/ProjectsSettingsSectionTests`、`Views/{ProjectsSettingsSectionView,ProjectCreationDialog}Tests`、`Foundation/OnboardingViewTests`、`Workbench/{OnboardingShell,AddProjectFlow}Tests`)。顺带两处既有测试的必要更正:`StringTableTests.KnownPrefixes` 加 `onboarding`/`projectCreate`/`projectFlow`,`Foundation/SettingsPanelTests.KnownSectionIds` 补 `projects`(W0 分配了 id 却没登记进那张表),`FileIndexTests` 的 `Services.FileIndex` 改成全名(新的 `Forge.App.Tests.Services` 命名空间把它遮蔽了)。
> **评审发现并已修的(均在本波范围内,故未遗留)。** ①`ProjectCreationDialog` 在成功那一次关不掉自己(`Completed` 在 `isRunning` 落回 false 之前发,而 `OnClosing` 只看 `IsRunning`),后果是 `ShowDialog` 永不完成、建好的仓库从不被登记——改成“运行状态落定后才发完成” + `OnClosing` 只否决用户主动关窗(`!e.IsProgrammatic`),并补了真空窗用例(把两半都拆掉时它 60 s 超时失败,证明确实能抓到这个缺陷)。②“克隆超时 0 = 不限”被实现成传 `null` 给 `GitCloneOptions.Timeout`,而 null 的含义是“用 git 层默认的 2 分钟”,默认配置下大仓库会在用户被告知“不限”的情况下被杀——改成 `Timeout.InfiniteTimeSpan`,用例同步改为断言它。③超时失败的对话框只显示 stderr(本波之后全是进度重绘行),丢掉唯一说清原因的一句——超时(`ExitCode < 0`)改用 `GitException.Message`。④`ShowOnboarding` 替换模型前不 `Dispose`、窗口销毁时也不向导的旧订阅——两处补上。
> **合并 main 后的第二轮评审(安全与清理)又修了六条。** ⑤克隆地址没有 scheme 白名单:`ext::sh -c …` 这类 `transport::address` 能过 `IsUsableUrl`,只靠用户的 git 配置挡——现在应用层只收 https/http/ssh/git+ssh/ssh+git/git/file、scp 形式与本地路径,git 层再给克隆进程设 `GIT_ALLOW_PROTOCOL`(压过任何 `protocol.*.allow`)。⑥凭据泄露:`GitException` 的消息与 `Arguments` 里是原样 URL,超时一路又直接把消息显示在对话框里——现在消息/参数/stderr 一律经 `GitCli.RedactCredentials` 脱敏;带密码的地址(会进 git 的 argv)在对话框里单独提示并拒绝,只带用户名的照常放行。⑦调用方准备的空目标目录在克隆或 `git init` 失败/取消后留着 git 写的半截 `.git`,重试被判“已存在”——现在保留目录但清空(`GitCli.DeleteTreeQuietlyAsync(keepRoot)`,`ProjectCreationService` 原先复制的一份删除逻辑也改为调用它)。⑧相对父目录会解析到进程工作目录——改为提示并拒绝,目标路径栏不再显示那个位置。⑨`OnboardingState` 在 UI 线程直接读写 `ui_state`,改走 `BackgroundRead`(与 `LayoutStateLoader` 一致)。⑩`ProjectCreationViewModel` 每次运行的 `CancellationTokenSource` 不释放——运行结束即释放。**未做**:向导没有“再次运行”入口(删掉 `ui_state` 的 `onboarding` 行即可重新触发;入口要落在调色板/菜单,不归本波);只带用户名的令牌式地址(`https://<token>@github.com/…`)仍会进 argv,消息里已脱敏。
| **W12** 评审包 + `request_review` | 给 Reviewer 补 `git_diff` 与变更集包;`request_review` 工具(主 Agent 发起评审、结果回到会话);评审是产品核心目标,放在并行批里 | `Forge.Core/Prompts/BuiltInAgents.cs`、新建 `Forge.Core/Tools/RequestReview*.cs` + `ReviewPackage*.cs`、`Localization/Strings.Review.cs` | 4 天 |
| **W11** 流程图 / Mermaid(**已批准(有条件,见「已定」),串在 W4b 之后**) | 聊天与 diff 预览里的 ```mermaid 代码块本地渲染、图类型白名单(可配置)、失败回路(源码 + 错误行 + 回给模型修)、主题跟随 | `Changes/Markdown/**`(W4b 先落地代码块渲染)、新建 `Views/Mermaid*` + `Services/MermaidRender*` | 3 天 |
| **W13** 聊天时间轴版式(**串在 W4b 之后;与 W11 可并行,W13 不改 `Changes/Markdown/**`**) | 把聊天记录排成时间轴:轴线 + 图标节点 + 右侧内容,铺满窗口、窗口内边距一致;图标映射表与节点状态见「已定」;键盘/读屏:节点图标不抢焦点、有可读的名称;随主题与语言切换;长会话仍走虚拟化,不因轴线/节点增加每条消息的分配 | `Views/Timeline*`(新建:轴线、节点、行容器)、`ViewModels/TimelineNodeViewModel*.cs`(节点类型与图标键的映射,从现有 `TranscriptItemViewModel` 派生,不改其业务数据)、`Views/SessionView.axaml` 的转录区、`App.axaml` 里转录条目的模板、`Resources/Icons.axaml`(只增不改)、`Localization/Strings.Timeline.cs`。**越界声明**:W13 期间独占上述 `App.axaml`/`SessionView.axaml` 的转录部分;`TurnTextView*`/`ToolRow*View*` 只改最外层容器与内边距,不改其内部渲染(归 W4b/W4/W8) | 4 天 |

> **W13 已交付。** 实现说明与判据见 `2026-11-14-w13-implementation.md`。新增 `ViewModels/TimelineNodeViewModel.cs`(节点类型、工具名映射、图标键、读屏名称键)、`ViewModels/TimelineLayout.cs`(轴线几何纯函数)、`Views/TimelineGutter.cs`(轴列控件)、`Views/TimelineStyles.axaml`(控件主题 + 状态样式)、`Views/TimelineTurnView.axaml(.cs)`(轮次的抬头/块/脚注三段行)、`Localization/Strings.Timeline.cs`。修改:`App.axaml`(6 个转录条目模板改为「轴列 + 原内容」,新增一条 `StyleInclude`,`Category`/模板内容没改)、`Views/SessionView.axaml`(转录区/输入卡/横幅的内边距改取 token)、`Resources/Tokens.axaml`(4 个时间轴尺寸 + 3 个聊天内边距)、`Resources/Controls.axaml`(用户气泡去掉 `MaxWidth=640`;删除只属于旧轮次卡的 `turn-header`/`turn-body` 样式,`turn-card` 保留给审批浮层与 MCP 设置)、`Resources/Icons.axaml`(27 个 FIS 键,只追加)、`THIRD_PARTY_NOTICES.md`(新增图标的清单)。
> **破了清单的文件(均为不可避免的接线,已记录).** `ViewModels/Transcript.cs`(提示行读不写 `Kind` 提示位,审批被拒才不是 `Notice`)、`ViewModels/Transcript.Tools.cs`(工具行只读 `IsRunning`)、`ViewModels/SessionWorkspace.cs`(被拒绝的审批传 `Kind=Approval`,一行)、`ViewModels/TreeIcons.cs`(`TreeMotion.ReducedMotion` 加 `Changed` 事件)、`Preferences/AppPreferences.cs`(登记外的**新可配置项** `ReduceMotion`,默认关:关闭动画是用户合理会调的开关,不做成硬编码,已在六·二补登记)、`Appearance/AppearanceApplier.cs`(写闸门)、`Appearance/AppearanceSettingsView(.axaml/.cs)` 与 `AppearanceSettingsViewModel`(动效勾选框)、`ViewModels/ProjectTree.cs`(闸门变化时给运行中的树行抬一次通知,否则用户勾选后树里的呼吸要等到下一次窗口属性变化才停)、`Localization/Strings.Appearance.cs`(三条文案)、`Forge.App/SPEC.md`。测试侧另有两处必要更正:`StringTableTests.KnownPrefixes` 加 `timeline`、`AppearanceStringsTests` 的键数 9→12。
> **未做(与实现说明第六节一致).** 思考节点只有映射与图标,会话仍丢弃 `AgentThinkingDelta`;待审审批卡不在转录里(窗口级浮层);MCP 工具按 `server__tool` 分隔符识别(行上没有来源)。子 Agent 视图自动复用同一套模板(未改其文件)。
> **测试与评审.** 两个独立测试会话共 10 个文件(180 例纯逻辑/状态 + 19 个 headless 视图方法),另修正两处既有测试(`StringTableTests` 前缀、`AppearanceStringsTests` 键数);视图会话拉出并修复一个真缺陷:`TimelineGutter` 在虚拟化面板滚动回收容器后不再重算,轴线会丢到下一次追加/展开——改为未定位时经 `Dispatcher` 最多 3 次后台重试,并留回归用例。独立评审会话(glm-5.3 high)的 1 blocking + 2 minor 全部已修(`TreeMotion.Changed` 订阅、块行双订阅、前缀映射),详见 `2026-11-14-w13-implementation.md` 第七节。

| **W14** CodeMode 工具面扩展(与 W13/W11 并行,不碰其文件) | `run_code`(Roslyn、并行、只回一次结果)已存在,但脚本目前只看得到扩展宿主的插件工具,`read/ls/find/grep/web_fetch` 与 MCP 工具都进不去——并行扇出最有用的恰恰是这些。本波:把**只读且免审批**的内置工具(`read/ls/find/grep`)与 `web_fetch`、以及已启用的 MCP 只读工具暴露给脚本;写入/执行类仍排除(脚本内调用不经逐次审批卡);脚本并发上限、单次结果字符上限、可见工具名单全部进设置;工具声明改为脚本内按名查询(`tools.list/describe`),主提示只保留 `run_code` 一条描述,验证声明 token 的实际下降并记入实现说明 | `Forge.HostClient/RunCode*`、`Forge.ScriptRuntime/ScriptToolsFacade.cs`、`Forge.App/Agents/SessionRuntime.cs` 的 `CollectExternalTools` 一处、`Localization/Strings.CodeMode.cs` | 3 天 |
| **W15** 按提供商限速桶(借鉴 TokenRateLimiter 的 Bucket.cs,开源可用) | 不可变状态 + CAS 无锁、惰性补充、注入 `TimeProvider`;结果区分 授权/需等待(带 retryTime)/超出容量/永不补充;按提供商(请求/分钟、令牌/分钟)在发请求前获取,用 `x-ratelimit-*` 响应头校准;`RetryingChatProvider` 改用 `TimeProvider`;`QuotaResume` 的 `Gap` 并入同一限速组件。容量/速率进设置,默认宽松。**基准用现有 `tools/Forge.Bench` 加 `RateLimitScenarios.cs`(给它加对 `Forge.Core` 的引用),先于实现落基线**:热路径分配须为 0,无争用时 <100ns,多线程争用下的吞吐与公平性也要有行 | `Forge.Core/Providers/RateLimit*`(新建)、`RetryingChatProvider.cs`、`Automations/QuotaResume.cs`、`Strings.RateLimit.cs`、设置分区 | 3 天 |

**W4b 已实施**:聊天助手回复按 Markdown 增量渲染(标题/段落/列表/引用/表格/链接/围栏代码块),代码块带语言标签与复制按钮;新增纯状态机 `Changes/Markdown/StreamingMarkdown.cs` 与 `Views/ChatMarkdownView*`,删除 `TextSegmentViewModel`。方案、被否备选、四条判据、与上表独占路径的三处偏差(`Resources/MarkdownStyles.axaml` 的追加式两条样式、不建 `Strings.ChatMarkdown.cs`、用户消息仍按纯文本)见 `2026-11-13-w4b-implementation.md`。

**W6 已实施**:实现记录、被否备选与验收见 `2026-11-13-w6-implementation.md`。计划外并入一项(已与用户确认):MCP 工具此前从未进入会话(`McpServerManager.Tools` 零消费),本波一并接上,`工具检索上限` 因此才有意义。

**跨波语义依赖(全部在 W0 满足,不构成串行边)**:W4/W8 依赖 W0 的契约、进度生产侧与流水线接线;W7 独占自己的 schema 升版(与其他波无交叉);W2/W3 依赖 W0 的 `Forge.Editor` API 确认;W5/W6/W9 依赖 W0 的 host/模板/模块骨架;W9 依赖 W0 的 `INotificationPermissionGuide` 接口;全部新文案表依赖 W0 的 `Loc.cs` 登记。**W1–W9(含 W4b、W12)之间没有任何一条"先做 A 才能做 B"的边。**

**唯一的串行边**:W11(Mermaid)在 W4b 之后(两个包已于 2026-11-14 有条件批准)——它要往 W4b 刚建的聊天代码块渲染器里插一层,同一批文件,所以不并行。

### W10:需要先定契约或要拍板的大件(串行)

| 项 | 前置 |
|---|---|
| 扩展包仓库(10) | 先定包格式(zip + manifest + hash)、安装/更新/卸载契约,`PluginRecord` 加"内置/捆绑/已装"维度 |
| 自动更新(11) | 已拍板(2026-11-14):自写、分两阶段,见「已定」。阶段 A 可直接开波次;阶段 B 前置:包格式契约、Windows 替换方案、签名策略 |
| MCP OAuth / `resources/subscribe` / progress(9) | 先改 `Mcp/SPEC.md` 未决;OAuth 要动态注册 + PKCE,token 进加密库 |
| 快捷键页(14d) | 先把分散的 XAML `KeyBinding` 收敛成 keymap 表 |
| 评审包 + `request_review`(L7) | 已升为并行波次 W12(见上) |
| L2/L3/L4/L6 | 按需(PR 评论线程 UI、breadcrumbs、用量统计、终端搜索与重启) |
| L5 Mermaid / 流程图 | 已升为独立波次 W11(见上,已批准) |
| L8 布局设置对话框 | 待定(聊天版式已定为铺满窗口,见「已定」) |

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
| 减少动画(全局动效闸门,树与时间轴共用) | W13 | 关(动画开) | appearance |

规则:新增项**先登记再写代码**;值只在 `AppPreferences` 读取,工具/服务通过构造参数或 options 注入,不在热路径里反复读文件;改动热加载(沿用 `IReloadableSettingsSection`)。登记表之外的新增可配置项,在该波的实现说明里补一行并说明为什么不是内部细节。

## 七、明确不做

隐私统计/问卷/反馈、JetBrains Central;不引新包前提下的语法高亮替换、tree-sitter;PDF 预览在拍板前不做(占位页);网页不在面板内渲染,只给可编辑源码与「在浏览器中打开」。

**W0 收尾补充**:新增设置分区的视图不再改 `App.axaml`——`App.axaml` 已挂一个 `Views/SectionViewTemplate`,各波在自己的 `*Composition.Register` 里调 `SectionViewTemplate.Register<TViewModel>(() => new XView())` 即可。`chat` 分区归 W5(Mermaid 与工具图片缩略图两项设置的视图都放进 W5 的 chat 分区视图;W4 只提供数据项,不建分区)。

## 八、W12 交付记录(评审包 + `request_review`)

开工前分支落后 `main` 55 个提交(W0–W5、W7 已合入,`pr_create` 的四道闸门、`GitReviewLedger`、计划面板都在),先快进再动手。开工期间 `main` 又进了两波:W6(MCP 管理与工具暴露)、W8(资源检查器),收尾时分两次 `git merge main`——W6 那次只有一处真冲突(`AppComposition.cs` 里 `runtimeServices.Reviews` 与 `runtimeServices.Mcp` 相邻两行,两条都保留),W8 那次全部自动合并(两个 SPEC 与计划文档的不同段落);每次合并后都重新构建并重跑相关过滤(见下验证)。

**范围**:计划里 W12 的两条——给 Reviewer 补 `git_diff` 与变更集包;`request_review` 工具(主 Agent 发起评审、结果回到会话)。三条边界在开工前由用户拍定:

1. **评审目标两种都要**:`step_id`(审一个计划步骤,用它自己的那次提交)与自由范围(`base` + `paths`,默认仓库默认分支,含未提交与未跟踪)。
2. **一次评审不满足 `pr_create` 的自评闸门**:闸门仍只认本会话在这个 HEAD 上读过完整 `git_diff`;Reviewer 的 `git_diff` 记在它自己的子会话上(`GitReviewLedger` 按 sessionId + worktree 分键)。
3. **`request changes` 只回会话**:工具结果由归一化结论 + 下一步组成,代码层面不硬阻断任何东西;不落存储、不升 schema、不改计划面板。

**落点**(对照计划给的独占路径):

- `src/Forge.Core/Tools/ReviewPackage.cs`(契约,主 Agent 写,FROZEN)、`ReviewPackageText.cs`(包渲染)、`RequestReviewVerdict.cs`(结论归一化)、`RequestReviewTool.cs`(工具)。
- `src/Forge.Core/Prompts/BuiltInAgents.cs`:Reviewer 白名单加只读 `git_diff`,提示词加一段"你会收到评审包,diff 被截断时用 `git_diff` 读其余"。
- `src/Forge.Core/Agents/AgentHost.cs`:`AgentHostOptions.Reviews`(可空)+ 注册 `request_review`(没有实现就没有这个工具)。
- `src/Forge.App/Git/ReviewChangeSetSource.cs`:实现 `IReviewChangeSetSource`,用 `GitCli` 公开 API;`AgentRuntimeServices.Reviews` 与组合根接线;`ToolCatalog` 把它归到 `agents` 组。
- 规格:`Forge.Core/SPEC.md`(评审工具一段 + 未决两条)、`Forge.App/SPEC.md`(会话工具面一段)、`Forge.IO/SPEC.md`(白名单那句不再笼统地说"Scout/Plan/Reviewer 用前面四个")。

**偏差(已记录)**:

1. **没有新建 `Localization/Strings.Review.cs`**。W12 没有用户可见的新表面:工具行显示工具名与结果(与其他工具一样),评审结论是模型可读的英文。计划本身在 W0 已否掉"建空表"的做法,所以不为凑文件名建一张空表;要让评审有本地化界面,应该是结论持久化/计划面板显示那一档(见下面的延后项)。
2. **动了一个 `Forge.Workspace` 文件**:`GitCli.Review.cs` 的 `EmptyTreeSha` 由 `private` 提升为 `public`。评审"仓库的第一个提交"必须对空树做 diff,而那个常量只能有一份定义。没有新增 API、没有改行为。
3. **动了三个计划外的 App 文件**(`SessionRuntime.cs` 两行、`AppComposition.cs` 三行、`ToolCatalog.cs` 两行):`request_review` 只有在组合根把 git 实现交出去时才存在,而 `FeatureComposition` 的各模块拿不到 `GitCli`,所以运行期接线只能留在 `Create()`。
4. **包内上限**(起初是内部常量,后续已进设置,见第二轮评审之后的「上限进设置」):包内 diff 48000 字符、提交清单 50 行与文件清单 200 行各自独立、每个文本字段 2000 字符(现为 `GitReviewDiffMaxChars`/`GitReviewMaxCommits`/`GitReviewMaxFiles`/`GitReviewFieldMaxChars` 的默认值);结论原文 8000 字符仍是内部常量。它们是防止子 Agent 上下文爆炸的边界,与 `AgentLoop` 的 artifact 阈值同性质;`diff` 与清单的 `stat_only` 退路都是 `git_diff`。
5. **评审方式可配置未做**(`goal-and-requirements.md` 的"Reviewer 子 Agent 一次评审,或人工逐行评审"):需要设置项 + 一条"步骤提交后自动评审"的触发路径,属于新的产品面,未在 W12 授权范围内。结论也不持久化。

**验证**(合入前与两次 `git merge main`(W6、W8)之后各跑一遍,数字一致):

- `dotnet build Forge.slnx` → 0 警告 0 错误(含 Native AOT 分析器)。
- `Forge.Core.Tests --filter "Tools|Agents"` → 825 通过;`--filter "Review|AgentCatalog"` → 90 通过。
- `Forge.App.Tests --filter "Forge.App.Tests.Git"` → 413 通过;`--filter "Localization|MinorRegression|ReviewChangeSetSource|Settings"` → 340 通过(前三项是仓库硬门槛,`Settings` 是因为 W6/W8 都改过设置与 `SessionRuntime`/`ToolCatalog`/`AppComposition` 一带)。
- `Forge.Workspace.Tests --filter "Git"` → 1006 通过(`EmptyTreeSha` 提高可见性 + 新增 `GetDiffSnapshotAsync`);这一条在合并 W6/W8 前跑,`Forge.Workspace` 没被那两波改过。
- 未跑全量 `dotnet test`(按仓库约定,全量混着与本次无关的偶发失败);只跑与改动落点相关的过滤。渲染样例人工核对过一次(评审包的段落与空行可读,收窄与截断信息自述)。

**测试 Agent 拉出来的四个真问题(已改)**——它们不是测试补丁,而是实现缺陷:

1. 结论行只往下扫 40 行:把结论写在前面、补充说明写在后面的 Reviewer 会被判成 `unstructured`,代价是多跑一轮评审。改成扫全文。
2. `AppendClamped` 无换行时取“第一个空格前”,一个 2500 字符的 URL 前面写着 `Fix ` 时,意图到达 Reviewer 只剩 `Fix…`。改成只在词边界能保留 ≥3/4 字段时才采用,否则硬截。
3. 用户改写 Reviewer 模板把 `request_review` 加进白名单后,评审可以要求再评审(靠深度上限兜住,但会白烧 token)。按 agent 名拦下。
4. 子 Agent 被深度上限拒绝时的文案只说“不能派生”,不指向评审。前缀改成 `The review could not be started: `。

另外两处一致性修正:分支评审的**提交清单也按 `paths` 收窄**(否则评审包里会列出全部不在范围内的提交);`ReviewChangeSetSource` 把“是否 git 仓库”的检查移进 `try`,git 起不来时也变成可读文本而不是原始异常。

**独立评审会话(deepseek,与实现/测试会话分开)拉出的问题(已改)**:

1. **blocking——基准没解析成提交,比较语义也错。** 原实现把 `base` 原样交给 `git diff <base>`(两点语义),而 `git_diff`/PR 正文/`pr_create` 全是 `base...HEAD`。默认基准 `origin/main` 只要 fetch 过一次就越过分叉点,上游新提交会在包里显示成“本次变更在删除它们”,Reviewer 会据此报假 blocking;同时 `Base` 是 ref 名却被 `ShortSha` 截断,包里印 `base origin/`。现改为先 `RevParse` 成提交、再取与 HEAD 的 merge-base,`Base` 因此恒为 sha;与 HEAD 无共同历史时 `Failed`。
2. **major——收窄信息没进包。** `Paths` 作用在 diff/文件表/提交表上,但包里不写“只看了这些路径”,Reviewer 分不出“别处没改”与“被过滤掉了”。`ReviewChangeSet` 增 `Paths` 与 `CommitsTruncated`,包内自述 `narrowed to: …`,计数在截断时写 `at least N commits`。
3. **minor——提交数被当成总数,且长提交历史会把文件表挤掉。** 提交清单上限 200 写死且与文件表共享 200 行预算,`... and N more` 永远不会触发。现拆为各自独立预算(提交 50 行、文件 200 行),源取“上限+1”条以区分“恰好”与“被截断”。
4. **minor——同一份 diff 跑两个 git 进程,且注释谎称两者同源。** 新增 `GitCli.GetDiffSnapshotAsync`(一次捕获同时给文本与文件,`GetDiffAsync` 改为它的 `.Files`),评审只调它;解析/补未跟踪的逻辑仍只有一份。
5. **minor——提交对象被抹掉时给模型的是 git 原文。** 先 `rev-parse <sha>^{commit}` 判存在,不存在就返回“这个提交在本仓库里不存在”。
6. **测试盲区已补**:基准领先 HEAD 的三点语义、默认基准解析成 sha(不再出现 `origin/`)、`GitCli` 提交对象丢失、`MaxParallelSubAgents = 1` 下 `Detach` 必需(实测把 `Detach` 拿掉该用例会 15s 超时)、`step_id` 指向 `CommitId == null` 的步骤、收窄字段与截断计数。

**合入前复核(第二轮评审)拉出的问题(已改,各有测试)**:

1. **被停止的 Reviewer 的部分文本会被当成结论。** `sub_agent_stop` 停掉的 Reviewer 若已写过 `verdict: approve`,工具会报通过。`SubAgentOutcome` 增 `Stopped`,`RequestReviewTool` 见到它直接返回错误“没有结论”,不解析原文。
2. **子 Agent 开关关掉后 `request_review` 仍可派生 Reviewer。** `SessionRuntime.ApplyDisabledTools` 改为撤下 `SpawningTools = [spawn_agent, request_review]`。
3. **截断退路指向裸 `git_diff`。** 裸调用比较默认分支,与自定义 `base`、单提交评审不是同一变更集;现在提示带 `{"base": "<sha>"}`,单提交时声明该范围还含之后的提交,根提交指向 `read`。`ReviewChangeSet` 增 `SingleCommit`。
4. **每个未跟踪文件读满 1MB 只为列出它。** 改为只探 8000 字节(与 git 二进制判定窗口一致);内容本就不进包。
5. **`narrowed to:` 路径列表无上限**,改按 2000 字符字段上限截断;**无换行 diff 的截断可能劈开代理对**,已避开。
6. 测试补齐:停止/父取消/同回合两个评审并发/超长原文的结论读取/截断时给出可复现基准;源侧删除、重命名、二进制、未跟踪二进制窗口、超出捕获上限、提交清单截断(上限经构造参数注入)、空变更集;子 Agent 开关与 `request_review` 联动。

**上限进设置(已实施,分支 `review-limits-settings`)**:包内 diff 字符、提交清单行数、文件清单行数、文本字段字符数四项进了 `AppPreferences`(`GitReviewDiffMaxChars` 1000–500000 默认 48000;`GitReviewMaxCommits` 5–500 默认 50;`GitReviewMaxFiles` 20–2000 默认 200;`GitReviewFieldMaxChars` 200–20000 默认 2000),界面在 `git` 设置分区;Core 以 `ReviewPackageLimits`(默认即旧常量)经 `AgentHostOptions.ReviewLimits` 每次评审现读。未跟踪文件的 8000 字节探测与 git 的二进制判定窗口一致,保持内部常量。

**仍未改(记录原因)**:没有墙钟超时,评审与 `spawn_agent` 一样由 MaxTurns 100 与深度 3 兜住;超过 git 8MB 捕获上限时评审以可读文本失败而不是回落到 stat。
