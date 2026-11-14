---
id: forge-mcp
type: module-design
status: draft
title: Forge.Mcp
parent: forge-architecture
---

# Forge.Mcp

MCP(Model Context Protocol)客户端与服务器管理。自写 JSON-RPC 与传输,不引用 MCP SDK;只依赖 `Forge.Abstractions` 与 BCL(System.Text.Json)。

## 边界

- 支持 stdio 本地进程与可流式 HTTP 两种传输;不含 OAuth、旧版 HTTP+SSE、`resources/subscribe`、`completions`、进度通知。
- 服务器工具经 `McpToolAdapter` 暴露为 `<server>__<tool>`,副作用默认 Execute,配置 `readOnlyTools` 可标只读;脚本/MCP 工具调用仍走主程序审批。
- 配置读 `~/.forge/mcp.json` 与 `<workspace>/.forge/mcp.json`(项目按名覆盖全局),坏条目产生诊断不致命;`url` 仅接受绝对 http/https URI(拒绝相对路径与 file 等其它 scheme)。
- **写**同一批文件(增删改/启停标记/从别家配置导入):只替换被编辑的那一条目,其它条目与其它根键原样保留;不展开 `${VAR}`(写回去的永远是占位符),JSON 注释不保留(`JsonNode` 无此能力,文档写明)。文件 IO 与锁**不在本库**:本库提供纯文本变换(`McpConfigFile`)与原文形态(`McpServerEntry`),原子写与互斥由 App 的 `McpConfigStore` 复用 `SettingsFileMutex` + `AtomicFile`。
- 预设(`McpPresets`)只是命令行数据,标题与说明在 App 的字符串表里——本库不含用户可见文案。
- `mcp_tool_search`:上限生效且隐藏了工具时由 App 提供给模型;调用它只改变“哪些工具可被调用”,不执行任何服务器工具。

## 关键决策

**请求生命周期。** 每请求的截止期限覆盖发送与等待响应全程,关联调用方取消与连接生命周期;超时/取消在已发出后发 `notifications/cancelled`(`initialize` 除外)。JSON-RPC id 按类型与值精确关联(字符串 id 不匹配数字 id)。

**传输健壮性。** stdio 按字节分帧,stdout 非 JSON 行记入有界诊断环且读取继续;stderr 严格有界。stdout 与 stderr 由两个独立任务读取,两者记录到诊断日志的先后不保证。子进程退出时 `Closed` 携带的异常必须带退出码:Unix 上子进程退出时先关管道、父进程稍后才被 SIGCHLD 收割,所以 stdout 读到 EOF 后先给退出一个 500 ms 的观察窗口,进程确实没了就交给退出监控(它报 `exited with code N`),仍活着才报 `closed its standard output`;否则 Linux 上退出码会稳定丢失。可流式 HTTP 按 SSE 流分别维护 Last-Event-ID,每个 POST SSE 响应流独立取消/释放,旧代际消息不完成新请求;会话 DELETE 带原始会话号并有独立关闭期限。

**工具命名稳定。** 暴露名由 (serverName, toolName) 的稳定哈希消歧,不依赖列表返回顺序;同一名字在管理器生命周期内不重新绑定到别的工具。

**服务器管理。** 懒启动,状态 Stopped/Starting/Ready/Failed;连接或初始化失败停止并回收传输;调用方取消不标记失败、不安排重连;崩溃后退避重连。

**配置原文与展开态分离。** 运行时用的是展开后的 `McpServerConfig`(`env`/`headers` 里 `${VAR}` 已取值),可编辑态是 `McpServerEntry`(逐字原文 + `RawNodeJson`)。两者不可互相替代:写文件只能用原文态,否则用户文件里会出现明文密钥;运行只能用展开态,否则变量名会被当成字面值传给子进程。`McpServerConfig.Source` 记录定义该条目的文件,供界面显示与定位。

**运行期改配置。** `ApplyConfigAsync` 按名字差量应用:删掉的先停进程再释放传输;条目变了的**连传输一起回收**再带新条目重启——传输在构造时捕获了 `command`/`args`/`env`/`cwd`(HTTP 是 `url`/`headers`),复用它会用旧命令行重启,旧进程于是继续跑文件里已不存在的配置;新增的注册为启用但懒启动。启用状态以文件里的 `disabled` 为准(手改删掉它即重启生效),不看运行期的旧开关。单台服务器重启失败只落在它自己的状态上,不中断整次应用——一次坏编辑不该让其它服务器下线。

**选项活读。** `McpServerManager` 接受 `Func<McpServerManagerOptions>`:请求超时在每次建客户端时读,重连延迟在每次退避时读,所以设置页改完的值对下一次连接生效,不需要重建管理器(重建会杀掉所有已连接服务器及其工具)。

**工具命名反查。** `ConfigOfTool(exposedName)` 走与暴露名分配同一张表反查服务器,所以消歧过的名字也不会认错服务器;审批卡用它展示“这条命令来自哪台服务器”。

**工具选面与检索。** 上限为 0 时全部就绪工具都给模型(默认,与引入上限前一致)。上限生效时由 App 的 `McpToolExposure` 选面:固定的(pin) → 按当前请求打分的前若干名;被隐藏时同时提供 `mcp_tool_search`,`Search` 命中即 pin 并触发重新发布,保证上限永远不会让某台服务器的工具**不可达**。打分只做小写词包含(暴露名 > 工具名/服务器名 > 描述),每次(目录快照, 上限, 请求文本)算一次并缓存,不动锁、不写状态。

## 未决

- MCP OAuth 登录、会话 id 跨重启持久化、`resources/subscribe` 与进度通知(均按需)。
- `ProviderException` 式的协议错误细节未完全类型化(部分以 DataJson 字符串承载)。
- 写回 `mcp.json` 会丢注释、统一为两空格缩进:要保留注释需要一个带 trivia 的 JSON 模型,目前无此依赖。
- 工具选面按“当前请求文本”打分,没有跨回合的使用频次记忆(那需要会话级的调用统计接缝);请求没命中任何词时按目录顺序取前 N 名。
