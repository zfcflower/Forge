# Forge 开发约定

本文件约束开发 Forge 产品时的 Agent 行为，不约束 Forge 自身的运行时行为。本文件是本仓库 Agent 开发规范的唯一事实源（根目录 `CLAUDE.md` 经 `@AGENTS.md` 导入，勿在任何入口粘贴全文副本）。

## 1. 规则优先级与评审标准

### 1.1 规则优先级

1. `architecture.md`、`goal-and-requirements.md`、模块 SPEC。
2. 本文件 `AGENTS.md`。
3. `.github/skills/`。

架构和需求以 SPEC 为准；实现偏差必须记录或征得用户同意。skill 里的建议不得据以修改 SPEC，也不构成扩张 NuGet 白名单的理由。

### 1.2 评审标准

评审按以下优先级执行：

1. **架构合规**：模块边界、依赖方向、性能优先、Native AOT 兼容、扩展点契约及 SPEC 一致性。
2. **测试充分性**：关键路径、边界、失败、取消、并发及错误状态，而非只测 happy path；Skip 须记录真实缺陷。测试范围与执行细则见 §4。
3. **代码质量**：正确性、资源生命周期、并发安全及性能陷阱。

前两项优先于一般代码质量。不得以代码评审通过代替测试义务。

### 1.3 规格维护

规格是事实依据。索引与更新机制遵循 spec-graph skill（系统已加载，不在此重复）：用 `spec_search`/`spec_grep`/`spec_get` 定位治理当前工作的规格并对齐；改动变更边界/契约/不变量/决策时，在同一改动中更新所属 SPEC（`spec_update`/`spec_create`/`spec_validate`）。

本仓库附加约束：**实现缺陷保留规格并记代码缺口，不得改规格掩盖问题；业务变更须用户确认。**

## 2. Agent 分工与仓库隔离

### 2.1 并行原则

先判断任务能否拆成至少两个互不依赖、可同时执行的子任务：

- 可以并行：派给多个 Worker。
- 存在先后依赖、上下文紧密耦合或只有一个任务：主 Agent 自己完成。
- 不得为了遵守分工而将单一任务外包给一个 Worker。

接口契约由主 Agent 先行定义，再并行实现。

主 Agent 负责：`Forge.Abstractions` 接口契约、任务调度与模块集成、SPEC 修改及架构决策、最终构建/测试/改动检查/结果验收。实现细节只有在存在真正可并行任务时才交给 Worker。

写新代码前先查仓内有无同类实现，优先复用，不写重复副本。

测试和评审必须使用彼此独立的会话，且测试、评审所用模型均不得与 Worker 模型相同。

### 2.2 Worker 仓库副本

每个实现或测试 Worker 必须在独立仓库副本中工作：`D:/FORGE/workN/<part>`。

强制规则：

- 副本不存在时必须先创建；创建或复制失败必须报告阻塞，不得回退到主仓库原地修改。
- Worker 只能修改分配给自己的归属路径；禁止修改其他路径、`*.csproj`、`Directory.Build.props`、`Forge.slnx`。
- 禁止执行任何 Git 命令。
- 完成相关测试且全部通过后，只将归属路径复制回主仓库。
- 副本使用完毕即可删除。

创建副本使用：

```powershell
robocopy <主仓> <副本> /E /XD bin obj .git .vs artifacts /R:1 /W:1 /NFL /NDL /NJH /NJS
```

或等价封装 `scripts/new-copy.ps1 <副本路径>`。

**必须排除 `.vs`**：其中可能有 IDE 锁定文件；不排除且不限制重试时，robocopy 默认每 30 秒重试、最多 100 万次，可能无限挂起。

搜索规则：Worker 只在自己副本内搜索，排除 `bin`/`obj`；禁止在 `D:/FORGE` 根目录递归搜索（含大量副本）；NuGet 包中的类型可能搜不到源码，参考仓库现有用法。

### 2.3 派活要求

