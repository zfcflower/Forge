# 开发 Skills(第三方来源,已筛选)

本目录存放**约束本仓库开发实践**的 agent 技能文件(Markdown + frontmatter),不是 Forge 的运行时能力,也不参与构建。

## 目录布局(为什么是一层)

加载器的规则是**一个目录只认一个 `SKILL.md`,一旦命中就不再下钻**。所以技能目录必须是平的:

```
.github/skills/<skill-name>/SKILL.md        ← 45 份,全部在这一层
.github/skills/<skill-name>/references/…    ← 各 skill 自己的引用材料,可以带子目录
.github/skills/README.md                    ← 无 frontmatter,扫描时会被静默忽略
.github/skills/LICENSES/                    ← 许可证原文
```

早先按 `dotnet/`、`avalonia/` 分组的布局只有 11 份能被发现:`avatonia/SKILL.md`(路由器)存在,整棵子树被跳过。分组信息已体现在名字前缀里(`avalonia-*`、`avalonia-pro-max-*`),不再用目录表达。

## 覆盖层级

冲突时按下列顺序取舍,上层优先:

1. `goal-and-requirements.md`、`architecture.md` 与 spec 图(`spec_*`)
2. `AGENTS.md`(角色分工与派活规则)
3. 本目录的 SKILL.md

也就是说:skill 里的建议在下面这些硬约束前一律让步,不得据 skill 反向修改 spec。

- **NuGet 白名单**:仅 BCL、Avalonia(含官方同仓包,见 `architecture.md`)、Roslyn、Microsoft.Data.Sqlite、neuecc 的库、xunit(仅测试)。skill 里出现的其他包(BenchmarkDotNet、CommunityToolkit.Mvvm、ReactiveUI、FluentAvalonia 等)一律需先经用户同意才可引入。
- **不含 Python/JS**:skill 提供的 `.py`/`.js` 脚本不执行;只取其中的方法论与命令。
- **栈固定**:`net11.0` / C# 15(SDK 11.0.100)/ Avalonia 12.1.6 / xunit v2(2.9.x)+ `xunit.runner.visualstudio` 3.x + VSTest(不是 MTP、不是 MSTest)。skill 里面向 MSTest、MTP 的分支不适用;面向 .NET 10 写的建议在 .NET 11 上照常适用,以 `Directory.Build.props` 与各 csproj 的实际版本为准。唯一已知的连带升级:Avalonia 的 `Avalonia.Headless.XUnit` 依赖 xunit v3,哪天要用它就得连带把 xunit 升到 v3(本仓直接手写 `HeadlessUnitTestSession`,所以仍留在 v2)。
- **性能第一**:与 `architecture.md` 的"性能第一"一致时可直接采纳;相互矛盾时以 spec 为准。

## 内容与来源

| 本地目录 | 上游 | 用途 |
|---|---|---|
| `analyzing-dotnet-performance` | `dotnet/skills` `plugins/dotnet-diag/skills/analyzing-dotnet-performance` | 约 50 条性能反模式扫描(async/分配/字符串/集合/LINQ/正则/序列化/IO) |
| `microbenchmarking` | `dotnet/skills` `plugins/dotnet-diag/skills/microbenchmarking` | BenchmarkDotNet 设计、配置与跑法(引入需白名单同意) |
| `dotnet-trace-collect` | `dotnet/skills` `plugins/dotnet-diag/skills/dotnet-trace-collect` | dotnet-trace/counters 采集与 perfcollect/PerfView |
| `csharp-refactoring` | `dotnet/skills` `plugins/dotnet/skills/csharp-refactoring` | 行为不变重构,内化三方源码时尤其相关 |
| `msbuild` | `dotnet/skills` `plugins/dotnet/skills/msbuild` | 构建故障、构建性能、`.csproj`/`.props` 评审 |
| `run-tests` | `dotnet/skills` `plugins/dotnet-test/skills/run-tests` | `dotnet test` 的框架/平台语法与过滤 |
| `test-anti-patterns` | `dotnet/skills` `plugins/dotnet-test/skills/test-anti-patterns` | 测试套件诊断(空断言、吞异常、顺序依赖) |
| `convert-to-cpm` | `dotnet/skills` `plugins/dotnet-nuget/skills/convert-to-cpm` | 中央包管理(Central Package Management)迁移 |
| `csharp-scripts` | `dotnet/skills` `plugins/dotnet-advanced/skills/csharp-scripts` | .NET 10 文件式 C# 应用(`dotnet run app.cs`,`#:package`/`#:property`/`#:sdk`/`#:ref`/`#:include`/`#:exclude`);在 `architecture.md` 中对应 "T3" 可选执行能力,不是 codemode 前提 |
| `avalonia-*`(34 份) | `linuxdevel/Avalonia-skills` | Avalonia 11.3/12.0/12.1 全套子 skill;引入范围与包依赖见下节 |
| `blazing-fast-dotnet` | `EgorBo/BlazingFastDotnet` | .NET 10/11 性能技巧;**本地为裁剪版** |
| `forge-dispatch` | 本地新增(无上游,不参与再同步) | 主 Agent 派活模板:副本创建、预期测试过滤器、skill 路径、收据要求 |

