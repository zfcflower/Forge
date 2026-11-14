# W13 实现说明:聊天时间轴版式

计划来源:`2026-11-13-thinkrail-gap-plan.md` 第四节 W13 行与「已定」表里的
「聊天时间轴版式(2026-11-14 批准)」。本文是开工前交付的方案、被否备选、四条判据与验收口径;
实施偏差与验证结果在文末「交付记录」。

## 一、目标(摘自批准结论,不改)

- 聊天记录排成时间轴:**左侧一条贯穿的细竖线(轴),每个事件一个节点(图标)落在轴上,内容在节点右侧**,
  铺满窗口、窗口内边距一致;相邻事件被轴连起来,**最后一个节点下面轴线截止**。
- 运行中的节点有轻量动画,可被「减少动画」关掉。
- 节点类型与图标(Regular 空心 / Filled 实心,按状态切换):用户消息、助手文本、思考、读文件、搜索、编辑/写文件、
  命令、网络、MCP 工具、子 Agent、计划/待办、提示、错误、审批。
- 节点图标大小、轴线粗细与颜色、节点间距走同一组样式资源,不在各视图写数字。
- 图标不抢焦点、有可读的名称;随主题与语言切换;长会话仍走虚拟化,不因轴线/节点增加每条消息的分配。

## 二、方案

### 2.1 事件到节点的映射(内容不变,只加"呈现层")

现有转录模型是三层:`Transcript.Items`(顶层:`UserMessageViewModel` / `NoticeViewModel` /
`TurnCardViewModel`)→ `TurnCardViewModel.Blocks`(`TurnTextViewModel` / `ToolRowViewModel` /
`TurnNoticeViewModel`)。时间轴把**节点画在每一个已经存在的行上**,不新增业务数据、不扁平化集合:

| 行 | 节点类型 | 来源(现有属性) |
|---|---|---|
| `UserMessageViewModel` | 用户消息 | 固定 |
| `NoticeViewModel` / `TurnNoticeViewModel` | 提示 / 错误(失败时)/ 审批(`Kind=Approval`) | `IsError`,新增只读 `Kind` 提示位 |
| `TurnCardViewModel`(抬头) | 无节点:它只是"第 N 轮 · 时间 · 耗时 · token"的元数据行,轴线从它中间穿过 | `Header` |
| `TurnTextViewModel` | 助手文本(流式时运行中) | `IsStreaming` |
| `TurnCardViewModel.Footnote` | 错误 | `HasFootnote` |
| `ToolRowViewModel` | 按 `Name` 分类:读文件/搜索/编辑/命令/网络/MCP/子 Agent/计划/通用工具 | `Name`、`IsError`、新增只读 `IsRunning` |

工具名 → 节点类型的表(`TimelineNodeMap`)按语义分组,覆盖全部内置工具名:
`read`/`read_artifact`/`read_history`/`git_status`/`git_log`/`git_diff`/`spec_get`/`spec_graph`/`spec_validate`
→ 读文件;`ls`/`find`/`grep`/`search_history`/`spec_grep`/`spec_search` → 搜索;`edit`/`write`/`spec_create`/
`spec_update`/`spec_delete` → 编辑;`bash`/`run_code` → 命令;`web_fetch`/`web_search`/`git_fetch`/`git_sync` 与
`pr_*`/`review_*` → 网络;`mcp_tool_search` 与名字含 MCP 分隔符 `__` 的工具(`McpToolNaming.Separator`)→ MCP;
`spawn_agent`/`subagent_*`/`request_review` → 子 Agent;`todo` → 计划;`ask_user` → 提示;
`git_branch`/`extension_*`/未知名字 → 通用工具。表是纯函数,单独可测。

状态映射:`IsRunning` → 运行中(实心 + 强调色 + 轻量脉冲);`IsError` → 失败(实心 + 危险色);
审批(拒绝)提示 → 警示色;其余空心 + 次要色。提示类节点失败时改用"错误"图标(三角形),其它类节点保留自己的图标只换色。

