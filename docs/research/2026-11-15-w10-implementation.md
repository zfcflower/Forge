# W10 与遗留收尾:实现说明(2026-11-15,待拍板)

对应 `2026-11-13-thinkrail-gap-plan.md` 的 W10 全部项 + 盘点出的其它未完成项。按 §五 规范:每波给方案、被否备选、四条判据、验收口径。**标注「待拍板」的项由用户本轮决定,定稿后开工。**

侦察依据:三路只读侦察(扩展/更新地基、MCP/keymap、L 系列与遗留),关键事实已核实行号。

## 〇、波次结构与并行性

契约先行(主 Agent 独占,见第一节),之后按文件级互斥并行:

| 波 | 内容 | 依赖 | Worker 模型 |
|---|---|---|---|
| 契约 | 包格式/manifest/Origin/IPC/MCP 注入接口/keymap 形状/偏好追加/预接线 | 无 | 主 Agent 自己写 |
| W10a | 扩展包仓库(浏览/安装/更新/卸载/来源区分) | 契约 | deepseek-v4-pro:high |
| W10b | 自动更新阶段 A(检查/下载/校验/说明/设置 + CI manifest) | 契约 | deepseek-v4-pro:high |
| W10c | 自动更新阶段 B(替换重启/失败恢复/安装日志) | W10a 契约 + W10b 合入 | deepseek-v4-pro:high |
| W10d | MCP OAuth + resources/subscribe + progress | 契约(先改 Mcp SPEC) | deepseek-v4-pro:high |
| W10e | keymap 收敛 + 快捷键设置页 | 契约 | deepseek-v4-pro:high |
| W16a | L2 PR 评论线程 UI + L4 会话用量汇总(已拍板) | 契约预接线;W16b 合入后(SessionView.axaml 互斥) | deepseek-v4-pro:high |
| W16b | L3 breadcrumbs + L6 终端搜索/重启(已拍板) | 无 | deepseek-v4-pro:high |
| W17 | 遗留包(已拍板四项全纳:W17a 小件包/W17b 评审可配置/W17c LSP/W17d 测试债务) | 各波合入后串行 | deepseek-v4-pro:high |
| 收尾 | 视觉手测 + L3 全量 + 文档更新 | 全部 | 主 Agent |

每波三个独立会话:Worker(deepseek/deepseek-v4-pro:high)→ 测试(deepseek/deepseek-flash:max)→ 评审(moonshotai/kimi-k3.1-pro:high),模型互不相同,各自副本(`scripts/new-copy.ps1 D:/FORGE/work24x`)。主 Agent 复核构建 + 相关测试 + 越界检查后合入。(会话可用模型注册表与 pi models.json 不同,custom-baidu/openrouter 路由不可用,故用会话内直连提供商。)

**文件冲突消解**(契约步预接线,Worker 不碰共享文件):
- `AppPreferences`/`PreferencesSection`:全部新属性由主 Agent 一次加完(沿用 W14/W15 追认先例),Worker 只读。
- `FeatureComposition.RegisterAll`:主 Agent 预挂全部新模块空壳,Worker 只填自己的模块文件。
- `MainWindow.axaml`:W10e 独占(KeyBinding 收敛);L2 的第六个覆盖层插槽由主 Agent 在契约步预加(W0 手法),W16a 不碰该文件。
- `PaletteCommands.cs`:W10e 独占(手势显示串改从 keymap 取);W16a 入口只放 PR 面板按钮,调色板命令收尾时由主 Agent 补。
- `FileDocumentView.axaml(.cs)`:W10e 独占键位部分;W17 的图片 diff 排在 W10e 合入后。

## 一、契约定稿(主 Agent 写,冻结后派活)

### 1.1 扩展包格式(zip + manifest + hash)

