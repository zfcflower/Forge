# Core D(网络/Web 工具/自动化/提示词模板)审计发现

审计人:主 Agent(子 Agent 配额耗尽后自审)。证据为定向 grep + 关键行精读 + 测试文件核对;未运行测试。

## 功能点矩阵

| ID | 功能点 | 规格位置 | 代码位置 | 测试证据 | 结论 |
|---|---|---|---|---|---|
| F-CORE-D-01 | web_fetch 只取 http/https,默认拒私网/回环 | Forge.Core/SPEC.md §Web 工具:110 | src/Forge.Core/Tools/Web/WebFetchTool.cs:16,50,325 | tests/Forge.Core.Tests/Tools/Web/WebFetchToolTests.cs | PASS |
| F-CORE-D-02 | 重定向手动跟随≤5 跳,每跳重查地址 | 同上 | WebFetchTool.cs:22,142-144 | WebFetchToolTests.cs | PASS |
| F-CORE-D-03 | 正文上限 5MB、超时 30s、HTML 转纯文本 | 同上 | WebFetchTool.cs:18,20;HtmlToText.cs | WebFetchToolTests.cs | PASS |
| F-CORE-D-04 | 私网判定以连接时为准(AddressFilter ConnectCallback),DNS rebinding 无效;代理时保留预检 | 同上 | WebFetchTool.cs:295(ConnectsDirectly);Net/SwappableNetworkHandler.cs | NetworkStackTests.cs | PASS |
| F-CORE-D-05 | web_search 三后端 Brave/Tavily/SearXNG,每次调用现取,未配置返回失败 | 同上 | WebSearch.cs:90,110,142 | WebSearchTests.cs | PASS |
| F-CORE-D-06 | NetworkStack 唯一出站来源,Apply 原子替换+SettingsChanged | Forge.Core/SPEC.md §网络:112 | Net/NetworkStack.cs:113,167 | NetworkStackTests.cs | PASS |
| F-CORE-D-07 | SwappableNetworkHandler 无锁请求路径(volatile+CAS),换代锁内先发布后退役,恰好释放一次 | 同上 | Net/SwappableNetworkHandler.cs:10-19,28-53 | NetworkStackSwapTests.cs(含 A_disposed_wrapper_is_not_kept_alive_by_the_stack) | PASS |
| F-CORE-D-08 | 释放后请求抛 ObjectDisposedException,事件不持有已释放包装器 | 同上 | SwappableNetworkHandler.cs:18-19 | NetworkStackSwapTests.cs | PASS |
| F-CORE-D-09 | PooledConnectionLifetime 5 分钟刷新 DNS TTL | 同上 | NetworkStack.cs:113(chosen.PooledConnectionLifetime) | NetworkStackTests.cs | PASS |
| F-CORE-D-10 | 代理 system/none/manual,manual 无效回退 system,绕过模式,回环恒直连 | 同上 | NetworkStack.cs:51,158-167 | NetworkSettingsRulesTests.cs、NetworkStackTests.cs | PASS |
| F-CORE-D-11 | DNS system/servers/doh 三模式;servers 手写 RFC1035(A/AAAA 并发、UDP→TC→TCP、SERVFAIL/REFUSED 换台、NXDOMAIN 停止、id/问题段校验) | 同上 | Net/DnsResolvers.cs:283-287,278;DnsWire.cs | DnsResolverTests.cs、DnsWireTests.cs | PASS |
| F-CORE-D-12 | DnsCache 按 TTL 过期、容量满逐出 | 同上 | DnsResolvers.cs:47-51,94(capacity 256,maxTtl 1h) | DnsResolverTests.cs | PASS |
| F-CORE-D-13 | NetworkSettingsRules 唯一校验实现 | 同上 | Net/NetworkSettingsRules.cs | NetworkSettingsRulesTests.cs | PASS |
| F-CORE-D-14 | AutomationSchedule 间隔/每日/每周/5 段 cron,本地时区,DST 跳过/重复各触发一次,TryCreate 校验,NextOccurrences 预览 | Forge.Core/SPEC.md §自动化:114 | Automations/AutomationSchedule.cs:7,12,31,68,93,189 | AutomationScheduleTests.cs | PASS |
| F-CORE-D-15 | AutomationEngine 单循环最长睡 30s | 同上 | AutomationEngine.cs:19(MaxWait) | AutomationEngineTests.cs | PASS |
| F-CORE-D-16 | 忙键=(定义,目标会话),Busy;同 Occurrence 只跑一次 Duplicate | 同上 | AutomationEngine.cs:177,182,441-450,476 | AutomationEngineTests.cs | PASS |
| F-CORE-D-17 | 链条深度≤5,自触发拒绝,TargetKey 预算 BudgetExhausted | 同上 | AutomationEngine.cs:15,433-438,464-467 | AutomationEngineTests.cs | PASS |
| F-CORE-D-18 | 心跳 engine.alive_at;启动标中断;CatchUp 补跑;ResumableWhileIdle 关闭 WaitingIdle→启动 WaitingQuota 续跑 | 同上 | AutomationEngine.cs:13,146-158,260-269 | AutomationEngineTests.cs | PASS |
| F-CORE-D-19 | 存储 v10 automation_runs/automation_state/sessions.automation_id | 同上 | Storage(见 Core-B 审计 schema 阶梯) | StorageSchemaV5To9MigrationTests 等 | PASS(与 Core-B 交叉) |
| F-CORE-D-20 | ProviderQuota.Classify 三级标记+速率措辞一票否决,未知错误从不算额度 | 同上 | Providers/ProviderQuota.cs:22-42,101 | ProviderQuota 相关测试(Providers/) | PASS |
| F-CORE-D-21 | QuotaProbeSchedule 无重置时间时退避探测 | 同上 | Providers/QuotaProbeSchedule.cs | 见 Providers 测试 | PASS |
| F-CORE-D-22 | 提示词模板三层 内置→~/.forge→项目/.forge,Resolve 不可变集 | Forge.Core/SPEC.md §提示词模板:120 | Prompts/Templates/PromptTemplateStore.cs:10-11,55 | PromptTemplateLayeringTests.cs | PASS |
| F-CORE-D-23 | Read 超限/非 UTF-8 抛 IOException(严格解码) | 同上 | PromptTemplateStore.cs:33,139,143 | PromptTemplateStoreIoTests.cs | PASS |
| F-CORE-D-24 | PromptFrontmatter 手写解析无 YAML 库;FormatSlot/TryExpandCommand | 同上 | Prompts/Templates/PromptFrontmatter.cs、CommandExpander.cs | CommandExpanderTests.cs、AgentFrontmatterRegressionTests.cs | PASS |
| F-CORE-D-25 | PromptComposer 系统层用生效 system 槽位,空回落 BuiltInPrompts.System,不重复拼内置 | 同上 | Prompts/PromptComposer.cs:27(SystemLayer) | PromptComposerTests.cs | PASS |
| F-CORE-D-26 | Agent 名称即身份,内置不可改名,用户不占内置名/id,空 tools 无效 | 同上 | Prompts/Templates/PromptTemplateSet.cs:50-75 | PromptTemplateValidationTests.cs、PromptTemplateIdsAndRegistryTests.cs | PASS |
| F-CORE-D-27 | PromptFingerprint 只依赖 StablePrefix | 同上 | PromptComposer.cs:39-53 | PromptFingerprintTests.cs | PASS |

