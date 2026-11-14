# 规格清理报告(草稿,最终版在审计结束时定稿)

本轮清理直接修改原始规格文档。每条目:位置 → 改动 → 原因/证据。

## 根级规格

### architecture.md
1. §分层与依赖方向 项目表:补齐缺失的 5 个项目行(`Approval`、`Workflows`、`Scripting`、`ScriptRuntime`、`ScriptExecutor`),职责取自各自 SPEC 与 csproj 实际引用。原因:表只列 13 个项目,实际 18 个,缺行导致依赖方向核对无依据(规格缺失,证据:src/*/*.csproj)。
2. 项目表:删除全部「(新增)」标记。原因:过时——这些项目早已落地,「新增」是相对旧版的历史叙述。
3. `Forge.PluginWorker` 行:「加载并执行动态 C# 插件与 run_code 脚本」→「加载并执行一个动态 C# 插件(run_code 走 ScriptExecutor)」。原因:与同文 codemode 决策(一次性执行器架构)及 PluginWorker/ScriptExecutor SPEC 矛盾(F-ARCH-11,FAIL→规格错误修正)。
4. `Forge.ExtensionHost` 行:「Roslyn 编译与缓存」→「编译服务(经 Scripting)」,补「审批把关、执行器管理、进程归属、交互终端 PTY」。原因:编译实现实际在 Scripting 项目;职责描述与 EH SPEC 对齐。
5. 分层总结句:重写主程序侧依赖闭包与 JIT 侧依赖(补 Approval/HostClient/Scripting/ScriptRuntime/ScriptExecutor/Workflows 的真实引用关系);删「(新分层待 Stage 0–2 验证,未确认)」。原因:与同文「Stage 0 实测确认」矛盾;闭包描述缺项目(证据:csproj ProjectReference 全量核对)。
6. §Native AOT 决策:「五个主程序项目…已开启 IsAotCompatible」→ 列全 12 个主程序侧项目。原因:数字过时,实际 13 个 csproj 开启(含 App)。
7. §codemode 决策:删「取代此前的 T1/T2/T3 分层;dotnet run 文件式应用(T3)只作为可选能力」;「work78 分段测量」→「分段测量」。原因:历史叙述与内部任务编号,对理解当前架构无意义;测量数字保留(有效证据)。

### goal-and-requirements.md
8. §Capabilities codemode 条:「在 Extension Host/Worker 中并行按需调用」→「经 Host 常驻编译服务编译、在一次性执行器中并行按需调用」。原因:与已确认的执行架构一致(architecture.md codemode 决策)。
9. §Technology 表 codemode 行:同上修正。原因:同上。

### testing.md
10. §结构性问题 2:「25 个测试类」→「22 个测试文件」。原因:夹具合并(commit 7a50591)后数量漂移,grep 实测 22 个文件含 TempDataRoot。

### src/Forge.ExtensionHost/SPEC.md(frontmatter)
11. depends-on 补 `forge-workflows`。原因:csproj 实际引用 Forge.Workflows,frontmatter 漏记(spec-graph 边与真实依赖不一致)。

## 模块 SPEC「未决」节迁移(来自已删除 task-spec)

### src/Forge.App/SPEC.md
12. §未决 新增两条:会话/工作区生命周期刻意推迟项(只读并行会话、单会话多工作区、恢复到某轮、子 Agent 跨工作区派发、obj/bin 抢占)与否决项(排队自动接手、rebase/cherry-pick 并回、第二实例转交);界面小项推迟(ChangesPanel 徽标、文件面板错误位、SemiBold 混重、图标打磨、箭头槽宽)。来源:TASK-session-worktree-lifecycle §9、TASK-files-tab-count-badges、TASK-panel-title-weight、TASK-icon-migration 的推迟节。

### src/Forge.Core/SPEC.md
13. §未决 新增一条:额度读数只覆盖 Anthropic,非 Anthropic 额度头/设置页额度摘要/历史曲线/跨会话持久化未做,配额横幅文案待用户选择。来源:TASK-provider-quota-meter 推迟节。

### src/Forge.IO/SPEC.md
14. §未决 新增一条:回收站过期清理、容量上限、网络盘导出兜底、jsonl 镜像联动推迟。来源:TASK-session-worktree-lifecycle §9。

## 删除的 task-spec(12 份,gitignored 本地工作文档)

按 spec-graph 约定「task-spec 是临时工作文档,工作落地后删除」。12 份全部描述已落地工作,规范性内容已被模块 SPEC 吸收(关键标识符逐一 grep 核实:claudeWire、FluentIcon/Icons.axaml、model-catalog.json、IsMainCheckout、count-badge、PanelStackSolver、occupancy_key 等),仍有效的推迟项已迁移(见上)。删除清单:
- task-branch-pr-strip → App SPEC「远程读数/出货行/PR 检查面板」
- task-claude-wire-claim → Core SPEC「官方身份」+ App SPEC「claudeWire 段」(含 sdk-cli 默认值实测证据)
- task-files-tab-count-badges → App SPEC「文件 panel」徽标段
- task-icon-migration → App SPEC「配色/图标」段
- task-inspector-column-splitters → App SPEC「检视区栈/PanelStackSolver」
- task-model-catalog-refresh → App SPEC「目录源 models.dev」
- task-panel-title-weight → App SPEC「Panel 标题栏」
- task-project-panel-branch-and-toast → App SPEC「项目区/仓库根检出」(Toast 推迟已被 W0 通知系统取代)
- task-provider-quota-meter → Core SPEC「额度读数」
- task-provider-settings-redesign → App SPEC「provider 分区与三个窗口」
- task-session-worktree-lifecycle → Core SPEC「会话生命周期 v5–v9」+ App SPEC「会话生命周期」
- task-todo-plan-structure → Core SPEC「todo 工具」+ App SPEC「TODO 面板/计划面板」

## 待模块审计发现到达后继续清理

(占位:各模块 SPEC 的冗余/过时/矛盾清理将在此追加。)