---
id: forge-testing
type: architecture-design
status: active
title: 测试策略与分层(L1/L2/L3)
parent: forge-architecture
depends-on: [forge-architecture]
tags: [testing, ci, agent-cost]
---

# 测试策略与分层

测试是回归防线,不是交付指标。本文约束三件事:**每次任务该跑哪些测试**、**新增测试的准入**、**何时可以删测试**。目标是让每个 Agent 任务只承担它真正需要的测试成本,同时不降低对真实回归的保护。

操作口径写在 `AGENTS.md`「测试分层与执行」一节;本文是判定依据。

## 现状基线(2026-11-14 实测)

13 个测试项目。用例数取 `dotnet test` 的统计(与 `[Fact]`/`[InlineData]` 静态计数略有差异,以运行器为准);耗时是同一次运行的实际墙钟,机器 32 核、构建产物已预热、期间另有其他工作区在跑测试(偏保守)。

| 测试项目 | 用例数 | 全量墙钟 | 用例耗时累加 | 备注 |
|---|---|---|---|---|
| `Forge.App.Tests` | 4,321 | 985 s(16 m 25 s) | 978 s | 累加 ≈ 墙钟,说明**完全没有并行** |
| `Forge.Core.Tests` | 2,190 | 50 s | 749 s | 累加/墙钟 = 15×,并行健康 |
| `Forge.Workspace.Tests` | 1,345 | 375 s | 2,762 s | 累加/墙钟 = 7.4×;真实 git 进程 |
| `Forge.Editor.Tests` | 513 | 5 s | 10 s | |
| `Forge.HostClient.Tests` | 503 | 10 s | 28 s | 命名管道 + `FakeHost` |
| `Forge.IO.Tests` | 451 | 19 s | 53 s | 真实文件系统与子进程 |
| `Forge.Mcp.Tests` | 307 | 18 s | 28 s | |
| `Forge.Integration.Tests` | 265 | 124 s | — | 跨进程 E2E,启动真实 Host/Worker |
| `Forge.Scripting.Tests` | 264 | 11 s | — | 一次性执行器进程 |
| `Forge.Approval.Tests` | 188 | 10 s | — | |
| `Forge.Workflows.Tests` | 148 | 5 s | — | |
| `Forge.Ipc.Tests` | 120 | 8 s | — | |
| `Forge.App.LiveSwitch.Tests` | 8 | 22 s | — | 真实 WebView2 冒烟 |

构建:`dotnet build Forge.slnx -c Debug` 89 s。

`Forge.Workspace.Tests` 里 191 个用例单个耗时 ≥5 s,最慢的是 `PrCreateSyncSmokeTests`、`Screenshot*`、`GitCliLifecycleTests`、`WorktreeLifecycleTests`(单条 15–34 s,每条都起若干真实 git 进程);命名空间累加为 `Git` 2,099 s、`Worktrees` 379 s、`Diff` 100 s。

`Forge.App.Tests`(累加 978 s)几乎全部集中在四个区域,其余区域都在几十秒以内:

| 区域 | 用例数 | 累加耗时 |
|---|---|---|
| `Git` | 396 | 363 s |
| `Workbench` | 582 | 265 s |
| `Changes` | 141 | 90 s |
| `Lifecycle` | 327 | 65 s |
| `ViewModels` | 401 | 36 s |
| `Editor` | 145 | 24 s |
| `Providers` | 272 | 4.8 s |
| `Terminal` | 328 | 2.2 s |
| `Localization` | 110 | 1.7 s |
| `Settings` | 192 | 0.7 s |

即:改 `Settings`/`Localization`/`Terminal`/`Providers` 时按落点跑只要秒级,改 `Git`/`Workbench` 才要几分钟。全量 16 分钟的成本主要由少数起真实 git 进程与 headless UI 会话的用例承担(`StepCommitterConcurrencyTests` 单条 31 s、`RemovalShellTests`/`GitSyncViewModelTests`/`ChangesReviewFlowTests` 单条 7–9 s)。

### 已确认的结构性问题

