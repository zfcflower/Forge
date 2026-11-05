---
id: task-ui-language
type: task-spec
status: draft
title: 界面语言可选(中文 / English,即时切换)
parent: forge-app
---

# 界面语言可选(中文 / English,即时切换)

## Request

goal:界面语言可选。用户决定(2026-11-05):**中文 + English,切换后即时生效**;默认跟随系统。

## Product(已确认)

- 设置分区“语言”(`language`):跟随系统 / 中文 / English。
- 切换后所有可见文案立刻刷新,包括已打开的会话卡片头、状态栏、标题栏按钮提示、右键菜单、空态文案。
- 模型对话内容、用户输入、文件内容不翻译。

## 待细化(本对话里问用户)

- 数字/日期/耗时格式是否随语言(如“第 3 轮 · 12:30 · 4.2s”对应英文写法)。
- 英文术语表(会话/Session、子 Agent/Sub-agent、检视区/Inspector、规格/Spec……)由谁定稿。
- 插件贡献的文案(工具名、命令描述)是否参与本地化(大概率不)。

## 设计提示

- **跟随系统目前恒为英文**:`Directory.Build.props` 开着 `InvariantGlobalization`,`CurrentUICulture` 是不变文化;底座已用 `InstalledUICulture` 与 `LANG`/`LC_ALL` 兜底。是否关闭不变全球化(会增大 AOT 体积并引入 ICU)由本任务向用户确认后决定。
- XAML 直接绑全局 `Loc` 时注意:已关闭窗口的绑定在切换语言时可能跨线程收到通知(底座已逐订阅者隔离异常);长生命周期的视图优先绑视图模型属性。

- 底座 `Loc` 已提供实时切换与按功能分表;本任务把现有 `src/Forge.App/ViewModels/Strings.cs`(约 200 条)和 XAML 里的硬编码中文迁到 `Localization/Strings.*.cs` 表,补英文。
- 视图模型里缓存的格式化文本要订阅 `Loc.LanguageChanged` 重算;XAML 用 `{Binding [Key], Source={x:Static loc:Loc.Instance}}`。
- 加一个测试扫描 `.axaml` 与 `.cs`,禁止新增未走 `Loc` 的中文字面量(白名单极少数例外)。

## 归属路径

`src/Forge.App/ViewModels/Strings.cs`(迁移后删除或只留转发)、`src/Forge.App/Localization/Strings.*.cs`(**不改**其它功能自建的表,只新增迁移用的表)、引用旧 `Strings` 的视图与视图模型、`tests/Forge.App.Tests/Localization/**`;SPEC:forge-app“字符串与自动化”段。

## 注意

迁移面大、和其它四个任务都会碰到视图文件。按模块分批迁移、每批单独提交并尽快合入;遇到别的任务刚改过的文件先 `git pull --rebase`。

## 共同前提(所有五个任务)

- **底座已合入**(`19c97d8`):偏好 `AppPreferences`/`PreferencesStore`、实时文案 `Loc`、设置分区 `SettingsSections`(`src/Forge.App/Preferences/`、`Localization/`)。用法与坑以 forge-app SPEC“偏好、文案与设置分区”为准:分区在 `AppComposition.Create()` 里向 `composition.Settings` 追加并在 `App.axaml` 加模板;文案表用 `[ModuleInitializer]` 自注册;视图模型里缓存的文本订阅 `Loc.LanguageChanged`。
- **新文案**一律注册到本功能自己的表 `src/Forge.App/Localization/Strings.<功能>.cs`(中英两份),不要改 `ViewModels/Strings.cs`,也不要改别的功能的表。
- **设置项**放在本功能自己的设置分区(`SettingsSectionIds` 里预留了 id 与顺序),不要改设置页本身。
- **并行**:五个任务在不同对话里同时进行。只改本 spec“归属路径”列出的路径;需要越界时先停下问用户。共享文件(`AppComposition.cs`、`App.axaml`)只做“追加注册”这类最小改动,提交前 `git pull --rebase`。
- **本文件位置**:`docs/tasks/`(随仓库提交,新 worktree 自带);任务完成后由本任务删除它。
- **流程**:按 brainstorming 工作流进行;本 spec 的 Product 是已确认部分,“待细化”的问题在本对话里向用户问清再定 Design。分工与模型按 `AGENTS.md`。