- **包** = zip,根目录 `manifest.json`(现有 `PluginManifest` schema 不变:id/version/kind/entryAssembly/…)+ 载荷文件。**目录即包**的现有布局不变,zip 只是分发形态。
- **完整性**:仓库索引登记 zip 的 SHA-256;安装器先校验再解包。解包后执行审批仍走现有 SourceHash/DependencyHash/ApprovedHash 链(内容变即批准失效),**不新增第二套信任**。
- **zip 安全边界**(内部常量,非可配):拒绝 `..`/绝对路径/符号链接条目(zip-slip);单文件解压上限 16 MiB、总解压 64 MiB、条目数 4096、压缩比上限 100:1(zip bomb)。
- **仓库索引 `repo.json`**(schema 1,静态 HTTPS 文件):
  ```json
  { "schema": 1, "name": "…", "packages": [
    { "id": "…", "version": "1.2.0", "kind": "plugin|workflow",
      "title": "…", "description": "…", "url": "https://…/pkg.zip",
      "sha256": "…", "sizeBytes": 123, "sdkVersion": "3.0.0", "publishedAt": "…" } ] }
  ```
- **来源维度**:`PluginOrigin { Installed=0, Bundled=1, BuiltIn=2 }`;`PluginRecord.Origin` 追加字段(默认 Installed,旧行兼容)。**持久化分工**:契约步只定枚举与字段;ApprovalStore schema v1→v2(origin 列、迁移阶梯、行映射、fail-closed)与 SPEC 记录由 W10a 交付(已在 work24a 副本实现,随波合入)。BuiltIn 不可删除/禁用;Bundled 随应用分发、首启复制到插件目录、可删;Installed 用户/仓库安装。**当前不附带任何捆绑插件**,机制先行、目录为空(诚实记录)。
- **安装 IPC**:新 op `extensions.install { sourceDir, origin }`——App 下载/解包/校验到临时目录,Host 在刷新级闸门内验证 manifest、trash 回滚式落位(复用 `RollBackCommitAsync` 机制)、同步审批记录(Origin 入 record)、`plugins.refresh` 语义。卸载/禁用走现有 op。

### 1.2 更新 manifest(`updates.json`,schema 1)

```json
{ "schema": 1, "channels": {
  "stable": { "version": "1.2.0", "releasedAt": "…", "notes": "markdown 或 notesUrl",
    "packages": { "win-x64": { "url": "…", "sha256": "…", "sizeBytes": 0 },
                  "linux-x64": { "…": "…" }, "osx-arm64": { "…": "…" } } },
  "beta": { "…": "…" } } }
```

- 渠道 token 沿用 W0 已冻结的 `stable|beta`(计划文本写 preview,以冻结偏好为准)。
- **CI**:`dotnet-desktop.yml` tag 发布时逐产物算 SHA-256,生成 `updates.json`(无预发布后缀→stable,`-beta.*`→beta)上传为 release asset;生成逻辑放 `scripts/make-update-manifest.ps1`(本地可测)。
- **当前版本**:读程序集 `InformationalVersion`(CI 已注入 `-p:Version`);`0.0.0-ci.*` 视为开发构建,只许手动检查、不参与自动检查。
- **阶段 A 终点**:下载 + SHA-256 校验通过 + 展示更新说明 + 打开下载位置,用户手动安装(已拍板)。阶段 B 加「安装并重启」。

### 1.3 MCP 注入接口(Forge.Mcp 只依赖 Abstractions + BCL,不破边界)

