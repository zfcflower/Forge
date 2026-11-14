# W14 交付记录:CodeMode 工具面扩展

对应 `2026-11-13-thinkrail-gap-plan.md` 的 W14 一行(缺口核查里的 CodeMode 一项不在 14 项表内,是本轮新增的并行波)。
分支 `w14-plan`,与 W11/W13 不重叠:本波不碰 `Changes/Markdown/**`、`Views/Timeline*`、`ViewModels/TimelineNodeViewModel*`、
`SessionView.axaml` 的转录区与 `App.axaml` 里转录条目的模板。

## 一、本波要解决的问题

`run_code`(CodeMode)早已存在:一段 C# 脚本在 Extension Host 的 worker 里跑,脚本用 `Tools.Call(...)` 调主程序的工具,
一次往返换回整条链的结果,`Task.WhenAll` 可以并行扇出。但**脚本能看到的工具面只有扩展宿主的插件工具**:
`SessionRuntime.CollectExternalTools` 把 `SnapshotToolRegistry(client.Tools)` 交给代理,文件工具、web 工具与 MCP 工具
都不在里面。而并行扇出最值钱的恰恰是 `read`/`grep`/`find`/`ls` 这些(读二十个文件、在二十个目录里找同一处符号)。

第二件事是**提示词成本**:`run_code` 的描述把每个可调用工具的「名字 + 副作用 + 一行描述 + 完整 JSON schema」都印在
里面,工具一多,每回合都要付这一大段。W14 把它改成:描述只列名字,细节由脚本在运行期按名查询。

## 二、方案(按文件)

### 1. 脚本侧按名查询(`Forge.ScriptRuntime/ScriptToolsFacade.cs`)

`ToolsFacade` 内部从 `HashSet<string>` 换成 `Dictionary<string, ToolDeclaration>`(同一份元数据,一次构造),新增两个
只读方法:

- `Tools.List()` → 本次运行可调用的名字,序数排序。
- `Tools.Describe(name)` → `name [read-only|write|execute] 描述\nargs=<schema>`,未知名字抛 `ToolCallException`
  (与 `Call` 的分类一致,脚本可以 catch)。

两者都只读本次运行随 `code.execute` 送来的 `ToolDeclaration`,不发 IPC、不解析工具、不改变可调用集合。`run_code`
自身仍被排除在两处(构造时不入表、调用时直接拒)。

### 2. 描述改形(`Forge.HostClient/RunCodeProxyTool.cs`)

`RunCodeDescription.Build` 不再逐工具印描述与 schema,改为:

- 表面表里增加 `Tools.List()` 与 `Tools.Describe("tool_name")` 两行;
- 末尾一行 `Tools (N), sorted by name; Tools.Describe("name") returns one tool's description and argument schema:`,
  下面是按 96 列折行的名字列表(只有名字)。

工具名列表保留是有意的:模型要在一次规划里判断「有没有 `grep` 可以扇出」,名字列表只花几十个字符;schema 才是大头。

### 3. 预算活读(`Forge.HostClient/IExtensionContributionClient.cs` + 代理)

`RunCodeProxyOptions` 增加 `ExecutionProvider`(可空 `Func<CodeExecutionOptions>`):读取点只有两处(读 `Definition`、
执行入口),`Execution` 退化成「提供者为空时的固定值」。`RunCodeToolSurface.Fingerprint` 把描述里印出的那几个数字
(默认/最大超时、并发上限、返回结果上限、内部结果上限、日志上限)一起算进哈希;`MaxScriptChars` 不进——描述不印它,只在本地的参数校验里用,所以数字变了描述会重建,
提示里的边界不会与实际执行的边界脱节。

### 4. 候选工具面与可见名单(`Forge.App/Agents/CodeModeTools.cs` + `CodeModeToolRegistry.cs` + `SessionRuntime.CollectExternalTools`)