### 2.2 轴线几何:每行自绘线段,不新增行视图模型

新增 `Views/TimelineGutter`(TemplatedControl,控件主题在新建的 `Views/TimelineStyles.axaml`)是**一行左侧的
轴列**:竖排三格——上段线(固定 `TimelineNodeTopGap` 高)、节点图标(固定 `TimelineNodeSize`)、下段线(撑满)。
行本身不加外边距,竖直间距全部由内容列的 `Margin` 或内容自带的 `Padding` 提供,所以相邻行的线段在边界处相接。

每行只回答两个布尔:`ShowTopLine`(它上方有节点)与 `ShowBottomLine`(它下方有节点)。判定规则在纯函数
`TimelineLayout` 里,不读可视树:

- 顶层叶子(用户消息/提示):上段 = 不是首项,下段 = 不是末项。
- 轮次抬头(无节点):两段都 = `不是首项 && (展开后有块 || 有脚注 || 不是末项)`——它只把轴从前一个节点牵到后一个节点。
- 第 i 块(共 n 块):上段 = `i > 0 || 不是首项`,下段 = `i < n-1 || 有脚注 || 不是末项`。
- 脚注行(错误节点):上段 = `有可见块 || 不是首项`,下段 = `不是末项`。

**"是否有可见块"= `IsExpanded && Blocks.Count > 0`**:折叠的轮次(只发生在子 Agent 转录里,且永远不是最后一项)
不再画块,轴从抬头直接穿到下一项/脚注,不会留空。轴起点落在第一个真正的节点上(首个轮次的抬头不画线)。

行的"是否是首/末项"与"块序号"由 `TimelineGutter` 在挂到可视树时从祖先算出:最近的 `ItemsControl`
+ `ContainerFromItem`/`IndexFromContainer` 给出序号;块行再向上找一层,拿到所属轮次在转录列表里的序号。
每行在挂树时订阅**两个集合的 CollectionChanged**(自己的 `Items`,以及外层转录的 `Items`)与轮次视图模型的
`PropertyChanged`(`IsExpanded`/`HasFootnote`),在离树时全部退订;集合变化只重算布尔与图标,不重建控件。
已实现的容器才会订阅(≈一屏),虚拟化不变,不产生"每条消息一个包装视图模型"的分配。

### 2.3 视觉与资源

- `Resources/Tokens.axaml` 新增:`TimelineGutterWidth`(26)、`TimelineNodeSize`(14)、`TimelineNodeTopGap`(6)、
  `TimelineAxisWidth`(1);聊天区三处内边距(`ChatTranscriptPadding`、`ChatComposerMargin`、`ChatBannerMargin`)
  统一按窗口内边距 16 建立。轴线颜色/节点颜色/脉冲动画只在 `Views/TimelineStyles.axaml` 的样式里出现一次:
  轴线 `BorderBrush`、常态 `TextMutedBrush`、运行中 `AccentBrush`、失败 `DangerBrush`、审批 `WarningBrush`。
- 图标仍只有一个来源与一个渲染件:`Resources/Icons.axaml` 追加 Fluent System Icons 24px 的 Regular/Filled 成对几何,
  `Workbench/Controls/FluentIcon` 渲染;新增 `TimelineNodeMap.IconKey(kind, filled)` 只给键名,
  控件按当前主题解析资源,不在代码里存路径字符串。
