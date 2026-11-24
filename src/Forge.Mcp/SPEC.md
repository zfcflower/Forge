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

- 支持 stdio 本地进程与可流式 HTTP 两种传输;不含旧版 HTTP+SSE 与 `completions`。OAuth、`resources/subscribe` 与进度通知的契约见下「OAuth 与资源订阅」(W10d 定稿),实现以该节为准。
- 服务器工具经 `McpToolAdapter` 暴露为 `<server>__<tool>`,副作用默认 Execute,配置 `readOnlyTools` 可标只读;脚本/MCP 工具调用仍走主程序审批。
- 配置读 `~/.forge/mcp.json` 与 `<workspace>/.forge/mcp.json`(项目按名覆盖全局),坏条目产生诊断不致命;`url` 仅接受绝对 http/https URI(拒绝相对路径与 file 等其它 scheme)。
- **写**同一批文件(增删改/启停标记/从别家配置导入):只替换被编辑的那一条目,其它条目与其它根键原样保留;不展开 `${VAR}`(写回去的永远是占位符),JSON 注释不保留(`JsonNode` 无此能力,文档写明)。文件 IO 与锁**不在本库**:本库提供纯文本变换(`McpConfigFile`)与原文形态(`McpServerEntry`),原子写与互斥由 App 的 `McpConfigStore` 复用 `SettingsFileMutex` + `AtomicFile`。
- 预设(`McpPresets`)只是命令行数据,标题与说明在 App 的字符串表里——本库不含用户可见文案。
- `mcp_tool_search`:上限生效且隐藏了工具时由 App 提供给模型;调用它只改变“哪些工具可被调用”,不执行任何服务器工具。

## 关键决策

**请求生命周期。** 每请求的截止期限覆盖发送与等待响应全程，关联调用方取消与连接生命周期；超时/取消在请求已发出后发 `notifications/cancelled`（`initialize` 除外；令牌在发送完成前就取消的预取消请求不发取消通知，因为它引用一个服务端从未见过的 id）。JSON-RPC id 按类型与值精确关联（字符串 id 不匹配数字 id）。

**传输健壮性。** stdio 按字节分帧,stdout 非 JSON 行记入有界诊断环且读取继续;stderr 严格有界。stdout 与 stderr 由两个独立任务读取,两者记录到诊断日志的先后不保证。子进程退出时 `Closed` 携带的异常必须带退出码:Unix 上子进程退出时先关管道、父进程稍后才被 SIGCHLD 收割,所以 stdout 读到 EOF 后先给退出一个 500 ms 的观察窗口,进程确实没了就交给退出监控(它报 `exited with code N`),仍活着才报 `closed its standard output`;否则 Linux 上退出码会稳定丢失。可流式 HTTP 按 SSE 流分别维护 Last-Event-ID,每个 POST SSE 响应流独立取消/释放,旧代际消息不完成新请求;会话 DELETE 带原始会话号并有独立关闭期限。

**工具命名稳定。** 暴露名由 (serverName, toolName) 的稳定哈希消歧,不依赖列表返回顺序;同一名字在管理器生命周期内不重新绑定到别的工具。

**服务器管理。** 懒启动,状态 Stopped/Starting/Ready/Failed;连接或初始化失败停止并回收传输;调用方取消不标记失败、不安排重连;崩溃后退避重连。

**配置原文与展开态分离。** 运行时用的是展开后的 `McpServerConfig`(`env`/`headers` 里 `${VAR}` 已取值),可编辑态是 `McpServerEntry`(逐字原文 + `RawNodeJson`)。两者不可互相替代:写文件只能用原文态,否则用户文件里会出现明文密钥;运行只能用展开态,否则变量名会被当成字面值传给子进程。`McpServerConfig.Source` 记录定义该条目的文件,供界面显示与定位。

**运行期改配置。** `ApplyConfigAsync` 按名字差量应用:删掉的先停进程再释放传输;条目变了的**连传输一起回收**再带新条目重启——传输在构造时捕获了 `command`/`args`/`env`/`cwd`(HTTP 是 `url`/`headers`),复用它会用旧命令行重启,旧进程于是继续跑文件里已不存在的配置;新增的注册为启用但懒启动。启用状态以文件里的 `disabled` 为准(手改删掉它即重启生效),不看运行期的旧开关。单台服务器重启失败只落在它自己的状态上,不中断整次应用——一次坏编辑不该让其它服务器下线。

**选项活读。** `McpServerManager` 接受 `Func<McpServerManagerOptions>`:请求超时在每次建客户端时读,重连延迟在每次退避时读,所以设置页改完的值对下一次连接生效,不需要重建管理器(重建会杀掉所有已连接服务器及其工具)。

**工具面的体积。** `tools/list` 里 `inputSchema` 超过 `MaxToolSchemaChars`(默认 64 K 字符,0 关闭)的工具**不提供给模型**,并在该服务器的 `LastError` 上写明跳过了几个、第一个是谁;描述超过 `MaxToolDescriptionChars`(默认 4 K)截断加省略号,不拆代理对。每次请求都带着全部已提供的 schema,一台病态服务器不能独自占满上下文。这两项与 `MaxResultChars` 一样是库默认值,不是用户偏好。

**工具命名反查。** `ConfigOfTool(exposedName)` 走与暴露名分配同一张表反查服务器,所以消歧过的名字也不会认错服务器;审批卡用它展示“这条命令来自哪台服务器”。

**工具选面与检索。** 上限为 0 时全部就绪工具都给模型(默认,与引入上限前一致)。上限生效时由 App 的 `McpToolExposure` 选面:固定的(pin) → 按当前请求打分的前若干名;被隐藏时同时提供 `mcp_tool_search`,`Search` 命中即 pin 并触发重新发布,保证上限永远不会让某台服务器的工具**不可达**。打分只做小写词包含(暴露名 > 工具名/服务器名 > 描述),每次(目录快照, 上限, 请求文本)算一次并缓存,不动锁、不写状态。

