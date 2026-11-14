# Forge 规格与实现一致性审计报告(草稿,审计结束时定稿)

## 1. 审计范围与方法

- **范围**:19 份 committed 规格(goal-and-requirements.md、architecture.md、testing.md + 16 份模块 SPEC),以及 12 份 gitignored task-spec(已按约定清理)。代码 18 个 src 项目(约 27.8 万行)、13 个测试项目(约 21.4 万行)。
- **方法**:规格拆解为编号功能点(F-<模块>-nn),逐项双向核对——规格→程序(要求是否实现、闭环是否完整)、程序→规格(未记录行为、冲突、过时)。追踪真实调用链与数据流,不凭类名/注释/TODO 判定。测试证据读实际断言,不凭存在即认定闭环。
- **执行结构**:主 Agent 自审根级与 Core-D;其余模块派只读审计子 Agent(各自 findings 文件)。子 Agent 不修改代码/规格/测试。
- **结论六级**:PASS / PARTIAL / FAIL / MISSING / SPEC_GAP / UNVERIFIED;`PASS*` = 实现存在但缺测试证据。
- **验证边界**:本轮为文档审计,按 AGENTS.md 文档改动不运行构建/测试。测试覆盖以读测试代码为证据;需运行时/其他平台/真实服务端的行为标 UNVERIFIED。历史测量记录(带日期)保留其历史属性,不重跑。
- **配额说明**:审计期间模型配额(Alibaba Model Studio)多次耗尽,子 Agent 批量 429 失败;改为 low-concurrency + high-thinking 串行派发后恢复。此为实现环境约束,不影响审计标准。

## 2. 总体结论(待全部 findings 到达后填统计)

| 结论 | 数量 |
|---|---|
| PASS | 1246 |
| PASS*(缺测试) | 143 |
| PARTIAL | 53 |
| FAIL | 12(绝大多数为规格过时/失实,已清理) |
| MISSING | 1(规格自述未落地,非缺陷) |
| SPEC_GAP | 10 |
| UNVERIFIED | 12 |

约 1509 个功能点(另约 30 行因测试列含管道符/结论带注记未计入精确解析,多为 PASS/PASS*)。总体:实现与规格高度一致,绝大多数功能点 PASS;FAIL 主要是规格过时(已修),实现缺陷均保留规格要求并记录代码缺口。详见 spec-implementation-matrix.md 与 findings/。

## 3. 根级架构发现(主 Agent 直接核查,详见 findings/00-root.md)

- **F-ARCH-02 / F-ARCH-03(PARTIAL,边界漂移)**:Forge.Editor.csproj 冗余引用 Abstractions、Forge.ScriptRuntime.csproj 冗余引用 Ipc.Protocol,均代码零使用,但与其 SPEC「只依赖 BCL+X」冲突。处理:保留规格,记录代码缺口(后续删除冗余引用),本轮不改代码。
- **F-ARCH-11(FAIL,规格内部矛盾→已修正)**:architecture.md 分层表旧称 PluginWorker 执行 run_code,与 codemode 决策(一次性 ScriptExecutor)矛盾。已在清理中修正分层表与 goal Technology 表。
- 其余根级声明(依赖方向、AOT 标志、NuGet 白名单、P/Invoke 边界、App 存储边界、7 工具面、testing 结构性声明)均 PASS。

## 4. Core-D 发现(主 Agent 自审,详见 findings/04-core-d.md)

- 网络栈/Web 工具/自动化/提示词模板 27 个功能点全部 PASS,测试覆盖密集。
- 反向发现:DnsCache 默认容量 256/maxTtl 1h、AutomationEngine 恢复批次 Limit=1000 未入规格(建议补默认值)。

## 5. 模块发现(逐模块,详见 findings/)

