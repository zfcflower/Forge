# W8 资源检查器:实现说明(缺口计划 §五)

对照 `2026-11-13-thinkrail-gap-plan.md` 的 W8 行(缺口 #13):「运行中工具/命令与子 Agent 统一列表(耗时、日志尾、停止)、消费 `AgentToolProgress`」。

## 一、方案

### 1. 数据的来源与线程模型

`AgentToolProgress` 由 `AgentLoop` 在工具执行线程上直接 `sink.Emit`,生产侧(`ProcessOutputTail`)已经限流到**每路每秒最多 4 次、尾部最多 16 KiB**(`ProcessContracts.OutputTailChars`)。因此消费侧**不能**把每个事件派发到 UI 线程(那会每秒几十次 dispatcher 排队,且尾部分片会互相覆盖):

- **会话持有一个实时登记表**(`SessionWorkspace.Tools.cs`,新 partial):`ConcurrentDictionary<(AgentId, CallId), LiveToolViewModel>`,容量 = 正在运行的工具调用数。
  - `AgentToolStarted`(已经过 coalescer,UI 线程):**只有这里创建条目**,再挂到刚建出的工具行上(先取一次快照,把「先到的尾部」补进去)。闸门按 agent 收窄:**本会话根 Agent 的调用要求回合在跑**(`IsRunning` 为真)——被取消回合的 start 事件可能仍排在 coalescer 队列里,而回合结束的续延是 `DispatcherPriority.Normal`、coalescer 的冲刷是 `Background`,清表会先跑、事件后到,不设闸就会造出一条永不清除的幽灵行(它的停止按钮还会顺手暂停排队消息);**子 Agent 的调用不要求回合在跑**,因为子 Agent 本来就能在回合外运行(`LaunchChildAsync`:CI 链的 Scout、mailbox 请求),拦掉它们会让检查器在最需要看输出的场景里只剩一行子 Agent。子 Agent 这一侧靠它自己的终止事件兜底:子 Agent 结束时 `FinishToolsOf(agentId)` 按 agent 清掉它的条目与暂存尾部(被取消的子 Agent 同样不会有 finished,不能等)。
  - `AgentToolProgress`(worker 线程,**不派发**):命中条目 → 一次 volatile 字符串赋值;没命中(进度比开始事件先到,两者来自不同线程)→ 暂存进 `pendingTails` 并由 `TrackTool` 消费。这条路径**从不创建行**,所以回合结束清表后迟到的快照不可能把幽灵行带回来(最多在 `pendingTails` 里留一个字符串,下一次清表时丢掉)。不分配、不加锁、不发 `PropertyChanged`。
  - `AgentToolFinished`(UI):移除条目并存着的尾部。
  - **回合边界**(开始与结束,`IsRunning` 的 setter 两个方向)只清「本会话根 Agent 与已不在活动名单里的 agent」的条目与暂存尾部(`ClearTurnTools`);**活动子 Agent 的条目跨边界保留** —— 它可能在回合外流式输出(CI 链/mailbox),用户这时发一条消息不该让检查器失明;子 Agent 的条目由它自己的终止事件(`FinishToolsOf`)或 `Dispose` 的全量清理(`ClearLiveTools`)负责。
- **显示侧是拉模型,不是推模型**:条目自己缓存「上次渲染的尾部版本 + 行数上限」,`Tick()` 只在尾部或行数真的变了才裁行 + 抬 `PropertyChanged`;`TailText` 是缓存字段,绑定的每次重读都不重新扫描文本。
  - 会话时钟(1 s,`MainWindowViewModel.OnClockTick` → `SessionWorkspace.Tick`)负责**耗时**读数与折叠面板里的工具行尾部(`LiveToolViewModel.Tick`);
  - 检查器(打开时才存在)另有一个 `DispatcherTimer`,间隔取 `AppPreferences.InspectorRefreshMilliseconds`(默认 1 s,250–10000),只调 `LiveToolViewModel.RefreshTail` —— **「刷新间隔」只作用于输出尾部**,耗时读数始终按会话时钟 1 s 走(与设置页提示文案一致;让间隔同时驱动耗时会让读数每秒跳 4 次 0.1 秒)。
- **关闭检查器后的一切成本 = 生产侧已有的成本**:0 次裁行、0 次 `PropertyChanged`、0 个定时器;登记表里每个运行中的调用只占一个条目 + 一个字符串引用。

### 2. 改动的文件

| 文件 | 做什么 |
|---|---|
| `Services/TextTail.cs`(**新**) | 「保留最后 N 行」的唯一实现,从 `Automations/CiChain.cs` 的 `CiChainRunner.Tail` 抽出;`CiChainRunner.Tail` 改为委托(与 W5 分支同名同内容,合并是 add/add 同文本)。检查器只裁行(`maxChars = 文本长度`),因为 16 KiB 是生产侧上限。 |
| `ViewModels/Transcript.Tools.cs` | 新增 `LiveToolViewModel`(工具调用的运行态:agent 归属、名字、参数摘要、开始时间、尾部、停止命令);`ToolRowViewModel` 增 `AttachLive`、`HasLiveOutput`、`LiveOutputText`、`LiveElapsedText`,`Tick` 时把实时状态转给绑定。 |
| `ViewModels/SessionWorkspace.Tools.cs`(**新**) | 实时登记表、`AgentToolProgress` 消费、`ClearLiveTools`、给工具行挂实时态。`SessionWorkspace.cs` 加 `partial`、`Tick` 里 tick 条目、`IsRunning=false`/`Dispose` 清表、把 `openInspector` 回调转给 `Instruments`。 |
| `ViewModels/SessionInstrumentsViewModel.cs` | 「资源」菜单第 5 行:`OpenInspectorCommand`(无宿主回调时 `CanOpenInspector=false`,`MenuItem.IsVisible` 跟随,不留死条目)、`OpenInspectorMenuText`(带数量,口径 = 检查器会列出的行数:运行中的调用 + 每个活动子 Agent,含排队中的)、`NoteRunningResources(int)`。 |
| `ViewModels/ResourceInspectorViewModel.cs`(占位替换) | 覆盖层视图模型:统一列表(`ObservableCollection<object>`,元素是 `SubAgentViewModel` 或 `LiveToolViewModel`,**按开始时间插入**(排队中的子 Agent 读作 `MinValue`,排最前)、~~标题旁带会话名~~(follow-up 改为按会话分组,见第七节)与运行中计数、空状态/无会话状态、关闭命令、按偏好配置的定时刷新(`schedule` 注入点让测试不用消息循环)、行只在增删时变化、`Dispose` 停表并退订 `Loc.LanguageChanged`/`AppPreferences.PropertyChanged`。~~`ResourceInspectorSession` 只带标题与两列活对象~~(follow-up 改为 `IResourceInspectorSource`,见第七节),面板从不写会话。 |
| `Views/ResourceInspectorView.axaml(.cs)` | 遮罩 + 居中卡片(与 W7 计划面板同构:Escape / 点遮罩 / 关闭按钮都关)、两种行的模板、尾部的只读等宽区(带自身滚动、上限高度)、停止按钮。 |
| `Views/ToolRowTailView.axaml` | 折叠展开后的工具行里的实时尾部(`运行中 · 12.3s` + 尾部文本),只在有实时输出时出现;调用结束后条目解除该行的订阅并在 `Finish` 里释放快照(结果里已有完整输出)。 |
| `Views/SessionView.axaml` | 「资源 ▾」菜单末尾一行「运行中的资源({n})…」,绑 `Instruments.OpenInspectorCommand`,没有宿主回调时整行隐藏。 |
| `ViewModels/MainWindowViewModel.Inspector.cs` | `Inspector` 槽的开关:`OpenInspectorCommand`、`OpenInspector()`(建 VM、打开)、`CloseInspector()`、~~切会话即关(W5/W7 同款)~~(follow-up:切会话只重排分组)。`MainWindowViewModel.cs` 只在会话构造处多一个 `openInspector: OpenInspector` 与 `Dispose` 里一行 `CloseInspector()`。 |
| `Settings/ResourceInspectorSettingsViewModel.cs` + `Views/ResourceInspectorSettingsSectionView*` + `Composition/InspectorComposition.cs` | `inspector` 设置分区(行数、刷新间隔),由 W8 的组合模块注册,不改 `AppComposition.cs` / `App.axaml`。 |
| `Localization/Strings.Resources.cs`(**新**) | 本波唯一的文案表(`resources.*`)。 |

### 3. 失败与取消路径

- 工具进度只用来显示:任何异常都不发生在它里面(只赋值)。
- 停止:**子 Agent** 用自己的 `StopCommand`(既有 `SubAgentControl.Stop`);**工具调用**起初只能停整个回合(回合令牌直接交给工具)。**follow-up 已改为只停该调用**:Core 给每次调用一个链到回合令牌的 CTS,见第七节。
- **排队中的子 Agent 不显示停止按钮**:`SubAgentControl.Stop` 只认已获得运行槽的实例,对排队中的返回 `NotFound`,原先是「按钮在、点不动」。`SubAgentViewModel`(W8 拥有的文件)因此新增 `CanStop = Status == Running && !isStopRequested`,检查器行与既有的子 Agent 标签页都改绑它 —— 前者是本波新增,后者顺手修掉同一枚死按钮。
- 检查器打开时没有活动会话(竞态):显示「没有打开的会话」空状态,不抛。
- 只读视图(归档/接管):照样列出正在跑的东西;它没有运行时,所以工具行不给停止按钮(`stopCall` 为 null → `CanStop` 为假)。

## 二、被否的备选

1. **把 `AgentToolProgress` 派发到 UI 线程(每个事件一次 `Dispatcher.Post`)** —— 否。每秒最多 4 次 × 运行中的调用数,UI 线程要在尾部分片之间来回合并;而尾部是**快照**,丢掉中间片什么也不损失。拉模型让 UI 的代价与事件频率脱钩。
2. **工具行 = 检查器行(共享同一个 `ToolRowViewModel`)** —— 否。工具行活在某个回合卡片里,卡片结束就清空引用(`TurnCardViewModel.Finish` 会清 `toolRows`),而检查器要的是「现在正在跑的」独立生命周期;而且工具行没有停止语义、没有 agent 归属。反过来让 `TurnCardViewModel.AddTool` 返回行(两行改动)可以让两者共用运行态条目 —— 采用了这个,但**行**仍是两个(卡片行 vs 检查器行),共用的是 `LiveToolViewModel`。
3. **检查器跨所有打开会话聚合** —— 初版否(用户当时选「仅当前会话」);**follow-up 按用户新要求已实现**(第七节)。未打开的后台自动化会话仍不在列。
4. **标题栏按钮入口** —— 否(用户已选「资源菜单加一行」)。语义最贴切,且标题栏不再加图标。

## 三、为什么这是最优(按四条判据)

- **性能**(forge-goal 第一约束):UI 线程零轮询、零锁、零 per-event 分配。worker 线程每事件一次字典查找 + 一次字符串赋值;`Tick` 每会话每秒一次(条目数 = 运行中的调用数),裁行只在「尾部变了」时发生且结果缓存;检查器关闭时定时器不存在。列表 `ListBox` 虚拟化,行数 = 运行中的调用 + 实时子 Agent。
- **依赖与 AOT**:零新包、零 P-Invoke、零反射;模板都是强类型 `x:DataType` 的 `DataTemplate`,视图由 `App.axaml` 既有模板实例化。`DispatcherTimer` 是 Avalonia 自带。
- **契约与可测性**:`Forge.Abstractions` 的 `AgentToolProgress` 已冻结(生产侧 W0 完成),本波只消费。可测点:通过私有 sink 反射驱动事件(既有 `SessionEventRoutingTests` 的做法)、`LiveToolViewModel`/`TextTail` 纯函数、检查器 VM 直接调 `Refresh()`、headless 视图渲染。
- **回滚成本**:本波新增文件为主;对既有文件的改动是可数的几处(`SessionWorkspace.cs` 的 partial + 5 处小接线、`SessionInstrumentsViewModel` 构造参数与 3 个成员、`SessionView.axaml` 一行菜单项、`MainWindowViewModel.cs` 两行、`CiChain.cs` 一处委托化、`StringTableTests` 一行前缀)。单独 revert 干净。

## 四、可配置项(§六·二 登记,不再新增)

| 项 | 默认 | 范围 | 读取点 |
|---|---|---|---|
| `InspectorLogTailLines` | 200 | 20–1000 | `SessionWorkspace` / `ResourceInspectorViewModel` 每次 tick 现读 |
| `InspectorRefreshMilliseconds` | 1000 | 250–10000 | 检查器建定时器时读,`PropertyChanged` 时改间隔 |

生产侧 16 KiB 上限是协议常量(`ProcessOutputTail` 的默认值),不设成偏好 —— 计划已注明「W8 的行数设置只能在此之内取」。

## 五、验收口径

- 构建:`dotnet build Forge.slnx`(全绿),AOT 分析器 0 警告。
- 测试(与本波改动相关):`Forge.App.Tests`(ViewModel + `Foundation` + `Localization` + `MinorRegressionTests` + `Workbench` 相关过滤),`Forge.IO.Tests` 的 `ProcessOutputTailTests`(生产侧未改,作回归)。
- 手测:会话里跑一条长命令 → 工具行出现实时尾部;「资源」菜单出现计数与入口 → 打开后看到 bash 行(耗时滚动、尾部滚动、停止按钮只停该调用,回合继续);子 Agent 行 → 停止只杀该子 Agent;切会话 → 分组重排,检查器不关;另开一个会话跑东西 → 出现第二组;设置页 `inspector` 分区改行数/间隔 → 下一次 tick 生效。

## 六、边界记录

- `Views/SessionView.axaml`(矩阵未把该文件分给任何波次)加一行菜单项:入口必须挂在「资源」按钮上(用户选定),而该按钮的菜单写在这个文件里。
- `ViewModels/Transcript.cs` 的 `TurnCardViewModel.AddTool` 改为返回刚建出的行(返回类型 + `return row`,共 2 行),让会话把运行态条目挂到行上 —— 该文件名义上归 W4b(聊天 Markdown),但这是与 Markdown 无关的签名扩展;合并时逐行复核。
- `Automations/CiChain.cs` 的 `Tail` 改为委托 `Services/TextTail.Keep`(与 W5 分支同一处改动、同一文本,git 可静默合并)。
- `Services/TextTail.cs` 与 W5 分支新增的同名文件内容一致(add/add 同文本,合并取任一)。
- 测试侧两处一行改动:`Localization/StringTableTests.cs` 的已知前缀表加 `resources`;`Foundation/SettingsPanelTests.cs` 的 `KnownSectionIds` 补 `SettingsSectionIds.Inspector`(该数组自称「每个预留 id 都在这里」,实际漏了它,不补则 W8 注册分区后该测试把 inspector 从导航里过滤掉而失败)。
- `Views/SubAgentView.axaml` 一行(停止按钮的 `IsVisible` 从 `IsLive` 改 `CanStop`):`SubAgentViewModel` 在 W8 的文件里,`CanStop` 是同一处状态的真实读法;只改绑定,不动布局。

## 七、follow-up:单调用停止 + 跨会话聚合 + 评审修正

### 1. 只停一条工具调用(Core 改动)

- `Forge.Core/Agents/ToolCallCancellations.cs`(**新**,internal):`ConcurrentDictionary<(AgentId, CallId), CancellationTokenSource>`。`Begin` 建一个链到回合令牌的 CTS 并 `TryAdd`(重复 id 仍拿到自己的源,只是不能单独停);`End` 用 `TryRemove(KeyValuePair)` 只摘自己那一项再 `Dispose`;`Cancel` 对未知/已取消返回 `false`,否则 `_ = CancelAsync()`(回调上线程池,UI 线程不等进程树终止),与 `End` 竞争时吞 `ObjectDisposedException` 返回 `false`。
- `AgentLoop.RunOneAsync`:在 `AgentToolStarted` **之前** `Begin`(行一出现就能停),工具拿 `callSource.Token`;`OperationCanceledException` 在回合令牌已取消时照旧抛,仅调用令牌取消时转成 `ToolResult(CancelledByUserResult, IsError: true)`;`finally` 里 `End`。转存与 `AgentToolFinished` 不变。根与所有子 Agent 共用同一个 `AgentLoop`,所以一份登记表覆盖整棵树。
- `AgentHost.CancelToolCall(agentId, callId)`(public,空 id 返回 `false`)、`RunningToolCallCount`(internal,测试查泄漏)。
- 命令工具:`ProcessRunner` 已在令牌触发时 `Kill(entireProcessTree: true)`,无需改动;`spawn_agent`:`SubAgentScheduler` 的 run 链到调用令牌,取消即子 Agent Stopped "parent cancelled"。
- 已写入 `Forge.Core/SPEC.md`「单个工具调用的取消」。

### 2. 跨所有打开会话聚合

- `IResourceInspectorSource`(`Title`、`CollectRunning(List<object>)`、UI 线程事件 `RunningResourcesChanged`)由 `SessionWorkspaceViewModel` 实现(`SessionWorkspace.Tools.cs`);`RaiseRunningResources` 顺带触发事件,`Dispose` 触发一次后把事件置空。
- `ResourceInspectorViewModel` 构造改为 `(collectSessions, activeSession, preferences, close, schedule, post)`:每次拉取按代号同步监听(新会话订阅、消失的退订并删组),平铺列表 = 每组一行 `ResourceInspectorGroupViewModel` 表头 + 该会话按开始时间排序的行,活动会话组在前;`Reconcile` 只做必要的 Remove/Move/Insert,行实例不重建;会话事件经 `Interlocked` 标志合并为一次 `Background` 派发;缓冲复用,稳态零自有分配。
- `MainWindowViewModel.Inspector.cs`:不再在换会话时关闭,改为 `RequestRefresh()`;`OpenInspectorCommand` 在没有任何会话时不可用。
- 视图:组表头模板(标题、「当前会话」标签、数量),工具行停止按钮绑 `CanStop`,请求后显示「正在停止…」。

### 3. 评审修正

- `LiveToolViewModel.ToggleCommand` 原为每次读取都 `new RelayCommand`(每次绑定读取一个新实例、一次分配),改为构造时建一次。
- 工具行停止原先调 `SessionWorkspace.Cancel`(停整个回合**并暂停排队消息**),与文案以外的行为都不符合单调用语义;改为单调用取消,`Core` 拒绝时不标「正在停止」。
- `CollectRunning` 读子 Agent 改为按下标(`Collection<T>` 的枚举器是装箱接口,每次拉取一个分配)。
- `TextTail` 注释补上检查器这个调用方;`SessionView.axaml` 菜单项注释改为「所有打开会话」。

### 4. 边界记录(follow-up)

- **Core**:`AgentLoop.cs`、`AgentHost.cs` 改动 + 新文件 `ToolCallCancellations.cs`(任务明确允许,已记 Core SPEC)。
- **`Forge.App/Agents/SessionRuntime.cs`(W1 的文件)**:加一个 `CancelToolCall(agentId, callId)` 转发方法(锁内取 host 后调用),没有它 App 无法够到 Core 的单调用取消。只加不改。
- 未改:W0 冻结文件(`App.axaml`、`MainWindow.axaml`、`MainWindowViewModel.cs`、`FeatureComposition.cs`、`AppPreferences.cs`、`SettingsSection.cs`)。其中两处注释已过时、按冻结规则未动:`MainWindowViewModel.cs` `Dispose` 里「The overlay reads one session's live work」、`AppPreferences.InspectorRefreshMilliseconds` 文档「refreshes its elapsed times」(实际只刷新输出尾部与行)。
