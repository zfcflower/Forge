# 字体任务合入后的遗留问题(2026-11-06)

字体自定义已合入 `main`(`321853c`)。以下问题在评审或测试中发现,本次未修复,留作后续。

## 1. `AppComposition.Create()` 中途抛异常时,字体功能的订阅不会被清理

- **位置**:`src/Forge.App/Composition/AppComposition.cs`,`Create()` 里较早执行的 `AppearanceFeature.Create(...)`。
- **现象**:字体功能一创建,`AppearanceApplier` 和 `AppearanceSettingsViewModel` 就订阅了 `AppPreferences.PropertyChanged` 与进程级的 `Loc.Instance.LanguageChanged`。如果 `Create()` 后面的步骤抛异常,组合对象不会返回,也就不会有人调用 `DisposeAsync`,这两个订阅会一直挂着(`Loc.Instance` 活得比组合对象长,于是把偏好对象和视图模型一起留住)。
- **为什么没修**:评审定为低优先级。`Create()` 里其它资源在中途失败时同样没有清理,单独给字体补清理意义不大;正常启动路径不受影响。
- **建议**:给 `Create()` 整体加“失败即回滚”(try/catch 里释放已创建的部分,包括 `appearance`),作为组合根的通用改动处理,而不是只修字体。

## 2. 一次无法复现的全量测试挂起

- **现象**:修复期间,一个 Worker 在自己的副本里跑全量 `dotnet test tests/Forge.App.Tests`,有一次 `testhost` 卡住超过 4 分钟。后续 3 次全量运行没有复现;主 Agent 在主仓又以 `--blame-hang-timeout 3m` 跑了 3 次 App 测试和 1 次全解决方案测试,都没有挂起。
- **原因**:未查明。那次运行与另一个后台 `dotnet test` 同时进行,机器负载很高,可能与此有关。
- **建议**:再出现时用 `dotnet test --blame-hang-timeout 3m` 运行,会生成转储并指出卡住的测试。

## 3. 已接受的偏差(记录在案,不是缺陷)

- **资源通知次数**:每次替换字体子字典会触发 2 次资源变更通知(旧字典移除一次、新字典挂上一次),不是设计稿里写的 1 次。已写进 forge-app SPEC 的“字体”段。
- **删掉的 headless 断言**:`AppearanceHeadlessTests` 不再断言设置面板上能看到“外观”分区标题。在每个测试单独开的 headless 会话里,这个检查和测试执行顺序有关,全量运行时稳定失败。“面板按顺序渲染所有已注册分区”改由 `SettingsPanelTests`(共享会话)保证。如果以后要在字体测试里证明标题确实渲染出来,需要先查清每个测试单独开 headless 会话时设置面板延迟渲染的原因。
