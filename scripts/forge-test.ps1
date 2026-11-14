<#
Forge 测试执行入口 —— 测试范围治理的唯一执行面。
规则来源:testing.md(id forge-testing)与 AGENTS.md「测试分层与执行」。
主仓(有 .git):scripts/forge-test.ps1 -Auto   按 git 改动推导落点过滤器(只跑与改动
  严格相关的用例),改了 .cs/.axaml 自动追加源码扫描门槛,失败先重建一次再判红
  (治旧 bin 假红),收据写 artifacts/testruns/ 并打印,须粘贴进完工报告。
副本(无 .git):按派活文本给的过滤器显式执行,例如
  scripts/forge-test.ps1 -Level L1 -Project Forge.App.Tests -Ns Workbench [-Gates]
L3 全量必须显式 --why merge|release|interface|infra|cross-module,缺省直接拒绝。
#>
[CmdletBinding()]
param(
    [switch]$Auto,
    [ValidateSet('L1', 'L2', 'L3')][string]$Level,
    [string]$Project,
    [string]$Ns,
    [switch]$Gates,
    [ValidateSet('merge', 'release', 'interface', 'infra', 'cross-module')][string]$Why,
    [switch]$SkipBuild,
    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }  # dotnet 输出是 UTF-8;zh-CN 控制台默认 GBK,不设会乱码且解析不到摘要
$GatesFilter = 'FullyQualifiedName~Localization|FullyQualifiedName~MinorRegressionTests|FullyQualifiedName~KeyRegistry|FullyQualifiedName~StringTable|FullyQualifiedName~NoHardcodedChinese'

function Write-Fail([string]$Message) {
    Write-Host "[forge-test] $Message" -ForegroundColor Red
    exit 2
}

if ($env:FORGE_RUN -eq '1') {
    Write-Fail "本 shell 设了 FORGE_RUN=1(测试 shell 不得设,否则测试项目会引用 artifacts/run 的 Forge.App)。Remove-Item Env:FORGE_RUN 后重跑。"
}

if ($Auto -and $Level) { Write-Fail "-Auto 与 -Level 二选一:-Auto 自己推导落点。" }
if (-not $Auto -and -not $Level) { $Auto = $true }

# ---- 落点映射(scripts/test-map.json,最长前缀匹配;改表须与 testing.md 落点表同步) ----
# Prefix=小写匹配键、Rule=原始大小写(收据/推导行展示用,保证可 grep 匹配真实路径);l1 的 ns 支持字符串或数组
$Map = Get-Content -Raw (Join-Path $RepoRoot 'scripts/test-map.json') | ConvertFrom-Json
$L3Rules = @($Map.l3 | ForEach-Object { @{ Prefix = ([string]$_.prefix).ToLowerInvariant(); Rule = [string]$_.prefix; Why = [string]$_.why } })
$L1Rules = @($Map.l1 | ForEach-Object { @{ Prefix = ([string]$_.prefix).ToLowerInvariant(); Rule = [string]$_.prefix; Ns = @($_.ns); GatesOnly = [bool]$_.gatesOnly } } | Sort-Object { $_.Prefix.Length } -Descending)
$L2Rules = @($Map.l2 | ForEach-Object { @{ Prefix = ([string]$_.prefix).ToLowerInvariant(); Rule = [string]$_.prefix; Project = [string]$_.project } })

