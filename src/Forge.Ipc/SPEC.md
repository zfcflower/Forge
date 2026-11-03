---
id: forge-ipc
type: module-design
status: draft
title: Forge.Ipc
parent: forge-architecture
---

# Forge.Ipc

主程序 ↔ Extension Host ↔ Plugin Worker 之间的 IPC 协议。命名管道(Linux/macOS 由 Unix 域套接字实现)+ 4 字节小端长度前缀 + UTF-8 JSON 帧。只依赖 BCL,AOT 兼容(源生成 JSON)。

## 边界

- 一个传输实例承载一条连接;重连即新传输。传输负责帧边界,`IIpcPeer` 负责请求/响应关联。
- 消息信封:`v`(协议版本)、`id`(0 = 通知)、`op`、`args`/`result`/`error`。计算属性不序列化。

## 关键决策

**请求生命周期。** 截止期限在发送前创建、覆盖发送与等待;id 严格按值关联(不匹配的响应忽略);连接关闭使全部在途请求失败;`ping` 自动应答。

**错误类型分层。** 线格式违反 → `IpcProtocolException`;远程业务错误 → `IpcRemoteException`;超时 → `IpcTimeoutException`;断连 → `IpcConnectionClosedException`;版本不匹配 → `IpcVersionMismatchException`(拒绝该帧、连接保持可用)。

**丢弃的帧必须可见。** 传输层(`IIpcTransport.ProtocolError`)与 `IIpcPeer.ProtocolError` 对每个被丢弃的入站帧各上报一次 `IpcProtocolException`:零长度帧、超帧上限、JSON 解码失败;连接保持可用,后续合法帧照常派发。调用方据此区分"对端发来坏帧"与"超时"(`IpcTimeoutException`),不再只看到超时。

**帧上限与恢复。** 单帧上限 16 MiB;零长度帧与超限帧读侧拒绝并跳帧重同步,超限超过重同步上限(64 MiB)才关闭连接;写侧拒绝不发送。

## 未决

- 通知/响应的 `id:0`/`op:""` 默认值仍入帧(约 10 字节噪音);`IpcError` 成员为 PascalCase。集成时可选 `JsonIgnore(WhenWritingDefault)` 与 camelCase 收敛。
- 批量调用(batch)尚无契约面;`PluginId` 不在通用信封(属上层消息语义)。
