# Deslop 重复代码审计报告

> 审计日期：2026-11-14。本报告为**只读审计**：未修改任何生产代码，未调整任何检测阈值或排除规则。
> 所有引用的文件位置与行号均已对照扫描时的当前工作树重新核对（扫描与核对之间无代码改动）。

## A. 总体情况

### 扫描范围与工具

| 项 | 值 |
| --- | --- |
| 仓库根目录 | `C:/Users/zfcfl/.thinkrail/worktrees/forge/workspace-19`（forge 主仓 worktree） |
| 工具 | Deslop CLI 0.42.0（VS Code 扩展 `nimblesite.deslop-live-0.42.0` 自带 `deslop.exe`） |
| 扫描窗口 | 2026-11-14T11:07:10Z → 11:08:57Z（约 107 秒，日志 `.deslop/logs/deslop-1794654430.log`） |
| 报告 | `.deslop/deslop-report.json`（11.0 MB，本报告唯一数据源） |
| 增量缓存 | 保留默认（0 hits / 1786 misses，首次全量扫描；`.deslop/cache` 未删除） |
| 检测参数 | `min_nodes=30`，embeddings 关闭，未加任何排除规则 |
| 文件 | 1786 个 C# 文件，489,996 行可分析代码（跳过 0 个） |
| `literal_findings` | 0 条（本版本/配置下字面量分析未产出结果，无重复字面量可报告） |

### 总体重复率

- **`metrics.duplication_percent` = 12.74%**（62,408 / 489,996 行）。该口径只计入 `identical` 与 `nearly_identical`；`loosely_similar` 另有 44,086 行（8.99%），不计入总数。
- 簇数量：报告可见 9,025 个（另有 1,801 个被内置噪声过滤/报告策略隐藏），其中 `nearly_identical` 5,344、`loosely_similar` 2,540、`identical` 1,141、`structural_only` 472。
- 注意：`per_kind` 各自的 `duplicated_loc`（identical 11,605 行 = 2.37%，nearly_identical 53,757 行 = 10.97%）相加（13.34%）略高于总数 12.74%——同一代码区域可被多个判定类型同时覆盖，**分类型行数存在重叠，不能直接相加**。
- 簇的位置分布：**测试 6,587 个（73%）、生产代码 2,486 个（27.5%）、混合 424 个**。重复主要压在测试侧。

### 各类型重复统计（按簇数）

| 类型 | 簇数 | 说明 |
| --- | --- | --- |
| `nearly_identical` | 5,344 | 最大头。集中在 Localization 访问器模式、ViewModel 属性通知级联、Providers OAuth 流、Review 控制器对 |
| `loosely_similar` | 2,540 | 仅作调查线索（不计入总重复率） |
| `identical` | 1,141 | 几乎全部在测试（最大 mass 884，测试替身 `ISessionStore` 空实现族） |
| `structural_only` | 472 | 形状相似，不作重复处理 |

### 主要问题目录（按重复行数）

| 目录 | 重复行 | 重复率 | 定性 |
| --- | --- | --- | --- |
| `tests/`（合计） | 41,402 | 19.28% | 测试夹具/替身复制为主 |
| `src/`（合计） | 20,845 | 7.66% | 生产代码，真正的治理对象 |
| `tests/Forge.App.Tests` | 19,304 | 19.52% | 最大单一测试项目 |
| `src/Forge.App` | 12,029 | 10.29% | Localization + ViewModels + Providers + Review |
| `tests/Forge.Core.Tests` | 6,561 | 17.99% | SubAgent 测试夹具 |
| `tests/Forge.Workspace.Tests` | 4,878 | 21.34% | Git 测试支持代码 |
| `src/Forge.App/Localization` | 3,107 | 30.51% | 47 个 `Strings.*.cs` 表，**规范约定的模式重复**（见 C 节） |
| `tests/Forge.HostClient.Tests` | 2,341 | 25.43% | 测试前置三件套（见 B-3） |

