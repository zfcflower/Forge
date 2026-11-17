# 03 模型提供商、认证与网络

本文件说明 FORGE 如何连接模型服务：三家协议、七种认证、凭据存储、模型目录与元数据、缓存策略、官方身份、网络栈与用量日志。

## 3.1 三家协议

| 协议 | 线路 | 使用方（预设） |
|---|---|---|
| `openai-compatible` | `POST {baseUrl}/chat/completions` | OpenAI（API 密钥）、GitHub Copilot（API 密钥）、OpenRouter、Kimi For Coding、xAI、自定义 OpenAI 兼容网关 |
| `anthropic` | `POST {baseUrl}/v1/messages` | Anthropic（API 密钥或 Claude 订阅） |
| `openai-responses` | `POST {baseUrl}/responses` | Meta、OpenAI 的 ChatGPT 订阅（`chatgpt.com/backend-api/codex`） |

- Responses 适配器：系统提示走 `instructions`，消息与工具调用走 `input`，推理档走 `reasoning.effort`，`store:false`；流按 `output_text.delta` / `function_call_arguments.delta` / `response.completed` 解析；七档里 `Max` 与 OpenAI 兼容一样夹到 `xhigh`。
- 重试只发生在**首个事件之前**；`HttpClient` 是无限超时，停滞由每次读取的空闲超时（默认 120 秒）判定。
- OpenAI 兼容适配器写出消息时合并相邻同角色 `user`/`assistant`（文本按序拼接、段落间空行，`tool_calls` 保序）；`tool` 与 `system` 不合并，同一历史序列化结果字节稳定。
- 三家适配器都按各自协议编码图片（`image` source / `image_url` data URL / `input_image`）、文件附件与工具结果图片；没有线路形状的组合在构造请求体时抛不可重试错误，不静默丢弃。

## 3.2 预设

内置预设（`ProviderPresets`）是“便捷接入”的唯一来源，纯数据：协议、base URL、环境变量名、文档链接、种子模型、是否支持 `/models`、缓存旋钮、可选的订阅端点。

- Anthropic（产品默认）、OpenAI、GitHub Copilot、OpenRouter、Kimi For Coding、Meta、xAI，以及无 URL 的“自定义 OpenAI 兼容”。
- **一家厂商一个预设，进入方式决定端点**：同一预设可带一个订阅端点，`ProviderDefinition.FromPreset` 按凭据种类取舍——OpenAI 的 API 密钥走 `api.openai.com/v1`，同一预设的 ChatGPT 订阅走 `chatgpt.com/backend-api/codex`；GitHub Copilot 的 API 密钥走静态 `api.githubcopilot.com` 加明文 Bearer，设备码那条是会话 token。
- 已被移除的厂商（OpenCode Zen/Go、DeepSeek）在旧配置里降级为自定义并保留原 URL，不删用户数据。

## 3.3 七种认证

| 凭据种类 | 机制 | 备注 |
|---|---|---|
| `api-key` | 密钥库或环境变量 | 环境变量优先；行内显示“环境变量 key · VAR · 托管中” |
| `claude-subscription` | 复用本机 `~/.claude/.credentials.json`，距过期 5 分钟内自动刷新并写回 | 推理请求带 `anthropic-beta` 与 Claude Code 身份 system 首块 |
| `github-copilot` | 设备码 → `ghu_` token → 会话 token | 带 `Copilot-Integration-Id: copilot-chat`；base URL 取响应 `endpoints.api`；会话 token 约半小时有效，留 60 秒余量 |
| `kimi-code` | `auth.kimi.com` 设备码 → access/refresh | `Authorization: Bearer` |
| `xai-subscription` | `auth.x.ai` 设备码，scope 含 `grok-cli:access` | 刷新响应可能省略 `refresh_token` 时沿用旧的 |
| `meta-subscription` | `auth.meta.com` 设备码 → identity token → `POST api.meta.ai/muse-code/key` 铸 24 小时 `api_key` | 到期前 5 分钟用 identity token 重铸；铸不出且带 `action_url` 时显示给用户 |
| `chatgpt-subscription` | `auth.openai.com` 设备码 → authorization code + verifier → `/oauth/token` | 账号 id 取自 access token JWT claim；每请求带 `chatgpt-account-id` 与 `OpenAI-Beta: responses=experimental` |

