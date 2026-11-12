# Forge 对比 ThinkRail 的功能缺口与第一批实施方案

日期:2026-11-12

## 方法与可信度

- ThinkRail 侧:从本机安装包(`%LOCALAPPDATA%/ai.thinkrail.app/stable/app/Resources/app/views/web/assets/*.js`)抽出约 3000 个界面文案键,按前缀归成约 60 个功能区。文案键只说明"有这个界面",不说明实现深度。
- Forge 侧:按 SPEC(含「未决」节)、`Localization/Strings.*.cs` 与源码做名字/关键词核对;第一批涉及的结论已在源码复核。
- 复核后修正的两处:
  - ThinkRail 的 `shell.location.remote.*` 是 Git 远端的领先/落后读数,不是"远程工作区";Forge 已有(`src/Forge.App/SPEC.md` 远程读数一节)。
  - "逐行评审评论"在 Forge **已实现**(文件预览与 diff 行评论、草稿、单条/批量发送、锚点重定位、`review_comments`/`review_comment_resolve` 工具);`src/Forge.App/SPEC.md` 未决节里"评审面板仍只读"一句已过时。

## 缺口清单

### 一、核心体验

| # | 缺口 | ThinkRail | Forge 现状 |
|---|---|---|---|
| 1 | 图片输入 | 提示词粘贴/拖入图片;工具返回图片可显示 | 无。消息模型没有图片块(`ContentBlock` 只有文本/思考/工具调用/工具结果/artifact 引用) |
| 2 | Ask-user 工具 | Agent 以卡片向用户提结构化问题,答完继续 | 无 |
| 3 | Web 搜索/抓取 | `webSearch`/`webFetch`,结果留存(`webStored`) | 无 |
| 4 | 上下文压缩 | 手动压缩 + 阈值自动压缩 | 有 `SummaryCompactor`,但 `AgentOptions.CompactionThresholdTokens` 从未被设置,**自动压缩实际从不触发**;无手动入口;继承前缀超阈值时会重复压缩(SPEC 已记为设计缺陷) |
| 5 | 逐行评审评论 | 行评论、线程、过期标记、批量发送 | **已实现**(见上)。剩余小差距:ThinkRail 的评论线程(Agent 回复挂在评论下)与过期标记的独立视图 |
| 6 | 全局搜索 / 命令面板 | 文件、符号、命令、会话一处跳转,带预览 | 无,搜索框为占位 |
| 7 | Plan 会话 | 计划面板、逐项状态、修订、全部检查、下一步、从计划开 PR、导出 | 部分:TODO 分组 + PR 正文里的 plan_summaries |

### 二、Git / PR 与编辑器

8. Ship 全流程(提交、推送、开 PR、ready、修复 checks、合并 PR/本地合并、删除远程分支、收尾工作区):部分。PR 服务随 Git 面板移除,只剩片段;CI 只有 `CiChain` 自动化,无 checks 面板。
9. 编辑器 LSP 接入 UI:部分。`Forge.Editor/Lsp` 后端已在,诊断/语义 token/补全未接 UI,编辑模式为无着色 TextBox(SPEC 未决)。
10. 富文件查看器:无。PDF、notebook、图片、CSV、SVG、HTML 预览、LFS 指针、二进制占位、图片可视化 diff;目前只有 Markdown 预览。

### 三、MCP 与扩展

11. MCP 管理界面:部分。仅只读启用/禁用列表;ThinkRail 有增删改、导入、测试、重连、日志、预设。
12. MCP 审批与信任:无。审批对话框(命令/cwd/env/头)、审批后变更提示、单工具始终允许与撤销、按项目/会话覆盖、工具搜索上限。
13. MCP OAuth、resources/subscribe、progress:无(`src/Forge.Mcp/SPEC.md` 未决)。
14. 扩展包仓库:部分。无浏览/安装/检查更新,不区分内置/捆绑/已安装。

### 四、应用级与设置

15. 自动更新:无(检查、下载、安装重启、安装日志、失败恢复)。
16. 首次启动引导 + 克隆/新建项目:无。
17. 资源检查器(运行中命令与子 Agent、耗时、日志尾、停止):部分,`SessionInstruments` 雏形。
18. 零散设置:模型可见性过滤、子 Agent 并发上限/开关、聊天显示(输入框大小、消息顺序、行宽)、快捷键页、工作区闲置自动归档、通知权限引导。

### 五、较小或低优先级

消息复制、会话内导航(breadcrumbs)、会话用量/耗时统计;Mermaid 与可视化卡片;终端缓冲区搜索与重启已退出终端(SPEC 已推迟);评审包与请求评审工具;布局设置对话框。不必跟进:隐私统计/问卷/反馈、JetBrains Central。

---

## 第一批:实施方案

范围:图片输入、Ask-user、Web 工具、压缩策略、内置工具目录与开关(用户补充,替代已实现的第 5 项)。