- `IMcpTokenStore`:按 (server url origin, resource) 键存取 token JSON;App 侧用 `ProviderSecretStore` 同款 AES-GCM 旁钥模式实现(`mcp-secrets.json` + `mcp-secrets.key`,owner-only,原子写)。**不引 DPAPI/P-Invoke**(AOT 约束,现成注释已明确)。
- `IMcpOAuthUi`:`OpenBrowser(Uri)`(App 用 Avalonia Launcher)+ 状态回调;loopback 回调用 BCL `HttpListener`(OpenRouter 已验证的模式)在 Forge.Mcp 内实现。
- **PKCE/loopback 在 Mcp 内重写而非抽公共**:Mcp 依赖边界(Abstractions+BCL)容不下 App 的 OpenRouter 助手,Core 又被 Mcp 禁止引用——记为**故意重复**并写明原因(deslop 规则允许)。
- 进度:`CallToolAsync` 生成 progressToken → `_meta.progressToken` → `notifications/progress` 按 token 路由 → `McpToolAdapter` 透传 `ToolContext.Progress`(最新尾快照语义,与 BashTool 同口径),资源检查器直接消费,无新事件。
- 订阅:`resources/subscribe|unsubscribe` + `notifications/resources/updated` → `ResourceUpdated` 事件;能力位 `ResourcesSubscribe` 已解析,缺的只是请求与路由。

### 1.4 keymap 形状

- `KeymapAction`(Id、本地化显示名键、默认手势、作用域 Window/Editor/View)+ 中央目录;覆盖存 `AppPreferences.KeymapOverrides`(归一化 `actionId|gesture` 列表,同 rateLimitOverrides 范式)。
- 可配范围:窗口命令(4)+ 编辑器菜单命令(EditorMenuBuilder 全表)+ 视图命令(save/J/补全/引用等),约 25–35 个动作。**排除**:终端 VT 透传、文本输入内部导航、面板列表上下文键(它们不是全局动作)。
- 应用:启动与偏好变更时代码构造 `Window.KeyBindings` 替换 XAML 静态集合;编辑器菜单手势从目录取;`Strings.Palette.cs:178` 等三处硬编码手势串改从 keymap 取(消地雷)。

### 1.5 偏好追加与登记(先登记再写代码)

主 Agent 一次加完并补进 gap-plan §6.2 登记表:

| 项 | 波 | 默认 | 分区 |
|---|---|---|---|
| 更新 manifest URL | W10b | `https://github.com/zfcflower/Forge/releases/latest/download/updates.json` | updates |
| 扩展仓库索引 URL | W10a | 空(=只显示已安装+本地安装) | extensions |
| keymap 覆盖表 | W10e | 空 | keymap(新 id,order 210) |
| 自动检查口径 | W10b | **每日**(用户 2026-11-15 拍板,与 W0 登记一致,不改冻结默认) | updates |
| 评审方式、步骤提交后自动评审 | W17b | subagent、关 | git |
| 终端字体族/字号、shell 路径 | W17a | 空(=现有回退)、现有默认、空(=自动) | tools |

W0 已冻结的 `UpdatesCheckFrequency`(daily/weekly/never)、`UpdatesChannel`(stable/beta)只读沿用;`SettingsSectionIds.Updates`(800)已预留。

### 1.6 契约步预接线清单

`PluginOrigin` 枚举与 `PluginRecord.Origin` 字段(ApprovalStore v2 持久化归 W10a,见 §1.1)、`extensions.install` 协议契约、MCP 两个注入接口、`AppPreferences` 全部新属性、`FeatureComposition` 空模块挂线、`MainWindow.axaml` 第六覆盖层(Comments)+ `MainWindowViewModel.Comments.cs` 槽、gap-plan §6.2 登记更新。构建全绿 + 相关测试全绿后冻结,派活文本引用本节。

## 二、W10a 扩展包仓库

**方案**:
- Host:`extensions.install` 实现(验证→trash 回滚落位→record 带 Origin→refresh);`PluginCatalog` 发现时读 origin;捆绑目录首启复制。
- App:`Services/ExtensionRepository.cs`(索引拉取/版本比较/下载+进度+取消+SHA-256,复用 `Forge.Core/Net` NetworkHandler 与 WebFetchTool 流式模式);`ExtensionsSettingsSectionViewModel` 加仓库区(浏览/安装/更新按钮、来源徽标、本地 zip 安装文件选择);`Strings.Extensions` 追加键。
- 更新比较用 `System.Version`(数字段),索引版本高于已装即提示;安装新版走同一 install op,旧版本目录由 VersionedOutputStore 保留,审批哈希变化自动失效(现有语义)。
- 下载/解包/校验全在后台线程,UI 只收进度快照;取消贯穿 HttpClient→解包→install。