- 五种设备码登录的 token bundle 只存**加密密钥库**（引用 `kimi:`/`xai:`/`meta:`/`chatgpt:` + provider id），绝不进 `settings.json`、绝不写日志；刷新按提供商串行，401 只在被拒的 refresh token 仍是当前值时才清库。
- OpenRouter 走浏览器回环（PKCE）：开 `openrouter.ai/auth`，本机 `127.0.0.1` 临时端口回调，再 `POST api/v1/auth/keys` 换一把长期 key——落地为普通 API 密钥（引用 `provider:<id>`），没有刷新。
- Claude 订阅登录弹窗检测本机 `claude` 命令与登录状态；未安装给三步说明与官方安装页，已登录则“使用此登录”，保存前尽力列一次模型。
- 密钥库：`<数据根>/provider-secrets.json`，AES-GCM，密钥为 `<数据根>/provider-secrets.key`（32 字节，非 Windows 上 `0600`），`settings.json` 只存 `secretRef`。库损坏时明确报 `Problem`，绝不把“读不出”当“没有密钥”再覆盖写入。它防误泄露与云同步，不承诺抵挡能读用户目录的本机攻击者。
- 先提交配置、再写密钥：编辑提供商时定义先写 `settings.json`，成功后才写密钥；配置保存被拒时旧密钥保持原样。写盘走 `SettingsFileMutex` + `AtomicFile`，三个写者（`preferences`、`providers`、`agents`）互斥。

## 3.4 模型列表与元数据

- **`/models`**：地址由协议决定（Anthropic 为 `{base}/v1/models` 并显式 `limit=1000`，其余 `{base}/models`）；20 秒链接超时；按服务端顺序去重；上下文窗口认 `context_length` / `max_context_length` / `top_provider.context_length`，价格认 `pricing.prompt|completion|input_cache_read|input_cache_write`（每 token 值换算成每百万 token）。刷新是显式动作（点击或每日定时），启动时不逐个访问提供商。
- 拉取结果经 `ProviderModelCache` 持久化到 `<数据根>/provider-models.json`，重启后仍在。
- **元数据三源合并**：目录快照 → provider 上报 → 用户 `providers[].modelMeta`，逐字段合并，未知保持未知，绝不补 0。解析结果按（提供商, 模型）记进内存 memo，在配置保存、`/models` 刷新、快照更新三处整体作废。
- **目录快照（models.dev）**：`settings.json` 的 `modelCatalog.sourceUrl`（默认 `https://models.dev/api.json`，可换同形状镜像）每天抓一次，写 `<数据根>/model-catalog.json`；只留窗口、回复上限、四个价格、输入模态、推理标记与 `effort` 档位（约 235 个提供商 / 8993 个模型 / 1.4 MB）。抓取带 `ETag`（只在使用同一地址时发）；304 不重写模型数据但记一次 `checkedAt`；失败只记一条失败运行，磁盘旧快照继续生效。请求侧没有过滤能力，过滤只发生在解析之后：预设 → 目录提供商映射优先，映射不到的按模型 id 全局兜底。
- **全局默认上下文窗口**：`modelCatalog.defaultContextWindow` 两种写法——正整数是固定 tokens，`"50%"`（接受 `50 %`，1–1000%）是该模型自身窗口的比例。优先级：目录快照 → 提供商上报 → 全局默认 → 每模型覆盖；百分比以链上已解析的窗口取 `floor(窗口 × 比例 / 100)` 且至少为 1，窗口未知时不产生数值。
- **价格**四个字段（输入/输出/缓存读/缓存写）；表单默认只显示三列，缓存写只在提供商协议是 Anthropic 或该模型已有非零缓存写价时出现；空值是未知，不写 0。
- **模型窗口**：表格列为 显示 | 模型 | 能力 | 推理档位 | 上下文窗口 | 输入 | 输出 | 缓存读 | 缓存写（`TableView`，虚拟化）；表头、单元格表格线与隔行底色；能力列用 14 px 图标表示输入模态（文本/图像/PDF/音频/视频），推理档位列用推理图标加档位名（如 `低/中/高`），两列悬停都给文字；上下文窗口与价格内联校验（正整数/非负小数，空=未知），价格需成套填写，非法输入红框并阻止保存。
- **模型可见性**：不再是独立分区；它是模型设置窗口表格的第一列“显示”，勾选随窗口“保存”一起写 `preferences.modelsHidden`（去空白、去重、按序数排序，为空不写），表头复选框全选/取消全选当前提供商。`provider` 分区的“已连接”标题右侧显示只读统计“全部模型（可见 x 个/共 y 个）”。当前正在用的模型即使被隐藏也照常列出（它是状态而非选项）。
- 内置 Agent 默认（`settings.json` 顶层 `agents`）：`main`/`scout`/`worker`/`plan`/`reviewer` → `{ providerId?, model, thinking }`；自己没条目就用 Worker 的，再否则用第一个已连接提供商的第一个模型、思考关闭；思考档位夹到模型接受的最近一档。