重复率最高的单文件（生产代码）：`PanelChrome.cs` 39.9%、`DiffReviewController.cs` 33.0%、`GitCli.cs` 21.8%、`ExtensionHost.cs` 17.6%、`LspClient.cs` 18.8%；（测试）：`XAiAuthenticationTests.cs` 54.7%、`MetaAuthenticationTests.cs` 52.6%、`KimiAuthenticationTests.cs` 45.1%、`StepCommitterTests.cs` 40.9%、`ChatGptAuthenticationTests.cs` 38.3%。

## B. 高优先级问题

以下每个问题均已阅读全部代表 occurrence 的实际源码（不依赖报告摘要），差异与业务语义见各条「分析」。

### B-1 OAuth 设备码流程在四个提供商里各写一遍（生产代码，P0）

- **Cluster**：`9f7e269d`（nearly_identical，mass 744，`KimiAuthentication.PollAsync` ↔ `XAiAuthentication.PollAsync`）、`90c8893e`（loosely_similar，mass 776，+ `MetaAuthentication`）、`9a10e43b`（loosely_similar，mass 736）、以及测试侧 `50a7bf48`（identical，mass 315，8 处跨 4 个认证测试文件）。
- **位置**：
  - `src/Forge.App/Providers/KimiAuthentication.cs:152-288`（338 行/文件）
  - `src/Forge.App/Providers/XAiAuthentication.cs:159-297`（353 行/文件）
  - `src/Forge.App/Providers/MetaAuthentication.cs:160-231`（338 行/文件）
  - `src/Forge.App/Providers/ChatGptAuthentication.cs`（534 行，`StartAsync:152`/`PollAsync:196`/`RefreshAsync:328` 同套结构）
  - 测试：`tests/Forge.App.Tests/Providers/{XAi,Kimi,Meta,ChatGpt}AuthenticationTests.cs`（重复率 54.7%/45.1%/52.6%/38.3%）
- **分析**：四个类各自实现 RFC 8628 设备码授权：`StartAsync`（发 device authorization 请求）→ `PollAsync`（轮询：`authorization_pending`/`slow_down`/`expired_token` 分支、`interval` 处理、5xx 抛 `ProviderException(retryable:true)`）→ `RefreshAsync`。已核对的差异只有：表单字段顺序、端点路径常量、错误消息里的提供商名、完成语义（Kimi 直接 `Bundle(element,"poll")`；XAi `Bundle(element,"device", previousRefreshToken:null)`；**Meta 拿到的是身份令牌而非 API 凭据**，还要再铸 key）。HTTP 传输层已共享（`ProviderSignInHttp` 管超时/错误映射/单次读体），**重复的是流程编排本身**。
- **建议公共位置**：`src/Forge.App/Providers` 内新增一个泛型设备码流程类（如 `DeviceCodeFlow<TCode, TResult>`），参数化：端点路径、提供商显示名、`Bundle`/完成委托。四个类缩为「配置 + 差异点」。
- **收益**：约 600–900 行生产代码收敛为一份；新增带 OAuth 设备码的提供商时只写配置；四处同步修 bug（如 `slow_down` 间隔逻辑）变一处。
- **风险**：中。Meta 的完成语义不同（身份令牌→铸 key），ChatGpt 534 行可能带额外 scope/参数，需以委托显式建模差异而不是强行统一。**不确定性**：ChatGpt 是否完全套用同一骨架未逐行核对（只确认方法结构一致），合并前需单独 `compare-pair` 核对。

### B-2 `DiffReviewController` ↔ `FileReviewController` 评审生命周期重复（生产代码，P0/P1）

