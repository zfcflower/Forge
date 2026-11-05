---
id: task-fonts
type: task-spec
status: draft
title: 界面字体与代码字体自定义
parent: forge-app
---

# 界面字体与代码字体自定义

## Request

goal:字体可自定义。用户决定(2026-11-05):**界面字体 + 代码字体,各自可改字号**,即时生效。

## Product(已确认)

- 设置分区“外观”(`appearance`):界面字体族 + 字号、代码字体族 + 字号;从系统已安装字体中选(可搜索),也可手输回退链。
- 界面字体作用于树、标签、聊天、按钮;代码字体作用于文件预览、Diff、工具参数/结果、终端(终端任务读同一偏好)。
- 即时生效,无需重启;重启后保持(偏好已持久化)。

## 细化结论(2026-11-05 用户确认)

- 字号范围 9–28,步进 1(与底座 `AppPreferences` 的夹紧一致,不另设范围)。
- `FontSize*` 令牌随界面字号等比缩放并取整,以 Base=13 为基准。
- “恢复默认”界面、代码各一个,只重置本组的字体族与字号。
- 代码文本直接用代码字号(所见即所设);默认 13,因此代码文字从现在的 12 变为 13。不改底座默认值。
- 越界许可:`Controls.axaml`、`MainWindow.axaml`、`Views/FinishedSubAgentsHeaderView.axaml` 中 `{StaticResource FontSize*}`/`{StaticResource FontMono}` 机械改为 `{DynamicResource …}`;`Resources/Workbench.axaml` 写死的 `FontSize="12"` 改为 `FontSizeSm` 令牌;`App.axaml` 追加 DataTemplate 与 StyleInclude;`AppComposition.cs` 追加分区注册、外观应用器的创建与拆除。

## Design

**令牌即契约。** 字体只经资源键传递,控件不订阅偏好:

| 键 | 类型 | 来源 | 默认 |
|---|---|---|---|
| `FontUi` | `FontFamily` | `UiFontFamily` | `AppPreferences.DefaultUiFontFamily` |
| `FontMono` | `FontFamily` | `CodeFontFamily` | `AppPreferences.DefaultCodeFontFamily` |
| `FontSizeXs/Sm/Base/Md/Lg/Xl` | `double` | `UiFontSize` 等比 | 11/12/13/15/18/22 |
| `FontSizeCode` | `double` | `CodeFontSize` 原值 | 13 |

- `Tokens.axaml` 保留这些键的静态默认值(设计器、无应用器的测试照常可用),新增 `FontUi`、`FontSizeCode`;所有使用处改为 `DynamicResource`。`TextBlock.mono` 改用 `FontMono` + `FontSizeCode`。
- 缩放是纯函数 `FontScale.Compute(uiSize)`:比例 = uiSize/13,各令牌 `Math.Round(默认值×比例, MidpointRounding.AwayFromZero)`;uiSize=13 时与现值逐个相等。

**应用器。** `Appearance/AppearanceApplier`(UI 线程)持有目标 `IResourceDictionary`(运行时为 `Application.Current.Resources`),把上表的值写进**一个**它自己拥有、合并在目标里的子字典;每次应用新建子字典整体替换,使一次偏好变化只引发一次资源变更通知,而不是逐键 8 次。只在字体/字号四个属性变化时应用,值未变不替换。若 Fluent 主题的控件模板读取 `ContentControlThemeFontFamily`/`ControlContentThemeFontSize`,一并覆盖,使 TextBox、按钮等内置控件也跟随界面字体(由 Worker 实测确认)。`FontFamily.Parse` 失败(非法回退链)时保留上一个有效值并 Trace,不抛。
- 界面字体的作用面:`Appearance/AppearanceStyles.axaml` 里 `:is(Window)` 样式设置 `FontFamily={DynamicResource FontUi}`、`FontSize={DynamicResource FontSizeBase}`,主窗口与浮动窗口经继承覆盖树、标签、聊天、按钮。
- 时序:组合根创建应用器时立刻按当前(默认)偏好应用一次;`InitializeWorkbenchAsync` 加载偏好后,像 `ApplyLanguage` 一样投递到 UI 线程再应用一次(加载不发通知);此后订阅 `PropertyChanged`。组合根拆除时退订。启动时若加载晚于首帧,会先以默认字体绘制再重排一次,可接受。

