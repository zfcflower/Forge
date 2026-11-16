# 06 编辑器与代码智能

本文件说明文件预览/编辑、TextMate 词法着色、LSP 客户端、诊断与问题面板、富文件查看器、行评审与可重绑的键位。

## 6.1 文本模型与保存

- 编辑控件是 AvaloniaEdit 12.0.0（Rope、不可变快照、撤销分组、文本锚点复用），`Forge.Editor` 提供语言服务、词法着色、Diff 模型与原子编码保存。
- 保存流式且原子：按快照 rope 分块写同目录 `<文件名>.<guid:N>.tmp`（`CreateNew` + `FileShare.None`）→ `Flush(flushToDisk: true)` → `AtomicFile.ReplaceFileAsync` 发布；失败或取消删临时文件、原文件字节不变；目标被占用时有界重试，只读/目录立即失败。
- 编码：先按 BOM 判 UTF-8 / UTF-16 LE/BE / UTF-32 LE/BE，无 BOM 探宽编码，都不命中回退 Latin1；含 NUL 等判二进制不作文本读。写回保留原 BOM 有无与编码。
- 行尾：按 LF/CRLF/CR 计数取主导；保存默认**不**规范化（`NormalizeLineEndingsOnSave` 默认关），开启时按主导行尾重写。
- 文件超过 8 MiB 明确拒绝（不再静默截断 20000 行）；超过 1 Mi 字符跳过 TextMate，但仍可显示、编辑、保存完整文本。
- 未保存关闭/替换需确认；多文件确认全部通过且内容未变才继续；保存中禁止替换/关闭；归档不保存也不丢弃仍打开的编辑。保存记录实际写入的快照版本，保存期间的新编辑保持脏。

## 6.2 TextMate 词法着色

- TextMate 组件内化自 Tint（MIT），Oniguruma 正则翻译为 .NET 正则（不内化 GregsEx，体积过大）：翻译不了的模式报诊断并不匹配；include 规则展开带环检测与深度上限。
- 增量与全量结果一致：状态相等性覆盖影响后续分词的全部字段（含 `BeginRuleCapturedEol`）。
- 用户语法可放在 `~/.forge/grammars` 与 `<workspace>/.forge/grammars`。
- 分词在 span 上零分配匹配；仅当命中规则需要捕获组时才把缓冲物化为字符串；单次搜索 500 ms 预算，超预算**先重试一次**（首次搜索有解释器启动成本），仍超时才当作不匹配并记诊断。
- 字符串与快照高亮把 LF/CRLF/CR 都视为换行、行内容不含换行符。

## 6.3 LSP 客户端

- 自写 JSON-RPC over stdio（`Content-Length` 分帧，收发头缓冲分离）；同步泵按文档维护单一有序状态，增量变更携带不可变截止快照；功能请求前建立同步屏障。
- 服务器请求 id 按 JSON 类型原样回传；支持 `.cmd`/`.bat` 的 `cmd /d /s /c` 引号规则；进程工厂可注入以便测试。
- 会话状态机以 initialize 握手为界：启动中观察到服务器退出（进程退出事件早于或晚于连接关闭都一样）落到 `Unavailable` 并记 `failed to initialize`；`Stopped` 只表示已 Running 的服务器崩溃、等待重启。

### 服务器来源（有界、离线）

1. PATH 上的已知命令：`roslyn-language-server` / `csharp-ls` / `OmniSharp`、`typescript-language-server`、`vscode-{json,css,html}-language-server`、`pyright-langserver` / `pylsp`、`gopls`、`rust-analyzer`、`clangd`、`bash-language-server`、`yaml-language-server`、`lua-language-server`。
2. VS Code / Cursor 扩展目录（`ms-dotnettools.csharp-*` 的 Roslyn LSP、`.roslyn` 同名片、`omnisharp/OmniSharp`），每个扩展最多看 32 个版本、按版本号取最新。
3. VS Code 安装目录自带的 JSON/CSS/HTML 服务（用 `node` 跑 `*ServerMain.js`），按根 / `resources/app` / `<版本>/resources/app` 三种布局展开。

- 只做存在性检查：不下载、不安装、不执行探测命令；不存在的目录直接跳过。
- 命中后静默使用，并在编辑器状态行写明“语言服务:<服务器名>(来源)”；找不到时说明已查过 PATH 与 VS Code 扩展/安装目录。
- 用户在 `languages.json` 里配置过的语言（包括显式配成无服务器或 `disabled`）一律不被发现结果覆盖；未被配置的语言继承发现结果与扩展名表。
- 带注释的 JSON 用独立 `jsonc` 语言（`.jsonc`、`tsconfig.json`/`jsconfig.json`/`devcontainer.json`、`.vscode` 下任意 `.json`）。
- TypeScript 带 `initializationOptions.tsserver.path`（npm 全局 → npm prefix → 编辑器自带），找不到就不带。
- Roslyn 另带 `--autoLoadProjects 100`，否则 `.cs` 停在“杂项文件”，没有诊断/悬停/跳转；同时声明 `window.workDoneProgress` 与 `workspace.diagnostics.refreshSupport`。
- 不使用 Pylance（许可只允许在 VS Code 内使用）。

