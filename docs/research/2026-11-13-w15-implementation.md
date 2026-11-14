# W15 按提供商限速桶:实现说明与交付记录

对应 `2026-11-13-thinkrail-gap-plan.md` 的 W15 行。本文按该文档第五节的四个交付项写:方案、被否的备选、为什么最优(四条判据)、验收口径;末尾是偏差与验证记录。

## 一、交付面

新增(全部在 `src/Forge.Core/Providers/`):

| 文件 | 内容 |
|---|---|
| `RateLimitPolicy.cs` | `ProviderRateLimitPolicy`:一份进程级策略(开关、请求/分钟、令牌/分钟、自动化恢复间隔),不可变,`Normalized()` 把非法值夹回范围内 |
| `RateLimitBucket.cs` | 一个预算桶:注入 `TimeProvider`、惰性补充、两个 64 位字的发布状态、`Take` 返回四态决定 |
| `RateLimitHeaders.cs` | 通用 `x-ratelimit-limit\|remaining\|reset-{requests,tokens}` 的读取(OpenAI 与多数兼容服务器每个响应都发),含 `-reset-*` 的时长方言解析 |
| `ProviderRateLimiter.cs` | 每提供商两个桶 + 一个 hold + 一个自动化槽位 + 两个“已报上限”(`ReportedRequests/ReportedTokens`);`Acquire`(含 `out chargedTokens` 重载)/ `Settle` / `Observe` / `ReserveAutomationSlot` / `Publish`,以及进程级的 `Shared`;生效上限 = 手填/全局与已报两者的较小者 |
| `RateLimitPolicy.cs` 内的 `ProviderRateLimitLimits` | 每提供商覆盖的值类型(`RequestsPerMinute?`/`TokensPerMinute?`,缺 = 继承,0 = 不限);初版交付面清单漏了它 |

改动:

| 文件 | 改什么 |
|---|---|
| `RetryingChatProvider.cs` | 用 `TimeProvider`(退避、额度分类、预算等待);每次尝试前 `Acquire`,应答后 `Settle` + `Observe`,失败后 `Observe`;等待超过 `maxRetryAfter` 转成带 `Retry-After` 的 429 |
| `Providers/Internal/ChatProviderPipeline.cs` | `RetryMiddleware` 可接收预算,**缺省 `null` = 不限速**(评审后改:原先缺省回落 `Shared`,随手 new 的中间件会碰进程级状态);`ProviderCatalog` 显式传 `Shared`(这是运行期接线点:中间件是唯一看得见每次尝试的地方) |
| `Providers/HttpChatProvider.cs` | `ReadRateLimit` 的缺省实现从"永远 null"改成读通用 `x-ratelimit-*`:OpenAI 兼容提供商不再只能靠被拒绝来学习限额 |
| `Providers/AnthropicProvider.cs` | 覆盖该钩子时先读 `anthropic-ratelimit-*`,读不到回落通用家族 |
| `src/Forge.App/Automations/QuotaResume.cs` | 删除 `ResumeStagger`(锁 + 字典),恢复间隔改调 `ProviderRateLimiter.ReserveAutomationSlot` |
| `src/Forge.App/Automations/AutomationService.cs` | 构造参数 `ProviderRateLimiter? rateLimits = null`(缺省 `Shared`),交给 `QuotaResumeRunner` |
| `src/Forge.App/Preferences/AppPreferences.cs` | 四项偏好(开关、请求/分钟、令牌/分钟、恢复间隔秒)+ 每提供商覆盖表(`rateLimitOverrides`,id 区分大小写、保持原写法,见六之三);即“四项+覆盖表”,共五项偏好 |
| `src/Forge.App/Preferences/SettingsSection.cs` | `SettingsSectionIds.RateLimit`(`"rate-limit"`)与 order 115 |
| `src/Forge.App/Settings/RateLimitSettingsSectionViewModel.cs`、`Views/RateLimitSettingsSectionView.axaml(.cs)`、`Localization/Strings.RateLimit.cs`、`Composition/RateLimitComposition.cs` | 设置分区与文案;`Register` 里把偏好投成策略并 `ProviderRateLimiter.Shared.Publish`(启动一次 + 四项偏好任一变化时一次) |
| `src/Forge.App/Composition/FeatureComposition.cs` | 一行 `RateLimitComposition.Register(composition, settings);` |
| `tools/Forge.Bench/RateLimitScenarios.cs`、`tools/Forge.Bench.csproj`、`Program.cs` | 三行基准(成功路径、等待路径、8 线程争用)+ 对 `Forge.Core` 的引用 |
| `src/Forge.Core/SPEC.md`、`src/Forge.App/SPEC.md`、`src/Forge.Core/THIRD_PARTY_NOTICES.md` | 契约说明、设置分区说明、设计出处 |

