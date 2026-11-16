# 10 MCP（Model Context Protocol）

FORGE 自写 MCP 客户端与服务器管理，不引用 MCP SDK：JSON-RPC 与传输都在 `Forge.Mcp`。本文件说明传输、配置、工具暴露、OAuth、资源订阅与设置页。

## 10.1 传输与协议

- 支持 **stdio 本地进程**与**可流式 HTTP**（streamable HTTP，SSE 分帧）；不含旧版 HTTP+SSE 与 `completions`。
- 请求生命周期：每请求的截止期限覆盖发送与等待响应全程，关联调用方取消与连接生命周期；超时/取消在请求已发出后发 `notifications/cancelled`（`initialize` 除外；预取消请求不发，因为它引用一个服务端从未见过的 id）。JSON-RPC id 按类型与值精确关联（字符串 id 不匹配数字 id）。
- stdio 健壮性：按字节分帧；stdout 非 JSON 行记入有界诊断环且读取继续；stderr 严格有界；stdout/stderr 由两个独立任务读取，进诊断日志的先后不保证。子进程退出时 `Closed` 必须带退出码：stdout 读到 EOF 后先给退出一个 500 ms 观察窗口，确实没了交给退出监控（报 `exited with code N`），仍活着才报 `closed its standard output`。
- streamable HTTP：按 SSE 流分别维护 `Last-Event-ID`，每个 POST SSE 响应流独立取消/释放，旧代际消息不完成新请求；会话 DELETE 带原始会话号并有独立关闭期限。

## 10.2 配置

- 读 `~/.forge/mcp.json` 与 `<workspace>/.forge/mcp.json`（项目按名覆盖全局）；坏条目产生诊断不致命；`url` 只接受绝对 http/https（拒绝相对路径与 `file` 等 scheme）。
- **运行期用展开态、编辑态用原文**：运行时 `McpServerConfig` 里的 `${VAR}` 已取值；可编辑的 `McpServerEntry` 逐字保留原文。写文件只能写原文态，否则用户文件里会出现明文密钥。
- 写文件（增删改/启停标记/从别家导入）：只替换被编辑的那一条目，其它条目与其它根键原样保留；不展开 `${VAR}`（写回去的永远是占位符）；JSON 注释不保留、统一两空格缩进；文件根不是对象时拒绝写。写入由 App 的 `McpConfigStore` 复用 `SettingsFileMutex` + `AtomicFile`。
- 导入来源：Claude / Cursor / Claude Desktop 的配置；按节点原样拷贝（占位符、未知字段、值的 JSON 种类都不变），已在 Forge 里的同名项默认不勾选；导入源不存在时说明路径与原因。
- 预设（filesystem / context7 / memory / playwright / git / fetch / time）只填进编辑器，不静默写文件；需要目录的预设留 `{path}` 给用户确认。
- 保存 = 写文件 → 重新读两份 → `ApplyConfigAsync` 差量应用：删掉的先停进程再释放传输；条目变了的**连传输一起回收**再带新条目重启（复用传输会用旧命令行重启）；新增的注册为启用但懒启动。启用状态以文件里的 `disabled` 为准（手改删掉它即下次重启生效）。单台启动失败只落在它自己的状态与 stderr 上。
- **读不懂的文件不等于删除**：某份文件根坏了（非法 JSON、根不是对象、没有 `mcpServers`）时，那份文件贡献不出服务器，于是**来自该文件的运行中服务器原样保留**，只应用其余文件的变化；“重新读取”按钮走同一条 `apply` 路径。
- 选项活读：请求超时（默认 60 s）在每次建客户端时读，重连延迟（默认 1 s）在每次退避时读；改完对下一次连接生效，不重建管理器。

## 10.3 服务器生命周期

- 状态：`Stopped` / `Starting` / `Ready` / `Failed`；懒启动；连接或初始化失败停止并回收传输；调用方取消不标记失败、不安排重连；崩溃后退避重连。
- 重连即“停 → 启”两步（没有独立重连动作）。
- 每次建客户端/每次退避现读超时与重连延迟；`stderr` 尾行数（默认 200）只在显示时截断。

## 10.4 工具暴露

