# Forge 对比 ThinkRail 的功能缺口

日期:2026-11-12

## 方法与可信度

- ThinkRail 侧:从本机安装包(`%LOCALAPPDATA%/ai.thinkrail.app/stable/app/Resources/app/views/web/assets/*.js`)抽出约 3000 个界面文案键,按前缀归成约 60 个功能区。文案键只说明"有这个界面",不说明实现深度。
- Forge 侧:按 SPEC(含「未决」节)、`Localization/Strings.*.cs` 与源码做名字/关键词核对;已对齐项已在源码复核。
- 已对齐 ThinkRail、从本清单移除的:Git 远端领先/落后读数、逐行评审评论、图片输入(用户消息)、Ask-user 工具、Web 搜索/抓取、上下文压缩(按模型窗口的阈值、迟滞、超限兜底、`/compact`)、内置工具目录与开关。实现见各模块 SPEC。

## 缺口清单

### 一、核心体验

| # | 缺口 | ThinkRail | Forge 现状 |
|---|---|---|---|
| 1 | 工具返回图片 | 工具结果里的图片可显示(`chat.toolImages`) | 无。用户消息已支持 `ImageBlock`,但 `ToolResult` 仍是单块文本,需扩成多块 |
| 2 | 全局搜索 / 命令面板 | 文件、符号、命令、会话一处跳转,带预览 | 无,搜索框为占位 |
| 3 | Plan 会话 | 计划面板、逐项状态、修订、全部检查、下一步、从计划开 PR、导出 | 部分:TODO 分组 + PR 正文里的 plan_summaries |

### 二、Git / PR 与编辑器

4. Ship 全流程(提交、推送、开 PR、ready、修复 checks、合并 PR/本地合并、删除远程分支、收尾工作区):部分。PR 服务随 Git 面板移除,只剩片段;CI 只有 `CiChain` 自动化,无 checks 面板。
5. 编辑器 LSP 接入 UI:部分。`Forge.Editor/Lsp` 后端已在,诊断/语义 token/补全未接 UI,编辑模式为无着色 TextBox(SPEC 未决)。
6. 富文件查看器:无。PDF、notebook、图片、CSV、SVG、HTML 预览、LFS 指针、二进制占位、图片可视化 diff;目前只有 Markdown 预览。

### 三、MCP 与扩展

7. MCP 管理界面:部分。仅只读启用/禁用列表;ThinkRail 有增删改、导入、测试、重连、日志、预设。
8. MCP 审批与信任:无。审批对话框(命令/cwd/env/头)、审批后变更提示、单工具始终允许与撤销、按项目/会话覆盖、工具搜索上限。
9. MCP OAuth、resources/subscribe、progress:无(`src/Forge.Mcp/SPEC.md` 未决)。
10. 扩展包仓库:部分。无浏览/安装/检查更新,不区分内置/捆绑/已安装。

### 四、应用级与设置

11. 自动更新:无(检查、下载、安装重启、安装日志、失败恢复)。
12. 首次启动引导 + 克隆/新建项目:无。
13. 资源检查器(运行中命令与子 Agent、耗时、日志尾、停止):部分,`SessionInstruments` 雏形。
14. 零散设置:模型可见性过滤、子 Agent 并发上限/开关、聊天显示(输入框大小、消息顺序、行宽)、快捷键页、工作区闲置自动归档、通知权限引导。

### 五、较小或低优先级

消息复制、评审评论线程(Agent 回复挂在评论下)与过期评论的独立视图、会话内导航(breadcrumbs)、会话用量/耗时统计;Mermaid 与可视化卡片;终端缓冲区搜索与重启已退出终端(SPEC 已推迟);评审包与请求评审工具;布局设置对话框。不必跟进:隐私统计/问卷/反馈、JetBrains Central。
