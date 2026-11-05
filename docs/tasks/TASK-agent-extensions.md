---
id: task-agent-extensions
type: task-spec
status: draft
title: 审批对话框 + Agent 自主创建/管理扩展
parent: forge-app
---

# 审批对话框 + Agent 自主创建/管理扩展

## Request

goal:用户侧动态扩展——Agent 可在使用中自主发现能力缺口,创建、修改、注册、禁用、删除工具与插件;扩展持久化、重启自动恢复;插件默认未批准,按源码哈希审批。用户决定(2026-11-05):**先做审批对话框;Agent 提交后由用户当场批准才生效**。

## Product(已确认)

- **审批对话框**(补上长期缺口:现在 Ask 类工具调用等于拒绝):
  - 工具调用审批:显示工具名、参数、副作用、工作目录;允许 / 拒绝 /(可选)本会话内对同类调用记住。
  - 插件审批:显示源码(新建为全文,修改为 Diff)、manifest 权限声明、源码哈希;批准后热加载。
- **Agent 扩展工具**:创建、修改、禁用、删除扩展;形态为声明式工作流(L0)与 C# 插件(L2)。创建/修改提交后进入“待审批”,用户批准才注册;禁用与删除不需要审批(删除需确认)。
- 扩展持久化,重启自动恢复(已批准的按哈希恢复;未批准的保持待审批)。

## 已细化(2026-11-05 用户回答)

- **记住**:分本会话、本项目、永久三档,只记“允许”,在设置分区 `extensions` 撤销。
- **存放**:全局插件目录 `AppPaths.PluginsDirectory`。常规工作区的 `extensions/` 仍是人工草稿区,永不自动加载,Agent 扩展工具不碰它。
- **形态**:非模态审批队列面板,不阻塞其它会话。
- **修改已批准插件**:新版本批准前旧版继续生效,批准后切换;拒绝等于什么都没发生。

## Design(2026-11-05 用户已确认)

### 1. 工具调用审批(App)

`src/Forge.App/Approval/` 新增两个类型:

- `ApprovalQueue`:队列归 UI 线程所有,每个条目是一张工具调用卡或插件审批卡。
- `SessionApprovalHandler : IApprovalHandler`:每个主会话一个,持有会话 id、标题和项目 id。子 Agent 共用同一个 `AgentHost`,所以它们的请求自然归入本会话。`RequestAsync` 的处理顺序:
  1. 先查记住规则,命中就直接返回 `Allow`。
  2. 没命中就入队,`await` 用户决定。
  3. `ct` 取消时撤下卡片,按取消返回;循环照旧补“已取消”。
- Core 的 `approvalGate` 保证每个会话同一时刻最多一张卡;多个会话的卡在队列里并列。

**工具调用卡**显示会话、Agent id、工具名、副作用、工作目录和参数(缩进 JSON,长参数可折叠)。按钮有“允许”“拒绝”,以及“记住并允许”的三档:本会话、本项目、永久。

**记住规则 `ApprovalRules`:**

- 键 = 工具名 + 副作用 + 作用域(会话 id、项目 id 或全局)。
- 会话级只存内存。项目级和永久写到 `<数据根>/approval-rules.json`,由本目录自己管理:源生成 JSON,用 `Forge.IO.AtomicFile` 写,在后台线程写。不放进 `AppPreferences`,避免改共享文件。
- 项目 id 从会话目录(`ISessionCatalog`)取;常规会话归 General 项目。
- 文件损坏时一律当作“没有记住”(失败即封闭),并把原文件备份成 `.bak`。
- `extension_delete` 和所有插件审批都不能记住。
- 插件新版本获批时,清除该插件所有工具名下的记住项。原因是 `ApprovalRequest` 只带工具名,不清除就会把对旧代码的信任带给新代码。

**策略:**

- `extension_create`/`extension_update`/`extension_disable`/`extension_list` 设为 `Allow`:它们只写暂存区,真正的闸门是插件审批卡。
- `extension_delete` 走 `Ask`,这次审批就是删除确认。

### 2. Agent 扩展工具(Core,`src/Forge.Core/Tools/`)

Core 只依赖 Abstractions,所以在 `Forge.Core/Tools/IExtensionAuthoring.cs` 声明一个窄接口,由 App 用 HostClient 实现:

- 接口方法:`ListAsync`、`SubmitAsync(ExtensionDraft)`、`DisableAsync`、`DeleteAsync`。
- App 的 `SubmitAsync` 依次:暂存 → 插件审批卡入队 → 等用户决定 → 批准则提交、拒绝则丢弃 → 返回结果文本。
- Core 拿不到“提交/批准”能力,人工闸门只存在于 App 的实现里。

新增五个工具:`extension_list`(只读)、`extension_create`、`extension_update`、`extension_disable`、`extension_delete`。

- create/update 的参数:`id`、`version`、`kind`(`workflow` 或 `plugin`)、`files`(相对路径 → 文本)、`permissions`、`dependencies`。
- 调用一直阻塞到用户决定。Agent 当场就能拿到结果:要么是已注册的工具名,要么是被拒绝或编译失败的原因。

