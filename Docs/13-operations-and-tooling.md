# 13 运维、日志、更新与工程工具链

本文件说明进程级行为（单实例、启动、日志、崩溃）、自动更新、CI 与基准工具，以及开发期如何抓取模型请求原文。

## 13.1 启动与组合根

- 组合根构造顺序即依赖方向，拆除按逆序（例外：审批先于存储/editor/mcp 释放；`FileArtifactStore` 不单独释放，靠进程级 `ClearAllPools` 清池）：
  `AppPaths` → 审批数据库路径（不打开）→ `ExtensionHostSupervisor`（`--worker`/`--executor` 显式传路径）→ `WorkspaceServices` → `McpServices` → `EditorServices` → `ProviderStore`/`ProviderCatalog` + `forge.db` 上的 5 个存储 + `TurnCheckpointService` + `WorktreeService` → `AgentRuntimeManager`。
- 审批库归 Host 所有，App 只拿到 `<host 数据目录>/approvals.db` 的路径。
- 启动顺序：单实例锁 → 迁移 → `HealMissingDirectoriesAsync` → 把未确认的 `storage_notices` 合成一条通知并确认 → 恢复标签 → 物化内置技能 → 自动化引擎 `Attach` 后启动。
- UI 线程不碰存储：所有存储/SQLite/文件调用经后台助手，不在调度器上 await。唯一例外是退出：最后一次布局保存后台执行，UI 线程最多等 3 秒（超时只记日志）。
- 降级：提供商未配置 / Host 未找到都降级为明确的 UI 状态，绝不抛异常或伪造模型调用。

## 13.2 诊断日志

- `Microsoft.Extensions.Logging` 是抽象、ZLogger 是唯一输出端，二者只在 `Forge.App` 引用；其他项目写 `System.Diagnostics.Trace`。
- `Logging/AppLog` 持有进程唯一的 `ILoggerFactory`（默认 `NullLoggerFactory`，测试与无头场景不写文件）；`Program.Main` 在 Avalonia 启动前按 `AppPaths` 的数据根配置，退出时 `Dispose` 冲刷缓冲。`AppLog.Create("<类别>")` 返回按调用时刻解析当前工厂的转发 logger；App 内按领域集中在 `Logging/AppLogs`，以 ZLogger 插值 API（`ZLogWarning($"…")`）写日志。测试用 `AppLog.Use(factory)` 临时换工厂。
- 桥接：`TraceBridgeListener` 注册进 `Trace.Listeners`，把其他项目的 `Trace.WriteLine`/`TraceWarning` 转进同一工厂（消息以 `[tag]` 开头取 `tag` 作类别，否则 `trace`；`TraceWarning`/`TraceError` 映射为 Warning/Error）。Avalonia 设 `Logger.Sink`：只收 Warning 及以上、保留原级别、area 作类别，自身从不抛错。
- 输出：滚动文本 `<数据根>/logs/forge-<yyyyMMdd>_<序号>.log`，按天滚动、单文件 10 MB；启动时后台删除 14 天前的日志，删除失败忽略。格式一行一条 `本地时间(毫秒) 级别缩写 类别 消息`（ZLogger 的 `local-longdate`/`short` 直接写 UTF-8，不产生字符串），异常按 ZLogger 默认格式追加；不用 JSON 格式化器。
- 最低级别默认 Information，`FORGE_LOG_LEVEL`（`Trace`/`Debug`/`Information`/`Warning`/`Error`）覆盖；`FORGE_TRACE_FILE` 已废弃。
- 内容边界：不记密钥、token、请求头值、提示词与消息正文、文件内容；只记事件、标识（会话 id、提供商 id、路径）与错误。被吞掉的异常记 Warning，导致功能失效的记 Error。
- 热路径：流式增量、渲染、逐键处理里不写 Information 及以下；需要时先判 `IsEnabled`。
- 崩溃：日志目录不可写时 `Configure` 返回空操作句柄，应用照常启动、不写文件；`CaptureCrashes` 把未处理异常记为 Critical 并在进程终止前冲刷，未观察的任务异常记为 Warning；工厂释放后的转发 logger 退化为 `NullLogger`。

## 13.3 单实例

