# Forge 开发约定(本仓库)

这些规则约束**开发 Forge 这个产品时的 Agent 分工**,不是 Forge 自身的运行时行为。

## 角色分工

| 角色 | 模型 | 负责 |
|---|---|---|
| 主 Agent(规划/调度) | `claude-bridge/claude-opus-5-5:medium`(中推理) | 读代码与 spec、拆解任务、写接口与骨架、派活、复核结果、合并、更新 spec、提交。能并行时才派活,不能并行时自己实现。 |
| 实现 Worker | `opencode-go/deepseek-v4.1-flash:xhigh`(超高推理) | 按任务写实现代码。只在自己副本里工作,测试通过后只拷回自己负责的路径。 |
| 测试 | `opencode-go/muse-spark-1.3-contributor:xhigh`(超高推理) | 只编写测试,不做 review。 |
| Review | `opencode-go/gpt-6-luna`;该模型不在 opencode-go 目录时回退 `opencode-go/glm-5.3-flash` | 只读评审,输出实质性问题与结论。 |

## 评审标准

评审不止于找代码 Bug,三项按优先级排序:
1. **实现是否符合架构约束**——对照 `architecture.md`/`goal-and-requirements.md`/模块 SPEC 检查边界、依赖方向、性能第一、Native AOT 兼容、扩展点契约等是否被遵守,偏差是否已记录或征得同意。
2. **测试是否足以证明实现正确**——测试是否覆盖关键路径、边界、失败/取消/并发、错误状态,而非只测 happy path;Skip 是否都在记录真实缺陷。
3. **代码质量**——正确性、资源、并发、性能陷阱。

第 1、2 项比第 3 项更重要。

## 派活规则

- **先问能不能并行,再决定派不派活**:只有当拆出的多个任务互不依赖、可以同时交给多个子 Agent 时,才用子 Agent 并行做;拆不出并行(单任务、有先后依赖、上下文紧密耦合)时,主 Agent 自己完成,不要为了“守分工”把单个任务外包给一个 Worker 再等它回来。
- 测试与评审必须使用彼此独立的会话。
- 实现与测试 Agent 都在自己的仓库副本里干活(`D:/FORGE/workN/<part>`),跑完与本次任务相关的测试且全绿后只把归属路径拷回主仓库;不碰其他路径、`*.csproj`、`Directory.Build.props`、`Forge.slnx`,不执行 git 命令。副本不存在须先创建,复制失败须报告阻塞,不得回退到共享主仓原地修改。创建副本用 `robocopy <主仓> <副本> /E /XD bin obj .git .vs /R:1 /W:1 /NFL /NDL /NJH /NJS`:`.vs` 里有 IDE 锁住的文件,不排除、不限重试时 robocopy 默认每 30 秒重试、最多 100 万次,会无限挂起。子 Agent 只在自己的副本内搜索,搜索排除 `bin`/`obj`;禁止在 `D:/FORGE` 根下递归搜索(那里有大量副本,曾导致单条 grep 跑了一小时)。NuGet 包里的类型搜源码搜不到,参考仓内现有用法。副本用完即可删除。
- 主 Agent 只写接口契约(`Forge.Abstractions`)、调度、集成与 spec;实现细节在能并行时交给 Worker,不能并行时自己写。
- 无论自己实现还是复核子 Agent 的产出,都要**构建**、**只跑与本次改动相关的测试**(见下节)、并检查是否越界改动,再决定是否采纳。
- **进程锁(`MSB3027`/`MSB3026`)的处理:让长驻进程换目录,而不是让每次构建/测试换目录。** 锁的来源只有两类,都不是代码问题:(a) 人在开着 `Forge.App`;(b) 上一次被中断的测试留下的 `testhost`/`ExtensionHost` 孤儿进程(也会让源码扫描类测试出现假红)。
  1. **默认不要给 `dotnet build`/`dotnet test` 加 `-p:BaseOutputPath=...`**,测试用默认 `bin/`,不新建逐任务的 `artifacts/<name>/`(不会被清理,会堆积)。万一必须重定向,输出放哪都行:源码扫描类测试的仓库根在构建时由 `Forge.App.Tests.csproj` 写进程序集元数据(`ForgeRepoRoot`,由 `tests/Forge.App.Tests/RepoRoot.cs` 读取),不再依赖输出目录位置。
  2. **开 App 时设 `FORGE_RUN=1`**:`FORGE_RUN=1 dotnet run --project src/Forge.App`(PowerShell:`$env:FORGE_RUN=1`)。`Directory.Build.props` 会把 `Forge.App` 的输出固定到 `artifacts/run/`,整个仓库只有这一个目录,反复覆盖;`artifacts/` 已在 `.gitignore`,删除即清理。`src/Forge.App/bin/` 因此不会被运行中的 App 锁住。**跑测试的 shell 里不要设 `FORGE_RUN`**(否则测试项目会引用到 `artifacts/run/` 的 Forge.App)。
  3. **遇到锁先排查再动手**:用 `tasklist`/`Get-Process Forge.App,testhost,ExtensionHost` 找占用者。是孤儿 `testhost`/`ExtensionHost` 就结束它们;是用户正在用的 `Forge.App` 就报告阻塞并请用户关闭或改用 `artifacts/run/` 启动,不要自行绕开。
