# 测试历史基线与债务(原 testing.md 的非规则部分,2026-11-15 迁出)

testing.md 的规则部分已简化并入 `AGENTS.md`「测试分层与执行」;本文件保留历史测量、结构性问题、夹具登记与已知债务,供参考,不是行为规则。

## 现状基线(2026-11-14 实测)

13 个测试项目。用例数取 `dotnet test` 统计;耗时为同次运行墙钟,机器 32 核、产物预热、期间另有其他工作区跑测试(偏保守)。

| 测试项目 | 用例数 | 全量墙钟 | 备注 |
|---|---|---|---|
| `Forge.App.Tests` | 4,321 | 985 s | 累加≈墙钟,完全串行 |
| `Forge.Core.Tests` | 2,190 | 50 s | 并行健康(15×) |
| `Forge.Workspace.Tests` | 1,345 | 375 s | 真实 git 进程(7.4×) |
| `Forge.Editor.Tests` | 513 | 5 s | |
| `Forge.HostClient.Tests` | 503 | 10 s | 命名管道+FakeHost |
| `Forge.IO.Tests` | 451 | 19 s | 真实文件系统与子进程 |
| `Forge.Mcp.Tests` | 307 | 18 s | |
| `Forge.Integration.Tests` | 265 | 124 s | 跨进程 E2E |
| `Forge.Scripting.Tests` | 264 | 11 s | 一次性执行器进程 |
| `Forge.Approval.Tests` | 188 | 10 s | |
| `Forge.Workflows.Tests` | 148 | 5 s | |
| `Forge.Ipc.Tests` | 120 | 8 s | |
| `Forge.App.LiveSwitch.Tests` | 8 | 22 s | 真实 WebView2 冒烟 |

`Forge.App.Tests` 耗时集中在 Git(363s)/Workbench(265s)/Changes(90s)/Lifecycle(65s);Settings/Localization/Terminal/Providers 按落点跑只需秒级。

## 已确认的结构性问题

1. `Forge.App.Tests` 全程序集串行(`TestAssemblySettings.cs` `DisableTestParallelization=true`),因 headless Avalonia 装进程级 Application/Dispatcher;真正并发约束只来自 UI 会话(已用 `[Collection("forge-app-ui")]` 分组),其余纯 ViewModel/存储测试跟着串行。
2. 进程级环境变量当业务状态:`TempDataRoot.Redirect` 改 `AppPaths` 三个进程级环境变量,放开并行前须先消除该共享面。
3. 测试代码重复集中在 arrange 样板,不是重复用例(704 文件带重复,但规范化后真正完全重复仅 2 对)。
4. 子进程孤儿:被中断的 `dotnet test` 留 `testhost`/`ExtensionHost` 锁住构建输出(过渡措施:`Directory.Build.props` 挂 `build-hygiene.ps1`)。
5. 复用旧 bin 让源码扫描门槛假红(先重建再跑)。

## 夹具登记表(优先引用,无私有同名副本)

| 项目 | 登记文件 | 内容 |
|---|---|---|
| `Forge.App.Tests` | `SessionStoreDoubles.cs` | `MemorySessionStore`、`SessionCatalogDouble`、`ThrowingSessionCatalog`/`ThrowingSessionStore` |
| `Forge.App.Tests` | `PrPlatformDoubles.cs` | `ThrowingPrPlatform`(非 DIM 成员全抛;`Merge/MarkReady` 是 DIM 刻意不声明) |
| `Forge.Core.Tests/Agents` | `TestSupport.cs :: SubAgentTestSupport` | `Scout` 定义、`Options()`、`Spawn()`、`ToolRounds`、`AgentId`、`ParentSpawnsScout()` |
| `Forge.Integration.Tests/Terminal` | `TerminalHosts.cs`/`TerminalAsserts.cs` | `TerminalWait.WaitForPidFileAsync`、`TerminalSleeper.SpawnCommand`、`TerminalFlood`、`NoCleanupFailures` |
| `Forge.Editor.Tests/Lsp` | `WorkspaceDiagnosticsTests.cs :: WorkspaceFixture` | 诊断聚合器+目录+池一体化夹具与 `OpenSessionAsync` |