- `SingleInstanceLock.TryAcquire(dataRoot)` 在数据根独占打开 `forge.instance.lock`；拿不到只显示“Forge 已在运行。”后退出——不转交、不 IPC、不把已有窗口带到前台。锁持有到 `ShutdownAsync` 结束，进程崩溃由 OS 释放句柄（不是“存在即锁”的标记文件）。

## 13.4 自动更新

- 清单由**发布流程**生产、App 只消费：`scripts/make-update-manifest.ps1` 生成 schema 1 的 `{schema, channels:{<stable|beta>:{version, releasedAt, notes, packages:{<RID>:{url, sha256, sizeBytes}}}}}`，包名 `Forge-<version>-<rid>.zip`（Windows）/ `.tar.gz`（其余），RID 取 `RuntimeInformation.RuntimeIdentifier`。
- 脚本两条硬规则就是清单语义：同一 RID 出现两个产物、或没有任何匹配产物都失败；`-MergeFrom` 把上一份清单的渠道原样带入、再覆盖本次发布的那一键（一份清单必须回答每个渠道）。默认地址指向滚动 `manifest` tag 的 `updates.json`（而不是 `latest`，`latest` 会按发布顺序漂到 beta 上）。
- 检查：`updates` 分区是唯一入口（清单地址、频率、渠道、当前版本、立即检查/取消、上次检查、说明与进度、打开下载位置、安装并重启）。三项偏好（`UpdatesManifestUrl`/`UpdatesCheckFrequency` daily|weekly|never/`UpdatesChannel` stable|beta）每次现读，地址必须是绝对 http(s)，留空即关闭检查。上次检查时间与结局落 `ui_state` 的 `updates.lastCheck`；失败 6 小时后重试（开机时网还没通的那次不该等一天）；`0.0.0-ci.*` 是开发构建，只许手动检查。
- 解析：1 MiB 上限、深度 16、`schema` 必须为 1、任一渠道字段坏即整体失败、`url` 必须绝对 http(s)、`sha256` 必须 64 位十六进制、`sizeBytes` 可有可无。**所选渠道缺失不是失败**——那是“用户这档没有新版”，回已是最新。版本按 SemVer 优先级比较（数字段 → 预发布逐标识，`beta.2 < beta.10`，预发布先于发布，`+sha` 忽略）。
- 下载：流式 64 KiB 缓冲、进度、可取消、2 GiB 上限；声明 `sizeBytes` 就比体积，空包与哈希不符都拒；通过后暂存 `<数据根>/updates/<version>/` 且只留这一版。
- 安装并重启：解包到该目录的 `app/`，再从 `<数据根>/updates/.applier-<guid>` 副本启动新的可执行当应用器——**App 自己从不 rename 或替换安装目录**。之后的换树、确认、回滚与 `.old-<version>` / `.failed-<version>` 命名、`install.log` 记录，逐步契约见 `src/Forge.App/SPEC.md`「自动更新」，此处不重复。
- **发布管线未接**：只有脚本本身，没有 tag 触发的 workflow 去按 RID 打包、算哈希、把 `updates.json` 推上滚动 tag；默认地址下没有可解析资产，更新只在手工发版、手工跑脚本并手工上传时生效（见 14）。

## 13.5 CI 与构建矩阵

- `.github/workflows/ci.yml` 的 `test` 作业：三平台（ubuntu / windows / macos）× `dotnet build Forge.slnx -v m` + `dotnet test Forge.slnx --no-build --output normal`（`TreatWarningsAsErrors` 打开，单个警告即失败）。测试项目是 `tests/Forge.App.Tests`（xunit.v3 4.0.3 + Avalonia.Headless 12.2.0，`Forge.App` 对它开了 `InternalsVisibleTo`）：用例成员数百个（`[Fact]`/`[Theory]`）。仓内 `artifacts/app-tests-run.log` 记录的一次运行为 **3 失败 / 5938 通过 / 0 跳过 / 5941 总计**，不是当前全绿证明；当前全量结果需以新一次实际测试输出为准。
  - **测试跑在 Microsoft.Testing.Platform（MTP）上，不是 VSTest**：`global.json` 钉 `"test": { "runner": "Microsoft.Testing.Platform" }`，测试项目是 `OutputType=Exe` 且只引 `xunit.v3`（不再引 `Microsoft.NET.Test.Sdk` 与 `xunit.runner.visualstudio`）。SDK 11 上 MTP 已拒绝 VSTest：两者同时存在时构建直接报 `Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later`。命令形式：`dotnet test --project <csproj>` 或 `dotnet test Forge.slnx --no-build` 都可用；`--filter` 换成 `--filter-class`/`--filter-method`/`--filter-query`，`--logger` 换成 `--output`。
