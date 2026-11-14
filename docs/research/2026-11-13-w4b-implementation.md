# W4b 实现说明:聊天 Markdown 渲染

对应 `2026-11-13-thinkrail-gap-plan.md` 的 W4b(L1「消息复制」里缺的那一半:聊天完全不过 Markdown 渲染、无 fenced code)。

## 一、要交付什么

聊天里 agent 的回复文本(**`TurnTextViewModel`**)改为按 Markdown 渲染:标题、段落、列表(含任务项)、引用、表格、链接、分隔线、围栏代码块;代码块带**语言标签**与**复制按钮**。流式到达时必须增量追加,**不得整段重排**——这与 `forge-goal` 的「性能第一」是同一条约束,不是附加项。

用户消息(`UserMessageViewModel`)**仍按纯文本**渲染。理由:用户输入的是字面文本,过 Markdown 会把 `*`、`_`、`#` 当语法吃掉;`Messages.cs` 里用户文本本来就是纯文本(`TextBlock`),而助手文本才是被要求产出 Markdown 的那一侧。其模板在 `App.axaml`(W0 冻结),本波不动。

## 二、方案

### 2.1 三层,各自只做一件事

| 层 | 文件 | 职责 |
|---|---|---|
| 纯状态机 | `Changes/Markdown/StreamingMarkdown.cs`(新) | 收字符流,维护「已定稿块前缀 + 未定稿区」,`Complete()` 时与整篇解析对齐。无 Avalonia 依赖,可直接单测 |
| 视图模型 | `ViewModels/Transcript.cs` 的 `TurnTextViewModel` | 持状态机、暴露 `Document`/`CopyText`/`IsStreaming`/占位文案,`Append`/`Complete` 后发 `ContentChanged` |
| 控件宿主 | `Views/ChatMarkdownView.axaml(.cs)`(新)+ `Views/TurnTextView.axaml(.cs)` | 订阅 `ContentChanged`,把新增块渲染成控件追加,只刷新未定稿区;`TurnTextView` 保留样式引入、占位文案与整条复制按钮 |

`MarkdownRenderer`/`MarkdownBlockParser`(既有,`Changes/Markdown`)原样复用;只做两处**追加式**改动:块解析器多一个 `top` 形参(见 2.4),`RenderCode` 换成带语言标签与复制按钮的 `MarkdownCodeBlock`(`Update` 就地改文本,流式代码块不必重建控件)。

### 2.2 增量切分规则(状态机)

- 行是唯一的输入单位:`Append` 把 chunk 拆成完整行(`\n` 结算)+ 未完成尾行(`partial`)。`lines` 只增不改。
- **已定稿前缀 `stable`**:`lines[0..stableLine)` 的解析结果,只追加、永不重排。定稿边界 = **不在围栏代码块里**的空行之后(回溯扫描只扫自上次扫描以来新增的行,围栏状态跨 flush 携带)。
- 边界处的块若**还能被后续行延长**(`List`、缩进代码、未闭合的 `Html` 注释),**由边界之后的第一行决定**:那一行仍是同类列表项 / 仍缩进 ≥4 → 块留在未定稿区继续长;否则照常定稿。后续内容还没到时不决定(留到下一帧),所以松列表(项之间有空行)不会被截断成两个列表;而一旦结尾段落开始写,列表当帧就定稿,不会每帧被重解析。围栏代码块与 YAML front matter 解析完即闭合,不参与这条判断。
- **未定稿区 `live`**:`lines[stableLine..]` + `partial`,每帧整体重解析。它是两个空行之间的内容(通常是一个段落),很小。
- **超长段落的收口**:未定稿区若恰好是**一个段落**且超过 `ParagraphFragmentLimit`(4096 字)且有换行,把它除最后一行外的部分定稿成独立段落块。否则一段 20 KB 的散文会让每一帧都对整段做一次内联解析与文本整形(O(n²))。这是渲染粒度上的切分,`Complete()` 会把它们合并回一个段落。
- **`Complete()` 对齐**:把 `partial` 并入 `lines`,整篇解析一次,与 `stable + live` 逐块比 `(Kind, StartLine, EndLine)`;不一致(缩进代码跨空行、HTML 注释跨空行、围栏启发式判断偏差、上一步的段落切分)就以整篇解析为准整体替换,视图据此整树重建一次。**最终渲染结果因此恒等于「对全文做一次 `MarkdownBlockParser.Parse`」**,这是可测的不变量。