只有根 Agent 能用这五个工具:它们不进内置 Agent 白名单,内置 Agent 的可缓存工具集不变。

**热加载**(需越界,见 §5):

- 新增 `AgentHost.ReplaceExternalTools`。循环在两次模型请求之间检查版本,有变化就重新 `Select`。
- 批准后的下一次模型请求就能看到新工具,同一回合内也是。
- 代价是该会话的提示词缓存失效一次,只在批准、禁用、删除时发生。

### 3. Host 操作(ExtensionHost + HostClient,只追加 op)

**暂存区 `<plugins>/.pending/<id>/`:**

- 只由 Host 写:Host 是插件目录唯一的属主,App 和 Core 不直接写文件。
- Catalog 扫描时跳过 `.` 开头的目录,所以暂存内容永远不会被加载。

**新增 op:**

- **`extensions.stage`** `{id, version, kind, files, permissions, dependencies}`,返回 `StagedExtension`。
  - 返回内容:每个文件的新文本(更新时附上线上旧文本)、权限、sourceHash/dependencyHash/sdkVersion、编译或校验诊断,以及是否为更新。
  - 写入校验:路径段规则与 `VersionedOutputStore` 相同;禁止 `..`、绝对路径和保留名(`manifest.json` 除外)。
  - 大小上限:单文件 ≤256 KiB,总量 ≤1 MiB。扩展名白名单:`.cs`、`.json`、`.md`。
  - 写完后,在暂存目录上走同一条“发现/准备”路径,算出哈希和诊断。
- **`extensions.pending`**:返回全部暂存项,重启后用来恢复待审批。
- **`extensions.commit`** `{id, sourceHash, dependencyHash, sdkVersion}`,在该插件的闸门内依次:
  1. 按暂存目录当前字节重算哈希,与卡上展示的比对;不一致返回 `approval_mismatch`,什么都不动。
  2. 交换目录:线上目录移到 `.trash/`,暂存目录移到线上。
  3. 只刷新这一个插件,再用这组哈希走既有的 `plugin.approve` 路径;失败就把旧目录换回来。
  4. 成功后发 `tools.register`,删除 `.trash/` 里的旧目录。
- **`extensions.discard`** `{id}`。
- **`plugin.disable` / `plugin.enable`** `{id, version}`:调用 `ApprovalStore.Disable`/`Enable`,同时注销工具、停掉 Worker。enable 只在设置分区提供,不开放给 Agent。
- **`plugin.delete`** `{id, version}`:先 `Disable`,再标为 `Uninstalled`,删除目录,注销工具。
  - 之后再提交同 id、同版本会保持 `Blocked`,审批规则不削弱;Agent 必须换版本号,工具会在结果里说明这一点。

**声明式工作流(L0):**

- manifest 新增三个字段:`kind: "workflow"`、`workflow: "workflow.json"`、`tool: {name, description, inputSchema}`。
- 审批:sourceHash 是 `workflow.json` 归一化后的哈希,dependencyHash 只含 `perm`/`dep` 域。和插件一样,默认未批准,重启时按哈希恢复。
- 执行:`plugin.execute` 命中工作流插件时,Host 直接用 `Forge.Workflows.WorkflowEngine` 运行,不启动 Worker。每一步的工具调用都经同一套 run 绑定,转成 `host.tool_call` 回到主程序逐步审批,记住规则同样适用。
- 工具参数通过新增的 `$input` 绑定根传给各步骤(需越界,见 §5)。

**HostClient:**

- 上述 op 对应的方法加在 `IExtensionHostClient`/`ExtensionHostClient` 上。
- 应答逐行校验,出错就抛出,规则与 `plugins.status` 相同。

### 4. 插件审批卡、队列与设置分区

**插件审批卡:**

- 新建扩展显示全文;更新显示与线上版本的逐文件 Diff,复用 Forge.Editor 的 Diff 模型。
- 同时显示 manifest 权限、依赖、三种哈希和编译诊断。
- 有编译错误时不入队,`SubmitAsync` 直接把诊断返回给 Agent。
- 只有“批准”和“拒绝”,没有“记住”。批准时把卡上展示的哈希原样传给 `extensions.commit`。

**队列面板:**

- 主窗口右下角的叠加层,宽约 420px,可折叠;每张卡标注来源会话。
- 有待审批项时,在会话标签和状态栏显示计数。
- 重启后,`extensions.pending` 里的项以“重启前提交”卡片出现,不阻塞任何 Agent。

**设置分区 `extensions`**(`SettingsSectionIds.Extensions`)分三块:

- 已安装扩展:显示状态,可以启用、禁用或删除;删除需要二次确认。
- 待审批项:点击打开审批卡。
- 记住规则:按作用域分组,可以逐条撤销。

**文案:**全部注册到 `Localization/Strings.Extensions.cs`(中英两份);视图模型订阅 `Loc.LanguageChanged`。

### 5. 越界的最小改动(用户已批准全部五项)

