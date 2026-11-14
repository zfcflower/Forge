# W11 实现说明:流程图 / Mermaid

对应 `2026-11-13-thinkrail-gap-plan.md` 的 W11(2026-11-14 有条件批准的两个包 + 图类型白名单 + 失败回路 + 主题跟随)。
本文件是开工前交付的实现说明(方案 / 被否备选 / 四条判据 / 验收口径),收尾时补"落地结果与偏差"一节。

## 一、方案

### 1. 依赖与版本(按"稳定版 + 发布满两周"钉)

| 包 | 版本 | 为什么是这个版本 |
|---|---|---|
| `Mermaider` | **0.15.1**(2026-10-06 发布) | 0.15.2/0.16/0.17 发布于 2026-11-11/12,未满两周;0.15.1 是满足规则的最新稳定版,已含 `RenderSvg`/`AllowedDiagrams`/`ResourceLimits`/`AsciiOptions` |
| `Svg.Skia` | **4.9.1**(2026-05-20 发布) | 5.x 依赖 SkiaSharp 4.148,与 Avalonia 12.1.6 的 SkiaSharp 3.119.4 冲突(会把整个 UI 的 Skia 后端拉到 4.x);4.9.1 依赖 SkiaSharp ≥3.119.2、HarfBuzzSharp 8.3.1.3,与 Avalonia 完全同版 |

`SkiaSharp` 不在 `Forge.App.csproj` 里显式引用:它是 `Avalonia.Skia` 的传递依赖,`Svg.Skia` 只要求 ≥3.119.2,NuGet 统一到 Avalonia 的 3.119.4(评审修复时先删掉了此前误加的显式 `SkiaSharp 3.119.4` 引用,重新还原后核对 `obj/project.assets.json` 仍解析为 3.119.4,故保持删除)。
传递依赖清单(待用户确认批准范围,已同步登记到 `architecture.md` 的 NuGet 表):Mermaider → Sugiyama 0.15.1;Svg.Skia → ExCSS 4.3.1、ShimSkiaSharp / Svg.Model / Svg.Custom / Svg.Animation / Svg.SceneGraph 4.9.1。
`Mermaider` 只有一个传递依赖 `Sugiyama`(布局引擎,零依赖)与 `Microsoft.Extensions.ObjectPool`(官方 .NET 库,白名单内直接放行)。

### 2. 渲染链路

```
MdBlock(Code, info 首词 = mermaid, FenceClosed)
  → MarkdownRenderer.Render 建 MermaidBlock
  → (类型白名单 / 围栏未闭合 / 源码上限) 决定是否渲染
  → 后台线程:MermaidRenderer.RenderSvg(source, RenderOptions{ 调色板, AllowedDiagrams, Gradient=false, Elevation=0,
                                                     ResourceLimits.RenderDeadline = 超时 })
  → 预处理:MermaidSvgColors 把 color-mix(...) 解析成具体十六进制(Svg.Skia 4.9.1 不支持 color-mix,见四·1)
  → SKSvg.CreateFromSvg(svg).Picture  → SKPicture(MermaidPicture 引用计数持有 SKSvg)
  → UI 线程:MermaidDiagramView.SetPicture → 渲染线程取 Skia canvas 回放 SKPicture(矢量,不栅格化、不编码 PNG)
```