## 二、方案

**桶(`RateLimitBucket`)。** 状态是两个 64 位字:固定点余额(1/1024 token)与上次补充的时间戳(注入的 `TimeProvider` 的时间戳域),只经原子操作发布。

- *补充协议*:补充者先 CAS 时间戳,赢下 `[上次, 现在]` 这个窗口,再按 `窗口 × 每秒补充率` 加信用(夹到容量上限);输者读到新时间戳后重试,把自己更晚的窗口叠上去。没有丢信用,也没有重复计。
- *不读时钟的快路径*:余额在低水位(容量 1/8)以上时,信用只与窗口和余额有关,推迟补充不会算错——于是成功路径只有一次 `Interlocked.Add`,一次时钟调用都不做。低水位以下才读时钟补信用。
- *结果四态*:`Authorized`(已扣)、`Wait`(带精确 retryTime;`TimeSpan.MaxValue` 表示永不补充)、`ExceedsCapacity`(单请求超过整个预算)、`NeverRefills`(空了且没有补充率),外加策略关闭时的 `Disabled`。后三者都放行——等不来的东西不该等,`ExceedsCapacity` 典型就是长上下文。

**限速器(`ProviderRateLimiter`)。** 桶按 `IChatProvider.Id` 分开(两家提供商是两个账号、两个窗口);策略是全局一份、以引用发布(`Publish`),因此读一次是一个 volatile 读,没有锁。每个提供商旁边还有一个 hold(来自"被拒的用量窗"或"剩余为 0 且有重置时间")与一个自动化槽位(每个 provider 一个 CAS 时间戳的预留)。

**校准来源。** 两条:Anthropic 自己的 `anthropic-ratelimit-*`(既有 `AnthropicRateLimits`)与通用 `x-ratelimit-*`(新 `RateLimitHeaders`,`HttpChatProvider` 的缺省钩子)。后者是这波补的实际覆盖面:用户主要跑 OpenAI 兼容端点,而它们每个响应都报 `limit/remaining/reset`,以前这些头没有任何代码读。`-reset-*` 在 OpenAI 方言里是时长(`1s`、`6m0s`、`1h2m3s`、`250ms`),也有网关发裸秒数,两种都收;解析失败就当没有,绝不当成 0。

**重试层(`RetryingChatProvider`)。** 它是最外层中间件,看得见每一次尝试,所以预算在它里面扣:重试也计费。估算 `ITokenCounter`(系统提示 + 消息 + 工具定义),应答回来后 `Settle` 用 `InputTokens` 校正(它是完整计量输入,缓存读写已含在内;评审发现原先加了两项缓存,会让长缓存会话按两倍速吃预算,已修);`Observe` 用 `ProviderRateLimit.Throttle` 与 `IsRejected` 校准上限、剩余、重置。等待超过 `_maxRetryAfter` 不睡过去,而是抛带 `Retry-After` 的 429——`ProviderQuota` 于是判成额度、`QuotaResume` 按既有语义等重置,限速层与服务端走同一条结论路径。

**QuotaResume。** `ResumeStagger`(一把锁 + 一个字典)删除,`Reserve` 改成 `ReserveAutomationSlot`。注意这里刻意**不是**令牌桶:预留会把这家提供商的下一个空档再推一个 gap(同一瞬间三次预留得到 0、gap、2×gap),否则同一时刻到点的多个等待运行会一起放行,正是原来那个 gap 要避免的。