**工具结果的块。** `tools/call` 的 `content` 按块保留而不是先扁平成字符串(audio 也只留占位),`McpContent` 携带 `Type`/`Text`(给人看的形态,放在字幕与日志里)/`MimeType`/`Uri`/`SizeBytes`,图片块额外带 `Data`。去程到 `Forge.Abstractions` 只留 app 能显示的四种图片格式(png/jpeg/gif/webp);单张解码后超过 5 MB 的**丢弃字节只留占位**,一次结果的图片合计超过 20 MB 时其后的图片同样只留占位(安全边界,不是可配值——它们同时是内存上限;单张上限挡不住「一次返回很多张」);`audio` 不保留字节。`McpToolAdapter` 因此返回块列表:文本优先(把已变成图片的块从文本里剔掉,免得同一张图说两遍),随后每张图一个 `ImageBlock`;`result.Text` 只在没有结构化 `content` 时作为唯一文本块。字符截断只作用于文本,图片不受字符数上限抽头。

## OAuth 与资源订阅(W10d 契约,2026-11-15 拍板)

**OAuth 只对可流式 HTTP。** 401 + `WWW-Authenticate` 触发发现:RFC 9728 protected-resource metadata → RFC 8414 授权服务器 metadata → RFC 7591 动态客户端注册 → 授权码 + PKCE(S256)+ loopback 回调(BCL `HttpListener`,随机端口)→ token 交换;过期用 refresh token 刷新,刷新后重试一次。**不静默弹浏览器**:连接遇 401 只把服务器置为需要登录的状态,用户在 MCP 设置里点「登录」才走流程;「退出」清除 token。会话中途的 401(工具调用、资源读写)同样置为需要登录并抛给调用方,行上的登录按钮因此可见。

**401 的重试有上限。** 每个请求(含服务端 GET 流的一次尝试)最多用新取得的 token 重试一次;重试后仍然 401 则按普通失败计入重试次数并走退避,绝不在原速率下循环。取不到 token(没有、刷新被拒、没有任何可用的)不重试:直接报需要登录。同一服务器上并发到达的多个 401 **只刷新一次**(按服务器的单飞门),后到的复用已刷新的 token——轮换 refresh token 的服务器上,两次并发刷新会让后一次拿着已被作废的 token,看起来像用户漏登录。

**每个端点都要求 https。** protected-resource metadata、授权服务器 metadata、注册、token 与授权端点的 scheme 必须是 `https`(RFC 9728 §2.1),唯一例外是 loopback(RFC 8252 的 http 回调地址,本地自建的 AS 同样落在那里);不合规的 URL 在发请求或打开浏览器之前就被拒为 `McpOAuthException`。单个 OAuth 步骤 30 s 超时,连接失败、超时与读体失败都包成 `McpOAuthException`。浏览器一侧还有一道自己的关:交给系统的 URL 必须是绝对 http/https URI,`Process.Start(UseShellExecute)` 对 `file://`、UNC 路径有本地执行面。

**token 不落明文。** 本库不存储 token:经 `IMcpTokenStore`(按 server url origin + resource 指示符的稳定键)注入,App 用 `ProviderSecretStore` 同款 AES-GCM 旁钥模式实现(`mcp-secrets.json` + owner-only 旁钥,原子写);两个加密存储共用同一份密文旁车件(`EncryptedSidecarFile`:密钥文件、加解密、信封与损坏检测),只在文件名与引用空间上不同。浏览器经 `IMcpOAuthUi` 注入。本库依赖边界(只 Abstractions + BCL)不变;PKCE/loopback 助手在本库内实现而不抽公共——App 的 OpenRouter 助手进不了本库的依赖边界,记为故意重复。

**进度。** `tools/call` 携带 `_meta.progressToken`(每调用唯一),`notifications/progress` 按 token 路由到该调用的 `ToolContext.Progress`(最新尾快照语义,与 BashTool 同口径),资源检查器直接消费;无 token 的进度通知落通用事件。

**资源订阅。** `resources/subscribe`/`unsubscribe` 请求 + `notifications/resources/updated` → `ResourceUpdated` 事件;能力位 `resources.subscribe` 已解析。**订阅集合归管理器**:订阅只属于发送它的那个 MCP 会话,`McpClient` 在 peer 重建时清空自己的订阅集合;管理器按服务器保存用户期望的订阅集合并于每次新会话建立后重放它,所以断线重连不会让已订阅的资源悄无声息地停止通知,而客户端配置变更(换 URL/命令)会清空该集合。App 表面:MCP 设置分区每服务器的资源浏览器(列表/读取/订阅,更新通知即时刷新,已订阅徽标读管理器状态而不是上一行自己的标记);**不加 agent 资源工具**(用户拍板)。通知触发的刷新不是用户动作:它用分区自己的取消源、同一个 tick 只列一次、且有动作在跑时直接跳过——拿用户动作的取消源会把刚点的删除/重启静默取消掉。会话 id 跨重启持久化仍未决。

## 未决

- 会话 id 跨重启持久化、`completions/complete`(按需)。
- `ProviderException` 式的协议错误细节未完全类型化(部分以 DataJson 字符串承载)。
- 写回 `mcp.json` 会丢注释、统一为两空格缩进:要保留注释需要一个带 trivia 的 JSON 模型,目前无此依赖。
- 工具选面按“当前请求文本”打分,没有跨回合的使用频次记忆(那需要会话级的调用统计接缝);请求没命中任何词时按目录顺序取前 N 名。
