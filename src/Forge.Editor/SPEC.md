---
id: forge-editor
type: module-design
status: draft
title: Forge.Editor
parent: forge-architecture
---

# Forge.Editor

文本缓冲、LSP 客户端、TextMate 词法着色、Diff 模型。不含 UI;只依赖 BCL 与 `Forge.IO`(仅保存时的原子替换)。TextMate 组件内化自 Tint(MIT,保留署名,见 `THIRD_PARTY_NOTICES.md`)。

## 边界

- 不依赖 UI 工具包,快照与事件供上层(Avalonia)消费;LSP 服务器本体由用户安装,Forge 只负责启动与通信。
- 着色分两层:TextMate 词法层立即给关键字/字符串/注释,LSP `semanticTokens` 返回后叠加标识符类别;不使用 tree-sitter。
- **AvaloniaEdit 与自有实现的边界。** AvaloniaEdit 已获准作为 `Forge.App` 的编辑 UI,不进入本项目;本项目仍提供语言服务器协议与会话、词法着色、原子编码保存、文本快照与 Diff,它们不是 AvaloniaEdit 的替代品。重叠评估(已核对 AvaloniaEdit 12.0.0):其文档已有 Rope、不可变快照、版本跟踪、撤销分组与文本锚点,应优先复用、不以保留本项目为目的长期维护重复编辑内核;该版本未提供 LSP 会话与同步、Diff 内容重定位或等价原子保存,`TextEditor.Load(string)`/`Save(string)` 尚为未实现方法、不得调用。剩余服务经适配与测试后可重新划分模块归属,项目去留取决于迁移结果;删除项目须先完成等价能力迁移与调用方验证,不能仅因替换编辑控件而删除。
- 用户已批准 AvaloniaEdit 的配套额外依赖(含 TextMate 集成与语法包),语言范围以实际采用的着色方案为准。**主文件编辑器当前使用 `AvaloniaEdit.TextMate`**(`Forge.App/Editing/ForgeEditorBridge.cs`),本项目的自有 TextMate 分词器不在该路径上;自有实现仍保留供语言服务/预览等无 UI 调用方评估与使用,无剩余用途再移除。LSP 服务器并不因此自动具备。

## 关键决策