**为什么不用 `Svg.Skia` 直接吃 mermaider 的 SVG**:mermaider 的 SVG 用 CSS `color-mix()` 派生节点填充与描边
(`--_text`、`_line` 等自定义属性本身 Svg.Skia 能解析,`color-mix` 不能)。实测未解析的 `color-mix` 一律画成黑色
(菱形决策框整块变黑、字压在上面)。`MermaidSvgColors` 只做两件事:把 `var(--x[, var(--y)])` 换成我们传进去的调色板值,
把 `color-mix(in srgb, A p%, B)` 按 sRGB 通道加权算成 `#rrggbb`;**解析不了的表达式直接判失败**(绝不把黑图当成功)。
mermaider 0.15.1 在 `Gradient=false` 下只会产生 26 种 `color-mix` 形态(全部是 "hex 或 var, 百分比, hex 或 var")。
生产代码**没有**"预处理后整份 SVG 不含 `color-mix(`"的断言:预处理只改标签属性与 `<style>` 正文、文本节点原样保留(标签文字可以合法地写这几个字),所以残留检查放在测试里——默认五种类型(flowchart / sequence / state / class / er)各渲一张,断言解析后无 `color-mix(`、Svg.Skia 得到非空 picture、画在白底上有非白像素且大半不是黑色。

**为什么是矢量回放而不是 PNG/位图**:`ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` 在渲染线程拿到
`SKCanvas`,直接 `SKPicture.Playback`。好处是:2x/3x DPI 与窗口缩放自动清晰(不需要换缩放重渲染)、
单张图内存约 2 KB(`ApproximateBytesUsed` 实测 1.8 KB)而不是 1 MB 级位图、没有 PNG 编码与纹理上传。
`SKPicture` 是不可变的,多线程回放安全(已实测 8 线程同图回放,像素一致)。

### 3. 生命周期与并发

- **流式不渲染**:`MdBlock.FenceClosed` 由块解析器在 `Fence()` 里记录(闭合围栏行存在 = true)。流式期间
  未闭合的 mermaid 块只显示源码;闭合后(`Refresh` 的 `Update(..., closed: true)` 或定稿时新建控件)才发起一次渲染。
  一条回复的整棵树只在 `Complete()` 时重建一次,重建时命中缓存,不会二次渲染。
- **渲染服务**(`MermaidRenderService`):`SemaphoreSlim` 限 2 并发;缓存以请求为键(源码 + 调色板 + 白名单 + 版本),
  存 `Task<MermaidRenderResult>`(同图并发请求共享同一次渲染),最多 24 条(插入序淘汰);失败的请求不缓存。
- **超时**:`ResourceLimits.RenderDeadline` = 设置值(协作式,拦在 mermaid 自己的管线里)+ 外层 `WaitAsync` 兜底
  (库不吃取消时 UI 也能拿到失败),UI 永远不会无限等。
- **取消**:控件离开可视树取消的是**这个控件的等待**(`CancellationToken` 只停 `WaitAsync`,不取消共享的底层渲染,缓存仍要被下一次重建用到);同一控件被替换/换主题时旧结果按代号丢弃;重新挂回时按当前主题与缓存重新取。服务层"已取消的 token 抛 `OperationCanceledException` 且共享渲染不受影响"、控件层"渲染中 detach → 结果被丢弃、重挂后重新渲染"各有用例。
- **生命周期(没有引用计数)**:`MermaidPicture` 持有 `SKSvg`(= 持有 `SKPicture`),两者绑在一起,**代码里不显式 `Dispose`**,靠 SkiaSharp 终结器在控件、绘制操作与缓存都不再引用时释放原生句柄(一张约 2 KB,缓存封顶 24 条;显式释放会与可能还在回放上一帧 picture 的渲染线程竞争)。不能"取完 Picture 就 Dispose SKSvg":实测直接 `AccessViolation`。此前本文写的"引用计数、最后一个释放时 Dispose"与代码不符,以此为准。

### 4. 状态与界面

`MermaidBlockView : Border, ICopyableText`,沿用 `MarkdownCodeBlock` 的头部(语言标签 + 复制按钮)与 `Border.md-code`
外观,新增右侧"显示源码/显示图"切换按钮。正文四种状态:

| 状态 | 显示 |
|---|---|
| 白名单未启用该图类型 | 与普通代码块一致(纯源码,不报错) |
| 围栏未闭合(流式中) | 源码 |
| 渲染中 | "渲染中…" + 源码(用户可以边看边等) |
| 成功 | 矢量图(宽度超出可用宽度时等比缩到可用宽度,不放大) |
| 失败 | 错误文案(带行号时写"第 N 行")+ 源码 + 重试 + 让模型修正 |

