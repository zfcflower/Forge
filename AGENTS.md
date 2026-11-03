# Forge 开发约定(本仓库)

这些规则约束**开发 Forge 这个产品时的 Agent 分工**,不是 Forge 自身的运行时行为。

## 角色分工

| 角色 | 模型 | 负责 |
|---|---|---|
| 主 Agent(规划/调度) | `anthropic/claude-sonnet-5.5` | 读代码与 spec、拆解任务、写接口与骨架、派活、复核结果、合并、更新 spec、提交。不写实现代码。 |
| 实现 Worker | `deepseek/deepseek-flash:high`(DeepSeek 官方供应商;备选 `openrouter/deepseek/deepseek-v4.1-flash-1017:high`) | 按任务写实现代码。只在自己副本里工作,测试通过后只拷回自己负责的路径。 |
| 测试 | `openrouter/anthropic/claude-haiku-5.5:high`(opencode-go 的 Muse Spark 1.3 Contributor 作备选) | 只编写测试,不做 review。 |
| Review | `openrouter/openai/gpt-6.1-sol:medium` | 只读评审,输出实质性问题与结论。 |

## 评审标准

评审不止于找代码 Bug,三项按优先级排序:
1. **实现是否符合架构约束**——对照 `architecture.md`/`goal-and-requirements.md`/模块 SPEC 检查边界、依赖方向、性能第一、Native AOT 兼容、扩展点契约等是否被遵守,偏差是否已记录或征得同意。
2. **测试是否足以证明实现正确**——测试是否覆盖关键路径、边界、失败/取消/并发、错误状态,而非只测 happy path;Skip 是否都在记录真实缺陷。
3. **代码质量**——正确性、资源、并发、性能陷阱。

第 1、2 项比第 3 项更重要。

## 派活规则

- 每个 Worker 任务必须显式带上第二列“实现 Worker”所列模型;测试任务必须带 Haiku 5.5(`openrouter/anthropic/claude-haiku-5.5:high`),评审任务必须带 GPT 6.1 Sol(`openrouter/openai/gpt-6.1-sol:medium`)。不要用主 Agent 的模型派子 Agent。
- Worker 在自己的仓库副本里干活(`D:/FORGE/workN/<part>`),`dotnet test` 全绿后只把归属路径拷回主仓库;不碰其他路径、`*.csproj`、`Directory.Build.props`、`Forge.slnx`,不执行 git 命令。
- 主 Agent 只写接口契约(`Forge.Abstractions`)、调度、集成与 spec;实现细节交给 Worker。
- 主 Agent 复核:构建、跑全量测试、检查是否越界改动,再决定是否采纳。
- 派活文本里写明该任务适用的 `.github/skills/...` 路径。Worker 的上下文是干净的,不会自己去找 skill,不带路径等于没这份约束。

## 开发 Skills

- 筛选后的第三方技能文件在 `.github/skills/`,只约束**开发实践**,不参与构建、不随产品发布。适用清单、裁剪内容、上游提交与再同步方式见 `.github/skills/README.md`。
- 优先级:spec(`architecture.md`、`goal-and-requirements.md`)>`AGENTS.md`> `.github/skills/`。skill 里的建议不得据以修改 spec,也不构成扩张 NuGet 白名单的理由。
- 写实现、写测试、排查构建或性能问题前,先读 `.github/skills/<skill-name>/SKILL.md`;涉及性能或分配时同时读 `.github/skills/blazing-fast-dotnet/SKILL.md`。目录是一层的,名字即目录名。
- Avalonia 的 skill 在 `.github/skills/avalonia-*`(34 份,包括路由器 `avalonia` 与 `avalonia-pro-max-*` 子能力)。`Forge.App` 落地前先把 `Avalonia` 版本钉进 `Directory.Packages.props` 或 csproj——该套 skill 的第一条规则是读项目版本,没版本它只会按 12.1.x 猜。

## NuGet 白名单

仅 BCL、Avalonia(含官方同仓包,逐名见 `architecture.md`)、Roslyn、Microsoft.Data.Sqlite、neuecc 的库、xunit(仅测试项目)。三方 Avalonia 主题/图标/图表库与 `AvaloniaUI.DiagnosticsSupport` 未批准。其他包默认内化源码;不值得内化的包须先问用户(见 `architecture.md`)。

## 并行与串行

后续模块里没有相互依赖的部分并行派给多个 Worker(各自副本、各自归属路径);有依赖的串行。接口契约先由主 Agent 定稿,再并行实现。
