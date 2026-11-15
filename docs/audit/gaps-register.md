# Forge 审计缺口与未完成项登记册

来源:19 份 findings + spec-implementation-matrix.md 的全部非 PASS 功能点(约 90 项)与反向发现。按性质分类,标注状态。
状态:🔴 待改代码 / 🟡 待补测试 / 🔵 需运行时/平台验证 / 🟣 规格待补定义 / ✅ 规格已修(无需再动) / ⚪ 规格自述未完成功能。

## 一、代码缺口(实现偏离规格,需改代码)🔴

### 架构/依赖
- **F-ARCH-02** Forge.Editor.csproj 冗余引用 Abstractions(代码零使用)→ 删引用。
- **F-ARCH-03 / F-SR-01** Forge.ScriptRuntime.csproj 冗余引用 Ipc.Protocol(代码零使用,违反「不含 Ipc」)→ 删引用。

### Core 存储/压缩
- **F-CORE-B-29** FTS5 缺失时不创建 messages_text 回退表,append/search 指向它→写队列永久致命(近乎理论,原生包自带 FTS5)→ 建回退表或显式降级。
- **F-CORE-B-35** messages.at/summaries.at 用宽容 ParseTimestamp,违反「严格读取指名报错」;v3 残留无 offset 值被静默按本地时区解析 → 改严格读取。
- **F-CORE-B-55** 保存点只包可能冲突的 op,Append/PutSummary 失败仍连坐整批(规格已收窄措辞,实现与收窄后规格一致;若要全操作隔离需扩保存点)→ 视决策。
- **F-CORE-B-102** 取消令牌传导:转存用回合令牌而非调用令牌(边界)→ 核实。
- **F-CORE-B-114** assistant 历史 FileBlock 被 Anthropic 静默丢弃(决策 4:可丢但须保语义占位、不影响轮次)→ 加占位。

### Core 提供商
- **F-CORE-C-13** 400 拒 x-grok-conv-id/X-Interaction-Id 头不触发一次性降级(CanDegrade 只匹配 body 字段)→ 扩降级匹配。
- **F-CORE-C-34** Claude 凭据并发刷新无锁,refresh token 轮换下并发回合互相作废、写回竞态覆盖旧 token(四种设备码 bundle 有串行,此路没有)→ 加串行锁。
- **F-CORE-C-54** Copilot 交换请求 Editor-Plugin-Version 误发 `Forge/1.0`(应 `forge/1.0`),测试固化了错误值 → 改值+改测试。
- **F-CORE-C-05** Copilot-Integration-Id 实为 App 注入而非「适配器本来就发」(规格已澄清)→ 核实接线。
- **F-CORE-C-38** 两类重置时间格式(unix 秒/RFC3339)解析覆盖 → 核实。

### App 组合根/提供商
- **F-APP-A-08** 拆除非「严格逆序」(审批先于存储/editor/mcp);FileArtifactStore 不在 DisposeAsync 单独释放,靠 ClearAllPools(规格已补例外)→ 视决策是否单独释放。
- **F-APP-A-11** 旧单 provider 段首读迁移路径 → 核实。

### App 工作台/通知
- **F-APP-B-23** 语言切换约 14 个订阅者缺创建线程守卫(LanguageSettings/Appearance/Terminal×3/Approval×3/Compaction/ModelsVisibility/Prompts/UsageLog/Network/NewBranchDialog),headless 跨线程文本停旧语言(生产单 UI 线程不触发)→ 补守卫或规格承认 post 模式。
- **F-APP-C-03** compare 回退 `Contains("github.com/")` 误命中 notgithub.com;CompareUrl 仅限未提交分支,已发布+gh 缺失回退不可达;禁用提示误报「没有配置远程」→ 修宿主判定+回退可达性。
- **F-APP-C-40** 检查点失败 catch 内同一 Notify 重复两次(缩进错位,编辑事故),用户见失败×2 → 删重复。

### App 编辑器/LSP/终端
- **F-APP-D-48 / F-ED-40** LspClient 未声明 workspace.diagnostics.refreshSupport,Roslyn 加载完不通知重拉,用户不编辑则诊断长期为空(假服务器测试掩盖)→ 声明能力。
- **F-APP-D-25** semanticTokens.refreshSupport=false 与规格依赖服务器 refresh 矛盾 → 统一能力声明。
- **F-APP-D-04** 20000 行不折叠只挡本地启发式,服务器 foldingRange 无条件应用 → 统一上限。
- **F-APP-D-11** 暗色选区 #4D5AA9FF 色相偏离品牌蓝 #3399FF → 校色。
- **F-ED-20** .cmd/.bat 启动用 /e:ON /v:OFF /d /c(无规格指定的 /s),引号规则复杂且零测试 → 核实/补测试。