- 派活文本里写明该任务适用的 `.github/skills/...` 路径。Worker 的上下文是干净的,不会自己去找 skill,不带路径等于没这份约束。

## 测试分层与执行

判定依据、落点映射与新增/删除测试的规则见 `testing.md`(id `forge-testing`)。本节只写操作口径。

- **L1(每次任务)**:先 `dotnet build Forge.slnx -c Debug` 一次,之后所有 `dotnet test` 都带 `--no-build`;只跑改动落点对应的命名空间,例如 `dotnet test tests/Forge.App.Tests --no-build --filter "FullyQualifiedName~Forge.App.Tests.Workbench"`。改了任何 `.cs`/`.axaml` 源文件时追加源码扫描门槛:`--filter "FullyQualifiedName~Localization|FullyQualifiedName~MinorRegressionTests|FullyQualifiedName~KeyRegistry|FullyQualifiedName~StringTable|FullyQualifiedName~NoHardcodedChinese"`。只改文档/spec 时无需跑测试。
- **L2(模块完成、接口变化、较大重构)**:跑承载改动的那个测试项目全量,例如 `dotnet test tests/Forge.Core.Tests --no-build`。
- **L3(合并前、发布、跨模块、公共协议变化)**:13 个测试项目全量。触发条件按 `testing.md`;`Forge.Integration.Tests` 与 `Forge.App.LiveSwitch.Tests` 只在 L3 跑。
- 不跑与本次改动无关的测试,也不为“保险”跑全量;全量混着与本次改动无关的偶发失败,判不出真伪。
- 报告里写清:实际执行的命令、用例数、耗时、结果;没跑的部分说明原因与未验证范围。
- 新增或删除测试前先读 `testing.md` 的准入与清理规则;删除测试必须在报告里逐条说明理由。
- 源码扫描类门槛(`StringTableTests`/`NoHardcodedChineseTests`/`KeyRegistryTests`/`MinorRegressionTests`)报“源码里明明已注册/已存在”的错误时,先 `dotnet build`(必要时加 `-t:Rebuild`)再跑一次:复用旧 `bin` 的 `--no-build` 会报假红。

## 开发 Skills

- 筛选后的第三方技能文件在 `.github/skills/`,只约束**开发实践**,不参与构建、不随产品发布。适用清单、裁剪内容、上游提交与再同步方式见 `.github/skills/README.md`。
- 优先级:spec(`architecture.md`、`goal-and-requirements.md`)>`AGENTS.md`> `.github/skills/`。skill 里的建议不得据以修改 spec,也不构成扩张 NuGet 白名单的理由。
- 写实现、写测试、排查构建或性能问题前,先读 `.github/skills/<skill-name>/SKILL.md`;涉及性能或分配时同时读 `.github/skills/blazing-fast-dotnet/SKILL.md`。目录是一层的,名字即目录名。
- Avalonia 的 skill 在 `.github/skills/avalonia-*`(34 份,包括路由器 `avalonia` 与 `avalonia-pro-max-*` 子能力)。`Forge.App` 落地前先把 `Avalonia` 版本钉进 `Directory.Packages.props` 或 csproj——该套 skill 的第一条规则是读项目版本,没版本它只会按 12.1.x 猜。

## NuGet 白名单

只用 `architecture.md`「NuGet 引用规则」表里的三方库,按表中范围与约束使用;新增任何包须先问用户。

## 并行与串行

后续模块里没有相互依赖的部分并行派给多个 Worker(各自副本、各自归属路径);有依赖的串行,串行部分由主 Agent 自己写。接口契约先由主 Agent 定稿,再并行实现。

判断标准很简单:能同时开出两个以上互不阻塞的任务 → 派子 Agent 并行;否则 → 自己写。不要为了并行而硬拆任务。

## 重复代码与抽象(Deslop)

本机通过 MCP 服务 `deslop`(工具名 `mcp__deslop__*`)接入 Deslop Live:它把编辑器里的实时重复代码分析暴露成工具。**预防优于治疗**——不写重复比事后抽取便宜。服务配在用户级 `~/.pi/agent/mcp.json`,指向 VS Code 扩展自带的 `deslop-mcp.exe`(路径含扩展版本号,升级扩展后要改),`--root` 绑 `D:/FORGE/forge`。