### 2.3 控件层的增量更新

视图持一份 `List<(MdBlock Block, Control Control)>`,前 `stable.Count` 项对应定稿块:

- 定稿块只追加:新定稿块各建一次控件。
- 未定稿区按块比较 `(Kind, StartLine, EndLine)`:末尾块若仍是**同一段落/标题**→ 只 `SetInlines`(不建控件、不动兄弟节点);若仍是**同一围栏代码块**→ 只 `Update(text, info)`(只换 `SelectableTextBlock.Text`);列表/表格/引用变了 → 重建该块及其后缀。
- 视图重新挂到可视树(切标签、会话切回)时按状态机当前状态整树重建一次——控件树不跨宿主保留。

线程模型不变:`Append` 仍在 UI 线程由既有的 `FrameCoalescer` flush 调用(`SessionWorkspace.RouteDelta` → `TranscriptViewModel.FlushPending`),所以状态机与控件更新都**不加锁**,也没有跨线程事件。

### 2.4 契约改动清单

- `TurnTextViewModel`:删 `Segments`/`TextSegmentViewModel`(段落实体),改为 `Document`(状态机)、`CopyText`(= 追加过的原文,零丢失)、`IsStreaming`、`HasPlaceholder`/`PlaceholderText`、`ContentChanged` 事件;`Complete()` 幂等。
- `TurnCardViewModel.AddTool/AddNotice` 丢弃旧文本块前先 `Complete()` 它:此前只封口「最后一段文本」,一段文本后面跟了工具行就永远拿不到复制按钮。
- `MarkdownBlockParser.Parse(..., bool top = true)`:分片解析时 `top:false`,否则一段以 `---` 开头的分片会被当成 YAML front matter 吞进代码块。
- `MarkdownRenderer.RenderCode` → `MarkdownCodeBlock`(语言标签 + 复制按钮,`Update` 就地更新);`Strings.Markdown.cs` 加一条 `changes.markdown.copyCode`。
- `Resources/MarkdownStyles.axaml` 追加 `Border.md-code` 的头部两条样式(`.md-code-head`、`.md-code-lang`)——diff 预览与计划面板是同一份渲染器,三处一起获得代码块头部(故意:三处不再各画一套)。

## 三、被否的备选

1. **每帧整篇重新解析 + 整树重建**(最朴素)。判据上输在性能:一篇 30 KB 的回复按 60 帧/秒算,每秒要对 30 KB 做 60 次块解析 + 内联解析 + 上百个控件重建,UI 线程直接被吃掉;而且会打断用户在一个已稳定的段落里划选。
2. **把 Markdown 渲染成 HTML 交给 WebView**(许多同类产品的做法)。仓库决策是「不含 JS 运行时依赖」(`architecture.md`),Avalonia 12 的 `NativeWebView` 是平台原生控件、AOT 下另有一整套托管要求,且 WebView 不能划选到宿主剪贴板、主题/字号要重注入。直接违反既有边界。
3. **引 Markdig 解析**。既有 `MarkdownBlockParser` 是纯托管、零依赖、已覆盖 GFM 子集且已服务 diff 预览与计划面板;引包要过白名单,还会让三处渲染分叉。W4b 只换渲染宿主,不换解析器。
4. **只做「流式期间显示纯文本,结束再渲染 Markdown」**。实现最省,但流式期间用户看到 `**`、`|a|b|` 原样文本,而这正是本波要消灭的「聊天不过 Markdown」;且结束瞬间整段重排/跳高,比增量更难看。
5. **流式期间按「最后一个空行」切分但不做 `Complete()` 对齐**。省掉对齐逻辑,代价是缩进代码跨空行、HTML 注释、段落切分这三类会永久渲染错(流式期间错、结束后还错)。对齐是让「最终结果等于一次整篇解析」成立的那一步,保留。

