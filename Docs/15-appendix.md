# 15 附录：工具总表、常量与术语

## 15.1 内置工具总表

副作用列：`R` = 只读，`W` = 写，`X` = 执行；`*` = 可被 `tools` 分区关闭，`—` = 必需项不可关闭，`†` = 只有根 Agent 可用。

| 工具 | 组 | 副作用 | 开关 | 一句话 |
|---|---|---|---|---|
| `read` | 文件 | R | * | 有界窗口读（2000 行 / 256 KiB，上限 20000 行 / 1 MiB） |
| `write` | 文件 | W | * | 原子整写或追加 |
| `edit` | 文件 | W | * | 唯一匹配替换，否则报错给行号 |
| `ls` | 文件 | R | * | 单层列目录，最多 10000 条 |
| `find` | 文件 | R | * | 遍历 + glob，最多 1000 条，深度 64 |
| `grep` | 文件 | R | * | 托管/ripgrep，1000 命中，单文件 8 MiB |
| `bash` | 文件 | X | * | Direct 优先进程，每流 512 KiB，默认 120 s |
| `todo` | 会话 | R/W | * | 7 种操作：清单、分组、计划摘要 |
| `ask_user` | 会话 | R | * | 问题卡（选项/自由文本/多选） |
| `skill` | 会话 | R | — | 按名取技能全文（≤64000 字符） |
| `spawn_agent` | Agent | R | * | 派生 Scout/Worker/Plan/Reviewer 或用户 Agent |
| `subagent_steer` | Agent | R | — | 批次边界转向 |
| `subagent_followup` | Agent | R | — | 结束时追加后续指令 |
| `subagent_stop` | Agent | R | — | 只取消该子 Agent |
| `request_review` | Agent | R | * | 派生 Reviewer，结论落库 |
| `search_history` | 检索 | R | * | 全文检索历史消息 |
| `read_history` | 检索 | R | * | 取回被压缩的原文 |
| `read_artifact` | 检索 | R | — | 读回超长输出 |
| `web_fetch` | Web | R | * | http/https 抓取，默认拒私网，5 MB/30 s |
| `web_search` | Web | R | * | Brave / Tavily / SearXNG，未配置不注册 |
| `git_status` | Git | R | * | 状态 |
| `git_log` | Git | R | * | 提交历史 |
| `git_diff` | Git | R | * | diff（`base`/`paths`/`stat_only`） |
| `git_fetch` | Git | R | * | 抓取 |
| `git_branch` | Git | W | * | 分支操作 |
| `git_sync` | Git | W | * | 拉取/推送（只 `--force-with-lease`） |
| `pr_view` | PR | R | * | PR 概览 |
| `pr_workflow` | PR | R | * | 六段 PR 流程文本 |
| `pr_checks` | PR | R | * | 检查与判决 |
| `pr_ci_log` | PR | R | * | 失败作业日志尾 |
| `pr_comments` | PR | R | * | PR 评审线程 |
| `review_comments` | PR | R | * | 本地行评审 |
| `pr_create` | PR | W | * | 五道闸门后创建 PR |
| `pr_sync` | PR | W | * | 同步分支（rebase/merge） |
| `pr_edit` | PR | W | * | 读-改-核对 |
| `pr_comment_reply` | PR | W | * | 带 verdict 回复 |
| `pr_comment_resolve` | PR | W | * | 解决 PR 线程 |
| `review_comment_resolve` | PR | W | * | 解决本地评论 |
| `pr_screenshots` | PR | X | * | before/after 截图 + 孤儿提交 |
| `spec_get` | 规格 | R | * | 读单节点与链接 |
| `spec_graph` | 规格 | R | * | 图遍历（depth ≤10） |
| `spec_validate` | 规格 | R | * | 校验重复/缺失/悬挂/环 |
| `extension_list` | 扩展编写 | R | * † | 列出已装/待决扩展 |
| `extension_create` | 扩展编写 | W | * † | 暂存新扩展 |
| `extension_update` | 扩展编写 | W | * † | 暂存更新 |
| `extension_disable` | 扩展编写 | W | * † | 禁用 |
| `extension_delete` | 扩展编写 | W | * † | 永久封锁（需审批） |
| `run_code` | codemode | X | * | C# 脚本，一次执行器 |
| `<server>__<tool>` | MCP | 默认 X / 可标 R | 跟随服务器 | MCP 服务器工具 |
| `mcp_tool_search` | MCP | R | — | 回取被选面上限隐藏的工具 |