- **文本模型。** Rope 加不可变快照;批量编辑原子提交为单版本;撤销/重做分组;锚点随编辑迁移;事件在提交后按版本顺序派发(重入安全);保存记录实际写入的快照版本,保存期间的新编辑保持 `IsDirty`。
- **保存流式且原子。** `TextFileCodec.WriteAsync` 正常路径不在内存里拼整份文件(行尾规范化路径例外:`GetText`+规范化+`GetBytes` 会整份物化):按快照 rope 分块编码写入目标目录下的 `<文件名>.<guid:N>.tmp`(`FileMode.CreateNew` + `FileShare.None`),`FlushAsync` 之后再 `Flush(flushToDisk: true)`,然后用 `AtomicFile.ReplaceFileAsync` 发布;失败或取消删除临时文件、原文件保持原字节。替换阶段复用 `Forge.IO` 的有界重试契约(目标被其他句柄占用时重试,目标是 `ReadOnly`/目录时立即失败),保存因此不再自己 `File.Move`。
- **编码检测与行尾契约。** `TextFileCodec.ReadAsync` 先按 BOM 判编码(UTF-8/UTF-16 LE/BE/UTF-32 LE/BE),无 BOM 再探宽编码(BOM-less wide),都不命中回退 Latin1;非宽编码、无 BOM 且 `LooksBinary`(含 NUL 等)判二进制(`IsBinary`,不作文本读)。行尾按 LF/CRLF/CR 计数取主导(`DetectLineEnding`);保存默认**不**规范化行尾(`NormalizeLineEndingsOnSave` 默认 off,绝不重排行尾),开启时按主导行尾重写;写回保留原 BOM 有无(`HasBom`)与编码。
- **LSP。** 自写 JSON-RPC over stdio(Content-Length 分帧,收发头缓冲分离);同步泵按文档维护单一有序状态,增量变更携带不可变截止快照,功能请求前建立同步屏障;服务器请求 id 按 JSON 类型原样回传;进程启动支持 `.cmd`/`.bat` 的 `cmd /d /s /c` 引号规则;进程工厂可注入以便测试。会话状态机以 initialize 握手是否完成为界:启动中观察到服务器退出(进程退出事件早于或晚于连接关闭都一样)一律落到 `Unavailable` 并记 `failed to initialize`;`Stopped` 只表示已 `Running` 的服务器崩溃、等待重启。两种观察顺序的测试用可注入的进程内假服务器固定顺序,不依赖真实进程启动时序。
- **自己装的语言服务器。** `LanguageServerDiscovery` 在本机找已安装的服务器,表里只有命令与参数,不下载、不执行探测:第一轮查 PATH 上的已知命令(C# 的 `roslyn-language-server`/`csharp-ls`/`OmniSharp`、`typescript-language-server`、`vscode-{json,css,html}-language-server`、`pyright-langserver`/`pylsp`、`gopls`、`rust-analyzer`、`clangd`、`bash-language-server`、`yaml-language-server`、`lua-language-server`);第二轮查 VS Code/Cursor 的扩展目录(目前是 `ms-dotnettools.csharp-*` 里的 `Microsoft.CodeAnalysis.LanguageServer.dll` 与 `.roslyn` 同名片、`omnisharp/OmniSharp`);第三轮查 VS Code 安装目录自带的 JSON/CSS/HTML 服务(用 `node` 跑 `*ServerMain.js`)。版本目录按版本号取最新、每个扩展最多看 32 个,不存在的根目录直接跳过。结果只作为 `LanguageDefinition` 的建议值并带 `ServerOrigin` 标出来源;`LanguageRegistry.WithDiscoveredServers` 不覆盖用户在 `languages.json` 里配置过的语言(含显式配成无服务器或 `disabled`),未配置的语言新增定义并继承其扩展名表。发现只在启动时跑一次,失败只记一条 trace,不影响编辑。
- **诊断:推送与拉取。** 客户端同时声明 `textDocument/publishDiagnostics`(带 `versionSupport`)与 `textDocument/diagnostic`,按 `diagnosticProvider` 决定能否拉取;拉取结果读 `kind: full` 的 `items`、`kind: unchanged`,也兼容旧式裸数组。拉取到的集合经 `LanguageSession.PublishDiagnostics` 写回会话诊断存储并**用与推送相同的事件**发出,所以渲染、序号排序与“视图晚绑定后重放”只有一条路径。还声明 `window.workDoneProgress`(否则 Roslyn 拿到 `discover projects` 后就停住、不加载项目)与 `workspace.diagnostics.refreshSupport`(否则服务器不告知“分析已更新,请重拉”);`workspace/diagnostic/refresh` 经 `RefreshRequested` 冒泡为 `LanguageSession.DiagnosticsRefreshRequested`。
- **语义 token 的按行索引。** `semanticTokens` 一次给全文件、视图每帧只画几十行,两者之间隔一层 `SemanticLineIndex`:不可变、按行分相的 `(start, length, category)` 数组。`SemanticTokenMap` 把服务器的 (行, 字符, 长度) 换成行内偏移——UTF-16 直接取,UTF-8 两端各自解码(多字节字符同时改变起点与长度),越行的剪到行尾;图例按位置一次性映射成 `TokenCategory[]`,每个 token 只付一次数组下标而不是一次字符串查表。`SemanticLineIndex.Build` 接收与 token 平行的行号数组(非递减,由 delta 解码器保证;乱序先排序再降级处理),**对内容未变的行直接复用上一版数组**——一次编辑后只分配服务器真的改过分类的那几行,并暴露 `BuiltLines`/`ReusedLines`/`DroppedTokens` 使这件事可断言。索引带快照版本号;不是同一版本的文本是否还画由展示层决定(App 继续画,因为丢整层会闪,真正错位的那几行在下一次答案里被纠正)。`SemanticTokensDecoder.ApplyEdits` 做两趟单次分配:先算长度再把旧数据拼接进结果,乱序的 edit 列表先排序、重叠的起点夹进上一条留下的区间。`workspace/semanticTokens/refresh` 经 `RefreshRequested` 冒泡为 `LanguageSession.SemanticTokensRefreshRequested`(与诊断的同名事件并列),它表示“分类变了”而不是“文本变了”。
- **代码操作、重命名与内联提示。** `LspClient` 以同一套请求口径(截止期限 + 会话取消)实现 `textDocument/codeAction`、`codeAction/resolve`、`textDocument/rename`、`textDocument/prepareRename`、`textDocument/inlayHint` 与 `inlayHint/resolve`,并处理 `workspace/{codeAction,inlayHint}/refresh`(冒泡成 `LanguageSession.CodeActionsRefreshRequested`/`InlayHintsRefreshRequested`)。
  - **能力声明。** `codeAction` 请求带 `codeActionLiteralSupport`(否则服务器只回裸 `Command`)、`dataSupport`(为真——`data` 是服务器关联两次调用的唯一线索)、`resolveSupport.properties: [edit]`(编辑可能要到 resolve 才出现)。能力解析同时接受 `true` 与选项对象两种形态,`resolveProvider`/`prepareProvider` 只从对象形态取;服务器没声明时请求直接回空、不发上线,编辑器据此把入口藏起来而不是报错。
  - **工作区编辑只认文本编辑**:`changes` 映射与 3.16 `documentChanges` 的 `textDocument/edits` 两种线形都读,同一文件的多处编辑必须互不重叠、越界位置一律拒绝;文件资源操作(create/rename/delete)不实现也不在客户端能力里声明,遇到时计入 `WorkspaceEdit.UnsupportedOperations` 交给上层报告“部分编辑未应用”,绝不把半个重命名当成功;只带 `Command` 的 code action 因为不声明 `workspace/executeCommand` 一样丢弃并计数。
  - `prepareRename` 兼容 3.12 的 `{range, placeholder}`、旧式四数字数组与 3.16 的 `{defaultBehavior: true}`。
  - 内联提示的 label 支持字符串与 `InlayHintLabelPart[]` 两种线形,空 label 保留(等待 resolve 补),并按 `range` 请求——按视口分片是展示层的事,`LspClient` 不改写调用方的 range。
- **诊断的工作区聚合。** `WorkspaceDiagnostics` 把会话池里每个会话的 `DiagnosticsStore` 看成一个整体:订阅 `LanguageSessionPool.SessionStarted`/`SessionClosed` 与每个 store 的 `Changed`,只冒泡一次变更通知,展平/排序在读取时做(没有后台计时器,没有变化时零工作)。行携带文件路径、**所属工作区**与**会话的位置编码**——后者让一条存储的 LSP 位置在文件后来才被打开时仍能正确换算成偏移,前者让跨工作区的列表项沿自己的会话打开而不是回退到活动会话。订阅集合与池对齐:池静默退役的会话(查到时已 dispose)在下次读取时摘掉,不会永久续订;`Snapshot()` 也跳过状态为 Disposed 的会话——池要等下一次查找才清槽位,而没人能再更新那批诊断,列出来只会被当成现状。
- **Diff。** 行 Diff 默认比较行终止符(`CRLF` vs `LF` 是变更),`IgnoreLineEndings` 可关闭;评论锚点按内容哈希重定位(精确/移动/模糊/丢失),移动搜索按窗口×全文起始行暴力扫描(无每 hunk 索引,`MaxDistance` 默认不限),大文件下是平方级。
- **折叠范围。** 服务器能折叠时用 `textDocument/foldingRange`(`LspClient`/`LanguageSession` 向 App 返回与本地启发式同一形状的 `Forge.Editor.Folding.FoldingRange`),否则用 `StructuralFolding` 在不可变快照上算结构范围:括号族(识别行/块注释、字串、C# 逐字串与 raw string)、缩进族、`#region`/`#endregion`(四种注释标记)。单次线性扫描、用 ArrayPool 滑窗读 rope、不按行分配字符串;超过 20000 行直接返回空(上层据此报提示而不是卡顿),区间上限 5000、嵌套深度上限 256,按起始行去重取最长(库的 `UpdateFoldings` 按起始偏移匹配,同起点的两段无法区分)。协议里倒序/负数/字符串行号/`endCharacter=0` 指到下一块首行等非法区间一律丢弃。扩展名→启发式族是 UI 层的展示选择,放在 `Forge.App`;XML 类交给库自带 `XmlFoldingStrategy`。
- **TextMate。** Oniguruma 正则翻译为 .NET 正则(未内化 GregsEx,体积过大),翻译不了的模式报诊断并不匹配;include 规则展开带环检测与深度上限;状态相等性覆盖影响后续分词的全部字段(含 `BeginRuleCapturedEol`),保证增量与全量结果一致;用户语法可来自 `~/.forge/grammars` 与 `<workspace>/.forge/grammars`。字符串高亮源与快照高亮源都把 LF/CRLF/CR 视为换行且行内容不含换行符;快照经 `TextSnapshotHighlightSource` 适配 `IHighlightSource`,整份文本物化为一个字符串后按行返回 `ReadOnlySpan<char>`,读行零分配。分词器把行内容加 `\n` 写入按需增长的复用 `char[]` 缓冲,用 `Regex.EnumerateMatches(ReadOnlySpan<char>)` 在 span 上找最早匹配(零分配);仅当命中的规则需要第 1 组及以上的捕获时才把缓冲物化为字符串交给 `Regex.Match`。单次搜索有 500 ms 匹配预算,超预算的搜索**先重试一次**:首次搜索某模式要付解释器启动成本,冷机器上这段成本本身就能吃掉整个预算,一次慢搜索决定那一行的分词结果就无法接受(同一文本换个负载会分得不一样,增量与全量也不再一致);第二次仍超时才当作不匹配并记诊断,所以最坏停顿是两个预算而不是一个。

## 未决

- 代码操作 / 重命名 / 内联提示在协议层的剩余缺口:`codeAction` 不按 `only` 过滤也不声明 `disabledSupport`(只防御性地读并禁用);`workspaceEdit` 不声明 `documentChanges`(服务器收到的意思是「只能发 `changes`」,资源操作因此不会出现,出现了也只计数);`inlayHint` 的 `textEdit` 不应用(接受提示时改写文本未做)。
- `Regex.Match` 没有能返回捕获组的 span 入口,所以命中规则需要第 1 组及以上的捕获(`captures`/捕获名/反向引用)时,分词器仍会把复用缓冲里的行文本物化成字符串供捕获收集;不需要捕获的规则全程在 span 上匹配,已无每行字符串分配。
