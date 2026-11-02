---
id: forge-editor
type: module-design
status: draft
title: Forge.Editor
parent: forge-architecture
---

# Forge.Editor

文本缓冲、LSP 客户端、TextMate 词法着色、Diff 模型。不含 UI;只依赖 BCL。TextMate 组件内化自 Tint(MIT,保留署名,见 `THIRD_PARTY_NOTICES.md`)。

## 边界

- 不依赖 UI 工具包;快照与事件供上层(Avalonia)消费。LSP 服务器本体由用户安装,Forge 只负责启动与通信。
- 着色分两层:TextMate 词法层立即给关键字/字符串/注释;LSP `semanticTokens` 返回后叠加标识符类别。不使用 tree-sitter。

## 关键决策

**文本模型。** Rope 加不可变快照;批量编辑原子提交为单版本;撤销/重做分组;锚点随编辑迁移;事件在提交后按版本顺序派发(重入安全)。保存记录实际写入的快照版本,保存期间的新编辑保持 `IsDirty`。

**LSP。** 自写 JSON-RPC over stdio(Content-Length 分帧,收发头缓冲分离);同步泵按文档维护单一有序状态,增量变更携带不可变截止快照,功能请求前建立同步屏障;服务器请求 id 按 JSON 类型原样回传;进程启动支持 `.cmd`/`.bat` 的 `cmd /d /s /c` 引号规则;进程工厂可注入以便测试。

**Diff。** 行 Diff 默认比较行终止符(`CRLF` vs `LF` 是变更),`IgnoreLineEndings` 可关闭;评论锚点按内容哈希重定位(精确/移动/模糊/丢失),移动搜索建每 hunk 索引,避免平方级扫描。

**TextMate。** Oniguruma 正则翻译为 .NET 正则(未内化 GregsEx,体积过大),翻译不了的模式报诊断并不匹配;include 规则展开带环检测与深度上限;状态相等性覆盖影响后续分词的全部字段(含 `BeginRuleCapturedEol`),保证增量与全量结果一致;用户语法可来自 `~/.forge/grammars` 与 `<workspace>/.forge/grammars`。

## 未决

- `TextSnapshot` → `IHighlightSource` 适配层尚未接入(当前按行返回字符串,每次分配)。
- 字符串高亮源与快照的换行语义已对齐(LF/CRLF/CR 均为换行)。