- **主题跟随**:挂到可视树后订阅 `ActualThemeVariantChanged`;变体切换时按新调色板重渲染(调色板取自
  `SurfaceBackgroundBrush`/`TextPrimaryBrush`/`AccentTextBrush`/`TextMutedBrush`/`SurfaceMutedBrush`/`BorderBrush`)。
- **失败回路**:错误行外加一条可直接发回模型的修正请求。宿主解析顺序:最近祖先 `SessionView` 的 DataContext(精确到
  这个会话,后台标签页也不会发错会话)→ 窗口 `MainWindowViewModel.ActiveSession`(diff 预览没有会话,用活动会话);
  都没有则不显示该按钮。会话忙时消息进输入框草稿而不是抢跑一轮(与 checks 面板同一套语义)。
- **可访问性**:图控件有可读名称、切换/重试/修正按钮都有自动化 id 与名称,图本身不进 Tab 序。

### 5. 设置(第六·二节已登记,只读)

| 项 | 默认 | 位置 |
|---|---|---|
| Mermaid 启用的图类型白名单 | `flowchart,sequence,state,class,er` | `chat` 分区,逗号分隔文本框 + 合法值提示 |
| 渲染超时 | 2000 ms,钳在 [500, 30000] | `chat` 分区,数字框 |

不新增第六·二节之外的项。内部常量(不作为用户可调项):渲染源码上限 64 KiB、修正请求源码上限 16 KiB、
缓存 24 条、渲染并发 2 —— 都是实现细节/安全边界,写在本文件而不是设置页。

## 二、被否的备选

1. **只上 ASCII(计划给的退路)** —— `Mermaider.RenderAscii` 只支持 flowchart/state/xychart。默认白名单里的
   sequence/class/er 三种会**一张都渲染不出来**,默认值就成了谎话;Mermaid 图里最常见的恰恰是 sequenceDiagram。
   故只把它当 `Svg.Skia` 不通过 AOT 时的退路,不是首选。
2. **PNG 栅格化 + `Image`** —— 简单,但 2x/3x DPI 下要么糊、要么按缩放重渲染;窗口宽度变化也要重渲染;
   单图 1 MB 级位图要在缓存里管总字节数。矢量回放在这三点上全胜。
3. **5.x 的 Svg.Skia(最新)** —— 依赖 SkiaSharp 4.148,Avalonia 12.1.6 编在 3.119.4 上;NuGet 统一到 4.x 后
   Avalonia 的 Skia 后端可能直接崩(原生库与托管 API 都换了 major)。4.9.1 与 Avalonia 同版,风险最小。
4. **WebView + mermaid.js / mermaid.ink / kroki** —— 引 JS 运行时或把私有架构图发给第三方,`goal-and-requirements`
   明确不允许。
5. **自写 SVG 渲染器 / 自写 Mermaid 解析器** —— 重复造 mermaider + Svg.Skia,且失败面更大。

## 三、为什么最优(四条判据)

### 性能(第一约束)

- **UI 线程**:只做资源取色、建控件、发起一次后台渲染。渲染线程回放 `SKPicture`(µs 级),不编码 PNG、不建位图、
  不上传纹理;缩放/DPI 变化零成本。流式回复里只有"围栏闭合"那一刻产生一次渲染,不是每帧。
- **分配**:控件态每个块几个对象;绘制操作每次 `Render` 一个(与 Avalonia 自带控件同量级);缓存命中的路径不解析
  SVG、不建 Picture。