- 已有运行时覆盖（三 OS 矩阵里真跑，不是只构建）：`forge.db` 的 schema 与时间戳守卫、按会话 purge、`turn_metrics` 读写与缺表回退（各自拿临时 SQLite 文件）；git 变更探针、diff 覆盖率账本、remote 配置解析（临时真实仓库，真跑 `git`）；`bash` 命令门禁与执行队列（进程替身，不起真实 shell）；三个流式适配器的首字节到流末计时、取消与缓存字段降级（假 SSE 传输）；转录视图模型、轮次分隔线与回放种子；headless 下 `TranscriptStackPanel` 的 Extent、`ScrollIntoView` 与宽度变化。
- **仍未产生运行时覆盖**：POSIX 进程归属、PTY 与终端、`ExtensionHost`/`PluginWorker` 的拉起、MCP 传输、编辑器，以及 AOT 产物里的行为；还有提示词来源变更的**信号侧**（真实 `FileSystemWatcher` 只测了路径筛选 `Relevant`，因为拉一下信号就要靠操作系统通知的时延）。这些没有测试宿主，靠人工冒烟与 AOT 发布冒烟兜底。
- `publish-aot` 作业：Windows 上 `dotnet publish src/Forge.App -c Release`，断言产物存在且体积 >10 MB（AOT 分析器警告即错误）。**不设体积上界**：宽带门禁的检出下限（按 2026-11-23 实测值算 8 MiB）盖不住真实回归，却带来源码不变时的重定基线义务，而 CI 装 `11.0.x`、`global.json` 钉 `11.0.100` 时还会因工具链换代误报；体积只按 §13.7 记录，不入门禁。
- **三平台 AOT `publish` 矩阵未接线**（macOS/Linux publish 从未在任何地方跑过），保持为显式后续项而不是永久红。
- 测试纪律见 `AGENTS.md` §4 的入仓门槛，本文件不重复维护。当前代码**只提供一处测试接缝**：`Forge.App` 对 `Forge.App.Tests` 的 `InternalsVisibleTo`；没有 `FORGE_HOST_TEST_*` 环境变量，`ApprovalStoreOptions` 只有 `BusyTimeout` 与 `ImportLegacyJson` 两个产品开关，Debug 与 Release 行为一致。

## 13.6 基准

- `tools/Forge.Bench`：无框架、无包，同一循环内交替测量基线与目标、取配对中位差（比值稳定，绝对值噪声可达 1.5–2×）。覆盖 read / write / copy / walk / grep / process / output / concurrency / ratelimit / usage / composer / transcript / ledger 场景，每个场景自带验收检查；每条场景族各自全通过为一次验收（`-- ledger` 等过滤跑法见 `Program.cs`，`transcript.*` 与 `composer.*` 需在独立进程里单跑——它们会初始化 Avalonia，与全量循环互斥）。
- 典型实测（win-x64 32 逻辑核、Release）：
  - `read` 100 MiB 全读 59.8 ms（1.5–1.6 GB/s）；100 MiB 取 100 行 0.071 ms。
  - `write` 1 KiB 原子写 2.67 ms（差距全在 `fsync`）；1 MiB 原子写 4.34 ms（比 BCL 直写快 21%）。
  - `FileWalker` 1k/10k/100k：0.79 / 9.19 / 56.9 ms。
  - 托管 `grep` 10k 文件 114 ms（命中）vs ripgrep 103–105 ms（慢 11–15%，命中数相同）。
  - `ProcessRunner` 100 MiB 子进程输出 69.8 ms（1.40 GB/s）、分配 1.39 MB、工作集增量 <6 MiB。
  - 限速器：0 字节/次、无争用 p50 <100 ns、8 线程共享一个桶时每次都授权。
