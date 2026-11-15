# W11–W15 视觉清单(2026-11-15,2026-11-15 收尾波补充自动化证据)

四波交付评审共同拉出的未验证项:各 Agent 的"手测未做"(真实 App 视觉复核)。跨波交互(主题/语言/动效)
本该一次过,不分散到各波。启动:`FORGE_RUN=1 dotnet run --project src/Forge.App`(输出走 `artifacts/run/`)。

**勾选口径**:凡是行为能由代码表达的,已改成自动化断言并在下面标 `[x] **自动化**`(附带用例名);只有**需要真实模型或网络会话**、以及**纯审美判断**的项目保持未勾,并在每项后写明原因。

## W13 时间轴

- [x] **自动化** 明暗主题切换:节点四态色(`TextMutedBrush` / `AccentBrush` / `DangerBrush` / `WarningBrush`)在 Light 与 Dark 下都能解析且互不相同 —— `VisualSurfacesTests.The_timeline_axis_and_node_brushes_follow_the_theme`。Mermaid 图的主题轮换由 `MermaidRenderServiceTests` 的调色板用例钉住。
- [x] **自动化** 「减少动画」即时生效:闸门开/关即时反映到 `TreeMotion.ReducedMotion`,已挂树的运行中节点读同一闸门 —— `VisualSurfacesTests.The_reduce_motion_gate_stops_and_restarts_the_pulse` + `ReduceMotionTests`(含 `TreeMotion.Changed` 订阅)。
- [x] **自动化** 长会话滚动:虚拟化回收容器后轴线重算(含 3 次后台重试与「只画节点不画线」回退),节点图标不错位 —— `TimelineGutterTests`(21 例)。滚动观感本身属审美。
- [x] **自动化** 空轮次:运行中且无块的回合 `IsRunning` 为真、结束时翻假 —— `VisualSurfacesTests.A_running_turn_with_no_output_is_its_own_axis_node_until_it_ends`;轴端几何由 `TimelineLayout` 用例钉住。
- [x] **自动化** 语言切换:每一类节点的名称键在中英两语下都解析成对应语言的文字 —— `VisualSurfacesTests.Node_names_and_states_switch_with_the_language`。
- [x] **自动化** 子 Agent 与主会话同一套轴模板:`SessionView.axaml` 与 `SubAgentView.axaml` 都引用 `ChatTranscriptPadding`,轴因此落在同一 x —— 由下面那条内边距断言覆盖。

## W11 Mermaid

- [x] **自动化** 五类默认图型(flowchart/sequence/state/class/er)各渲一张,明暗主题各一次 —— `MermaidRenderServiceTests`(默认白名单逐类真实渲染 + 明暗调色板)。
- [x] **自动化** 故意语法错误:失败态保留源码、错误行、重试与「让模型修正」,修正消息固定英文 —— `MermaidBlockView` 状态机用例 + `MermaidFixMessage` 常量断言。按钮的视觉呈现属审美。
- [x] **自动化** 围栏不闭合的流式回复:流式期间只显源码,定稿时按闭合重解析并渲染(`FinalizeOpenFence`) —— `StreamingMarkdown` final 路径用例。
- [x] **自动化** 主题切换后已渲染的图重渲(调色板变了才重渲);重新挂回可视树时比较当前调色板 —— `MermaidRenderServiceTests` 的调色板→picture 引用变化用例。
- [ ] **人工** 图内文字:中英文标签都用系统字体栈渲染、无黑块。需要真实 Skia 帧与系统字体,headless 无法判定「字有没有画出来」。
- [x] **自动化** 设置分区 `chat`:图类型白名单收窄后,被排除的类型回落普通代码块、不报错 —— `MermaidDiagramTypes` + `MarkdownRenderer` 用例。

## W14 CodeMode

- [ ] **人工(需真实模型会话)** 让模型用 `run_code` 扇出 `read`/`grep`,结果回传。
- [ ] **人工(需真实模型会话)** 脚本内调 `web_fetch` 的审批卡逐卡/run 级折叠交互。
- [x] **自动化** `tools` 分区四项旋钮:改完下一回合生效(每次读设置快照),可见名单收窄后 `Tools.List()` 只列允许的 —— `CodeModePreferencesTests` + `RunCodeScriptTests`。
- [x] **自动化** 没装插件的会话也有 `run_code`;扩展宿主未就绪时以可读 `IsError` 结束 —— `SessionRuntime`/`RunCode` 用例。

## W15 限速

- [x] **自动化** `rate-limit` 分区四项 + 覆盖表:留空继承全局、填 0 该家不限、清空删条目 —— `RateLimitSettingsSection` 用例(归一化/顺序/百分比语义)。
- [ ] **人工(需真实会话)** 请求/分钟调到 1 后连发两条:第二条轮次卡出现等待行。等待事件(`AgentBudgetWait`)本身已由 `RetryingChatProvider` 用例钉住,、「卡片上确实出现」需要真实回合。
- [x] **自动化** 关掉总开关:自动化恢复间隔一起关 —— `QuotaResume` 用例。
- [ ] **人工(需真实会话)** 令牌维度不再明显低估本地预算:需长输出真实会话对照设置值。

## 跨波

- [x] **自动化** 语言切换即时生效:节点名、失败标题/按钮、设置分区、等待行的文案键在中英两语下都已注册且解析成对应语言 —— `VisualSurfacesTests.Node_names_and_states_switch_with_the_language` + `StringTableTests`/`KeyRegistryTests` 扫描门(所有 `Loc` 用点必须双语注册)。
- [x] **自动化** 窗口内边距一致:转录/输入卡/横幅三处左右值来自同一组 token 且彼此相等 —— `VisualSurfacesTests.The_chat_surfaces_share_one_padding_token`。

## 仍需人工的项目汇总(共 6 项,全部依赖真实模型/网络会话或审美)

| 项 | 为什么不能自动化 |
|---|---|
| W11 图内文字无黑块 | 需真实 Skia 帧与系统字体渲染结果 |
| W14 模型驱动 `run_code` 扇出 | 需真实 LLM 响应 |
| W14 脚本内 `web_fetch` 审批卡交互 | 需真实模型发起的内层调用 |
| W15 等待行出现在轮次卡 | 需真实回合与真实限速等待 |
| W15 令牌维度长输出对照 | 需真实长输出会话 |
| 全部项目的「观感」层面(间距是否好看、动效是否顺眼) | 审美判断,非行为断言 |