## 15.2 关键默认值与常量

| 常量 | 默认 | 可调范围 |
|---|---|---|
| `read` 窗口 | 2000 行 / 256 KiB | 上限 20000 行 / 1 MiB |
| `bash` 超时 / 每流输出 / 并发 | 120 s / 512 KiB / 8 | 1–600 s / 硬上限 4 MiB |
| `grep` 命中 / 单文件上限 | 1000 / 8 MiB（固定，不可调） | 命中硬上限 10000 |
| `find` 命中 / 遍历深度 | 1000 / 16 | 命中硬上限 10000；深度 1–64 |
| 子 Agent 深度 / 并发 | 3 / 4 | 并发 1–16 |
| 压缩触发 / 目标 / 保留 | 85% / 50% / `max(1, min(20000, 0.2U))` | 触发 50–95，目标 ≥1 且低于触发 |
| 手动压缩保留 | keepRecent（W 未知时 20000） | — |
| 计划摘要修订 | 50 条/会话 | limit 1–50 |
| 评审包 | diff 48000 字符 / 提交 50 / 文件 200 / 字段 2000 | 1000–500000 / 5–500 / 20–2000 / 200–20000 |
| Reviewer 原文上限 | 8000 字符 | — |
| 本地限速 | 600 req/min、4,000,000 tok/min、自动化间隔 10 s | 1–100000 / 1000–1e9 / 0–600 |
| 限速 hold 上限 | 1 小时 | — |
| 上下文默认窗口 | 无（未知） | 正整数或 1–1000% |
| `/models` 列表分页 | `limit=1000`（Anthropic） | — |
| models.dev 快照 | 每日一次，约 235 提供商 / 8993 模型 / 1.4 MB | 地址可换 |
| 终端回滚 | 10000 行 | 1000–100000 |
| 终端窗口 / 输入队列 / 终端数 | 1 MiB / 1024×64 KiB / 32 | — |
| 终端 resize 防抖 / 搜索防抖 | 50 ms / 150 ms | — |
| 附件上限 | PDF 32 MB、音视频 20 MB、文本 256 KB、非图片 10 个/条 | — |
| 图片预览 / 工具缩略图 | 4096 长边、20 MB / 256 px | 64–1024 px |
| CSV 行数 / 笔记本输出 / 笔记本读取 | 1000 / 20 KB / 8 MiB | 100–100000 / 1–1024 KB |
| MCP schema / 描述 / 图片 | 64 K / 4 K / 5 MB 单张、20 MB 合计 | schema 0 关闭 |
| MCP 超时 / 重连 / stderr 尾 | 60 s / 1 s / 200 行 | — |
| 插件 IPC 帧 / 产物 / 暂存 | 16 MiB / 8 MiB / 256 KiB 单文件、1 MiB 总量 | — |
| 插件失败预算 / 取消宽限 / 撤销等待 | 4 次 / 2 s / 10 s | — |
| 插件链深度 / 每插件有效并发 | 8 / 4（Host 派发闸门 `--concurrency` 默认 4；Worker 内层默认 8） | — |
| `run_code` 默认/最大预算 | 60 s / 600 s | — |
| `run_code` 内部并发 / 返回 / 单次结果 | 8 / 40000 / 20000 字符 | 1–32 / 1000–400000 / 500–400000 |
| 执行器并发槽 | 4 | — |
| 菜单结果上限 / 每来源上限 | 50 / 50 | 10–500 |
| Toast 上限 / 停留 | 5 / 4、4、6、8 s | — |
| 布局快照版本 | layout v5、open-tabs v2 | — |
| 检查轮询 / 最长等待 / 无检查确认 / 日志尾 | 30 s / 30 min / 3 / 200 行 | 10–300 s / 1–180 min / 1–10 / 20–2000 |
| 自动更新 | 每日/每周/从不；失败 6 h 重试；下载 2 GiB；确认 60 s | — |

