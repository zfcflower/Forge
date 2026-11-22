# 14 未实现、未接线与未验证

本文件列出 SPEC 里明确标注的边界与缺口。**这些不是功能**：读功能章节时不要把它们算进“已实现”。

## 14.1 平台与构建

| 项 | 状态 | 说明 |
|---|---|---|
| macOS 进程归属分支 | **未验证** | `pipe`+`fcntl`（无 `pipe2`）、`WNOWAIT` 常量（`0x20` 与 Linux 不同）、`siginfo_t.si_pid` 偏移（`12` 与 glibc 的 `16`）都只在代码里按 `OperatingSystem.IsMacOS()` 分岔并标注待 CI 验证 |
| macOS PTY | **未验证** | 同一套 POSIX 分支；Darwin 的 `O_CLOEXEC` 取值待 CI |
| Linux 进程归属与 PTY | 已在 WSL2 实测 | 真 Host 探针跑过 `/bin/sh`、会话归属、resize、退出码、整树回收 |
| JIT 组件随发布分发 | **未接线** | AOT `publish` 产物不含 `Forge.ExtensionHost`/`PluginWorker`/`ScriptExecutor`；它们只在 `FORGE_JIT_DIR` 或仓内构建布局里被找到，更新包也只装 AOT 目录——按现状发出去，宿主起不来（`diag.jitHostMissing`），终端/插件/codemode 全部不可用（见 13.7） |
| 运行时探测 | **未实现** | 没有 .NET 版本检查；缺 `dotnet` 只表现为 `diag.hostStartFailed` 的启动失败文本，不是“缺运行时”的明确错误（`architecture.md` 原先那句“启动时检测并给出明确错误”已按现状改正） |
| 三平台 AOT `publish` 矩阵 | **未接线** | 只有 Windows AOT publish 冒烟；macOS/Linux publish 从未跑过 |
| 测试项目与 CI 运行时覆盖 | **部分接线** | `tests/Forge.App.Tests`（290 例，见 `Docs/13-operations-and-tooling.md` §13.5）在 CI 三 OS 矩阵里真跑：存储 schema/purge、git 探针与 diff 账本、bash 门禁、三个流式适配器、转录 VM 与 headless 面板均有运行时覆盖；POSIX 进程归属、PTY/终端、`ExtensionHost` 拉起、MCP 传输、AOT 产物行为与提示词来源的信号侧仍无测试宿主 |
| macOS 从 Dock 启动的语言检测 | 已知会落到英文 | 无 `LANG` 时的退路 |

## 14.2 明确未实现的产品功能