**被否备选**:①GitHub Releases 专用源——把仓库绑死在 GitHub,自建索引更通用且零依赖;②App 直接写插件目录——绕过 Host 的闸门与审批记录所有权,破坏「审批库归 Host」契约;③安装即批准——破坏 fail-closed 审批链,新包必须 Draft 起步。

**四判据**:性能——下载流式、解包后台、索引按需拉取不轮询;依赖——BCL `System.IO.Compression`,零新包;契约——PluginRecord/ApprovalStore 升版由主 Agent 定稿,install op 单一所有者 Host;回滚——**不干净**:schema 阶梯不前向容忍,`user_version = 2` 的库在旧程序(上限 1)构造期 fail-closed,所以 revert W10a 必须先删 `<data>/approvals.db`(审批记录清空、插件回 Draft 重新审批);`origin` 列可留,拦住 revert 的是版本号,记录见 `src/Forge.Approval/SPEC.md` 破坏面。

**验收**:Worker 副本 `forge-test.ps1 -Level L1 -Project Forge.Approval.Tests -Ns Forge.Approval.Tests -Gates` + `Forge.App.Tests` 的 `Extensions|Approval` 落点;测试会话覆盖:zip-slip/炸弹/坏哈希/坏 manifest/取消/并发安装/Origin 迁移/版本比较/卸载后重装;评审按架构>测试>质量。

## 三、W10b 自动更新阶段 A

**方案**:`Services/UpdateService.cs`(检查调度:手动按钮 + 频率定时器,never=关;manifest 拉取→渠道选择→版本比较→下载+进度+取消→SHA-256→暂存 `<data>/updates/<version>/`→更新说明 Markdown 渲染(复用现有渲染器)→打开下载位置);`Settings/UpdatesSettingsSectionViewModel` + View(URL/频率/渠道/当前版本/立即检查);`Composition/UpdatesComposition.cs`;CI 脚本 + workflow 追加 manifest 生成上传;`Strings.Updates.cs`。检查状态(上次检查时间/结果)落 `ui_state`,不新增存储表。

**被否备选**:①Velopack——已拍板自写;②GitHub API 查 latest——API 限流且绑 GitHub,静态 manifest URL 通用;③增量更新——产物是整包 zip,增量要二进制 diff 工具,收益不抵复杂度。

**四判据**:性能——检查后台、下载流式、UI 零阻塞;依赖——零新包;契约——manifest schema 1 由主 Agent 冻结,CI 与 App 同 schema;回滚——revert 后 CI 多产的 asset 无害。

**验收**:`Forge.App.Tests` 的 `Update|Settings|Localization` 落点 + 扫描门;测试会话覆盖:版本比较(含开发构建)/坏 manifest/坏哈希/下载取消/网络失败/频率调度/渠道选择;CI 脚本本地干跑生成样例 manifest 校验。

## 四、W10c 自动更新阶段 B(串在 a+b 后)