**设置。** `rate-limit` 分区(order 115,AGENT 组,紧接模型可见性):开关(关掉它同时关掉自动化恢复间隔)、请求/分钟(1–100000,默认 600)、令牌/分钟(1000–1e9,默认 4,000,000)、自动化恢复间隔(0–600 秒,默认 10)。默认宽松:它不是节流器,而是"防跑飞"——子 Agent 扇出、自动化重试、另一个会话同时恢复,都能在一秒内烧掉一整分钟的额度。某一维为 0 表示该维度不限。

## 三、被否的备选

1. **参照 `Boarlock/TokenRateLimiter` 的原始形状:每次操作发布一个不可变 state 记录再 CAS。** 这是最直白的"不可变状态 + CAS",但每次授权分配一个对象(~32 字节)。基准的验收项是"0 字节/次",而且这段代码跑在正要序列化一大段提示词的线程上。所以只借了它的两件事:惰性补充 + 结果分四态;发布机制换成两个 64 位字。退出条件明确:如果将来有人要一个"可快照、可序列化的完整状态对象",这条仍可回退。
2. **用 `System.Threading.RateLimiting`(`TokenBucketRateLimiter`)。** 官方包,但:①它为通用场景设计,一次 `AcquireAsync` 返回一个要 `Dispose` 的 lease,零分配说法不成立;②没有 per-provider 键控、没有 `x-ratelimit-*` 校准、没有"单请求超过容量"的独立结论(它只给 `AcquireFailed`),这些都得在外面再包一层;③多一个随框架版本升级的依赖面。写 200 行断言得起的代码,比把三条语义拆到两个包之间更便宜。
3. **纯锁实现(基准里的基线)。** 无争用时它不慢(见数字),但 8 线程共享一个桶时它把同一行缓存在线程之间传递:实测 `171 ns/op` 对 CAS 版的 `66 ns/op`(最终复测)。而争用恰恰是这个组件存在的场景(并行子 Agent、多个会话同时恢复)。
4. **把请求与令牌两维压成一个"分钟份额"桶(每次取 `max(1/RPM, tokens/TPM)`)。** 只要一次原子操作、实现更短,但对混合负载会**过严**:一个长请求加一批短请求,真实约束都没到,合成桶已经拒绝。它换来的是实现简洁,代价是正确性(会平白多等),所以不做。

## 四、为什么最优(四条判据)

**性能。** 请求路径上没有锁、没有分配、成功时一次原子操作、(无 hold 时)不读时钟;UI 线程完全不参与(桶是纯数据结构,`Acquire` 在发起请求的线程上同步执行,微秒以下)。策略读是 volatile 读,设置变更不需要重启任何会话。基准见第五节(评审修复后的最终复测):成功路径 ~68 ns/次、等待路径 ~86–110 ns/次、8 线程争用 66 ns/op(锁基线 171 ns/op),三行都是 0 字节/次。

**依赖与 AOT。** 零新包(只加了 `tools/Forge.Bench` 对 `Forge.Core` 的项目引用,bench 本来就在仓内)。没有 P/Invoke、没有反射、没有 Roslyn;`dotnet build Forge.slnx` 含 Native AOT 分析器仍 0 警告。

**契约与可测性。** 契约只有四处:`ProviderRateLimitPolicy`(值)、`ProviderRateLimitLimits`(每提供商覆盖的值,初版清单漏列)、`ProviderRateLimiter`(进程级一个实例 + 可注入 `TimeProvider`)、`RateLimitDecision`(四态 + Disabled)。唯一所有者是 `Forge.Core/Providers`;App 只 `Publish` 策略,不持有桶。可测性来自注入的 `TimeProvider`:桶、校准(上限/剩余/重置→hold)、自动化预留全部可以在不睡眠的情况下钉住;重试层的等待经既有的 `delayAsync` 钩子注入,用例里不真的等。

**回滚成本。** 这一波单独 revert 是干净的:删除 `Providers/RateLimit*.cs`、把 `RetryingChatProvider` 的两个可选参数与 `RetryMiddleware` 的预算参数去掉、恢复 `ResumeStagger`、删掉设置分区与四行偏好。没有 schema 变更、没有存储格式变更、没有 provider wire 行为变更(校准只影响本地等待)。

