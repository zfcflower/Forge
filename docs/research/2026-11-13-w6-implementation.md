# W6(MCP 管理面)实现记录

对照 `2026-11-13-thinkrail-gap-plan.md` 第四节 W6 行落地。本文件记录**实际做了什么**、被否的备选、四条判据的答复与验收结果;计划本身不在此处改。

## 一、交付面

### 1. 配置写入(`Forge.Mcp` 纯逻辑 + App 的 IO)

| 新增/改动 | 内容 |
|---|---|
| `Forge.Mcp/McpServerEntry` | **文件原文形态**的可编辑条目(`Command`/`Args`/`Env`/`cwd`/`Url`/`Headers`/`Disabled`/`ReadOnlyTools`/`TimeoutSeconds`),`${VAR}` 不展开,`RawNodeJson` 记住读到的原节点。`Normalized()` 去空白、丢空参数与空键行;`Validate()` 返回 `McpEntryIssue(Field, Problem, Detail)` **代码**而不是句子(文案归 App 的字符串表)。 |
| `Forge.Mcp/McpConfigFile` | 对一份 `mcp.json` 文本的读/增改/删/导入:`Read` 宽容(坏条目=诊断,可在编辑器里修)、`Upsert` 以原节点为底只覆盖本工具管理的键(未知字段保留)、`Import` **逐字拷贝**源节点(占位符、未知字段、值的 JSON 种类都不变,只要求名字非空)、`Remove` 只删一条。根不是对象或 `mcpServers` 不是对象 → `InvalidDataException`,不覆盖用户文件。 |
| `Forge.Mcp/McpPresets` | 7 个预设(filesystem/context7/memory/playwright/git/fetch/time)的命令行数据 + `{path}` 占位;文案在 App 的 `Strings.Mcp.cs`,库里没有用户可见文案。 |
| `Forge.App/Services/McpConfigStore` | 两份文件(`~/.forge/mcp.json`、`<workspace>/.forge/mcp.json`)的读写:写入取 `SettingsFileMutex.Acquire(path)` + `AtomicFile.WriteAllBytes`(**复用**既有互斥与原子写,不另造一份);每次写前重读文件,手改不会被覆盖;`Load()` 产出运行时用的展开态 `McpConfig`(项目按名覆盖全局)。 |
| `McpServerConfig.Source` | 记录条目来自哪份文件;`McpConfigLoader` 解析时填。 |

### 2. 运行期应用配置与选项活读(`Forge.Mcp/McpServerManager`)

- `ApplyConfigAsync(McpConfig, ct)`:按名差量应用——删除→停进程并释放传输;条目变更→带新条目重启(旧进程不留在文件里已不存在的配置上);新增→注册为启用、懒启动;`Diagnostics`/`ServerNames` 变活。单台重启失败只落它自己的状态,不中断整次应用。手写的重复名字按"后者生效"折叠(与 loader 一致)。
- `McpServerManager(McpConfig, Func<McpServerManagerOptions>)`:请求超时在**每次建客户端**时读、重连延迟在**每次退避**时读,所以设置页改完对下一次连接生效,不必重建管理器(重建会杀掉所有服务器及其工具)。
- 条目变更时**连传输一起回收**再重启:传输在构造时捕获了 `command`/`args`/`env`/`cwd`(HTTP 是 `url`/`headers`),复用旧传输等于用旧命令行重启;启用状态以文件里的 `disabled` 为准。
- `ConfigOfTool(exposedName)`(走与暴露名分配同一张表,消歧名也认得出)、`ToolsOf(server)`、`ProbeAsync`(连接 + `tools/list` + 耗时;未知名字与起不来的服务器都返回 `Failed` 结果,不抛)。
- 启动失败的 stderr 尾巴会被记到服务器状态上(传输回收前抓取),否则设置页与「测试」恰好在最需要它的时候看不到原因。
- `McpServerProbe` 记录。

### 3. 设置页(`ViewModels/Settings/McpSettingsSectionViewModel` + `Views/McpSettingsView.axaml` + `Localization/Strings.Mcp.cs`)

