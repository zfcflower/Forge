---
id: forge-app
type: module-design
status: draft
title: Forge.App
parent: forge-architecture
---

# Forge.App

Forge 的 Native AOT 桌面主程序(Avalonia 12.1.x,仅桌面)。承载组合根与停靠式工作台:把 `forge-core`(Agent 循环、存储)、`forge-workspace`、`forge-mcp`、`forge-editor`、`forge-host-client`(插件/CodeMode 代理)与 `forge-approval` 串成一个进程。

## 边界

- Native AOT(`IsAotCompatible` + `PublishAot`),依赖闭包不含 Roslyn、`AssemblyLoadContext`、`Forge.Scripting`/`Forge.ScriptExecutor`;只用源生成 JSON(`SettingsJsonContext`、`WorkbenchJsonContext`);无 DI 容器/MVVM 包(手写 `INotifyPropertyChanged` + `ICommand`)。
- **不实现任何存储或 Git 管道。** 只经 `ISessionStore`/`ISessionCatalog`/`IProjectRegistry`/`ITodoStore`/`IUiStateStore`/`ITurnCheckpointStore`、`GitCli`、`WorktreeManager`、`SpecGraph`、`ReviewStore` 组合;`src/Forge.App` 中不得出现 `SqliteConnection`、`CREATE TABLE`、`write-tree`、`GIT_INDEX_FILE`。
- 不直接读审批数据库做判断(哈希经 `plugins.status` 取);为状态面用只读连接数 `plugin_approval` 的行数,进程里不持有任何可写审批连接,也不建表、不迁移。计数只在文件不存在(不打开、不创建文件/`-wal`/`-shm`)或没有 `plugin_approval` 表时返回 0;其余错误上抛,状态栏显示"不可用",不把损坏或锁住的库显示成没有插件。
- UI 线程不碰存储:Microsoft.Data.Sqlite 的 `*Async` 是同步执行的,所以所有存储/SQLite/文件调用(含归档、重命名、`ui_state` 写入、TODO 增删)都经后台助手,不在调度器上 await。
- 无 P/Invoke;进程归属是 `forge-extension-host` 的职责。App 只启动/重启 Host 子进程并保证退出时杀树等待。
- 工作台布局自写(`Workbench/`),不用任何停靠框架;原内化的 Dock 已移除(见 `task-workbench-rewrite`)。

## 组合根

构造顺序即依赖方向,拆除严格逆序:`AppPaths` → (审批数据库路径,不打开)→ `ExtensionHostSupervisor`(`--worker`/`--executor` 显式传路径)→ `WorkspaceServices` → `McpServices` → `EditorServices` → `ProviderSettings` + forge.db 上的 5 个存储 + `TurnCheckpointService` + `WorktreeService` → `AgentRuntimeManager`。审批库归 Host 所有,App 只拿到 `<host 数据目录>/approvals.db` 的路径。数据根默认 `%LOCALAPPDATA%/Forge`,`FORGE_DATA_DIR` 覆盖;常规工作区默认 `文档/Forge/General`,`FORGE_GENERAL_WORKSPACE` 覆盖。开发验证一律设置这两个变量,不写真实用户目录。

## 关键决策

**工作台布局。** 三个区域按角色命名,不按位置:项目区、主区、检视区。默认顺序 项目 | 主 | 检视,可以拖拽互换,内容跟着角色走。
- 项目区:顶部常规对话区,其下 项目 → worktree → 会话;归档会话收进各 worktree 的“历史 (n)”。
- 主区:主选项卡组(只放会话标签)+ 底部组(子 Agent 选项卡组,命令行以后也放这里)。
- 检视区:文件 panel(全部 | 已改动)在上,TODO | 评审 | 规格树 选项卡组在下。在文件 panel 里点文件,会在检视区靠主区一侧打开**预览组**(选项卡组,做文件预览与逐行审阅)。原中区的固定“文件”“改动”两个标签都移进预览组:
  - “全部”里单击复用预览选项卡,右键才新建。
  - “已改动”里点文件,打开固定的“改动”选项卡(按轮次 / 未提交改动,多文件连续 Diff),并定位到该文件。
  - 预览组的“铺满主区”按钮让它盖住整个主区,方便专注审阅。