- **Cluster**：`ac90045a`（nearly_identical，mass 267，`MarkAsync`+`RetryUnmarkedAsync` 逐行一致）、`9d0574cb`（loosely_similar，mass 598，`PersistAsync`）、`e17023bd`(344)、`ff1a18da`(222)、`ece2d1a4`(187)、`2cbbde8c`(176)、`a4dcf3fe`(200) 等共 10+ 个簇，合计 mass ≈ 2,300。
- **位置**：`src/Forge.App/Review/DiffReviewController.cs`（33.0% 重复）与 `src/Forge.App/Review/FileReviewController.cs`。核对样例：`DiffReviewController.cs:349-394` 与 `FileReviewController.cs:295-336` 的 `RetryUnmarkedAsync` 除 `FileReviewController.` 前缀与一个 `infos.Remove(done)` 外完全一致。
- **分析**：SPEC 明确两者是同一 `IReviewSurface` 的两个实现（文件预览 vs diff 视图，`src/Forge.App/SPEC.md` §diff 中的行评审）。`DiffReviewController` 已经在调 `FileReviewController` 的静态方法（`IsBlocked`/`RestorePendingOf`/`UnmarkedOf`），说明共享是被接受的方向，但**草稿生命周期簿记（发送→标记→重试→释放→集合维护）仍是两份**。真正的语义差异在锚点：文件侧用 `ReviewTextAnchors.Create`（路径+行），diff 侧用 `DiffAnchor.Create`（diff 行号），以及 `infos` 字典的额外维护。
- **建议公共位置**：`src/Forge.App/Review` 内提取共享的草稿生命周期引擎（标记/重试未标记/恢复/释放的集合操作与消息设置），锚点计算与 `PersistAsync` 的落库细节留在各控制器。
- **收益**：消掉约 400–600 行；评审「已发送未标记→重试」这类有状态逻辑只维护一份，行为漂移风险（现在两边已经是微妙地不一致：diff 侧多一个 `infos.Remove`）消失。
- **风险**：中。两控制器状态字段不同（`infos` vs 无），提取时接口设计要避免反向耦合进锚点语义。重构属于行为敏感区（评审标记/恢复有 SPEC 约束的串行化边界），需配齐 `Forge.App.Tests` 的 Review 相关测试。

### B-3 `Forge.HostClient.Tests` 测试前置三件套 69 次重复（测试，P1）

- **Cluster**：`e49c7b62`（nearly_identical，mass 2108，**全报告 rank #1**，69 处 × 31 规范节点，15 个文件）。
- **位置**（每文件出现次数）：
  - `tests/Forge.HostClient.Tests/Terminal/TerminalStartProtocolTests.cs`（12×）、`ExtensionAuthoringPayloadTests.cs`（11×）、`Terminal/TerminalSessionLifecycleTests.cs`（9×）、`RunCodeProxyTests.cs`（8×）、`Terminal/TerminalOutputTests.cs`（6×）、`Terminal/TerminalConnectionTests.cs`（4×）、`Terminal/TerminalInputTests.cs`（4×）、`ExtensionHostClientPluginCommandTests.cs`（5×）、`HostClientRootContextTests.cs`（2×）、`HostToolCallRunBindingTests.cs`（2×）、`PromptDisconnectTests.cs`（2×）、`ExtensionHostCommandTests.cs` / `HostToolCallConcurrencyTests.cs` / `HostToolCallRelayTests.cs` / `PluginStatusPromptTests.cs`（各 1×）
- **代码**（`ExtensionAuthoringPayloadTests.cs:152-154` 等处）：
  ```csharp
  await using var host = new FakeHost();
  await using var client = await ClientFactory.StartAsync(host);
  using var limit = TestLimit.Create();
  ```
- **分析**：每个用例开头的宿主/客户端/限制器搭建样板。这不是测试数据，是**夹具结构**。
- **建议公共位置**：`tests/Forge.HostClient.Tests` 内加一个 `HostClientTestContext`（`IAsyncDisposable`，封装三者并暴露 `host`/`client`），或按 xUnit 惯例提基夹具类。**注意**：各用例在三件套之后紧跟自己的初始化（写扩展、开终端……），封装时保持只收编这三行，别吞用例特有步骤。
- **收益**：69 处 × 3 行样板收敛；新用例不会再抄错顺序（`await using` 嵌套次序错了就是资源泄漏）。
- **风险**：低。纯测试重构；需跑 `Forge.HostClient.Tests` 全量验证。