1. **`Forge.App.Tests` 全量串行。** `tests/Forge.App.Tests/TestAssemblySettings.cs` 关掉了整个程序集的并行(`DisableTestParallelization = true`),理由是 headless Avalonia 会安装进程级 `Application`/`Dispatcher`。真正的并发约束只来自 UI 会话,而这类测试已经用 `[Collection("forge-app-ui")]` 分组(119 个文件);其余 169 个测试文件是纯 ViewModel/存储/格式化测试,却跟着一起串行。
2. **进程级环境变量被当作业务状态。** `TempDataRoot.Redirect` 直接改 `AppPaths` 的三个进程级环境变量,22 个测试文件这么干。因此“关掉全程序集并行”同时也在掩盖跨测试类的全局状态竞争——放开并行前必须先消除这个共享面,否则会变成偶发失败。
3. **测试代码重复集中在 arrange 样板,不是重复用例。** Deslop 结果:704 个测试文件带重复,合计 34,641 行重复(占这些文件 180,324 行的 19.2%);5,802 个重复簇触及 `tests/`。但把 6,884 个测试方法体规范化后逐字节比对,真正完全重复的只有 2 对。**所以“删重复测试”能省下的行数远小于“合并样板”。**
4. **没有分层。** 只有一条口头约定(“只跑与本次改动相关的测试”),没有可执行的落点映射;全量跑一次的成本高到不敢跑,Agent 每次都要重新猜过滤范围。
5. **子进程与孤儿进程。** 被中断的 `dotnet test` 会留下 `testhost.exe` 与 `Forge.ExtensionHost.dll` 子进程,继续锁住 `src/Forge.ExtensionHost/bin/...`,让后续任意任务的构建直接以 `MSB3027` 失败。
6. **已经失效的测试没人修。** `Forge.App.LiveSwitch.Tests` 的 `Fixed_panel_titles_follow_the_language_live` 在 main 上是红的:产品给检视区加了“问题”页,测试仍按旧标题断言。
7. **复用旧 `bin` 会让源码扫描门槛假红。** `--no-build` 跑 `StringTableTests.Every_axaml_loc_binding_refers_to_a_registered_key` 时报了 6 个“已绑定但未注册”的 `resources.settings.*` 键,而 `Strings.Resources.cs` 里它们确实已注册;重建后全绿,说明是增量构建留下的旧 `Forge.App.dll`。这类失败先 `dotnet build`(必要时 `-t:Rebuild`)再跑,不要直接改代码。

## 分层

分层是**执行策略**,不是把测试改写成三层。归属由“改动落点 → 测试项目/命名空间”决定,不需要给用例逐个打标。

### L1:快速(每次任务默认)

范围 = 改动落点的直接测试 + 源码扫描硬门槛。几分钟内结束,失败就一定是本次改动引起的。执行一律经 `scripts/forge-test.ps1`:`-Auto` 按改动落点(`scripts/test-map.json`)推导过滤器、改了 `.cs`/`.axaml` 自动追加扫描门槛、失败先重建一次再判红(治旧 `bin` 假红);本节命令是手工等价操作。

```bash
# 先构建一次,之后所有运行都带 --no-build,避免重复构建
dotnet build Forge.slnx -c Debug

# 只跑改动落点(示例:Workbench)
dotnet test tests/Forge.App.Tests --no-build --filter "FullyQualifiedName~Forge.App.Tests.Workbench"
```

改了任何 `.cs`/`.axaml` 源文件时,追加源码扫描门槛(它们读源码,失败即真实问题):

```bash
dotnet test tests/Forge.App.Tests --no-build \
  --filter "FullyQualifiedName~Localization|FullyQualifiedName~MinorRegressionTests|FullyQualifiedName~KeyRegistry|FullyQualifiedName~StringTable|FullyQualifiedName~NoHardcodedChinese"
```

### L2:模块(模块功能完成、接口变化、较大重构后)

范围 = 承载改动的那个测试项目全量 + 直接依赖它的项目。

```bash
dotnet test tests/Forge.Core.Tests --no-build
```

### L3:完整(合并前、发布、跨模块改动)

范围 = 13 个测试项目全部。触发条件:

- 准备合并或发布。
- 跨模块架构改动(`architecture.md` 的分层边界被改动)。
- 公共接口或协议变化:`Forge.Abstractions`、`Forge.Plugin.Sdk`、`Forge.Ipc`、`Forge.Ipc.Protocol`。
- 核心基础设施变化(存储 schema、IPC 帧格式、进程归属、Native AOT 发布配置)。
- L1/L2 出现疑似跨模块回归。
- 用户明确要求全量验证。

```bash
# 每项目独立 results 目录;项目之间可以并行,同一项目的构建不要并行
for p in Forge.App.Tests Forge.Core.Tests Forge.Workspace.Tests; do
  dotnet test "tests/$p" --no-build --logger "trx;LogFileName=$p.trx" --results-directory "artifacts/testresults/$p"
done
```

`forge-test.ps1 -Level L3` 必须带 `--why`(merge/release/interface/infra/cross-module,对应上列触发条件),缺省拒绝执行;`-Auto` 命中 L3 落点时自动代填并记录;每次运行在 `artifacts/testruns/` 落收据,范围与改动落点不匹配的全量视为未完成测试义务。

## 改动落点 → 测试落点