## 15.3 设置分区速查

`provider` 100 · `rate-limit` 115 · `agents` 120 · `prompts` 130 · `web` 140 · `automations` 150 · `appearance` 200 · `keymap` 210 · `language` 300 · `chat` 320 · `editor` 350 · `files` 360 · `palette` 380 · `git` 500 · `projects` 510 · `notifications` 550 · `notification-permission` 560 · `idle-archive` 570 · `network` 580 · `extensions` 600 · `approvals` 700 · `skills` 710 · `mcp` 720 · `tools` 730 · `inspector` 740 · `updates` 800 · `paths` 900。分组：AGENT（100–150）、界面（200–380、500–510）、系统（550–900）。每个分区的内容见 [07-workbench-ui.md](07-workbench-ui.md) 7.12。

## 15.4 数据文件速查

见 [12-storage-and-data.md](12-storage-and-data.md) 12.1 的目录树。最常见的四个：

- `settings.json`：唯一的偏好/提供商/Agent 配置源，三个写者互斥。
- `forge.db`：会话与工作台状态，`user_version` 当前 11。
- `reviews.db`：评论、标记、结论，无 schema 版本、幂等建表。
- `approvals.db`：插件审批，Host 所有，App 只读。

## 15.5 术语表

| 术语 | 含义 |
|---|---|
| worktree | Git 的工作树；FORGE 每个仓库会话一个，目录在 `<仓库>.worktrees/<slug>` |
| 占用键 | 由工作树与工作区路径算出的规范化键，唯一索引保证一个工作区一个活动根会话 |
| 接手（handover） | 把工作区活动权从一个会话事务性交给另一个会话 |
| 分叉（fork） | 从某个提交点开出独立分支继续，源会话不受影响 |
| spec-graph | 规格文件组成的带类型边的图；规格是行为契约 |
| codemode | 模型写 C# 脚本、在一次性执行器里并行调用已注册工具的模式 |
| 一次性执行器 | 每次 `run_code` 一个新进程，跑完杀整棵进程树，绝不复用 |
| Extension Host | 常驻 JIT 进程，负责插件发现/编译/审批把关/Worker 与执行器管理/PTY |
| Plugin Worker | 每插件一个 JIT 进程，只在已验证字节上装载插件 |
| L0 | 声明式工作流（JSON，步骤 + `after` + JSON 路径映射），不写 C# |
| L2 | 动态 C# 插件 |
| verdict | Reviewer 输出末尾的唯一结论行（approve / request changes） |
| 步骤完成门禁 | 改了代码的 TODO 步骤标 done 前必须已有 approve 结论与有效 verification |
| 出货（ship） | 分支的发布/PR/合并一组动作，位于文件面板第 1 行 git 头 |
| 已查看 / 已保留 | diff 审阅的两种标记，按会话存在 `reviews.db` |
| 减少动画 | `appearance` 分区的全局动效闸门，树与时间轴都读它 |
| 首启向导 | 三步覆盖层（语言、提供商、通知权限），由 `ui_state` 决定是否显示 |
| 资源检查器 | 一眼看全所有打开会话正在跑的工具与子 Agent，并可只停一条调用 |
| 编排（orchestration） | 四份内置技能 + 根 Agent 的 per-task 路由，开关 `orchestrationEnabled` |
| 机械门禁 | 由代码强制（而非提示引导）的两个节点：步骤完成门禁与 `pr_create` 全步骤 done |

相关文档：[README.md](README.md)、[02-agent-runtime.md](02-agent-runtime.md)、[07-workbench-ui.md](07-workbench-ui.md)、[11-extensions-and-codemode.md](11-extensions-and-codemode.md)、[14-limitations.md](14-limitations.md)。