- 服务器工具经 `McpToolAdapter` 暴露为 `<server>__<tool>`；暴露名由（serverName, toolName）的稳定哈希消歧，不依赖列表返回顺序；同一名字在管理器生命周期内不重新绑定到别的工具。
- 副作用默认 `Execute`，配置 `readOnlyTools` 可标只读；脚本/MCP 工具调用仍走主程序审批。
- **体积上限**：`inputSchema` 超过 `MaxToolSchemaChars`（默认 64 K 字符，0 关闭）的工具不提供给模型，并在该服务器 `LastError` 写明跳过了几个、第一个是谁；描述超过 `MaxToolDescriptionChars`（默认 4 K）截断加省略号、不拆代理对。
- **结果按块保留**：`tools/call` 的 `content` 不去程扁平化；图片块解码后超过 5 MB 的丢弃字节只留占位，一次结果合计超过 20 MB 时其后的图片同样只留占位（安全边界，不是可配值）；`audio` 不保留字节。适配器返回块列表（文本优先，随后每张图一个 `ImageBlock`）。
- **进度**：`tools/call` 携带 `_meta.progressToken`，`notifications/progress` 按 token 路由到该调用的 `ToolContext.Progress`（最新尾快照语义），资源检查器直接消费；无 token 的进度通知落通用事件。
- **工具选面上限**（`mcpToolSearchLimit`，0 = 不限，默认 0）：
  - 0：全部就绪工具都给模型（与引入上限前一致）。
  - >0：固定的（pin，多于上限时取最近固定的）→ 按本回合用户文本打分的前若干名（小写词包含：暴露名 > 工具名/服务器名 > 描述），不够时按目录顺序补足；每个（目录快照, 上限, 请求文本）算一次并缓存，结果按暴露名排序以保证集合不变时定义字节不变。
  - 被隐藏时同时提供 `mcp_tool_search`（只读、不需要审批）：命中即 pin 并**当场重新发布**工具面，所以上限永远不会让某台服务器的工具不可达。
  - 每回合开始由 `ResolveAsync` 比较选中的名字与宿主持有的名字，不一致才 `ReplaceExternalTools`（集合没变就不发布、提示词缓存不破）。

## 10.5 OAuth（仅 streamable HTTP）

- 触发：连接遇到 401 + `WWW-Authenticate` → RFC 9728 protected-resource metadata → RFC 8414 授权服务器 metadata → RFC 7591 动态客户端注册 → 授权码 + PKCE(S256) + loopback 回调（BCL `HttpListener`，随机端口）→ token 交换；过期用 refresh token 刷新，刷新后重试一次。
- **不静默弹浏览器**：连接遇 401 只把服务器置为“需要登录”，用户在 MCP 设置里点“登录”才走流程；“退出”清除 token。会话中途的 401（工具调用、资源读写）同样置为需要登录并抛给调用方，行上的登录按钮因此可见。
- 401 重试有上限：每个请求（含服务端 GET 流的一次尝试）最多用新取得的 token 重试一次；仍 401 按普通失败计入重试次数并退避。取不到 token 不重试，直接报需要登录。同一服务器上并发到达的多个 401 **只刷新一次**（单飞门），避免轮换 refresh token 的服务器上后一次拿已作废的 token。
- 每个端点都要求 https（唯一例外是 loopback 回调地址）；不合规 URL 在发请求或打开浏览器之前就被拒为 `McpOAuthException`；单个 OAuth 步骤 30 s 超时。
- token 不落明文：本库不存储 token，经 `IMcpTokenStore`（按 server url origin + resource 指示符的稳定键）注入，App 用与 `ProviderSecretStore` 相同的 AES-GCM 旁钥模式实现（`mcp-secrets.json` + owner-only 旁钥、原子写、损坏检测）。

## 10.6 资源订阅

- `resources/subscribe` / `unsubscribe` + `notifications/resources/updated` → `ResourceUpdated` 事件；能力位 `resources.subscribe` 已解析。
- 订阅集合归**管理器**：订阅只属于发送它的那个 MCP 会话，客户端在 peer 重建时清空自己的订阅集合；管理器按服务器保存用户期望的订阅集合并于每次新会话建立后重放，所以断线重连不会让已订阅资源悄无声息地停止通知；客户端配置变更（换 URL/命令）会清空该集合。
- 界面：MCP 设置分区每服务器的**资源浏览器**（列表 / 读取 / 订阅；更新通知即时刷新；已订阅徽标读管理器状态而不是上一行自己的标记）；通知触发的刷新用分区自己的取消源、同一个 tick 只列一次、有动作在跑时直接跳过。
- **不提供 agent 资源工具**（用户拍板）。

## 10.7 设置页 `mcp`

- 一页里增删改、启停、测试、看工具清单与 stderr 尾、从别家配置导入、用内置预设起步。
- 参数四项直连偏好：默认请求超时 60 s、重连初始延迟 1 s、工具检索上限（0 = 不限）、stderr 尾行数 200。
- 线程：文件读写、`ApplyConfigAsync`、`ProbeAsync` 全部在线程池；`StateChanged` 可能来自任意线程，页面把它合并成一次 dispatcher post 再重建行；工具清单与 stderr 只在展开时读，展开/导入列表都有上限。
- 工具进入会话：就绪服务器的工具经 `CollectExternalTools` 出现在会话工具面上（`ToolOrigin.Mcp`，组与来源都是服务器名，开关跟随服务器，`tools` 分区只读）。

相关文档：[02-agent-runtime.md](02-agent-runtime.md)（审批卡与工具面）、[03-providers-and-network.md](03-providers-and-network.md)（网络栈与代理）、[15-appendix.md](15-appendix.md)（MCP 常量）。