- 运行约定：`Forge.App` 必须通过 `FORGE_RUN=1` 启动，运行输出统一到 `artifacts/run/`；运行测试的 shell 禁止设置 `FORGE_RUN`；遇到 `MSB3027`/`MSB3026` 先检查 `Forge.App`、`testhost`、`ExtensionHost` 是否占用文件，确认孤儿进程后再清理，用户正在运行的进程报告阻塞而不是擅自结束；不通过切换输出目录掩盖文件锁。
- **Avalonia 构建统计任务**：Avalonia 12.2.0 带来的 `Avalonia.BuildServices` 12.0.0 在每次非设计时构建前跑 `AvaloniaStats`（`BeforeTargets=CoreCompile`，`ContinueOnError`），会打印 “Avalonia Trial requires telemetry…” 并向 Avalonia 上报构建记录（11.3.2 时代同一个任务已在跑，只是提示文案不同）。`AVALONIA_TELEMETRY_OPTOUT=1` 能关掉上报，但**只在机器上没有付费/试用许可证时生效**（包的 README：Trial 与 Community 许可证不允许退出，本机登了 Avalonia 账号、带 Trial 票，实测该变量无效）；CI 里没有许可证，所以 `.github/workflows/ci.yml` 已在 workflow 级设了它。那行提示是 Normal 重要级的 MSBuild 消息，本地要不见它只能用 `-v:q`，或者拿到付费许可证。构建结果不受影响（Debug/Release/AOT 均 0 警告）。

## 13.7 发布形态

- 主程序 Native AOT、不可变；发布体积（win-x64，.NET 11 / Avalonia 12.2.0，`dotnet publish src/Forge.App -c Release`，`PublishAot` 隐含 `PublishTrimmed=true`，`OptimizationPreference` 默认 Speed）：
  - **2026-11-23 升级后同机重测（Avalonia 12.2.0 + Svg.Skia 5.2.3，即 SkiaSharp 4.148.0 / HarfBuzzSharp 14.2.0）**：`Forge.App.exe` **79.12 MiB**，发布目录去掉 `.pdb` 后 **100.26 MiB**（libSkiaSharp 11.61、av_libglesv2 5.14、libHarfBuzzSharp 1.97、e_sqlite3 1.88、libonigwrap 0.52 MiB），AOT 分析器与 ILLink 0 警告。与下条 12.1.6 工作树 75.99 MiB 相差 +3.13 MiB，归因于 Avalonia 升版与 SkiaSharp/HarfBuzzSharp 抬版，不作优化或回归结论。
  - **2026-11-16 同机同配置重测**：`Forge.App.exe` **72.92 MiB**；`OptimizationPreference=Size` 62.27 MiB；`StackTraceSupport=false` 69.83 MiB；发布目录去掉 `.pdb` 后 93.31 MiB（含原生库 libSkiaSharp 11.09、av_libglesv2 5.14、e_sqlite3 1.88、libHarfBuzzSharp 1.73、libonigwrap 0.52 MiB），`.pdb` 另占 350.31 MiB、不随产品分发。
  - 早先同机记录：最小 Avalonia 窗口 16.5 MiB、加 AvaloniaEdit+TextMate 28.7 MiB、完整 App 49.7 MiB（约 +12 MiB 编辑器栈、+21 MiB 产品代码与其余模块，嵌入资源 8.6 MiB）；`Size` 42.8 MiB、关托管堆栈追踪 40.8 MiB。两次记录（49.7 MiB 与 72.92 MiB）之间的逐项差值仍未核对（要检出当时的提交重测）；当前体积的构成见下面两条，但不得据此宣称体积回归或优化。
  - 两者都不建议默认开 `Size` 或关闭堆栈追踪（前者牺牲速度，后者丢掉插件与 Agent 链路最需要的托管堆栈）。
