# W0 合入 W2 之后的差异变化记录(2026-11-13)

W2(`feat-w2-editor-lsp`)已把 W0(`main`,tip `0d5e1b3`)合并进来,合并提交 `c20834e`。本文记录**合并前后到底变了什么**,以及 W0 落地后需要更正的计划判断。W2 的功能实现本身没有因为合并而改动。

## 一、合并结果

- 合并方式:`git merge main`,写进 `feat-w2-editor-lsp`,不 rebase(保留 W2 的四个独立提交与合并提交)。
- 构建:`dotnet build Forge.slnx` → 0 警告 0 错误(含 Native AOT 分析器)。
- 测试:`tests/Forge.Editor.Tests` 503 通过;`tests/Forge.App.Tests`(Editor + Workbench + Localization + MinorRegression + ProblemsPanel + **W0 新增的 GapPlanPreferences**)= 943 通过(W2 单独时是 844,W0 带来 +99)。
- 冲突:仅 1 处(`src/Forge.App/SPEC.md` 的「未决」清单),其余全部自动合并。

## 二、重叠文件与处理

W0 与 W2 实际都改过的文件只有 4 个:

| 文件 | 结果 | 说明 |
|---|---|---|
| `src/Forge.App/SPEC.md` | **冲突,已手工解决** | 两边都在「未决」第一条写文件预览的现状:W2 的版本覆盖了「语义 token 叠加/查找引用/问题列表面板尚未接到 UI」的过时说法,保留 W0 新增的「聊天消息有复制按钮、尚不过 Markdown 渲染」一条,其余条目原样保留 |
| `src/Forge.App/ViewModels/MainWindowViewModel.cs` | 自动合并 | W0 的 `DescribePullRequestAsync` 等改动与 W2 的 `Problems` 属性、构造参数、`OpenFileAsync` 新增 `hasWorkspaceOverride`、`Dispose` 两行互不重叠,合并后两者都在 |
| `src/Forge.App/App.axaml` | 自动合并 | W0 的五个覆盖层占位模板(Palette/Checks/Plan/ResourceInspector/Onboarding)与 W2 的 `ProblemsPanelViewModel` 模板各在各的位置,`ProblemsPanelViewModel` 没有被 W0 预建过,所以没有重复注册 |
| `docs/research/2026-11-13-thinkrail-gap-plan.md` | 自动合并 | W0 的 W0-1/W0-2 实施记录与 W2 行下的交付记录并存 |

**没有重叠的**(W0 完全没碰,因此 W2 的改动原样保留、零冲突):`Workbench/WorkbenchLayout.cs`、`Workbench/WorkbenchTypes.cs`、`Localization/Strings.Inspector.cs`、`Localization/Strings.Shell.cs`、`Views/FileDocumentView.axaml`、`Views/FileDocumentView.Lsp.cs`、`Editing/**`、`src/Forge.Editor/**`、`Workbench/Controls/SyntaxTokenTextBlock.cs`、全部 W2 测试文件。

也就是说:此前担心的"W2 破了 W0 独占文件"在实际合并里只落在 4 个文件上,其中唯一需要人判断的是 `SPEC.md` 那一段文字。

## 三、W0 落地后需要更正的判断

1. **`Localization/Loc.cs` 没有共享注册点**(W0 核实并写进计划):每张 `Strings.<X>.cs` 用 `[ModuleInitializer]` 自注册,`KeyRegistryTests` 反射扫的是各表自己的常量。原计划「W0 一次性建空表并登记、`Loc.cs` 冻结」不成立。W2 把新键放进既有的 `Strings.Inspector.cs`(`files.editorLsp.references.*`、`problems.*`)与 `Strings.Shell.cs`(`workbench.problems`)因此是正确的,不需要迁到新表;`StringTableTests` 的已知前缀表补了 `problems`。
2. **W0 把各波的窗口面板定成了覆盖层**(用户已定):`MainWindow.axaml` 里五个 `ContentControl` 绑 `MainWindowViewModel.{Palette,Checks,Plan,Inspector,Onboarding}`(`object?` 槽)。**W2 的问题面板没有跟着做成覆盖层**,仍是工作台右侧工具组的固定标签(TODO | 评审 | 规格树 | 问题),理由是:诊断列表要常驻、要能一直开着扫,浮层每次都要开关;工具组里已经有三个同类的常驻列表,它是同一类东西。这一条差异是刻意的,若后续要统一成覆盖层,需要改的是 `ProblemsPanelView` 的宿主而不是面板本身。
3. **存储不升版**(W0 核实):工具结果多块由 `content_json` 旁的 `blocks` 表达,唯一的 schema 升版归 W7。与 W2 无关,记录在此以免后续再被旧计划误导。