| 功能 | 现状 |
|---|---|
| 插件声明式 UI 面板 | `PanelDeclaration` 只做记录，不注册、不渲染；App 侧“声明式插件面板渲染未做” |
| 多光标 | AvaloniaEdit 12.0.0 没有实现；按用户确认改用**矩形（列）选择**，已在设置与菜单写明 |
| 语义 token 的 range 请求 | 只用 full/delta；编辑后旧索引不重映射（下一次答案纠正，不做热路径第二份坐标表） |
| `WorkspaceEdit` 跨标签 | 打开在另一标签的文件跳过并计入 dropped（不背后写盘）；完整修法待后续 |
| `inlayHint` 的 `textEdit` | 接受提示不改写文本 |
| `codeAction` 的 `only` 过滤 / `disabledSupport` | 只防御性地读并禁用，不按 `only` 过滤；`context.diagnostics` 留空由服务器按范围自算 |
| 内联提示按视口分片 | 按整文件 range 请求 |
| 全局 `core.excludesFile` / `.gitmodules` | 不参与忽略判定（需读 git config） |
| `edit` 的 UTF-16 保留 | 会把 UTF-16 文件写回 UTF-8（已在工具文档与 SPEC 写明） |
| 终端缩放重排行 / OSC 133 / 重启后恢复 / 图片协议 / 双向文本 | 未做；macOS 实机未验证 |
| 终端搜索的并发预算 | 大缓冲（万行级）下持锁逐行快照，与输出流并发时可能超一帧预算（待度量） |
| MCP 会话 id 跨重启持久化 | 未做 |
| MCP `completions/complete` | 未做（按需） |
| `mcp.json` 注释保留 | 写回丢注释、统一两空格缩进（`JsonNode` 无 trivia） |
| 工作流 `onError: retry/continue`、步骤级超时、`when`/`foreach`、`$expr` | 未实现；格式按可向前兼容设计 |
| 额度读数覆盖面 | 只有 Anthropic 订阅窗口 + 通用 `x-ratelimit-*` 分钟级；非 Anthropic 的订阅额度端点、设置页额度摘要、历史曲线、跨会话持久化未做 |
| 上下文继承的根因 | 继承前缀本身已超触发线时，迟滞只能把重复压缩限制为两次无效尝试；根因需要新的存储契约（T0） |
| 历史检索沿继承链 | `search_history`/`read_history` 不沿继承链回溯 |
| 交接对话框的模型/推理档位、缓存剩余时间、接近压缩阈值、前缀指纹不符提示 | 未做；摘要只用旧会话自有消息，不沿继承链取父会话 |
| 文件追踪的广度 | 只认 `read`/`edit`/`write` 的 `path` 参数 |
| 只读并行会话 | 刻意推迟（`bash` 无法区分读写，要么禁要么每次审批） |
| 一个会话切换多个工作区目录 | 推迟（`turn_checkpoints` 主键、步骤提交、PR 闸门、索引都要按当前目录取） |
| 恢复到某一轮 | 推迟（届时被接手会话的检查点只读） |
| 子 Agent 跨工作区派发、同父多子 Agent 抢 `obj/`/`bin/` | 另立任务 |
| Agent 会话切换种类/模型不落库 | 切换在下一轮前重建实例并生效，但 `sessions.agent_kind` 只在建行时写、`composer.json` 只由“设为默认”写；重启进程后重开该会话，chip 仍显示库里的旧种类与偏好模型 |
| 无标签的投递回合没有工具计数 | Core 自驱的 `message_session` 回合会写 `turn_metrics`（token/流式时长/状态都真实），但工具调用次数未知，该行 `tool_calls` 写 NULL 而不是 0 |
| 根终结路径不等待在飞回合 | 归档、移除、接手、交接在拆除根时直接释放 runtime，可能打断其 Agent 会话正在跑的一轮（既有整体拆除语义；关标签路径已改成等回合结束） |
| Agent 会话的未读是文本前缀 | 标签与树行用 `● ` 前缀而非图形圆点（`TabModel`/`TabHeaderView`/`ProjectView` 未加未读状态） |
| `message_session` 的授权只在该进程内 | 内存态，重启即清空：同一会话里重启后需要重新 `@` 点名 |
| 投递回执的通知不可测 | 唯一发出点是窗口私有方法，`AppComposition` 内部自建 `NotificationCenter`，无法注入替身——接线正确性只有代码审查保证 |
| Worker 资源限制（CPU/内存/活动进程） | 受威胁模型决策门控，未实现；Windows 可设 Job Object 上限，其他平台不承诺 |
| 预启动执行器池 | 未采用；只允许“取”不允许“还”，须由分段实测决定 |
| 自动更新的发布管线 | 只有 `scripts/make-update-manifest.ps1`，没有 tag 触发的打包/哈希/上传 workflow |
| 安装打包、签名、三平台发布产物 | 未做（只验证了 AOT 产物本身） |

## 14.3 已实现但有明确缺口的行为