每项派活必须明确：

- 任务目标及验收条件。
- 允许修改的归属路径。
- 必须遵守的架构和接口约束。
- 该任务适用的 `.github/skills/...` 路径（Worker 上下文干净，不会自己找 skill，不带路径等于没那份约束）。
- 预期测试项目、命名空间及过滤器（副本测试执行见 §4.3）。
- 完工报告要求（含 `forge-test.ps1` 收据，见 §5.5）。

主 Agent 必须将测试要求、收据要求及验收标准写入派活内容，不得假设 Worker 知道主仓库的测试规则。

## 3. 构建与进程锁

处理 `MSB3027`/`MSB3026` 时，优先调整长驻进程的输出目录，不要让每次构建和测试使用不同输出目录。锁的来源主要有两类，都不应直接判定为代码问题：① 用户正在运行 `Forge.App`；② 上一次中断的测试遗留 `testhost`/`ExtensionHost` 孤儿进程。

### 3.1 默认构建输出

- `dotnet build`/`dotnet test` 默认使用 `bin/`；不得默认添加 `-p:BaseOutputPath=...`；不得创建逐任务的 `artifacts/<name>/` 输出目录（无法自动清理）。
- 确实必须重定向输出时，源码扫描测试仍通过程序集元数据 `ForgeRepoRoot` 定位仓库根，不依赖输出目录位置（由 `Forge.App.Tests.csproj` 构建时写入，读取在 `tests/Forge.App.Tests/RepoRoot.cs`）。

### 3.2 Forge.App 运行目录

启动 App 必须设置 `FORGE_RUN=1`：

```powershell
$env:FORGE_RUN=1
dotnet run --project src/Forge.App
```

`Directory.Build.props` 将 `Forge.App` 输出固定到 `artifacts/run/`（整个仓库唯一运行输出目录，可反复覆盖，已被 `.gitignore` 忽略），避免运行中的 App 锁住 `src/Forge.App/bin/`。

**运行测试的 shell 禁止设置 `FORGE_RUN`**，否则测试项目可能引用 `artifacts/run/` 中的 Forge.App。

### 3.3 进程锁排查

构建入口已通过 `Directory.Build.props` 的 `BeforeTargets=Build` 集成 `scripts/build-hygiene.ps1`（仅本地 Windows、CI 跳过）：自动结束遗留 `testhost`/`ExtensionHost` 孤儿，日志写 `artifacts/hygiene/`，活锁报 PID 与处置指引。

遇到锁先排查：`tasklist` / `Get-Process Forge.App,testhost,ExtensionHost`。

- 确认孤儿进程：结束它。
- 确认用户正在使用的 `Forge.App`：报告阻塞，请用户关闭或改用 `artifacts/run/` 启动。
- 不得擅自绕过活锁或靠改每次构建输出目录掩盖问题。

## 4. 测试策略与执行

### 4.1 核心原则

**只运行与本次改动相关的测试，不得为保险运行全量测试。** 全量混入与本次无关的偶发失败，无法准确判断回归。测试范围必须与改动落点匹配，以 `scripts/forge-test.ps1` 收据为判据；无 `--why` 的全量视为未完成测试义务。

历史基线、夹具明细及测试债务见 `docs/analysis/testing-notes.md`。

### 4.2 唯一测试入口

统一使用 `scripts/forge-test.ps1`，收据落 `artifacts/testruns/`。

### 4.3 测试等级

| 等级 | 适用场景 | 执行要求 |
|---|---|---|
| L1 | 每次代码任务 | `forge-test.ps1 -Auto` |
| L2 | 模块完成、接口变化、较大重构 | 承载改动的测试项目全量 |
| L3 | 合并、发布、跨模块架构、公共协议、核心基础设施变化 | 13 个测试项目全量，必须 `--why` |

**L1**：`scripts/forge-test.ps1 -Auto`——按 Git 改动推导落点过滤器（映射 `scripts/test-map.json`），改 `.cs`/`.axaml` 自动追加源码扫描门槛，失败先重建一次再判红。