## 四、为什么这是最优(按四条判据)

**性能。** UI 线程每帧的工作量 = 把 chunk 拆行 + 扫描新增行里的空行 + 解析「上一个空行之后的内容」+ 刷新一个段落的 `Inlines`(或一个代码块的 `Text`)。凡是被空行封口的块只解析一次、只建一次控件、永不重排;定稿前缀本身一次都不再触碰。分配只在新增块、未定稿区的块列表和 `MdInline` 树上;`stable` 前缀零分配。列表是否虚拟化与既有的轮次卡一致(聊天区本来就不是虚拟列表,本波不改变这一点,也不新增每行委托);热路径不加锁(单线程,既有 flush 语义)。

**依赖与 AOT。** 零新包。新代码只有 `StringBuilder`/`List`/既有解析器;XAML 新增一个 `UserControl` 与一个 `StackPanel`,控件在代码里构造(Avalonia 控件构造是 AOT 安全的)。`CopyTextButton`、`FluentIcon`、`MarkdownTextBlock` 都是既有类型。AOT 分析器警告数不变(无反射、无动态绑定;编译期绑定照旧)。

**契约与可测性。** 唯一行为所有者是 `TurnTextViewModel`(定稿/未定稿都在它的 `Document` 里),视图是纯消费者。「最终渲染 == 一次整篇解析」是可断言的不变量;`StreamingMarkdown` 无 Avalonia 依赖,可以逐字符喂 + 随机分块喂对照整篇解析;控件层可用既有的无头 UI 会话断言「定稿块控件实例在后续帧里不变」。`Segments` 的删除是唯一破约改动,已全量迁移调用点与测试。

**回滚成本。** 单独 revert 干净:`Changes/Markdown/StreamingMarkdown.cs`、`Views/ChatMarkdown*` 是新增文件,`Transcript.cs`/`TurnTextView.axaml`/`MarkdownRenderer.cs`/`MarkdownStyles.axaml`/`Strings.Markdown.cs` 的改动互相自洽、不牵动其它波;没有 schema 变更、没有偏好字段、没有新设置分区。

## 四·二、与计划矩阵的偏差(实施时定)

| 项 | 计划的归属 | 实际 | 为什么 |
|---|---|---|---|
| `Resources/MarkdownStyles.axaml` | 不在 W4b 的独占路径里 | 追加两条样式(`Grid.md-code-head`、`TextBlock.md-code-lang`) | 代码块头部是**共享**渲染器的一部分(diff 预览、计划面板、聊天同一份),样式只能跟 `md-*` 那套走;无人独占此文件,追加式改动不会与任何波冲突 |
| `Localization/Strings.ChatMarkdown.cs` | 列为 W4b 独占路径 | **未创建** | 本波没有聊天专属文案:唯一新增的键(`changes.markdown.copyCode`)属于共享代码块渲染器,放进 `Strings.Markdown.cs`;建一张空表只会多一个没人读的文件 |
| 用户消息 | 计划写「聊天消息走 Markdown」 | 只有助手回复走 Markdown,用户消息仍是纯文本 | 用户打进来的是字面文本,过 Markdown 会吃掉 `*`/`_`/`#`;而且用户消息模板在 `App.axaml`(W0 冻结)。见 §一 |
| `tests/Forge.App.Tests/**` | 未列 | 迁移了 6 个既有用例文件 + 新增 2 个 | `Segments` 是唯一破约改动,调用点必须跟着改;新增用例覆盖状态机与视图的增量性 |

