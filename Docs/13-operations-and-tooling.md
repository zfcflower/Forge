# 13 运维、日志、更新与工程工具链

本文件说明进程级行为（单实例、启动、日志、崩溃）以及自动更新、CI 与基准工具。

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
- 安装并重启：解包到该目录的 `app/`，再从 `<数据根>/updates/.applier-<guid>` 副本启动新的可执行当应用器——**App 自己从不 rename 或替换安装目录**。应用器等旧实例退出（60 s）→ 安装目录改名 `<install>.old-<version>`、新树就位 → 以 `--update-applied <version> --install <dir>` 启动新版本 → 主窗口起来后写 `ok-<version>` 确认（60 s，且 `--install` 必须等于自己所在目录）→ 超时或提前退出则杀掉、未确认的树标 `<install>.failed-<version>`、还原备份并重启旧版本；每步连同回滚原因写 `<数据根>/updates/install.log`。成功也保留 `.old-<version>`（确认来自一个可能还会崩的进程），由下次启动内务删掉，同时把崩溃遗留的 `.restoring-<version>-<guid>` 孤儿改回 `.old` 再走一遍回滚。
- **发布管线未接**：只有脚本本身，没有 tag 触发的 workflow 去按 RID 打包、算哈希、把 `updates.json` 推上滚动 tag；默认地址下没有可解析资产，更新只在手工发版、手工跑脚本并手工上传时生效（见 14）。

## 13.5 CI 与构建矩阵

- `.github/workflows/ci.yml` 的 `test` 作业：三平台（ubuntu / windows / macos）× `dotnet build Forge.slnx -v m` + `dotnet test Forge.slnx --no-build`（`TreatWarningsAsErrors` 打开，单个警告即失败）。**当前仓库没有测试项目**（`tests/` 与 Debug-only 调试接缝已移除），所以 `dotnet test` 没有测试可跑，矩阵实际只验证三平台构建；POSIX 进程归属、PTY、shell 路径与 macOS 分支在三平台测试接线前不产生运行时覆盖（`architecture.md` 已记录这一点）。
- `publish-aot` 作业：Windows 上 `dotnet publish src/Forge.App -c Release`，断言产物存在且体积 >10 MB（AOT 分析器警告即错误）。
- **三平台 AOT `publish` 矩阵未接线**（macOS/Linux publish 从未在任何地方跑过），保持为显式后续项而不是永久红。
- 测试纪律（恢复测试项目时适用）：只保护重要行为；不测自动属性、简单 DTO 映射、纯转发层与第三方框架自身行为；异步测试用 `async Task`；不削弱断言迁就实现。当前代码**不提供**测试注入接缝：没有 `InternalsVisibleTo`、没有 `FORGE_HOST_TEST_*` 环境变量，`ApprovalStoreOptions` 只有 `BusyTimeout` 与 `ImportLegacyJson` 两个产品开关，Debug 与 Release 行为一致。

## 13.6 基准

- `tools/Forge.Bench`：无框架、无包，同一循环内交替测量基线与目标、取配对中位差（比值稳定，绝对值噪声可达 1.5–2×）。覆盖 read / write / copy / walk / grep / process / output / concurrency / ratelimit 场景，每个场景自带验收检查；全量 18/18 通过为一次验收。
- 典型实测（win-x64 32 逻辑核、Release）：
  - `read` 100 MiB 全读 59.8 ms（1.5–1.6 GB/s）；100 MiB 取 100 行 0.071 ms。
  - `write` 1 KiB 原子写 2.67 ms（差距全在 `fsync`）；1 MiB 原子写 4.34 ms（比 BCL 直写快 21%）。
  - `FileWalker` 1k/10k/100k：0.79 / 9.19 / 56.9 ms。
  - 托管 `grep` 10k 文件 114 ms（命中）vs ripgrep 103–105 ms（慢 11–15%，命中数相同）。
  - `ProcessRunner` 100 MiB 子进程输出 69.8 ms（1.40 GB/s）、分配 1.39 MB、工作集增量 <6 MiB。
  - 限速器：0 字节/次、无争用 p50 <100 ns、8 线程共享一个桶时每次都授权。