**副本没有 `.git`，必须用显式过滤器**：

```powershell
scripts/forge-test.ps1 -Level L1 -Project <测试项目> -Ns <命名空间> [-Gates]
```

**L2**：`scripts/forge-test.ps1 -Level L2 -Project <测试项目>`——承载改动的测试项目全量，不跑无关项目。

**L3**：`scripts/forge-test.ps1 -Level L3 --why <原因>`——`--why` 必须是 `merge`/`release`/`interface`/`infra`/`cross-module` 之一，缺少直接拒绝执行；`-Auto` 命中 L3 落点自动升级代填。`Forge.Integration.Tests` 和 `Forge.App.LiveSwitch.Tests` 只在 L3 执行。

### 4.4 测试落点映射

权威映射 `scripts/test-map.json`，最长前缀匹配，改规则须同步该文件。映射原则：

- App 按子目录→对应测试命名空间：`Workbench`/`Resources`→Workbench；`Views`→Views|Workbench；`Providers`/`Git`/`Terminal`/`Settings`/`Changes`/`Editing`/`ViewModels`→各自命名空间；`Localization`→源码扫描门槛。
- Core/Workspace/IO/Editor/Mcp/Approval/Workflows/Scripting/HostClient→对应测试项目（L2）。
- Abstractions/Plugin.Sdk/Ipc 相关/存储 Schema/根级构建及 CI 配置→L3。
- csproj 的 AOT/发布属性→L3，必须 `--why infra`。
- 文档/SPEC/Markdown→不跑测试。
- 涉及多个落点用 `|` 组合过滤器，不得降级为 L2。

### 4.5 手工等价执行

仅在测试脚本不可用时允许。先 `dotnet build Forge.slnx -c Debug` 一次，再 `dotnet test --no-build --filter "FullyQualifiedName~Forge.App.Tests.Workbench"` 跑落点命名空间；改 `.cs`/`.axaml` 追加源码扫描门槛过滤器。手工执行不能替代唯一测试入口及收据要求。

## 5. 测试准入、删除与报告

### 5.1 新增测试

1. 优先修改现有测试文件。
2. 同一行为一个测试文件、一个主测试；多组输入用 `[Theory]`/`[InlineData]`，不写 `Foo_case1`/`Foo_case2`。
3. 不测框架自身、生成代码或简单属性转发；不为测私有实现放宽可见性。
4. 不为提高覆盖率数字新增测试（覆盖率升降都不是增删理由）。
5. 修 Bug 必配复现回归测试，测试名说明保护的行为。
6. 每个新增测试必须能回答：保护哪条可能被误改的契约？答不上不新增。

### 5.2 测试删除

删除前确认：是否已有别处覆盖、是否守边界或历史回归、是否依赖外部环境、对应功能是否仍存在。

允许删除：完全重复/功能已删/无实际断言/只测第三方库自身/被更完整测试替代。

禁止删除：核心业务规则/权限安全数据一致性/历史 Bug 回归/关键协议兼容/难以其他方式发现的边界。

暂不能修的不稳定测试：记录原因并隔离（跳过+跟踪点），不直接删也不永久 Skip。删除测试必须在完工报告逐条说明理由。

### 5.3 测试夹具

同一项目新增测试优先复用现有替身和夹具，不允许私有同名副本。夹具登记见 `docs/analysis/testing-notes.md`。

注意 C# 默认接口方法（DIM）陷阱：替身基类声明同名 DIM 公共方法不等于重新实现接口；需要重实现时必须在类上重新声明接口。

### 5.4 源码扫描测试假红

`StringTableTests`/`NoHardcodedChineseTests`/`KeyRegistryTests`/`MinorRegressionTests` 可能因复用旧构建产物假红。若报「源码里明明已注册/已存在」的内容缺失，先 `dotnet build`（必要时 `dotnet build -t:Rebuild`）再重跑。不得据复用旧 `bin` 的 `--no-build` 结果判定源码扫描失败。