固定到上游提交,便于复核与再同步:

- `dotnet/skills` @ `95e7d0cb7994d47b5789baa68e13f7a2f348b956`
- `linuxdevel/Avalonia-skills` @ `d04c079a49405fd62baaa29fb61b3e2d58752cc4`
- `EgorBo/BlazingFastDotnet` @ `f14fe335048708c9a45bd31e4b403174e92a02d8`

## 未纳入

- `dotnet/skills` 的 `dotnet11`(net11-only)、`dotnet-upgrade`(已在 net11.0)、`dotnet-data`(EF Core,本项目直用 Microsoft.Data.Sqlite)、`dotnet-maui`/`dotnet-winforms`/`dotnet-aspnetcore`/`dotnet-blazor`/`dotnet-template-engine`、`dotnet-test-migration`、`dotnet-ai`(引导引 MEAI/MAF 包,与内化 agent-framework 源码的路线相反)。
- `dotnet-advanced` 的其余三项:`vectorization`(SIMD 专用)、`dotnet-pinvoke`、`nuget-trusted-publishing`;只取了同 plugin 的 `csharp-scripts`。
- `csharp-scripts` 的 `#:package` 指令能拉任意 NuGet 包。它在开发期用于原型无妨;一旦作为 codemode 的执行面,包来源、版本与离线可用性需要单独定规则,不能默认放行。

## Avalonia(已引入 34 份)

上游 `linuxdevel/Avalonia-skills` @ `d04c079`(MIT,覆盖 11.3/12.0/12.1)。已引入:`avalonia`(路由器)、`avalonia-versions`、`avalonia-xaml`、`avalonia-layout`、`avalonia-styling`、`avalonia-data-binding`、`avalonia-data-templates`、`avalonia-controls` 及其 `input`/`layout`/`navigation`/`data-display`/`media`(本地名为 `avalonia-controls-input` 等)、`avalonia-custom-controls`、`avalonia-property-system`、`avalonia-events`、`avalonia-graphics-animation`、`avalonia-input-interaction`、`avalonia-services`、`avalonia-app-development`、`avalonia-threading`、`avalonia-performance`、`avalonia-interop-rendering`、`avalonia-mvvm`、`avalonia-testing`、`avalonia-deployment`、`avalonia-wpf-migration`、`avalonia-pro-max` 及 `avalonia-pro-max-{design-system,components,motion,accessibility,layout-patterns,review-checklist}`。

未引入:`avalonia-mobile`(Non-Goals 只做桌面)、`avalonia-pro-max/themes` 与 `icons-imagery`(整篇围绕未批准的三方库)、`avalonia-pro-max/preview-server`(Python 脚本)。

**包依赖与白名单状态:**