- **体积构成（2026-11-23 同机实测，win-x64，Avalonia 12.1.6）**：`Forge.App.exe` **75.99 MiB**（工作树含未提交改动，与上条 72.92 MiB 的差不属优化或回归）。
  - 复现：`dotnet publish src/Forge.App -c Release -p:IlcGenerateMstatFile=true` 产出 `src/Forge.App/obj/Release/net11.0/win-x64/native/Forge.App.mstat`（45.18 MiB，落在已忽略的 `obj/`）；`sizoscope-cli` 只做两份 mstat 的 diff，单文件归因要自己遍历 `MstatData` 并按类型/方法/字段/冻结对象/清单资源/blob 求和。
  - mstat 覆盖 **63.70 MiB**：生成代码（方法）32.27、类型数据 11.33、嵌入清单资源 9.37、无归属冻结对象 5.15、数据 blob 4.77（反射与堆栈元数据 2.62、反射类型装载表 0.76、接口分派单元 0.70）、字段 0.44、有归属冻结对象 0.36；余下 **12.29 MiB** 是 mstat 不归因的 NativeAOT 运行时本体（GC、异常处理、PE 头与对齐），由 exe 减 mstat 得出、未独立测量。
  - 按程序集（枚举覆盖 50.74 MiB，无归属冻结对象与部分类型规格未摊入）：`System.Private.CoreLib` 12.63、`Forge.App` 7.74、`TextMateSharp.Grammars` 6.38、`Avalonia.Base` 3.30、`Avalonia.Fonts.Inter` 1.79、`Forge.Core` 1.35、`Avalonia.Themes.Fluent` 1.27、`Mermaider` 1.26、`System.Linq` 1.05、`Avalonia.Controls` 0.98、`System.Text.Json` 0.91、`Forge.Workspace` 0.89、`Svg.Custom` 0.83、`MessagePack` 0.68、`System.Text.Encoding.CodePages` 0.65、`System.Private.Xml` 0.62、`AvaloniaEdit` 0.57、`Avalonia.Win32` 0.57 MiB；Forge 自身 12 个程序集合计 11.89 MiB。
- 整块可裁的最大项是 `TextMateSharp.Grammars` 6.38 MiB 嵌入式语法资源（`RegistryOptions` 按语言名取用）；更小的整块候选是 `Avalonia.Fonts.Inter` 1.79（只供 UI 字体的直接引用）、`Avalonia.Themes.Fluent` 1.27、`System.Text.Encoding.CodePages` 0.65 MiB。这些都属产品行为变更（部分语言失去高亮 / 字体与主题换掉 / 旧字符集网页与 Windows 控制台输出解码失效），一并未做；方法体、CoreLib、Avalonia 主体不是靠开关或删引用能减的。
- **不采用 ILLink substitution**（`ILLink.Substitutions.xml` + `<IlcArg Include="--substitution:…" />` 把三方方法体换成常量，推进死代码消除）：本仓依赖闭包里没有同形的链条。文章点掉的是 `Microsoft.Diagnostics.Runtime` 构造函数里由运行时字符串比较锚定的 `Azure.Identity` 链；本仓没有身份/诊断类重依赖，OS 条件分支（`Avalonia.Desktop` 的 X11/macOS 后端）已被 ILC 按 RID 折掉——mstat 里 `Avalonia.X11` 仅 1.3 KiB、`Avalonia.Native` 0.19 MiB。代价侧：它钉死三方的私有签名，签名不变而语义变了会**静默**改变三方行为，且被折叠掉的分支按定义不在任何测试路径上，过不了 §4 的入仓门槛。重新评估的前提是 mstat 先指出一个具体类型。
- **扩展侧要求用户已安装 .NET 11 运行时**（`net11.0`；`global.json` 固定 SDK `11.0.100`）：在 JIT 侧跑的是三个进程——常驻的 `Forge.ExtensionHost`（每应用 1 个）、`Forge.PluginWorker`（每插件 1 个）、`Forge.ScriptExecutor`（每次 `code.execute` 1 个，一次性）；主程序不需要运行时。Host 的 `--dotnet` 指定拉起 `.dll` 形态 Worker/执行器的运行时（默认是跑 Host 的那个），`Forge.HostClient` 的 `RuntimePath` 置空时直接执行 apphost 可执行文件——所以“随包私有运行时”（`<install>/runtime/dotnet.exe`）不需要改代码，只需把这两处指过去。
- **AOT 发布产物不含这三个 JIT 组件**（实测 `dotnet publish src/Forge.App -c Release` 的输出只有 `Forge.App.exe`、原生库、`.pdb` 与 `Assets/`；`Forge.App.csproj` 不引用它们的项目）。`JitHostLayout` 只按 `FORGE_JIT_DIR` 与仓内构建布局（`src/<asm>/bin/<cfg>/net11.0`、`artifacts/publish/<asm>/<cfg>`、`publish/<asm>/<cfg>`）定位它们，仓库里也没有发布它们的脚本或 CI 步骤——把 publish 目录原样发给用户时宿主起不来（`diag.jitHostMissing`），终端、插件、`run_code` 全部不可用。
- 实测对照（本机 win-x64、同一提交、剔除 `.pdb`）：Native AOT 主程序目录 **94 MiB**、启动到窗口 **171 ms**；同一提交 `-p:PublishAot=false --self-contained` 的 JIT 版 **141 MiB**、**874 ms**（3 次中位）；框架依赖 JIT **58 MiB**。私有运行时单份（`Microsoft.NETCore.App` 共享框架）约 **75 MiB**，上面 105 MiB 的 JIT 目录 gzip 后 **44 MiB**。因此“反正要带运行时”时 AOT 不再省体积（94 + 75 > 141，本体反而大 ~28 MiB），它的收益是启动、内存、不可变边界与主程序不随用户机器 .NET 版本漂移。
- 原生库不能静态链接，“单文件”不成立。