- **并发**:渲染在线程池,限 2 并发(32 核机器上也不让一次回复里的 10 张图同时抢满 CPU);缓存字典只在小临界区。
- **内存**:picture 本身 24 条 × ~2 KB ≈ 50 KB 级,但缓存键里持有**源码字符串**,上界按 24 × 64 KiB 字符(.NET 字符串 2 字节/字符)≈ 3 MB 量级;常见图只有几百字节。不缓存失败项。
- **流式热路径(M4)**:围栏未闭合时每帧 `Update` 只替换文本;状态没变就提前返回,状态图标只在失败/非失败切换时重绑。同一线程 `GC.GetAllocatedBytesForCurrentThread` 口径(`MermaidBlockViewTests.A_streaming_frame_with_unchanged_state_allocates_far_less_than_a_state_pass`,2000 帧均值,headless、仅含调用线程):**修复前 2176 B/帧,修复后 160 B/帧**(不含耗时;未加 `tools/Forge.Bench` 场景,因为被测路径是带资源绑定的 Avalonia 控件,Bench 无 UI 宿主)。

### 依赖与 AOT

- 新增两包都在 2026-11-14 批准的白名单内,版本按规则钉死;`SkiaSharp`/`HarfBuzzSharp` 与 Avalonia 同版(不新增原生资产)。
- 无自写 P/Invoke、无 Roslyn、无反射序列化;`Mermaider` 用源生成正则。
- 验证:`dotnet publish src/Forge.App -r win-x64 -p:PublishAot=true -p:TreatWarningsAsErrors=true`(本地,0 警告)
  + PR CI 的三平台 AOT 矩阵(win-x64/linux-x64/osx-arm64,`publish` job 已存在且对 PR 生效)作为最终门。

### 契约与可测性

- 不改 `Forge.Abstractions`;新增的内部契约只有:
  `MdBlock.FenceClosed`(块解析器唯一所有者)、`MarkdownRenderer` 的 mermaid 分支、
  `MermaidSettings`(ambient 投影,与 `ToolImageSettings` 同一形态,组合根赋值)、
  `IAgentMessageHost`(会话 → "把消息交给 agent"的唯一入口,checks 与 mermaid 共用)。
- 测试:纯函数(SVG 颜色解析、白名单解析、类型嗅探、失败行提取)、服务(缓存/并发/超时/失败不缓存;取消 = 服务层已取消 token 与等待中取消两条,控件层渲染中 detach 一条)、
  控件状态机(未闭合/禁用/成功/失败/重试/主题切换/修正按钮可见性/自动化 id)、
  以及一个 Skia headless 像素用例(证明真的画出了非白像素,而不是空白)。

### 回滚成本

- 新增文件全部在 `Services/Mermaid*`、`Views/Mermaid*`、`Localization/Strings.Mermaid.cs`、测试目录;
  产品改动不止最初的 4 个小 hunk:另有 `MarkdownCodeChrome`(从 `MarkdownCodeBlock` 抽出共享头部/正文,供 Mermaid 块复用)、`ChatMarkdownView` 的就地升级分支、`IAgentMessageHost` 与 `SessionWorkspace.HandMessageAsync`(checks 与 Mermaid 共用的交接入口)、`ComposerDraft.Append`、`MainWindowViewModel.Checks` 的改接,外加 csproj/spec/architecture。
  单独 revert 即回到 W4b 的纯代码块状态,无残留 schema、无持久化格式变化。

## 四、已知限制(记录,不是待办)

1. **`Svg.Skia` 4.9.1 不支持 `color-mix()`**,由 `MermaidSvgColors` 预处理解决;它只认 mermaider 输出的形态
   (`in srgb`、两个颜色、一个百分比),遇到别的形态判失败回落到"源码 + 错误",绝不会画出黑图。mermaider 版本钉死,
   升级时要先跑颜色解析的测试。
2. **字体**:Svg.Skia 走 SkiaSharp 的系统字体栈,mermaider 默认 `font-family: Inter`。应用内嵌的 Inter 不在系统字体里,
   图内文字会落到系统同族字体(Windows 实测中文正常);与 UI 的 Inter 可能有细微字形差异,不影响可读性。