候选的判定抽成 `CodeModeTools.Collect(pluginTools, fileTools, webTools, mcpTools)`:`CollectExternalTools` 是唯一调用者,但它本身需要一个活的 `ExtensionHostClient` 才能跑,规则放在那里就只能手工测;拆出来之后四路来源直接传进去就能验证(经同一次遍历拿到的四个来源,仍然只有一处判定):

| 来源 | 进脚本面的规则 |
|---|---|
| 扩展宿主插件工具(`client.Tools`) | 全部(与 W14 之前一致:插件声明什么就是它的契约) |
| `services.FileTools` | `SideEffect == ReadOnly` → 即 `read`/`ls`/`find`/`grep` |
| `services.WebTools()` | `SideEffect == ReadOnly` → 即 `web_fetch`/`web_search` |
| MCP | `McpToolAdapter` 且 `SideEffect == ReadOnly` → 即服务端 `readOnlyTools` 列入的那些 |

**判据只有一条:只读。** 这不是新发明的规则,而是审批策略已经在用的那一条 `DefaultApprovalPolicy.AutoAllowReadOnly`;
于是「脚本能调的工具」与「本来就不需要审批卡的工具」是同一个集合,`write`/`edit`/`bash`、git/PR 写工具、扩展编写
工具一律不在候选里——脚本既调不到也**看不到**(名字都不在 `Tools.List()` 里)。

两个刻意不进的:

- `mcp_tool_search`:不是 `McpToolAdapter`,而且它的存在意义是替提示词省 token,脚本有 `Tools.List`/`Describe` 不需要它;
- `read_artifact` 与检索/子 Agent/提问类 Core 工具:它们由 `AgentHost` 在内部注册,`CollectExternalTools` 里没有
  第二份实例。为了把它们塞进脚本面而复制一份同名工具,会让同一个名字在两份定义上分叉(宿主注册的那份与脚本侧那份
  可以由不同版本/不同选项构造),这正是 W14 要避免的“两个推导点”。要破例的话应该给 `AgentHostOptions` 加一个
  “额外暴露给脚本的内置工具列表”,那是另一个设计;本波不做。

可见名单(`CodeModeToolRegistry`)是**候选之上的收窄**,不是补充:空白 = 全部候选,`name` 精确匹配,`name*` 前缀
通配(用来放行整台 MCP 服务器,如 `github__*`),匹配忽略大小写。过滤在 `TryGet` 与 `Tools` 里**每次现读设置**,所以
收窄不重发布外部工具集、注册表实例不变、也不等下一回合。代价说清:`run_code` 描述末尾的名字列表会跟着变,
所以该工具定义的提示词缓存照样失效——“名单变了”与“缓存还在”本来就不可兼得(评审指出,已改掉原先“不破缓存”的说法)。

`run_code` 的存在条件从「有插件工具」改成「候选集合非空」。后果(有意):没装插件的会话现在也有 `run_code`,这正是
W14 的意义——它扇出的本来就不是插件工具。代价是每回合多一份 `run_code` 定义(改形后约一千字符)。

### 5. 设置项与界面(`src/Forge.App`)

四项进 `AppPreferences`(见计划文档 §6.2 新登记的一行),界面在既有 `tools` 分区顶部新增一段「脚本工具(run_code)」:

| 项 | 属性 | 范围 | 默认 |
|---|---|---|---|
| 内部调用并发上限 | `CodeScriptMaxConcurrentCalls` | 1–32 | 8(与 `CodeExecutionOptions` 的默认一致) |
| 返回结果字符上限 | `CodeScriptResultChars` | 1000–400000 | 40000(同上) |
| 单次内部结果字符上限 | `CodeScriptInnerResultChars` | 500–400000 | 20000(同上) |
| 可见工具名单 | `CodeScriptTools` | 文本 | 空白(=全部候选) |

`tools` 分区的内联模板从 `App.axaml` 抽到 `Views/ToolsSettingsSectionView.axaml`(只把原内容搬过去,行为不变),新控件
加在里面;运行期接线只有 `AppComposition.Create()` 里的一行 `runtimeServices.CodeMode = () => CodeModeSettings.From(...)`。