> **适用范围:只约束读 `AGENTS.md` 的 Agent(pi、Codex 系)。** 对 **Claude Code 无效**——它只读 `CLAUDE.md`;对 **Copilot 无效**——它读 `.github/copilot-instructions.md`。这两个客户端不会看到本节,默认也不会调 `find-similar`。要让它们同样遵守,必须把本节内容原样粘进各自的入口文件(`CLAUDE.md` / `.github/copilot-instructions.md`),否则本节写成什么样都不影响它们。

### 定律:写新代码前先调 `find-similar`

在写任何超过几行的新函数/方法/类/helper/测试夹具/解析分支/错误类型/路由处理器/视图模型之前,先用拟定代码(`snippet` + `language`)或草稿的字节区间(`path`、`start_byte`、`end_byte`)调 `find-similar`,并读回结果:

- 命中 `identical` 或 `nearly_identical` → 不要写新副本,复用返回的规范实现;需要时抽公共函数。
- `clusters` 为空但 `existing` 有位置 → 这段代码已经存在一份,复用它或抽取它。
- `loosely_similar` / `same_behavior` → 读规范实现再判断是否真的不同,倾向复用。
- `structural_only` 只表示形状相同,不是克隆,读完再决定。
- 两者都空 → 可以写。

### 各工具的用法

- 合并已有重复 → `duplicates`(`detail:"summary"` 小页起,用 `path`/`kinds` 收窄,按 `next_offset` 翻页)→ `cluster-by-id` 取全部成员 → 可选 `merge-plan`(只读,`ai_or_human` 判定不可执行)。
- 想弄清两处为何被判成克隆 → `compare-pair`;字段含义 → 每个会话调一次 `schema-doc`,不要每次都带。
- 改完复检 → 同一 `path` 再查 `duplicates`,或用 `rescan` 带 `paths`;编辑后旧的字节偏移失效,须重新分析再复用。

### 硬规则

- 不许因为“这看起来是新的”跳过 `find-similar`——绝大多数克隆正是由真的以为它是新代码的人引入的。
- 不许写完再调一次“确认”,那是治疗不是预防。
- 不许为了让告警消失而放宽阈值、加 `exclude`/`report_hide`、调 `--min-nodes`、改 `[total] include`。判定为故意重复就保留并写明原因。
- 移动重复代码时选**所有调用者都能依赖的最通用模块**,不是先打开的那个文件;移完改所有调用者,不留旧副本、不加第三份,并跑本项目相关测试。
- `mass`/`rank` 是影响面,不是置信度也不是合并安全度;`kind` 也不证明合并保行为,必须逐个读源码。
- 工具报错或空结果**不等于没有重复**:先读 `.deslop/cache/live-report.json`,再跑扩展自带 CLI(见下)生成报告;两者都不行就报告阻塞,不许猜。
- 不要为了重复率百分比好看去弱化断言、缩短消息或重排代码——它只是“复制行/分析行”。

前置条件与降级:`deslop-mcp` 自己没有引擎,每次调用都转发给 VS Code 里为该仓库运行的 `deslop-lsp`。**VS Code 没开这个仓库时报 `LspNotRunning`**,此时用扩展自带 CLI:`"C:/Users/zfcfl/.vscode/extensions/nimblesite.deslop-live-0.42.0-win32-x64/bin/win32-x64/deslop.exe" . --notext --nohtml --no-color`,然后读 `.deslop/deslop-report.json`(缓存默认开启,不要加 `--no-incremental`)。工具是 `deferred` 暴露的,调用前先 `tool_search`;子 Agent 不能调用 MCP 工具,需要重复检查时由主 Agent 查完把结论写进派活文本。完整规则见 https://deslop.live/docs/for-ai/。

## 换行约定

- 换行由 `.gitattributes`(`* text=auto eol=lf`)统一为 LF:仓库与工作树都是 LF,三平台检出一致。不要再用 `core.autocrlf=true` 让 Windows 检出变 CRLF——这正是此前 CI 换行失败与测试侧逐处归一化(`PromptSourceText`、`SpecFrontmatterTests` 的 `ReplaceLineEndings`)的根源,现已移除。`.gitattributes` 的 `eol=lf` 优先于 `core.autocrlf`,所以本机 system 级 `core.autocrlf=true` 不再影响本仓库。
- 若本地工作树仍是旧 CRLF 检出,一次性重归一化:`git add --renormalize .` 后 `git checkout -- .`(或 `git rm -r --cached . && git reset --hard`)。新克隆无需任何设置。