| 改动位置 | L1 过滤器 |
|---|---|
| `src/Forge.App/Workbench/`、`Resources/` | `FullyQualifiedName~Forge.App.Tests.Workbench` |
| `src/Forge.App/Views/`(`*.axaml` 视图;对应测试在 `Views` 命名空间,另并 Workbench) | `FullyQualifiedName~Forge.App.Tests.Views|FullyQualifiedName~Forge.App.Tests.Workbench` |
| `src/Forge.App/Providers/` | `FullyQualifiedName~Forge.App.Tests.Providers` |
| `src/Forge.App/Git/` | `FullyQualifiedName~Forge.App.Tests.Git` |
| `src/Forge.App/Terminal/` | `FullyQualifiedName~Forge.App.Tests.Terminal` |
| `src/Forge.App/Settings/` | `FullyQualifiedName~Forge.App.Tests.Settings` |
| `src/Forge.App/{Changes,Editing,ViewModels,PlanPanel*}`(Palette 源码散在 `ViewModels`/`Views`,由对应规则覆盖) | 对应命名空间(`Changes`/`Editor`/`ViewModels`) |
| `src/Forge.App/Localization/`、`Resources/` 文案 | `Localization` + `KeyRegistry` + `StringTable` + `NoHardcodedChinese` |
| `src/Forge.App/` 其他 | `dotnet test tests/Forge.App.Tests`(L2) |
| 根级构建/CI 配置(`Directory.Build.props`、`Directory.Build.rsp`、`global.json`、`Forge.slnx`、`.github/workflows/`) | L3 |
| `src/Forge.Core/` | `dotnet test tests/Forge.Core.Tests`(L2) |
| `src/Forge.Workspace/` | `dotnet test tests/Forge.Workspace.Tests`(L2) |
| `src/Forge.{IO,Editor,Mcp,Approval,Workflows,Scripting,HostClient,Ipc}/` | 同名 `tests/Forge.<M>.Tests`(L2) |
| `src/Forge.Abstractions/`、`Plugin.Sdk/`、`Ipc*/`、存储 schema、AOT 配置 | L3 |
| 文档、spec、`.md`、`AGENTS.md` | 不跑测试 |

L1 只跑一个命名空间时,若改动跨了两个落点(例如同时改了 `Workbench` 与 `Git`),把过滤器用 `|` 串起来,而不是退化成 L2。落点表的数据副本在 `scripts/test-map.json`(脚本按最长前缀匹配,`Ipc*` 依 L3 行优先于同名-tests 行;l1 的 `ns` 可为数组,一落点多命名空间;根级构建/CI 配置归 l3 `why=infra`);改表须两处同步。

## 新增测试的准入

1. 优先改现有测试文件,不新开文件;同一行为对应一个测试文件。
2. 一个行为一个主要测试。不要用两个测试分别验证“返回值”和“同一个返回值的一部分”。
3. 多个输入走 `[Theory]`/`[InlineData]`,不写 `Foo_works_case1`、`Foo_works_case2`。
4. 不测框架自身行为、自动生成代码、简单属性转发。
5. 不为测私有方法放宽生产代码可见性。
6. 不为覆盖率凑测试。覆盖率下降不是删除测试的理由,覆盖率上升也不是新增测试的理由。
7. 修 Bug 必须配一个能复现它的回归测试;测试名写“为什么这个行为重要”,不是“步骤 1/2/3”。
8. 新增测试必须能回答:它保护的是哪条会被误改的契约?答不上来就不加。

## 清理与删除规则

删之前逐项确认:行为是否已被别处覆盖、是否守边界条件、是否是历史 Bug 的回归测试、是否依赖外部环境、是否还对应活着的产品功能。

**可以删**:完全重复的用例;测已删除功能的用例;没有实际断言的用例;只验证第三方库自身行为的用例;已被更完整用例取代的用例。

**不能删**:核心业务规则;权限、安全、数据一致性;历史 Bug 回归;关键协议兼容性;难以用别的方式发现的边界条件。

不稳定但暂不能修的用例:记录原因并隔离(跳过 + 注明跟踪点),不直接删除,也不永久 `Skip` 了事。

## 已实施的清理(2026-11-14)

- `tests/Forge.App.Tests/PropertyChangeLog.cs` 新增共享记录器,替代 18 个测试文件里 37 处重复的 `new List<string?>() + PropertyChanged += ...`(Deslop 簇 `e0c452cb2c5873c7`,mass 1122,35 处)。行为不变,断言的属性名序列完全一致。
- 修好 main 上一直红的 `Forge.App.LiveSwitch.Tests.Fixed_panel_titles_follow_the_language_live`:期望标题补上新增的“问题”/`Problems` 页,而不是删掉该用例。
- 删掉空目录 `tests/Forge.Plugin.Sdk.Tests/`(只剩 `bin`/`obj`,无 csproj 与源码)。