C# 默认接口方法(DIM)陷阱:替身基类下再声明 DIM 同名公共方法不会重实现接口,必须在类上重新列出接口(`RemovalFlowTests`/`TreeMenuRowTests`/`GitSyncShipFinaleTests`/`ChecksPanelViewModelTests` 有先例)。`Forge.App.LiveSwitch.Tests` 替身拷贝是跨程序集结构性重复,动它需 csproj 层面动作,未收敛。

## 已知债务(未处理,按收益排序)

1. **放开 `Forge.App.Tests` 并行**:前置把数据根从进程级环境变量改成可注入实例(或把读写 `AppPaths` 的测试类收进同一串行 collection),逐命名空间验证,放开后连跑 3 轮全量确认无偶发。收益最大风险最大,单独任务。
2. **测试进程清理**:测试侧全局超时与退出清理仍待做(构建侧 `build-hygiene.ps1` 已落地)。
3. **5 家供应商认证测试参数化**:XAi/Meta/Kimi/ChatGpt 自重复 36–56%,4 个 `Scripted*Handler` 除记录类型逐行相同;抽 `ScriptedAuthHandler<T>` 基类再按供应商参数化。
4. **`throw new NotSupportedException()` 假实现样板**:183 处/23 文件,按接口抽 abstract 基类,保留失败语义,不直接删。
5. **HostClient/Workbench arrange 样板**:`FakeHost`+`ClientFactory.StartAsync`+`TestLimit.Create` 重复 69 处(全仓第一簇);阅读/构建成本,不改善回归保护,优先级低。

## 已实施的清理(2026-11-14)

- `PropertyChangeLog.cs` 共享记录器替代 18 文件 37 处重复 PropertyChanged 记录。
- 修好 `Fixed_panel_titles_follow_the_language_live`(补「问题」/Problems 页期望,非删用例)。
- 删空目录 `tests/Forge.Plugin.Sdk.Tests/`。

## 已实施的清理(2026-11-15 低价值测试清理)

6 个只读审计会话覆盖全部 13 个项目、8076 个 `[Fact]/[Theory]`;每条候选都回读完整方法体并在全仓反查等价覆盖,确认不了的一律保留。净减 49 个测试方法(App 29 / Core 15 / HostClient 3 / Workspace 1 / Scripting 1)。

命中的噪声类型(按频率):

1. **恒真的常量自证**:`Assert.Equal(<Default 常量>, new AppPreferences().X)`(字段初始化器就是同一常量)、`SubAgentLimits.MinParallel == AppPreferences.MinAgentsMaxParallelSubAgents`(const 别名)、`Assert.Equal(TimeSpan.FromSeconds(5), SystemNotifier.Timeout)`(数值已被同样的 5 秒日志断言钉住)。
2. **零断言**:`NotifyAsync_keeps_its_resultless_signature...`(只 await 一次)、`HeadlessSmokeTests`(测 Avalonia headless 框架)、`TreeVisualTests` 里的 `FORGE_TREE_SHOTS` 截图工具(不设环境变量就 return,设了也只存 PNG)。
3. **严格子集的重复**:`SystemNotifierTests` 的 NotifyAsync 版 trace 断言 ⊂ TryNotifyAsync 版(后者多断言返回值)、`WebToolsTests` 的 searxng 注册 ⊂ 四行 InlineData 版、`ProjectCreationServiceTests` 的 `.git` 后缀 8 行 ⊂ 既有 10 行用例。
4. **框架/私有实现细节**:`Assert.Same(tool.ToggleCommand, tool.ToggleCommand)`(属性缓存)、`InstrumentLayoutTests` 里断言与其名字无关的 `For(0).ShowGit`、`ThinkingLevels.For` 的 `Assert.NotNull`(每个出口都返回非 null 常量)。
5. **旧基线整文件**:两个自述 "Smoke checks" 的 `Agents/TodoToolStepCommitTests.cs`、`Storage/SqliteTodoStoreStepCommitTests.cs`(14 个用例)已被 `*EdgeTests` 与 `StorageSchemaV2MigrationTests` 逐条严格覆盖;仅把 `git_log`/`Conventional Commits` 两条描述断言移入 Edge 的 schema 用例。

