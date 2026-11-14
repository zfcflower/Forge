# 根级规格(goal-and-requirements / architecture / testing)审计发现

审计人:主 Agent(直接核查)。证据均为静态核查(csproj/源码扫描/文件存在性),未运行构建与测试(本轮为文档审计,按 AGENTS.md 文档改动不跑测试)。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-ARCH-01 | 依赖只能自上而下,下层不引用上层 | architecture.md §分层与依赖方向:12 | 全部 src/*/*.csproj ProjectReference | 无(结构约束,无专门测试) | PASS |
| F-ARCH-02 | Forge.Editor 只依赖 BCL 与 Forge.IO | Forge.Editor/SPEC.md 首段:9 | src/Forge.Editor/Forge.Editor.csproj(多引用 Forge.Abstractions,代码中无任何使用) | 无 | PARTIAL |
| F-ARCH-03 | Forge.ScriptRuntime 只依赖 SDK 与 BCL,不含 Ipc | Forge.ScriptRuntime/SPEC.md 首段:9 | src/Forge.ScriptRuntime/Forge.ScriptRuntime.csproj(多引用 Forge.Ipc.Protocol,代码仅在 doc 注释提及,无类型使用) | 无 | PARTIAL |
| F-ARCH-04 | 主程序项目 IsAotCompatible、App PublishAot | architecture.md §关键决策(Native AOT):36 | 13 个 AOT 侧 csproj 均 IsAotCompatible=true;Forge.App.csproj PublishAot=true | 无(构建期分析器保证) | PASS |
| F-ARCH-05 | NuGet 白名单与钉版 | architecture.md §NuGet 引用规则:88 | 全部 PackageReference:Avalonia 12.1.6、AvaloniaEdit 12.0.0、MessagePack 4.0.0-rc.1、Microsoft.Data.Sqlite 11.*、Roslyn 5.*、MEL 11.*、ZLogger 2.6.2、ZLinq 1.6.1、xunit 2.*/3.*、Test.Sdk 18.* | 无 | PASS |
| F-ARCH-06 | Mermaider/Svg.Skia 仅在 W11 落地且满足条件时引入 | architecture.md §NuGet 表:104 | 无任何引用;App SPEC 未决:454 明确 Mermaid 归 W11 未做 | 无 | PASS |
| F-ARCH-07 | P/Invoke 只在 JIT Host 的 WorkerProcessOwnership(含 Pty partial) | architecture.md §原生进程归属边界:56 | grep DllImport/LibraryImport 全仓:仅 src/Forge.ExtensionHost/WorkerProcessOwnership.cs 与 WorkerProcessOwnership.Pty.cs;HostClient/Editor 的命中均为字符串字面量 | 无(源码扫描) | PASS |
| F-ARCH-08 | App 不出现 SqliteConnection/CREATE TABLE/write-tree/GIT_INDEX_FILE | Forge.App/SPEC.md §边界:15 | grep src/Forge.App 零命中 | tests/Forge.App.Tests 源码扫描类门槛(未运行) | PASS |
| F-ARCH-09 | 工具面固定 7 个:read/write/edit/ls/find/grep/bash | architecture.md §关键决策(文件与命令):32 | src/Forge.IO/Tools/{Read,Write,Edit,List,Find,Grep,Bash}Tool.cs | tests/Forge.IO.Tests(IO 路审计) | PASS |
| F-ARCH-10 | ExtensionHost 不引用 Core;主程序不引用 Roslyn | architecture.md §分层:24 | ExtensionHost.csproj 无 Core 引用;Roslyn 仅在 Scripting(JIT 侧) | 无 | PASS |
| F-ARCH-11 | 层表称 PluginWorker「加载并执行动态 C# 插件与 run_code 脚本」 | architecture.md §分层表:21 | run_code 实际走 ScriptExecutor 一次性执行器(architecture.md §codemode:54 自述已切换);PluginWorker SPEC 只装载插件 | 见 12-ext/13-script | FAIL(规格内部矛盾,见详情) |
| F-TEST-01 | 13 个测试项目 | testing.md §现状基线:13 | tests/ 下 13 个测试项目目录 | — | PASS |
| F-TEST-02 | Forge.App.Tests 全程序集串行 | testing.md §结构性问题 1:47 | tests/Forge.App.Tests/TestAssemblySettings.cs `[assembly: CollectionBehavior(DisableTestParallelization = true)]` | — | PASS |
| F-TEST-03 | TempDataRoot.Redirect 改 3 个进程级环境变量,25 个测试类使用 | testing.md §结构性问题 2:48 | grep TempDataRoot 命中 22 个文件(夹具合并后数量漂移) | — | PASS(数字过时,见规格质量) |
| F-TEST-04 | 夹具登记表 6 份登记文件存在 | testing.md §夹具登记表:150 | SessionStoreDoubles.cs、PrPlatformDoubles.cs、Agents/TestSupport.cs、Terminal/TerminalHosts.cs、Terminal/TerminalAsserts.cs、Lsp/WorkspaceDiagnosticsTests.cs 均存在 | — | PASS |
| F-TEST-05 | LiveSwitch 的 Fixed_panel_titles… 已修复 | testing.md §已实施的清理:139 | tests/Forge.App.LiveSwitch.Tests/LiveSwitchTests.cs:92 存在该用例 | 未运行(历史 8/8 绿记录) | UNVERIFIED(静态存在,未重跑) |
| F-TEST-06 | 空目录 Forge.Plugin.Sdk.Tests 已删除 | testing.md §已实施的清理:141 | tests/Forge.Plugin.Sdk.Tests 不存在 | — | PASS |
| F-TEST-07 | 基线表用例数/耗时(2026-11-14 实测) | testing.md §现状基线:15 | 历史测量记录,本轮未重跑 | — | UNVERIFIED(历史记录,保留) |
| F-GOAL-01 | 全 C#、无 Python/JS 运行时依赖 | goal-and-requirements.md §Non-Goals:33 | src 全部为 C# 项目;无 python/js 运行时引用 | 无 | PASS |
| F-GOAL-02 | .NET 11 / C# 15 | goal-and-requirements.md §Technology:63 | global.json sdk 11.0.100;Directory.Build.props net11.0 / LangVersion 15 | — | PASS |
| F-GOAL-03 | 能力清单(工作区/Agent/界面)逐条落地 | goal-and-requirements.md §Capabilities:39 | 映射到各模块 SPEC,见分路审计 | 见各路 | PASS(映射成立,细节见各路结论) |