3. **错误行**只能从 mermaider 的异常文本里提取(mermaider 0.15.1 不提供行列号):头部错误固定指第 1 行,
   文本里带 `line N` 时取 N,其余不猜;源码始终带行号显示。
4. **用户自写的 `style`/`classDef`** 不做 strict 模式限制(只依赖 mermaider 始终开启的 SVG 消毒);颜色值仍会被
   颜色解析器处理,解析不了就失败。
5. **主题切换**会重渲染当前可见的图(旧主题的缓存条目留着等淘汰);后台标签页的图重挂时按当前主题重取。

## 五、验收口径

- 构建:`dotnet build Forge.slnx -c Debug` 0 警告 0 错误。
- 测试(先 build,再 `--no-build`):
  - `Forge.App.Tests --filter "FullyQualifiedName~Mermaid"`(新增用例)
  - `Forge.App.Tests --filter "FullyQualifiedName~Markdown"`(回归 W4b 的渲染器)
  - `Forge.App.Tests --filter "FullyQualifiedName~Settings"`(chat 分区两项)
  - 源码扫描硬门槛:`Localization|MinorRegressionTests|KeyRegistry|StringTable|NoHardcodedChinese`
- AOT:Windows `dotnet publish src/Forge.App -r win-x64 -p:PublishAot=true`(0 警告);Linux/macOS 由 PR CI 的三平台
  `publish` job 验证(`Svg.Skia` 不新增原生资产,新增的托管程序集与 Avalonia 同版 SkiaSharp/HarfBuzzSharp)。
- 手测:浅色/深色主题各看一张 flowchart 与一张 sequence;流式中途看到源码、闭合后变图;坏图看到错误 + 源码 +
  按钮;点"让模型修正"后消息进会话。

## 六、落地结果与偏差

**交付面（最终状态）**

- **渲染链路**：`MermaidBlockView`（`Border.md-code`，头部沿用 `MarkdownCodeChrome` 的 `MarkdownCodeHeader` + 正文 `MarkdownCodeTextView`）→ `MermaidRenderService`（后台、限 2 并发、缓存 24 条、超时）→ `MermaidSvgColors`（把 `color-mix()` 算成具体颜色）→ `Svg.Skia` 解析成 `SKPicture` → `MermaidDiagramView` 用 `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` 在渲染线程矢量回放。
- **流式**：`MdBlock.FenceClosed` 由块解析器记录；未闭合只显示源码，闭合/定稿才渲染一次；围栏 info 串分段到达（先 ``` 再 `mermaid`）时就地升级为 Mermaid 块；同一条回复只在 `Complete()` 重建一次。
- **白名单**：`chat` 分区的逗号分隔列表（默认 5 种，合法 token 由 `MermaidDiagramTypes.Tokens` 列出）；不在白名单的类型保持普通代码块外观，不报错、不渲染。
- **失败回路**：源码 + 渲染器原文（`第 N 行` 只在消息里带行号或头部错误时给出）+ 重试 + "让模型修正"；修正消息交给最近 `SessionView` 的会话（diff 预览回落到窗口活动会话；忙则进输入框草稿）。
- **主题**：调色板来自 `SurfaceBackgroundBrush`/`TextPrimaryBrush`/`AccentTextBrush`/`TextMutedBrush`/`SurfaceMutedBrush`/`BorderBrush`；`ActualThemeVariantChanged` 后按新调色板重渲染，重渲期间保留旧图不闪源码。
- **设置**：`chat` 分区新增两行（图类型白名单、渲染超时），读 W0 已登记的两项偏好，不新增登记项。
- **可访问性**：图控件有名称与 `mermaid-diagram` id，切换/重试/修正按钮有 id 与随语言变化的名称。

**实际执行的验证**

- `dotnet build Forge.slnx -c Debug` → 0 警告 0 错误。
- `dotnet test tests/Forge.App.Tests --filter "FullyQualifiedName~Mermaid"` → 148 通过（服务/颜色/类型/设置/修正消息/块状态机/主题切换/缓存淘汰/Markdown 集成/diff 预览）。
- `--filter "Views|Changes|Services|Settings"` → 727 通过；源码门槛（`Localization|MinorRegressionTests|KeyRegistry|StringTable|NoHardcodedChinese`）→ 131 通过；源码门槛 + `ChecksHandOffTests` 一起 → 146 通过。
- `Forge.App.Tests` 全量（L2）→ 见下方"收尾验证"。
- `dotnet publish src/Forge.App -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true -p:TreatWarningsAsErrors=true` → 成功（原生 exe 73.8 MB），仅 Avalonia.Win32 既有的两条 ILC 信息行；依赖图确认 SkiaSharp 3.119.4 / HarfBuzzSharp 8.3.1.3 与 Avalonia 同版，未引入 SkiaSharp 4.x。
- 视觉核对：浅色中文 flowchart、深色中文 flowchart、失败面板各一张（headless Skia 帧，见临时冒烟，未留在仓库）。

**测试会话拉出的真实缺陷（已修）**

- `MermaidSvgColors.Mix` 在百分比和 < 100% 时把通道除以已被 `alphaScale` 缩放过的 alpha，颜色偏亮（`#9F009F` 而非 CSS 的 `#800080`）；改为通道除以未缩放的混合 alpha、只有回报的 alpha 乘 `alphaScale`，测试断言 `#800080CC`。

