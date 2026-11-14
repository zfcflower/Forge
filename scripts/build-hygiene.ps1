<#
构建期进程卫生(由 Directory.Build.props 的 ForgeBuildHygiene target 调用,仅本地 Windows、CI 跳过)。
规则:孤儿 testhost/Forge.ExtensionHost/PluginWorker/ScriptExecutor(含 dotnet.exe 承载的 .dll 形态)自动结束并记 artifacts/hygiene/ 日志;
活锁只报 PID 与处置指引,不 fail fast(先观察一周);只管命令行引用本仓库路径的进程,
绝不误杀其它副本(如 D:/FORGE/integ)正在跑的测试或 VS Code 的扩展服务进程。
绝不因自身失败打断构建:任何异常都吞掉,始终退出 0。
#>
[CmdletBinding()]
param([string]$RepoRoot)

try {
    $ErrorActionPreference = 'SilentlyContinue'
    if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
    $repo = ([string]$RepoRoot).TrimEnd('\').ToLowerInvariant().Replace('/', '\')
    $logDir = Join-Path $RepoRoot 'artifacts\hygiene'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $logPath = Join-Path $logDir ("hygiene-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".txt")
    $log = New-Object System.Collections.Generic.List[string]

    function Get-Hits {
        $procs = @(Get-CimInstance Win32_Process)
        $live = @{}
        foreach ($p in $procs) { $live[[int]$p.ProcessId] = $true }
        $hits = New-Object System.Collections.Generic.List[object]
        foreach ($p in $procs) {
            $cl = ''
            if ($p.CommandLine) { $cl = $p.CommandLine.ToLowerInvariant().Replace('/', '\') }
            $nm = ([string]$p.Name).ToLowerInvariant()
            # 载荷分类:Host/Worker/Executor 常以 dotnet.exe 承载 .dll 运行(HostProcess.cs/ExtensionHostSupervisor.cs),
            # 按命令行载荷判类别而非进程名,否则孤儿 Host 永不会被自动结束;载荷判据仅对 dotnet.exe 生效,
            # 避免误把恰在命令行里提到该字符串的无关进程当命中。Forge.App(含 dotnet 承载)是用户程序,永不自动结束。
            $payload = ''
            switch ($nm) {
                'testhost.exe'             { $payload = 'testhost' }
                'forge.app.exe'            { $payload = 'app' }
                'forge.extensionhost.exe'  { $payload = 'extensionhost' }
                'forge.pluginworker.exe'   { $payload = 'pluginworker' }
                'forge.scriptexecutor.exe' { $payload = 'scriptexecutor' }
                default {
                    if ($nm -eq 'dotnet.exe') {
                        if ($cl -match 'forge\.extensionhost\.dll') { $payload = 'extensionhost' }
                        elseif ($cl -match 'forge\.pluginworker\.dll') { $payload = 'pluginworker' }
                        elseif ($cl -match 'forge\.scriptexecutor\.dll') { $payload = 'scriptexecutor' }
                        elseif ($cl -match 'forge\.app\.dll') { $payload = 'app' }
                    }
                }
            }
            if (-not $payload) { continue }
            # 必须正向命中本仓库路径;CommandLine 读不到(提权/他用户进程)宁可放过,绝不误杀其它副本正在跑的测试
            if (-not $cl -or -not $cl.Contains($repo)) { continue }
            $hits.Add([pscustomobject]@{
                Pid      = [int]$p.ProcessId
                Parent   = [int]$p.ParentProcessId
                Name     = [string]$p.Name
                Cl       = $cl
                Orphan   = (-not $live[[int]$p.ParentProcessId])
                AutoKill = ($payload -ne 'app')
            })
        }
        return , $hits
    }

    $killed = New-Object System.Collections.Generic.List[string]
    for ($pass = 0; $pass -lt 3; $pass++) {
        $hits = Get-Hits
        $orphans = @($hits | Where-Object { $_.Orphan -and $_.AutoKill })
        if ($orphans.Count -eq 0) { break }
        foreach ($o in $orphans) {
            taskkill /PID $o.Pid /T /F | Out-Null
            $killed.Add("孤儿 $($o.Name) PID $($o.Pid)(父 $($o.Parent) 已死,连同子进程结束)")
        }
    }

    $hits = Get-Hits
    $runRoot = [regex]::Escape((Join-Path $repo 'artifacts\run'))
    foreach ($h in ($hits | Where-Object { $_.Orphan -and -not $_.AutoKill })) {
        $log.Add("孤儿 $($h.Name) PID $($h.Pid)(父 $($h.Parent) 已死)是用户程序,不自动结束:确认无人使用后手动清 taskkill /PID $($h.Pid) /T /F,或关掉它再构建。")
    }
    foreach ($h in ($hits | Where-Object { -not $_.Orphan })) {
        if ($h.Cl -match $runRoot) {
            $log.Add("INFO PID $($h.Pid) $($h.Name) 运行于 artifacts/run(不锁 src bin),可忽略。")
        }
        else {
            $log.Add("活动 PID $($h.Pid) ($($h.Name),父 $($h.Parent)) 占着本仓库路径:若是正在跑的测试,等它结束;若是残留,手动清: taskkill /PID $($h.Pid) /T /F;若是人在用的 Forge.App,请关闭或改用 FORGE_RUN=1(artifacts/run)启动。")
        }
    }

    foreach ($l in $log) { Write-Host "[hygiene] $l" }
    foreach ($k in $killed) { Write-Host "[hygiene] 已结束 $k" -ForegroundColor Yellow }
    if ($log.Count -eq 0 -and $killed.Count -eq 0) { Write-Host "[hygiene] OK:无孤儿进程、无本仓活动锁。" }
    $all = @($killed) + @($log)
    if ($all.Count -gt 0) { $all | Set-Content -Encoding UTF8 $logPath }
}
catch { }
exit 0