### B-4 `ISessionStore`/`ISessionCatalog` 空实现测试替身家族（测试，P1）

- **Cluster**：`f0a46a76`（**identical**，mass 884，14 处/10 文件）、`16e5d655`（identical，581）、`45064978`（identical，486）、`37858ff9`（identical，333）、`630eb71e`（nearly_identical，mass 1218，609 节点，`ComposerQueueTests` ↔ `SessionAutomationTurnTests` ↔ `SessionTurnFailureTests` 三个 79 行的私有 `FakeSessionStore`/`MemorySessionStore`）。
- **位置**：跨**三个测试项目**——`tests/Forge.App.Tests/EmptySessionDoubles.cs`（已是公共文件）被逐份复制到 `LiveSwitchTests.cs`（Forge.App.LiveSwitch.Tests）、`Git/ProjectTree*Tests.cs`、`ViewModels/ComposerQueueTests.cs`、`ViewModels/SessionAutomationTurnTests.cs`、`ViewModels/FinishedSubAgentReplayTests.cs`、`Git/WorktreeOrphanCleanupTests.cs` 等约 15 个文件（各自声明私有嵌套类）。
- **分析**：`SetStatusAsync → true`、`RenameAsync → true`、`TouchAsync → CompletedTask` 这类空实现按接口逐方法复制。`EmptySessionDoubles.cs` 已存在，说明公共替身是被接受的形态；剩下的是历史复制未回收，以及**跨测试项目**（LiveSwitch.Tests 不能直接引用 Forge.App.Tests 的内部类型）造成的结构性复制。
- **建议公共位置**：短期——把 `EmptySessionDoubles` 提升为 `public` 并让同仓测试项目引用（若程序集可见性不允许，用 `InternalsVisibleTo` 或 link 同一源文件进多个测试项目）；跨项目**不建议**新建共享测试工具项目（动 `*.csproj` 须先按仓库规则征得同意）。带记忆语义的 `MemorySessionStore`（630eb71e）三个 79 行副本可先合并为 Forge.App.Tests 内一份。
- **收益**：约 1,500+ 行测试代码收敛；`ISessionStore` 接口演进时不用改 15 个文件。
- **风险**：低到中。**不确定性**：各文件私有替身可能有细粒度差异（有的记日志、有的抛 NotSupported），合并前必须逐个读（本次已核对 630eb71e 三份确实逐行同构；其余 identical 簇同名方法体完全一致，风险低）。
- **反向注意**：测试替身刻意隔离也有价值（防止测试间通过共享状态串扰）——合并的是「空实现」族，**不是**有状态的替身。

### B-5 `IPrPlatform`/PR 检查的 `NotSupportedException` 桩家族（测试，P1）

- **Cluster**：`8a24c252`（identical，mass 660）、`c328aa62`（identical，567）、`898d563f`（560）、`ee771a34`（576）、`ff66d582`（410）、`b1a9a435`（364）。
- **位置**：`tests/Forge.App.Tests/Automations/CiChainFakes.cs:184-194`、`Git/ChecksPanelViewModelTests.cs:1013-1023`、`Git/GitSyncShipFinaleTests.cs`、`Git/GitSyncViewModelTests.cs`、`Git/GitTestSupport.cs`，以及 `tests/Forge.Workspace.Tests/Git/PrCommentToolsSmokeTests.cs`、`PrToolsSmokeTests.cs`、`PrCheckPollerSmokeTests.cs`。
- **分析**：实现 `IPrPlatform`/PR 检查相关接口时，「用不到的方法 → `throw new NotSupportedException()`」按接口逐方法复制。同 B-4 的结构性成因，外加跨 `Forge.App.Tests` / `Forge.Workspace.Tests` 两项目。
- **建议公共位置**：各测试项目内提一个 `ThrowingPrPlatformBase`（虚方法全抛 NotSupported，用例只覆写关心的方法）。跨项目复用同 B-4 的口径。
- **收益**：接口加方法时不再需要同步改 7+ 个假件；删掉约 1,000 行。
- **风险**：低。测试专用；冒烟测试（`*SmokeTests`）本身是 L3 才跑的，改完要在 L3 窗口验证一次。