**方案**:目录交换 + 看门狗回滚,三平台同一状态机:
1. 「安装并重启」→ 以暂存目录的新 `Forge.App` 起 `--apply-update --pid <旧> --install <目录> --staging <目录>` 进程,旧进程正常退出(先关 ExtensionHost 等子进程)。
2. updater 等旧 pid 退出(超时兜底)→ 现安装目录改名 `.old-<版本>` → 暂存内容移入 → 写 `install.log` → 起新 exe。
3. 新 exe 主窗口显示后写 `<data>/updates/ok-<版本>` 标记;updater 限时(60 s)等标记,超时/进程早退 → 回滚 `.old` 并重启旧版、日志记因。
4. 成功后下次启动清 `.old`;启动时检测到半截交换(有 `.old` 无 ok)→ 修复回滚。
- **applier 的运行位置(评审修复轮补记)**:applier 的源树(前向 = staging、恢复向 = `.old-<版本>` 备份)先复制到 `<data>/updates/.applier-<guid>/` 再从那里启动,工作目录同样是这个中性目录。Windows 不允许 rename 一个装着运行中进程映像的目录:从 staging 起会让前向每次都退化成整树 CopyTree(且遗留 staging),从 `.old` 起会让恢复向的 claim 必失败 —— 启动修复永远修不成、用户每次启动都只得到进程退出。中性目录由下次启动的 Maintain 清理(applier 删不了自己在跑的目录)。
- **恢复向的 claim 名带版本**:`.restoring-<版本>-<guid>`。崩溃落在 claim 与最终 move 之间时,启动修复在缺 `.old-<版本>` 而存在 `.restoring-<版本>-*` 时先改名回 `.old-<版本>` 再走既有修复;Maintain 仍不匹配 `.restoring-*`(不误删唯一好副本)。
- **回滚先撤确认标记**:kill 掉未确认的新进程后尽力删 `ok-<版本>`,否则晚到标记 + 回滚失败会让下次启动把 `.old` 当“已确认”而删掉唯一备份。
- Windows:等进程退出后无文件锁;rename 而非覆盖运行中文件。Unix:tar.gz 用 BCL `System.Formats.Tar` 解包(保执行权限);zip 仅 Windows。
- **签名策略已拍板**:沿用阶段 A 零签名,HTTPS + manifest SHA-256,威胁模型写入 SPEC。
- 安装日志:`<data>/updates/install.log`,有界追加;失败恢复全程留痕。

**被否备选**:①覆盖运行中文件(Windows 不可行);②常驻 updater 服务(多一个长驻进程与安装器,目录交换 + 一次性 updater 足够);③双目录 A/B 启动器(要改快捷方式与启动入口,侵入分发)。

**四判据**:性能——updater 一次性进程,无热路径;依赖——BCL Tar/Compression,零新包;契约——标记文件与命令行参数是私有协议,主 Agent 定稿;回滚——阶段 B 单独 revert 后阶段 A 仍完整(手动安装终点)。

**验收**:测试会话用假安装目录 + 假 exe 脚本钉住状态机(等待/交换/回滚/半截修复/日志),Windows 真机冒烟由主 Agent 复核;`Forge.App.Tests` 的 `Update` 落点 + 扫描门。

## 五、W10d MCP OAuth / resources / progress

**先改 SPEC**:`Forge.Mcp/SPEC.md` 边界行与未决节——OAuth/订阅/进度移出未决,会话 id 跨重启持久化与 completions 仍留未决。

**方案**:
- OAuth:401 + `WWW-Authenticate` → RFC 9728 protected-resource metadata → RFC 8414 AS metadata → RFC 7591 动态注册 → 授权码 + PKCE(S256)+ loopback 回调 → token 交换/刷新;token 经 `IMcpTokenStore` 加密持久化,按 (origin, resource) 键;`StreamableHttpTransport` 加 401 钩子与 Bearer 注入,刷新后重试一次。
- **不静默弹浏览器**:连接遇 401 → 服务器状态 Failed + LastError「需要登录」,MCP 设置行出「登录」按钮,用户点击才走流程;「退出」清 token。
- 进度:progressToken 表 + `_meta` + 通知路由 → `ToolContext.Progress`(见 1.3),检查器日志尾直接可见。
- 订阅:subscribe/unsubscribe 请求 + `ResourceUpdated` 事件;**资源浏览器(待拍板)**:MCP 设置分区每服务器资源列表/读取/订阅,更新通知即时刷新。
- stdio 传输不受影响;OAuth 只对 streamable HTTP。

**被否备选**:①设备码流程——MCP 授权规范是授权码+PKCE,设备码非标准路径;②token 明文存 mcp.json——违反「写回永远是占位符/不出现明文密钥」契约;③静默自动弹浏览器——安全与体验都差,用户显式触发。