## 四、W2 交付物(合并后)的位置

- `src/Forge.Editor/Highlighting/TokenCategoryGroup.cs`、`SemanticLineIndex.cs`;`src/Forge.Editor/Lsp/SemanticTokenMap.cs`、`WorkspaceDiagnostics.cs`;`Lsp/LspCapabilities.cs` 的 `ApplyEdits`;`Lsp/LanguageSession.cs` 的 `SemanticTokensRefreshRequested`/`PositionEncoding`;`Languages/LanguageSessionPool.cs` 的 `SessionStarted`/`SessionClosed`。
- `src/Forge.App/Editing/SemanticTokenColorizer.cs`;`Editing/ForgeEditorBridge.cs` 的 `HighlightingInstalled` + `HighlightLimit` 公开;`ViewModels/FileDocumentViewModel.Lsp.cs`(语义请求泵、`RequestReferencesAsync`、`BeginEditorRequestAsync`/`RequestLocationsAsync`);`Views/FileDocumentView.Lsp.cs`(挂载、Shift+F12、引用 `Flyout`);`Views/ReferenceListView*`、`ViewModels/ReferenceListViewModel.cs`;`Views/ProblemsPanelView*`、`ViewModels/ProblemsPanelViewModel.cs`、`ViewModels/MainWindowViewModel.Problems.cs`。
- 宿主接线(合并后在 W0 的文件里):`App.axaml:63` 的 `ProblemsPanelViewModel` 模板、`Workbench/WorkbenchTypes.cs`(`TabKind.Problems`、`WorkbenchIds.ProblemsTab`)、`Workbench/WorkbenchLayout.cs`(`object? problems = null` + 组标题)、`MainWindowViewModel.cs`(属性/构造/`Dispose`)、`Localization/Strings.Shell.cs`(`workbench.problems`)。
- 文案:`Strings.Inspector.cs` 的 `files.editorLsp.references.*` 与 `problems.*`。
- 规格:`Forge.Editor/SPEC.md`(语义 token 按行索引、诊断的工作区聚合)、`Forge.App/SPEC.md`(语义叠加、查找引用、问题面板段落 + 未决条目更正)。

## 五、对后续波次的提示

- `WorkbenchLayout` 现在多一个可空追加参数(`object? problems = null`)。W5(checks)/W7(plan)/W8(inspector)若也要进工具组,请把它换成**一组额外 inspector 标签的列表**再一起搬,不要继续追加单个参数。注意 W0 已把 Checks/Plan/Inspector 定为窗口覆盖层,这三波按 W0 的槽位走即可,不必进工具组。
- 计划文档里 `MainWindowViewModel.cs`/`App.axaml` 标着「冻结」,但 W2 与 W0 都实际改过(且合并干净)。后续波次若还要改这两处,先读本文与计划 W2 行下的记录,不要按「只多了一行」估冲突面:`OpenFileAsync` 的签名、`Dispose` 的释放列表都在里面。

## 六、复现验证

```bash
dotnet build Forge.slnx -p:BaseOutputPath=artifacts/merged/
dotnet test tests/Forge.Editor.Tests/Forge.Editor.Tests.csproj -p:BaseOutputPath=artifacts/m1/
dotnet test tests/Forge.App.Tests/Forge.App.Tests.csproj -p:BaseOutputPath=artifacts/m2/ \
  --filter "FullyQualifiedName~Forge.App.Tests.Editor|FullyQualifiedName~Forge.App.Tests.Workbench|FullyQualifiedName~Localization|FullyQualifiedName~MinorRegression|FullyQualifiedName~ProblemsPanel|FullyQualifiedName~GapPlanPreferences"
```

结果:构建 0 警告 0 错误;503 通过;943 通过。