### B-6 `StepCommitterTests` ↔ `StepCommitterConcurrencyTests`（测试，P1）

- **Cluster**：`6d664103`（nearly_identical，mass 957，30 处）、`7cc49426`（nearly_identical，943，24 处）。
- **位置**：`tests/Forge.App.Tests/Git/StepCommitterTests.cs`（单文件重复率 40.9%，363 重复行）与 `StepCommitterConcurrencyTests.cs`。
- **分析**：两个文件共享 git 仓库搭建、提交断言等大量夹具；并发版是在串行版用例上加并发的衍生。合并方向是共享 `GitTestSupport` 式的夹具基类，而非合并测试文件（并发语义的用例应保持独立文件）。
- **收益/风险**：约 900 mass 收敛；风险低，但两文件的用例命名/意图需保留，只抽夹具。

### B-7 认证测试四联拷（测试，跟随 B-1）

- **Cluster**：`50a7bf48`（identical，mass 315，8 处）+ 各文件 38–55% 的整体重复率。
- **位置**：`tests/Forge.App.Tests/Providers/{XAi,Kimi,Meta,ChatGpt}AuthenticationTests.cs`。
- **分析**：测试结构完全镜像生产代码的重复（假 Http 响应、设备码happy path、slow_down、过期）。**B-1 重构后这些测试自然各测差异点**；若先动测试等于把重复固化。顺序上应随 B-1 一起做，不单独立项。

## C. 低价值或合理重复（不建议合并）

1. **`Localization/Strings.*.cs` 访问器模式（47 个文件，目录重复率 30.5%，13 个簇合计 mass ≈ 15,000）**。`public static string X => Loc.Instance[XKey];` 是 SPEC 明文约定的分表规范（`src/Forge.App/SPEC.md` §字符串与自动化：按界面区域分表、中英双语、切换即时生效）。源码扫描门槛（`StringTableTests`/`NoHardcodedChineseTests`/`KeyRegistryTests`）依赖这套形态存在。每个键都是独立数据点，不存在可抽取的「公共实现」。**Ignore**。若未来重复率成为负担，出路是源生成器（属 spec 变更，须先走架构决策），不是手工合并。
2. **Avalonia `StyledProperty` 注册样板（`PanelChrome.cs` 39.9%、`PanelTabStrip.cs`、`TabHeaderView.cs`，簇 3b1158c3 等，合计 mass ≈ 2,500）**。`AvaloniaProperty.Register<T,..."` 每属性两行是框架强制形态，无参数化空间（属性名必须逐个 `nameof`）。**Ignore**。
3. **`Terminal/Vt/CellWidth.cs` 的 Unicode 零宽区表（簇 66be7ffc，mass 1120，2 处）**。`ZeroWidthStarts`/`ZeroWidthEnds` 两张数据表结构相同、数值不同，是 Unicode 属性数据，不是逻辑。**Ignore**。
4. **ViewModel `OnPropertyChanged(nameof(...))` 级联（簇 0b96fceb/9ac4d6be/eb8b8c3e/0918f44a 等，合计 mass ≈ 5,000，20+ 个 VM）**。计算属性失效通知是 MVVM 惯用法；每处通知的属性集合就是各自的业务语义。可以引入 `RaiseComputed(params string[])` 之类的助手把 5 行压成 1 行，但这只是行数游戏，不消除语义重复，且会模糊「谁依赖谁」的阅读线索。**P2/Ignore**（见 D）。
5. **`structural_only` 全部 472 个簇**：形状相似而已，按约定不作重复处理。
6. **`loosely_similar` 中已核对为业务不同的**：`GitCli` ↔ `GhCli`（簇 bdad77c2，mass 396）——两个 CLI 包装的参数/输出语义不同，且进程启动已有 `Forge.IO.ProcessRunner` 承接共性；`LspClient`/`ModelCatalogJson`/`McpContentFlattener` 的 JSON 遍历（簇 7cf64dc9/f911a4d7）——System.Text.Json 防御式读取的惯用形态。**均不建议合并**。