**独立评审（glm-5.3）结论：approve，五条 minor，已全部处理**

1. `Forget` 只删缓存不删插入序队列，失败重试会在 `order` 里留下陈旧位 → 改为 `LinkedList` + `Dictionary<CacheKey, LinkedListNode>`，失败时 O(1) 摘除。
2. `color-mix` 扫描作用于整份 SVG（含 `<text>` 文本节点），标签里写 `color-mix(` 会被改写或判失败 → 只解析标签属性值与 `<style>` 的 CSS 正文（文本节点原样保留），并补两条用例。
3. 状态图标每次状态迁移重复 `Bind` 且不释放旧订阅 → 保存并 `Dispose` 上一次绑定（状态图标的数据/画刷、切换按钮的图标各一条）。
4. 默认白名单的 sequence/state/class/er 没有端到端用例、Timeout/TooLarge 没有构造过 → 补：四种类型各渲一张图、`MaxLines` 超限→TooLarge、1 ms 超时→Timeout。
5. 本文件原"收尾补"小节为空 → 即本节。

**仍未验证（记录）**

- Linux/macOS 的 AOT：本机只能验 win-x64；最终门是 PR CI 的三平台 `publish` 矩阵（`Svg.Skia` 不新增原生资产，托管程序集的 IL/trim 分析平台无关）。
- 真实桌面主题下的视觉复核（headless 已用 `RequestedThemeVariant` 钉住 picture 变更）。
- `MaxInputLength` 等 mermaider 资源上限的逐条映射（只构造了 `MaxLines` 一条）。
- (评审修复后已补)Timeout/TooLarge 的界面文案:`MermaidRenderService.Renderer` 是测试缝,控件层用例直接断言标题文字(Timeout 带本次请求的毫秒数)。

## 七、收尾验证(2026-11-14 最终树)

- `dotnet build Forge.slnx -c Debug` → 0 警告 0 错误。
- `Forge.App.Tests` 全量(L2)→ **4830 通过 / 0 失败**,13 m 29 s。此前两次全量尝试各出现一次 `testhost` 原生崩溃(一次在 2852 个用例后、一次在第 116 个 `Git.StepCommitterTests` 用例处;崩溃前没有任何 Mermaid 用例执行过,Mermaid 过滤连跑 3 次全绿,`StepCommitterTests` 单独连跑 3 次全绿,同机另有一个工作区在跑测试),判定为与本波无关的环境问题;第三次在最终树上全量通过。
- 定向:148(Mermaid)、727(Views|Changes|Services|Settings)、131(源码门槛)、15(ChecksHandOff)全部通过。
- `dotnet publish src/Forge.App -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true -p:TreatWarningsAsErrors=true`(最终树) → 成功(原生 exe 73.8 MB,仅 Avalonia.Win32 既有的两条 ILC 信息行);依赖图不变(SkiaSharp 3.119.4 / HarfBuzzSharp 8.3.1.3 与 Avalonia 同版)。

