# W4 实现说明(工具图片 + 富查看器)

对应 `2026-11-13-thinkrail-gap-plan.md` 的 W4 波次。本文是开工前应交付的实现说明 + 落地后的实测记录;落地与验证在同一次提交序列里完成,数值是实测值。

## 一、方案

### 1. 工具结果的图片(契约 → 流水线 → 界面)

| 层 | 文件 | 做法 |
|---|---|---|
| 契约 | `Forge.Abstractions/Tools.cs`、`Messages.cs`、`ToolPayload.cs`(新) | `ToolResult`/`ToolResultBlock` 从单串变成**块列表** + 两个文本读法:`Text`(工具自己的话,只含 `TextBlock`)与 `Content`(整份结果的文本视图,非文本块各占一行 `[image: <type>]`);`Images` 预计算。块列表构造时拷贝,`Equals`/`GetHashCode` 手写为逐块比较(编译器合成的数组是引用相等)。`(string Content, bool IsError)` 构造保留,所以 **~120 个既有调用点一行没改**。`ChatRequest.IncludeToolImages`(默认 true)告诉适配器这一轮能不能发图。 |
| MCP | `Forge.Mcp/McpModels.cs`、`McpContentFlattener.cs`、`McpToolAdapter.cs` | `McpContent` 加 `Data`;`image` 块在**可显示媒体类型 + 解码后 ≤ 5 MiB**(安全边界常量)时保留 base64;适配器返回 `[TextBlock(去掉已变成图片的那几条占位), ImageBlock…]`,纯文本结果与旧输出逐字节一致。 |
| 存储 | `Forge.Core/Storage/MessageJson.cs` | `tool_result` 仍写 `call_id`/`content`/`is_error`(旧读者照旧),**只在**块数 > 1 或含非文本块时追加递归 `blocks` 数组;读时两种形状都认。无 schema 变更。 |
| 计费 | `Forge.Core/Agents/TokenCounter.cs`、`Compaction.cs` | 嵌套图片按顶层 `ImageBlock` 同样的 1600 token 计;摘要器给模型看的仍是 `MessageJson.ImagePlaceholder`(带大小、绝无 base64)。 |
| 循环 | `Forge.Core/Agents/AgentLoop.cs`、`AgentOptions.cs`、`App/Agents/SessionRuntime.cs`、`Composition/AppComposition.cs` | 工具结果按块回写;`SpillAsync` 只把**文本**搬进 artifact、图片留着;`BuildRequest` 每请求算一次 `IncludeToolImages` = 用户开关(`AgentOptions.SendToolImagesToModel`)× 该模型目录口径(`AgentOptions.ModelInputs`,未知视为可收图,与输入框对用户附件的规则一致)。 |
| 适配器 | `AnthropicProvider`、`OpenAiResponsesProvider`、`OpenAiCompatibleProvider` | Anthropic 写 `tool_result.content` 的**块数组**(`text` 取 `Text`,随后 `image`);Responses 写 `function_call_output.output` 数组(`input_text` + `input_image`);Chat Completions 的 `tool` 角色只能是文本,所以结果文本(带占位)照常进 `tool` 消息,图片随后作为**一条用户消息**(一行说明 + 每张 `image_url`)。文本结果三条线路的字节都不变。 |
| 界面 | `App/ViewModels/Transcript.ToolImages.cs`(新)、`Transcript.cs`(一行钩子)、`App.axaml` | `ToolRowViewModel` 的图片半边是 `partial` 方法实现,行内 `WrapPanel` 缩略图;缩略图首次读取时后台解码,长边取 `AppPreferences.ToolImageThumbnailEdge`(64–512,默认 256),`IImageCodec.Thumbnail(data, maxLongEdge)` 用**默认接口成员**加,旧实现与测试假件不必改。回放经 `TranscriptReplay` 用 `Blocks` 重建 `ToolResult`。 |

### 2. 富文件查看器

`FileDocumentViewModel` 从 `Panels.cs` 拆到独立文件(W0 未落地的遗留项,本波一起做),加载路径改为**先分类后读**:`FilePreviewClassifier.Classify(path, firstBytes)` 是纯函数,`LoadAsync` 只喂前 512 字节。

