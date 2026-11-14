# W7 实现说明(计划会话面板)

对应 `2026-11-13-thinkrail-gap-plan.md` 的 W7 波次(缺口 #3 的剩余一半、矩阵里 W7 那一行)。本文是开工前的实现说明 + 落地后的验收记录;数值是实测值。

## 一、方案

### 1. 一层一张表

| 层 | 文件 | 做法 |
|---|---|---|
| 契约 | `Forge.Abstractions/Workbench.cs` | 新增 `PlanRevision(SessionId, Text, ReplacedAt)`;`ITodoStore.ListPlanRevisionsAsync(sessionId, limit, ct)` **带默认实现**(返回空),所以既有的测试替身与旧实现一行不用改。`SetPlanSummaryAsync` 的注释写明“替换旧文本时同事务记一条修订”。`SessionLifecycle.cs` 的 `CopyTodosAndPlans` 注释补上“并复制修订历史”。 |
| 存储 | `Core/Storage/StorageSchema.cs`、`SqliteTodoStore.cs`、`SqliteSessionStore.Lifecycle.cs` | schema **10 → 11**:新表 `plan_summary_revisions(id INTEGER PK AUTOINCREMENT, session_id, text, replaced_at)` + 索引 `ix_plan_summary_revisions_session(session_id, id DESC)`;登记进 `TimestampColumns`,迁移步骤里重装写守卫(与 v5–v10 同一做法);**无回填**(本版之前的摘要只有当前文本)。`SetPlanSummaryAsync` 在**同一个 `BEGIN IMMEDIATE` 事务**里先读旧文本,不等则插一条修订并按 `id DESC` 裁到 `PlanRevisionLimit = 50`;同文本不记。`ListPlanRevisionsAsync` 倒序读、`limit` 夹到 1..50,行解析走新的 `ReadRevision(reader)`(与 `ReadItem`/`ReadGroup` 同形,复用 `StorageDb.ReadText`/`ParseCanonicalTimestamp`)。复制会话(接手/分叉带上下文)按 `ORDER BY id` 复制修订,`PurgeOp` 的表清单加一行。 |
| 面板状态机 | `App/ViewModels/PlanPanelViewModel.cs`、`PlanPanelRows.cs`、`PlanPanelHost.cs` | 面板= 摘要 + 一张扁平步骤表 + 右栏(下一步/三个出口/修订历史/状态行)。四条读(摘要、条目、分组、修订)在 `BackgroundRead` 上跑、回 UI 线程应用,世代号丢弃被顶掉的读,**读中点再来 `Changed` 只排队一次补读**。行模型只有一种(`PlanRowViewModel`)带分组/步骤两种形状,刷新按 key 原地 patch(改的字段才发通知、顺序变化用 `Move`),所以 agent 干活期间列表不滚回顶部。只读:唯一动作是把某一步交回 agent(`Handoff`),不写存储。 |
| 视图 | `App/Views/PlanPanelView.axaml(.cs)` | 遮罩(点空白关、点卡片不关)+ 居中卡片;左列摘要(Markdown,`MarkdownRenderer`)在 `ScrollViewer` 里、下面是虚拟化 `ListBox`(扁平行、两种形状一个模板);右栏下一步卡片 + 三个动作 + 修订列表;Esc 关;焦点落在关闭按钮上。导出走 `StorageProvider.SaveFilePickerAsync`(窗口归视图)。 |
| 接线 | `App/ViewModels/MainWindowViewModel.Plan.cs`、`MainWindowViewModel.cs`(各 2 行)、`MainWindow.axaml`(1 个按钮)、`Resources/Icons.axaml`(1 个图标)、`Resources/MarkdownStyles.axaml`(新) | 壳实现 `IPlanPanelHost`:给面板会话(id/标题/是否只读)、跑 `GitSync.ComposePullRequestCommand`(与提交目标行同一条路径,正文已是计划)、把交接消息写进该会话 composer 后发送或排队、关闭面板。面板打开时创建、关闭时 `Dispose`;切会话自动关。标题行搜索按钮旁加计划按钮(`plan-open`),无会话时禁用。 |
| 样式复用 | `Resources/MarkdownStyles.axaml` + `Changes/Markdown/MarkdownDiffView.axaml` | 把 `md-*` 的**呈现**样式(文本/标题/列表/引用/代码/表格)从 diff 视图搬到共享 `Styles`,diff 视图只留自己的框(变更条、折叠行、间隔),两份视图各自 `StyleInclude`。 |
| 文案 | `App/Localization/Strings.Plan.cs`(新,`plan.*`) | 约 45 个键;参数化文本走 `LocText`,状态文案复用 TODO 面板的 `todo.status.*` 与 `{0}/{1}`(`todo.groupProgress`),不重复造。 |

### 2. 面板的交互口径

- **下一步**:计划顺序里第一个 `in_progress`,否则第一个 `pending`(`paused` 不算);全做完时说“没有未完成的步骤”,此时自然的下一步是右栏的开 PR。列表里那一步也带 `下一步` 小片 + 强调色左沿。
- **交接**:消息里带步骤标题、**步骤 id**、组名、note(`Finish step "<title>" (todo id <id>, group "<g>"): …`),并明确要求做完后用 `todo` 标记 done 并写 summary/verification。回合进行中排队;空闲直接发;输入框里有用户未发送的文字时**不覆盖**,报一行提示;只读/交接中/压缩或门控不可发时报“不可用”并把刚写进去的草稿清掉(不留半状态)。
- **修订历史**:新→旧,时间 + 首行预览 + 每行复制;点行把该版本显示在左列(顶部横幅写明被替换时刻 + 回到当前摘要),被存储裁掉的版本预览自行退出。
- **三个出口**:复制 Markdown / 导出 `<会话标题>-plan.md` / 从计划开 PR;三者用的是**同一份文本** `PrBodyRenderer.Render(摘要, 步骤, 分组)`——也就是 `pr_create` 默认正文与打开 PR 对话框的预填正文。

### 3. 性能要点

- UI 线程只做:应用读好的结果、patch 行、重解文案。四条 SQLite 读全在 `Task.Run`(复用 `BackgroundRead`),`ConfigureAwait(true)` 回 UI 线程应用。
- 摘要的 Markdown 只在**文本变化**时重新解析+建控件树(视图侧比较上一次的文本);一次刷新不重建列表容器。
- 步骤列表用 `ListBox`(虚拟化);修订列表上限 50(`PlanRevisionLimit`),是 `ItemsControl` 即可。
- 读并发合并:一次只有一个读在跑,期间的 `Changed` 合并成一次补读;`Dispose` 之后不再被唤醒。

## 二、被否的备选

| 决定 | 被否的备选 | 为什么输 |
|---|---|---|
| 修订历史进新表(`v11`) | 复用 `plan_summaries` 一行、在 JSON 列里存历史数组 | 每次读都要解析整份历史;裁剪要在 C# 里做,写放大;而且 `plan_summaries` 的语义(当前摘要)会被搞混。新表一行一版本,读只要 `ORDER BY id DESC LIMIT n`,裁剪一条 SQL。 |
| 修订记录**被替换掉的旧文本** | 记录每次写入的新文本(含当前) | 当前文本已经在 `plan_summaries` 里,再存一份就有两个真相;而且“历史”列表会首条重复当前。 |
| 同事务内记修订+裁剪 | 写完摘要再补一次事务 | 两步之间崩溃会丢掉一个版本;而且两次写锁竞争。同事务一条路径,失败就整体回滚。 |
| 面板**只读** + 交接按钮 | 面板里能勾选/改状态、能恢复旧版本 | 状态只由 agent 改是既有契约(done→`StepCommitter` 自动提交),面板写状态会出现“人工 done 但没提交”的新含义;恢复旧版本等于新增一条存储写路径。交接把动作交回 agent,语义不新增。 |
| 交接写 composer 再发送 | 直接调内部 `RunTurnAsync` | 那是 `SessionWorkspace` 的私有实现,而且绕过排队/门控/门卫(审批、压缩、交接中)。写 draft 再走 `SendCommand`/`QueueCommand` 与用户按发送完全同一条路。 |
| 有未发送草稿时**拒绝**并提示 | 覆盖草稿 / 把消息排到队列里 | 覆盖会丢用户输入;空闲时队列不接消息(`QueueCommand` 只在回合进行中可用),硬塞会绕过它自己的可用性判断。 |
| 一个行模型两种形状 + `ListBox.ItemTemplate` | 两个行类型 + `App.axaml` 里注册隐式 `DataTemplate` | `App.axaml` 是 W0 冻结的共享文件,各波都不再改;`UserControl.Resources` 在 Avalonia 12 里不接受无 `x:Key` 的 `DataTemplate`,而列表是异构的。一个行模型 + 一个本地模板,职责都在本波文件里。 |
| 行按 key 原地 patch | 每次刷新重建整表 | 重建等于 `Reset`,`ListBox` 会滚回顶部;agent 每标记一步就跳一次滚动的面板不能用。 |
| 摘要用共享 `md-*` 样式 | 面板自己再写一份 Markdown 样式 | 两份样式必然漂移,且都是 20+ 条重复;搬到 `Resources/MarkdownStyles.axaml` 后 diff 预览与计划面板同一套排版。 |
| 导出走保存对话框(视图侧) | 固定写到工作区某路径 | 用户要能选位置;写进工作区还会脏工作树,和步骤提交的 `add -A` 撞车。 |
| 开 PR 复用 `ComposePullRequestCommand` | 面板自己拼 `PullRequestDraft` 再调平台 | 标题/正文/草稿开关的对话框与正文生成已经在那条路径上,复制一遍就是第二份真相。 |

## 三、为什么最优(按四条判据)

- **性能**:见上“性能要点”。没有热路径加锁;每会话 50 条上限让存储与列表都有界;摘要重渲只在文本变化时发生;行 patch 不产生 `Reset`。
- **依赖与 AOT**:零新包、零 P/Invoke、零 Roslyn。Markdown 用既有渲染器;`App.axaml`/`FeatureComposition`/`AppPreferences` 一行没动(没有新设置项,见下节),AOT 分析器 0 警告。
- **契约与可测性**:新增的唯一契约是 `PlanRevision` + 一个带默认实现的读方法(旧替身不破);面板的一切写动作经 `IPlanPanelHost` 出界面,所以 `PlanPanelViewModel` 可以在没有窗口的测试里驱动(存储替身 + host 替身);存储侧的语义(same-text 不记、裁剪、复制、清理、守卫)全部由 Core 测试证明。
- **回滚成本**:本波只碰自己的文件 + 4 个明确的小接缝(`MainWindowViewModel.cs` 2 行、`MainWindow.axaml` 1 个按钮、`Icons.axaml` 1 个图标、`MarkdownDiffView.axaml` 换成共享样式);单独 revert 一次干净。schema 升版有 v11 一步,降级被拒绝(既有策略),但 v11 只**新增**一张表,旧构建读 11 库会在 `ThrowIfNewer` 停下——与过去每次升版一致。

## 四、可配置项与内部常量

第六·二节的登记表里 **W7 没有条目**。本波新增的数值及归类:

| 数值 | 值 | 归类 | 说明 |
|---|---|---|---|
| `SqliteTodoStore.PlanRevisionLimit` | 50 | 内部实现细节(存储上界) | 修订历史的保留条数。它不是用户会想调的量,而是一个防无界增长的存储上界:面板显示的就是“能拿到的全部”,改小会让历史突然消失,改大只是多占几 KB。SPEC 里写明它是常量。 |
| `PlanPanelViewModel.RevisionTake` | 50 | 同上 | 面板一次问存储要的条数,等于存储上界;读多没有意义。 |
| `PlanPanelViewModel.SuggestedNameLength` | 60 | 内部细节 | 导出建议文件名的截断长度(文件名上限的保守值)。 |
| `PlanRowViewModel.ShortCommitLength` | 7 | 内部细节(与 PR 正文一致) | sha 显示长度,和 `PrBodyRenderer`、TODO 行同一口径。 |

## 五、与计划的偏差(以及为什么)

1. **`StorageSchema.cs` 属于 W0 的“一次升版”,但 W0a/W0b 落地时没有加计划修订表**(`plan_summaries` 仍是单行 upsert)。W7 要修订历史就必须有那张表,所以本波自己加了 v11。为避免与其它波再撞,这次只动了这一张表的 DDL + 一个迁移步骤;**没有**顺手做“工具结果多块”之类 W0 已完成的表示。
2. **`App.axaml` 没有动**:面板级的 `DataTemplate` 由 W0 预注册,行级模板放在视图自己的 `ItemTemplate` 里(见上表“被否的备选”)。
3. **`PurgeOp` 而不是 `SoftDeleteOp`**:计划文档写“清理(`SoftDeleteOp`)”的表清单,实际删行的清单在 `PurgeOp`;改的是实际存在的那张。
4. **`Forge.Abstractions` 加了一个成员**:`PlanRevision` 与带默认实现的 `ITodoStore.ListPlanRevisionsAsync`。执行口径写“`Forge.Abstractions` 的契约定稿只在 W0 一次”,W0 没有为 W7 预留这个读方法,而修订历史必须有人读。做法取带默认实现的最小增量(全仓唯一实现 `SqliteTodoStore` 之外所有替身零改动),不预埋别的波次要用的东西;列在这里作为偏差记录。
5. **`check-all` 不做**:缺口文档 #3 的“缺”列表里有它,但本波“已定”口径是**只读 + 交接**:面板不能把步骤标 done(那是 agent 与 `StepCommitter` 的语义),因此“全部勾选”没有不新增写路径的实现方式。等价的用户价值由“下一步 + 交接 + 进度读数(`3/7 已完成`)”承担。
6. **顺手纠正两处既有文档漂移**:`Forge.Core/SPEC.md` 的时间戳守卫计数(原文“9 张表 11 个列”,实为 12/19);`Forge.Abstractions/SessionLifecycle.cs` 的复制说明补上修订历史。
7. **基于合并后的 `main`(`0d5e1b3` W0 收尾)重跑**:rebase 无冲突;W0 的收尾只动 `App.axaml`(设置分区模板)、`AppComposition`(通知权限槽位)、`CopyTextButton` 的异常范围与两份文档,与 W7 的路径无重叠。重跑时`PlanPanelShellTests` 的一个真实回合用例在 11 分钟的整套里超时(单独跑 7 秒绿),已把那三处等待的边界从泵默认的 2 秒放宽到 30 秒(仍是硬上限)。

## 六、验收

### 跑过的命令(主工作树)

| 命令 | 结果 |
|---|---|
| `dotnet build src/Forge.App/Forge.App.csproj` | 成功,0 警告 0 错误 |
| `dotnet test tests/Forge.App.Tests … --filter "FullyQualifiedName~Localization\|FullyQualifiedName~IconGeometry"` | 124 通过 0 失败 |
| `dotnet test tests/Forge.Core.Tests … --filter "FullyQualifiedName~Storage"` | 398 通过 0 失败 |
| `dotnet test tests/Forge.Core.Tests … --filter "FullyQualifiedName~SessionLifecycle\|FullyQualifiedName~Handover"` | 40 通过 0 失败 |
| `dotnet test tests/Forge.App.Tests … --filter "FullyQualifiedName~PlanPanel"` | 58 通过 0 失败(本波新增:43 个 VM 用例、6 个 headless 视图用例、9 个壳侧用例,含评审后补的 19 个覆盖缺口用例) |
| `dotnet test tests/Forge.App.Tests … --filter "FullyQualifiedName~Markdown"` | 49 通过 0 失败(共享 `md-*` 样式搬家后 diff 预览照旧) |
| `dotnet test tests/Forge.App.Tests … --filter "FullyQualifiedName~PlanPanel\|FullyQualifiedName~Workbench\|FullyQualifiedName~Localization\|FullyQualifiedName~MinorRegression"` | 764 通过 0 失败。机器上有两个子会话在并行构建/跑测试时,同一过滤曾出现 1–2 个**偶发**失败(`Window_header_replaces_the_search_box…`、`Window_header_uses_the_same_chrome_colour_as_a_panel_title_bar`、`MenuShellTests.Changing_the_start_point_while_listing…`,每次配对都不同、单独跑与按命名空间跑全绿);机器空下来后本改动(764/764)与 `main` 基线(706/706)各自跑同一过滤都全绿,失败随之消失 |
| `dotnet build Forge.slnx` | 成功,0 警告 0 错误 |

### App 侧计划测试(两个独立测试会话写的,58 例)

- `PlanPanelViewModelTests`(43):行结构(分组顺序、散项落回、缺分组落回、组行进度与派生状态)、下一步(进行中优先、待办兜底、paused 跳过、全 done 为 null)、就地更新(`Assert.Same` + 只通知改动字段 + 行移动保实例)、摘要与时间戳、修订(倒序/计数/预览/返回/被裁剪自动退出/首行预览与 160 字符截断/复制与时间戳)、交接(消息含 id 与标题、四种 `PlanHandoff` 的文案与失败位、done 不动作、只读不调 host)、导出(内容 == `PrBodyRenderer.Render`、失败报状态行、建议文件名清洗与 60 字符截断)、开 PR(委托 host、随 host 可用性变化)、刷新合并(读中两次请求只补读一次、`Changed` 合并、Dispose 后不再读、慢读在 Dispose 后不落地)、语言切换只重解文案不重读存储、`RevisionTake == SqliteTodoStore.PlanRevisionLimit`、无会话。
- `PlanPanelViewHeadlessTests`(6):面板加载 + 7 个自动化 id + 摘要**真的**渲染成 `md-h2`/`md-p` 控件、同文本刷新不重建控件树(换文本会重建)、Esc 关、点遮罩关而点卡片不关、点修订行进预览、步骤行三行证据且交接按钮只出现在未完成行。
- `PlanPanelShellTests`(9):无会话时命令禁用、开关两次、切会话自动关、`HandoffToAgent` 五态(空闲发送 / 回合中排队 / 草稿阻塞且不覆盖 / 只读不可用 / 交接中不可用且不动草稿),以及壳 + 面板集成时不可用的状态行文案是 `plan.handoff.unavailable`。

### 未覆盖(记录在案)

- 两个读“以相反顺序完成”这条已不可能通过公开 API 构造(`RefreshAsync` 的 `refreshing` 守卫让读严格串行);其可观测等价面被覆盖:读中请求只补读一次、Dispose 后 `Changed` 不再读、慢读在 Dispose 后不落地。
- 壳侧“旧面板释放后不再被存储唤醒”无法从外部触发真实 `SqliteTodoStore.Changed`,同语义在 VM 层覆盖。
- “打开 PR 的对话框正文以计划开头”是壳/视图层面的手测项(三条线路共用 `GitSyncViewModel.ComposePullRequestCommand`,正文由 `DescribePullRequestAsync` 给出,已列在手测项里)。

## 七、合并 `main`(W0/W1/W2)后的评审修正

合并冲突两处:`MainWindowViewModel.Dispose` 的释放行(两边都保留)与 `StringTableTests.KnownPrefixes`(取并集,`plan` 追加在末尾)。`main` 的 schema 仍是 v10,本波的 v11 不需要改号。

| 问题 | 修正 |
|---|---|
| 交接只看输入框文字:有附件而无文字时,发送会把用户还没发的附件夹带进交接消息 | `HandoffToAgent` 把 `Composer.HasAttachments` 也当作未发送草稿(`BlockedByDraft`),提示文案改为“文字或附件” |
| 面板的“从计划开 PR”可用性只在存储刷新时重算,推送完成/PR 出现后按钮不跟;且只看 `CanComposePullRequest`,与实际执行的 `CanExecute`(含 git 行忙)不一致 | 宿主改用 `ComposePullRequestCommand.CanExecute`,面板打开期间订阅它的 `CanExecuteChanged`,关闭(含窗口释放)时退订 |
| 读失败的状态行在下一次读成功后仍留着 | 读失败单独记一位,下一次成功应用时撤掉;交接/导出回执不受影响 |
| 修订列表按 `ReplacedAt` 再排序,而比较按存储顺序:时钟回拨时每次刷新都清空重建 | 以存储顺序(按记录先后)为准 |
| 修订计数 `1 versions` | 改为 `plan.revisions.count.one/.other` 复数键 |

补的测试:VM 两例(读恢复撤掉失败但保留交接回执;修订保持存储顺序且历史不变时不重建、单复数)、壳两例(附件阻止交接且附件保留;PR 门随 git 行变化、关面板后退订)、Core 两例(v11 步骤重跑保留数据;20 路并发替换每条旧文本恰好记一次)。
