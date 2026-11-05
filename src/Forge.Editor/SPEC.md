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

- 不依赖 UI 工具包;快照与事件供上层(Avalonia)消费。LSP 服务器本体由用户安装,Forge 只负责启动与通信。
- 着色分两层:TextMate 词法层立即给关键字/字符串/注释;LSP `semanticTokens` 返回后叠加标识符类别。不使用 tree-sitter。

## 关键决策

**文本模型。** Rope 加不可变快照;批量编辑原子提交为单版本;撤销/重做分组;锚点随编辑迁移;事件在提交后按版本顺序派发(重入安全)。保存记录实际写入的快照版本,保存期间的新编辑保持 `IsDirty`。

**保存流式且原子。** `TextFileCodec.WriteAsync` 不在内存里拼整份文件:按快照 rope 分块编码写入目标目录下的 `<文件名>.<guid:N>.tmp`(`FileMode.CreateNew` + `FileShare.None`),`FlushAsync` 之后再 `Flush(flushToDisk: true)`,然后用 `AtomicFile.ReplaceFileAsync` 发布;失败或取消删除临时文件、原文件保持原字节。替换阶段复用 `Forge.IO` 的有界重试契约(目标被其他句柄占用时重试,目标是 `ReadOnly`/目录时立即失败),保存因此不再自己 `File.Move`。

**LSP。** 自写 JSON-RPC over stdio(Content-Length 分帧,收发头缓冲分离);同步泵按文档维护单一有序状态,增量变更携带不可变截止快照,功能请求前建立同步屏障;服务器请求 id 按 JSON 类型原样回传;进程启动支持 `.cmd`/`.bat` 的 `cmd /d /s /c` 引号规则;进程工厂可注入以便测试。会话状态机以 initialize 握手是否完成为界:启动中观察到服务器退出(进程退出事件早于或晚于连接关闭都一样)一律落到 `Unavailable` 并记 `failed to initialize`,`Stopped` 只表示已 `Running` 的服务器崩溃、等待重启;两种观察顺序的测试用可注入的进程内假服务器固定顺序,不依赖真实进程启动时序。

**Diff。** 行 Diff 默认比较行终止符(`CRLF` vs `LF` 是变更),`IgnoreLineEndings` 可关闭;评论锚点按内容哈希重定位(精确/移动/模糊/丢失),移动搜索建每 hunk 索引,避免平方级扫描。

**TextMate。** Oniguruma 正则翻译为 .NET 正则(未内化 GregsEx,体积过大),翻译不了的模式报诊断并不匹配;include 规则展开带环检测与深度上限;状态相等性覆盖影响后续分词的全部字段(含 `BeginRuleCapturedEol`),保证增量与全量结果一致;用户语法可来自 `~/.forge/grammars` 与 `<workspace>/.forge/grammars`。字符串高亮源与快照高亮源都把 LF/CRLF/CR 视为换行且行内容不含换行符;快照经 `TextSnapshotHighlightSource` 适配 `IHighlightSource`,整份文本物化为一个字符串后按行返回 `ReadOnlySpan<char>`,读行零分配。分词器把行内容加 `\n` 写入按需增长的复用 `char[]` 缓冲,用 `Regex.EnumerateMatches(ReadOnlySpan<char>)` 在 span 上找最早匹配(零分配);仅当命中的规则需要第 1 组及以上的捕获(`captures`/捕获名/反向引用)时才把缓冲物化为字符串交给 `Regex.Match`。


## 未决

- `Regex.Match` 没有能返回捕获组的 span 入口,所以当命中规则需要第 1 组及以上的捕获(`captures`/捕获名/反向引用)时,分词器仍会把复用缓冲里的行文本物化成一个字符串供捕获收集;不需要捕获的规则全程在 span 上匹配,已无每行字符串分配。
