# Store 版 pwsh 做终端 shell 时子进程逃出 Job(2026-11-06)

## 现象

rebase 到 main 后,4 个终端集成测试失败:TerminalStopTests 的两个用例、TerminalShutdownTests、TerminalForegroundTests。表现是 stop 或 host.stop 之后孙进程还在,有前台子进程时 busy 仍为 false。失败与 rebase 无关:本机 PATH 上第一个 `pwsh.exe` 变成了 Microsoft Store 版 PowerShell 7.6.9 的应用执行别名,位于 `%LOCALAPPDATA%\Microsoft\WindowsApps`。把 `C:\Program Files\PowerShell\7` 放到 PATH 最前面后,16 个终端用例全部通过。

## 机理(探针实验)

shell 本身能被分配进私有 Job(`IsProcessInJob=True`),但打包(MSIX)进程启动的子进程不在这个 Job 里。换成真实的 `C:\Program Files\PowerShell\7\pwsh.exe` 后,shell 和子进程都在 Job 内。

## 处理

- Host 默认 shell 的查找顺序:PATH 上第一个**非别名**的 `pwsh.exe` → `%ProgramFiles%\PowerShell\7\pwsh.exe` → Windows PowerShell → `%COMSPEC%`。只按路径判断是否为别名,不新增 P/Invoke。
- 用户显式指定的别名照原样使用,不做静默替换。这一条作为已知限制写进 forge-extension-host SPEC:这类 shell 的子进程不受 stop、Host 关闭时整树回收以及 busy 判定的约束。
- 测试辅助 `TerminalShells.Rich()` 采用与产品默认相同的跳过规则。