- Core A(Agent 循环/子 Agent/工具面):✅ 01-core-a.md(69 项:62 PASS + 6 PASS* + 1 PARTIAL)。规格已修:L44 步骤评审补「无 CommitId 回退分支+未提交树并自述」例外(F-CORE-A-42,实现有测试且 WithCommitNote 自述)。待补契约(详见 findings):L41 extension_create/update 参数表缺 `tool`(workflow 必填)与本地校验规则(plugin 单 .cs/workflow.json/禁 manifest)、L109 插话送达时机(工具批后)与 run 结束未送达回收的跨层契约。PASS*:MaxTurns 续跑/todo 仅根/扩展工具白名单 pin/自动评审未接线(显式未决)等缺专项测试。低风险代码边界:取消时同批已完成兄弟调用结果被覆盖为 Cancelled.(历史合法但失真,无测试)。业务待确认:步骤无提交时评审应回退还是显式报错(现行为回退+自述)。
- Core B(存储/会话生命周期/压缩):✅ 02-core-b.md(123 项:113 PASS + 1 PASS* + 5 PARTIAL + 3 FAIL[均规格过时] + 1 UNVERIFIED)。规格已修:L71 末句 MessageCacheTtl/CacheHint.MessageTtl 过时→CacheTtl/CacheHint.Ttl+ResolveCacheTtl、L148 未决「1h 未接线」已解决删除、L57「所有写入」/L69「每个操作包保存点」过宽全称量词收窄(UsesSavepoint 默认 false)。代码缺口(保留规格、本轮不改):B-29 FTS5 缺失时 messages_text 回退表不创建致写队列永久致命(近乎理论,原生包自带 FTS5,无测试)、B-35 messages.at/summaries.at 宽容 ParseTimestamp 违反严格读取。B-114 PARTIAL:assistant 消息 FileBlock 被 Anthropic 静默丢弃(FileWireTests 钉住)与「绝不静默丢弃」全称句冲突(措辞待收窄,详见 findings)。业务闭环缺口:跨进程同会话 seq 竞态、写队列致命故障不可恢复、Purge 不清 automation_runs 引用等 6 条详见 findings。
- Core C(提供商/认证/缓存):✅ 03-core-c.md(77 项:64 PASS + 3 PASS* + 6 PARTIAL + 2 FAIL + 1 SPEC_GAP + 1 UNVERIFIED)。规格已修:TTL 过时(同 Core-B)、beta 表 18→17 项(AnthropicWire.cs:52-70 实数 17)、L119 preamble「system 第一块」与 L123 修正矛盾→billing 在前身份在后。代码缺口(保留规格、本轮不改):F-54 Copilot Editor-Plugin-Version 误发 Forge/1.0 应为 forge/1.0(CopilotAuthentication.cs:214,测试固化错误值)、F-13 400 拒 x-grok-conv-id/X-Interaction-Id 头不触发降级(CanDegrade 只匹配 body)。SPEC_GAP:F-34 Claude 凭据并发刷新无锁(refresh token 轮换下并发回合可互相作废,四种设备码 bundle 有串行此路没有)。跨规格大面积重复(App↔Core claudeWire/认证/探针/提示词)已现漂移,建议后续以 Core 为权威去重(本轮记录)。
- App A(组合根/提供商配置/密钥/日志):✅ 05-app-a.md(88 项:67 PASS + 17 PASS* + 4 PARTIAL)。规格已修:Copilot 端点 api.individual→api.githubcopilot.com、六种→七种认证、四种→五种设备码、SettingsJsonContext/ProviderSettings 不存在类名→真实类、拆除「严格逆序」/加载「绝不写文件」绝对化表述补例外。代码缺口/风险:F-39 CredentialedChatProvider 401 刷新重试不变量零测试(防重复输出关键)、F-08 FileArtifactStore 不单独释放靠 ClearAllPools、F-55 settings.json 实 7 个写者(规格说 3)均接 SettingsFileMutex 但无并发不丢段测试、锁仅进程内双实例互丢段未入规格。未记录功能:usageLog 段+JsonlUsageRecorder。跨规格重复+矛盾:Core:123 ClaimsOfficialIdentity 漏双条件(须连 claude-subscription)。
- App B(工作台外壳/会话 UI/通知/布局):✅ 06-app-b.md(232 项:192 PASS + 33 PASS* + 4 PARTIAL + 3 SPEC_GAP)。规格已修:窗口顶部搜索按钮「还不是功能」→打开命令面板、终端图标 IconTerminalFrame/Screen→单一 IconTerminal(panel_bottom)、「(项目|Git)」→(项目|常规对话)。代码缺口:F-APP-B-23 语言切换创建线程守卫约 14 个订阅者缺失(生产单 UI 线程不触发,headless 跨线程文本停旧语言)、F-APP-B-18 缺失 key 日志级别 Warning vs 规格 Trace、F-APP-B-149/163 通知接线约 20 路径仅审批/附件有回归测试。待修规格矛盾(详见 findings Q1-Q13):L171 调色板旧值 vs L293(实现跟 L293)、L216 图标尺寸 13/11 vs L294 14/13、L218 行尾工具 10/20 vs L65 12/22、L66/L68 检视区 tab 顺序、L220 缩进分量自矛盾。
- App C(Git/PR/出货/检查/步骤提交/行评审):✅ 07-app-c.md(60 项:51 PASS + 7 PASS* + 2 PARTIAL)。规格已修:L91「通知仍只属于等待 PR 检查」错误全称(与 L209 矛盾)。代码缺口:F-APP-C-03 compare 回退 Contains("github.com/") 误命中 notgithub.com、已发布+gh 缺失回退不可达(GitSyncViewModel.cs:524/483)、F-APP-C-40 检查点失败 catch 内 Notify 重复两次(SessionWorkspace.cs:1434-1443)。PASS*:检查面板世代号竞态/无上游零 gh 计数/轮次号续接缺测试。跨规格重复:8000 字节探测/评审包默认/EmptyTreeSha/no-CI 判决分散 App/Core/Workspace。
- App D(编辑器接线/LSP/问题面板/终端):✅ 08-app-d.md(88 项:65 PASS + 16 PASS* + 4 PARTIAL + 1 FAIL + 2 UNVERIFIED)。规格已修::331 终端分区未注册理由过时(「撞 SettingsPanelTests 硬断言」已失效,KnownSectionIds 已预留 terminal)。代码缺口(与 Editor F-ED-40 同源):F-48 LspClient 未声明 workspace.diagnostics.refreshSupport(Roslyn 不通知重拉,诊断闭环只剩编辑防抖+打开兜底)、F-25 semanticTokens.refreshSupport=false 与 :274 依赖服务器 refresh 矛盾。F-04 20000 行不折叠只挡本地启发式、服务器 foldingRange 无条件。真实 Roslyn/PTY 运行时整体 UNVERIFIED(全 ScriptedServer/FakeTerminalHost)。16 项 PASS* 最险:壳层跨工作区问题打开+评审归属零测试。
- App E(TODO/计划面板/审批队列/会话生命周期/自动化):✅ 09-app-e.md(69 项:61 PASS + 6 PASS* + 1 MISSING + 1 UNVERIFIED)。规格已修:TODO 动作枚举补「失败步骤重试提交」(Panels.cs:1594 RetryCommitCommand)。E-42 MISSING 为规格自述未落地(接手摘要不沿继承链/对话框无模型选择),非缺陷。E-57 UNVERIFIED:提供商缓存配置解析未追踪到存储实现。PASS*:TODO 拖拽 SameLayer/单实例锁/冲突快照 Epoch 竞态缺测。跨规格重复:Core:111↔App:402 ask_user min/max 语义;审批术语 App/Approval/Core 三处交叠缺互链。
- App F(设置分区/工具面/MCP 管理/提示词模板/面板):✅ 10-app-f.md(87 项:68 PASS + 10 PASS* + 4 PARTIAL + 1 SPEC_GAP + 4 UNVERIFIED)。规格已修::452 未决「文档符号列表未做」过时(面板 @ 符号来源已实现)。待修(详见 findings):F-54 :60 instrument「只读分区/InstrumentSectionBody 模板/四 VM 同文件」与代码不符(mcp/tools 不再只读,模板名不存在)、F-66 :432 skills 与 prompts 共用徽标约定未实现、F-19 MCP 页无独立重连动作。代码缺口:F-35 /compact 发送拦截+附件门零测试(SessionWorkspace.cs:909-916)。未记录:关派生同时禁用 request_review(SessionRuntime.cs:608,有测试)两规格都没写、ChatMermaid* 死设置(预注册无渲染消费者)。跨规格重复已致漂移(App↔Core 压缩/Web/模板)。
- App G(附件/首启向导/项目创建/工具图片/预览):✅ 11-app-g.md(72 项:60 PASS + 9 PASS* + 2 PARTIAL + 1 SPEC_GAP)。规格已修:L408 「仅附件插话通知为空串」过时→实为计数+列名(ComposerInterjectedNotice、InterjectionNoticeTests 10 例)。待修/裁决:F-APP-G-71 预览竞赛输家释放测试只断言赢家(位图泄漏回归无守护)、F-APP-G-04 UTF-8 拒绝通知规格称一律 Warning 但实现仅声称文本弹 toast(未知扩展二进制内联拒绝,测试钉住窄行为)。PASS*:克隆超时 GitException.Message 呈现零覆盖、运行中关窗=取消视图否决分支无头测。反向:19 条未文档化行为(图片读前 80MiB 宽松上限、表头启发式、CSV 4096+1 代理对例外)。
- ExtensionHost/PluginWorker/SDK:✅ 12-ext.md(109 项:89 PASS + 13 PASS* + 3 PARTIAL + 2 FAIL + 2 UNVERIFIED)。规格已修:EH L39 无 context 兼容路径过时声明(实际 CallBinding.Require 拒 null,随机 runId 构造已移除)、L24 revoke 补明有界等待(RevokeDrainWindow 10s)与工作流在途缺口。代码缺口(保留规格、本轮不改):F-PW-13 worker.prompt 异常以 prompt_failed IpcError 逃逸而非 PromptOutput(违背 PW L17 结果契约,Host 不计失败预算不杀树)、F-EH-77 host_shutdown_failed 零测试(实现存在但无回归保护)、F-EH-70 POSIX 终端后代残留 exitCode≥0 时不上报。UNVERIFIED:macOS 进程归属分支(WNOWAIT/si_pid 偏移/pipe+fcntl)需 macOS CI。业务闭环缺口:工作流 revoke 不取消在途 run(待用户定义终止方式)。
- Scripting/ScriptRuntime/ScriptExecutor:✅ 13-script.md(80 项:69 PASS + 5 PASS* + 2 PARTIAL + 2 FAIL + 2 SPEC_GAP)。代码缺口(保留规格、本轮不改代码):CompiledScript.EntryPoint() 死代码(F-SCRIPT-30)、ScriptRuntime.csproj 冗余引用 Ipc.Protocol(F-SR-01/F-ARCH-03)、Evict/Clear 未接线(F-SCRIPT-45,缓存键身份兜底正确性)、ScriptExecutorProtocol.cs:20 doc 注释误导。规格已修:SR 首段/责任/边界自相矛盾、SC 委托复用死代码描述、SC 失效未接线、SE 验证约定过时(见 cleanup-report 18–23)。F-SE-06 PARTIAL:publish 闭包 grep 已被测试放弃,现查 deps.json。
- Ipc/HostClient:✅ 14-ipc-hc.md(51 项:47 PASS + 3 PASS* + 1 PARTIAL)。重点:F-IPC-06 PARTIAL——PipeSession.EnqueueAsync(PipeSession.cs:304-312)零拷贝所有权判定(Offset==0 && Count==全长)在苛刻前提下可致入站缓冲被池复用、负载静默损坏(实现缺陷,保留规格记录代码缺口,本轮不改代码)。F-IPC-11/14/02 PASS*(连接超时/denied 映射/传输单用性缺测试)。规格问题已修:architecture.md IPC 段过时 v1 信封、HC SPEC:84 `ipc.cancel`→`cancel`。安全默认(tools.register 未定义 SideEffect→Execute、CurrentUserOnly、64 并发门)待补规格。
- Workspace:✅ 15-workspace.md(58 项:47 PASS + 1 PASS* + 9 PARTIAL + 1 SPEC_GAP)。规格已修:L15 评审写入归属(评审存应用数据目录非工作区)、L52 pr_create 五道→四道闸门(第5道与3同源)、L62 移除方式(RemoveAsync 用 git worktree remove,App 回收站流程另路)。代码缺口(保留规格、本轮不改):F-WS-14 LeaseHoldsAsync 只比本地 HEAD 不重读 ls-remote(推送期远端前进的过期 lease 漏判)、F-WS-18/19 diff 诚实性契约(GitOutputTruncatedException/IsCorrupt)零测试、F-WS-28 跨进程碰撞重试零测试。待核:pr_sync merge 路径是否 plain push(规格称 lease)、L19 工具清单与代码 11 个 PR/评审工具对齐。测试基建:14 文件约 183 处 GitProbe.IsAvailable 静默跳过(宿主缺 git 时绿而不跑)。PR 平台真实 gh/GitHub 集成面 UNVERIFIED(全 fake-gh)。
- IO:✅ 16-io.md(28 项全 PASS,主 Agent 自审)。工具常量逐项精确匹配,原子写/进程/路径关键不变量均有代码与专门测试。反向发现:grep/find 硬上限 10000 未入规格常量表。规格质量:§验证/§基准实测数字属验收证据保留,「改造前/后」对照可精简;§实现约定与§关键决策部分重叠(有意分层)。TOCTOU 逃逸已如实声明为已知局限。
- Editor:✅ 17-editor.md(67 项:50 PASS + 10 PASS* + 3 PARTIAL + 2 FAIL + 1 UNVERIFIED + 1 SPEC_GAP)。规格已修::37 评论锚点「建每 hunk 索引避免平方级」失实→实为窗口×起始行暴力扫描(CommentAnchor.cs:179-213)、:25 保存「不内存拼整份」补行尾规范化整份物化例外。代码缺口(保留规格、本轮不改):F-ED-40 LspClient 未声明 workspace.diagnostics.refreshSupport(App SPEC:285 要求,Roslyn 不发诊断刷新,拉取闭环失触发源;现有测试假服务器无条件发送掩盖)、F-ED-20 .cmd/.bat 启动无 /s 且零测试、F-ED-02 csproj 冗余 Abstractions 引用(同 F-ARCH-02)。SPEC_GAP:编码检测/行尾契约(27 测试)未入规格。内化 Tint 署名完整、无 UI/tree-sitter 残留。
- Mcp/Approval/Workflows:✅ 18-mcp-appr-wf.md(77 项:MCP 36/Approval 24/Workflows 17;73 PASS + 3 PASS* + 1 PARTIAL)。规格已修:Mcp :24 取消通知「已发出后」→无条件发送(JsonRpcPeer.cs:66-91)、Approval :21 补生产 DependencyHash 用 EH DependencyHashSchema v2(Hashing.DependencyHash 仅测试遗留)。代码缺口/缺测:F-APPR-09 ListReadOnlyAsync 零测试(设置页 approvals 唯一数据源,fail-closed)、F-MCP-10 HTTP 代际守卫无直接测试、F-MCP-13 名字生命周期不重绑定缺直接用例。跨规格重复:App:395/397↔Mcp:18/34/36/42 近乎逐字(重连上限 30s/5 次仅 App 侧)。跨模块闭环缺口:EH revoke 不终止在途工作流 run(待定义,Workflows 未交叉引用)。