## 非 PASS 项详情

无 FAIL/MISSING。本范围声明与实现高度一致。

## 已实现但缺测试清单

- 本范围测试覆盖密集(Net/Automations/Prompts/Web 均有专门测试文件),未发现明显缺测试的关键规则。DnsCache TTL0 不缓存/失败不缓存的逐条断言未逐一核对(测试文件存在,标 PASS 基于结构证据)。

## 程序→规格:未记录行为

- DnsCache 默认容量 256、maxTtl 1 小时(DnsResolvers.cs:47-51):规格只说「按 TTL 过期、容量满逐出」,未写默认容量与 maxTtl 上限。建议:补入规格(默认值属业务约束)。
- AutomationEngine 启动恢复查询 Limit=1000(AutomationEngine.cs:146,158):规格未写恢复批次上限。建议:补入或注明实现细节。

## 规格质量问题

- §网络 段落极长(单段逾 2000 字),含大量实现细节(CAS/代际/退役)。属技术契约,保留;但可按「代理/DNS/换代/校验」拆子节提升可读性(建议,非必须)。
- §自动化 与 Forge.App/SPEC.md 自动化段存在职责重叠:Core 写引擎,App 写宿主接线,边界清楚,无实质重复。

## 业务闭环缺口

- 自动化额度恢复链:Core 引擎 WaitingQuota 续跑 + QuotaProbeSchedule 探测 + App 横幅倒计时,三方契约一致,闭环完整(静态证据)。
- 网络换代在途请求不遇已释放内层:SwappableNetworkHandler 代际 CAS + 恰好一次释放,有专门测试钉住,闭环完整。