## 五、基准(`tools/Forge.Bench -- RateLimit --quick`)

**以下是评审修复(`Take` 改 CAS 循环、生效上限取小、hold 封顶等)之后的最终复测,全文唯一的一组数字**(本机,`dotnet run -c Release --project tools/Forge.Bench -- RateLimit --quick`;实现与基线同一循环交替计时)。

此前并列保留过的“实施前 / 首版实现”数字已被取代:它们来自不同轮次、彼此不一致(曾出现 38/67/163、37.4/58.3/~150 与 38.8/72.3/185.7 三组),读相对值而不是绝对值。实施前只有锁基线(成功 70 ns、等待 64 ns、争用 197 ns/op),仅作历史参照。

| 行 | 最终复测 实现 | 基线(锁) | 比值 | 分配 |
|---|---|---|---|---|
| `ratelimit.acquire.authorized` | 67.7 ns/acquire(三次复测 60–68) | 同量级 | 0.99x | 0 字节/次 |
| `ratelimit.acquire.wait` | 109.8 ns/acquire(三次复测 86–110) | 78 ns | 1.40x | 0 字节/次 |
| `ratelimit.acquire.contended.8t` | 65.9 ns/op、14.8 Mitems/s | 171 ns/op(2.74 ms/16000) | 0.40x | 每次调用 ~2.4 KB(8 线程的 Task/状态机,不是桶);spread 1.00 |
| `ratelimit.acquire.fairness.8t` | 4.2 ms,8000 请求预算只花一次 | 28.5 ms | 0.15x | 同上 |
| `ratelimit.request.overhead`(新增:估算 + Acquire + Settle + Observe) | 2.04 ms/1000 请求(约 2.0 µs/请求) | 仅估算 1.86 ms | 1.10x | 680 B/请求,全部来自提示估算(`ApproximateTokenCounter`),限速器自己 0 |

读法:一次请求的限速净开销约 0.22–0.25 µs(新增行与“仅估算”的差),其余是提示估算本身——所以限速关闭(或没有限流器)时 `RetryingChatProvider` 整个跳过估算(评审低-12)。

**等待路径比修复前慢了一点,且在 100 ns 线上下抖动。** 修复前这行约 67–77 ns;`Take` 从“先减后回补”改成 CAS 循环(消除被拒瞬间的负余额,评审低-10)后,拒绝路径多一次读与比较,三次复测给出 86、101、110 ns,其中 1 次越过 100 ns 验收线(报 `[MISS]`,0 字节不变)。这是有意的取舍:修的是正确性缺陷(瞬时负余额会让并发请求为借来的信用被拒,`Adjust` 的夹取还会多退),代价是只在预算真的用完时才走的路径慢了约 20–40 ns;如实记录,不为过线放宽验收或改回旧实现。

**公平性附注。** 吞吐行只断言“没有一次被拒”;`fairness.8t` 用 8000 请求的紧预算、8 线程各 20000 次尝试,断言**这份预算只花一次**(超发会被抓住),并把每线程计数与锁基线同一工作量的 spread 一起打印。spread 本身不做断言:窗口只有几毫秒到十几毫秒,分散度主要反映任务怎么落到核上(本轮最慢线程 0/8000、锁基线 spread 136.9),把它写成验收等于断言调度器。

最终复测验收 5 项里 4 项通过(0 字节/acquire、8 线程争用无一被拒、紧预算 8000 只花一次、总开销行只报告);`acquire.wait` 的 <100 ns 三次里 2 次过、1 次 MISS(见上)。

## 六、偏差与越界(已记录)

