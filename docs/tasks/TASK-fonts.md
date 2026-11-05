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

## 待细化(本对话里问用户)

- 字号范围与步进(底座默认夹紧 9–28)。
- 现有 `FontSize*` 令牌(Xs/Sm/Base/Md/Lg/Xl)是否按界面字号等比缩放。
- 是否提供“恢复默认”。

## 设计提示

- 把 `Tokens.axaml` 的 `FontMono` 与字号令牌改为运行时可替换的动态资源,由偏好驱动(`DynamicResource`);避免逐控件订阅。
- 系统字体枚举:Avalonia `FontManager.Current.SystemFonts`(AOT 安全);枚举放后台,结果缓存。

## 归属路径

`src/Forge.App/Resources/Tokens.axaml` 中字体与字号令牌、`src/Forge.App/Appearance/**`(新目录)、使用代码字体的视图里对 `FontMono` 的引用、`Localization/Strings.Appearance.cs`、设置分区 `appearance`、`tests/Forge.App.Tests/Appearance/**`;SPEC:forge-app“主题”段。

## 共同前提(所有五个任务)

- **底座已合入**(`19c97d8`):偏好 `AppPreferences`/`PreferencesStore`、实时文案 `Loc`、设置分区 `SettingsSections`(`src/Forge.App/Preferences/`、`Localization/`)。用法与坑以 forge-app SPEC“偏好、文案与设置分区”为准:分区在 `AppComposition.Create()` 里向 `composition.Settings` 追加并在 `App.axaml` 加模板;文案表用 `[ModuleInitializer]` 自注册;视图模型里缓存的文本订阅 `Loc.LanguageChanged`。
- **新文案**一律注册到本功能自己的表 `src/Forge.App/Localization/Strings.<功能>.cs`(中英两份),不要改 `ViewModels/Strings.cs`,也不要改别的功能的表。
- **设置项**放在本功能自己的设置分区(`SettingsSectionIds` 里预留了 id 与顺序),不要改设置页本身。
- **并行**:五个任务在不同对话里同时进行。只改本 spec“归属路径”列出的路径;需要越界时先停下问用户。共享文件(`AppComposition.cs`、`App.axaml`)只做“追加注册”这类最小改动,提交前 `git pull --rebase`。
- **本文件位置**:`docs/tasks/`(随仓库提交,新 worktree 自带);任务完成后由本任务删除它。
- **流程**:按 brainstorming 工作流进行;本 spec 的 Product 是已确认部分,“待细化”的问题在本对话里向用户问清再定 Design。分工与模型按 `AGENTS.md`。