### App 设置/MCP/附件
- **F-APP-F-66** skills 分区无来源徽标(决策 7:实现 内置/全局/项目 徽标,与 prompts 一致)→ 实现。
- **F-APP-G-04** UTF-8 拒绝仅「声称文本」弹 toast,未知扩展二进制只内联拒绝(决策 3:一律弹 Warning)→ 扩通知。
- **F-APP-G-71** 预览竞赛输家释放只断言赢家 ReleaseCount,输家释放不可观察,位图泄漏回归无守护 → 补输家断言。

### ExtensionHost / PluginWorker
- **F-EH-12** revoke 不取消工作流在途 run(决策 2:取消在途+提示)→ 接取消令牌+通知。
- **F-EH-70** POSIX 终端杀树失败仅在 exitCode<0 计报告;shell 退出码可观测但后代残留时不上报,Host 可能 exit 0 留残留 → 补上报。
- **F-PW-13** worker.prompt 异常/预算耗尽以 IpcError prompt_failed/handler_error 逃逸而非 PromptOutput;Host 不捕获 IpcRemoteException、不计失败预算不杀树,反复超时贡献者永不停用 → 修结果契约+失败预算。

### Scripting
- **F-SCRIPT-30** CompiledScript.EntryPoint() 全仓零调用(死代码);真实链路每运行新建 ALC → 删死代码。
- **F-SCRIPT-45** 依赖变化失效 Evict/Clear 宿主零调用(缓存键身份兜底正确性)→ 接主动失效+内存卫生。

### IPC
- **F-IPC-06** PipeSession.EnqueueAsync 零拷贝所有权判定(Offset==0 && Count==全长)在「4 字节前缀跨段+帧体恰占满池数组」苛刻前提下,入站缓冲被 AdvanceTo 归还池复用→负载静默损坏 → 单段一律 ToArray。

### Workspace
- **F-WS-14** LeaseHoldsAsync 只比本地 HEAD 不重读 ls-remote,推送期远端前进的过期 lease 漏判;「Everything up-to-date」分支零测试 → 重读 ls-remote。
- **F-WS-11** git 子进程移除 GIT_* 变量清单 → 核实。
- **F-WS-32** AOT/源生成核实 → 核实。
- **F-WS-58** 索引刷新默认信任长度+mtime,同长度保时间戳修改需 force → 核实。

### Mcp
- **F-MCP-04** 取消通知无条件发送(含未发出预取消;规格已修措辞,服务端忽略未知 id,影响轻微)→ 视决策加发送标志。

## 二、缺测试(实现存在,需补回归)🟡

- **F-CORE-A** MaxTurns 续跑耗尽、todo 仅根可见、扩展工具白名单 pin、自动评审未接线(显式未决)。
- **F-CORE-B-34** 计划面板只读历史/恢复不走 UI 写路径。
- **F-APP-A-39**(最高风险)CredentialedChatProvider 首事件前 401 刷新重试一次/之后不重试——整条无测试(防重复输出关键不变量)。
- **F-APP-A-55** settings.json 实 7 个写者均接 SettingsFileMutex,但无并发不丢段测试;锁仅进程内,双实例互丢段未入规格。
- **F-APP-B-149/163** 通知接线约 20 路径仅审批/附件有回归,等级/key 漂移无守护。
- **F-APP-C** 检查面板世代号竞态、无上游零 gh 调用计数、轮次号续接规则。
- **F-APP-D-61** VT 吞吐 99MiB/s/稳态零分配只有 5s 宽松预算测试,无分配断言。
- **F-APP-E** TODO 拖拽 SameLayer、单实例锁持有至 Shutdown、冲突快照 4 并发/Epoch 竞态。
- **F-APP-F-35** /compact 发送拦截+附件门分支零测试。
- **F-APP-G** 克隆超时 GitException.Message 呈现、运行中关窗=取消视图否决分支。
- **F-EH-77** host_shutdown_failed 清理失败分支零测试(实现存在)。
- **F-WS-18/19** diff 诚实性契约 GitOutputTruncatedException(全套件 0 引用)/hunk 行数不符 IsCorrupt 零测试。
- **F-WS-28** 跨进程碰撞重试上限 20/耗尽分支零测试(并发测试全在同进程门内)。
- **F-IPC-11/14/02** 客户端连接超时、HostToolCalls denied 映射、传输单用性拒绝零测试。
- **F-MCP-10/13** HTTP 代际守卫、名字生命周期不重绑定缺直接用例。
- **F-APPR-09** ApprovalStore.ListReadOnlyAsync 零测试(设置页 approvals 唯一数据源,fail-closed;同类 CountReadOnlyAsync 有 6 测试,不对称)。
- **F-ED** 字符串 id 回传、500ms 超时重试、include 环检测、用户语法目录、同步屏障、能力载荷断言。
- 测试基建:Workspace 14 文件约 183 处 `if(!GitProbe.IsAvailable)return;` 静默跳过(宿主缺 git 时绿而不跑)。

