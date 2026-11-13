---
id: forge-ipc
type: module-design
status: draft
title: Forge.Ipc
parent: forge-architecture
---

# Forge.Ipc

主程序 ↔ Extension Host ↔ Plugin Worker/脚本执行器之间的 IPC 传输。命名管道(Linux/macOS 由 Unix 域套接字实现)+ 4 字节小端长度前缀 + MessagePack 帧(MessagePack-CSharp v4,源生成格式化器,`DefaultAot`)。只依赖 BCL 与 MessagePack,AOT 兼容。各操作的类型化负载不在本模块:插件可见的在 `Forge.Plugin.Sdk`,宿主内部的在 `Forge.Ipc.Protocol`(`HostMessages`、`TerminalMessages`)。

## 边界

- 一个传输实例承载一条连接;重连即新传输。传输负责帧边界,`IIpcPeer` 负责请求/响应关联。
- 帧体 = `IpcHeader`(`Version`、`Id`(0 = 通知)、`Operation`、`IsResponse`、`Error`)+ 至多一个 MessagePack 负载值(请求/通知的参数或成功响应的结果;失败响应只有头)。协议版本 2。
- 负载在 `IpcMessage` 里保持编码状态,由消费者按操作约定的类型 `Read<T>()` 一次解码;不经中间 DOM。无参数/无结果用 `IpcEmpty`。工具参数是模型给出的 JSON,以原始 UTF-8 字节随负载传递,只在交给工具的那一端解析。
- **负载对编解码器是不透明的,因此也能由调用方自己编码。** `FrameCodec.EncodeRaw` 追加调用方给的负载字节,`IpcPeer.RequestRawAsync`(internal,只对测试程序集可见)用它发出格式化器不会产生的值——缺成员、非空成员上的 nil、类型不符——用来证明对端在负载无法解码时按协议拒绝,而不是崩溃或静默跳过。产品代码一律走类型化的 `RequestAsync`/`NotifyAsync`/`RespondAsync`。
- 发送帧写入池化的 `IpcFrameBuffer`(`ArrayPool`),传输在 `SendAsync` 完成前复制,调用方随即归还;入站帧缓冲交给消费者后不再复用,负载切片可直接引用。
- 命名管道在 Linux/macOS 上由 .NET 的 Unix 域套接字实现,套接字文件落在 `<临时目录>/CoreFxPipe_<name>`。完整路径上限 `sockaddr_un.sun_path` 为 108 字节(Linux;含结尾 NUL)与 104 字节(macOS),macOS 的用户临时目录本身就很长(CI 上 `/var/folders/<x>/<32 字符>/T/` 占 51 字节),因此 `ValidatePipeName` 的 108 只是名称上限、**不保证**在 macOS 上能 bind。生成名称的调用方改用 `IpcPipe.MaxNameLength`:由当前平台的套接字路径上限减去 `<临时目录>` + `CoreFxPipe_` + 1(NUL)的 UTF-8 字节数算出(取不到临时目录时保守返回 16),Windows 无路径约束、返回 108。超预算的命名在 macOS 上表现为 BCL 抛出的 `ArgumentOutOfRangeException`,不是 IPC 层的协议错误。

## 关键决策

**请求生命周期。** 截止期限在发送前创建、覆盖发送与等待;id 严格按值关联(不匹配的响应忽略);连接关闭使全部在途请求失败;`ping` 自动应答。

**错误类型分层。** 线格式违反 → `IpcProtocolException`;远程业务错误 → `IpcRemoteException`;超时 → `IpcTimeoutException`;断连 → `IpcConnectionClosedException`;版本不匹配 → `IpcVersionMismatchException`(拒绝该帧、连接保持可用)。

**丢弃的帧必须可见。** 传输层(`IIpcTransport.ProtocolError`)与 `IIpcPeer.ProtocolError` 对每个被丢弃的入站帧各上报一次 `IpcProtocolException`:零长度帧、超帧上限、帧头解码失败;连接保持可用,后续合法帧照常派发。调用方据此区分"对端发来坏帧"与"超时"(`IpcTimeoutException`),不再只看到超时。

**放弃的连接必须真的停下。** `ConnectTimeout` 到期时用链接令牌取消底层连接并处置流,不再只放弃等待:`NamedPipeClientStream.ConnectAsync` 被放任继续重试时,它会在服务端开始监听后抢到唯一的管道实例、随即断开,服务端把"连上却不发请求"当作对端离开而正常退出(exit 0、无输出),此后任何重连都连不上。因此超时的连接尝试必须取消并观察其结束,再处置。

**帧上限与恢复。** 单帧上限 16 MiB;零长度帧与超限帧读侧拒绝并跳帧重同步,超限超过重同步上限(64 MiB)才关闭连接;写侧拒绝不发送。

## 未决

- 批量调用(batch)尚无契约面;`PluginId` 不在通用帧头(属上层消息语义)。