文案表 `Localization/Strings.CodeMode.cs`(`codeMode.*`,10 个键)照其它表的形状写(常量 + 每个键一个
`public static string` 访问器 + `[ModuleInitializer]` 注册),但**视图是按 `Loc` 键名直接绑的**(与本仓所有设置分区
一致,如 `ResourceInspectorSettingsSectionView`),所以这十个访问器目前没有 C# 调用点。它们是表的类型化 API、不影响
`KeyRegistryTests`,保留是有意的;删掉它们会变成“只有常量表”的孤例,以后 C# 里要用时又得补回去。

## 三、被否的备选

1. **让脚本注册表放全部工具,靠审批卡兜住。** 否:一次扇出二十个 `bash` 就是二十张卡,交互上等于不可用;而且
   「脚本里能做什么」会随用户当时点不点卡而变,模型的规划无法稳定。只读既能并行又不产生卡。
2. **把 `read`/`ls`/`find`/`grep` 的名字写成白名单常量。** 否:名字白名单会和「审批只看副作用」的既有判定分叉
   (将来加一个只读文件工具就得改两处)。按 `SideEffect == ReadOnly` 选,判据只有一处。
3. **保留逐工具 schema,只压缩 schema。** 否:压缩只能省一半,而「不印」能省到接近零;且模型询问的成本是一次
   `Tools.Describe`,只在真的要调那个工具时才付。
4. **可见名单做成第二组逐工具开关(像 `toolsDisabled`)。** 否:那会为一个很少调的旋钮加 N 行 UI 与第二个持久化
   形状;一个文本名单 + 前缀通配能表达同样的事,且默认空白就是「全部」。
5. **把设置缓存进会话宿主(建宿主时读一次)。** 否:`run_code` 的预算改完要重启会话才生效,与仓库里其它设置
   (子 Agent 上限、检查轮询、评审包上限)的活读口径不一致;活读的成本是每次读一次 `AppPreferences` 属性。
6. **可见名单变化时重发布外部工具集。** 否:重发布会把 `run_code` 代理与整份外部工具换新,连带让**所有**工具定义的
   缓存前缀重算;改成注册表内过滤后,只有 `run_code` 这一个定义的名字列表变(它本来就要变),其余工具定义原封不动。

## 四、四条判据

**性能(第一约束)。**

- UI 线程:本波不新增任何 UI 线程上的等待或 IO。设置页的四个控件直接读写内存里的 `AppPreferences`,保存走既有的
  防抖写盘。
- 分配:每次读 `run_code.Definition`(每回合一到两次)多一份「候选数」大小的列表(已有行为:原来也是每次读都
  分配快照);`CodeModeSettings.From` 每次读一次(一个小 record + 名单非空时一次短字符串 split),发生在读描述与每次
  内部调用时——单次内部调用已经要付一次 IPC 往返与一次 JSON 解析,这点分配是噪声。名单空白(=默认)时
  `CodeModeToolRegistry.Tools` 直接返回候选数组本身,不复制。
- 热路径不加锁:设置读取是无锁的字段读(`AppPreferences` 由 UI 线程写,引用赋值原子;旧快照继续可用)。
- 提示词:每回合省下「工具数 × (一行描述 + 完整 schema)」,实测见第六节。
- 列表/虚拟化:本波不新增列表。

**依赖与 AOT。** 零新包、零 P/Invoke、零反射。`Tools.List`/`Describe` 是纯字典与字符串拼接(脚本侧本来就在 JIT 进程
里,但形状仍然 AOT 友好);`RunCodeProxyOptions.ExecutionProvider` 是一个委托,不引入动态类型查找。Native AOT 分析器
仍是 0 警告(解决方案整体构建验证)。

**契约与可测性。**