## 非 PASS 项详情

### F-ARCH-02 / F-ARCH-03(PARTIAL,边界漂移)
- 证据:两个 csproj 各含一个代码中零使用的 ProjectReference(Editor→Abstractions;ScriptRuntime→Ipc.Protocol)。
- 影响:不改变运行时行为,但使「只依赖 BCL+X」的规格声明失真,且 ScriptRuntime 的依赖闭包因此含 Ipc(与其 SPEC「不含 Ipc」直接冲突)。
- 处理类型:**实现缺陷(轻微)**——保留规格要求,记录代码缺口:后续代码任务删除这两个冗余引用;本轮不改代码。

### F-ARCH-11(FAIL,规格内部矛盾)
- 证据:architecture.md 分层表(行 21)仍写 PluginWorker 执行 run_code 脚本;同文件 codemode 决策(行 54)明确「正式路径已切换为 Host 常驻编译服务 + 每次调用一个一次性执行器」;ScriptExecutor SPEC 与 PluginWorker SPEC 均按新架构写。goal-and-requirements.md Technology 表「codemode 在 Extension Host/Worker 中编译执行」同样过时。
- 处理类型:**规格错误(过时)**——清理时把分层表与 goal 的措辞改为新架构(编译在 Host 编译服务,执行在一次性 ScriptExecutor;PluginWorker 只跑插件)。

## 已实现但缺测试清单
- 依赖方向/AOT 标志/P/Invoke 边界:目前靠构建期分析器与人工扫描,无自动化守护测试(可考虑加源码扫描门槛,属建议不属本轮)。

## 程序→规格:未记录行为
- 无(根级)。

## 规格质量问题
1. architecture.md:24「(新分层待 Stage 0–2 验证,未确认)」与同文「可行性已由 Stage 0 实测确认」矛盾,过时括注应删。
2. architecture.md:36「五个主程序项目…已开启 IsAotCompatible」实际 13 个项目开启,数字过时。
3. architecture.md:21 与 goal-and-requirements.md:67 的 PluginWorker/codemode 措辞过时(见 F-ARCH-11)。
4. testing.md:48「25 个测试类」现为 22 个文件(夹具合并后漂移),数字应更新或改为不写死。
5. testing.md 基线表与「已实施的清理」是带日期的历史记录,保留但应明确其历史属性(已有日期标注,合格)。
6. ExtensionHost SPEC frontmatter depends-on 含 forge-script-executor(进程启动关系,无项目引用)但缺 forge-workflows(csproj 实际引用)——frontmatter 与真实依赖不一致,清理时修正。

## 业务闭环缺口
- 无(根级;跨模块闭环见各路发现与汇总报告)。