# 映射表自检:前缀失配(写错/模块改名)会让规则永不命中、静默降级假绿——l3 失配直接拒绝执行,l1/l2 只警告
function Test-MapPrefix([string]$Prefix) {
    $p = $Prefix.TrimEnd('/')
    $full = Join-Path $RepoRoot ($p -replace '/', '\')
    if ($Prefix.EndsWith('/')) { return (Test-Path -LiteralPath $full) }
    if (Test-Path -LiteralPath $full) { return $true }
    return (Test-Path -LiteralPath (Split-Path $full -Parent))
}
foreach ($r in $L3Rules) {
    if (-not (Test-MapPrefix $r.Rule)) { Write-Fail "scripts/test-map.json 的 l3 前缀 '$($r.Rule)' 在仓库中不存在(写错或模块已改名);为防静默失配假绿,拒绝执行——先修表再跑。" }
}
$badMap = @((@($L1Rules) + @($L2Rules)) | Where-Object { -not (Test-MapPrefix $_.Rule) })
if ($badMap.Count -gt 0) {
    Write-Host "[forge-test] 警告:落点表前缀失配(仍继续,但这些规则永不命中):$(($badMap | ForEach-Object { $_.Rule }) -join ', ')" -ForegroundColor Yellow
}

function Get-ChangedFiles {
    if (-not (Test-Path (Join-Path $RepoRoot '.git'))) { return $null }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $source = 'worktree'
    try {
        $tracked = @(git -C $RepoRoot diff --name-only HEAD 2>$null)
        $untracked = @(git -C $RepoRoot ls-files --others --exclude-standard 2>$null)
        $all = @($tracked) + @($untracked)
        $all = @($all | Where-Object { $_ })
        if ($all.Count -eq 0) {
            # 工作区干净:改用最近一次提交的范围(已提交的工作也该有落点可测)。
            # 单提交仓 HEAD~1 不存在,git stderr 在 EAP=Continue + 2>$null 下被吞掉,all 为空走下方 Write-Fail。
            $all = @(git -C $RepoRoot diff --name-only HEAD~1 HEAD 2>$null)
            $source = 'HEAD~1'
            if ($all.Count -gt 0) { Write-Host "[forge-test] 工作区无未提交改动,改用最近一次提交的范围。" -ForegroundColor Yellow }
        }
    }
    finally { $ErrorActionPreference = $prev }
    return , @{ Files = $all; Source = $source }
}

function Get-Classification([string]$File) {
    $f = ($File -replace '\\', '/').ToLowerInvariant()   # 匹配用小写副本
    $fOrig = ($File -replace '\\', '/')                    # 取值保留原大小写(项目名/命名空间)
    # 文档/spec 一律无测试落点(testing.md「文档、spec、.md -> 不跑测试"),先于任何目录前缀判,否则 src/*/SPEC.md 会被当产品改动触发全量
    if ($f -match '\.(md|txt|svg|png|jpe?g|gif|ico|webp|license)$') { return @{ Kind = 'doc'; Rule = '' } }
    foreach ($r in $L3Rules) { if ($f.StartsWith($r.Prefix)) { return @{ Kind = 'L3'; Why = $r.Why; Rule = $r.Rule } } }
    foreach ($r in $L1Rules) { if ($f.StartsWith($r.Prefix)) { return @{ Kind = 'L1'; Ns = @($r.Ns); GatesOnly = $r.GatesOnly; Rule = $r.Rule } } }
    foreach ($r in $L2Rules) { if ($f.StartsWith($r.Prefix)) { return @{ Kind = 'L2'; Project = $r.Project; Rule = $r.Rule } } }
    if ($f.StartsWith('tests/')) {
        $parts = $fOrig.Split('/')
        if ($parts.Count -ge 4) { return @{ Kind = 'L1'; Ns = ($parts[1] + '.' + $parts[2]); Rule = 'tests/<项目>/<命名空间>/' } }
        if ($parts.Count -ge 3) { return @{ Kind = 'L2'; Project = $parts[1]; Rule = 'tests/<项目>/' } }
    }
    return @{ Kind = 'none'; Rule = '' }
}

# ---- 1. 定界 ----
$Runs = New-Object System.Collections.Generic.List[object]
$Derivation = New-Object System.Collections.Generic.List[string]
$PlanLevel = $Level
$PlanWhy = $Why
$GatesNeeded = $false
$ChangedCount = $null
$Files = $null

if ($Auto) {
    $chg = Get-ChangedFiles
    if ($null -eq $chg) {
        Write-Fail "未发现 .git(这是副本):不要用 -Auto。按派活文本给的过滤器显式执行,例如 scripts/forge-test.ps1 -Level L1 -Project Forge.App.Tests -Ns Workbench"
    }
    if ($chg.Source -eq 'HEAD~1') {
        $Derivation.Add("工作区无未提交改动,范围=最近一次提交(HEAD~1..HEAD);更早的提交不在本收据内")
    }
    $Files = @($chg.Files)
    if ($Files.Count -eq 0) { Write-Fail "git 无任何改动(也未提交过),没有可测落点;若改动未保存,先保存再跑。" }
    $ChangedCount = $Files.Count
    $nsByProject = @{}
    $fullProjects = New-Object System.Collections.Generic.List[string]
    $groups = @{}
    $unmappedCode = 0

    foreach ($file in $Files) {
        $c = Get-Classification $file
        $f = ($file -replace '\\', '/').ToLowerInvariant()
        # 门菶触发:.cs/.axaml(AGENTS.md)与 .resx(testing.md「Resources 文案」行)都是会被源码扫描门槛覆盖的源文件
        if ($f -match '\.(cs|axaml|resx)$' -and ($f.StartsWith('src/') -or $f.StartsWith('tests/'))) { $GatesNeeded = $true }

        $label = switch ($c.Kind) {
            'L3'   { "L3(落点 $($c.Rule) why=$($c.Why))" }
            'L1'   { if ($c.GatesOnly) { "L1 源码扫描门槛(落点 $($c.Rule))" } else { "L1 $(($c.Ns -join '|'))(落点 $($c.Rule))" } }
            'L2'   { "L2 $($c.Project)(落点 $($c.Rule))" }
            'doc'  { "无测试落点(文档/spec):testing.md 规定不跑测试" }
            default { "未命中落点表(工具/未映射路径,无专属测试落点)" }
        }
        $key = "$($c.Kind)|$($c.Rule)|$($label)"
        if (-not $groups.ContainsKey($key)) { $groups[$key] = 0 }
        $groups[$key]++

        switch ($c.Kind) {
            'L3' {
                $PlanLevel = 'L3'
                if (-not $PlanWhy) { $PlanWhy = $c.Why }
            }
            'L1' {
                if ($c.GatesOnly) { $GatesNeeded = $true }
                else {
                    foreach ($ns in $c.Ns) {
                        $proj = $ns.Substring(0, $ns.LastIndexOf('.'))
                        if (-not $nsByProject.ContainsKey($proj)) { $nsByProject[$proj] = New-Object System.Collections.Generic.List[string] }
                        $nsByProject[$proj].Add($ns)
                    }
                }
            }
            'L2' { if ($c.Project -and -not $fullProjects.Contains($c.Project)) { $fullProjects.Add($c.Project) } }
            default { if ($c.Kind -eq 'none' -and ($f.StartsWith('src/') -or $f.StartsWith('tests/'))) { $unmappedCode++ } }
        }
    }
    foreach ($k in $groups.Keys) {
        $label = ($k -split '\|', 3)[2]
        $Derivation.Add("$label x $($groups[$k])")
    }
    if ($unmappedCode -gt 0 -and $PlanLevel -ne 'L3') {
        $Derivation.Add("警告: $unmappedCode 个 src/tests 改动未命中落点表(如 ExtensionHost/PluginWorker 等未映射模块),它们只被构建与源码扫描门槛覆盖;需要跨进程保护时显式 -Level L3 --why")
    }

    if ($PlanLevel -eq 'L3') {
        $Derivation.Add("命中 L3 落点 -> 13 个测试项目全量(why 自动=$PlanWhy)")
    }
    else {
        foreach ($proj in $fullProjects) { $Runs.Add(@{ Project = $proj; Filter = ''; Reason = 'L2 落点(项目全量)' }) }
        foreach ($proj in $nsByProject.Keys) {
            if ($fullProjects -contains $proj) { continue }
            $segs = @($nsByProject[$proj] | Sort-Object -Unique | ForEach-Object { "FullyQualifiedName~$_" })
            $Runs.Add(@{ Project = $proj; Filter = ($segs -join '|'); Reason = 'L1 落点' })
        }
    }
}
else {
    if ($Level -eq 'L3' -and -not $Why) {
        Write-Fail @"
拒绝:L3(全量)必须带 --why。
L3 触发条件(testing.md):合并/发布;Abstractions/Plugin.Sdk/Ipc/存储 schema/AOT 等公共协议与核心基础设施变化;L1/L2 疑似跨模块回归;用户明确要求。
全量会把与本次改动无关的偶发失败混进来,判不出真伪——不是保险,是稀释信号。
成本参考:Forge.App.Tests 全量 16m25s(串行),13 个项目全量约 30 分钟起。
确属触发:scripts/forge-test.ps1 -Level L3 --why merge|release|interface|infra|cross-module
"@
    }
    if ($Level -eq 'L1') {
        if (-not $Project -or -not $Ns) { Write-Fail "L1 需要 -Project <测试项目> 与 -Ns <命名空间段>(副本无 .git,不能用 -Auto)。示例:scripts/forge-test.ps1 -Level L1 -Project Forge.App.Tests -Ns Workbench" }
        if (-not (Test-Path (Join-Path $RepoRoot "tests/$Project"))) { Write-Fail "tests/$Project 不存在;项目名取 tests/ 下的目录,例如 Forge.App.Tests。" }
        $segs = @()
        foreach ($n in ($Ns -split ',')) {
            $s = $n.Trim()
            if (-not $s) { continue }
            if ($s.StartsWith($Project + '.')) { $segs += "FullyQualifiedName~$s" } else { $segs += "FullyQualifiedName~$Project.$s" }
        }
        if ($segs.Count -eq 0) { Write-Fail "-Ns 解析后为空。" }
        if ($Gates) { $segs += ($GatesFilter -split '\|') }
        $Runs.Add(@{ Project = $Project; Filter = ($segs -join '|'); Reason = '显式 L1' })
    }
    if ($Level -eq 'L2') {
        if (-not $Project) { Write-Fail "L2 需要 -Project <测试项目>,例如 -Project Forge.Core.Tests。" }
        if (-not (Test-Path (Join-Path $RepoRoot "tests/$Project"))) { Write-Fail "tests/$Project 不存在;项目名取 tests/ 下的目录,例如 Forge.Core.Tests。" }
        $Runs.Add(@{ Project = $Project; Filter = ''; Reason = '显式 L2(项目全量)' })
        if ($Gates -and $Project -ne 'Forge.App.Tests') { $Runs.Add(@{ Project = 'Forge.App.Tests'; Filter = $GatesFilter; Reason = '显式源码扫描门槛' }) }
    }
}

# 源码扫描门槛合并:App.Tests 已有带过滤器的运行就并入,否则单开一条
if ($Auto -and $PlanLevel -ne 'L3' -and $GatesNeeded) {
    $appRun = @($Runs | Where-Object { $_.Project -eq 'Forge.App.Tests' -and $_.Filter })
    if ($appRun.Count -gt 0) {
        $appRun[0].Filter = $appRun[0].Filter + '|' + $GatesFilter
        $appRun[0].Reason = $appRun[0].Reason + '+源码扫描门槛'
    }
    elseif (-not ($Runs | Where-Object { $_.Project -eq 'Forge.App.Tests' })) {
        $Runs.Add(@{ Project = 'Forge.App.Tests'; Filter = $GatesFilter; Reason = '源码扫描门槛(.cs/.axaml 改动)' })
    }
}

# 收据的 level 从实际 Runs 反推(命中 L3 时已置;无落点为 none;带过滤器的运行= L1,项目全量= L2,两者都有= L1+L2)
if (-not $PlanLevel) {
    $hasFull = [bool]($Runs | Where-Object { -not $_.Filter })
    $hasNs = [bool]($Runs | Where-Object { $_.Filter })
    if ($hasFull -and $hasNs) { $PlanLevel = 'L1+L2' }
    elseif ($hasFull) { $PlanLevel = 'L2' }
    elseif ($hasNs) { $PlanLevel = 'L1' }
}

# L3:13 个测试项目全量
if ($PlanLevel -eq 'L3') {
    $Runs.Clear()
    foreach ($d in (Get-ChildItem (Join-Path $RepoRoot 'tests') -Directory | Sort-Object Name)) {
        if (Get-ChildItem $d.FullName -Filter '*.csproj' -File -ErrorAction SilentlyContinue) {
            $Runs.Add(@{ Project = $d.Name; Filter = ''; Reason = 'L3 全量' })
        }
    }
}

if ($ListOnly) {
    Write-Host "[forge-test] 计划(level=$(if ($PlanLevel) { $PlanLevel } else { 'none' })$(if ($PlanWhy) { " why=$PlanWhy" } else { '' })):"
    if ($Derivation.Count -gt 0) { foreach ($d in $Derivation) { Write-Host "  推导: $d" } }
    if ($Runs.Count -eq 0) { Write-Host "  无测试落点(文档/spec/工具等非测试落点改动)-> 不跑测试。" }
    foreach ($r in $Runs) {
        $cmd = "dotnet test tests/$($r.Project) --no-build"
        if ($r.Filter) { $cmd += " --filter `"$($r.Filter)`"" }
        if ($PlanLevel -eq 'L3') { $cmd += " --logger `"trx;LogFileName=$($r.Project).trx`" --results-directory artifacts/testresults/$($r.Project) --blame-hang --blame-hang-timeout 10min --blame-crash" }
        Write-Host "  [计划] $cmd"
    }
    exit 0
}

# ---- 2. 执行 ----
function Invoke-Dotnet([string[]]$ArgList) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = (& dotnet $ArgList 2>&1 | Out-String -Width 4096)
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }
    Write-Host $text
    return @{ Exit = $code; Text = $text }
}