1. **改了清单外的 `Providers/HttpChatProvider.cs`、`Providers/AnthropicProvider.cs` 与 `Providers/ProviderHeaders.cs`**:计划的 W15 行写的是"用 `x-ratelimit-*` 响应头校准",但代码里只有 `anthropic-ratelimit-*` 的读取,通用家族没有任何消费者;不补这两处,校准对 OpenAI 兼容提供商完全不存在。`ProviderHeaders` 里新增 `ReadFirst`(读一个响应头的第一个非空值)并把 `AnthropicRateLimits` 与新 `RateLimitHeaders` 各自的那一份私有副本改为共用——Deslop 报这两处是 `identical` 克隆,而"移动重复代码时选所有调用者都能依赖的最通用模块"要求把它放到两边都已在用的模块。这一处会改到已交付的 `AnthropicRateLimits.cs`,但只是删掉 15 行重复实现、行为不变(既有 523 个 Providers 用例全绿)。
2. **改了清单外的 `Providers/Internal/ChatProviderPipeline.cs`**(`RetryMiddleware` 多一个可选预算参数)。不改它就得让 `ProviderCatalog` 自己构造限速器并透传,那是三个文件的改动;而"每次尝试都要计费"只有最外层中间件满足。
3. **改了清单外的 `src/Forge.App/Automations/AutomationService.cs`**(两个只读字段与一行构造实参)。`ResumeStagger` 是它 new 出来的,删除后必须在这里换成限速器。
4. **改了 W0 冻结的 `Preferences/AppPreferences.cs`、`Preferences/SettingsSection.cs`、`Composition/FeatureComposition.cs`**:W15 的四项设置在 §6.2 里原本没有登记(登记表是 W0 一次性写完的),而"容量/速率进设置"是 W15 自己的要求;三处都是新增行,不动既有内容。§6.2 的补登记见计划文档。
5. **每提供商数值覆盖**:首轮没做,后在六之三补做,**现已完成**(设置分区内的覆盖表,id 区分大小写保持原写法);服务端报的真实限额与手填取较小者。
6. **自动化间隔不是令牌桶,是预留**(见第二节末)。行为与旧 `ResumeStagger` 逐个数值一致,测试照旧钉 0 / gap / 2×gap。
7. **`RateLimitDecision` 比计划多一个 `Disabled`**:策略关闭时必须有一个"没扣、直接发"的结论,不能借 `Authorized`(那会让人以为扣过了)。

## 六之二、Deslop 复检(新增代码是否又造了克隆)

写代码前跑过一次全仓报告,确认仓库里没有可复用的桶/补充实现;交付前对同一份报告复核,新增文件命中的簇只有以下三类:

1. **`identical`:`RateLimitHeaders` 与 `AnthropicRateLimits` 的"读取头里第一个非空值"** —— 已抽取到 `ProviderHeaders.ReadFirst`,两边共用(见第六节第 1 条)。
2. **`nearly_identical`:设置分区视图模型的样板** (`RateLimitSettingsSectionViewModel` 与 `ResourceInspectorSettingsViewModel`/`FilesSettingsSectionViewModel`/`ChatSettingsSectionViewModel`,mass 78–195)。这是仓库里每个设置分区都在用的形状(直接读写偏好 + 暴露 Min/Max + 一行 XML 注释),`agents`/`files`/`inspector`/`chat` 之间本来就互为克隆。要消掉得抽一个 `SettingsSectionViewModel` 基类,那是跨多个已交付分区的重构,不在本波范围;**故意保留**。
3. **`loosely_similar`/`structural_only`**:`ProviderRateLimiter` 与仓库里其它无锁/`Volatile`+CAS 代码(`PrReplyLedger`、`PluginLoadContext` 等)同形——是语言习语,不是克隆;`RateLimitPolicy` 与 `ReviewPackageLimits`/`ToolOptions`(带默认值的 `record` 配置);`RateLimitComposition` 与其它 `*Composition` 模块(组合根的样板,`FeatureComposition` 就是按这个形状设计的);`RateLimitScenarios` 与其它 bench 场景文件(场景骨架)。

## 六之三、后续增量:每提供商数值覆盖(用户确认后补做)

首轮把它记成"未做",理由写的是"唯一诚实入口是提供商编辑弹窗";这个判断不成立——`rate-limit` 分区自己就能列出提供商目录,归属路径内即可完成。补做内容:

- **Core**:`ProviderRateLimitLimits(int? RequestsPerMinute, int? TokensPerMinute)` 与 `ProviderRateLimitPolicy.Overrides`;`LimitsFor(providerId)` 给出该提供商的生效值(缺的维度继承全局,0 = 不限),`Normalized()` 把负数夹成 0 并去掉"什么都没写"的条目。`ProviderRateLimiter` 建桶与 `Publish` 重配都走 `LimitsFor`,所以覆盖出现、改变、清除都在下一次请求生效;跳过某一维度的判断也用它,覆盖里的 0 才真的读作"这家不限"。
- **App**:`preferences.rateLimitOverrides`(归一化条目 `id|requests|tokens`,空字段 = 继承,按 id 排序,超范围夹取而不是丢弃——手写的"十亿"是想要一个上限,不是想要默认值)、`RateLimitOverrideFor`/`SetRateLimitOverride`;分区里一张按 `composition.Providers.Catalog` 重建的提供商表(`RateLimitOverrideRowViewModel`,两个可空 `NumericUpDown`,留空 = 继承,清空两格 = 删掉条目);`RateLimitComposition.Publish` 把条目投成策略里的覆盖。
- **测试**:Core 6 个(只影响该 provider、0 = 不限、单维覆盖另一维继承、清除后回到全局、归一化丢空条目、**响应头优先于手填覆盖**),App 7 个(归一化与读回、增删只动该 provider、持久化往返、表按活目录重建且行写穿、headless 渲染含空状态、策略携带覆盖、**已移除提供商的残留条目可清除**)。
- **增量评审**(同一位 `glm-5.3` high,`Approve`)提的两条非阻塞项已处理:补“响应头优先于覆盖”的用例;删掉分区 VM 里没人调用的 `ClearOverrides()`。评审也指出残留条目“不可见、不可编辑、同 id 重连会静默继承”,于是补了 `ClearUnusedCommand` + “清除已移除提供商的限度”按钮(与 `models-visibility` 的“显示全部”同一理由:不可见的偏好也要有出口),并钉了用例。

## 七、验证

实现会话(合入前,本机):

| 命令 | 结果 |
|---|---|
| `dotnet build Forge.slnx -c Debug` | 0 警告 0 错误(含 Native AOT 分析器) |
| `dotnet test tests/Forge.Core.Tests --no-build`(承载改动的项目全量,L2;评审修复后) | 2415 通过 / 0 失败 / 0 跳过,36 s |
| `dotnet test tests/Forge.Core.Tests --no-build --filter "FullyQualifiedName~RateLimit"` | 132 通过(评审修复后) |
| `dotnet test tests/Forge.App.Tests --filter "FullyQualifiedName~RateLimit\|FullyQualifiedName~QuotaResume\|FullyQualifiedName~Localization\|FullyQualifiedName~MinorRegression\|FullyQualifiedName~Settings\|FullyQualifiedName~Views"` | 866 通过(评审修复后;含混合大小写 id、未读目录、节流条带、限速组合新用例) |
| `dotnet test tests/Forge.App.Tests --no-build --filter "~RateLimit\|~QuotaResume\|~SettingsPanel\|~SessionInstruments\|~Usage"` | 133 通过 |
| 源码扫描门槛(`~Localization\|~MinorRegressionTests\|~KeyRegistry\|~StringTable\|~NoHardcodedChinese`) | 136 通过 |
| `dotnet run -c Release --project tools/Forge.Bench -- RateLimit --quick` | 5 项验收 4 项通过,`acquire.wait` 的 <100 ns 三次里 1 次 MISS(数字与解释见第五节) |

**失败记录**:初版 App 过滤第一次跑时 testhost 在 438 个用例后崩溃(无失败断言、无堆栈),重跑同一过滤 799 全绿;评审修复轮同样出现 1 次 testhost 崩溃(26 例后,重跑 133 例全绿 3 次)与 1 次未能定位的单例失败(866 例中 1 例,同过滤重跑 4 次全绿);按仓库既有经验属该 headless UI 会话的偶发中断,不是本波的断言。

**没跑的部分**:`Forge.Integration.Tests` 与 `Forge.App.LiveSwitch.Tests`(仓库约定只在 L3 跑,本波不改跨模块协议);`Forge.Workspace`/`Forge.Editor`/`Forge.Mcp`/`Forge.HostClient` 一行未改,未跑其全量。