- 改到的契约:`ITool`/`IToolRegistry`(未改)、`RunCodeProxyOptions`(加一个可空属性,旧的 `Execution` 语义保留)、
  `ToolsFacade`(加两个只读方法)、`AppPreferences`(加四项,冻结文件里的获批追加)、`AgentRuntimeServices`(加
  `CodeMode`)。唯一所有者:候选集合归 `CodeModeTools.Collect`(`CollectExternalTools` 唯一调用);过滤归 `CodeModeToolRegistry`;数字与
  名单的唯一存放处是 `AppPreferences`。
- 怎么测才算证明:见第五节的分层。

**回滚成本。** 单波独立:`git revert` 这一次提交即回到「脚本只看到插件工具 + 描述带完整 schema」。没有存储/协议
升版,没有持久化形状变化(`preferences` 段多四个键,旧版本读新文件时忽略即可)。

## 五、验收口径

```
dotnet build Forge.slnx -c Debug
dotnet test tests/Forge.HostClient.Tests --no-build --filter "FullyQualifiedName~RunCode"
dotnet test tests/Forge.Scripting.Tests --no-build
dotnet test tests/Forge.App.Tests --no-build --filter "FullyQualifiedName~CodeMode|FullyQualifiedName~Settings|FullyQualifiedName~Localization|FullyQualifiedName~MinorRegressionTests|FullyQualifiedName~KeyRegistry|FullyQualifiedName~StringTable|FullyQualifiedName~NoHardcodedChinese"
dotnet test tests/Forge.Integration.Tests --no-build --filter "FullyQualifiedName~RunCode|FullyQualifiedName~Script"
```

手测:装零个插件时 `run_code` 存在并可读目录;`Tools.List()`/`Describe("grep")`;把可见名单改成 `read` 后
`Tools.List()` 只剩 `read` 且 `Call("grep")` 报未知工具;把并发改成 1 后并行扇出变串行(描述里的数字同步变)。

## 六、实测数字

同一组工具定义、同一台机器、同一套格式器,分别跑改前与改后的描述构建器(改前是用 HEAD 上那份 `RunCodeDescription.Build`
的逐字重写,改后是现在的真实构建器)。工具面取默认安装真实能拿到的那六个:read / ls / find / grep / web_fetch / web_search
(用它们真实的描述与 JSON schema,长度 284–972 字符)。

| | 字符 | ≈ token(仓库自己的 4 字符/token 口径) |
|---|---|---|
| 改前(逐工具印描述 + schema) | 6510 | 1627 |
| 改后(只印名字 + 两行自述面) | 2516 | 629 |
| 节省 | 3994(-61.4%) | 998 |

增长方向也验证过:这六个工具在改前每多一个就多 461–1262 字符(上面那行工具行本身的长短,schema 大的更长),
改后每多一个只多一个名字加分隔符(7–20 字符)。所以「工具多」这次是真省下了,而不是把成本換了个地方。

头部几段(表面表、语言/拒绝列表、限制行、进程隔离说明)没变,变化只有两处:表面表多了两行(约 110 字符),
工具列表从「每工具一行描述 + schema」变成「一行折行名字」。

## 七、验收结果

合入前在主仓跑的(数字是最后一次全部绿的那一轮):

```
dotnet build Forge.slnx -c Debug                                                          → 0 警告 0 错误
dotnet test tests/Forge.HostClient.Tests --no-build                                       → 511 通过(6 s;其中 RunCode 过滤 34)
dotnet test tests/Forge.Scripting.Tests --no-build                                        → 269 通过(4 s)
dotnet test tests/Forge.App.Tests --no-build --filter "CodeMode|ToolSwitchSettings"       →  50 通过
dotnet test tests/Forge.App.Tests --no-build --filter "Settings|Localization|MinorRegressionTests" → 694 通过(28 s)
dotnet test tests/Forge.App.Tests --no-build --filter "Localization|MinorRegressionTests|KeyRegistry|StringTable|NoHardcodedChinese" → 131 通过
dotnet test tests/Forge.Integration.Tests --no-build --filter "RunCode|Script"            →  34 通过(45 s)
```