## 6. 跨模块业务闭环缺口

按风险排序(证据见各 findings):

1. **LSP 诊断刷新闭环断裂**(Editor↔App D,F-ED-40/F-48):`LspClient` 未声明 `workspace.diagnostics.refreshSupport`,Roslyn 加载完项目不通知重拉;用户不编辑则诊断长期为空(只剩编辑防抖 500ms+打开 2.5s 兜底)。`semanticTokens.refreshSupport=false` 又与规格依赖服务器 refresh 矛盾。现有测试用假服务器无条件发送掩盖了缺口。
2. **worker.prompt 结果契约违背**(EXT↔PluginWorker,F-PW-13):贡献者抛异常/预算耗尽以 IpcError `prompt_failed`/`handler_error` 逃逸而非 `PromptOutput`;Host 不捕获 IpcRemoteException、不计失败预算不杀树,反复超时的提示词贡献者永不停用 worker。
3. **Claude 凭据并发刷新无锁**(Core C↔App,F-34):refresh token 轮换下并发回合可互相作废致回合失败、写回竞态覆盖旧 token;四种设备码 bundle 有串行规则,claude-subscription 直通路径没有。
4. **工作流 revoke 不终止在途 run**(EH↔Workflows,F-EH-12):revoke 杀 Worker 但工作流包无 Worker,Host 内 `WorkflowEngine.RunAsync` 不被取消,可在 revoke 返回后继续经 host.tool_call 调用主程序工具直到预算耗尽。终止方式待定义。
5. **写队列致命故障不可恢复**(Core B,B-29):FTS5 缺失时 messages_text 回退表不创建,append 失败使写队列永久致命(近乎理论,原生包自带 FTS5);另有跨进程同会话 seq 竞态。
6. **PipeSession 零拷贝所有权**(IPC,F-IPC-06):4 字节前缀跨段+帧体恰占满池数组的苛刻前提下,入站缓冲被 AdvanceTo 归还池复用,可致负载静默损坏。
7. **推送 lease 二次核对**(Workspace,F-WS-14):`LeaseHoldsAsync` 只比本地 HEAD 不重读 ls-remote,推送期远端前进的过期 lease 漏判。
8. **通知接线回归缺口**(App B,F-149/163):约 20 条通知路径仅审批/附件有接线测试,等级/key 漂移无守护。
9. **语言切换线程守卫未全覆盖**(App B,F-23):约 14 个订阅者缺创建线程守卫,headless 跨线程文本停旧语言(生产单 UI 线程不触发)。
10. **评审存储归属**(Workspace,F-WS-01):ReviewStore 对 workspace_path/file_path 无归属校验(规格已澄清评审存应用数据目录、不受工作区守卫)。