- 运行约定：`Forge.App` 必须通过 `FORGE_RUN=1` 启动，运行输出统一到 `artifacts/run/`；运行测试的 shell 禁止设置 `FORGE_RUN`；遇到 `MSB3027`/`MSB3026` 先检查 `Forge.App`、`testhost`、`ExtensionHost` 是否占用文件，确认孤儿进程后再清理，用户正在运行的进程报告阻塞而不是擅自结束；不通过切换输出目录掩盖文件锁。

## 13.7 发布形态

- 主程序 Native AOT、不可变；发布体积（win-x64，.NET 11 / Avalonia 12.1.6，`dotnet publish src/Forge.App -c Release`，`PublishAot` 隐含 `PublishTrimmed=true`，`OptimizationPreference` 默认 Speed）：
  - **2026-11-16 同机同配置重测**：`Forge.App.exe` **72.92 MiB**；`OptimizationPreference=Size` 62.27 MiB；`StackTraceSupport=false` 69.83 MiB；发布目录去掉 `.pdb` 后 93.31 MiB（含原生库 libSkiaSharp 11.09、av_libglesv2 5.14、e_sqlite3 1.88、libHarfBuzzSharp 1.73、libonigwrap 0.52 MiB），`.pdb` 另占 350.31 MiB、不随产品分发。
  - 早先同机记录：最小 Avalonia 窗口 16.5 MiB、加 AvaloniaEdit+TextMate 28.7 MiB、完整 App 49.7 MiB（约 +12 MiB 编辑器栈、+21 MiB 产品代码与其余模块，嵌入资源 8.6 MiB）；`Size` 42.8 MiB、关托管堆栈追踪 40.8 MiB。两次记录之间的差值未逐项定位，不得据此宣称体积回归或优化。
  - 两者都不建议默认开 `Size` 或关闭堆栈追踪（前者牺牲速度，后者丢掉插件与 Agent 链路最需要的托管堆栈）。
- **扩展侧要求用户已安装 .NET 11 运行时**（`net11.0`；`global.json` 固定 SDK `11.0.100`）：在 JIT 侧跑的是三个进程——常驻的 `Forge.ExtensionHost`（每应用 1 个）、`Forge.PluginWorker`（每插件 1 个）、`Forge.ScriptExecutor`（每次 `code.execute` 1 个，一次性）；主程序不需要运行时。Host 的 `--dotnet` 指定拉起 `.dll` 形态 Worker/执行器的运行时（默认是跑 Host 的那个），`Forge.HostClient` 的 `RuntimePath` 置空时直接执行 apphost 可执行文件——所以“随包私有运行时”（`<install>/runtime/dotnet.exe`）不需要改代码，只需把这两处指过去。
- **AOT 发布产物不含这三个 JIT 组件**（实测 `dotnet publish src/Forge.App -c Release` 的输出只有 `Forge.App.exe`、原生库、`.pdb` 与 `Assets/`；`Forge.App.csproj` 不引用它们的项目）。`JitHostLayout` 只按 `FORGE_JIT_DIR` 与仓内构建布局（`src/<asm>/bin/<cfg>/net11.0`、`artifacts/publish/<asm>/<cfg>`、`publish/<asm>/<cfg>`）定位它们，仓库里也没有发布它们的脚本或 CI 步骤——把 publish 目录原样发给用户时宿主起不来（`diag.jitHostMissing`），终端、插件、`run_code` 全部不可用。
- 实测对照（本机 win-x64、同一提交、剔除 `.pdb`）：Native AOT 主程序目录 **94 MiB**、启动到窗口 **171 ms**；同一提交 `-p:PublishAot=false --self-contained` 的 JIT 版 **141 MiB**、**874 ms**（3 次中位）；框架依赖 JIT **58 MiB**。私有运行时单份（`Microsoft.NETCore.App` 共享框架）约 **75 MiB**，上面 105 MiB 的 JIT 目录 gzip 后 **44 MiB**。因此“反正要带运行时”时 AOT 不再省体积（94 + 75 > 141，本体反而大 ~28 MiB），它的收益是启动、内存、不可变边界与主程序不随用户机器 .NET 版本漂移。
- 原生库不能静态链接，“单文件”不成立。

相关文档：[03-providers-and-network.md](03-providers-and-network.md)（网络与用量日志）、[12-storage-and-data.md](12-storage-and-data.md)（数据目录）、[14-limitations.md](14-limitations.md)（未接线项）。