## 五、可配置项

本波**不新增可配置项**:切分粒度(4096 字)与「哪些块可被延长」属于内部实现细节;没有任何用户会想调「流式分片大小」。计划第六·二节登记的 W4b 相关项(工具图片缩略图、是否回传)属于 W4,已随 W4 落地。

## 六、已知边界(记录,不做)

- 未定稿区里一个**不封口的超大代码块**每帧重设一次 `Text`(O(n));流式速率下这是几百 KB/s 的量级,可接受;若将来出现 100 KB 级单块,再按行分片。
- 一个**正在长的紧凑列表**(项之间没有空行)在它自己还没写完时没有安全的定稿边界(lists 可以跨空行延长,而项之间没有空行可切),所以它在流式期间整块留在未定稿区、逐帧重解析。实测(200 项列表 + 40 行结尾段落,8 字符增量)0.22 ms/flush;一旦列表后的空行与下一行到达,列表当帧定稿。同一实测里 30 KB 的混合回复是 0.014 ms/flush。
- 用户消息不经 Markdown(见 §一)。
- 富文本只到 GFM 子集:没有脚注、定义列表、内嵌 HTML 渲染(HTML 块按代码显示,沿用既有行为),Mermaid 归 W11。
- 代码块头部沿用既有的 `CopyTextButton`(复制成功没有 toast)——与整条消息复制同一行为。

## 六·二、评审后修订(独立评审:approve + 两条非阻塞建议,均已采纳)

1. **定稿规则改为「看下一行」而不是无条件延长**(性能,评审提出的唯一实质项)。原实现把列表/缩进代码/HTML 块无条件留在未定稿区,并放弃已消费的边界;于是「列表 + 空行 + 仍在写的结尾段落」这种最常见的收尾形态会让列表每帧重解析(状态机里 `stableLine` 停在原处)。现在 `Extends(block, boundary)` 读边界之后的第一个非空行:同类列表项(`MarkdownBlockParser.StartsListItem`,新加的 internal 判定)/ 缩进 ≥4 才继续留在未定稿区,否则当帧定稿;后续内容还没到时不决定。新增 6 个用例钉住该行为(含「结尾段落一开始写列表即定稿」「半个 `-` 不关列表」「`1984` 不算有序列表项」「缩进代码按下一行决定」「围栏代码块不因后续缩进而滞留」)。
2. **`MarkdownCodeBlock.Update` 同步更新可访问名称**(评审提出的可访问性小瑕疵):语言串可能跨 chunk 到达,过去只有构造时设置名称,流式期间屏幕阅读器会读到旧语言;现在 `ApplyName` 在 `Update` 里也走一遍,空 info 时清空名称(不留硬编码回退词)。新增用例。
3. 测试强度补强(评审的次要建议):大文档的不变量断言从 `Describe`(kind/行区间/纯文本)换成 `Canonical`(含 list item 编号、表格对齐、引用子块、内联结构),与另外两份语料的断言强度一致。

## 七、验收口径

- 构建:`dotnet build Forge.slnx`(`Forge.App` 开着时 `-p:BaseOutputPath=artifacts/w4b/`,目录留在仓库内)。
- 测试:`Forge.App.Tests` 过滤 `Changes|ViewModels|Views`(覆盖 `StreamingMarkdown*`、`ChatMarkdown*`、`TranscriptCopyText`、`FrameCoalescing`、`TranscriptReplay*`)+ `Localization` + `MinorRegressionTests`(扫源码的硬门槛)。
- 手测:流式回复里出现标题/列表/表格/围栏代码块时格式正确、代码块有语言标签与复制按钮、复制内容与原文一致;复制按钮在回复结束后出现;切换标签再切回,回复内容与滚动位置不跳;长回复(>100 块)流式期间不卡顿。