**四判据**:性能——token 缓存内存、刷新单次,通知路由 O(1) 表查;依赖——BCL HttpListener/Json,零新包;契约——Mcp 依赖边界不破,注入接口主 Agent 定稿;回滚——SPEC 与代码同 revert。

**验收**:`Forge.Mcp.Tests` 全量(L2,接口变化)+ `Forge.App.Tests` 的 `Mcp|Settings` 落点 + 扫描门;测试会话用假 AS(本地 HTTP)钉住:metadata 发现/动态注册/PKCE 校验/刷新/401 重试/坏 token/取消/订阅通知/进度路由;评审重点:token 不落明文、依赖方向、传输健壮性契约。

## 六、W10e keymap 与快捷键设置页

**方案**:见 1.4。`Input/Keymap.cs`(目录+解析+冲突检测纯函数)、`Input/KeymapApplier.cs`(构造 KeyBinding/菜单手势)、`Settings/KeymapSettingsSectionViewModel` + View(搜索/分组/按键捕获/冲突提示/单项与全部重置)、`Composition/KeymapComposition.cs`、`Strings.Keymap.cs`;移除 MainWindow/ChangesView/FileDocumentView 的 XAML KeyBinding 改代码应用;手势显示串三处硬编码改从 keymap 取。捕获对话框:按键→规范化字符串→同作用域冲突检测→确认覆盖。

**被否备选**:①只读列表页——「快捷键设置页」的价值在可改,只读不解决 14d;②Avalonia HotKeyManager 全量替换——视图内上下文键(编辑器/终端)不适合全局热键,分层应用更稳。

**四判据**:性能——键位表启动构造一次,变更才重建,热路径(每次按键)只是现有 KeyBinding 匹配;依赖——零新包;契约——动作目录是单一事实源,显示串不再各写一份;回滚——revert 恢复 XAML 静态键位。

**验收**:`Forge.App.Tests` 的 `Keymap|Workbench|Settings|Localization` 落点 + 扫描门;测试会话覆盖:解析/规范化/冲突/覆盖持久化/重置/应用后命令触发(headless 按键);评审重点:无行为回归(原 6 条键位必须仍生效)。

## 七、W16 L 系列(已拍板:四项全做)

- **L2 PR 评论线程 UI**:第六覆盖层(契约步预接线)。线程列表(未解决/已解决/过期过滤,过期独立视图)、位置(file:line,过期显示原行号)、评论 Markdown、回复框 → `ReplyToReviewThreadAsync`、解决/重开 → `ResolveReviewThreadAsync`(接口已存在,零后端改动)、「让 agent 处理」交接草稿(同 checks/Mermaid 模式)。入口:PR 面板按钮。
- **L4 会话用量汇总**:数据已在 `messages.usage_json` 与 `turn_checkpoints`(零 schema 改动);会话级累计(打开时读库 + 运行中内存累加),汇总面(总 token/成本/缓存命中/回合数/总耗时/均值)挂输入条仪器区展开。
- **L3 breadcrumbs**:SessionView 转录区上方一行 `项目 › 工作区(分支) › 会话`,点项目/工作区聚焦项目树,子 Agent 标签显示 `父会话 › 子 Agent` 可点回父。不改输入条(其「不放路径」注释仍有效,位置不同)。
- **L6 终端搜索/重启**:终端聚焦时 Ctrl+F 出搜索条(缓冲区匹配高亮/上下跳/大小写),已退出标签出「重启」按钮(同配置新进程、保留标签)。均在 `Terminal/**`。

**被否备选**:L2 走 agent 回复而非直接 API——接口已有直接回复,多绕一层无收益;L4 新增用量表——usage_json 已落库,重复存储;L3 放输入条——违反既有设计注释。

**四判据**:性能——线程列表虚拟化、用量读库后台、终端搜索只扫缓冲区;依赖——零新包;契约——IPrPlatform 不动,存储不动;回滚——各 L 项独立 revert。