- **LFS 指针最先判**(内容前缀,不看扩展名)——被 LFS 管的 PNG 在磁盘上就是文本指针。
- 其后按扩展名:图片 / `.csv .tsv` / `.ipynb` / `.pdf` / `.html .htm`;其余走原文本路径,`TextDocument.LoadAsync` 抛 `NotSupportedException` 时才出二进制占位(超 8 MiB 仍是原来的“文件过大”)。
- **只读**:`HasDocument` 为 false,编辑器与换行/保存/复制/编辑菜单一起隐藏,`IsDirty`/`CanSave` 恒 false。
- **PDF/HTML 不做内部渲染**:占位卡 + 「用默认程序打开」(`Process.Start(UseShellExecute)`);HTML 另给「查看源码」按文本重开,不渲染也不丢能力。
- 图片:后台解码,长边 > 4096 先 `CreateScaledBitmap` 再释放原图;解码器可注入,无头测试不碰渲染。
- 表格:真 RFC 4180(引号、成对引号、CRLF/LF、字段内换行、参差行补齐);TSV 不认引号;超上限照常流式读完只计数;a11y 行虚拟化 `VirtualizingStackPanel`,列宽按前 200 行最宽单元格算,表头与数据行放在**同一个横向 ScrollViewer** 里,宽表整体横向滚动、列不歪。
- 笔记本:`nbformat` 四种输出(`stream`/`execute_result`/`display_data`/`error`),每段按字符上限截断并标记,`image/png` 按需解码,坏 JSON 出提示不出异常。
- 释放:换文件、取消、被后续加载顶掉三条路径都 `Release()` 位图(竞赛失败那一份也释放)。

### 3. 设置(第六·二节登记项 → 界面)

五项配置来自 **W0b 已注册的登记表**(本波只读、不再自己加字段):`ChatToolImageThumbnailPixels`(64–1024,默认 256)、`ChatToolImagesToModel`(默认开)、`FilesCsvMaxRows`(100–100000,默认 1000)、`FilesNotebookOutputKilobytes`(1–1024,默认 20)、`FilesImagePreviewMaxMegabytes`(1–256,默认 20);分区 id/order 也用 W0b 分配好的 `SettingsSectionIds.Chat`(order 320,`IconChat`)与 `.Files`(order 360,`IconEye`)。视图模型直接写偏好、下一次动作即生效,组合根 `FilePreviewSettings.Current` 与 `ToolImageSettings.ThumbnailEdge` 把偏好透给查看器与工具行(笔记本以 KB 登记、预览按字符截断,图片上限以 MB 登记、预览按字节比较,单位换算只在 `FilePreviewSettings` 一处)。

## 二、被否的备选

