# 调研:CodeAlta.Automations(重点:定时任务)对 Forge 的借鉴

- 来源:<https://github.com/CodeAlta/CodeAlta/tree/main/src/CodeAlta.Automations>(BSD-2-Clause,内化须保留署名),用户文档 `site/docs/automations.md`。调研快照日期 2026-11-11。
- 规模:约 1 万行 C#,`net10.0`,只依赖 Orchestration 与 Git 插件;项目注释原话:"knows no window and no network"——引擎与 UI、网络解耦,由宿主注入 runner/clock/feed。
- Forge 现状:没有任何定时/自动化能力;已有 `TimeProvider` 注入习惯(通知模块及其假时钟测试)、`Forge.Approval` 的哈希审批、SQLite 会话存储(含 worktree 占用唯一索引)、`NotificationCenter`。没有托盘常驻。

## 1. CodeAlta 的模型一句话

**Automation = 提示词(或脚本)+ 触发器列表 + "用什么跑"(provider/model/effort/权限模式/沙箱)**。每次触发产生一个 **Run**,一个 Run 新开一个会话;用户像看普通会话一样看结果(侧栏带闪电标记,首卡显示"由哪个自动化、因为什么触发")。定义(配置文件)与状态(每实例一个状态文件)分开存。

触发器:Hourly / Daily / Weekly / Cron;事件类有 Issue、PR、Command、Jira、Slack、incident.io、Webhook、Project、Automation(另一个自动化结束)、插件贡献的触发器。没有触发器 = 只能手动 Run now。

## 2. 定时任务的核心实现(最值得照搬的部分)

### 2.1 调度表达:统一成位集规则(`AutomationSchedule.cs`,212 行,零依赖)

- 四种定时触发器全部编译成同一种 `Rule(ulong Minutes, ulong Hours, ulong Days, ulong Months, ulong WeekDays, bool Either)`;Daily/Weekly 的多个时间点 = 多条 Rule,`Next` 取最小。UI 友好的 Hourly/Daily/Weekly 与专家用的 Cron 共享一套求值。
- `Next(after, zone)`:在**目标时区的本地钟面**上逐字段跳(月不匹配跳到下月 1 日、日不匹配跳到次日、时不匹配跳到下个整点、分逐分),上限 8 年,找不到返回 null(如 2 月 30 日)。
- **夏令时处理写得很对**:跳过的本地时刻(春季拨快)在"本应到达的那一刻"触发;重复的本地时刻(秋季拨回)只在第一次触发。这是自写 cron 最容易错的地方。
- Cron 语义与标准一致:5 字段、`*`/值/范围/列表/步长、月份与星期英文缩写、周日写 0 或 7;**日和星期都受限时取"或"**(cron 原义,很多实现写成"与")。
- 解析失败返回可读错误("'x' is not a minute of a cron expression"),直接给编辑器显示。

### 2.2 调度循环(`AutomationService.LoopAsync` / `Tick`)

- 单个后台循环;`_due[(automationId, triggerKey)]` 缓存每个触发器的下一次到期时间。
- `Tick(now)` 是**纯同步、可直接在测试里用任意 `now` 调用**的一步:收集到期项 → 立即把该触发器的下次时间改为 `Next(now)`(不是 `Next(due)`,所以长时间休眠后不会连发补跑)→ 启动 run → 返回"到下一个到期还要等多久"。
- 等待用 `Task.WhenAny(wake, Task.Delay(wait, timeProvider, ct))`:配置变化时用 TCS 唤醒;等待时间夹在 **[250 ms, 30 s]**——30 s 上限顺带兜住系统睡眠恢复、手动改系统时间等时钟跳变,无需监听电源事件。
- **迟到容忍 `Late = 2 min`**:到期时间已过但在 2 分钟内照常执行(GC 停顿、短暂休眠);更久的视为"错过"。
- 同一自动化多个触发器同时到期只起一个 run(`fire ??=`)。
- 每 60 s 重扫配置文件,捕获应用外的编辑。
- 单次 Tick 异常被吞掉,不影响下一次——循环永不因一条坏定义死掉。

### 2.3 错过的任务:心跳 + 可选补跑

- 状态里存 `AliveAt`(最后一次看过调度表的时刻),**最多每分钟写盘一次**,关闭时 `Flush()`。
- 默认:关着期间错过的就跳过。自动化开了 `catch_up` 时,启动时从 `AliveAt` 而不是 `now` 算 `Next`,于是错过的**只补跑一次**(而不是错过几次跑几次)。
- 进程意外退出时,状态里仍为进行中的 run 在下次打开时标为 `interrupted`,结束时间取 `AliveAt`。

### 2.4 并发与"跳过"要可见