## 三、需运行时/其他平台/真实服务端验证 🔵

- **F-EH-24/73** macOS 进程归属/PTY 分支(WNOWAIT 0x20、si_pid 偏移 12、pipe+fcntl、O_CLOEXEC/SETSID/TIOCSWINSZ Darwin)——规格自述未验证,需 macOS CI。
- **F-APP-D-88** 真实 LSP/PTY 运行时(Roslyn 项目加载、真实 shell 前台进程探测、真实退出码)——全 ScriptedServer/FakeTerminalHost。
- **F-CORE-C-77** 服务端行为断言(缺身份块 400、billing 首块受理、axios UA 免限流、X-Interaction-Id 命中率、sdk-cli 成本减半、limit=1000 接受)。
- **F-APP-D-54** VT 引擎 AOT 无警告。
- **F-ED-35** 语言服务器发现只启动跑一次/失败 trace。
- **F-TEST-05/07** LiveSwitch 修复未重跑、基线表用例数/耗时历史记录。
- **F-APP-E-57** 提供商缓存配置解析未追踪到存储实现。
- PR 平台真实 gh/GitHub 集成面(全 fake-gh)。

## 四、规格待补定义 🟣

- **F-CORE-C-34** Claude 凭据并发刷新保护规格未规定(与代码缺口同项)。
- **F-APP-B-123** 主题亮/暗调色板数值:L171 旧值 vs L293 实现跟随,需以 L293 为权威重写 L171。
- **F-APP-F-54** instrument 节(:60)「只读分区/InstrumentSectionBody 模板/四 VM 同文件」与代码不符(mcp/tools 不再只读、模板名不存在),需精确重写。
- **F-SR-12** 内层工具调用协议形状(HostToolCall/HostToolCallRequest→ToolResult)规格表述需理清。
- **F-WS-07** PR 工具清单与副作用:规格漏 pr_comments/review_comments(代码实有 11 个),需对齐。
- **F-ED-67** 编码检测与保存格式(BOM/宽编码/Latin1 回退/二进制拒绝/行尾保持与规范化,27 测试)整体未入规格。
- 安全默认未入规格:tools.register 未定义 SideEffect→Execute、服务端 CurrentUserOnly、64 并发门、命令审批命名空间。
- 反向未记录行为(各 findings):DnsCache 容量 256/maxTtl 1h、AutomationEngine 恢复 Limit 1000、grep/find 硬上限 10000、usageLog 段+JsonlUsageRecorder、关派生同时禁用 request_review、ChatMermaid* 死设置、图片读前 80MiB 宽松上限等。

## 五、规格已修(FAIL/过时项,已处理,无需再动)✅

F-ARCH-11(PluginWorker/run_code 矛盾)、F-CORE-B-69/97/121 与 F-CORE-C-70/74(TTL 过时)、F-CORE-C-29(preamble 顺序)、F-CORE-C-41(beta 18→17)、F-EH-32(无 context 兼容路径)、F-SCRIPT-30(委托复用死代码描述)、F-SR-01(依赖自相矛盾)、F-ED-53(每 hunk 索引失实)、F-APP-B-228/230(搜索按钮/终端图标过时)、F-APP-G-20(插话通知空串)、F-APP-A-20/35(Copilot 端点/加载写文件绝对化)、F-WS-48(移除方式)、F-MCP-04(取消通知措辞)、F-APP-F-76(文档符号列表已实现)、F-APP-B-18(缺失 key Warning,决策 5)、F-APP-F-19(MCP 重连=停→启,决策 6)。