## 3.5 缓存策略与降级

- 缓存策略不可移植，每家各按它的文档：
  - Anthropic：显式断点 + 寿命。系统块与消息末块取同一个 `ttl`；`auto` 先 5 分钟，一旦本实例两次请求间隔 ≥ 5 分钟（`CacheTtlPromotionGap`）就在**下一次写入**时永久升到 1 小时；`5m`/`1h` 直用。
  - OpenAI 兼容：`prompt_cache_key`（需 `provider.sendPromptCacheKey` 打开，默认关）与 `prompt_cache_retention`（`5m` → `in_memory`，其余 → `24h`）。
  - xAI：`x-grok-conv-id`（自动缓存 + 命中率）。
  - GitHub Copilot：`X-Interaction-Id`（按它分桶，不填几乎拿不到 Claude 模型缓存命中）。
  - DeepSeek/自定义：只有自动前缀缓存，不发任何请求级旋钮。
- 缓存路由键取 `CacheRoutingKey ?? SessionId`（`CacheRoutingKey` = 谱系根），交接/分叉后仍命中同一批条目。
- 被 400 拒的旋钮走**一次性降级**：缩到必需 beta、不写 ttl，按 `Id|AuthStyle|入口` 记进进程级备忘，之后不再多付一个往返；降级必须可见——`MessageCompleted.Notice` 转成 `AgentNotice`，UI 以警告色 toast 呈现，并在当轮卡片留一行说明。
- 同一前缀一小时内被整段重写 3 次时，以信息色 toast 提示一次（会话级去重，首轮全量写入排除）。
- **缓存寿命小片**（`strip-cache-ttl`，`缓存 1h` / `缓存 5m`）显示真正发出去的值；`已连接` 列表每行一个下拉（`缓存自动` / `缓存 5 分钟` / `缓存 1 小时`），只有协议里真有这个旋钮的提供商显示（Anthropic、OpenAI）。

## 3.6 官方身份（claudeWire）

- `settings.json` 的 `claudeWire` 段是**配置口，没有设置页**：`{"enabled":true,"version":"…","build":"…","entrypoint":"claude-desktop-3p","note":"…"}`；`entrypoint` 默认 `sdk-cli`（实测定为最省窗口的入口）。段缺失或开关缺失都等于**开**；只有 `false` 关闭，值不是布尔一律按缺省当开。
- 声称官方身份时同时做四件事：`system[0]` 是 billing 头（`cc_version=<版本>.<构建>; cc_entrypoint=<入口>;`，无 cache 断点）；`system[1]` 是官方身份句；`user-agent: claude-cli/<版本> (external, <入口>)`；`x-app: cli` + SDK 传输头 + `?beta=true`。缓存形状固定：两个 system 块各带 `ttl: "1h"` 断点，工具数组不带断点，消息末块 1 小时。
- `cc_version` 两段来源不同：版本来自本机 CLI（`claude --version`，保存提供商时写进 `claudeWire.version`）；后缀（`build`）不是构建号、同一安装会变，只能发请求时现问（`ClaudeCliProbe.TryReadBuild`：起 127.0.0.1 监听口跑一次 CLI，从请求体读 billing 块后立刻杀进程，不复制/记录头部）。
- 提示词跟着开关走：声称开用不自报产品名的系统提示，关用自报 Forge 的版本，两份除首句名字外逐字符相同。
- 生效条件是双条件：`claudeWire` 生效**且**确实连着一个 `claude-subscription` 提供商；只有 API 密钥的安装不声称。
- 该段由提供商保存自动维护但只增删不改值：连了 Claude 订阅而文件没有该段时追加默认值，一个都没有时删掉整段；已存在的段原样保留。

## 3.7 网络栈

`NetworkStack` 是**全部出站 HTTP 的唯一来源**（提供商、模型列表、models.dev、MCP HTTP、Web 工具；本机 IPC 不经过它）。