分区从只读行改为可管理:每台服务器的名字/传输/状态/就绪时刻/上次错误/stderr 尾(按 `mcpStderrTailLines` 截断)/工具清单(展开时读、上限 500)、启用开关、启动/停止/重启/测试/刷新工具/编辑/删除;内联编辑器(名称、传输、命令、参数、cwd、URL、env 与 headers 的键值行、readOnlyTools、timeoutSeconds、保存目标全局/项目);导入面板(Claude Code `.claude.json`、Claude Desktop `claude_desktop_config.json`、Cursor `.cursor/mcp.json` 三源,同名只取先找到的,已存在标出且默认不勾选);7 个预设填入编辑器(不静默写文件);四个 MCP 偏好直连 `AppPreferences`。线程:`StateChanged` 只置脏 + **一次** dispatcher post;文件与 manager 操作全部在线程池;每个动作各自 CTS,`Dispose` 全部取消并退订。

### 4. 审批卡(`Approval/`)

卡片在 MCP 工具时多一段服务器信息:名字、传输、`command` + 参数(后台线程拼行、含空白加引号、4 000 字符封顶)、`cwd`、`url`、`env`/`headers` 的**键名**(值永不出现),以及"撤销覆盖本工具的已记住规则"(显示条数,会话/项目/全局一起撤销,本卡决策不受影响)。`ApprovalServices.McpToolInfo` 由组合根在 MCP 服务建好后赋值。

### 5. MCP 工具进入会话(计划外,已获批)

- 之前 `McpServerManager.Tools` 在 App 零消费,MCP 工具从未到达模型。`SessionRuntime.CollectExternalTools` 现在追加 MCP 适配器,`DescribeExternal` 追加 `ToolOrigin.Mcp` 描述符。
- `Agents/McpToolExposure`:`mcpToolSearchLimit` 为 0(默认)时全给;大于 0 时选面 = 固定的(pin;多于上限时取最近固定的)→ 按本回合用户文本打分的前若干名 → 按目录顺序补足,结果按暴露名排序(集合不变则工具定义字节不变)。打分是小写词包含(暴露名 4 分 > 工具名/服务器名 3 分 > 描述 1 分),每个(目录快照, 上限, 请求文本)算一次并缓存;`Lock` 保护(回合线程与工具线程都会碰)。
- `Forge.Mcp/McpToolSearchTool`(`mcp_tool_search`,只读):命中即 pin 并**当场重新发布**工具面,所以上限永远不会让某台服务器的工具不可达;`SessionRuntime.RefreshMcpTools` 每回合开始比较"选中的名字"与"宿主持有的名字",不一致才 `ReplaceExternalTools`(与 `web_search` 有无的同一机制),集合没变就不发布、提示词缓存不破。

### 6. 偏好(六·二登记的四个 MCP 项)

| 项 | 消费点 |
|---|---|
| `McpRequestTimeoutSeconds` | `McpServices.ServerOptions` → manager 每次建客户端时读(服务器自己的 `timeoutSeconds` 仍覆盖) |
| `McpReconnectInitialDelaySeconds` | 同上 → 每次退避时读 |
| `McpToolSearchLimit` | `McpServices.ToolSearchLimit` → `McpToolExposure.Select` |
| `McpStderrTailLines` | 设置页显示 stderr 尾时截断(`McpServices.Tail`) |

## 二、被否的备选

1. **把展开后的 `McpServerConfig` 直接写回文件** —— 会把 `${VAR}` 的取值写成明文落盘;改为"原文形态"编辑(`McpServerEntry`)。
2. **整份重写 `mcp.json`** —— 手写/共享的项目文件会被削平,与仓库既有文件规则相反;改为按节点合并,只替换被编辑的那一条。
3. **在 `Forge.Mcp` 里自建第二份文件锁** —— 与 `SettingsFileMutex` 逐字重复;改为纯变换在库内、IO 与锁在 App 复用同一把。
4. **重建 `McpServerManager` 来应用新配置** —— 会杀掉所有已连接服务器及其工具;改为差量 `ApplyConfigAsync`。
5. **工具检索上限只做"按名字取前 N"** —— 静默让工具不可达;改为检索 + pin + `mcp_tool_search` 兜底。
6. **审批卡自己读 `mcp.json`** —— 把 IO 塞进判定路径;改为组合根注入只读快照(`DescribeForApproval`)。
7. **撤销放可交互 toast** —— 现有通知没有按钮,要新做一类通知;改为卡片上撤销(计划原文只有"即时撤销"四字,用户确认按此理解)。