## D. 重构建议（按收益/风险排序）

| 级别 | 事项 | 对应问题 | 预估规模 | 前置条件 |
| --- | --- | --- | --- | --- |
| **P0** | 提取通用 OAuth 设备码流程（Kimi/XAi/Meta/ChatGpt），差异用委托/配置建模；测试随动收敛 | B-1 + B-7 | 生产 ≈600–900 行 + 测试 ≈300 行 | 先 `compare-pair` 核对 ChatGpt；Meta 完成语义单独建模；跑 `Forge.App.Tests` Providers 全量 |
| **P0** | `Review` 草稿生命周期引擎共享（Mark/RetryUnmarked/Restore/Release），锚点留在各 `IReviewSurface` | B-2 | ≈400–600 行 | Review 行为敏感区，配齐现有 Review 测试再动；SPEC 的标记/恢复串行化边界不得变 |
| **P1** | `HostClient.Tests` 三件套夹具基类（rank #1 的 69 处样板） | B-3 | ≈210 行样板 + 维护面 | 跑 `Forge.HostClient.Tests` 全量 |
| **P1** | `ISessionStore` 空实现族收敛到 `EmptySessionDoubles`（跨项目用 link/公开可见性，不新建项目） | B-4 | ≈1,500 行 | 逐份核对私有替身差异；注意别并有状态替身 |
| **P1** | `IPrPlatform` 抛桩基类 `ThrowingPrPlatformBase` | B-5 | ≈1,000 行 | L3 窗口跑一次冒烟（`*SmokeTests` 仅 L3 执行） |
| **P1** | `StepCommitter` 两测试文件抽共享夹具基类 | B-6 | ≈900 mass | 保持用例独立，只抽夹具 |
| **P2** | ViewModel 计算属性失效通知助手（`RaiseComputed`） | C-4 | 行数压缩，语义不变 | 收益有限；需团队统一习惯，否则新旧两式并存更乱 |
| **P2** | `Forge.Core.Tests/Agents` SubAgent pin 测试夹具（簇 c610b231/bbbb1c3b）、`McpToolAdapterImageTests` 与 `WorkspaceDiagnosticsTests` 文件内自重复（9a262439/676ff7fb）、Terminal 集成测试样板（c115e9e8） | — | 各数百 mass | 逐个核对后再决定；同文件内自重复优先在原文件内提取局部 helper |
| **P2** | `630eb71e`（三个 79 行 `MemorySessionStore`）单独先合并 | B-4 子项 | 237 行 | 已核对三份逐行同构，风险最低，可作为 B-4 的第一步 |
| **Ignore** | Localization Strings 表、StyledProperty 注册、CellWidth 数据表、`structural_only`、已核对的 `loosely_similar` | C 节 | — | — |

执行顺序建议：B-4 的 `630eb71e` 子项（零风险热身）→ B-3 → B-1（+B-7）→ B-2 → 其余 P1。每步独立成任务，各自跑改动落点对应的测试命名空间；**不要把 P0 两项拆给并行 Worker 同时做**（都涉及 `Forge.App` 行为敏感区，且 B-1/B-2 会各自触碰 `Forge.App.Tests` 不同目录，串行更稳）。

## E. 后续治理建议

