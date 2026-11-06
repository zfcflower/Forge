# Git 与 PR 工作流合入后的遗留(2026-11-07)

批次 ①–⑦ 全部合入 `workspace-1`(见 74089e2、d1bf6e8、c0e919d、f1a84cf、cad66fa)。以下是有意不做或未验证的项。

## 1. 真实 GitHub 手动走查未执行

- **位置**:TASK-git-remote-sync 交付要求里“在本机对真实 GitHub 测试仓库手动走一遍 创建 → 检查 → 回复”。
- **现状**:所有 gh/PR 行为都只经假 `gh`(测试里通过 `ExecutablePath` 注入回放 JSON 的脚本/小程序)验证,没有对真实 GitHub 走查。
- **为什么接受**:真实走查需要本机已登录的 `gh` 与一个测试仓库;`pr_*`/`pr_checks`/`pr_comment_*` 的每个闸门、分页、失败分类与审批路径都有确定性单测覆盖(`tests/Forge.Workspace.Tests/Git/**`、`tests/Forge.App.Tests/Git/**`),平台差异的风险点在 `gh` 的 JSON 输出形状,而这由 `GhJsonContext` 源生成解析 + 假 gh 回放的真实输出样例覆盖。
- **建议**:在登录了 `gh` 的机器上,对一个私有测试仓库走一遍 `pr_create`(五道闸门)→ `pr_checks`(快照与等待)→ `pr_comment_reply`(apply 引用提交)→ `pr_comment_resolve`,把结果记进当次计划摘要。

## 2. SystemNotifier 的 macOS / Linux 路径未实机验证

- **位置**:`src/Forge.App/Git/SystemNotifier.cs`。
- **现状**:Windows 路径(powershell + WinRT toast)在开发机跑通(退出码 0);macOS(`osascript`)与 Linux(`notify-send`)两条路径只按工具文档写成,未在对应系统运行。
- **为什么接受**:失败只记 `Trace` 不抛,通知缺失不影响状态栏与 Git panel 的等待结果展示;参数经 `ArgumentList` 转义,不拼 shell。已写进 forge-app SPEC。

## 3. 过程:一个实现 Worker 违反副本隔离

- 批次 ③–⑤ 评审修复的 Worker 应只在 `D:/FORGE/work-git345fix` 副本里工作,实际直接改在了主仓工作树。主 Agent 已逐项复核其 diff、修复与截图 Worker 的 `PublishAsync` 接口冲突并就地提交,最终树干净;违规原因未查(可能是该 Worker 把“主仓已有 bin/obj 便于跑测试”当成了就地工作的理由)。
