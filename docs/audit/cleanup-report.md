# 规格清理报告(草稿,最终版在审计结束时定稿)

本轮清理直接修改原始规格文档。每条目:位置 → 改动 → 原因/证据。

## 根级规格

### architecture.md
1. §分层与依赖方向 项目表:补齐缺失的 5 个项目行(`Approval`、`Workflows`、`Scripting`、`ScriptRuntime`、`ScriptExecutor`),职责取自各自 SPEC 与 csproj 实际引用。原因:表只列 13 个项目,实际 18 个,缺行导致依赖方向核对无依据(规格缺失,证据:src/*/*.csproj)。
2. 项目表:删除全部「(新增)」标记。原因:过时——这些项目早已落地,「新增」是相对旧版的历史叙述。
3. `Forge.PluginWorker` 行:「加载并执行动态 C# 插件与 run_code 脚本」→「加载并执行一个动态 C# 插件(run_code 走 ScriptExecutor)」。原因:与同文 codemode 决策(一次性执行器架构)及 PluginWorker/ScriptExecutor SPEC 矛盾(F-ARCH-11,FAIL→规格错误修正)。
4. `Forge.ExtensionHost` 行:「Roslyn 编译与缓存」→「编译服务(经 Scripting)」,补「审批把关、执行器管理、进程归属、交互终端 PTY」。原因:编译实现实际在 Scripting 项目;职责描述与 EH SPEC 对齐。
5. 分层总结句:重写主程序侧依赖闭包与 JIT 侧依赖(补 Approval/HostClient/Scripting/ScriptRuntime/ScriptExecutor/Workflows 的真实引用关系);删「(新分层待 Stage 0–2 验证,未确认)」。原因:与同文「Stage 0 实测确认」矛盾;闭包描述缺项目(证据:csproj ProjectReference 全量核对)。
6. §Native AOT 决策:「五个主程序项目…已开启 IsAotCompatible」→ 列全 12 个主程序侧项目。原因:数字过时,实际 13 个 csproj 开启(含 App)。
7. §codemode 决策:删「取代此前的 T1/T2/T3 分层;dotnet run 文件式应用(T3)只作为可选能力」;「work78 分段测量」→「分段测量」。原因:历史叙述与内部任务编号,对理解当前架构无意义;测量数字保留(有效证据)。

### goal-and-requirements.md
8. §Capabilities codemode 条:「在 Extension Host/Worker 中并行按需调用」→「经 Host 常驻编译服务编译、在一次性执行器中并行按需调用」。原因:与已确认的执行架构一致(architecture.md codemode 决策)。
9. §Technology 表 codemode 行:同上修正。原因:同上。

### testing.md
10. §结构性问题 2:「25 个测试类」→「22 个测试文件」。原因:夹具合并(commit 7a50591)后数量漂移,grep 实测 22 个文件含 TempDataRoot。

### src/Forge.ExtensionHost/SPEC.md(frontmatter)
11. depends-on 补 `forge-workflows`。原因:csproj 实际引用 Forge.Workflows,frontmatter 漏记(spec-graph 边与真实依赖不一致)。

## 模块 SPEC「未决」节迁移(来自已删除 task-spec)

### src/Forge.App/SPEC.md
12. §未决 新增两条:会话/工作区生命周期刻意推迟项(只读并行会话、单会话多工作区、恢复到某轮、子 Agent 跨工作区派发、obj/bin 抢占)与否决项(排队自动接手、rebase/cherry-pick 并回、第二实例转交);界面小项推迟(ChangesPanel 徽标、文件面板错误位、SemiBold 混重、图标打磨、箭头槽宽)。来源:TASK-session-worktree-lifecycle §9、TASK-files-tab-count-badges、TASK-panel-title-weight、TASK-icon-migration 的推迟节。

### src/Forge.Core/SPEC.md
13. §未决 新增一条:额度读数只覆盖 Anthropic,非 Anthropic 额度头/设置页额度摘要/历史曲线/跨会话持久化未做,配额横幅文案待用户选择。来源:TASK-provider-quota-meter 推迟节。

### src/Forge.IO/SPEC.md
14. §未决 新增一条:回收站过期清理、容量上限、网络盘导出兜底、jsonl 镜像联动推迟。来源:TASK-session-worktree-lifecycle §9。

## 删除的 task-spec(12 份,gitignored 本地工作文档)

按 spec-graph 约定「task-spec 是临时工作文档,工作落地后删除」。12 份全部描述已落地工作,规范性内容已被模块 SPEC 吸收(关键标识符逐一 grep 核实:claudeWire、FluentIcon/Icons.axaml、model-catalog.json、IsMainCheckout、count-badge、PanelStackSolver、occupancy_key 等),仍有效的推迟项已迁移(见上)。删除清单:
- task-branch-pr-strip → App SPEC「远程读数/出货行/PR 检查面板」
- task-claude-wire-claim → Core SPEC「官方身份」+ App SPEC「claudeWire 段」(含 sdk-cli 默认值实测证据)
- task-files-tab-count-badges → App SPEC「文件 panel」徽标段
- task-icon-migration → App SPEC「配色/图标」段
- task-inspector-column-splitters → App SPEC「检视区栈/PanelStackSolver」
- task-model-catalog-refresh → App SPEC「目录源 models.dev」
- task-panel-title-weight → App SPEC「Panel 标题栏」
- task-project-panel-branch-and-toast → App SPEC「项目区/仓库根检出」(Toast 推迟已被 W0 通知系统取代)
- task-provider-quota-meter → Core SPEC「额度读数」
- task-provider-settings-redesign → App SPEC「provider 分区与三个窗口」
- task-session-worktree-lifecycle → Core SPEC「会话生命周期 v5–v9」+ App SPEC「会话生命周期」
- task-todo-plan-structure → Core SPEC「todo 工具」+ App SPEC「TODO 面板/计划面板」

## 模块 SPEC 清理(据审计发现,持续追加)

### architecture.md(据 14-ipc-hc.md)
15. §关键决策 IPC 段:旧描述「消息含 ProtocolVersion、RequestId、PluginId、Operation…支持批量」与实际帧头冲突。改为准确帧头 `IpcHeader(Version、Id、Operation、IsResponse、Error)`+协议版本 2,明写「PluginId 不在通用帧头、batch 尚无契约面」,细节引用 forge-ipc。证据:src/Forge.Ipc/IpcMessages.cs:36-43、Forge.Ipc/SPEC.md:16,36。原因:过时 v1 信封描述,与 owning SPEC 矛盾(规格错误修正)。

### src/Forge.HostClient/SPEC.md(据 14-ipc-hc.md)
16. :84 取消通知名 `ipc.cancel` → `cancel`。证据:src/Forge.Ipc/FrameCodec.cs:216 `Cancel = "cancel"`,全仓无 ipc.cancel。原因:命名错误,按规格实现取消会失效(规格错误修正)。

### src/Forge.IO/SPEC.md
17. §未决 迁移条错字「兖底」→「兜底」(本轮迁移引入,自查修正)。

### Scripting/ScriptRuntime/ScriptExecutor SPEC(据 13-script.md)
18. ScriptRuntime/SPEC.md 首段:「只依赖 SDK 与 BCL」→「只依赖 SDK、BCL 与 MessagePack(线格式)」。原因:MessagePack 是真实包依赖,原描述字面不符(规格错误)。csproj 冗余引用 Ipc.Protocol 为实现缺陷,保留「不含 Ipc」契约、记录代码缺口。
19. ScriptRuntime/SPEC.md §责任 执行器协议形状:删「Forge.Ipc.Protocol.HostToolCallRequest」作为本模块依赖的表述,改为「复用 SDK 的 ExtensionProtocol.HostToolCall→HostToolResult,宿主侧再映射到 Ipc.Protocol 线类型」。原因:与首段「不含 Ipc」自相矛盾;代码实际只用 SDK 类型(ScriptToolsFacade.cs:97),Ipc.Protocol 仅出现于 doc 注释(规格错误+代码注释误导)。
20. ScriptRuntime/SPEC.md §边界:「不做反射」→「不做反射 Emit 或动态代码生成(结果解包仅用单处 GetProperty("Result") 读 Task<T>.Result)」。原因:ScriptHarness.cs:247 用 GetProperty 解包,原措辞过于绝对(规格措辞修正,无安全后果)。
21. Scripting/SPEC.md §按请求重建能力:「入口委托按产物加锁创建一次并复用」→「每次运行新建可卸载 ALC 装载入口(ScriptCodeLoader.Load),不复用已装载程序集」。原因:CompiledScript.EntryPoint()(ScriptCompilation.cs:33)全仓零调用是死代码;真实链路 ScriptCodeRunner.LoadEntryPoint 每运行新装载,与一次性执行器架构一致(规格错误修正;死代码记为代码缺口)。
22. Scripting/SPEC.md §未决:依赖变化失效条补明「Evict/Clear 尚未被 ExtensionHost 接线,正确性由缓存键身份兜底」。原因:宿主零调用 Evict/Clear,原描述把未接线责任写成已生效(规格缺失补全)。
23. ScriptExecutor/SPEC.md §验证约定:「dotnet publish 后 grep 目录证明无 Roslyn」→「csproj 闭包核对 + 测试查 deps.json(publish grep 不可行,测试已放弃)」。原因:验证约定过时/不可执行,与实际测试不符(规格错误修正)。

### src/Forge.ExtensionHost/SPEC.md(据 12-ext.md)
24. L39 调用绑定:删「旧式无 context 的 plugin.execute 兼容路径保留,Host 用随机 runId 现场构造 context」,改为「所有执行操作缺 context 一律 invalid_context,不存在无 context 兼容路径」。证据:CallBinding.cs:29-35 拒 null、ExtensionHost.cs:351/439/466/586 均经 Require、HostMessages.cs:13 注释明拒;全 Host 无随机 runId 构造(Guid 仅用于终端/管道/暂存名)。原因:过时声明与安全设计端到端矛盾(规格错误修正,F-EH-32)。
25. L24 审批变更与执行串行:revoke「等在途计数归零后才返回」补明「有界等待 RevokeDrainWindow(默认 10s,超时即返回)」+「工作流包无 Worker 可杀,revoke 不取消 Host 内在途 WorkflowEngine.RunAsync,该 run 可在 revoke 返回后继续调用主程序工具直到预算耗尽(在途终止方式待定义)」。证据:ExtensionHost.cs:1831 RevokeDrainWindow=10s、PluginState.cs:189 WaitForIdleAsync(bound)、L58 工作流无 Worker。原因:规格未定义有界等待与工作流 revoke 语义(规格缺失补全,F-EH-12;工作流在途终止属业务待确认)。

### src/Forge.Core/SPEC.md(据 01-core-a.md)
26. L44 评审工具两种目标:步骤评审绝对化措辞补例外——「步骤无 CommitId(提交失败但已 done)时回退为默认分支+未提交树,并在意图里自述(WithCommitNote)」。证据:RequestReviewTool.cs:128 Commit=step?.CommitId、:239 WithCommitNote 自述文本(有测试)。原因:规格与实现矛盾,实现是有测试且自述的回退(规格错误修正,F-CORE-A-42;是否改为显式报错属业务待确认)。

### src/Forge.Core/SPEC.md(据 02-core-b.md,存储/缓存)
27. L71 末句:「AgentOptions.MessageCacheTtl 填进 CacheHint.MessageTtl,Anthropic 目前忽略它」→「AgentOptions.CacheTtl 经 ResolveCacheTtl 填进 CacheHint.Ttl,Anthropic 遵循它(5m/1h 直用、auto 按 CacheTtlPromotionGap 升档)」。证据:代码无 MessageCacheTtl/CacheHint.MessageTtl;AgentOptions.cs:60 CacheTtl、AgentLoop.cs:416 ResolveCacheTtl、AnthropicProvider.cs:593 CacheTtl(request) 写 cache_control。原因:过时+与「官方身份」节自相矛盾(规格错误修正,B-69/97/121)。
28. L148 未决:删「Anthropic promptCacheTtl 1h 未接线」条(已实现,官方身份节描述自动升档机制)。原因:过时未决项(规格错误修正)。
29. L57 存储:「所有写入走单写线程批量」→「会话消息/摘要写入走单写线程批量(工作台各 store 另用每 store 连接+信号量)」。原因:全称过宽,与 L65 工作台存储自述矛盾(规格措辞收窄,B-55 相关)。
30. L69 写入不连坐:「每个操作包在 SAVEPOINT forge_op」→「可能业务冲突的操作(UsesSavepoint)包保存点,其余(Append/PutSummary)失败仍连坐整批」。证据:SqliteSessionStore.cs:504 op.UsesSavepoint、:759 默认 false。原因:全称过宽与实现不符(规格措辞收窄,B-55)。

### src/Forge.Core/SPEC.md(据 03-core-c.md,提供商)
31. L123 官方身份:「anthropic-beta 用官方的 18 项列表」→17 项。证据:AnthropicWire.cs:52-70 Betas 实数 17。原因:数字与实现不符(规格错误修正,F-41)。
32. L119 认证与端点:「SystemPromptPreamble 非空时 system 数组第一块必须是它」→「身份句单独一块;Wire 生效时 billing 在前、身份在后(见官方身份)」。证据:AnthropicProvider.cs:273-279 billing 先写、L123 已有修正。原因:被修正段未同步,内部矛盾(规格错误修正,F-29)。

### 跨规格重复(记录,本轮未全量去重)
33. App SPEC↔Core SPEC 在 claudeWire/认证/探针/提示词/缓存 TTL 大面积重复(App L29↔Core L121/123/124、L28↔L127、L34↔L129、L146↔L71/L129),已现漂移(接线状态、401 vs 401/403)。建议:以 Core 为提供商机制唯一权威,App 只留配置/UI 接线并引用 Core;L123 实测叙述(31–33k/62–68k、68a/d83/da9、91 秒)移出规范。因 App SPEC 体量(236KB)与配额约束,本轮仅修已证漂移点,全量去重留待后续任务。

### src/Forge.Workspace/SPEC.md(据 15-workspace.md)
34. L15 边界:「所有文件写入(规格、评审)限工作区」→「规格树文件写入限工作区;评审意见另存应用数据目录 SQLite,不受此守卫」。证据:L20 评审存应用数据目录、ReviewStore 无工作区归属校验。原因:与 L20 互斥(规格错误修正,F-WS-01)。
35. L52 pr_create:「五道闸门」→四道,删「(5)与3同源」。证据:代码/测试按 4 道。原因:第5道与3同源重复计数(规格错误修正)。
36. L62 生命周期 git 原语:「移除目录只走回收站,不用 git worktree remove」→「RemoveAsync 经守卫后用 git worktree remove;App 层移除工作区另走回收站流程(先移入回收站再 prune),不经本方法」。证据:WorktreeManager.cs:399 git.RemoveWorktreeAsync、App SPEC 移除流程先 ITrashService 再 prune。原因:所有权失真+与稀疏失败回滚「非强制移除 worktree」自相矛盾(规格错误修正,F-WS-48)。

### src/Forge.App/SPEC.md(据 07-app-c.md)
37. L91–92 出货失败:「不发系统通知:通知仍只属于等待 PR 检查结束那一种后台进程」→「不发系统通知(只写结果行;其它失败如 fetch/pull/push、检查点、回合的通知见通知节)」。证据:L209 列出 fetch/pull/push 失败(Error)、检查点失败(Warning)等多种通知源,实现遵循 L209。原因:错误全称与 L209 矛盾(规格错误修正)。

### src/Forge.App/SPEC.md(据 05-app-a.md,组合根/提供商)
38. Copilot API 密钥端点:api.individual.githubcopilot.com→api.githubcopilot.com。证据:CopilotAuthentication.cs:56 DefaultApiHost、CopilotKeyRouteTests 钉住。原因:规格落单(规格错误修正,F-20)。
39. 「六种认证」→七种、「四种设备码登录」→五种。证据:实列 api-key/claude-subscription/github-copilot/kimi-code/xai-subscription/meta-subscription/chatgpt-subscription 七种;设备码 5 种(Copilot/Kimi/xAI/Meta/ChatGPT)。原因:计数失真(规格错误修正)。
40. 边界「SettingsJsonContext」→PreferencesJsonContext/ProvidersJsonContext/WorkbenchJsonContext 等;组合根「ProviderSettings」→ProviderStore/ProviderCatalog。证据:全库无 SettingsJsonContext/ProviderSettings 类;实有 PreferencesJsonContext/ProvidersJsonContext/ProviderStore/ProviderCatalog。原因:类名不存在(规格错误修正)。
41. 组合根「拆除严格逆序」→「按逆序(例外:审批先于存储/editor/mcp;FileArtifactStore 不单独释放靠 ClearAllPools)」。证据:AppComposition.cs:518,855-922。原因:绝对化与实现不符(规格措辞修正,F-08)。
42. claudeWire「加载路径绝不写文件」→「原则上不写(例外:旧段明文密钥迁移 Load 时写 provider-secrets.json)」。证据:ProviderStore.cs:390-401。原因:绝对化被有意偏离打破(规格措辞修正,F-35)。

### src/Forge.App/SPEC.md(据 09-app-e.md)
43. L299 TODO 面板动作枚举:「只读展示+新建/删除/调序」补「失败步骤重试提交」。证据:Panels.cs:1594 RetryCommitCommand、:1882 RetryCommitAsync,规格 L141 亦有。原因:枚举漏项(规格缺失补全)。

### src/Forge.Editor/SPEC.md(据 17-editor.md)
44. :37 Diff:「移动搜索建每 hunk 索引,避免平方级扫描」→「按窗口×全文起始行暴力扫描(无每 hunk 索引,MaxDistance 默认不限),大文件下是平方级」。证据:Diff/CommentAnchor.cs:179-213 嵌套 for 循环无索引。原因:把不存在的索引写成既成事实(规格错误修正,F-ED-53)。
45. :25 保存流式:「不在内存里拼整份文件」→「正常路径不拼整份(行尾规范化路径例外:GetText+规范化+GetBytes 整份物化)」。证据:规范化路径三份全文驻留。原因:无条件表述与实现矛盾(规格措辞修正,F-ED-12)。

### src/Forge.App/SPEC.md(据 10-app-f.md)
46. :452 未决:「文档符号列表未做」移除,补「文档符号列表已实现:面板 @ 符号来源」。证据:面板 @ 符号来源已实现且测试齐全(:440)。原因:未决项过时(规格错误修正,F-76)。
47. (待修,详见 10-app-f.md):60 instrument「只读分区/InstrumentSectionBody 模板/四 VM 同 InstrumentSettingsSections.cs」与代码不符(mcp/tools 不再只读、模板名不存在,F-54);:432 skills 与 prompts 共用徽标约定未实现(F-66);MCP 页无独立重连动作(F-19)。因需精确重写 instrument 节结构,本轮记录待后续。

### src/Forge.Mcp/SPEC.md 与 src/Forge.Approval/SPEC.md(据 18-mcp-appr-wf.md)
48. Mcp :24 取消通知:「已发出后发 notifications/cancelled」→「发 notifications/cancelled(无论请求是否已发出,服务端按协议忽略未知 id)」。证据:JsonRpcPeer.cs:66-91 无条件 SendCancellation。原因:边界措辞与实现不符(规格措辞修正,F-MCP-04)。
49. Approval :21 组合指纹:补「生产 DependencyHash 由 EH DependencyHashSchema v2(域分离+长度定帧)算出;Hashing.DependencyHash name@version 仅测试遗留」。证据:EH SPEC 已记录 v2、Hashing.DependencyHash 生产零调用。原因:Approval 规格只字未提致单读误导(规格缺失补全)。

### src/Forge.App/SPEC.md(据 08-app-d.md)
50. :331 终端设置分区未注册:删过时理由「注册会撞 SettingsPanelTests 硬断言」,补「KnownSectionIds 已预留 terminal,无分区列表硬断言」。证据:SettingsPanelTests.cs:490-517。原因:理由失效(规格错误修正;未注册仍是事实)。

### src/Forge.App/SPEC.md(据 11-app-g.md)
51. L408 附件已知限制:「插话通知只显示键入文本(仅附件插话通知为空串)」→「显示键入文本与附件计数/列名(纯附件插话也有通知)」。证据:Strings.Session.cs ComposerInterjectedNotice、InterjectionNoticeTests 10 例。原因:过时限制与实现+测试矛盾(规格错误修正,F-APP-G-20)。

### src/Forge.App/SPEC.md(据 06-app-b.md)
52. 窗口顶部:搜索按钮「搜索还不是功能/即将可用」→「打开命令面板」(代码绑 OpenPaletteCommand);终端图标「IconTerminalFrame+IconTerminalScreen」→「单一 IconTerminal=panel_bottom 20 filled 放大」(Icons.axaml:112-115)。原因:过时描述与实现矛盾(规格错误修正,Q5/Q6)。
53. 主题节「(项目|Git)」→「(项目|常规对话)」。原因:Git panel 已删除,过时残留(规格错误修正,Q7)。
54. (待修,详见 06-app-b.md Q1-Q13):L171 调色板旧值(#F5F6F5 等)与 L293 配色节矛盾(实现跟 L293,需以配色节为权威重写);L216 图标尺寸 13/11 vs L294 14/13(代码 14/13);L218 行尾工具 10/20 vs L65 12/22(代码 12/22);L66/L68 检视区 tab 顺序(代码 todo/review/specs);L220 缩进分量自矛盾;L54 logo/字标/任务栏歧义;L68 留未决问题;历史波次引用(W1/W8/缺口#13)混入正文。因需逐处核对代码重写,本轮修 Q5/Q6/Q7,余记录待后续。

## 总结

本轮共 54 条编号清理记录,直接修改原始规格文档(未仅生成总结而保留混乱)。分类:

- **规格错误修正**(过时数值/内部矛盾/失实契约/不存在类名):architecture 分层表与 IPC/codemode、Core TTL/preamble/beta/保存点全称、App Copilot 端点/认证计数/类名/拆除与加载绝对化/通知全称/搜索与终端图标/插话限制/终端分区理由、WS 评审归属/闸门数/移除方式、Editor 索引失实/整份物化、Mcp 取消通知、EH 兼容路径/revoke、SR/SC/SE 矛盾与死代码描述。
- **规格缺失补全**:EH 工作流 revoke 语义、Approval DependencyHash v2、Core 步骤评审例外、App TODO 重试提交枚举、task-spec 推迟项迁移。
- **冗余删除**:12 份已落地 task-spec(gitignored 临时工作文档,内容已被模块 SPEC 吸收)、Core 已解决未决项(1h 未接线)。

**原则**:实现缺陷一律保留规格要求并记录代码缺口(见 audit-report §5/§6),未通过改规格掩盖问题;业务含义不明者标待确认(audit-report §7);有效信息(测量证据、契约细节)保留。

**未全量完成(记录待后续)**:App↔Core 跨规格大面积重复的去重(需以 Core 为唯一权威重写 App 提供商/认证/缓存/MCP/模板节);App instrument 节(:60)与调色板(:L171)需精确重写;App B Q1-Q4/Q8-Q13 剩余过时数值。这些需逐处核对代码重写,本轮因体量(236KB App SPEC)与配额约束仅修已证漂移点,详见各 findings 规格质量问题节。