**独立评审(`custom-baidu-6f4a99/glm-5.3`,high)发现并已修的五个问题:**
1. **blocking:`Settle` 把缓存计了两次。** 原来的 `actual = InputTokens + CacheRead + CacheWrite`,而 `InputTokens` 早已包含缓存读写(Anthropic 适配器求和、OpenAI 形状传 `prompt_tokens`/`input_tokens`,`SessionUsageMath.BilledInputTokens` 正是减缓存得计费部分)——长缓存会话会按约两倍速吃本地预算,最后抛一个本不该有的 429。改成 `actual = usage.InputTokens`,并同步改了 SPEC 那句与那条 InlineData(955 → 970,两行现在给出同一个余额,双计会被它抓住)。
2. **major:`SetRemaining` 不动 `updatedAt`,校准被陈旧窗口抹掉。** 服务端的 `remaining` 已含它自己到应答时刻的补充;不推进时间戳的话,随后一次低水位补充会把安静窗口里的整段信用再叠一遍并夹到容量,桶直接回到满。现在 `SetRemaining` 先单调推进时间戳、再写余额(补充为此让位的信用由服务端数字取代)。新增用例 `Calibration_is_not_erased_by_the_refill_window_it_replaced`。
3. **minor:一个维度有余额就取消了另一维度的 hold。** `Calibrate` 原来各自 `Release`,requests(剩余 0 + reset)刚设的 hold 会被 tokens(有余额)立刻取消。现在一次读数内先算出 hold、最后统一应用,只有"看到余额且没有任何维度需要 hold"才释放。新增用例 `A_dimension_with_room_does_not_lift_the_hold_another_dimension_needs`。
4. **minor:hold 过期后仍每请求读一次时钟。** 过期时顺带把印记 CAS 归零,恢复"无 hold 不读时钟"。
5. **minor:公平性验收是同义反复。** 见第五节"公平性附注":吞吐行只断言没被拒,新增紧预算 `fairness.8t` 断言"预算只花一次"并把 spread 只作报告。

**测试会话(独立,同模型)的报告要点**:新增 10 个文件;发现并报出 1 个真实缺陷(`RateLimitBucket.Adjust` 的负 delta 被非负的 `ToUnits` 压成 0,`Settle` 的补扣半边恒不生效 —— 已修:`ToDelta` 有符号转换,两个红灯转绿),未改产品、未 Skip;另指出 `Forge.Core/SPEC.md` 中"基类返回 null"是 W15 之前的旧句(已改为"基类读通用家族")。

## 八、已知限制

- 令牌估算用 4 字符/token(`ApproximateTokenCounter`),与分批预算同一口径;真实用量在应答返回后校正,偏差只影响一两个请求的提前等待。
- 响应头校准对“报告了上限”的提供商生效:Anthropic 家族与**任何发通用 `x-ratelimit-*` 的 OpenAI 兼容端点**(基类 `ReadRateLimit` 的缺省实现;**推翻了初版“目前只有 Anthropic 家族”的说法**)。生效上限 = min(手填/全局, 已报),已报值只收紧、不放宽。其余提供商按设置里的数字与 `429 + Retry-After` 的推断走。
- 通用头读数只有 `Throttle` 没有窗口,条带的额度小片因此对所有 OpenAI 兼容提供商出现(显示分钟级量表,有测试钉住);用量日志的 `rateLimit` 节点仍只由 Anthropic 读数产生(`UsageAttempt` 只读 `AnthropicRateLimits`),也有测试钉住。
- `Calibrate` 假定 `x-ratelimit-limit-requests` 是每分钟上限;发日/小时额度的兼容服务会被当成分钟桶(见 Core SPEC 未决)。
- hold 有出口:单次封顶 1 小时,任何一次 `Publish` 清除;裸数字重置值按时长封顶 365 天,大于 1e9 当 unix 时间戳。
- 交互回合在预算里最多静默等待 5 分钟而不发事件,是否加 `AgentNotice` 待用户决定(Core SPEC 未决)。
- 令牌维度只计输入;服务端 TPM 通常含输出,靠响应头剩余值校准追平。
- 单请求超过整个预算时放行(不等待、也不部分扣),这是有意的:拒绝会让长上下文直接不可用,而提供商会自己给出结论。