共同约束:Native AOT(无反射序列化,JSON 走源生成上下文)、性能第一(热路径不分配大对象、不阻塞 UI 线程)、NuGet 白名单不扩张(HTML 转文本等自行实现)、扩展点契约放 `Forge.Abstractions`、依赖方向 App → Core → Abstractions 不反转。

### A. 工具目录:内置工具可开关,与扩展工具分开

**问题。** 现在工具来源有四处:Core 内置(检索、`todo`、子 Agent)、App 内置(Git、文件、PR,以及将加入的 Web、ask_user)、扩展宿主的插件工具(含 `run_code`)、MCP 工具;它们在 `SessionRuntime.CollectExternalTools` 里拼成一个列表,UI 只能列名字,用户无法区分"产品自带"与"自己加的",也不能关掉内置工具。

**方案。**

- 契约(`Forge.Abstractions/Tools.cs`):
  - `enum ToolOrigin { BuiltIn, Extension, Mcp }`。
  - `sealed record ToolDescriptor(string Name, ToolOrigin Origin, string Group, bool CanDisable, string? Source)`:`Group` 是内置工具的分组(`files`/`git`/`pr`/`web`/`session`/`agents`/`retrieval`),`Source` 是插件名或 MCP 服务器名。
  - 不改 `ITool` 接口(插件 SDK 已依赖它,改接口会打断所有扩展);来源由**注册方**声明,而不是工具自报。
- Core:`ToolSurface` 分三层登记——内置(带 descriptor)、外部(扩展/MCP,descriptor 由 App 给)、禁用集合;对 loop 只暴露"启用的"注册表。外部工具永不覆盖同名内置工具(现有规则保持)。
- 设置:`settings.json` 新增 `tools.disabled: string[]`(只记内置工具名;扩展工具的启停仍走插件级启停,MCP 走服务器启停,不在这里重复一套)。缺省为空 = 全开。
- `CanDisable=false` 的极少数:无。`todo`、检索工具也可关,但关检索工具时设置页提示"压缩后将无法回读原文"。
- 生效:改设置后走 `AgentHost.ReplaceExternalTools` 同一路径,运行中的 loop 在下一次模型请求前拿到新集合(会打断一次提示词缓存,与插件热加载一致)。
- 设置页「工具」分区重做为三组:**内置工具**(按 Group 分组,每行开关 + 一句说明)、**扩展工具**(按插件分组,只读,跳到扩展分区启停)、**MCP 工具**(按服务器分组,只读,跳到 MCP 分区)。不再依赖"必须有活动会话才能列出"。

### B. 压缩策略:按模型窗口与全局设置计算阈值

**现状问题。** 阈值是一个全局绝对数且未设置 → 不触发;即便设置,一个绝对数对 200K 与 1M 窗口的模型都不对;子 Agent 可能用不同模型;用量用的是"4 字符 ≈ 1 token"的估算,与服务端真实计数可能差 30% 以上。

**窗口来源(复用现有链,不新造)。** 每个模型的有效窗口 `W` 沿已有的解析链得到:`目录快照 → 提供商上报 → 全局默认上下文窗口(绝对值或百分比) → 每模型覆盖`(`ContextWindowDefault` + `ModelMetadataResolver`)。全局默认窗口小于目录值时,`W` 就是用户想要的上限(省成本/配额)——压缩阈值天然跟着它走。

**算法。**

1. 预留输出:`R = min(模型最大输出 或 MaxOutputTokens, 0.2·W)`,至少 4K;可用输入 `U = W − R`。
2. 当前占用 `C`:优先用**上一次响应的真实输入用量**(`input + cacheRead + cacheWrite`),再加上一次请求之后新追加消息的估算值;没有真实用量(首轮、提供商不报)才全量估算。真实用量按 (实例, 模型) 记,换模型后作废。
3. 触发:`C ≥ trigger · U`,`trigger` 默认 0.85。在**请求前**检查,不在每轮无条件执行。
4. 目标:压到 `target · U` 以下,`target` 默认 0.5(沿用 `CompactionTargetRatio`);始终保留最近一次完整交互(用户消息 + 其后的工具调用/结果)原样,不拆工具调用与结果对(沿用 `ExtendOverToolResults`)。
5. 迟滞,修正继承前缀的重复压缩:压缩后若仍 ≥ 触发线(例如继承前缀本身就超),记下这次的 `C`,在本会话自有内容再增长 `≥ 0.1·U` 之前不再尝试;连续两次无收益则发一条"上下文无法再压缩"的提示并停止自动尝试,直到用户手动压缩或换模型。
6. 兜底:提供商返回上下文超限错误(Anthropic `prompt is too long`、OpenAI `context_length_exceeded` 等,归一到 `ProviderErrors` 的一个分类)时,强制压缩一次并重试本次请求一次;再失败则如实报错。
7. 窗口未知(`W` 解析不出):不自动压缩,只靠第 6 步兜底;状态栏显示"窗口未知"。