## 13.8 开发期抓取模型请求原文（代理）

§13.2 的诊断日志与 `Docs/03` §3.8 的用量日志按既定边界**不写提示词、消息正文与请求头值**，所以排查“我们究竟发了什么”时不要指望调高日志级别——原文得另外抓：常规情况把出站流量引到本地代理（零代码），Debug 构建另有逐请求的原样落盘到工作目录的 `.log/`（见“Debug 构建的逐请求原文”）。代理的两种形态分工不同，不是替代关系：

| | 透明 MITM（正向代理） | 本地反向代理（改基址） |
|---|---|---|
| 路径 | Forge → `127.0.0.1:8080` → 真主机，代理终结 TLS | Forge → `127.0.0.1:9000`（明文）→ 真主机，代理自己发起 TLS |
| 覆盖 | 全部提供商与三家协议，含订阅登录与官方身份头 | 只有基址可改的提供商（自定义 = OpenAI 兼容） |
| 代价 | CA 要进用户信任库；代理设置作用于该进程全部出站流量 | 无 CA、无全局影响，可以长期挂着 |
| 失真 | TLS 之上没有（主机、路径不变） | 依赖主机的头失真（`cc_version` 这类官方身份、订阅登录无法复用） |

### 透明 MITM

1. `pip install mitmproxy`（或 `uv tool install mitmproxy`）。首次运行 `mitmdump --listen-port 8080 -w artifacts/run/wire/flows.mitm` 会在 `~/.mitmproxy/` 生成 CA。
2. 把 CA 装进**当前用户**的受信任根：`certutil -addstore -user Root "%USERPROFILE%\.mitmproxy\mitmproxy-ca-cert.cer"`。收尾时 `certutil -store -user Root` 查到 `mitmproxy` 那条的序列号，`certutil -delstore -user Root <序列号>` 删掉。
3. 设置 → **网络与代理** → 代理选 `manual`，填 `http://127.0.0.1:8080`。只影响 Forge，系统的其它流量不动；用完改回 `system`。
4. 只抓模型流量：给 `mitmdump` 加位置过滤（URL 正则），如 `mitmdump --listen-port 8080 -w artifacts/run/wire/flows.mitm '~u api.anthropic.com'`；不匹配的请求不落盘。
5. 关联会话：提供商设置的请求头加一行 `x-forge-session: {session}`——`{session}` 每请求替换为会话 id（无会话的请求不发这条），flow 文件里就能按会话归档。

### 本地反向代理

1. `mitmdump --mode reverse:https://api.anthropic.com@9000 -w artifacts/run/wire/flows.mitm`。
2. 新建一个**自定义提供商**指向 `http://127.0.0.1:9000`；自定义只有 OpenAI 兼容协议（`ProviderPresets.Custom`），Anthropic 线路需手改 `settings.json` 里该条目的 `protocol: "anthropic"` 与 `baseUrl`——`FromDocument` 按存下来的值读，而编辑框对非自定义提供商不允许改基址（`Docs/03` §3.10）。
3. 不需要装 CA：这一跳是明文，代理自己与真主机建 TLS。

### 读回与边界