没跑:13 个测试项目全量(L3,交给合入前的流水线);与本波无关的模块不跑。

手测项(未自动化,**本次未跑真实 App**):装零个插件时 `run_code` 出现在工具列表并能读目录;把可见名单改成 `read` 后
`Tools.List()` 只剩 `read`;把并发改成 1 后并行扇出变串行。这三条已由单元与集成测试分别覆盖了“存在”“名单生效”“数字活读”,
但界面上的端到端人工验收没做。

**测试协作的一次失败(记录在案,不隐藏)**:这块的测试由三个独立会话写。第一个(glm-5.3-flash)交付了 22 条;
`CodeModeTools.Collect` 的补测派了两次 glm-5.3-flash 对话——两次都安静结束、没产出文件也没报告
(副本本身可正常构建,排除环境原因);第三次换成 glm-5.3(high)一次成功(11 例)。
三次都是与实现会话不同的模型、独立会话,符合仓库对测试会话的要求;两次失败是工具会话层面的事,与产品代码无关。

## 八、独立评审(glm-5.3 high)与处理

评审对象是基线 `7a50591` 起的完整 diff(2815 行)。判决 `APPROVE`,两条措辞不一致与两条 nit 已改:

1. **major —— 指纹清单写多了一个数。** SPEC 与本文原先写“脚本上限参与指纹”,而代码只哈希描述里**印出来**的那几个数
   (测试也钉了“脚本上限改了描述字节不变”)。→ 已从 `Forge.HostClient/SPEC.md` 与本文 §二.3 删掉 `MaxScriptChars`。
2. **minor —— “不破提示词缓存”是错的说法。** 收窄可见名单会改 `run_code` 描述末尾的名字列表,该定义的缓存前缀照样变。
   → `Forge.App/SPEC.md`、本文 §二.4 与被否备选 #6 均改为“不重发布外部工具集、注册表实例不变,但 `run_code` 这一个
   定义的提示词缓存会失效;其余工具定义原封不动”。
3. **nit —— 宿主机未就绪的窗口。** `run_code` 现在无插件时也在工具列表里,宿主还在启动时调用会以 `IsError` 失败。
   → 已写进计划文档的偏离③(与 `extension_*` 工具在宿主不可用时的既有表现一致)。
4. **nit —— 两个死分支。** `options.Execution` 是非可空 init 属性(有 `= new()`),两处 `?? throw` 永远不执行。→ 已删。

评审认为需要测试复核的四条,处理如下:

| 评审要求 | 处理 |
|---|---|
| 插件工具(含 Write/Execute)**全量**进候选 | `tests/Forge.App.Tests/Agents/CodeModeToolsTests.cs` |
| 三族 `ReadOnly` 过滤的纯单元验证 | 同上 |
| 跨来源同名由首个注册者胜 | 同上(断言留下的那份的 `SideEffect`,不只数个数) |
| 脚本内 `web_fetch` 确实走会话审批 | 已有覆盖:`tests/Forge.Integration.Tests/RunCodeScriptTests.Each_inner_tool_approved_individually_allow_and_deny` 断言每个内层调用都进审批路径(allow 与 deny 两向),与工具类型/副作用无关;`Ask` 这条来自 `SessionPolicy` 的工具规则,由 `Forge.App.Tests` 的成交策略测试钉住 |

**已知限制(接受、不改代码)**:`AgentLoop.RecordFingerprintAsync` 只在工具注册表**实例**变化时重算
`prefix_fingerprint`;活过滤让实例不变而定义变了,所以那个指纹会陈旧一拍。它只喂交接对话框的“工具集变过”提示,
不影响实际请求;要修得给 `IToolRegistry` 加版本号(改冻结接口)或把可见名单变化改成重发布工具集(会把整份外部工具
换新、连带所有定义的缓存前缀重算),两个代价都大于收益。