- `max_runs` 默认 1(上限 16)。上一次还在跑时,**定时触发不排队,而是记录一条 `skipped` run**("The previous run was still in progress.");事件触发则等待。用户能在运行列表里看到"为什么这次没跑"。
- run 状态机:`running / completed / failed / cancelled / interrupted / skipped / waiting(等人审批) / held(预算) / queued(等名额)`。
- run 记录:触发来源、detail、会话 id、token 用量与成本、名字快照(改名/删除后历史仍可读)、`Cause`(链式触发的上游 run)。保留上限:每个自动化 100 条、总计 2000 条。

### 2.5 幂等:occurrence 键

- 一次到期 = `triggerKey@yyyy-MM-ddTHH:mm:ssZ`(UTC 到秒)。先 claim 再起 run,同一 occurrence 只跑一次("This time was already claimed by another run")。CodeAlta 用它协调多机,但单机同样有用:防止重启补跑与正常触发重复、防止两个实例同时开。

### 2.6 给用户的可预期性

- `Preview(trigger, n)`:编辑器里实时显示接下来 n 次执行时间,同时就是表达式校验。
- `Upcoming(window, slots, max)`:"未来 24 小时"时间线;把窗口切成槽,每个自动化每槽只列一次——每分钟一次的任务显示为"一直在跑",而不是一千个点。
- 全局 **Running** 开关(暂停全部触发)+ 每个自动化的启用开关;**Run now 永远可用**(禁用的也能手动跑)。
- 每个自动化可指定 `time_zone`(存 IANA id),否则用本机时区。

## 3. 定时任务之外,值得一起借鉴的设计

| 设计 | CodeAlta 做法 | 对 Forge 的意义 |
|---|---|---|
| **项目自带的自动化需批准** | 项目 `.alta/config.toml` 里别人写的自动化,需用户读过提示词点 Allow;批准的是**指纹** = SHA-256(定义的所有影响行为的字段,不含 Enabled + 脚本当前哈希 + 文件路径)。任何改动(含 `git pull` 带来的)重新要求批准。 | 与 Forge 插件的 `ApprovedHash` 模型同构,可直接复用 `Forge.Approval` 的哈希与存储。项目自动化 = 仓库里的可执行指令,必须有这层。 |
| **Agent 建的自动化默认禁用** | Agent 可以列出、创建、立即运行、禁用、删除;**不能启用**。"会自己跑的东西必须由人打开。" | 规则简单、价值大,Forge 若开放 automation 工具应原样采用。 |
| **无人值守的权限** | Autonomy:权限模式必须是"会询问"的那种(全放行被拒绝);命令/编辑走原有审批;其他工具只有列入白名单才免审批;每小时调用上限(熔断,计数持久化,重启不清零);审批超时 stop / wait;Take over 把 run 接成自己的会话。 | Forge 的审批引擎是现成落点:给 run 的会话挂一个"自动化策略",而不是靠提示词约束。 |
| **Dry run(Try)** | 跑一遍,但白名单工具不真调用,只把调用及参数记在 run 上("Recorded, not made")。 | 新建自动化时"先试一次"的安全阀。 |
| **Command 触发器** | 常驻一个命令(如 `gh run watch 123 --exit-status`),exit 0 时把输出附在提示词后起 run;无论成败都重启;5 s 内结束则退避,最长 5 min。 | 便宜又通用的"事件源",用 `Forge.IO.ProcessRunner` 即可实现,不引入任何集成。 |
| **事件水位** | `Watermark{Since, Floor, Seen(有界), Heads(PR→最后 commit), Cursor}`:首次观察设 `Since=now` 不回灌历史;每个条目只处理一次;PR 有新 commit 再触发。 | Forge 走 `gh` 做 Issue/PR 触发时直接沿用此结构。 |
| **自动化链** | "另一个自动化完成/失败"可触发;自身不触发自身;链深 5 后记为 skipped。 | 后期再做,但上限规则要一开始就定。 |
| **脚本模式** | 跑代码库里保存的脚本代替提示词;`direct` 模式无模型、无会话,只读,开写权限需沙箱。 | 对应 Forge 已保存的 run_code 脚本,适合"每晚汇总""定期清理"类零 token 任务。 |
| **配置写入** | 每个自动化一张 `[automations.<guid>]` 表;写入只替换这一张表的文本、保留文件其余字符;**写后读回校验**,读不回来就不写;单条定义坏了只报这一条(`AutomationFault`),其他照跑。 | 原则照搬,格式不照搬(见 §5)。 |

## 4. 建议 Forge 的落地方案

### 4.1 分层