- `WorkbenchLayout` 是唯一的布局状态所有者,不依赖 Avalonia。视图模型只通过它开关选项卡,不碰控件树。
- 预览列的宽度只由一个状态驱动:检视区“当前是否含预览列”(= 文件 panel 停靠且预览组有停靠的选项卡)变化时才借或还 `PreviewWidth`。打开、关闭、浮出最后一个预览、浮出文件 panel 都走这一处,不在各操作里各自加减,否则浮出期间的开关会让宽度漂移。

**会话运行。** 一个主会话一个 `AgentHost`(`AgentRuntimeManager` → `SessionRuntime`),各自取消源,可并发;关标签不取消运行;重开/取消归档走 `ResumeRootAsync`。在项目上新建会话 = 新 worktree(`SiblingOfRepository`:`<仓库>.worktrees/<slug>`,`forge/<slug>`);常规会话在 `scratch/<日期-标题>/`。运行时的释放点在:归档、标签已关且该回合结束、组合根拆除前(`MainWindowViewModel.Dispose` 先于 `AppComposition.DisposeAsync`)。检查点快照失败不把会话卡在“运行中”:记入会话通知,状态重置与 `TurnCompleted` 放在嵌套 `finally`。

**事件路由与渲染。** 按 `AgentEvent.AgentId` 分发到各自的轮次卡模型,子 Agent 文本不进主会话;文本增量在后台合并、每个调度周期 post 一次。流式增量收进 `TextSegmentViewModel` 段落(≤2048 字、段落边界收口),一次 flush 只重写未闭合段,避免整段回复反复复制。Diff 行、文件树、规格树各自展开成一个 `RangeObservableCollection` 行列表配 `ListBox`/`VirtualizingStackPanel`(展开 = 区间插入/移除),树/行视图模型、`FileIndex` 与规格图读取都在线程池构建、UI 线程一次 `ReplaceAll`。轮次卡头“第 N 轮 · 时间 · 耗时 · 输入/输出”:`AgentTurnCompleted` 是实例累计用量,卡上显示本轮差值;子 Agent 用 `AgentSubAgentFinished` 的本 run 用量;未上报一律显示“—”。

**子 Agent 区。** 每个运行/排队中的子 Agent 一个标签,标签底部唯一的状态栏(状态、任务、模型、用量、实时耗时、“停止” → `SubAgentControl.Stop`),无输入框;标签携带所属主会话、绝不跨会话。结束的按顺序收进标题栏末端“已结束 (n)”(20px 扁平按钮 + Flyout 列表),选中后从子会话存储经 `TranscriptReplay` 重放为只读标签。“已结束”列表也算内容:没有未浮动的标签但有已结束条目时,整块仍留在布局里(标记为 `TabGroupModel.KeepsVisibleWhenEmpty`,由视图模型按条目数设置);两者都没有时整块从布局移除,再出现时按原比例放回。会话底部“子 Agent · 收起/展开”开关(带运行计数)是手动收起入口,收起后本会话内不再自动弹出。

**轮次改动。** 每个主轮次开始/结束(含失败/取消)用 `GitCli.SnapshotTreeAsync` 取树哈希写 `turn_checkpoints`;轮次号从已存检查点续接,否则重开会话后新轮次的检查点会被“已完成轮次不覆盖”规则静默丢弃。非 git 目录跳过。

**布局快照自管。** 两份快照,都用源生成 JSON 存在 `ui_state`:
- `LayoutSnapshot`(v3)存 `layout`:区域顺序、宽度、自动隐藏状态,底部组高度与可见性,各区 panel 高度比例,预览组的开关与宽度,浮动窗口(装的对象 + 几何)。
- `OpenTabsSnapshot`(v2,增加预览选项卡路径)存 `open-tabs`。

恢复规则:
- 版本不符(含旧 Dock 时代的 v2)、缺 `version` 字段或解析失败,一律回落默认布局,不迁移。快照的 `Version` 缺省为 0,序列化时才写入当前版本,所以缺字段不会被当成当前版本。存储异常同样回落;取消照常抛出。
- 未知 id 丢弃;缺失项保持默认位置。
- 浮动窗口在它装的选项卡存在之后才应用;坐标落在所有屏幕之外时,移回主窗口所在屏幕。
- 检视区宽度存的是不含预览列的宽度:恢复时预览选项卡重新打开会再借一次 `PreviewWidth`,若按总宽存就会重复计入。
- 未放置过的浮动窗口不存坐标,恢复时居中于主窗口。
- 放大状态不持久化;切换选项卡会触发保存(预览组与工具组的活动选项卡要持久化)。