**系统字体目录。** `Appearance/SystemFontCatalog`:首次请求时在线程池枚举 `FontManager.Current.SystemFonts` 的族名,去重、按序数忽略大小写排序,结果缓存(同一 `Task` 复用,不重复枚举)。枚举源可注入以便测试;枚举抛错时返回空列表并 Trace,设置页仍可手输。

**设置分区 `appearance`。** `AppearanceSettingsViewModel` + `AppearanceSettingsView.axaml`(编译绑定,`x:DataType`):
- 两组(界面字体、代码字体),每组:字体族可编辑下拉(`AutoCompleteBox`,按包含过滤系统字体,允许手输逗号分隔的回退链)、字号 `NumericUpDown`(9–28,步进 1,整数显示)、一行用本组字体与字号渲染的预览文字、“恢复默认”按钮。
- 字体族在选中候选项、回车或失焦时提交(不随每次按键应用,避免逐字触发字体回退与全树重排);字号改动即时提交。提交走 `AppPreferences` 的 setter(校验与持久化归底座)。
- 视图模型监听 `AppPreferences.PropertyChanged`,外部改动(如另一处重置)回显到编辑框。文案注册在 `Localization/Strings.Appearance.cs`(`AppearanceStrings`,键前缀 `appearance.`),缓存文本订阅 `Loc.LanguageChanged`。

**终端。** 终端任务直接用 `FontMono`/`FontSizeCode` 资源(或读同一偏好),本任务不触碰终端代码。

**验证。** 单测:`FontScale` 边界(9、13、28,单调不减);应用器对独立 `ResourceDictionary` 的写入、一次变更只替换一次、值未变不替换、非法族名保留旧值、拆除后不再响应;目录的缓存、去重排序、枚举失败;视图模型的提交时机、重置、外部回显、语言切换。headless:改偏好后 `TextBlock.mono` 与普通 `TextBlock` 的实际 `FontFamily`/`FontSize` 即时变化;设置分区按 `appearance` id 与顺序出现;中英文案齐全。AOT:不用反射绑定。

**推迟。** 行高、字重、连字、按语言区分字体不做;字体预览不渲染整段样例代码。

## 设计提示

- 把 `Tokens.axaml` 的 `FontMono` 与字号令牌改为运行时可替换的动态资源,由偏好驱动(`DynamicResource`);避免逐控件订阅。
- 系统字体枚举:Avalonia `FontManager.Current.SystemFonts`(AOT 安全);枚举放后台,结果缓存。

## 归属路径

`src/Forge.App/Resources/Tokens.axaml` 中字体与字号令牌、`src/Forge.App/Appearance/**`(新目录)、使用代码字体的视图里对 `FontMono` 的引用、`Localization/Strings.Appearance.cs`、设置分区 `appearance`、`tests/Forge.App.Tests/Appearance/**`;SPEC:forge-app“主题”段。经用户许可的越界(仅限机械替换/追加):`Resources/Controls.axaml`、`MainWindow.axaml`、`Views/FinishedSubAgentsHeaderView.axaml`、`Resources/Workbench.axaml`、`App.axaml`、`Composition/AppComposition.cs`。

## 共同前提(所有五个任务)

- **底座已合入**(`19c97d8`):偏好 `AppPreferences`/`PreferencesStore`、实时文案 `Loc`、设置分区 `SettingsSections`(`src/Forge.App/Preferences/`、`Localization/`)。用法与坑以 forge-app SPEC“偏好、文案与设置分区”为准:分区在 `AppComposition.Create()` 里向 `composition.Settings` 追加并在 `App.axaml` 加模板;文案表用 `[ModuleInitializer]` 自注册;视图模型里缓存的文本订阅 `Loc.LanguageChanged`。
- **新文案**一律注册到本功能自己的表 `src/Forge.App/Localization/Strings.<功能>.cs`(中英两份),不要改 `ViewModels/Strings.cs`,也不要改别的功能的表。
- **设置项**放在本功能自己的设置分区(`SettingsSectionIds` 里预留了 id 与顺序),不要改设置页本身。
- **并行**:五个任务在不同对话里同时进行。只改本 spec“归属路径”列出的路径;需要越界时先停下问用户。共享文件(`AppComposition.cs`、`App.axaml`)只做“追加注册”这类最小改动,提交前 `git pull --rebase`。
- **本文件位置**:`docs/tasks/`(随仓库提交,新 worktree 自带);任务完成后由本任务删除它。
- **流程**:按 brainstorming 工作流进行;本 spec 的 Product 是已确认部分,“待细化”的问题在本对话里向用户问清再定 Design。分工与模型按 `AGENTS.md`。