1. **把 `find-similar` 前置检查固化到派活文本**。`AGENTS.md` 已有 deslop 定律；本次审计显示最大的簇（rank #1，mass 2108）正是「每个新测试文件都重抄一遍三行夹具」积累出来的——预防成本远低于回收成本。派活文本里写明「写新测试文件前先查 `tests/<project>` 是否已有同名夹具/替身」。
2. **测试项目级「夹具登记」约定**：每个测试项目维护一个显式的公共 doubles/夹具文件（如 `EmptySessionDoubles.cs`），新测试文件**只允许引用**其中已有的替身或往里加，不允许再声明私有同名空实现。这条不改 spec，可作为 testing.md 的操作口径补充。
3. **新增 OAuth 类提供商时**：先读 `ProviderSignInHttp` 与 B-1 的公共流程（若已落地），只写差异点。新增「接口大、用得少」的假件时先找 `ThrowingPrPlatformBase` 这类基类。
4. **重复率指标的使用边界**：本仓 12.74% 中约三分之二是测试夹具与规范模式（Localization/StyledProperty），**不要**把全仓重复率当作质量门禁数字或 KPI 去压；应按目录看趋势（`src/` 7.66% 是真实水位，目标是不升）。
5. **定期复扫**：增量缓存保留时全仓复扫约 2 分钟，成本可接受。建议在每个大模块（波次）合并后复扫一次 `src/`，对比 `folders` 里 `src/Forge.App` 的 `duplication_percent` 是否上升；上升即回到本次报告的簇 ID 定位新增长点（编辑后旧字节偏移失效，需重扫再用）。
6. **不做的治理**：不加 `exclude`、不调阈值、不为指标弱化断言（AGENTS.md 硬规则）；`structural_only`/`loosely_similar` 永远只作线索。

---

### 最值得优先处理的 10 个问题（速查）

| # | 级别 | 问题 | Cluster | 位置 |
| --- | --- | --- | --- | --- |
| 1 | P0 | OAuth 设备码流程四提供商重写 | 9f7e269d / 90c8893e / 9a10e43b | `src/Forge.App/Providers/{Kimi,XAi,Meta,ChatGpt}Authentication.cs` |
| 2 | P0 | Review 双控制器草稿生命周期重复 | ac90045a / 9d0574cb（+10 簇） | `src/Forge.App/Review/{DiffReview,FileReview}Controller.cs` |
| 3 | P1 | HostClient 测试三件套 69 处样板（全报告 rank #1） | e49c7b62 | `tests/Forge.HostClient.Tests`（15 文件） |
| 4 | P1 | `ISessionStore` 空实现替身跨 3 项目复制 | f0a46a76 / 630eb71e 等 | `EmptySessionDoubles.cs` + 约 15 个复制文件 |
| 5 | P1 | `IPrPlatform` NotSupported 桩 7+ 文件 | 8a24c252 / c328aa62 / 898d563f / ee771a34 / ff66d582 | `Forge.App.Tests` + `Forge.Workspace.Tests` |
| 6 | P1 | `StepCommitter` 两测试文件夹具重叠 | 6d664103 / 7cc49426 | `tests/Forge.App.Tests/Git/StepCommitter*.cs` |
| 7 | P1 | 认证测试四联拷（随 #1 一起做） | 50a7bf48 | `tests/Forge.App.Tests/Providers/*AuthenticationTests.cs` |
| 8 | P2 | 三个 79 行 `MemorySessionStore` 逐行同构（#4 的零风险第一步） | 630eb71e | `ComposerQueueTests` / `SessionAutomationTurnTests` / `SessionTurnFailureTests` |
| 9 | P2 | 单文件内自重复：McpToolAdapterImageTests、WorkspaceDiagnosticsTests | 9a262439 / 676ff7fb | 对应测试文件内部 |
| 10 | P2 | SubAgent pin 测试与 Terminal 集成测试夹具 | c610b231 / bbbb1c3b / c115e9e8 | `Forge.Core.Tests/Agents`、`Forge.Integration.Tests/Terminal` |

（Ignore 级：Localization Strings 表、Avalonia StyledProperty 样板、CellWidth Unicode 数据表、`structural_only` 全部——详见 C 节。）