**主题。** 灰白为主体,护眼绿只做强调色;亮:背景 #F5F6F5 / 面板 #FFFFFF / 边框 #E3E6E3 / 文字 #2B2F2C / 强调 #3A8F5C;暗:#1E2120 / #262A28 / #343936 / #DDE3DF / #5FBF86;`RequestedThemeVariant=Default` 跟随系统,同时映射 Fluent 系统色。区域间分隔线 2px、panel 间 1px,都用边框令牌;线本身细,可拖热区 ≥6px。主按钮用单独的 `PrimaryButtonBackgroundBrush`(#2F7449 亮 / #5FBF86 暗),白字在 #3A8F5C 只有 3.99:1、在 #2F7449 有 5.65:1,达到 WCAG AA;强调令牌不变。基础按钮样式只作用于 Forge 自有样式类,不全局覆盖 `Button`。

**字符串与自动化。** 用户可见文案集中在 `ViewModels/Strings.cs`(含工作台标题栏按钮的提示);UI 自动化只认 `AutomationProperties.AutomationId`。

**降级。** 提供方未配置 / Host 未找到降级为明确的 UI 状态,绝不抛异常或伪造模型调用。审批:只读自动放行,`todo` 工具显式放行(写会话清单而非工作区);其余写/执行为 Ask,暂无审批对话框即等于拒绝,轮次卡显示“已拒绝”。

**树一律扁平虚拟化。** 项目树、文件树、规格树、Diff 都是扁平 `RangeObservableCollection` 行列表 + 虚拟化 `ListBox`;展开/折叠只对子树做区间插入/删除,刷新复用节点实例(展开与滚动存活);行模型负责缩进档数与引导线,视图不递归。后台计划只吃 UI 线程上快照好的不可变数据(会话运行集合、Agent 层级都归调度器所有)。

**树与图标。** 文件/项目树:每级 12 px 缩进 + 每级一格 1 px 竖引导线(行内 `Items`,行高 22 无纵向 padding 使相邻行首尾相接),展开箭头是几何 chevron(`TreeChevronGeometry`,折叠向右、展开 rotate 90° 向下),图标与文字同一 `VerticalAlignment=Center`。文件夹/文件用几何图标(`TreeIcons.Folder`/`File`,无图标包)。规格树仍用旧文字箭头(需求未点名)。

**Agent 树。** 会话下有子 Agent 时,在会话行插入“主 Agent → 子 Agent → 孙 Agent”层级:按 `AgentSubAgentStarted` 的父 `AgentId` 链接嵌套;按 `Kind` 选图标(主 Agent 星形、Scout 放大镜、Worker 锤子、Plan 三行、Reviewer 对勾、未知回落菱形),行尾运行状态点 + 文案;只缩进不画引导线,共用 chevron。子 Agent 仍在中下标签区。重启后层级靠递归查询 `parent_session_id` 还原(直接子的父是主 Agent 行,其余以祖先会话 id 为父);存储没有运行状态列,状态从最后几条存储消息推断(`Cancelled.` 结果=已停止、错误结果或悬空调用=失败、最终文本=完成),推不出显示中性"已结束",不把未知当成完成。

**已改动徽标。** 与"改动"面板同语义(对 HEAD 的整树差异、含未跟踪、路径按会话目录裁剪,`scratch/…` 下也有计数);二进制只显示状态字母,绝不显示 `+0 -0`;watcher 700 ms 一跳,补丁按"状态 + 每文件大小/修改时间"指纹缓存,无变化不重算。

**会话标签与归档。** 会话标签按会话 id 唯一:重开同一会话复用并重绑已有标签;归档先关闭其标签再释放运行时与视图模型。

**区域宽度分配。** 每区有最小宽度:项目/检视 200px、主 360px。拖动区域分隔条时:
- 分隔条另一侧紧邻的区域吸收全部增量。
- 被挤压的那一侧,主区先缩到最小宽度,其余区域再按离分隔条由近到远依次缩到最小宽度。
- 边栏缩到最小宽度后继续拖,越过最小宽度的一半(`CollapseOvershoot` = 0.5)才自动隐藏;这段粘滞是为了防止贴着最小宽度拖动时误隐藏。主区永不隐藏。
- 空间够但边栏总宽过大时(例如第一次 arrange 之前就打开了预览,检视区已经加上 `PreviewWidth`),也同样先把边栏按顺序缩到最小、再隐藏非 protected 的边栏,主区最后拿到 ≥360px;`protected` 区域只缩不藏。
- 窗口变窄时先缩主区,再缩检视区、项目区,仍不够就依次自动隐藏;变宽的增量给主区。