1. `src/Forge.Plugin.Sdk/ExtensionProtocol.cs` 与协议 DTO:追加新 op 常量和请求/应答记录。协议常量本来就定义在 SDK 里。
2. `src/Forge.Core/Agents/AgentHost.cs`、`AgentLoop.cs`:加 `ReplaceExternalTools`,并在两次请求之间重新 `Select`,用于热加载。
3. `src/Forge.App/Agents/SessionRuntime.cs`:
   - 注入 `SessionApprovalHandler`,替换现在的 `ApprovalHandler = null`;
   - 追加 4 条 `Allow` 策略规则,并注册扩展工具;
   - 订阅 `ToolsChanged`,收到后调用 `ReplaceExternalTools`。
4. `src/Forge.App/MainWindow.axaml`:挂上队列叠加层(只加一个元素)。`AppComposition.cs` 和 `App.axaml` 只追加注册。
5. `src/Forge.Workflows`:新增 `$input` 引用根(写法 `{"$ref":"$input.x"}`),同步更新校验和 README。

### 不做(延后)

- 记住“拒绝”。
- 按参数或路径细分的记住规则。
- 插件 UI 面板渲染。
- 工作流的 `retry`/`continue`。
- 项目内扩展目录。
- OS 级沙箱。

### 验证

每层都有单元/集成测试(放在 `tests/` 对应目录),覆盖:

- 暂存的路径校验与大小上限;
- commit 时哈希不一致则什么都不动,中途失败能换回旧目录;
- 删除后同一版本被封锁;
- 工作流插件经回调逐步审批;
- `$input` 绑定;
- 记住规则的各作用域、撤销,以及插件新版本获批时清除;
- 处理器被取消时撤卡;
- 热加载在同一回合内可见。

App 的 headless 测试覆盖多会话并发入队,以及审批卡的允许、拒绝、记住。完成后走一次安全评审(AGENTS.md 的 Review 角色)。

## 契约

- 审批走已有 `IApprovalHandler`(`src/Forge.Abstractions/Tools.cs`);App 实现它并注入 `AgentHost.ApprovalHandler`。
- 插件批准/撤销已有 `ExtensionHostClient.ApprovePluginAsync`/`RevokePluginAsync` 与 `plugins.status`;manifest 权限、哈希、依赖规则见 forge-extension-host、forge-approval SPEC,不得削弱。
- 新的 Agent 工具定义放 Core 工具注册表;注意内置 Agent 白名单与可缓存工具集(forge-core SPEC)。

## 归属路径

`src/Forge.App/Approval/**`(新目录:对话框与队列)、`src/Forge.Core/Tools/**` 中新增扩展工具、`src/Forge.HostClient/**` 与 `src/Forge.ExtensionHost/**` 中为扩展写入/禁用/删除新增的 op、`Localization/Strings.Extensions.cs`、设置分区 `extensions`、对应测试;SPEC:forge-app、forge-core、forge-host-client、forge-extension-host、forge-approval。

经用户批准(2026-11-05)追加:`src/Forge.Plugin.Sdk/ExtensionProtocol.cs` 及协议 DTO(只追加)、`src/Forge.Core/Agents/AgentHost.cs` 与 `AgentLoop.cs`(热加载)、`src/Forge.App/Agents/SessionRuntime.cs`、`src/Forge.App/MainWindow.axaml`(队列叠加层)、`src/Forge.Workflows/**`(`$input`);SPEC 相应追加 forge-workflows、forge-plugin-sdk。

## 注意

终端任务同时改 ExtensionHost/HostClient 的请求分发;只追加各自的 op,提交前 rebase。安全相关改动完成后必须走一次评审(AGENTS.md Review 角色)。

## 共同前提(所有五个任务)

- **底座已合入**(`19c97d8`):偏好 `AppPreferences`/`PreferencesStore`、实时文案 `Loc`、设置分区 `SettingsSections`(`src/Forge.App/Preferences/`、`Localization/`)。用法与坑以 forge-app SPEC“偏好、文案与设置分区”为准:分区在 `AppComposition.Create()` 里向 `composition.Settings` 追加并在 `App.axaml` 加模板;文案表用 `[ModuleInitializer]` 自注册;视图模型里缓存的文本订阅 `Loc.LanguageChanged`。
- **新文案**一律注册到本功能自己的表 `src/Forge.App/Localization/Strings.<功能>.cs`(中英两份),不要改 `ViewModels/Strings.cs`,也不要改别的功能的表。
- **设置项**放在本功能自己的设置分区(`SettingsSectionIds` 里预留了 id 与顺序),不要改设置页本身。
- **并行**:五个任务在不同对话里同时进行。只改本 spec“归属路径”列出的路径;需要越界时先停下问用户。共享文件(`AppComposition.cs`、`App.axaml`)只做“追加注册”这类最小改动,提交前 `git pull --rebase`。
- **本文件位置**:`docs/tasks/`(随仓库提交,新 worktree 自带);任务完成后由本任务删除它。
- **流程**:按 brainstorming 工作流进行;本 spec 的 Product 是已确认部分,“待细化”的问题在本对话里向用户问清再定 Design。分工与模型按 `AGENTS.md`。
