---
id: forge-mcp
type: module-design
status: draft
title: Forge.Mcp
parent: forge-architecture
---

# Forge.Mcp

MCP(Model Context Protocol)客户端与服务器管理。自写 JSON-RPC 与传输,不引用 MCP SDK;只依赖 `Forge.Abstractions` 与 BCL(System.Text.Json)。

## 边界

- 支持 stdio 本地进程与可流式 HTTP 两种传输;不含 OAuth、旧版 HTTP+SSE、`resources/subscribe`、`completions`、进度通知。
- 服务器工具经 `McpToolAdapter` 暴露为 `<server>__<tool>`,副作用默认 Execute,配置 `readOnlyTools` 可标只读;脚本/MCP 工具调用仍走主程序审批。
- 配置读 `~/.forge/mcp.json` 与 `<workspace>/.forge/mcp.json`(项目按名覆盖全局),坏条目产生诊断不致命;`url` 仅接受绝对 http/https URI(拒绝相对路径与 file 等其它 scheme)。

## 关键决策

**请求生命周期。** 每请求的截止期限覆盖发送与等待响应全程,关联调用方取消与连接生命周期;超时/取消在已发出后发 `notifications/cancelled`(`initialize` 除外)。JSON-RPC id 按类型与值精确关联(字符串 id 不匹配数字 id)。

**传输健壮性。** stdio 按字节分帧,stdout 非 JSON 行记入有界诊断环且读取继续;stderr 严格有界。子进程退出时 `Closed` 携带的异常必须带退出码:Unix 上子进程退出时先关管道、父进程稍后才被 SIGCHLD 收割,所以 stdout 读到 EOF 后先给退出一个 500 ms 的观察窗口,进程确实没了就交给退出监控(它报 `exited with code N`),仍活着才报 `closed its standard output`;否则 Linux 上退出码会稳定丢失。可流式 HTTP 按 SSE 流分别维护 Last-Event-ID,每个 POST SSE 响应流独立取消/释放,旧代际消息不完成新请求;会话 DELETE 带原始会话号并有独立关闭期限。

**工具命名稳定。** 暴露名由 (serverName, toolName) 的稳定哈希消歧,不依赖列表返回顺序;同一名字在管理器生命周期内不重新绑定到别的工具。

**服务器管理。** 懒启动,状态 Stopped/Starting/Ready/Failed;连接或初始化失败停止并回收传输;调用方取消不标记失败、不安排重连;崩溃后退避重连。

## 未决

- MCP OAuth 登录、会话 id 跨重启持久化、`resources/subscribe` 与进度通知(均按需)。
- `ProviderException` 式的协议错误细节未完全类型化(部分以 DataJson 字符串承载)。