function Get-Counts([string]$Text) {
    # 摘要可能是英文(VSTest 英文资源)或 zh-CN 本地化("失败: 0，通过: 110，已跳过: 0",全角逗号);取最后一条匹配(最终摘要)
    $patterns = @(
        'Failed:\s*(\d+)\s*[,，]\s*Passed:\s*(\d+)\s*[,，]\s*Skipped:\s*(\d+)',
        '失败:\s*(\d+)\s*[,，]\s*通过:\s*(\d+)\s*[,，]\s*已跳过:\s*(\d+)'
    )
    foreach ($pat in $patterns) {
        $ms = [regex]::Matches($Text, $pat)
        if ($ms.Count -gt 0) {
            $m = $ms[$ms.Count - 1]
            return @{ Failed = [int]$m.Groups[1].Value; Passed = [int]$m.Groups[2].Value; Skipped = [int]$m.Groups[3].Value }
        }
    }
    return $null
}

function Invoke-TestOnce([string[]]$ArgList) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $res = Invoke-Dotnet $ArgList
    $sw.Stop()
    return @{ Res = $res; Counts = (Get-Counts $res.Text); Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) }
}

$totalSw = [Diagnostics.Stopwatch]::StartNew()
$buildSeconds = $null
Push-Location $RepoRoot
try {
    if ($Runs.Count -eq 0) {
        Write-Host "[forge-test] 改动 $ChangedCount 个文件,无测试落点(文档/spec/工具等非测试落点)-> 不跑测试、不构建。" -ForegroundColor Cyan
    }
    elseif (-not $SkipBuild) {
        Write-Host "[forge-test] dotnet build Forge.slnx -c Debug ..."
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $b = Invoke-Dotnet @('build', 'Forge.slnx', '-c', 'Debug')
        $sw.Stop()
        $buildSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        if ($b.Exit -ne 0) {
            # 构建失败也必须落收据(build.failed=true):「无收据的全量视为未完成测试义务」,评审须能区分“构建失败”与“没跑”
            $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
            $receiptDir = Join-Path $RepoRoot 'artifacts/testruns'
            New-Item -ItemType Directory -Force -Path $receiptDir | Out-Null
            $lvlVal = $PlanLevel
            if (-not $lvlVal) { $lvlVal = 'none' }
            [ordered]@{
                timestamp        = $ts
                level            = $lvlVal
                why              = $PlanWhy
                auto             = [bool]$Auto
                changedFiles     = $ChangedCount
                derivation       = $Derivation.ToArray()
                build            = @{ failed = $true; skipped = $false; seconds = $buildSeconds }
                runs             = @()
                totals           = @{ passed = 0; failed = 0; skipped = 0; countsKnown = $true }
                wallClockSeconds = [math]::Round($totalSw.Elapsed.TotalSeconds, 1)
            } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $receiptDir "forge-test-$ts.json")
            Write-Host "收据文件: artifacts/testruns/forge-test-$ts.json(build.failed=true)"
            Write-Fail "构建失败(退出码 $($b.Exit)),测试未执行;失败收据已落盘。"
        }
    }

    $receiptRuns = New-Object System.Collections.Generic.List[object]
    $anyFailed = $false
    $i = 0
    foreach ($r in $Runs) {
        $i++
        $argList = @('test', "tests/$($r.Project)", '--no-build')
        if ($r.Filter) { $argList += @('--filter', $r.Filter) }
        if ($PlanLevel -eq 'L3') {
            $argList += @('--logger', "trx;LogFileName=$($r.Project).trx")
            $argList += @('--results-directory', "artifacts/testresults/$($r.Project)")
            $argList += @('--blame-hang', '--blame-hang-timeout', '10min', '--blame-crash')
        }
        Write-Host "`n[forge-test] ($i/$($Runs.Count)) tests/$($r.Project)$(if ($r.Filter) { '  filter: ' + $r.Filter })" -ForegroundColor Cyan
        $t = Invoke-TestOnce $argList
        $counts = $t.Counts
        $retried = $false

        $isFail = ($t.Res.Exit -ne 0) -or (($counts -and $counts.Failed -gt 0))
        if (-not $isFail -and $counts -and (($counts.Passed + $counts.Failed + $counts.Skipped) -eq 0)) { $isFail = $true }

        if ($isFail -and ($counts -eq $null -or $counts.Failed -gt 0 -or $t.Res.Exit -ne 0)) {
            Write-Host "[forge-test] 首跑失败 -> 先 dotnet build tests/$($r.Project) 再重跑一次(治旧 bin 假红)..." -ForegroundColor Yellow
            $rb = Invoke-Dotnet @('build', "tests/$($r.Project)", '-c', 'Debug')
            if ($rb.Exit -eq 0) {
                $t = Invoke-TestOnce $argList
                $counts = $t.Counts
                $retried = $true
            }
            else { Write-Host "[forge-test] 重建也失败,按首跑结果判红。" -ForegroundColor Yellow }
        }

        $finalFail = ($t.Res.Exit -ne 0) -or (($counts -and $counts.Failed -gt 0)) -or ($counts -and (($counts.Passed + $counts.Failed + $counts.Skipped) -eq 0))
        if ($finalFail) { $anyFailed = $true }
        if (-not $counts) { Write-Host "[forge-test] 警告:未能解析用例计数(输出格式变化?),以退出码 $($t.Res.Exit) 为准。" -ForegroundColor Yellow }

        $p = $null; $f = $null; $s = $null
        if ($counts) { $p = $counts.Passed; $f = $counts.Failed; $s = $counts.Skipped }
        $receiptRuns.Add([ordered]@{
            project  = $r.Project
            filter   = $r.Filter
            reason   = $r.Reason
            command  = ($argList -join ' ')
            exit     = $t.Res.Exit
            passed   = $p
            failed   = $f
            skipped  = $s
            retried  = $retried
            seconds  = $t.Seconds
        })
    }

    # ---- 3. 收据 ----
    $ts = Get-Date -Format 'yyyyMMdd-HHmmss'
    $receiptDir = Join-Path $RepoRoot 'artifacts/testruns'
    New-Item -ItemType Directory -Force -Path $receiptDir | Out-Null
    $receiptPath = Join-Path $receiptDir "forge-test-$ts.json"
    $sumPassed = 0; $sumFailed = 0; $sumSkipped = 0; $known = $true
    foreach ($rr in $receiptRuns) {
        if ($null -ne $rr.passed) { $sumPassed += $rr.passed; $sumFailed += $rr.failed; $sumSkipped += $rr.skipped } else { $known = $false }
    }
    $levelVal = $PlanLevel
    if (-not $levelVal) { $levelVal = 'none' }
    $receipt = [ordered]@{
        timestamp        = $ts
        level            = $levelVal
        why              = $PlanWhy
        auto             = [bool]$Auto
        changedFiles     = $ChangedCount
        derivation       = $Derivation.ToArray()
        build            = @{ failed = $false; skipped = [bool]$SkipBuild; seconds = $buildSeconds }
        runs             = $receiptRuns.ToArray()
        totals           = @{ passed = $sumPassed; failed = $sumFailed; skipped = $sumSkipped; countsKnown = $known }
        wallClockSeconds = [math]::Round($totalSw.Elapsed.TotalSeconds, 1)
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $receiptPath

    Write-Host "`n== Forge 测试收据(粘贴进完工报告)==" -ForegroundColor Green
    Write-Host "level: $(if ($PlanLevel) { $PlanLevel } else { 'none' })$(if ($PlanLevel -eq 'L3') { "(why=$PlanWhy)" } else { '' })$(if ($Auto) { ' auto' } else { '' })  改动文件: $ChangedCount"
    if ($Derivation.Count -gt 0) { foreach ($d in $Derivation) { Write-Host "推导: $d" } }
    if ($buildSeconds) { Write-Host "构建: $buildSeconds s$(if ($SkipBuild) { '(跳过)' } else { '' })" }
    foreach ($rr in $receiptRuns) {
        $countTxt = $(if ($null -ne $rr.passed) { "passed $($rr.passed) / failed $($rr.failed) / skipped $($rr.skipped)" } else { '计数未解析' })
        Write-Host "运行: tests/$($rr.project)$(if ($rr.filter) { "  filter: $($rr.filter)" })"
        Write-Host "      $countTxt$(if ($rr.retried) { '(重建后重跑)' } else { '' }) - $($rr.seconds) s  exit=$($rr.exit)"
    }
    if ($receiptRuns.Count -eq 0) { Write-Host "运行: (无,无测试落点)" }
    Write-Host "收据文件: artifacts/testruns/forge-test-$ts.json"
    if ($anyFailed) { Write-Host "结果: FAILED" -ForegroundColor Red; exit 1 }
    Write-Host "结果: OK" -ForegroundColor Green
    exit 0
}
finally { Pop-Location }
