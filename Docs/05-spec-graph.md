# 05 规格图（spec-graph）

规格是 FORGE 的事实依据：规格文件与代码同仓演进，Agent 用规格工具读写它，界面有独立的规格树面板。本文件说明规格模型、工具、面板与写入守卫。

## 5.1 规格模型

- 一份规格 = 一个带 frontmatter 的 Markdown 文件（根相对路径，`.md` 结尾）。
- frontmatter 字段：
  - `id`（唯一）、`type`（`goal-and-requirements` / `architecture-design` / `module-design` / `submodule-design` / `task-spec`）、`title`、`status`（`draft` / `active` / `stale` / `done` / `deprecated`）。
  - 链接字段：`parent`（树边）、`depends-on`、`references`、`implements`、`covers`、`tags`。
- 图结构：`parent` 构成树，其余是带类型的边；反向边（谁引用了我）由索引自动得出。
- 索引：`SpecGraph` 按工作区缓存索引（信任文件大小 + 修改时间），`refresh` 参数强制重读内容。索引提供按类型/标签/父节点/depends-on 过滤的搜索与图遍历。

## 5.2 三个规格工具

| 工具 | 副作用 | 参数与行为 |
|---|---|---|
| `spec_get` | 只读 | `id` 必填；返回 frontmatter、解析后的正向链接、反向链接与路径；不存在时返回错误文本 |
| `spec_graph` | 只读 | `id`、`direction`（`subtree` 默认 / `ancestors` / `neighbors`）、`edge`（`parent`/`depends-on`/`references`/`implements`/`covers`，neighbors 默认 `depends-on`）、`depth`（默认 1，最多 10）。返回节点（id/type/title/status/path/depth）与边（from/kind/to/reverse） |
| `spec_validate` | 只读 | 检查重复 id、缺失父节点、悬挂链接与 parent 环；返回 `valid` 与逐条 `issues`（code/id/path/message） |

- 三个工具都是只读的 `Forge.Abstractions` `ITool`，经 `Forge.Workspace` 暴露；仅当编排开启或工作区已有带 `id` 的规格时注册（会话启动时一次性决定，保持工具定义前缀稳定）。查找规格内容用 `grep`，修改规格用 `edit`/`write`。
- 工具输出走反射无关的 `JsonPayload`（AOT 安全）；持久化类型走 `WorkspaceJsonContext` 源生成。

## 5.3 规格树面板

- 检视区的“规格树”固定标签（与 TODO、评审同组）。
- 按 `parent` 建树，扁平虚拟化行列表；展开/折叠只对子树做区间插入/删除，展开状态按 id 跨刷新保留。
- 每行：展开箭头（有子节点时）、文件/文件夹图标、标题（链接样式，点击在编辑器打开该规格文件）、状态（灰色小字，仅在有 status 时显示）。
- 底部/顶部状态行：无项目、读取中、`N 个规格`、读失败原因。
- 刷新按钮；刷新是整表替换（滚动位置回到顶部）。
- 行有引导线（竖线对齐父级箭头中线），与文件树同一套缩进样式。

## 5.4 写入守卫与无损往返

- 以下写入守卫属于 `SpecGraph` 库 API(`CreateAsync`/`UpdateFrontmatterAsync`/`DeleteAsync`),目前没有工具或界面调用它们;Agent 改规格走 `edit`/`write`。规格写入限制在所属工作区内：创建/更新/删除前解析符号链接与 junction 的真实目标，越界即拒绝（`WorkspacePathException`）。
- 更新 frontmatter 时把整份 Markdown（UTF-8 无 BOM、沿用原有行尾）交给 `AtomicFile.WriteAllBytesAsync`：同目录临时文件 + `fsync` + 改名覆盖，读者不会看到半文件；目标是只读文件或目录时立即失败且原文件逐字节不变，临时文件被删除。
- frontmatter 列表解析与序列化互逆（引号、逗号、转义）；解析器不支持的语法保留 `RawText` 原样回写，不静默重写。
- `spec_validate` 是唯一的图健康检查入口；界面不提供“批量修复”，悬挂链接与环只报告、不猜。

## 5.5 在 Agent 工作流中的位置

- 规格工具与文件工具同时可用：Agent 先 `grep` 找约束、用 `spec_get`/`spec_graph` 读节点与链接，再改代码，规格变更用 `edit`。
- 规格与代码同仓：worktree 会话里的规格改动随分支走，合并/PR 后其他 worktree 才看得到（项目作用域的提示词模板同理，见 09）。
- 评审包会把“与任务相关的规格”交给 Reviewer 阅读；`request_review` 的 Reviewer 可以读任意工作区文件与规格。

相关文档：[02-agent-runtime.md](02-agent-runtime.md)（评审与门禁）、[06-editor-and-code-intelligence.md](06-editor-and-code-intelligence.md)（打开规格文件）、[12-storage-and-data.md](12-storage-and-data.md)（写入路径）。