计算由纯函数 `RegionWidthSolver` 完成,拖动路径不分配内存,松手才写快照。不用 `GridSplitter`,因为它只能调相邻两列。零分配要算上挂着 UI 的整条同步事件链:`ObservableObject` 按属性名缓存 `PropertyChangedEventArgs`;控件只对影响自身的属性名刷新(宽度类属性只触发 arrange,不重算标题栏)。

**Panel 标题栏。** 左侧是标题,选项卡组改为选项卡条(只在本组内拖动排序,不跨组)。右侧图标按钮按能力显示:
- 钉住/自动隐藏:边栏与底部组有,作用于整个区域或整个底部组。
- 放大/还原:所在区域可见 panel ≥2 时显示。放大时该 panel 占满区域,其他 panel 暂时隐藏。
- 新窗口/停靠:只给可浮出的对象。
- 其他按需工具,如子 Agent 的“已结束 (n)”:标题栏里是 20px 扁平按钮(“已结束 (n)” + 小 chevron),点开 Flyout 列出已结束的子 Agent;空列表时按钮禁用。

不放菜单和关闭按钮;选项卡自带关闭,右键菜单每个选项卡一份实例。

**自动隐藏。** 以区域为单位。自动隐藏的边栏收成它所在一侧窗口边缘的按钮条,两个边栏在同一侧时并排。点按钮条弹出抽屉,覆盖在主区上方,点外部或按 Esc 收起。底部组收成主区底边的按钮条。

**区域置换。** 按住任一 panel 标题栏的空白处横向拖,出现三个落点,放下后两区互换,宽度跟着区域走。自动隐藏中的区域不能拖。按钮条位置、抽屉方向、预览组的“靠主区一侧”都由“角色 + 当前顺序”推导。

**浮动窗口。** 只允许浮出选项卡(任意组的任意选项卡,含 TODO/评审/规格)和文件 panel;其他 panel 不能浮出。
- 文件 panel 浮出时,预览组随它进同一个窗口(仍在其左侧),回位时一起回。
- 一个对象一个 `FloatingPanelWindow`,用同一视图模型重新套数据模板(视图内的滚动等临时状态不保留)。
- 浮出的选项卡仍留在原组的集合里,只标记浮动、不显示,所以点“停靠”或关窗时天然回到原槽位,不用另记“家”;组里活动选项卡永远不是浮动的。
- 浮动默认尺寸 900×640(文件 panel 320×420)。文件 panel 的浮动窗口标题栏同样带它的工具按钮。最小化期间不写回几何(否则存下 -32000 坐标)。
- 关停:主窗口确认真正关闭后才置 `IsShuttingDown`(关闭被取消就复位),之后关浮动窗口不回位,浮动项照常进快照。

**窗口顶部。** `WindowDecorations="BorderOnly"`:保留系统可缩放边框、去掉系统标题栏,标题行由 App 自绘,故窗口底色 = `SurfaceBackgroundBrush`、无独立标题栏底色。标题行 40 px:左 Forge + 会话标题,中 380×26 占位搜索框(无行为),右 设置/最小化/最大化/关闭(设置紧挨最小化左侧);提供方状态移到状态栏。搜索框背景比窗口亮一档、边框 1 px 且比窗口暗一档(亮/暗各一对令牌)。标题行可拖动、双击最大化。

## 未决

- 预览组与评审面板只读;逐行审阅的行评论交互未做;命令行区(底部组)未做;编辑器内核(LSP/TextMate/补全/行评论)、审批对话框、声明式插件面板渲染未做。
- 搜索框暂无行为(占位)。
- 规格树面板仍用旧文字箭头(待与其他树统一)。
- `TurnCheckpointService` 是否上移到 `forge-workspace`(目前只做编排)未定。
- Worker 资源限制受威胁模型决策门控,不在此模块实现。
- 已知缺陷:`WorkspaceServices` 不释放 `reviews.db` 句柄。