| 决定 | 被否的备选 | 为什么输 |
|---|---|---|
| 工具结果是块列表 | 在 `ToolResult` 上加一个 `Images` 字段 | 不能表达顺序,下一个类型(文件、artifact)又要加字段;而且图片与文本的相对次序恰恰是模型读图时唯一的结构信息。 |
| 单 `Content`(含占位) | 只要 `Content` | Anthropic/Responses 能原生内联图片时,`Content` 里的 `[image: …]` 会让模型同时看到占位与图,同一张图说两遍、白花 token。所以 `Text`(自己话)与 `Content`(带占位)分开,谁送图谁用 `Text`。 |
| Chat Completions 追加用户消息 | 把图片转成 base64 塞进 `tool` 消息文本 | 协议规定 `tool.content` 是文本,塞 base64 等于把几 MB 文本喂给模型:又贵又读不懂。 |
| Chat Completions 追加用户消息 | 干脆不发图,只留占位 | 那就等于在 Chat Completions 上退回缺口 #1 的现状,一个常见协议永远看不到工具图。 |
| 是否发图按模型口径判断 | 无脑发,失败再降级重试 | 文本模型遇到 image 部件直接 4xx,一轮对话白跑;重试要重建请求还要记住降级状态。目录已知就用,未知才放行,与输入框对用户附件的规则同一条。 |
| MCP 图片保留原 base64 | 解码后重新编码成 PNG 统一格式 | 需要在 `Forge.Mcp` 引图像编码器(白名单外),还会毁掉 GIF 动画与原图质量。保留原字节,只做「能不能显示 + 多大」两道判断。 |
| 存储加 `blocks` 数组 | 加一列 / 升 schema | 本波明令不碰 schema;JSON 里多一个嵌套数组,旧读者忽略即可,新读者按需读,零迁移。 |
| 查看器先分类后读 | 读完文本失败再按扩展名兜底 | 先读会把 20 MB 图片当文本读进内存再报错;分类是纯函数,先看 512 字节就能决定走哪条路。 |
| 表格行虚拟化 + 预计算列宽 | 整表画成一段等宽文本 | 1000 行 × N 列的 `TextBlock` 是几十万字符的单控件,布局与选择都退化;等宽文本还丢列对齐。 |
| 表格外层横向 ScrollViewer | 禁止横向滚动(让单元格省略) | 宽 CSV 的右半边列不可达,这不是可用性问题而是缺陷。 |
| PDF/HTML 占位 + OS 打开 | WebView / PDF 渲染库 | 引 JS 运行时或原生 PDF 库,与「能交给 OS 的不引渲染器」直接冲突。 |
| 指针文件按内容判 LFS | 按 `git lfs` 命令或 `.gitattributes` 查 | 那要读仓库状态(`.gitattributes`、LFS 存储),查看器不该依赖仓库;内容前缀是文件自身的事实。 |

## 三、为什么这是最优(按四判据)

- **性能。** UI 线程只做属性读取与提交布局:解码与解析都在 `Task.Run` 里(缩略图首次读取才解、图片预览超 4096 先缩放、CSV/笔记本在后台线程);列表都虚拟化;工具行图片是**惰性**解码,长会话回放不会一次解出全部;`AvaloniaImageCodec` 的缩略图路径改为「按宽解码一次,超界才按高再解一次」,避免为了判断朝向而先做一次全尺寸解码(20 MB 截图原本要瞬时分掉近 100 MB 像素);`TokenCounter` 与存储的额外开销只在带图结果上出现,纯文本路径字节不变、分配不变;热路径没有新增锁(块列表构造时拷贝一次,之后只读)。
- **依赖与 AOT。** 零新包:图片解码用 Avalonia 自带 `Bitmap`,表格/笔记本/LFS 都是手写纯逻辑;`IImageCodec` 的扩展是**默认接口成员**(无运行时派发代码生成),`PreferencesSection` 走已有 source-generated JSON 上下文。`dotnet build` 全解决方案 0 警告 0 错误(仓里 `TreatWarningsAsErrors`),AOT 分析器无新告警。
- **契约与可测性。** 唯一所有者:`ToolResult`/`ToolResultBlock` 在 `Forge.Abstractions`,谁要图谁读 `Images`,谁只会文本谁读 `Content`;「发不发」是 `ChatRequest.IncludeToolImages` 一处的决定,适配器不各自判断。证明该行为的测试:契约 7 个(`ToolPayloadTests`)、三条线路 11 个(`ToolImageWireTests`)、循环与开关 8 个(`ToolImageTurnTests`)、MCP 9 个、存储/计费/压缩 9 个、App 侧 168 个(工具行图片、预览、两个分区、偏好持久化),外加分类器/CSV/笔记本/LFS 的纯逻辑测试。分类、读取、渲染三层各自可单独测。
- **回滚成本。** 三处独立提交,任一可单独 revert:契约+适配器(行为:能发图)、MCP+存储(行为:能收图)、App(行为:能看)。查看器与两个设置分区在第三处,不依赖前两处仍能编译运行(它们只读 `AppPreferences` 与文件系统)。没有 schema 迁移、没有配置格式变更(新键缺省即旧行为),所以回滚不需要数据修复。

## 四、验收口径(实测)

合并 `main`(W0a/W0b)之后的最终数值:

```
dotnet build Forge.slnx                                   → 0 警告 0 错误
dotnet test tests/Forge.Core.Tests                        → 2136 通过 / 0 失败
dotnet test tests/Forge.Mcp.Tests                         → 190 通过 / 1 失败(既有环境问题,见下)
dotnet test tests/Forge.Workspace.Tests                   → 1312 通过 / 0 失败
dotnet test tests/Forge.Integration.Tests                 → 265 通过 / 0 失败
dotnet test tests/Forge.App.Tests                          → 见「全量 App 套件」一行
dotnet test tests/Forge.App.Tests --filter Workbench      → 575 通过 / 0 失败
dotnet test tests/Forge.App.Tests --filter Preview|Transcript|Settings|Localization|MinorRegression → 590 通过 / 0 失败
```

`Forge.Mcp.Tests` 的那一条失败是 `StdioTransportTests.Timed_out_request_sends_cancellation_and_handshake_messages_arrive_on_stdin_as_lines`(`initialize did not complete within 3s`);在干净的 `main` 检出(`D:/FORGE/forge` @ 5dd2068)上跑同一条测试同样失败,所以是既有环境问题,与本波无关。

手测项(需真人看):工具行缩略图的观感与悬停提示;宽 CSV 的横向滚动与列对齐;大图(>4096 长边)预览的缩放质量;PDF/HTML 卡片的「用默认程序打开」在 Windows 上真的开对程序;HTML 的「查看源码」切回文本。

## 五、与计划的偏差

1. **W0 落地晚于本波开工**:开工时 `main` 上还没有 W0,所以本波先在自己分支上补做了块契约、`FileDocumentViewModel` 拆分、偏好与分区 id(否则 W4 无法开工);随后 W0a/W0b 合入 `main`,本波把 `main` 合进分支并以 **W0 的形状为准**:契约用 `ToolResultBlocks`/`ContentPlaceholders`(本波自己的 `ToolPayload` 已删除),拆分用 W0 的 `FileDocumentViewModel.cs`/`Transcript.Tools.cs`,偏好与 section id 用 W0b 的登记表,工具行改为实现 W0 留好的 `CaptureImages` 钩子。合并后 `git diff main..HEAD` **不再删除任何 W0 文件**(已核对 `--diff-filter=D` 为空)。本波在 W0 之上只多两件契约读法:`Text`(工具自己的话,不发占位)与 `HasImages`。
2. **`Text` 与 `Content` 两个读法**是对计划「`ToolResult` 保留 `string Content`」的细化:计划只说保留便捷构造;实测发现只有 `Content`(带占位)时,能内联图片的适配器会让模型把同一张图看两遍,故补 `Text`。
3. **缩略图长边偏好**用「不要超界」语义:只在解码时约束,不重编码存下的字节(重编码会毁掉原图质量与动画 GIF)。
4. **HTML 占位多一个「查看源码」**:计划只写占位 + OS 打开,但那样会丢掉原本能读 `.html` 源码的能力,等于回退;加一个按钮即不动「不引渲染器」的决定。
5. **工具行模板留在 `App.axaml`**:计划让 W0 先把 `ToolRowViewModel` 的模板拆到 `Resources/ToolRow.axaml`(W4 缩略图 + W8 日志尾共用)。W0 未落地,而 W8 不在本轮,所以缩略图直接加在 `App.axaml` 现有的 `ToolRowViewModel` 模板里;将来 W8 要拆时按段拆即可(日志尾在结果区之后、缩略图之前)。
6. **表格表头与数据行共用外层横向滚动**:计划未提,属实现细节;禁止横向滚动会让宽表右列不可达,按缺陷处理。

## 六、未做 / 已知限制

- 工具行缩略图不可点开大图,也不支持缩放/平移(计划只要求缩略图)。
- 图片 diff(计划把它列在 `Changes/**`,属 W4b 的文件域)、SVG 内部渲染(需白名单外的 Skia SVG 包)不在本波。
- `pr_screenshots` 之类的工具还没改用「把截图作为图片返回」,它仍只把图片推到 PR;流水线已就绪,换用是下一步的事。
- 无头测试不覆盖真实解码与文件选择器;上面「手测项」需要真人过一次。