## 6.4 代码智能能力

| 能力 | 入口 | 行为与限制 |
|---|---|---|
| 诊断 | 波浪线 + 行尾按严重度着色圆点 | 同时支持推送（`publishDiagnostics`，带 `versionSupport`）与拉取（`textDocument/diagnostic`，LSP 3.17）；拉取结果写回会话诊断存储并走与推送同一条映射/渲染/重放；视图绑定、编辑防抖 500 ms、`workspace/diagnostic/refresh` 时拉取；打开后有一次延时重拉兜底 |
| 问题面板 | 右侧“问题”固定标签 | 跨文件、跨工作区列出所有语言会话当前诊断；按严重度/路径/行排序，四档筛选，标题给“错误数 · 警告数 · 总数”；超过 2000 条只列前 2000 并明说截断；双击/Enter 打开并定位（工作区取自诊断本身） |
| 语义 token | 叠加在 TextMate 之上 | `semanticTokens` full/delta；`SemanticLineIndex` 按行分桶，只重建服务器真的改了分类的行；映射在 UI 线程之外；请求时机：视图绑定、编辑防抖 400 ms、`workspace/semanticTokens/refresh`、打开后 2.5 s 兜底；过期版本落地后立即再要一份 |
| 补全 | Ctrl+Space / 服务器触发符 / 无会话时词表 | `triggerCharacters` 自动触发；`isIncomplete` 在下一个字符后重试；`InsertTextFormat.Snippet` 按纯文本插入（`${1:name}` → `name`，无默认值则不插入，取首项，畸形片段消失，不做 Tab 跳转）；无会话时用文档词表（光标附近窗口、200 项上限） |
| 悬停 | 鼠标悬停 | 纯文本展示 |
| 跳转定义 | F12 / Ctrl（Cmd）+ 点击 | 只跟随第一个定义目标；跨文件沿源文件的工作区与固定评审会话打开，显式无所属会话不回退到活动会话 |
| 查找引用 | Shift+F12 | `textDocument/references`（含声明），按“本文件在前，然后路径、行、列”排序去重，远程 URI 丢弃；结果 Flyout 贴在光标处；空结果是真答案（状态行写“没有找到引用”），服务器未声明时写“不支持查找引用” |
| 重命名 | F2 | 服务器声明 `prepareProvider` 时先 `prepareRename` 取占位符；Enter 走 `textDocument/rename`；服务器不支持时 F2 不弹框，只在状态行说明 |
| 代码操作 | 灯泡 / Ctrl+. | 光标停 300 ms 后以零宽 range 请求；`data` 动作先 `codeAction/resolve` 再应用；`WorkspaceEdit` 只认文本编辑（同文件多处编辑必须互不重叠，打开中的文件走编辑器文档并合并成一个撤销步，其它文件线程池读—映射—原子写回）；文件资源操作与只带 `Command` 的动作不执行、只计数说明 |
| 内联提示 | 编辑器菜单“显示 → 内联提示”（默认开） | `inlayHintProvider`；视图绑定、编辑防抖 500 ms、`workspace/inlayHint/refresh` 时按整文件 range 拉取，空 label 且 `resolveProvider` 时逐条 resolve（每次上限 32 条）；零文档长度元素，不改文本、不进光标与复制 |
| 折叠 | 折叠边栏与命令 | 优先语言服务器 `textDocument/foldingRange`，否则结构启发式（括号族、缩进族、`#region`），XML/HTML 用库自带策略；文本变化 250 ms 防抖，结果按文档版本校验；超过 20000 行不折叠并在编辑器提示 |
| 文档符号 | 命令面板 `@` 来源 | 当前编辑器文档的语言服务器；位置取 `SelectionRange` 起点；上限 2000 后排序 |

- 异步结果须验证文档、版本、会话与绑定代次；分离视图取消请求。
- 声明式能力同时接受 `true` 与选项对象；服务器没声明时请求直接回空、入口隐藏而不是报错。

## 6.5 编辑器交互全集

