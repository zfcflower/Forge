# 字体任务 rebase 到 main 的冲突记录(2026-11-06)

分支 `implement-fonts-task` 以 `db0d1dd` 为基,rebase 到 `main`(`1c61334`,已合入“界面语言”与“终端”任务)时,只有最后一个提交(`register the appearance section and apply fonts at startup`)产生冲突。处理如下。

## 冲突与处理

| 文件 | 冲突 | 处理 |
|---|---|---|
| `src/Forge.App/App.axaml` | main 在同处新增了 `LanguageSettingsViewModel` 的 DataTemplate,字体任务新增了 `AppearanceSettingsViewModel` 的 DataTemplate | 两个模板都保留(语言在前);`appearance:` xmlns 与 `AppearanceStyles.axaml` 的 StyleInclude 自动合并。 |
| `src/Forge.App/Composition/AppComposition.cs` — `DisposeAsync` | main 新增“逐个释放实现了 `IDisposable` 的设置分区视图模型”的循环,字体任务新增 `appearance.Dispose()` | 两者都保留,`appearance.Dispose()` 在前(先拆应用器,再由循环释放分区);`AppearanceSettingsViewModel.Dispose` 幂等,重复释放无害。 |
| `tests/Forge.App.Tests/Foundation/SettingsPanelTests.cs` | main 把断言改成精确的 `[provider, language, paths]`,字体任务(经用户同意)改成“provider 在首、paths 在尾”,`KnownSectionIds` 两边也不同 | 采用字体任务的宽松断言与全部预留 id:五个并行任务各自追加分区,精确列表会让每个后合入的任务都冲突一次。 |

## rebase 后的适配(语义冲突,git 未报)

- main 新增的 `StringTableTests.Every_key_carries_a_known_feature_prefix` 硬编码功能前缀白名单:追加 `appearance`(最小追加,同终端任务的处理)。
- 同文件的 `No_English_text_contains_CJK_characters`:字体预览的英文文本原本夹了“界面文字”“中文”用来演示 CJK 回退,改为纯英文(中文预览仍含中文)。

## 验证

`dotnet build Forge.slnx` 0 警告 0 错误;`dotnet test Forge.slnx` 全部项目全绿(App 930、Integration 170 等)。随后 `main` 快进到 `implement-fonts-task`。