## 八、评审修复(2026-11-15)

独立评审后按条修复,逐条见本节;测试只跑相关范围(见交付报告)。

- **H1** `MarkdownCodeTextView`(`ScrollViewer`)、`MarkdownCodeHeader`(`Grid`)、`MermaidBlockView`(`Border`)补 `StyleKeyOverride`,否则按类型的样式/控件主题一个都不命中(源码正文没有 ScrollViewer 模板、头部丢边距、块没有底色)。补 headless 用例:块有底色与内边距、头部有边距、源码 `SelectableTextBlock` 在可视树里且 `Bounds` 非零。
- **H2** Timeout 标题改取本次请求实际用的超时,按 `TotalMilliseconds`(原来读 `Milliseconds` 分量,2000 ms 显示成 0)。
- **M1** `MermaidFixHost.Resolve`:有祖先 `SessionView` 就只返回它的会话,不能交接则 null,不回落活动会话;只有无 `SessionView` 才回落。用真实 `MainWindowViewModel` + `SessionView` 树测。
- **M2** 重新挂树时比较当前主题调色板键与上次渲染所用的键,不同则 `keepDiagram` 重渲;补 detach → 切主题 → attach 与"主题不变不重渲"两条。
- **M3** `MermaidRenderService.HasExternalReference` + `FromSvg`(服务里 Mermaider 之后的那段):不以 `#` 开头的 `href`/`xlink:href`、`url(...)`、`@import` 一律在 Svg.Skia 之前判 Internal 失败。
- **M4** 见 §三 性能一节的数字。
- **M5** 补 `SessionHandMessageTests`(空闲跑一轮 / 忙进草稿 / 已有草稿时追加 / 被停止(撤回)进草稿 / 抛异常进草稿 / 空白消息拒绝)与块的 FixQueued、抛异常、Retry 用例。
- **L1** `SaveLayer` 的 `SKPaint` 改 `using var`。
- **L2** `FixRequest` 模板只写 `{2}mermaid\n{1}\n{2}`,围栏长度 = `max(3, 最长反引号串 + 1)`;修正消息测试改精确前后缀断言(含源码带 4 个反引号的情形)。
- **L3** 修正消息按界面语言拼、checks 消息固定英文:保持现状,写进 SPEC;服务层两条英文串(空图、外部引用)改走 `MermaidStrings`,`MermaidSvgColors` 的 problem 串保留英文,在 SPEC 标为诊断文本。
- **L4** mermaider 答"not in the allowed set"映射为 `MermaidRenderFailure.Disabled`(块保持普通代码块);同时让本地嗅探也认 mermaider 接受的不带 `-beta` 的头(`sankey`/`xychart`/`packet`/`block`/`architecture`/`treeView`/`requirement`)。注意:评审举的 `flowchart-elk` 在 0.15.1 里是"Invalid mermaid header"(mermaider 根本不认),仍是头部错误,不是 Disabled。
- **L5** 定稿后未闭合围栏:**选了"在 SPEC 的 Mermaid 未决里写明"**,没改块解析器(`FenceClosed` 的语义牵涉流式定稿与 diff 预览,改动面大)。
- **L6** 本文 §一.1/§一.2/§一.3/§三 与缓存内存已按代码修正。
- **M6** `architecture.md` NuGet 表 Svg.Skia 行改为如实措辞并补列传递依赖;`Forge.App.csproj` 的显式 `SkiaSharp` 引用已删(解析结果不变)。