## 7. 需用户确认的业务事项

1. 步骤无 CommitId(提交失败但已 done)时,`request_review` 应回退为分支+未提交树评审(现行为,有测试且意图自述)还是显式报错?(Core A F-CORE-A-42)
2. 工作流 revoke 时在途 run 的终止方式:取消 run 还是任其预算耗尽?(EH F-EH-12)
3. UTF-8 拒绝通知范围:一律弹 Warning(规格)还是仅「声称文本」弹 toast(实现+测试钉住的窄行为)?(App G F-APP-G-04)
4. assistant 消息中的 FileBlock 被 Anthropic 静默丢弃是否可接受(与「绝不静默丢弃」全称句冲突)?(Core B B-114)
5. 缺失 key 日志级别取 Warning(实现)还是 Trace(规格)?(App B F-APP-B-18)
6. MCP 页是否需独立「重连」动作(规格承诺,实现需停→启两步)?(App F F-19)
7. skills 与 prompts 共用作用域/徽标约定是否要实现(规格 :432 承诺,未实现)?(App F F-66)
8. 跨规格大面积重复(App↔Core 提供商/认证/缓存/MCP/模板,已现漂移)是否启动以 Core 为唯一权威的去重任务?

## 8. 规格清理摘要

见 cleanup-report.md(54 条编号清理记录)。本轮直接修改原始规格:根级规格(分层表缺行/PluginWorker-run_code 矛盾/AOT 数字/codemode 与 IPC 过时叙述)、12 份已落地 task-spec 删除(推迟项迁移至模块 SPEC 未决节)、以及 Core/App/ExtensionHost/Scripting/Workspace/Editor/Mcp/Approval 的过时数值、内部矛盾、过宽全称量词、不存在类名、失实性能契约等。所有实现缺陷均保留规格要求并记录代码缺口,未通过改规格掩盖问题;业务含义不明者标待确认(§7)。未全量去重的跨规格重复与需精确重写的 instrument/调色板节已记录待后续。