| 依赖 | 状态 |
|---|---|
| 官方同仓包:`Avalonia.Controls.DataGrid`、`.TreeDataGrid`、`.WebView`、`Avalonia.Headless`(+ `.XUnit`)、`Avalonia.Skia`(含传递的 SkiaSharp)、`Avalonia.Themes.*`、`Avalonia.Fonts.*` | 已批准,逐名写入 `architecture.md` |
| `AvaloniaUI.DiagnosticsSupport`(12.x DevTools) | 未批准:Avalonia Plus 商业工具链。`RendererDiagnostics` 叠加层不依赖它,先用那部分 |
| `Avalonia.Headless.XUnit` | 已批准,但它的适配依赖 `xunit.v3.extensibility.core`;启用 headless UI 测试要连带把 xunit 升到 v3。本仓不用它:测试直接用 `Avalonia.Headless` 的 `HeadlessUnitTestSession`(见 `SharedUi.cs`) |
| 三方主题/图标/图表库(`FluentAvaloniaUI`、`SukiUI`、`Semi.Avalonia`、`Irihi.Ursa`、`Material.Avalonia`、`Citrus.Avalonia`、`ShadUI`、`LiveCharts`、`Projektanker.Icons.*`、`Lucide.Avalonia`、`Svg.Skia`) | 待定,引入前逐个确认;`avalonia-styling` 与 `avalonia-pro-max` 正文里的选型表按"候选"读 |
| `CommunityToolkit.Mvvm` / `ReactiveUI`(`avalonia-mvvm`、`avalonia-threading`、`avalonia-data-binding` 中提及) | 待定;手写 `INotifyPropertyChanged` + `ICommand` 的分支无需它们 |

**启用前提:** 本套按 Avalonia 12.1.x 撰写(skill 正文里的示例版本是 12.1.4,本仓库钉在 12.1.6;skill 内推荐 `net10.0`,本仓库为 `net11.0`),而路由器第一条规则是"先读 csproj 检测版本"。`Forge.App` 建立后先把 `Avalonia` 版本钉进 `Directory.Packages.props` 或 csproj,再让 Agent 加载这套 skill,否则它只能按 12.1.x 猜。主程序走 Native AOT,则 `avalonia-interop-rendering` 与 `avalonia-performance` 的 NativeAOT/trimming 段需按 AOT 约束读。

## 接入与可见性

`.github/skills/` 是应用认的**项目级兼容技能根**(与 `.claude/skills`、`.gemini/skills` 同列),因此:

- **Skill 面板可见**:右上角 Skill 按钮走 `skills.state` → `listSkillCatalog`,只枚举内置技能与该组兼容目录;放在这里才会被列出来。
- **需要确认**:项目级技能 `gated: true`,决策规则是"未受信任 → untrusted;已受信任但未确认 → pending-ack;确认后 → load"。面板里确认过才会进入会话技能列表。
- **命令形态**:加载后的斜杠命令是 `/skill:<name>`,例如 `/skill:msbuild`。
- **额外兜底**:`.pi/settings.json` 也指向同一目录(`{"skills": [".github/skills"]}`),给 pi 核心的加载器用;同一文件被两条路径发现时按规范化路径去重,不会重复注册。
- **成本**:全量 45 份的 name+description 常驻约 5k tokens(Avalonia 34 份 ≈ 3.2k、dotnet 9 份 ≈ 1.8k)。面板里可逐项关闭,或从设置里的数组移除。
- **快捷开关**:`--no-skills` 可全局关闭技能发现。

## 再同步

逐文件覆盖对应上游路径即可,不要改 `SKILL.md` 正文(覆盖规则统一由本文件表达,改动集中在 README 便于 review)。已知的偏离上游之处,必须记在这里:

1. `blazing-fast-dotnet/SKILL.md`:删除上游 `# ASP.NET Core` 整节与 `# Magic knobs to Tune .NET Performance` 表;两处跨仓引用改指本地副本;文首有裁剪说明。
2. `avalonia-pro-max-{accessibility,components,design-system,layout-patterns,motion,review-checklist}`:frontmatter `name` 由 `avalonia-pro-max/<x>` 改为连字符形式——斜杠不符合加载器的名称规则(`^[a-z0-9-]+$`)。正文未改。
3. 全部 44 份:目录名统一取 frontmatter `name`(受上条"一层布局"的规则约束)。
4. `avalonia-controls-{data-display,input,layout,media,navigation}`、`avalonia-property-system`、`avalonia-services`(7 份):上游把 `description:` 写成含 `: ` 的明文标量,Avalonia 那套的其它文件也是这个毛病,但只有这 7 份的正文里含冒号加空格,YAML 解析直接报 `Nested mappings are not allowed in compact mappings` 而被整份丢弃。已改为折叠块标量(`description: >-` + 缩进两格),文本逐字未改。

## 许可

上游均为 MIT,原文见 `LICENSES/`。