## 六、规格自述未完成功能(非缺陷,产品决策)⚪

- **F-APP-E-42** 交接摘要沿继承链取父、对话框选模型/档位及四类提示(缓存剩余/接近压缩/前缀指纹不符)。
- 编辑器:code action、重命名、inlay hints、语义 token range 请求、声明式插件面板渲染。
- Mermaid:回复定稿后未闭合围栏只显源码不渲染(与 CommonMark 不一致)。
- **F-APP-F-80/81/84/87** 继承超线迟滞根因(归 T0)、降级只推送不开 PR、Worker 资源限制受威胁模型门控、界面小项推迟(ChangesPanel 徽标/刷新失败呈现位/SemiBold 混重/小图标/箭头槽宽)。
- 完整继承跨不同前缀 ThinkingBlock 可能被拒、search_history/read_history 不沿继承链。

## 七、跨规格去重剩余(决策 8 已做高漂移目标)

- App 压缩/Web/模板节与 Core 的剩余重复。
- App instrument 节(:60)、调色板节(:L171)精确重写。
- App-B Q1-Q4/Q8-Q13 剩余过时数值(图标尺寸 13/11 vs 14/13、行尾工具 10/20 vs 12/22、检视区 tab 顺序、缩进分量自矛盾、logo/字标/任务栏歧义、未决问题混入正文、历史波次引用)。

## 汇总

| 类别 | 数量(约) | 性质 |
|---|---|---|
| 代码缺口 🔴 | 35 | 需改代码(本轮未改,留任务) |
| 缺测试 🟡 | 25+ | 需补回归 |
| 需验证 🔵 | 12 | 运行时/其他平台/真实服务端 |
| 规格待补 🟣 | 8+ | 需补定义/重写 |
| 规格已修 ✅ | 18 | 已处理 |
| 未完成功能 ⚪ | 10+ | 产品决策,非缺陷 |

**最高优先**(安全/数据完整性):F-IPC-06(负载损坏)、F-CORE-C-34(凭据并发)、F-PW-13( prompt 契约+失败预算)、F-EH-12(revoke 在途)、F-APP-D-48/F-ED-40(诊断刷新)、F-CORE-B-29(写队列致命)、F-WS-14(过期 lease)、F-APP-A-39(401 重试无测试)。
---

## 修复状态(本轮已完成,worker=qwen3.8-max:high,测试=deepseek-flash:max,review=qwen4-max-preview)