- 代理三态：`system`（`HttpClient.DefaultProxy`）、`none`（直连）、`manual`（`proxyUrl`，支持 http/https/socks4/socks4a/socks5，userinfo 转凭据并从 URL 去掉）；`proxyBypass` 支持 `*`、`*.域名`/`.域名`（含自身）、精确主机与 IP；回环与 `localhost` 恒直连；manual URL 无效时回退 system。
- DNS 三态：`system`（`Dns.GetHostAddressesAsync`）、`servers`（手写 RFC 1035 客户端：A/AAAA 并发、UDP 先发、TC 置位改 TCP、按服务器顺序尝试、NXDOMAIN 立即失败、校验事务 id 与问题段）、`doh`（RFC 8484 POST `application/dns-message`，id 为 0，只允许 https，回复上限 64 KB）。两者共用按 TTL 过期的缓存（默认容量 256、maxTtl 1 小时；TTL 0 不缓存，失败不缓存）。
- **私网过滤在连接时判定**：`ConnectCallback` 里对实际拨号的地址过 `AddressFilter`，拒绝时抛 `NetworkAddressRefusedException`，所以 DNS rebinding 无效；走代理时端点是代理本身，不做目标过滤。Web 抓取的请求前预检只在走代理时保留。
- 设置原子替换、无需重建客户端：`Apply` 逐字段比较，确实不同才换内层 handler，在途请求不会遇到已释放的内层；连接池 `PooledConnectionLifetime` 5 分钟只用于刷新 DNS TTL。
- `network` 分区（系统组）：DNS/代理字段、“应用”保存并生效、“测试”用未应用的候选设置解析默认 `api.anthropic.com`（15 秒）；两条内联提示：socks4 只传 IPv4 且目标主机名走系统 DNS；DoH 服务器自身的主机名先由系统解析（写 IP 形式可避免）。

## 3.8 用量日志

- `settings.json` 的 `usageLog` 段 `{"enabled":false}` 默认关；开启后每次模型请求追加一行到 `<数据根>/usage/usage-YYYYMMDD.jsonl`（UTC 日一个文件），保存即生效（录制器每次写前现读实时设置）。
- 字段：`at`、`session`、`provider`、`kind`、`model`、`entrypoint`、`cacheTtl`、`downgraded`、`outcome`、`status`、`usage{input,output,cacheRead,cacheWrite}`、`elapsedMs`、`requestId`，以及账号计量 `rateLimit{fiveHour,weekly,…}`；缺的数值省略而不写 null。
- **绝不写提示词或回复正文**：只写分析与复现需要的形状。录制是 best-effort：磁盘满、文件被占、目录被拒一律吞掉，只经诊断日志 Warning 报一次，绝不因为日志让回合失败。

## 3.9 提供商设置页与登录流

- `provider` 分区自上而下：无可运行提供商时的顶部横幅；全局“默认上下文窗口”；«模型目录»一行（状态“更新于 … · N 个提供商 · M 个模型”、可编辑地址、立即更新；刷新期间按钮禁用）；已连接列表（名称、凭据摘要、状态点、编辑、N 个模型、设为默认、删除需点两次）；自定义提供商（“添加提供商”）；内置提供商（按预设能力显示“登录”与“API 密钥”，同一家允许多份）；底部“内置 Agent 默认”（每个 agent 一行：模型下拉 + 思考下拉）。
- 添加/编辑是同一个模态：名称、Base URL（自定义才可改）、密钥（勾选=密钥本身进加密库，不勾选=环境变量名）、请求头（每行 `名称: 值`，按第一个冒号拆分、名称不分大小写、后行覆盖前行，值里的 `{session}` 按请求替换为会话 id；无会话的请求不发这条头）。底部“测试连接”通过后变“保存”；改动 URL/密钥/请求头会回到“测试连接”。取消时删除为未保存提供商写入的测试密钥。
- OpenAI 兼容的额度读数：基类读通用 `x-ratelimit-*` 头，条带显示分钟级量表（标签首词写明“限流”）。

相关文档：[02-agent-runtime.md](02-agent-runtime.md)（用量与限速、缓存小片）、[12-storage-and-data.md](12-storage-and-data.md)（`settings.json` 与密钥库文件）、[14-limitations.md](14-limitations.md)（额度读数覆盖面与未验证项）。