**验收**:`Forge.App.Tests` 的 `Workbench|ViewModels|Terminal|Settings` 相应落点 + 扫描门;测试会话覆盖:过滤/回复失败/解决幂等/用量累计与重启后读库/breadcrumb 导航/搜索边界/重启取消。

## 八、W17 遗留包(已拍板:四项全纳)

1. **W17a 小件包**:W13 思考节点(消费 `AgentThinkingDelta`,时间轴加思考节点)、W4 图片 diff(diff 视图图片并排)+ `pr_screenshots` 改图片返回(管道已就绪)、终端工具设置分区(字体族/字号/shell 路径,从手改 settings.json 进 UI)。
   **实施记录(2026-11-15)**:思考节点与图片 diff 已交付;终端字体/shell/参数/scrollback 五项已进 `tools` 分区 UI。**`pr_screenshots` 已改为返回图片块**(收尾波补齐,不再延后):`ScreenshotRunner` 在 `DeleteCaptureDirectory` **之前**用 `ScreenshotImages.Read` 把 before/after 两侧读进 `ScreenshotImage(FileName, MediaType, Data)`(单图 5 MiB、PNG 签名校验、读不到或超限就跳过,文本报告仍列全部文件名),工具由此构造 `[TextBlock, ImageBlock…]`。用例:`ScreenshotToolsTests.The_published_pictures_come_back_as_image_blocks_next_to_the_text_report`。
2. **W17b 评审可配置 + 结论持久化**:设置(评审方式:子 Agent 一次/人工)+ 步骤提交后自动评审触发(默认关)+ `request_review` 结论落 `reviews.db`(ReviewStore 已在)。
3. **W17c LSP code action / rename / inlay hints**:后端请求 + 编辑器 UI(灯泡菜单/重命名对话框/内联提示)。**排在 W10e 合入后**(同碰 FileDocumentView 键位文件)。
4. **W17d 测试债务 6 条**(testing.md):**排在全部产品波合入后**(App.Tests 并行化要动 22 个测试文件的 TempDataRoot.Redirect,与任何新增测试冲突)。

明确**不做/挂起**(已有用户决策,不重开):L8 布局对话框(待定,除非本轮拍板)、会话/工作区生命周期一揽子(T0)、W15 日/小时头 P3(已定不改)、「明确不做」清单全部。

## 九、视觉手测与收尾

- `2026-11-15-visual-checklist.md` 22 项全未勾。**已拍板:自动 + 人工混合**——Agent headless 自动验证可自动项(主题/渲染/语言/内边距等)并勾选;起真实 App(`FORGE_RUN=1`)供人工复核交互项,在收尾阶段执行。
- 收尾:`forge-test.ps1 -Level L3 --why merge` 全量;更新 gap-plan(W10 交付记录、§6.2 登记)、各 SPEC 未决节、视觉清单勾选;`feat/provider-cache-wire` 陈旧分支确认删除(内容已在 main)。

## 十、拍板记录(2026-11-15 用户答复,本文据此定稿)

1. 扩展仓库源:**可配索引 URL + 本地 zip**。
2. 更新源:**本仓 GitHub Releases + 自动检查默认每日**(与 W0 冻结默认一致,无需追认改默认)。
3. 阶段 B:**仅 SHA-256,零签名**,威胁模型写入 SPEC。
4. keymap:**完整改绑**(搜索/捕获/冲突/重置/即时生效)。
5. MCP:**资源浏览器 + 进度接检查器**,不加 agent 资源工具。
6. L 系列:**四项全做**(L2/L3/L4/L6)。
7. 遗留包:**四项全纳**——小件包、评审可配置+结论持久化、LSP code action/rename/inlay hints(排 W10e 后)、测试债务 6 条(排最后,全部合入后)。
8. L8:**维持待定**。
9. 视觉手测:**自动验证 + 人工复核**(headless 可自动项先勾,交互项起真实 App 留人工)。

会话生命周期 T0、W15 P3、「明确不做」清单维持原决策不重开。