- 交互看：`mitmproxy -nr artifacts/run/wire/flows.mitm`。命令行快看：`mitmdump -nr <flows> --flow-detail 4`——**正文是美化后的，不是原始字节**，不能拿它比对字节。逐字节比对要读 flow 文件本身（`mitmproxy.io.FlowReader` 的 `request.raw_content`／`response.raw_content`）。
- flow 在响应结束后才落盘：卡住未结束的流要等它断开才出现在文件里，实时看只能看控制台。
- **走代理时目标由代理解析**：`network.dns` 对目标主机不再生效，`ConnectCallback` 的私网过滤也不作用于目标（`IsDirect` 为假就不解析不过滤），所以排查 DNS 与地址过滤问题时不要经过代理。
- 代理不做脱敏：`x-api-key`／`authorization` 在 flow 文件里是**明文**。flows 一律放 `artifacts/`（已忽略）并随手删，不要提交或外发。
- 提供商客户端不开自动解压（`NetworkHandlerOptions` 默认 `AutomaticDecompression = None`），抓到的正文就是服务端发的那一份，没有解压后再压缩的歧义。

### 实测（2026-11-23，本机 win-x64，mitmproxy 12.3.1 + Python 3.12，自建 SSE 端点）

- 请求体逐字节保真：390 B 的 Anthropic 形状请求体，上游收到的 SHA-256 与本地文件相同；flow 文件里的 `raw_content` 也相同（正向、反向各测一次）。
- SSE 不被整包缓冲：5 帧 × 0.5 s 的流，客户端 **2.52 s** 内逐帧收完（整包缓冲则首字节要到 ~2.5 s 才出现）；正向 **2.53 s**。
- 代理不注入请求头：客户端不发 `Accept-Encoding` 时上游也收不到（默认 `normalize_outbound_headers` 对它没有可见改动）；反向模式上游多看到 `Connection: close`，正向模式 `Proxy-Connection` 会漏到上游，两者对三家协议无影响。
- `stream_large_bodies` 默认空即不触发流式丢弃，所以正文默认全部留存；一旦设了这个阈值又不设 `store_streamed_bodies`，超过阈值的正文**不会被存**。
- 未验证：HTTPS 拦截全程（CA 未装进本机信任库）、真实三家提供商、.NET 客户端的 CONNECT 路径。

### Debug 构建的逐请求原文

Debug 构建把每个模型请求（每次 attempt，降级重发各算一次）写进**仓库根**的 `.log/`（从工作目录与可执行文件位置向上找最近的 `Forge.slnx`，所以从 `bin/` 启动也落在树根）：`<UTC 时间>-<序号>-<provider>-a<attempt>.http`，内容是请求行、最终请求头、原始 body，`---- response ----`、状态行、响应头与**边读边写**的原始响应字节；被拒的那次没有可读响应，改为写状态码与服务端返回的那句话。文件是适配器真正发出去的那份（`cache_control`、`beta` 头、`max_tokens` 夹取都在），所以回答“到底发了什么”不需要装证书，也不需要代理。

- 没有开关：Debug 跑起来就写，路径写死在 `<仓库根>/.log/`，`.gitignore` 已忽略；不设目录、不建配置项。只认入口程序集是 `Forge.App`——测试宿主与 `Forge.Bench` 同样发请求，但它们的流量不是打开 `.log/` 要看的东西。
- 只在 Debug 构建里存在：`WireDump` 整个类与五个调用点（`Start`、`Response`、三处 `Failed`）都在 `#if DEBUG` 内，Release 编译后 `WireDump` 这个类型、调用、局部变量与字符串全部消失（实测 Release 程序集里搜不到该类型，Debug 里有）——发布产物里没有任何能写提示词到磁盘的代码路径，也没有需要跳过的空调用。
- 一次模型请求一个文件；一个带工具调用的回合会有多份，按文件名排序即时间顺序。
- 写盘 best-effort：目录不可写、磁盘满、写到一半失败一律吞掉并停掉这一次抓取，绝不让调试记录影响回合；请求头与正文都是**原样**，连凭据也一样——它的用途就是看真实形状；`.log/` 不进版本库、随手删，别外发。

相关文档：[03-providers-and-network.md](03-providers-and-network.md)（网络与用量日志）、[12-storage-and-data.md](12-storage-and-data.md)（数据目录）、[14-limitations.md](14-limitations.md)（未接线项）。