合并(断言语义不减,只把同一逻辑的多组输入改成 `[Theory]`):SessionRuntimePolicy 工具→审批模式表 4→1、SessionWatch round-trip 2→1、StreamingMarkdown 任意分块 2→1、ToolApprovalCardMcp 命令行格式化 2→1、AgentRuntimeManager refresh 无害 2→1、PrChecksPoller 确认次数 2→1、HostClient compile budget 4→1。

刻意保留:

- `Terminal/` 全部单断言用例:每条是一个不同的 xterm 转义序列/字节契约。
- `KeyRegistryTests`/`StringTableTests`/`NoHardcodedChineseTests` 源码扫描门槛。
- 权限/审批/并发/持久化/协议边界的单断言用例(如 `SessionRuntimeGitToolsTests` 写类工具必须落到 `Ask`)。
- `WorkbenchControlsTests` 的静态样式与几何断言:每条带设计理由(字体权重在 CJK 字体链上的可用性、标题栏高度与 token 的关系),合并只能省几十毫秒。
- `PanelScrollExtentTests` 的 72 个 headless 用例:矩阵表达同一不变量在不同面板组合下的状态,压缩降低失败定位精度。
- `Providers/` 五家供应商认证测试:逐行相同的段落确实存在,但合并属夹具重构(抽 `Scripted*Handler` 基类),不是删用例,见「已知债务 3」。

观察项:2026-11-15 App.Tests L2 首跑 `MainWindowSmokeTests.Finished_subagents_menu_is_at_most_28_pixels_high_with_a_finished_sub_agent` 在 `ShellFixture.DisposeAsync` 的 `composition.DisposeAsync()` 处报 Avalonia `The calling thread cannot access this object`(非断言失败),隔离复跑 3/3 绿,判为 headless 会话重建的时序偶发;与本次删除的用例无因果关系(App.Tests 串行执行,失败在 fixture 释放阶段)。

## 观察项（2026-11-15 L3 收尾轮，非本次回归，待跟踪）

收尾轮 L3（收据 `forge-test-20261115-144124.json`，11994/0/0）首跑 3 处失败均在本次未触及代码，隔离复跑全绿，判非本次回归。两处不在既有 flake 清单，记为观察项：

1. **App.LiveSwitch.Tests 一次性挂死**：`Title_bar_settings_button_tip_and_name_follow_the_language_live` testhost 无活动 10min 被 blame-hang 终止（纯挂起、无 exception、dump 无 WebView2 模块）。重建后连续 5 次 8/8 绿，不可复现。根因未定位（机器无 cdb/windbg）；dump 留 `artifacts/testresults/Forge.App.LiveSwitch.Tests/`。后续 L3 观察。
2. **ClaudeSubscriptionCredentialsTests.Concurrent_reads_of_one_file_share_a_single_refresh 偶发 400**：签名是「二次刷新 HTTP 400」（非清单的 HTTP 超时）。机制：`Persist()` 写回 catch IOException 等仅 Trace，`File.Move` 瞬时占用失败时文件留已消费 refresh token，门内二次读判 NeedsRefresh 再刷→400。隔离 12 次全绿。I/O 时序偶发，根治需 Persist 失败保留内存/重试。后续观察。

既有 flake（隔离复跑确认）：`FrameCoalescingTests.Burst`（flushCount 阈值调度抖动）、`MenuShellTests` project_plus（临时沙箱 git index.lock）。

判读注意：`forge-test.ps1` 自本轮改造后只在“疑似旧 bin”时重建复跑（`-SkipBuild`、含源码扫描门槛、改动文件在引用子树内且比产物新），其余失败直接判红，收据 `retryWhy` 记明重建理由。