| 功能 | 缺口 |
|---|---|
| 步骤完成门禁 | 评审结论已绑定工作树内容指纹、步骤记起始基线；剩余：没走过 `in_progress` 的步骤无基线（自己 `git commit` 的代码会被当成“没改代码”）；子模块内的未提交改动不进指纹；评审后构建写入未 ignore 的文件会使结论过期；步骤交错时可能多一次评审；台账已落库（后台串行写，被杀时可能丢最后一条，重启后多评一次） |
| `pr_create` 第三道闸门 | 只强制“重读过完整 diff”，清理是否彻底是 Agent 的判断（工具描述诚实写明） |
| 评论队列 | 队列接受与数据库标记不是跨系统原子事务，不承诺 exactly-once 或崩溃恢复；接受后标记失败必须提示并在本次运行阻塞重复发送；应用退出时异步恢复可能尚未完成 |
| 合并冲突快照 | 冲突在终端里解决后，标志要等下一次树刷新才解除（回合前校验按只读快照，不跑 git） |
| 代码块 / 单行 Markdown | 超大**不封口**围栏每次 flush 重设整块文本；**不换行**的超长单行每次整行重解析（分片只按行切） |
| 图片 diff | 解码时机是建文档即起（与类注释“滚到才解码”不符），每图有界且后台 |
| 思考行顺序 | 与回复文本在同一 flush 内固定“思考先于文本”，交织顺序异常的提供商会倒序 |
| CodeMode | 会话落库的 `prefix_fingerprint` 会陈旧一拍（`run_code` 描述随可见名单/预算活变，指纹只在回合边界重算） |
| Copilot 成本读数 | OpenAI 兼容面把缓存写入摊进 `prompt_tokens`、只报 `cached_tokens`，写入按普通输入价计；字段不存在，不做估算 |
| 限速 hold | 只有 1 小时封顶与“发布一次策略”两个出口，没有 UI 上显式忽略服务端 hold；`x-ratelimit-limit-requests` 一律按每分钟解释（某些兼容服务发日/小时限额，会偏松不会误锁） |
| 审批规则 | 记住规则只记“允许”，没有“始终拒绝”；插件新版本获批时清除其新旧工具名下的全部记住项 |
| `ReviewStore` 释放 | 不可释放；池化句柄靠 `SqliteSessionStore` 拆除时进程级 `ClearAllPools` 才释放，拆除顺序一变就会回退 |
| 路径守卫 | `WorkspacePath` 只挡意外与明显越界，不是安全沙箱（能在工作区内建链接并抢在检查后替换的调用方仍可逃逸，TOCTOU） |
| 回收站 | Windows 上 UNC 路径与非固定盘直接拒绝（失败而不是永久删除）；`subst` 盘先换算到真实目录 |
| Store 版 `pwsh.exe` 别名 | 启动的是打包进程，子进程不在 Host 私有 Job 里，整树回收与 busy 判定管不到（默认跳过别名） |

## 14.4 安全边界（不是沙箱）

- **进程隔离不是安全沙箱**：同一操作系统账户下插件仍可访问该账户可访问的文件与网络；未经审批的插件不执行，但批准不代表代码没有可靠性风险。
- **POSIX 进程组不是硬边界**：后代自己 `setsid`/`setpgid` 即可脱离；POSIX 没有 `KILL_ON_JOB_CLOSE` 的等价物，Host 被强杀时 Worker 只靠管道断开自行退出，其残留后代不被清理。不得宣称与 Windows Job Object 等价。
- **脚本执行器**：被禁止启动子进程（只接受 `--pipe`，无文件/网络/进程能力），编译白名单与语义检查也拒绝进程 API，所以“整棵进程树”实际就是一个进程；但内存与 CPU 不能在进程内限制，空转只能杀进程。1 秒宽限是常量而非按调用配置。
- **凭据库**：AES-GCM + 旁钥防误泄露与云同步，不承诺抵挡能读用户目录的本机攻击者（为此不引入 DPAPI 包，也不在 App 里写 P/Invoke）。
- **网络过滤**：私网过滤在连接时判定（防 DNS rebinding）；走代理时目标由代理解析、不做过滤；代理服务器地址本身不过滤。
- **审批**：插件调用主程序能力一律经 IPC，由主程序的权限引擎判断、审批与审计；`extension_*` 对插件永不可解析、永不放行。

## 14.5 已知的“看起来像缺陷”的设计取舍

- 第二个实例直接退出（`Forge 已在运行。`），不转交、不唤醒已有窗口。
- 关闭标签不结束会话运行；回合仍在跑时重开回到同一个活视图（不另建视图也不回放），回合结束、视图释放后重开才按存储回放；取消归档走 `ResumeRootAsync`。
- 归档不保存也不丢弃仍打开的编辑。
- 退出时最后一次布局保存最多等 3 秒，超时只记日志（不卡死关窗）。
- 只读会话的“继续”在目录缺失/`git worktree` 未列出时禁用：继续会重新打开会话，不删不改文件，但目录不在时回合里的 git 调用会自己报错。
- `artifacts` 转存阈值与 keepRecent（20000 / 0.2U）用默认值，未经使用验证。
- 图片预览上限、CSV 行数、笔记本输出上限都是 `files` 分区的可调值，默认值不是性能最优值。
- `Docs/` 文档是**功能清单**而非行为契约：行为契约在 `architecture.md` 与各模块 `SPEC.md`；两者冲突时以 SPEC 为准，并应修文档。

相关文档：[README.md](README.md)（口径）、[15-appendix.md](15-appendix.md)（常量与工具表）。
