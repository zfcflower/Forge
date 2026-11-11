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
- 派活时模型 id 必须带 provider 前缀且原样照抄(子 Agent 的 `model` 只做严格 `provider/id` 匹配,不认模糊匹配和 family 简写):claude-bridge 注册的是连字符 id(`claude-sonnet-5-5`),点号形式 `anthropic/claude-sonnet-5.5` 是 OpenRouter 目录的写法,走桥会直接报 “not available in this session”。
- Claude 模型的 thinking 档位只有 `low/medium/high/xhigh/max`(`off`、`minimal` 会被向上夹到 `low`),不要写这两个。
- 每个 Worker 任务必须显式带上第二列“实现 Worker”所列模型;测试任务使用第二列“测试”所列模型;仅在额度受限且经用户同意时才可临时改用其它模型,恢复后回到表中模型;测试与评审必须使用彼此独立的会话,评审任务必须带第二列“Review”所列模型。不要用主 Agent 的模型派子 Agent。
- 实现与测试 Agent 都在自己的仓库副本里干活(`D:/FORGE/workN/<part>`),跑完与本次任务相关的测试且全绿后只把归属路径拷回主仓库;不碰其他路径、`*.csproj`、`Directory.Build.props`、`Forge.slnx`,不执行 git 命令。副本不存在须先创建,复制失败须报告阻塞,不得回退到共享主仓原地修改。创建副本用 `robocopy <主仓> <副本> /E /XD bin obj .git .vs /R:1 /W:1 /NFL /NDL /NJH /NJS`:`.vs` 里有 IDE 锁住的文件,不排除、不限重试时 robocopy 默认每 30 秒重试、最多 100 万次,会无限挂起。子 Agent 只在自己的副本内搜索,搜索排除 `bin`/`obj`;禁止在 `D:/FORGE` 根下递归搜索(那里有大量副本,曾导致单条 grep 跑了一小时)。NuGet 包里的类型搜源码搜不到,参考仓内现有用法。副本用完即可删除。
- 主 Agent 只写接口契约(`Forge.Abstractions`)、调度、集成与 spec;实现细节在能并行时交给 Worker,不能并行时自己写。
- 无论自己实现还是复核子 Agent 的产出,都要**构建**、**只跑与本次改动相关的测试**、不跑全量 `dotnet test`,并检查是否越界改动,再决定是否采纳。全量约 5 分钟,且混着与本次改动无关的偶发失败,判不出真伪;相关范围按改动落点定——改了 `src/Forge.App/Workbench/`、`Resources/` 或视图就带 `Forge.App.Tests.Workbench`;改了 `.cs`/`.axaml` 源文件就再加 `Localization` 与 `MinorRegressionTests`(它们扫源码,是硬门槛);只改文档/spec 时无需跑测试。
- 本地跑测试而 `Forge.App` 正开着时,`src/Forge.App/bin/Debug/net11.0/` 被进程锁住,构建以 `MSB3027`/`MSB3026` 失败(不是代码问题)。用 `dotnet test <proj> -p:BaseOutputPath=artifacts/<name>/` 重定向输出。**输出目录必须留在仓库内**:`KeyRegistryTests`、`NoHardcodedChineseTests`、`StringTableTests`、`MinorRegressionTests` 从测试程序集往上找 `Forge.slnx` 读源码,放到仓库外(如 `D:/tmp/...`)时它们会以“找不到仓库根”失败,看起来像真失败。
- 派活文本里写明该任务适用的 `.github/skills/...` 路径。Worker 的上下文是干净的,不会自己去找 skill,不带路径等于没这份约束。

## 开发 Skills

- 筛选后的第三方技能文件在 `.github/skills/`,只约束**开发实践**,不参与构建、不随产品发布。适用清单、裁剪内容、上游提交与再同步方式见 `.github/skills/README.md`。
- 优先级:spec(`architecture.md`、`goal-and-requirements.md`)>`AGENTS.md`> `.github/skills/`。skill 里的建议不得据以修改 spec,也不构成扩张 NuGet 白名单的理由。
- 写实现、写测试、排查构建或性能问题前,先读 `.github/skills/<skill-name>/SKILL.md`;涉及性能或分配时同时读 `.github/skills/blazing-fast-dotnet/SKILL.md`。目录是一层的,名字即目录名。
- Avalonia 的 skill 在 `.github/skills/avalonia-*`(34 份,包括路由器 `avalonia` 与 `avalonia-pro-max-*` 子能力)。`Forge.App` 落地前先把 `Avalonia` 版本钉进 `Directory.Packages.props` 或 csproj——该套 skill 的第一条规则是读项目版本,没版本它只会按 12.1.x 猜。

## NuGet 白名单

仅 BCL、Avalonia(含官方同仓包,逐名见 `architecture.md`)、Roslyn、Microsoft.Data.Sqlite、neuecc 的库、xunit(仅测试项目)。三方 Avalonia 主题/图标/图表库与 `AvaloniaUI.DiagnosticsSupport` 未批准。其他包默认内化源码;不值得内化的包须先问用户(见 `architecture.md`)。

## 并行与串行

后续模块里没有相互依赖的部分并行派给多个 Worker(各自副本、各自归属路径);有依赖的串行,串行部分由主 Agent 自己写。接口契约先由主 Agent 定稿,再并行实现。

判断标准很简单:能同时开出两个以上互不阻塞的任务 → 派子 Agent 并行;否则 → 自己写。不要为了并行而硬拆任务。

## 换行约定

- 换行由 `.gitattributes`(`* text=auto eol=lf`)统一为 LF:仓库与工作树都是 LF,三平台检出一致。不要再用 `core.autocrlf=true` 让 Windows 检出变 CRLF——这正是此前 CI 换行失败与测试侧逐处归一化(`PromptSourceText`、`SpecFrontmatterTests` 的 `ReplaceLineEndings`)的根源,现已移除。`.gitattributes` 的 `eol=lf` 优先于 `core.autocrlf`,所以本机 system 级 `core.autocrlf=true` 不再影响本仓库。
- 若本地工作树仍是旧 CRLF 检出,一次性重归一化:`git add --renormalize .` 后 `git checkout -- .`(或 `git rm -r --cached . && git reset --hard`)。新克隆无需任何设置。