### 已修复代码缺口(均副本内修复+测试绿+变异验证+单文件拷回)
- **F-ARCH-02/03** ✅ 主Agent 删 Editor→Abstractions、ScriptRuntime→Ipc.Protocol 冗余引用(build 0/0)。
- **F-SCRIPT-30** ✅ 删 CompiledScript.EntryPoint 死代码+gate/entry 字段(Scripting 269/269)。
- **F-APP-D-48/ED-40** ✅ LspClient 声明 workspace.diagnostics.refreshSupport=true(核实客户端处理 refresh)。
- **F-APP-D-25** ✅ semanticTokens.refreshSupport false→true。
- **F-APP-D-04** ✅ 折叠 20000 行上限移入 RecomputeAsync 入口,服务器 foldingRange 同受约束。
- **F-APP-D-11** ✅ 暗色选区回归品牌蓝 #3399FF。
- **F-ED-20** ✅ 批处理命令行补 /s + 22 测试(含真实 cmd e2e)。
- **F-WS-14** ✅ LeaseHoldsAsync 重读 ls-remote 远端 tip+fail-closed(变异验证真实复现过期 lease)。
- **F-WS-18/19** ✅ GitOutputTruncatedException + hunk IsCorrupt 测试。
- **F-WS-28** ✅ 真跨进程 RacingGit 夹具,重试-2 后缀/20 次耗尽测试。
- **F-MCP-04** ✅ sent 标志(已发出后才发 cancelled,预取消不发),规格同步。
- **F-MCP-10/13** ✅ HTTP 代际守卫真 SSE 夹具 + 名字碰撞生日搜索消歧测试。
- **F-APPR-09** ✅ ListReadOnlyAsync 七例(含坏库 fail-closed)。
- **F-CORE-C-34** ✅ Claude 凭据按文件路径 SemaphoreSlim 串行+门内双检(变异验证)。
- **F-CORE-C-54** ✅ Copilot Editor-Plugin-Version→forge/1.0(Core+App 两侧)。
- **F-CORE-C-13** ✅ CanDegrade 匹配 x-grok-conv-id/X-Interaction-Id 头降级。
- **F-CORE-B-114** ✅ 助手历史 FileBlock→占位(决策4,用户附件仍构造期抛错)。
- **F-CORE-C-05/38** ✅ 核实 Copilot-Integration-Id 为 App 注入 + 交叉格式丢弃测试。
- **F-CORE-B-29** ✅ FTS5 缺失同事务补建 messages_text 回退表+只回填缺失行(变异验证)。
- **F-CORE-B-35** ✅ messages.at/summaries.at 严格 ParseCanonicalTimestamp(指名行键)。
- **F-CORE-B-55/102** ✅ 隔离/连坐测试钉住现状 + 转存令牌边界测试。
- **F-IPC-06** ✅ FrameExtractor 帧体一律 ToArray(变异验证真实复现负载损坏 Expected[171]Actual[255])。
- **F-IPC-11/14/02** ✅ 连接超时/HostToolCalls denied 三态/传输单用性测试。
- **F-EH-12** ✅ revoke 取消在途工作流 run(引擎令牌链到 run 生命周期)+提示(决策2)。
- **F-EH-70** ✅ 终端残留上报不依赖 exitCode<0。
- **F-EH-77** ✅ Debug 接缝 FORGE_HOST_TEST_FAIL_CLEANUP 验证清理失败分支。
- **F-PW-13** ✅ HandlePromptAsync 捕获异常归类 prompt_failed + Host 计失败预算达阈停用。
- **F-APP-C-40** ✅ 删重复 Notify + 三检查点失败 Theory。
- **F-APP-C-03** ✅ WebUrlOf 精确 github.com 判定 + CompareUrl 去「仅未发布」+ 禁用提示新 key。
- **F-APP-B-23** ✅ 共 33 订阅点补 CurrentManagedThreadId 守卫(14+19),Theory 25 例+控件 3 例。
- **F-APP-A-39** ✅ 401/403 首事件前重试一次/后不重试 4 例。
- **F-APP-A-55** ✅ 五写者×30 轮 Barrier 并发不丢段(变异验证)。
- **F-APP-B-149/163** ✅ NotificationWiringTests 3→11 例。
- **F-APP-F-66** ✅ skills 来源徽标(决策7,复用 prompts.scope.* 键)。
- **F-APP-G-04** ✅ 分类拒绝统一弹 Warning(决策3)。
- **F-APP-F-35** ✅ /compact 发送拦截三例。
- **F-APP-G-71** ✅ 真位图竞赛输家释放测试+删弱桩。

### 已补规格缺口
WS-07 工具清单、Core 凭据并发串行、Core FTS 回退、HostClient 安全默认(SideEffect→Execute/并发门8)、Editor 编码契约(ED-67)、IO grep/find 硬上限、Core DnsCache 默认、App 调色板 L180 引用配色节、App instrument 节模板名、IPC:19、skills SPEC:60。

### 仍剩余(诚实记录)
- **F-APP-B-149/163 环境侧 6 路径**(扩展宿主失败/模型列表失败/preferences-problem/startup-failed/projects-problem/purge-cleanup/评审存储失败)接线测试:卡在缺生产测试接缝,需先加接缝。
- **4 控件守卫测试**(DiffEditorView/MermaidBlockView/TimelineGutter/ReviewOverlay):守卫已落地编译通过,缺哨兵+夹具组合的用例。
- **FileArtifactStore.cs:132 artifacts.at** 仍宽松 ParseTimestamp(无守卫、不在 TimestampColumns,非 B-35 范围);降级模式 PurgeOp 只清 messages_text 不清 messages_fts(既存行为)。
- **F-SCRIPT-45** Evict/Clear 主动失效接线(内存卫生,正确性有缓存键身份兜底)暂缓。
- **UNVERIFIED**:F-EH-70 POSIX 真实后代残留、F-EH-77 真实不可杀进程(需 Linux CI)、F-CORE-C-77 服务端行为、真实 LSP/PTY/gh 集成。
-  minor 反向行为文档(usageLog 段、关派生禁 request_review、ChatMermaid 死设置)待补。
- 决策3 范围:图片路径 AddImageAsync Reject 与读文件失败仍只内联(决策3 仅统一分类拒绝,如需扩需再裁决)。