验证:`dotnet build tests/Forge.App.Tests` 0 警告 0 错误;改动触到的 12 个命名空间(含 Providers)2,334 例全绿(8 m 5 s);`Forge.App.LiveSwitch.Tests` 8/8 全绿(22 s)。

## 夹具登记表(2026-11-14 deslop 审计后的收敛)

同一测试项目内写新测试时**优先引用既有替身/夹具**;没有就往登记文件里加一份,不允许再声明私有同名副本。全仓 deslop 扫描(`docs/analysis/deslop-audit.md`)与五路并行重构已把下列注册表建好:

| 项目 | 登记文件 | 内容 |
| --- | --- | --- |
| `Forge.App.Tests` | `SessionStoreDoubles.cs` | `MemorySessionStore`(带日志的 ISessionStore+ISessionCatalog)、`SessionCatalogDouble`(平凡成员基类)、`ThrowingSessionCatalog`/`ThrowingSessionStore`(虚方法全抛) |
| `Forge.App.Tests` | `PrPlatformDoubles.cs` | `ThrowingPrPlatform`(非 DIM 成员全抛;`Merge/MarkReady` 是接口默认实现,刻意不声明) |
| `Forge.Core.Tests/Agents` | `TestSupport.cs :: SubAgentTestSupport` | `Scout` 定义、`Options()`、`Spawn()`、`ToolRounds`、`AgentId`、`ParentSpawnsScout()` |
| `Forge.Integration.Tests/Terminal` | `TerminalHosts.cs`/`TerminalAsserts.cs` | `TerminalWait.WaitForPidFileAsync`、`TerminalSleeper.SpawnCommand`、`TerminalFlood`、`NoCleanupFailures` |
| `Forge.Editor.Tests/Lsp` | `WorkspaceDiagnosticsTests.cs :: WorkspaceFixture` | 诊断聚合器+目录+池的一体化夹具与 `OpenSessionAsync` |

注意 C# 默认接口方法(DIM)陷阱:替身基类下再声明 DIM 同名公共方法不会重实现接口,必须在类上重新列出接口(`RemovalFlowTests`/`TreeMenuRowTests`/`GitSyncShipFinaleTests`/`ChecksPanelViewModelTests` 里有四处先例)。`Forge.App.LiveSwitch.Tests` 的替身拷贝是跨程序集结构性重复,动它需要 csproj 层面动作,未收敛。

## 已知债务(未处理,按收益排序)

1. **放开 `Forge.App.Tests` 的并行。** 前置条件是把数据根从进程级环境变量改成可注入的实例(或把所有读写 `AppPaths` 的测试类收进同一个串行 collection),再逐个命名空间验证;放开后需连续跑 3 轮全量确认无偶发。收益最大、风险也最大,必须单独一个任务做。
2. **测试进程清理。** 中断的测试留下 `testhost`/`ExtensionHost` 孤儿,锁住构建输出。需要在测试侧加全局超时与退出清理。(过渡措施已落地:`Directory.Build.props` 挂 `scripts/build-hygiene.ps1`,构建时自动结束孤儿并报活锁;测试侧全局超时仍待做。)
3. **把 5 家供应商的认证测试合成参数化套件。** 自重复率:`XAiAuthenticationTests` 56%、`MetaAuthenticationTests` 51%、`KimiAuthenticationTests` 47%、`ChatGptAuthenticationTests` 36%(合计 2,757 行里 1,305 行重复);4 个 `Scripted*Handler` 除记录类型外逐行相同。做法:抽 `ScriptedAuthHandler<T>` 基类,再按供应商把公共用例参数化,保留各家协议特有的用例。
4. **`throw new NotSupportedException()` 假实现样板。** 183 处,分布在 23 个测试文件里。按接口抽 abstract 基类,保留“生产代码不该调用它”的失败语义,不要直接删。
5. **`HostClient`/`Workbench` 的 arrange 样板。** `await using var host = new FakeHost(); await using var client = await ClientFactory.StartAsync(host); using var limit = TestLimit.Create();` 重复 69 处(Deslop 簇 `e49c7b620d9adb58`,mass 2108,全仓第一);Workbench/Lifecycle 的 shell 测试自重复 21–30%。属于阅读与构建成本,不改善回归保护,优先级低于前四项。
6. **`tests/Forge.Plugin.Sdk.Tests/` 空目录。** 只剩 `bin`/`obj`,没有 csproj 与源码(2026-11-14 已删除)。

## 不做的事

- 不把测试拆成更多项目来“并行化”;项目边界按 `architecture.md` 的模块走。
- 不引入新的测试框架或包(见 `architecture.md`「NuGet 引用规则」)。
- 不为减少测试数量而删除测试;数量不是指标,被保护的行为才是。