## 三、四条判据

- **性能**:热路径只有 `McpToolExposure`(每回合一次、结果缓存、无锁内分配);选面为 0 时限(默认)直接返回目录快照,不排序不缓存。UI 线程零文件 IO、零阻塞,状态更新合并成一次 post;工具清单与导入候选按需读取并各有 500 上限。锁只有 `McpServerManager.gate`(短临界区)、`McpToolExposure.sync`(每回合一次)、`SettingsFileMutex`(跨 await 不持锁)。
- **依赖与 AOT**:零新包、无 P/Invoke、无 Roslyn;只用 `System.Text.Json.Nodes` 与既有 `AtomicFile`/`SettingsFileMutex`。AOT 分析器 0 警告(`JsonArray.Add<T>` 的 IL2026/IL3050 已改为 `Add((JsonNode?)JsonValue.Create(...))`;`new JsonArray(int)` 会被 `params JsonNode[]` 重载吃掉,已改 `new JsonArray()`)。
- **契约与可测性**:改的是 `McpServerConfig`(+`Source`)、`McpServerManager`(活 options/`ApplyConfigAsync`/`ConfigOfTool`/`ToolsOf`/`ProbeAsync`)、`ApprovalServices.McpToolInfo`、`AgentRuntimeServices.Mcp`。纯函数(`McpConfigFile`/`McpServerEntry`/`McpPresets`/`McpToolSearchTool`)与差量应用(假 transport)各自单测;App 侧 `McpConfigStore`/`McpServices`/`McpToolExposure`/审批卡 VM 单测;设置页由 headless 渲染测试覆盖。
- **回滚成本**:全部落在 W6 的归属路径 + 三个 MCP 专用接线点(`AppComposition` 的 MCP 段、`Agents/SessionRuntime` 的 MCP 相关行、`App.axaml` 的 mcp 分区模板一行);无 schema、无迁移、无数据格式兼容问题(没写过的文件不会出现新键)。

## 四、验收

构建:
```
dotnet build Forge.slnx            → 已成功生成,0 警告 0 错误
```
测试(命令与结果见下):`Forge.Mcp.Tests` 全量;`Forge.App.Tests` 的 `Settings`/`Approval`/`Agents`/`Mcp`/`Localization`/`MinorRegression`。手测项:加一台 stdio 服务器保存 → 文件只多这一条、其余字节不变;停用 → 子进程退出;从 Claude/Cursor 导入 → 占位符与未知字段原样保留;审批卡显示 command 与 env 键名、撤销生效;`mcpToolSearchLimit` 设 3 时模型只看到 3 个工具且 `mcp_tool_search` 出现,检索后命中工具可调用。

## 五、偏差与遗留

- 保存改名会在同一次写里删掉旧键(`McpConfigFile.Upsert(..., replacedName)`),不留幽灵条目。
- 某份文件读不懂(非法 JSON / 根不是对象 / 没有 `mcpServers`)时,**来自该文件的运行中服务器原样保留**,只应用其余文件的变化;完全读不出来时什么都不应用。「重新读取」按钮走同一条 apply 路径。
- **MCP 工具接入会话属于计划外**(计划只写"管理面"),已向用户确认后并入本波。
- 工具选面按"本回合请求文本"打分,**没有**跨回合的使用频次记忆(需要会话级调用统计接缝);请求命中 0 个词时按目录顺序取前 N。
- 写回 `mcp.json` 丢 JSON 注释、统一两空格缩进;`Claude Code` 的 `~/.claude.json` 只读顶层 `mcpServers`(不读 `projects.<路径>.mcpServers`)。
- 编辑一个既有条目时,`env`/`headers` 的非字符串值会被写成字符串(按原文文本),导入路径不受影响(逐字拷贝)。
- `Forge.App/SPEC.md` 原有的 `settings.mcp.hint/empty/enabled/disabled` 四个只读分区文案随 stub 一起删除(标题键仍被分区注册使用)。