- 显示开关（全部落 `preferences`，改动立即套用到已打开编辑器）：行号、空白、制表符、行尾符、控制字符、文档末尾以下滚动、当前行高亮、超链接、自动折叠、缩进宽度、Tab 转空格、列标尺、着色主题（跟随界面/固定亮/固定暗）。
- 工具栏 `⋯` 与右键菜单共用一份菜单构建逻辑，但各自持有实例（`ContextMenu` 只能在挂接的控件上打开，工具栏用 `MenuFlyout`）；菜单项打开时读设置、点击时写设置，勾选状态不说谎。
- 菜单内容：撤销/重做/剪切/复制/粘贴/全选、显示开关、折叠命令、转换命令（大小写四种、前导 Tab↔空格、缩进选区、删除行）、查找/替换（库自带 `SearchPanel`，Ctrl+F / Ctrl+H）。
- 折叠快捷键：Ctrl+Shift+[ 折叠当前块，Ctrl+Alt+[ / ] 全部折叠/展开。
- 多光标：库 12.0.0 **没有实现**，按用户确认改用**矩形（列）选择**（Alt+拖拽），设置页与菜单写明这一点，不把多光标伪装成已实现。
- 行内 adornment：诊断行尾圆点（零文档长度，悬停显示该行消息）、折叠占位符；`¶` 与 `→` 由 Forge 自己的零长度元素绘制（库的行尾符生成器被注释、`ShowTabs` 无读取者）。
- 超链接只认 http/https/mailto、需 Ctrl/Cmd，交给系统默认程序；失败写编辑器提示行，不静默吞掉。
- 选区配色：品牌蓝 `#3399FF` 半透明（亮色约 24%、暗色约 30%），选中不遮住代码；搜索命中用半透明黄。
- 原生文档与撤销历史属于文件模型，切换标签或分离视图不销毁缓冲；着色用 AvaloniaEdit.TextMate，不重建全文件行列表。

## 6.6 富文件查看器

文件标签先**分类再读**（纯函数 `FilePreviewClassifier.Classify(path, firstBytes)`，只喂前 512 字节）：

| 类型 | 判定 | 预览行为 |
|---|---|---|
| Git LFS 指针 | 内容以 `version https://git-lfs.github.com/spec/v1` 开头（优先于扩展名） | 按指针文本处理，不把 PNG 按扩展名误判 |
| 图片 | `.png/.jpg/.jpeg/.gif/.webp` | 后台解码；长边超 4096 直接解码到 4096（不先全尺寸解码）；小图不放大；头部读不出才退回原尺寸再缩放；`Release()` 释放位图 |
| 表格 | `.csv` / `.tsv` | 真 RFC 4180 读取器（引号、成对引号、CRLF/LF、字段内换行、参差行补齐；TSV 用制表符且不认引号）；默认行数上限 1000（100–100000，`files` 分区）；超限照常读完只计数并说明跳过多少；单字段 ≤4096 字符、单行 ≤1024 列；列宽按前 200 行估算；虚拟化 |
| 笔记本 | `.ipynb` | 读 `nbformat`；`stream`/`execute_result`/`display_data`/`error` 四种输出，每段按字符上限截断（默认 20 KB，1–1024 KB）并标出；`image/png` 超过 5 MB 直接不显示；坏 JSON 出提示不出异常；整篇读取上限 8 MiB |
| PDF | `.pdf` | 不内部渲染，出占位卡 + “用默认程序打开” |
| 网页 | `.html` / `.htm` | 在编辑器里就是可编辑源码；“打开”按钮换成“在浏览器中打开”，不在面板里渲染网页 |
| 其余文本 | 其余扩展名 | 走文本路径；只有 `TextDocument.LoadAsync` 抛 `NotSupportedException`（二进制）时才出二进制占位；超 `MaxFileBytes` 出“文件过大”错误 |

- 预览是**只读**的：`HasDocument` 保持 false，编辑器工具栏的换行/保存/复制/编辑菜单一起隐藏，`IsDirty`/`CanSave` 恒 false；标签始终显示路径，并常驻“用默认程序打开”。
- `files` 分区三项：CSV/TSV 预览行数、`.ipynb` 单段输出上限、图片预览上限（默认 20 MB，1–256 MB）。
- 分类、CSV/笔记本/LFS 解析都是纯逻辑、可单测；真实解码与文件选择器在无头测试中不覆盖。

## 6.7 行评审

- 文件与 diff 共用同一套浮层（`IReviewSurface`：`FileReviewController`、`DiffReviewController`），选中文本或点击行号即出评论图标，展开行范围 + “保存草稿 / 立即发送 / 取消”。
- 草稿存 `reviews.db`；单条发送只含该评论，批量发送把当前文件草稿合成一次消息；队列接受成功后才标记已发送；`MarkSentAsync` 幂等。
- 发送前保存并验证锚点；锚点随编辑迁移，无法重定位时显示失效；不可基于未保存内容发送。
- 详情与面板见 [04-git-diff-pr.md](04-git-diff-pr.md) 4.3 / 4.10。

## 6.8 可重绑的键位（keymap）

- `keymap` 分区（界面组）：搜索、捕获重绑、冲突检测、恢复默认；写 `preferences`，`KeymapApplier` 绑定到活偏好，改完立即重建窗口键绑定、编辑器菜单手势与视图按键处理器。
- 21 个可重绑动作，分三个作用域（窗口 / 编辑器 / 视图）：
  - 窗口：新建终端、打开文件面板、打开命令面板、打开工作区搜索。
  - 编辑器：撤销、重做、剪切、复制、粘贴、全选、折叠当前块、全部折叠、全部展开、查找、替换、代码操作、重命名。
  - 视图：保存文档、下一个未评审改动、LSP 补全、查找引用。
- 终端 VT 透传、编辑器内文本导航与列表上下文键（会话建议、面板导航）**不是**全局动作，故意不可重绑。

相关文档：[04-git-diff-pr.md](04-git-diff-pr.md)（diff 与评审）、[07-workbench-ui.md](07-workbench-ui.md)（面板与设置）、[14-limitations.md](14-limitations.md)（多光标与协议缺口）。