**契约。** `AgentOptions.CompactionThresholdTokens`(绝对数)替换为 `CompactionPolicy`:`Func<ModelSettings, int?> ContextWindow`(由 App 注入,复用解析链)、`TriggerRatio`、`TargetRatio`、`Enabled`;Core 只做算术与调度,不知道目录与设置。

**设置。** `settings.json` 的 `compaction` 段:`enabled`(默认 true)、`triggerPercent`(默认 85,允许 50–95)、`targetPercent`(默认 50,须小于 trigger)。设置页放在「模型目录 / 默认上下文窗口」旁边,文案说明"阈值按每个模型的有效窗口计算,受上面的默认上下文窗口约束"。

**手动与可见性。** 命令 `/compact` 与输入框旁的上下文用量指示(`C / W`,超过触发线变色,点击即手动压缩)。手动压缩无视阈值,但仍遵守"保留最近一次交互"。

### C. 图片输入

- 契约:`ImageBlock(string MediaType, string Data)`,`Data` 为 base64;支持 `image/png`、`image/jpeg`、`image/gif`、`image/webp`。
- 入口:输入框粘贴剪贴板图片、拖入图片文件、附件按钮;发送前显示缩略图,可逐个移除。单张解码后长边超过 2048px 时按比例缩到 2048px,编码后超过 5MB 拒绝;每条消息最多 10 张。缩放用 Avalonia 自带位图能力,不引入图像库。
- 能力门禁:当前模型 `ModelCapabilities.Inputs` 明确不含 `Image` 时禁止附图并提示;未知时允许发送,由提供商报错兜底。
- 线格式:Anthropic `{"type":"image","source":{"type":"base64",...}}`;OpenAI 兼容 `{"type":"image_url","image_url":{"url":"data:<mime>;base64,..."}}`;Responses `{"type":"input_image","image_url":"data:..."}`。
- 存储:`MessageJson` 序列化 `ImageBlock`;摘要/检索文本里以 `[image: <mime>, <KB>]` 代替数据,压缩器不把 base64 送进摘要请求。
- 计数:估算器把每张图按 `≈ 1600` tokens 计(真实用量回来后以真实值为准,见 B)。
- 第一批不做:工具返回图片(`chat.toolImages`)——它需要 `ToolResult` 契约扩成多块,单独排期。

### D. Ask-user 工具

- 契约:`IUserQuestionHandler.AskAsync(UserQuestionRequest, CancellationToken) → UserQuestionResponse`;请求含会话 id、Agent 标识、1–4 个问题(每题:标题、可选项 2–6 个、是否多选、是否允许自由输入);响应为每题的所选项/自由文本,或"用户拒答/取消"。
- Core:`ask_user` 内置工具,`ToolSideEffect.ReadOnly`,不走审批(它本身就是向用户发问);handler 未注入(测试、无 UI)时工具不注册。子 Agent 也可调用,卡片标出是哪个 Agent 在问。轮次取消 → 工具返回"已取消",不挂死。
- App:卡片放在与审批卡同一个"等待用户"区域(输入框上方的待处理队列),和审批共享计数与系统通知("Agent 在等你回答");答完卡片折叠进转录为一行摘要。

### E. Web 工具

- `web_fetch`:仅 http/https;最多 5 次重定向;30 秒超时;响应体上限 5MB;HTML 转 Markdown 风格纯文本(自实现,去 script/style/nav,保留标题、列表、链接、代码块),JSON/纯文本原样,二进制拒绝;长输出照常进 artifact 存储。拒绝回环/内网地址(防 SSRF),可在设置里关掉这条限制。
- `web_search`:后端可插拔 `IWebSearchBackend`,第一批实现 Brave Search API、Tavily、SearXNG(自建 URL);密钥进加密密钥库,不写 `settings.json`。未配置后端时 `web_search` 不注册(而不是注册一个必然失败的工具)。结果统一为 `[{title, url, snippet}]`。
- 审批:两个工具声明为 `ReadOnly`,但网络出站有数据外泄风险(提示注入可把内容拼进 URL),默认规则 `Ask`,用户可设"始终允许";二者都属内置工具,可在 A 的目录里关掉。
- 依赖:只用 `HttpClient`,不引入 HTML 解析包。

### 实施顺序与分工

1. 主 Agent 先定稿 `Forge.Abstractions` 契约:`ToolOrigin`/`ToolDescriptor`、`ImageBlock`、`IUserQuestionHandler` 及其记录、`ChatRequest`/`AgentOptions` 的压缩策略形状。
2. 四个互不依赖的实现并行:C 图片、D ask_user、E Web 工具、B 压缩;各自只动归属路径。
3. 主 Agent 自己做 A(工具目录)与接线:`SessionRuntime`/`AgentHost`/`AppComposition` 的注册与设置读写,这些是 B–E 的共同落点,不拆给 Worker。
4. 最后统一更新 SPEC(含删除过时的"评审面板仍只读")。