### 5.5 完工报告

报告必须包含：① 实际执行的构建和测试命令；② 用例数、耗时及结果；③ `forge-test.ps1` 测试收据；④ 本次全部改动文件（与 `git diff --name-only` 一致）；⑤ 未执行的测试及原因；⑥ 尚未验证的范围；⑦ 删除测试的逐条理由。

不得只报告部分改动路径；未列出全部文件视为报告未完成。无收据的全量视为未完成测试义务。

## 6. NuGet 白名单

- 官方 .NET 库（`dotnet` 组织发布的 `Microsoft.*`/`System.*`）自动允许，无需逐个询问；仍须固定版本并满足 Native AOT。
- 第三方库仅允许 `architecture.md`「NuGet 引用规则」规定的库，遵守对应范围和约束。
- 新增任何第三方 NuGet 包必须事先征得用户同意。

## 7. 性能基准

统一使用仓内 `tools/Forge.Bench`，不得另建 BenchmarkDotNet 项目。执行：

```powershell
dotnet run -c Release --project tools/Forge.Bench -- [filter] [--quick]
```

无额外 NuGet 依赖，支持场景注册、预热定次计时、延迟分位/吞吐、分配/GC/峰值内存、与 BCL 基线语义签名对账、性能验收。新增热路径基准注册到现有 `*Scenarios.cs`，必要时加 `ProjectReference`。

热路径改动必须提供改前改后的耗时和分配数据；没有实测数据不得宣称性能提升。基准不属于 L1/L2 测试义务。指标口径与场景设计参考 https://github.com/aspnet/Benchmarks。

## 8. 换行约定

`.gitattributes` 的 `* text=auto eol=lf` 是统一换行规范：仓库和工作树统一 LF；不得用 `core.autocrlf=true` 把 Windows 工作树转 CRLF；`eol=lf` 优先于 `core.autocrlf`，本机 system 级 `core.autocrlf=true` 不影响本仓库；新克隆无需额外配置。

旧 CRLF 工作树一次性重归一化：

```powershell
git add --renormalize .
git checkout -- .
```

或：

```powershell
git rm -r --cached .
git reset --hard
```

**执行前必须检查工作区状态，确认不会丢失用户修改；第二种会重置工作树，不得在存在未保护修改时执行。**

## 9. 开发 Skills

- 筛选后的第三方技能在 `.github/skills/`，只约束开发实践，不参与构建、不随产品发布；适用清单见 `.github/skills/README.md`。
- 写实现、写测试、排查构建或性能问题前，先读 `.github/skills/<skill-name>/SKILL.md`；涉及性能或分配时同时读 `.github/skills/blazing-fast-dotnet/SKILL.md`。目录一层，名字即目录名。
- Avalonia skill 在 `.github/skills/avalonia-*`（34 份，含路由器 `avalonia` 与 `avalonia-pro-max-*` 子能力）。`Forge.App` 落地前先把 Avalonia 版本钉进 `Directory.Packages.props` 或 csproj——该套 skill 第一条规则是读项目版本，没版本它只按 12.1.x 猜。

## 10. 最终验收

每项任务完成前，主 Agent 必须确认（无论实现由谁完成，不得仅凭 Worker 声称完成就接受）：

- [ ] 实现符合架构、需求及 SPEC。
- [ ] 没有未经同意的架构偏差或第三方 NuGet 依赖。
- [ ] 修改范围符合分工，没有越界改动。
- [ ] 构建成功。
- [ ] 与改动相关的测试已按正确等级执行。
- [ ] 测试结果、收据和完整文件清单已提供。
- [ ] 测试删除均有明确理由。
- [ ] 热路径改动提供前后性能数据。
- [ ] 未验证范围、阻塞和遗留问题已明确报告。

未满足的项目必须明确说明原因和影响，不得将未完成事项报告为已完成。