| 层 | 内容 |
|---|---|
| `Forge.Abstractions` | `AutomationDefinition`、`AutomationTrigger`、`AutomationRun` 记录与状态常量、`IAutomationRunner`(起一个 run,返回会话 id + 完成 Task)。 |
| `Forge.Core/Automations`(或新建 AOT 项目 `Forge.Automations`,仅依赖 Abstractions/IO) | `AutomationSchedule`(内化 CodeAlta 版本,保留 BSD-2 署名)、调度服务(Loop/Tick)、状态存储、指纹。不引用 UI,时钟用 `TimeProvider` 注入。 |
| `Forge.App` | 组合根里实现 `IAutomationRunner`(建 worktree + 会话 + 发提示词)、Automations 页、编辑器、会话侧栏标记、完成/失败通知(`NotificationCenter`,后台时走系统通知)。 |

### 4.2 存储

- **定义**:JSON(System.Text.Json 源生成,AOT 安全)。用户级 `~/.forge/automations.json`;项目级 `.forge/automations.json`(可提交、需指纹批准)。写入走 `AtomicFile`,写后读回校验,保持其它条目字节不变。不引入 Tomlyn——不在 NuGet 白名单内。
- **状态与 run 历史**:进现有 SQLite。`automation_runs` 表对 `occurrence` 建唯一索引,天然替代 CodeAlta 的 claim;`automation_state` 存 `paused`、`alive_at`、批准指纹、熔断计数、水位。

### 4.3 与 Forge 架构约束的冲突点(需要决定)

1. **会话与 worktree 一对一、每个工作区同时最多一个活动根会话。** CodeAlta 的 run 直接在项目里开会话;Forge 不行。建议:每个 run **新建一个分叉 worktree**(基于项目默认分支),run 结束后按策略保留/归档/清理(无改动时自动清理)。若目标 worktree 正被占用,记为 `skipped` 并写明原因,而不是抢占。——**需确认。**
2. **并发归属(更正)。** Forge 的子 Agent 槽位池按会话树划分(`AgentLoop` 每棵根会话树一个 `AgentSlotPool`),不存在全局 Agent 并发上限;自动化运行新建根会话时自带槽位池,附着到已有会话的子 Agent 才与该会话共享槽位。真正需要限制的是每个自动化同时只有一个运行(忙则记 `Skipped`)。
3. **InvariantGlobalization = true。** Windows 上 IANA 时区 id 的查找与 Windows↔IANA 转换依赖 ICU,不变全球化模式下可能失败。第一版建议只支持"本机时区",或存 `TimeZoneInfo.Id` 原样;做跨平台时区前先实测三平台。
4. **只在 Forge 打开时运行。** Forge 没有托盘常驻,关窗即停;因此 catch-up 的价值比 CodeAlta 更大,且 UI 要明说"关闭期间不会执行"。托盘常驻是否要做,单独决定。

### 4.4 分期

- **P0(定时任务 MVP)**:Hourly/Daily/Weekly/Cron 四种触发器 + 手动 Run now;`Schedule`/`Tick`/`AliveAt`/catch-up 一次/`max_runs`=1 + skipped 记录/interrupted 恢复/occurrence 唯一;编辑器的"接下来 5 次"预览;全局暂停 + 单项开关;run 列表点开即会话;完成/失败通知;用户级定义。
- **P1**:项目级定义 + 指纹批准;Agent 可用的 automation 工具(建出来默认禁用);Autonomy(白名单、每小时熔断、审批超时、Take over)与 Dry run;未来 24 小时时间线;token/成本记入 run。
- **P2**:Command 触发器;经 `gh` 的 Issue/PR 触发器(水位结构);run_code 脚本模式;自动化链。
- **不借鉴**:多机/池(RunsOn、tags、server claim、lease 续约)、PublishTo/团队、on-call、环境与预算/席位、Jira/Slack/incident.io/Webhook 集成——都是 CodeAlta 的 fleet 产品面,与 Forge 的单机桌面定位无关。

### 4.5 测试要点(照 CodeAlta 的可测性设计)

- `Tick(now)` 直接驱动,自写 `TimeProvider` 假时钟(仓里已有同类写法;`Microsoft.Extensions.TimeProvider.Testing` 不在白名单)。
- `Schedule.Next`:春季拨快/秋季拨回(用固定的 `TimeZoneInfo.CreateCustomTimeZone` 构造调整规则,避免依赖系统时区数据)、2 月 30 日返回 null、日/星期"或"语义、`*/15`、`1-5`、周日 7、名称大小写、非法字段报错。
- 循环:2 分钟内迟到执行、超过则跳过;catch-up 只补一次;忙时记 skipped;重启后进行中→interrupted;同一 occurrence 不重复;暂停时不触发但 Run now 可用;一条坏定义不影响其他。