- 聊天内边距统一:`SessionView` 转录列表用 `ChatTranscriptPadding`,输入卡与自动化横幅用同一档
  `ChatComposerMargin`/`ChatBannerMargin`;`Border.user-bubble` 去掉固定 `MaxWidth=640`(计划已定"铺满、
  不设最大行宽"),用户气泡仍右对齐、内容不限宽。

### 2.4 动画与"减少动画"

- `TreeMotion.ReducedMotion` 是这个仓库已有的全局动效闸门(`ProjectTree` 已在读),但从来没有人给它赋值。
  本波把它接上:新增偏好 `ReduceMotion`(`AppPreferences`,默认关,`appearance` 分区一个勾选框),
  `AppearanceApplier` 在每次应用外观时写闸门;`TreeMotion` 增加 `Changed` 事件,已挂树的时间轴行即时
  去掉/恢复 `pulsing` 类。这样「减少动画」是真实存在的用户开关,而不是一个永远为 false 的静态字段。
- 运行中的节点:实心图标 + 强调色 + 透明度脉冲(2 s,与树里 Agent 行的呼吸同一节奏);减少动画时保留强调色、
  去掉动画类。

### 2.5 可访问性

节点图标 `Focusable=false`、`IsTabStop=false`、`IsHitTestVisible=false`(不抢焦点、不吃点击),控件的
`AutomationProperties.Name` 由 `TimelineStrings` 给出当前语言的名字,运行中/失败/审批再拼上状态词;
订阅 `Loc.LanguageChanged` 与状态变化时重设。折叠按钮的 `AutomationId="turn-toggle"` 保持原值。

### 2.6 变更面

新增:`Views/TimelineGutter.cs`、`Views/TimelineStyles.axaml`、`Views/TimelineTurnView.axaml(.cs)`、
`ViewModels/TimelineNodeViewModel.cs`(枚举 + 映射表)、`ViewModels/TimelineLayout.cs`(纯几何判定)、
`Localization/Strings.Timeline.cs`。

修改:`App.axaml`(转录条目的 6 个模板改为"轴列 + 原内容",新增一条 `StyleInclude`)、
`Views/SessionView.axaml`(转录区内边距、输入卡/横幅外边距)、
`Resources/Tokens.axaml`、`Resources/Icons.axaml`(只追加)、`Resources/Controls.axaml`
(`Border.user-bubble` 去掉 `MaxWidth`)、`ViewModels/Transcript.cs`(提示行的只读 `Kind` 提示位)、
`ViewModels/Transcript.Tools.cs`(只读 `IsRunning`)、`ViewModels/TreeIcons.cs`(`TreeMotion.Changed`)、
`SessionWorkspace.cs`(被拒绝的审批把 `Kind=Approval` 传进提示行)、`Preferences/AppPreferences.cs`(ReduceMotion)、
`Appearance/AppearanceApplier.cs`(写闸门)、`Appearance/AppearanceSettingsView(+VM).axaml/.cs`(勾选框)、
`Localization/Strings.Appearance.cs`(一项文案)、`Forge.App/SPEC.md`(聊天时间轴一段 + 未决项更正)、
`docs/research/2026-11-13-thinkrail-gap-plan.md`(W13 交付记录与登记项)。

## 三、被否的备选

1. **把转录扁平化成"一个事件一个视图模型"的 `TimelineRowViewModel` 列表**(最像 Claude 的数据形状,
   还能把工具行也纳入虚拟化)。否掉:计划要求"不因轴线/节点增加每条消息的分配",而这条会为每条事件新增一个包装对象,
   还要镜像 `Blocks` 的增删与折叠、替换 `Transcript.Items` 的语义(17 个测试文件与两处视图在用);
   收益只是几何判定更直接,不值得把契约翻一遍。
2. **在 ListBox 背景里画一条贯穿视口的轴线**(最省事,零逐行逻辑)。否掉:轴会越过第一个节点上方、
   最后一个节点下方,直接违反"最后一个节点下面轴线截止";滚动长会话时也会看到没有节点的空轴线。
3. **每行用一个 `ContentControl` 包装原模板、内容用 keyed DataTemplate 选择器**(零模板重复)。
   否掉:为避开同类型模板递归要另建一套 keyed 模板与选择器,模板与选择器两份状态容易不同步;
   现在的做法只在外层加一层两列 Grid,原模板内容一行不改,回滚时删掉 Grid 即可。
4. **用 OS 的"减少动画"系统设置**(更像原生)。否掉:Avalonia 12 的 `IPlatformSettings` 没有这项能力
   (只有点击/热键/颜色),要做只能 P/Invoke,违反本模块的边界。
5. **给"思考"接一条真实的内容管线**(把 `AgentThinkingDelta` 渲染成块)。否掉(本波):计划明确本波
   "从现有 `TranscriptItemViewModel` 派生、不改其业务数据",而思考块的流式管线是聊天内容面的新功能
   (会话、转录状态机、渲染三层都要改,并与 W4b 的 Markdown 流式叠加),不是版式工作。见「未覆盖与理由」。

## 四、四条判据

- **性能**:UI 线程只做几何布尔的读写与图标资源查找(只在状态或集合变化时,不在渲染帧里);不新建逐消息对象,
  不增加绑定层;竖直间距放在内容 `Margin`,行本身零外边距,轴线没有额外测量;每行订阅的是已有的
  `ObservableCollection`,一屏 ≈ 20 个订阅者,增量为 O(可见行);长会话仍由 ListBox 的虚拟化决定实例数。
- **依赖与 AOT**:零新包、零 P/Invoke、零 Roslyn;只用现有 Avalonia 控件与 `FluentIcon`;新代码没有反射与动态绑定
  (模板仍是编译绑定),Native AOT 分析器保持 0 警告。
- **契约与可测性**:不改 `TranscriptItemViewModel` 的业务数据;新增两个只读派生属性(`IsRunning`、提示的 `Kind`)
  与一个纯函数层(`TimelineNodeMap`、`TimelineLayout`)。测试可以完全绕开视觉:映射表逐名断言、几何按
  (首/末/块序号/折叠/脚注)组合断言;视图侧用 headless 断言已实现行的 `ShowTopLine`/`ShowBottomLine`、
  图标几何、类、可读名称、语言切换、减少动画类与虚拟化下的重算。
- **回滚成本**:单独 revert 本波即可:所有改动集中在时间轴渲染、内边距 token 与"减少动画"三项;
  原模板内容没有搬家以外的重写,`Transcript.Items` 的语义未变,计划面板/其他转录宿主不受影响。

## 五、验收口径

- 构建:`dotnet build Forge.slnx -c Debug` 0 警告 0 错误(含 AOT 分析器)。
- `Forge.App.Tests` 相关过滤:`Timeline|Transcript|SessionView|ChatMarkdown|Localization|MinorRegressionTests|KeyRegistry|StringTable|NoHardcodedChinese|Appearance|Settings`。
- 手测项:长会话滚动(轴线连续、末节点截止)、工具行运行/完成/失败三态、展开/折叠轮次、语言切换后的读屏名称、
  外观里勾上"减少动画"后脉冲停止、窗口缩放时聊天区与输入卡左右内边距一致。

## 六、未覆盖与理由(开工前记录)

- **思考节点**:映射与图标齐备,但会话当前有意丢弃 `AgentThinkingDelta`(`SessionWorkspace.OnAgentEvent`
  对它是 `break`,W4b/SPEC 的决定),所以本波不会真的出现思考节点;把它打开需要新增思考块、流式缓冲与渲染,
  属于聊天内容面而非版式,未在 W13 授权范围内。此条随交付记录上报,不静默留在代码里。
- **审批节点**:只覆盖"被拒绝的审批"在转录里留下的提示行(`AgentApprovalDenied`);待审卡是窗口级浮层
  (`Approval/ApprovalQueueOverlay`),不在转录里,因此不画在轴上。
- **MCP 节点**的分类依据只有工具名(行上没有来源信息):按 `McpToolNaming` 的 `server__tool` 分隔符判定,
  误判只会换一个图标;把来源塞进工具行属于转录业务数据,不在本波。